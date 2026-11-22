using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using DotaCoach.TwoPc;

internal static class RecoveryChecks
{
    private const string BrokenLog = "940003", BrokenSeal = "940002", BrokenReplay = "940001", Healthy = "810000";
    private static readonly string[] Ids = [BrokenLog, BrokenSeal, BrokenReplay, Healthy];
    private static MatchFact Live(string id) => new(1, id, 1, 1, "own-gsi", "phase", JsonSerializer.SerializeToElement(new { phase = "playing" }));

    internal static async Task Run(string temp, Action<bool, string> check)
    {
        string root = Path.Combine(temp, "catalog-recovery"), settings = Path.Combine(temp, "recovery-settings.json");
        string candidate = Path.Combine(temp, "validated-input.json");
        using (var a = new MatchArchive(root)) {
            foreach (var id in Ids) { a.Append(Live(id)); a.Seal(id, 30); }
            var replay = JsonNode.Parse(ReviewChecks.ReplayJson(Healthy))!;
            var heroRows = replay["events"]!.AsArray().Where(e => (string?)e!["kind"] == "replay-hero");
            JsonNode At(int clock, int slot) => heroRows.Single(e => (int)e!["clock"]! == clock && (int)e["data"]!["playerId"]! == slot)!["data"]!;
            // Sparse contemporary counters, an old-style frame for slot 4, and unrelated counters for slot 8.
            At(9, 8)["economy"] = new JsonObject { ["netWorth"] = 88771, ["currentXP"] = 888 };
            At(26, 4)["economy"] = new JsonObject { ["netWorth"] = 5202, ["lastHits"] = 0, ["currentXP"] = 777,
                ["totalEarnedGold"] = 6203, ["creepKillGold"] = 53 };
            File.WriteAllText(candidate, replay.ToJsonString()); a.ImportReplay(Healthy, candidate);
        }
        // Syntactically complete LF row, but its deferred JsonElement string has an invalid escaped surrogate.
        File.AppendAllText(Path.Combine(root, BrokenLog, "live.jsonl"),
            """{"sequence":2,"matchId":"940003","clock":2,"knownAt":2,"source":"coach","kind":"gap","data":{"reason":"\uD800"}}""" + "\n");
        var sealPath = Path.Combine(root, BrokenSeal, "sealed.json");
        var seal = JsonNode.Parse(File.ReadAllText(sealPath))!; seal["lastSequence"] = 123; File.WriteAllText(sealPath, seal.ToJsonString());
        foreach (var id in Ids.Take(3)) File.WriteAllText(Path.Combine(root, id, "replay.json"), """{"invalid":"must not be replaced"}""");
        string[] Evidence() => Ids.SelectMany(id => Directory.EnumerateFiles(Path.Combine(root, id)).Order()
            .Select(f => $"{Path.GetRelativePath(root, f)}:{File.GetLastWriteTimeUtc(f).Ticks}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}")).ToArray();
        var original = Evidence();
        int forbiddenCalls = 0;
        static bool Denied<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
        static string Label(MatchReviewWindow w, object choice) =>
            (string)TypeDescriptor.GetProperties(choice)[w.Matches.DisplayMemberPath]!.GetValue(choice)!;
        static void Select(MatchReviewWindow w, string id) => w.Matches.SelectedItem =
            w.Matches.Items.Cast<object>().Single(c => Label(w, c).StartsWith(id, StringComparison.Ordinal));
        static async Task Wait(Func<bool> condition, string detail) {
            long start = Environment.TickCount64;
            while (!condition()) { if (Environment.TickCount64 - start > 10000) throw new TimeoutException(detail); await Task.Delay(10); }
        }
        static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static MatchReviewWindow Open(ReviewStore store, string settings) {
            var w = new MatchReviewWindow(store, () => BrokenLog, new HeroNames(), settings) {
                Left = -15000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
            w.Show(); return w;
        }
        using (var a = new MatchArchive(root)) {
            var listed = a.List();
            check(listed.Count == 4 && listed.Where(e => e.Damaged).Select(e => e.MatchId).SequenceEqual([BrokenLog, BrokenSeal]) &&
                listed.Where(e => e.Damaged).All(e => !e.Ended && !e.HasReplay), "failed journal/manifest recovery preserves both IDs as unavailable alongside healthy history");
            check(a.Read(Healthy).Count == 1 && a.IsSealed(Healthy) && Denied<IOException>(() => a.ImportReplay(BrokenSeal, candidate)),
                "a mismatched seal with a planted sidecar cannot import; an unrelated match retains its authentic seal");
            var live = new LiveMatchState(a, () => 1000);
            var guarded = ReviewStore.From(a, () => live.CanAudit);
            var store = guarded with {
                IsSealed = id => { if (id is BrokenLog or BrokenSeal) forbiddenCalls++; return guarded.IsSealed(id); },
                Read = (id, replay) => { if (id is BrokenLog or BrokenSeal) forbiddenCalls++; return guarded.Read(id, replay); },
                Import = (id, f) => { if (id is BrokenLog or BrokenSeal) forbiddenCalls++; guarded.Import(id, f); }
            };
            var w = Open(store, settings);
            try {
                await Wait(() => w.MatchStatus.Text.Contains(BrokenLog) && w.MatchStatus.Text.Contains("损坏"), "selected corrupt log");
                Select(w, BrokenSeal); w.CompareMode.IsChecked = true; Click(w.LocalReplay); Click(w.DownloadReplay);
                check(w.Matches.Items.Count == 4 && Label(w, w.Matches.SelectedItem).Contains("损坏") &&
                    !w.Scrubber.IsEnabled && !w.ReplayMode.IsEnabled && !w.DownloadReplay.IsEnabled && !w.LocalReplay.IsEnabled &&
                    w.KnownPane.Events.Items.Count == 0 && w.ReplayPane.Events.Items.Count == 0 && forbiddenCalls == 0,
                    "selecting known damage clears both panes and blocks even raised click events without reading a replay or probing a seal");
                Select(w, BrokenReplay);
                await Wait(() => w.ReplayMode.IsEnabled, "metadata-only listing for uninspected sidecar");
                w.ReplayMode.IsChecked = true;
                await Wait(() => w.MatchStatus.Text.Contains("损坏") && Label(w, w.Matches.SelectedItem).Contains("损坏"), "explicit replay read discovered corruption");
                check(w.Matches.Items.Count == 4 && !w.LocalReplay.IsEnabled && !w.CompareMode.IsEnabled &&
                    a.List().Single(e => e.MatchId == BrokenReplay).Damaged && Denied<IOException>(() => a.ImportReplay(BrokenReplay, candidate)) &&
                    a.Read(BrokenReplay).Count == 1, "newly discovered sidecar damage locks reimport without deleting the catalog or altering the independent live prefix");
                Select(w, Healthy); await Wait(() => w.CompareMode.IsEnabled, "healthy match after corrupt selection");
                w.CompareMode.IsChecked = true; await Wait(() => w.Scrubber.Maximum == 120, "healthy replay");
                w.Scrubber.Value = 26; w.ReplayPane.HeroSlot.SelectedIndex = 5;
                string snapshot = w.ReplayPane.SnapshotText.Text, row = w.ReplayPane.DetailText.Text;
                check(new[] { snapshot, row }.All(s => s.Contains("净资产 5202") && s.Contains("补刀/反补 0/未知") &&
                    s.Contains("当前经验 777") && s.Contains("累计赚金 6203") && s.Contains("小兵击杀金 53")) &&
                    !w.KnownPane.SnapshotText.Text.Contains("5202"), "selected replay slot and event display the observed counters, including zero, and label each unreported counter unknown");
                w.Scrubber.Value = 9;
                w.ReplayPane.Events.SelectedItem = w.ReplayPane.Events.Items.Cast<ReviewRow>().Single(r => r.When == "0:09" && r.Detail.Contains("玩家槽 4"));
                check(w.ReplayPane.SnapshotText.Text.Contains("录像经济：未知") && w.ReplayPane.DetailText.Text.Contains("录像经济：未知") &&
                    !w.ReplayPane.SnapshotText.Text.Contains("88771") && !w.ReplayPane.SnapshotText.Text.Contains("5202"),
                    "rewind to a v1 slot without economy shows unknown, not a teammate's, later frame's, or inferred value");
                // The actual reducer enters live-only when it tries to resume the damaged recording. The
                // older healthy seal is still valid on disk: only the runtime audit guard revokes that review.
                live.ObserveFrame(new FrameView(new("fixture", "fixture", 1, "dota2-gsi", 1920, 1080, null, null,
                    new(BrokenSeal, "radiant", "npc_dota_hero_luna", 2, "playing", false)), [], 1000, 1000));
                check(!live.CanAudit && a.IsSealed(Healthy), "real live-only transition revokes audit despite the historical match still having a good seal");
                Click(w.DownloadReplay); Click(w.LocalReplay);
                check(!guarded.IsSealed(Healthy) && Denied<InvalidOperationException>(() => guarded.Import(Healthy, candidate)) &&
                    Denied<InvalidOperationException>(() => guarded.Read(Healthy, true)) && w.Notice.Text.Contains("仅实时显示") &&
                    !w.DownloadReplay.IsEnabled && !w.LocalReplay.IsEnabled && w.ReplayPane.Events.Items.Count == 0 &&
                    w.KnownPane.Events.Items.Count == 1, "writer fault revokes an already-open review's launch/read/import delegates while its cached known prefix remains usable");
            } finally { await w.StopAsync(); }
        }
        using (var reopened = new MatchArchive(root)) {
            var w = Open(ReviewStore.From(reopened, () => true), settings);
            try {
                await Wait(() => w.MatchStatus.Text.Contains(Healthy) && w.CompareMode.IsEnabled, "bookmark restored with damaged neighbors");
                check(w.Matches.Items.Count == 4 && w.KnownMode.IsChecked == true && w.KnownPane.Events.Items.Count == 1 &&
                    w.Matches.Items.Cast<object>().Count(c => Label(w, c).Contains("损坏")) == 2,
                    "restart reopens the selected healthy match; corrupt historical IDs remain independently selectable");
                File.WriteAllText(candidate, ReviewChecks.ReplayJson(BrokenReplay));
                check(Denied<InvalidDataException>(() => reopened.ImportReplay(BrokenReplay, candidate)) &&
                    reopened.List().Single(e => e.MatchId == BrokenReplay).Damaged, "even an uninspected bad stored replay cannot be overwritten by a valid reimport");
            } finally { await w.StopAsync(); }
        }
        check(original.SequenceEqual(Evidence()), "listing, selecting, failed reimports and restart neither repair nor rewrite any original match evidence");
        var failed = new MatchArchive(Path.Combine(temp, "catalog-global-fault"));
        File.WriteAllText(Path.Combine(failed.Root, Healthy), "not a directory"); failed.Append(Live(Healthy));
        check(Denied<IOException>(() => failed.List()) && Denied<IOException>(failed.Dispose),
            "a failed writer durability barrier is still a global error, not a fabricated damaged catalog row");
    }
}
