using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DotaCoach.TwoPc;

/// <summary>
/// scripts/vision/hero_scan.py run by the coach client from a repository folder (its scripts and .tmp/vision-assets)
/// with the Python that has the vision packages. The scan reads a pipe as standard input and ends when it closes:
/// on the stop button, and also when the client exits or crashes.
/// </summary>
public sealed class HeroScanProcess : IAsyncDisposable
{
    public static readonly string Script = Path.Combine("scripts", "vision", "hero_scan.py");
    public const string ConfigName = "gamestate_integration_coach_hero_scan.cfg";
    public const int StopMs = 5000;
    private readonly Process process = new();
    private readonly object gate = new();
    private StreamWriter? log;
    private string lastOutput = "", lastError = "";

    /// <summary>The last line the scanner printed, "" before the first.</summary>
    public string LastOutput { get { lock (gate) return lastOutput; } }
    /// <summary>The last line of standard error: why the scanner failed.</summary>
    public string LastError { get { lock (gate) return lastError; } }
    public int? ExitCode => process.HasExited ? process.ExitCode : null;

    /// <summary>The scanner keeps its GSI token beside the live file the coach client reads.</summary>
    public static string SecretsBeside(string live) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(live))!, "secrets.json");
    public static string ConfigPath(string install) => Path.Combine(install, "game", "dota", "cfg", "gamestate_integration", ConfigName);
    /// <summary>`setup` has run: without its GSI config the scan never learns what Dota shows and selects nobody.</summary>
    public static bool Configured(string install, string live) => File.Exists(SecretsBeside(live)) && File.Exists(ConfigPath(install));

    /// <summary>The repository folder holding the scanner at or above <paramref name="start"/>, or null.</summary>
    public static string? FindFolder(string start)
    {
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, Script))) return dir.FullName;
        return null;
    }
    /// <summary>Whether the folder's scanner can be run by the coach client: older checkouts lack --stop-with-stdin and exit at once.</summary>
    public static bool Supported(string folder)
    {
        try { return File.ReadAllText(Path.Combine(folder, Script)).Contains("--stop-with-stdin", StringComparison.Ordinal); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    /// <summary>The vision venv (.tmp/vision-tools) of the folder, or of the main checkout when the folder is a git worktree.</summary>
    public static string? FindPython(string folder)
    {
        foreach (var root in new[] { folder, MainCheckout(folder) }.OfType<string>()) {
            string python = Path.Combine(root, ".tmp", "vision-tools", "Scripts", "python.exe");
            if (File.Exists(python)) return python;
        }
        return null;
    }
    /// <summary>A worktree's .git is a file: "gitdir: &lt;main&gt;/.git/worktrees/&lt;name&gt;".</summary>
    private static string? MainCheckout(string folder)
    {
        try {
            string git = Path.Combine(folder, ".git");
            if (!File.Exists(git) || File.ReadLines(git).FirstOrDefault() is not { } line || !line.StartsWith("gitdir:", StringComparison.Ordinal))
                return null;
            return new DirectoryInfo(line[7..].Trim()).Parent?.Parent?.Parent?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>`hero_scan.py setup`: the scanner's GSI token and Dota config. The exit code and everything it printed.</summary>
    public static async Task<(int Code, string Output)> Setup(string python, string folder, string install, string live, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath(install))!);
        using var process = Process.Start(Info(python, folder, "setup", "--install", install, "--secrets", SecretsBeside(live)))!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { Kill(process); throw; }
        return (process.ExitCode, await output + await error);
    }

    /// <summary>Starts the scan, writing <paramref name="live"/> for the coach client; every line it prints also goes to <paramref name="logFile"/>.</summary>
    public static HeroScanProcess Start(string python, string folder, string live, string? logFile)
    {
        // A day: the scanner's own default ends after an hour, while the coach client runs for many matches.
        var scan = new HeroScanProcess();
        scan.process.StartInfo = Info(python, folder, "scan", "--stop-with-stdin", "--duration", "86400",
            "--live", Path.GetFullPath(live), "--secrets", SecretsBeside(live));
        scan.process.OutputDataReceived += (_, e) => scan.Line(e.Data, error: false);
        scan.process.ErrorDataReceived += (_, e) => scan.Line(e.Data, error: true);
        if (logFile != null) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logFile))!);
            scan.log = new StreamWriter(logFile, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        try { scan.process.Start(); }
        catch { scan.log?.Dispose(); scan.process.Dispose(); throw; }
        scan.process.BeginOutputReadLine(); scan.process.BeginErrorReadLine();
        return scan;
    }

    /// <summary>Closes standard input: the scanner stops and marks its live file stopped. Killed if still there after <paramref name="waitMs"/>.</summary>
    public async Task StopAsync(int waitMs = StopMs)
    {
        try { process.StandardInput.Close(); } catch (IOException) { }
        using var timeout = new CancellationTokenSource(waitMs);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { Kill(process); await process.WaitForExitAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) await StopAsync();
        process.Dispose();
        lock (gate) { log?.Dispose(); log = null; }
    }

    private void Line(string? line, bool error)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (gate) {
            if (error) lastError = line.Trim(); else lastOutput = line.Trim();
            log?.WriteLine(line);
        }
    }

    private static ProcessStartInfo Info(string python, string folder, params string[] args)
    {
        var info = new ProcessStartInfo(python) {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        // -I: nothing from the environment or the working folder is imported; -X utf8: tracebacks in UTF-8 too; -u: lines arrive as printed.
        foreach (var arg in new[] { "-I", "-X", "utf8", "-u", Script }.Concat(args)) info.ArgumentList.Add(arg);
        return info;
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
    }
}
