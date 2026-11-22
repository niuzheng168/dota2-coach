using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DotaCoach.TwoPc;

internal static class ReplayToolPin
{
    // Pin the complete deterministic build_windows.py tree, not just the distlib bootstrap .exe.
    // A sibling .sha256/.json supplied by a modified package is not itself a trust anchor.
    internal const string ManifestSha256 = "BFE9801AFB125616B5C9208DA9CBD47A3BE48F0A59172C74DA7279980629D783";
    private const string Manifest = "sha256.json", Launcher = "PostgameReplay.exe";

    internal static Task<ReplayTools> ResolveAsync(CancellationToken ct)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "replay-tools");
        if (!Directory.Exists(root)) {
            string? supplied = Environment.GetEnvironmentVariable("DOTA_COACH_REPLAY_TOOLS");
            if (string.IsNullOrEmpty(supplied) || !Path.IsPathFullyQualified(supplied))
                throw new FileNotFoundException("未找到随包录像工具。请完整解压同版本 Windows x64 教练端（含 replay-tools 目录）。" +
                    "源码运行可将 DOTA_COACH_REPLAY_TOOLS 指向 build_windows.py 生成的同版完整目录；不需要系统 Python，也不会自动安装依赖。");
            root = supplied;
        }
        return VerifyAsync(root, ManifestSha256, ct);
    }

    internal static async Task<ReplayTools> VerifyAsync(string root, string expectedManifest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dir = new DirectoryInfo(root);
        if (!dir.Exists || dir.FullName.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(dir.Root.FullName).DriveType == DriveType.Network)
            throw new IOException("录像工具必须是本机磁盘上的完整目录。");
        for (var p = dir; p != null; p = p.Parent) EnsureNotLink(p);
        var manifest = new FileInfo(Path.Combine(dir.FullName, Manifest));
        if (!manifest.Exists || manifest.Length is <= 0 or > 2 * 1024 * 1024) throw BadPin();
        EnsureNotLink(manifest);
        byte[] bytes;
        using (var s = manifest.OpenRead()) {
            if (s.Length is <= 0 or > 2 * 1024 * 1024) throw BadPin();
            bytes = new byte[(int)s.Length];
            await s.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        }
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedManifest, StringComparison.OrdinalIgnoreCase)) throw BadPin();
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2, AllowDuplicateProperties = false });
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.RootElement.EnumerateObject()) {
            if (files.Count >= 4096 || p.Name.Length > 400 || p.Name.Contains('\\') || Path.IsPathRooted(p.Name) ||
                p.Name.Split('/').Any(s => s is "" or "." or ".." || s.Contains(':')) ||
                p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() is not { Length: 64 } sha || !sha.All(Uri.IsHexDigit) ||
                !files.TryAdd(p.Name, sha)) throw BadPin();
            for (int i = p.Name.IndexOf('/'); i >= 0; i = p.Name.IndexOf('/', i + 1)) directories.Add(p.Name[..i]);
        }
        if (!files.ContainsKey(Launcher) || !files.ContainsKey("runtime/python.exe") || !files.ContainsKey("node/node.exe") ||
            !files.ContainsKey("scripts/replays/postgame/cli.py")) throw BadPin();

        // Reject additions as well as modifications (e.g. a DLL, Python module, package.json, or ._pth file).
        var pending = new Stack<DirectoryInfo>(); pending.Push(dir);
        long budget = 768L * 1024 * 1024;
        while (pending.TryPop(out var next)) foreach (var f in next.EnumerateFileSystemInfos()) {
            ct.ThrowIfCancellationRequested(); EnsureNotLink(f);
            string rel = Path.GetRelativePath(dir.FullName, f.FullName).Replace('\\', '/');
            if (f is DirectoryInfo sub) {
                if (!directories.Contains(rel)) throw BadPin();
                pending.Push(sub);
            } else {
                if (rel == Manifest) continue;
                if (!files.Remove(rel, out string? expected)) throw BadPin();
                using var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                budget -= s.Length;
                if (budget < 0 || !Convert.ToHexString(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false))
                    .Equals(expected, StringComparison.OrdinalIgnoreCase)) throw BadPin();
            }
        }
        if (files.Count != 0) throw BadPin();
        return new(Path.Combine(dir.FullName, Launcher));
    }

    private static void EnsureNotLink(FileSystemInfo f) {
        if ((f.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) throw BadPin();
    }
    private static InvalidDataException BadPin() => new("录像工具哈希校验失败、文件缺失或含意外文件。" +
        "请在新的本机文件夹完整解压同版本教练端，不要混用不同版本。未运行程序。");
}
