using DotaCoach.TwoPc;

internal static class LiveOnlyTests
{
    public static void Run(Action<bool, string> check)
    {
        const string a = "9100200001", b = "9100200002", hero = "npc_dota_hero_luna", enemy = "npc_dota_hero_lion";
        string session = Guid.NewGuid().ToString("N");
        long time = 4300, seq = 0;
        FrameView Frame(string id, int game, string phase = "playing") {
            var own = new OwnState(id, hero, "dire", game, true, 1000, 340, -2100, 400, [], [],
                new(true, true, false, Level: 10, InstalledBuild: 6989, Epoch: Guid.Empty));
            return new(new(session, "only-a-ticket", ++seq, "dota2", 1920, 1080, phase == "postgame" ? null : own,
                phase == "postgame" ? null : 1, new(id, "dire", hero, game, phase, false) { AgeMs = 1 }), [], time, time);
        }
        void Live(LiveMatchState state, string id) {
            var f = Frame(id, 605); state.ObserveFrame(f);
            state.ObserveEquipment(id, "dire", 605, [new(enemy, 7, ["item_boots", null, null, null, null, null], null, 0)]);
            var shown = state.ForGame(f, true, false, null);
            check(state.ActiveMatch == id && !state.CanAudit && state.StorageWarning?.Contains("仅实时显示") == true &&
                shown?.MatchId == id && shown.Heroes.Single().Hero == enemy && state.ForFarm(f, true) == null && state.ForVision(f) == null,
                "storage unavailable: real-time filtered equipment remains useful; no paid input or claim of archival success");
            state.ObserveFrame(Frame(id, 610, "postgame"));
            check(state.ActiveMatch == null && state.ForGame(f, true, false, null) == null,
                "unrecorded but valid end withdraws the session without inventing an archive");
        }
        Live(new LiveMatchState(null, () => time), a);
        var temp = Directory.CreateTempSubdirectory("match-writer-failure-");
        var archive = new MatchArchive(temp.FullName);
        try {
            // This unrelated regular file occupies the match-directory name; never overwrite it.
            var blocker = Path.Combine(temp.FullName, a); File.WriteAllText(blocker, "preserve");
            var state = new LiveMatchState(archive, () => time);
            state.ObserveFrame(Frame(a, 100));
            try { archive.LastSequence(a); throw new Exception("writer failure was not reported"); } catch (IOException) { }
            state.ObserveFrame(Frame(a, 101)); // observes the async failure
            time += 100; Live(state, b);
            check(!Directory.Exists(Path.Combine(temp.FullName, b)) && File.ReadAllText(blocker) == "preserve",
                "failed writer never 'recovers' by creating a substitute archive or overwriting an unrelated file");
        } finally {
            try { archive.Dispose(); } catch (IOException) { /* the injected failure stays visible */ }
            temp.Delete(recursive: true);
        }
    }
}
