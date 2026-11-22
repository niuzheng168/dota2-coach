using System.Diagnostics;
using DotaCoach.TwoPc;

/// <summary>
/// The coach client's 启动视觉扫描: finding the repository and its vision Python, and the scan's life as a child process,
/// against stand-in scripts (the real scanner needs Dota). The process checks need the vision Python of this checkout.
/// </summary>
internal static class HeroScanTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "dotacoach-scan-" + Guid.NewGuid().ToString("N"));
        try {
            // A git worktree finds the venv of its main checkout.
            string main = Path.Combine(root, "main checkout"), tree = Path.Combine(root, "tree");
            Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "tree"));
            Directory.CreateDirectory(Path.Combine(main, ".tmp", "vision-tools", "Scripts"));
            string venv = Path.Combine(main, ".tmp", "vision-tools", "Scripts", "python.exe");
            File.WriteAllText(venv, "");
            Directory.CreateDirectory(Path.Combine(tree, "scripts", "vision"));
            File.WriteAllText(Path.Combine(tree, HeroScanProcess.Script), "# old scan --stop-with-stdin\n");
            File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: " + Path.Combine(main, ".git", "worktrees", "tree").Replace('\\', '/') + "\n");
            string bin = Directory.CreateDirectory(Path.Combine(tree, "desktop", "bin")).FullName;
            check(HeroScanProcess.FindFolder(bin) == tree && HeroScanProcess.FindFolder(root) == HeroScanProcess.FindFolder(Path.GetDirectoryName(root)!),
                "the nearest scanner ancestor is found; the search never descends into a child or sibling fixture");
            check(HeroScanProcess.FindPython(tree) == venv && HeroScanProcess.FindPython(main) == venv && HeroScanProcess.FindPython(root) == null,
                "the vision Python is the checkout's own venv, or the main checkout's for a worktree");
            string assets = Directory.CreateDirectory(Path.Combine(main, ".tmp", "vision-assets")).FullName;
            File.WriteAllText(Path.Combine(assets, "manifest.json"), "{}"); File.WriteAllBytes(Path.Combine(assets, "hud-1080.npz"), []);
            string local = Directory.CreateDirectory(Path.Combine(tree, ".tmp", "vision-assets")).FullName;
            File.WriteAllText(Path.Combine(local, "manifest.json"), "{}");
            check(HeroScanProcess.FindWorkspace(bin) == main, "a worktree without HUD calibration can use the main checkout's local assets");
            File.WriteAllBytes(Path.Combine(local, "hud-1080.npz"), []);
            check(HeroScanProcess.FindWorkspace(bin) == tree, "worktree-local HUD assets take precedence; ward digit calibration is optional");
            string live = Path.Combine(root, "hero-scan", "live.json"), install = Path.Combine(root, "dota");
            check(!HeroScanProcess.Configured(install, live) && HeroScanProcess.SecretsBeside(live) == Path.Combine(root, "hero-scan", "secrets.json"),
                "without the scanner's token and GSI config the coach runs setup first");

            if (HeroScanProcess.FindFolder(AppContext.BaseDirectory) is not { } repo || HeroScanProcess.FindPython(repo) is not { } python) {
                Console.WriteLine("SKIP vision scan process checks: no .tmp\\vision-tools Python in this checkout.");
                return;
            }
            // Stand-in scanner: prints like hero_scan.py and stops only when standard input closes.
            string fake = Path.Combine(root, "fake");
            Directory.CreateDirectory(Path.Combine(fake, "scripts", "vision"));
            File.WriteAllText(Path.Combine(fake, HeroScanProcess.Script), """
                import sys
                from pathlib import Path
                args = sys.argv[1:]
                if args[0] == "setup":
                    print("Wrote", args[args.index("--install") + 1], "- restart Dota once so it is loaded.")
                    sys.exit(0)
                if "--stop-with-stdin" not in args or "--team-view-only" not in args or args[args.index("--duration") + 1] != "86400":
                    raise SystemExit("bad arguments: " + " ".join(args))
                live = Path(args[args.index("--live") + 1])
                assets = live.parent.parent / "main checkout" / ".tmp" / "vision-assets"
                for option, expected in {"--assets": assets, "--calibration": assets / "hud-1080.npz",
                                         "--secrets": live.with_name("secrets.json"), "--out": live.parent / "sessions"}.items():
                    if Path(args[args.index(option) + 1]) != expected:
                        raise SystemExit("wrong path for " + option)
                print("Waiting for the Dota window...", flush=True)
                print("Capturing the Dota window (1280x720). 中文", flush=True)
                sys.stdin.buffer.read()
                print("Vision scan stopped.")
                """);
            check(HeroScanProcess.Supported(fake) && !HeroScanProcess.Supported(tree) && !HeroScanProcess.Supported(root),
                "a stoppable legacy scanner without --team-view-only is refused before it is started");
            var (code, output) = await HeroScanProcess.Setup(python, fake, install, live, default);
            check(code == 0 && output.Contains("Wrote " + install) && Directory.Exists(Path.GetDirectoryName(HeroScanProcess.ConfigPath(install))),
                "setup runs with the Dota folder the coach uses and finds its cfg folder in place");
            string log = Path.Combine(root, "hero-scan", "coach-scan.log");
            var scan = HeroScanProcess.Start(python, fake, live, log, main);
            var started = Stopwatch.StartNew();
            while (scan.LastOutput != "Capturing the Dota window (1280x720). 中文" && scan.ExitCode == null && started.ElapsedMilliseconds < 10000)
                await Task.Delay(20);
            check(scan.ExitCode == null && scan.LastOutput.StartsWith("Capturing the Dota window", StringComparison.Ordinal) && scan.LastOutput.EndsWith("中文"),
                "the scan runs with the coach's arguments and its lines arrive as printed, in UTF-8");
            var stopping = Stopwatch.StartNew();
            await scan.StopAsync();
            check(scan.ExitCode == 0 && stopping.ElapsedMilliseconds < HeroScanProcess.StopMs, "closing standard input ends the scan on its own, before any kill");
            await scan.DisposeAsync();
            check(File.ReadAllText(log).Contains("Waiting for the Dota window...") && File.ReadAllText(log).Contains("Vision scan stopped."),
                "everything the scan printed is kept in the log beside the live file");

            File.WriteAllText(Path.Combine(fake, HeroScanProcess.Script), "raise SystemExit('No secrets at x; run `setup` first')\n");
            await using (var failed = HeroScanProcess.Start(python, fake, live, null)) {
                started.Restart();
                while (failed.ExitCode == null && started.ElapsedMilliseconds < 10000) await Task.Delay(20);
                await Task.Delay(200); // the last error line may arrive just after the exit
                check(failed.ExitCode == 1 && failed.LastError == "No secrets at x; run `setup` first", "a failing scan reports its exit code and last error line");
            }
            File.WriteAllText(Path.Combine(fake, HeroScanProcess.Script), "import time\ntime.sleep(60)\n");
            var stuck = HeroScanProcess.Start(python, fake, live, null);
            stopping.Restart();
            await stuck.StopAsync(waitMs: 500);
            check(stuck.ExitCode != null && stopping.ElapsedMilliseconds < 5000, "a scan that ignores the closed input is killed");
            await stuck.DisposeAsync();
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
