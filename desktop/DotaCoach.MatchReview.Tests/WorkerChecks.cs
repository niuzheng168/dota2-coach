using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DotaCoach.TwoPc;

internal static class WorkerChecks
{
    private sealed class ProgressSink(Action<ReviewProgress> report) : IProgress<ReviewProgress> { public void Report(ReviewProgress p) => report(p); }
    private static async Task<bool> Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; } catch (T) { return true; }
    }

    // The test process is also a tiny substitute for the CLI. Never starts a network client or reads a real DEM.
    internal static int Child(string[] args)
    {
        static string Arg(string[] a, string k) => a[Array.IndexOf(a, k) + 1];
        if (!args.Contains("--dem") || args.Contains("--download")) return 2;
        string behavior = File.ReadAllText(Arg(args, "--dem")), output = Arg(args, "--output"), id = Arg(args, "--match-id");
        Console.Error.WriteLine("""{"type":"progress","stage":"parse","tick":1,"totalTicks":2}""");
        if (behavior == "ignore-cancel") Thread.Sleep(30000); // force launcher's tree-kill fallback
        if (behavior == "refuse") {
            Console.Error.WriteLine("""{"type":"error","stage":"verify","message":"Fixture rejected internal match ID"}"""); return 4;
        }
        File.WriteAllText(output, ReviewChecks.ReplayJson(id));
        // A zero exit with a stale/mismatched success announcement must not be imported.
        Console.Error.WriteLine(JsonSerializer.Serialize(new { type = "success", stage = "published",
            matchId = behavior == "wrong-id" ? "9" : id, bytes = new FileInfo(output).Length }));
        return 0;
    }

    internal static async Task Run(string root, Action<bool, string> check)
    {
        string tools = Directory.CreateDirectory(Path.Combine(root, "pin fixture")).FullName;
        var manifest = new Dictionary<string, string>();
        foreach (var key in new[] { "PostgameReplay.exe", "runtime/python.exe", "node/node.exe", "scripts/replays/postgame/cli.py" }) {
            string dst = Path.Combine(tools, key); Directory.CreateDirectory(Path.GetDirectoryName(dst)!); File.WriteAllText(dst, key);
            manifest[key] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dst)));
        }
        string file = Path.Combine(tools, "sha256.json");
        File.WriteAllText(file, JsonSerializer.Serialize(manifest));
        string pin = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        var verified = await ReplayToolPin.VerifyAsync(tools, pin, default);
        check(verified.Executable == Path.Combine(tools, "PostgameReplay.exe"), "pinned bundle resolves an absolute executable rather than PATH or a shell");
        string extra = Path.Combine(tools, "runtime", "sitecustomize.py"); File.WriteAllText(extra, "");
        check(await Reject<InvalidDataException>(() => ReplayToolPin.VerifyAsync(tools, pin, default)), "an extra importable file in the tool tree fails the pin"); File.Delete(extra);
        File.AppendAllText(verified.Executable, "modified");
        check(await Reject<InvalidDataException>(() => ReplayToolPin.VerifyAsync(tools, pin, default)), "modified executable fails even with an unchanged manifest");
        File.AppendAllText(file, " ");
        check(await Reject<InvalidDataException>(() => ReplayToolPin.VerifyAsync(tools, pin, default)), "a changed manifest cannot supply its own trusted digest");

        const string id = "900000001";
        string temp = Directory.CreateDirectory(Path.Combine(root, "jobs")).FullName, input = Path.Combine(root, "local input.dem.bz2");
        File.WriteAllText(input, "success"); string executable = Path.Combine(AppContext.BaseDirectory, "DotaCoach.MatchReview.Tests.exe");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new ProgressSink(p => { if (p.Percent == 50) reached.TrySetResult(); });
        int imports = 0, resolutions = 0;
        var store = new ReviewStore(() => throw new Exception(), _ => true, (_, _) => throw new Exception(), (actual, output) => {
            using var data = JsonDocument.Parse(File.ReadAllText(output));
            if (actual != id || data.RootElement.GetProperty("matchId").GetString() != id) throw new Exception("bad publication");
            Interlocked.Increment(ref imports);
        });
        Task Run(ReviewStore s, CancellationToken ct = default) => Task.Run(() =>
            SealedReplayJob.ExecuteAsync(s, id, input, progress, ct, _ => {
                Interlocked.Increment(ref resolutions); return Task.FromResult(new ReplayTools(executable));
            }, temp, TimeSpan.FromSeconds(20)));
        bool Clean() => !Directory.EnumerateFileSystemEntries(temp).Any() && File.Exists(input);

        check(await Reject<InvalidOperationException>(() => Run(store with { IsSealed = _ => false })) && resolutions == 0 && Clean(),
            "an unsealed match is denied before tool resolution, disk staging, download, or local import");
        await Run(store); check(imports == 1 && Clean() && reached.Task.IsCompleted,
            "successful local action passes spaced paths as arguments, drains structured progress, imports once, and cleans only its scratch directory");
        int gates = 0;
        check(await Reject<InvalidOperationException>(() => Run(store with { IsSealed = _ => ++gates < 3 })) && imports == 1 && Clean(),
            "seal is rechecked between parse completion and the archive's atomic import");
        File.WriteAllText(input, "wrong-id");
        check(await Reject<InvalidDataException>(() => Run(store)) && imports == 1 && Clean(), "wrong success ID cannot publish even on exit zero");
        File.WriteAllText(input, "refuse");
        check(await Reject<InvalidDataException>(() => Run(store)) && imports == 1 && Clean(), "worker verification failure leaves no partial output or new import");
        File.WriteAllText(input, "ignore-cancel"); reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        Task pending = Run(store, cancel.Token); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); await cancel.CancelAsync();
        check(await Reject<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10))) && imports == 1 && Clean(),
            "non-cooperative worker is terminated on cancel; no residue, user file deletion, or partial import");
    }

    internal static async Task PackagedRejection(string root, string bundle, Action<bool, string> check)
    {
        string temp = Directory.CreateDirectory(Path.Combine(root, "packaged-jobs")).FullName;
        string source = Path.Combine(root, "not a real game.dem.bz2");
        File.WriteAllText(source, "This synthetic file is neither a Dota replay nor a supported compression stream.");
        var store = new ReviewStore(() => [], _ => true, (_, _) => [], (_, _) => throw new Exception("invalid replay imported"));
        bool copied = false, decompressed = false;
        var progress = new ProgressSink(p => { copied |= p.Text.Contains("复制"); decompressed |= p.Text.Contains("解压"); });
        string message = "";
        try {
            await Task.Run(() => SealedReplayJob.ExecuteAsync(store, "900000001", source, progress, default,
                ct => ReplayToolPin.VerifyAsync(bundle, ReplayToolPin.ManifestSha256, ct), temp, TimeSpan.FromSeconds(40)));
        } catch (InvalidDataException e) { message = e.Message; }
        check(copied && decompressed && message.Contains("compression", StringComparison.OrdinalIgnoreCase) &&
            !Directory.EnumerateFileSystemEntries(temp).Any() && File.Exists(source),
            "real pinned portable CLI accepted the local .dem.bz2 path, rejected synthetic garbage offline, and left no scratch files (" + message + ")");
    }
}
