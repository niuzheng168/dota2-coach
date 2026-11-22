using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DotaCoach.TwoPc;

internal sealed record ReviewProgress(string Text, double? Percent = null);
internal sealed record ReplayTools(string Executable);

/// <summary>Explicit review action only. No discovery/network in the live loop, no shell, no user-supplied executable or URL.</summary>
internal static class SealedReplayJob
{
    internal const string BudgetNotice = "公开元数据最多 8 MiB，录像最多 512 MiB；临时解压上限 2 GiB。请为临时盘预留 3 GiB。" +
        "录像源可能是 Valve HTTP（非认证传输）；会核对 DEM 内局号和终局。任务可取消，完成后仅保留核验过的时间线。";
    private const long MiB = 1024L * 1024, DemLimit = 2048 * MiB, ArchiveLimit = 512 * MiB;
    private static readonly TimeSpan JobLimit = TimeSpan.FromMinutes(22);

    internal static Task RunAsync(ReviewStore store, string id, string? input, IProgress<ReviewProgress> progress, CancellationToken token)
        => ExecuteAsync(store, id, input, progress, token, ReplayToolPin.ResolveAsync, Path.GetTempPath(), JobLimit);

    // Inject only the already-verified tool pair in synthetic tests. Both gates, limits, spawning and cleanup remain real.
    internal static async Task ExecuteAsync(ReviewStore store, string id, string? input, IProgress<ReviewProgress> progress,
        CancellationToken user, Func<CancellationToken, Task<ReplayTools>> resolve, string tempParent, TimeSpan limit)
    {
        user.ThrowIfCancellationRequested();
        if (id.Length is < 1 or > 16 || id[0] == '0' || !id.All(char.IsAsciiDigit) ||
            !ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > 9007199254740991UL)
            throw new InvalidDataException("不是可提交的正式对局号。");
        void RequireEnd() {
            if (!store.IsSealed(id)) throw new InvalidOperationException("这场对局没有经过验证的终局；未启动下载或导入。");
        }
        RequireEnd(); // even before discovering tools, probing local paths, creating temp files, or starting a child
        using var timeout = new CancellationTokenSource(limit);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(user, timeout.Token);
        var ct = combined.Token;
        string? jobDir = null;
        bool committed = false;
        try {
            var tools = await resolve(ct).ConfigureAwait(false);
            tempParent = LocalPath(tempParent);
            long needed = 3 * 1024 * MiB;
            if (input != null) {
                input = LocalPath(input);
                bool compressed = input.EndsWith(".dem.bz2", StringComparison.OrdinalIgnoreCase);
                var f = new FileInfo(input);
                if ((!compressed && !input.EndsWith(".dem", StringComparison.OrdinalIgnoreCase)) || !f.Exists ||
                    (f.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Directory)) != 0)
                    throw new IOException("请选择本机普通 .dem 或 .dem.bz2 文件；不接受链接、设备或网络路径。");
                if (f.Length <= 0 || f.Length > (compressed ? ArchiveLimit : DemLimit))
                    throw new IOException("录像为空或过大（.dem 上限 2 GiB，.dem.bz2 上限 512 MiB）。");
                if (!compressed) needed = f.Length + 96 * MiB;
            }
            var drive = new DriveInfo(Path.GetPathRoot(tempParent)!);
            if (drive.AvailableFreeSpace < needed) throw new IOException($"临时盘 {drive.Name} 空间不足，请至少留出 {(needed + MiB - 1) / MiB} MiB。");
            ct.ThrowIfCancellationRequested();
            RequireEnd(); // pinning is asynchronous; no stale UI decision can authorize process launch
            string fresh = Path.Combine(tempParent, "dota-coach-review-" + Guid.NewGuid().ToString("N"));
            if (Path.Exists(fresh)) throw new IOException("临时目录已存在；未覆盖已有内容。");
            jobDir = Directory.CreateDirectory(fresh).FullName;
            string output = Path.Combine(jobDir, "timeline.json"), marker = Path.Combine(jobDir, "cancel");
            var log = new WorkerLog(id, progress);
            await RunProcessAsync(tools, id, input, output, marker, jobDir, log, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var result = new FileInfo(output);
            if (!log.Published || !result.Exists || result.Length != log.Bytes || result.Length is <= 0 or > MatchArchive.MaxReplayBytes ||
                (result.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("解析器没有为本局发布完整且大小一致的结果；没有导入。");
            progress.Report(new("正在核验并写入本机档案…"));
            RequireEnd();
            ct.ThrowIfCancellationRequested(); // once the archive's bounded atomic commit starts, wait for its real result
            store.Import(id, output);
            committed = true;
        } catch (OperationCanceledException) when (!user.IsCancellationRequested && timeout.IsCancellationRequested) {
            throw new TimeoutException("录像任务超过时间上限，已停止并放弃未导入的结果。");
        } finally {
            if (jobDir != null) await CleanJobAsync(tempParent, jobDir, committed).ConfigureAwait(false);
        }
    }

    private static string LocalPath(string value)
    {
        if (!Path.IsPathFullyQualified(value)) throw new IOException("请选择本机文件的完整路径。");
        string path = Path.GetFullPath(value);
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(path) is not { Length: 3 } root ||
            path.AsSpan(root.Length).Contains(':') || new DriveInfo(root).DriveType == DriveType.Network)
            throw new IOException("录像处理只接受本机磁盘上的路径。");
        // Check parents first: a local-looking path can traverse a junction to a remote/synchronized directory.
        string checkedPath = root;
        foreach (string segment in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
            checkedPath = Path.Combine(checkedPath, segment);
            if ((File.GetAttributes(checkedPath) & (FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Device)) != 0)
                throw new IOException("请选择本机普通文件夹中的文件；不跟随重解析点、设备或待下载的云文件。");
        }
        return path;
    }

    private static async Task CleanJobAsync(string parent, string job, bool committed)
    {
        // Never derive the deletion target from a match ID, DEM path, worker message or user-controlled filename.
        var dir = new DirectoryInfo(job);
        if (!string.Equals(Path.TrimEndingDirectorySeparator(parent), dir.Parent?.FullName, StringComparison.OrdinalIgnoreCase) ||
            !dir.Name.StartsWith("dota-coach-review-", StringComparison.Ordinal))
            throw new IOException("临时目录边界不符，未执行删除。");
        for (int i = 0; ; i++) {
            try { if (dir.Exists) dir.Delete(recursive: true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                if (i == 3) throw new IOException((committed ? "录像已经导入，但" : "") + $"临时文件未能全部清理，请关闭占用程序后删除 {job}。", e);
                await Task.Delay(120 * (i + 1)).ConfigureAwait(false); dir.Refresh();
            }
        }
    }

    private static async Task RunProcessAsync(ReplayTools tools, string id, string? local, string output, string marker,
        string work, WorkerLog log, CancellationToken ct)
    {
        var start = new ProcessStartInfo(tools.Executable) {
            WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardError = true, RedirectStandardOutput = true, StandardErrorEncoding = Encoding.UTF8
        };
        // Do not hand the offline parser the coach's credentials, Python/Node environment or a user PATH.
        start.Environment.Clear();
        foreach (var k in new[] { "SystemRoot", "WINDIR", "SystemDrive" })
            if (Environment.GetEnvironmentVariable(k) is { } v) start.Environment[k] = v;
        start.Environment["TEMP"] = start.Environment["TMP"] = work;
        foreach (var a in new[] { "--match-id", id, "--output", output, "--cancel-file", marker })
            start.ArgumentList.Add(a);
        if (local == null) start.ArgumentList.Add("--download");
        else { start.ArgumentList.Add("--dem"); start.ArgumentList.Add(local); }
        ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        try {
            if (!process.Start()) throw new IOException("无法启动随包离线解析器。");
        } catch (Win32Exception e) { throw new IOException("无法启动随包解析器，请重新解压完整的 Windows x64 教练端包。", e); }
        var stderr = log.DrainAsync(process.StandardError);
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null); // drain both pipes, never retain unbounded logs
        var exit = process.WaitForExitAsync();
        try {
            process.StandardInput.Close();
            await exit.WaitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(stderr, stdout).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) {
                string guidance = process.ExitCode switch {
                    3 => "无法取得公开录像。链接可能未公开、已过期或网络不可用；可以改为导入本地成品录像。",
                    4 => "录像未通过核验。请核对本局局号，并选择完整的、受支持的 5v5 成品录像。",
                    5 => "解析器超时或遇到文件读写问题；请检查临时盘空间和访问权限。",
                    _ => $"离线任务退出码 {process.ExitCode}；没有导入部分结果。" };
                throw new InvalidDataException(guidance + (log.Error != null ? " 详情：" + log.Error : ""));
            }
        } finally {
            if (!process.HasExited) {
                try { await File.WriteAllTextAsync(marker, "").ConfigureAwait(false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                try { await exit.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (TimeoutException) {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) when (process.HasExited) { }
                    await exit.ConfigureAwait(false);
                }
            }
            // The supervisor also owns a kill-on-close Windows job. Cleanup waits for the pipe handles from its children.
            await Task.WhenAll(stderr, stdout).WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        }
    }

    private sealed class WorkerLog(string id, IProgress<ReviewProgress> progress)
    {
        internal bool Published;
        internal long Bytes;
        internal string? Error;
        private long last;
        private string lastStage = "";
        internal async Task DrainAsync(StreamReader stream)
        {
            char[] buf = new char[2048]; var line = new StringBuilder(); bool discard = false;
            int count;
            while ((count = await stream.ReadAsync(buf).ConfigureAwait(false)) > 0) {
                for (int i = 0; i < count; i++) {
                    char c = buf[i];
                    if (c == '\n') { if (!discard) Message(line.ToString()); line.Clear(); discard = false; }
                    else if (line.Length < 4096) line.Append(c); else discard = true;
                }
            }
            if (!discard && line.Length > 0) Message(line.ToString());
        }
        private void Message(string line)
        {
            try {
                using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 5 });
                var d = doc.RootElement;
                if (d.ValueKind != JsonValueKind.Object) return;
                string? Text(string key) => d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                long? Num(string key) => d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;
                string? stage = Text("stage");
                switch (Text("type")) {
                    case "error":
                        if (Text("message") is { Length: > 0 } error) Error ??= MatchReviewText.Clean(error, 260);
                        break;
                    case "success" when stage == "published" && Text("matchId") == id:
                        Published = true; Bytes = Num("bytes") ?? -1; break;
                    case "progress":
                        string? label = stage switch {
                            "download" => "查询公开对局元数据", "archive" => "从 Valve 下载录像",
                            "extract" => "离线读取录像", "decompress" => "解压本地录像副本", "copy" => "复制本地输入",
                            "verify" => "核验局号、文件边界与完整性", "parse" => "解析全图事件", "serialize" => "生成时间线", _ => null };
                        if (label == null || stage == null) break;
                        long now = Environment.TickCount64;
                        if (stage == lastStage && now - last < 250) break;
                        last = now; lastStage = stage;
                        double? pct = Num("tick") is long tick && Num("totalTicks") is long total && total > 0 ?
                            Math.Clamp(100.0 * tick / total, 0, 100) : null;
                        progress.Report(new(label + (pct.HasValue ? $"　{pct:F0}%" :
                            Num("elapsedSeconds") is long s ? $"　已用 {s} 秒" : "…"), pct));
                        break;
                }
            } catch (JsonException) { /* Non-protocol stderr is not evidence or review data. */ }
        }
    }
}
