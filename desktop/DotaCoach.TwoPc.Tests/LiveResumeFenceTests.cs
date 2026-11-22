using System.Text.Json;
using DotaCoach.TwoPc;

internal static class LiveResumeFenceTests
{
    private const string A = "9130200001", B = "9130200002", Luna = "npc_dota_hero_luna", Lion = "npc_dota_hero_lion";
    private const long Now = 17_000;
    private static FrameView Frame(string id, double clock, long seq, string session = "first", string side = "radiant",
        string hero = Luna, string phase = "playing", int age = 0, int lag = 0) =>
        new(new(session, "fixture", seq, "dota2-gsi", 1920, 1080, null, null,
            new(id, side, hero, clock, phase, false) { AgeMs = age }), [], Now - lag, Now);
    private static MatchFact Fact(long seq, string id, double at, double known, string source, string kind, object data) =>
        new(seq, id, at, known, source, kind, JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web));
    private static bool ResumeGap(MatchFact f) => f is { Source: "coach", Kind: "gap" } &&
        f.Data.GetProperty("reason").GetString() == "resumed-after-interruption";

    public static void Run(Action<bool, string> check)
    {
        var temp = Directory.CreateTempSubdirectory("dota-live-resume-");
        try {
            Reopen(Path.Combine(temp.FullName, "restart"), check);
            Switch(Path.Combine(temp.FullName, "switch"), check);
            PregameAndSparse(Path.Combine(temp.FullName, "sparse"), check);
            Unreadable(Path.Combine(temp.FullName, "corrupt"), check);
            CachedDamage(Path.Combine(temp.FullName, "cached"), check);
        } finally { temp.Delete(recursive: true); }
    }

    private static void Reopen(string root, Action<bool, string> check)
    {
        string log = Path.Combine(root, A, "live.jsonl");
        long last;
        var expected = new LiveResumeFence("radiant", Luna, 105, true);
        using (var a = new MatchArchive(root)) {
            var s = new LiveMatchState(a, () => Now);
            s.ObserveFrame(Frame(A, 100, 1));
            var active = Frame(A, 105, 2); s.ObserveFrame(active);
            s.ObserveEquipment(A, "radiant", 99, [new(Lion, 12, ["item_blink", null, null, null, null, null], null, 1)]);
            (last, var fence) = a.GetLiveResume(A);
            check(s.ForGame(active, true, false, null)?.Heroes.Single().Hero == Lion && fence == expected &&
                a.Read(A)[^1] is { Kind: "equipment", Clock: 99, KnownAt: 105 }, "live fence counts when delayed equipment became known, not its observation or the last phase clock");
        }
        byte[] committed = File.ReadAllBytes(log);
        // A complete-looking future phase without LF is NOT committed. A replay must not even be opened.
        File.AppendAllText(log, JsonSerializer.Serialize(Fact(last + 1, A, 900, 900, "own-gsi", "phase",
            new { team = "dire", hero = Lion, phase = "playing" }), JsonSerializerOptions.Web));
        byte[] torn = File.ReadAllBytes(log);
        using var replay = File.Open(Path.Combine(root, A, "replay.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        replay.Write("not a live input"u8); replay.Flush();
        using (var a = new MatchArchive(root)) {
            check(a.GetLiveResume(A) == (last, expected), "durable reopen recovers the identical live-only prefix despite a torn line and locked replay");
            int reads = 0;
            var delayedAdmission = new LiveMatchState(a, () => ++reads == 1 ? Now : Now + 100);
            delayedAdmission.ObserveFrame(Frame(A, 105, 1, age: 2900));
            check(delayedAdmission.ActiveMatch == null && a.LastSequence(A) == last && File.ReadAllBytes(log).AsSpan().SequenceEqual(torn),
                "a compatible witness that uses its last 100 ms during the storage lookup is not fresh at admission");
            var s = new LiveMatchState(a, () => Now);
            foreach (var (why, packet) in new[] {
                ("opposite side", Frame(A, 110, 1, side: "dire")),
                ("different hero", Frame(A, 110, 2, hero: Lion)),
                ("submillisecond rewind", Frame(A, 104.9999, 3)),
                ("pregame after playing", Frame(A, 110, 4, phase: "pregame")),
                ("isolated terminal even with the old session name", Frame(A, 110, 5, phase: "postgame")),
                ("stale combined witness and transport age", Frame(A, 110, 6, age: 2900, lag: 101)),
                ("no positive transport sequence", Frame(A, 110, 0))
            }) {
                s.ObserveFrame(packet);
                check(s.ActiveMatch == null && s.CanAudit && !a.IsSealed(A) && a.GetLiveResume(A) == (last, expected) &&
                    File.ReadAllBytes(log).AsSpan().SequenceEqual(torn), $"restart rejects {why} without any write, tail repair, attachment or seal");
            }
            var first = Frame(A, 105, 1, session: "fresh");
            s.ObserveFrame(first); s.ObserveFrame(first);
            var added = a.Read(A).Where(f => f.Sequence > last).ToArray();
            check(s.ActiveMatch == A && s.CanAudit && s.ForGame(first, true, false, null) == null && !a.IsSealed(A) &&
                File.ReadAllBytes(log).AsSpan().StartsWith(committed), "compatible fresh frame at equal high-water resumes the original prefix but restores no equipment");
            check(added.Length == 2 && ResumeGap(added[0]) && added[1] is { Source: "own-gsi", Kind: "phase" } &&
                added[1].Data.GetProperty("resumed").GetBoolean() && added.All(f => f.Clock == 105 && f.KnownAt == 105) &&
                added[0].Sequence == last + 1 && added[1].Sequence == last + 2,
                "repeated UI tick records exactly one interruption and one new phase at the current known-at");
            s.ObserveFrame(Frame(A, 110, 2, phase: "postgame")); // old transport
            check(!a.IsSealed(A) && a.LastSequence(A) == last + 2, "old session's terminal cannot borrow the resumed playing frame");
            s.ObserveFrame(Frame(A, 110, 2, session: "fresh", phase: "postgame"));
        }
        using var sealedArchive = new MatchArchive(root);
        check(sealedArchive.IsSealed(A) && sealedArchive.GetLiveResume(A) == (last + 3, expected with { MaxKnownAt = 110 }) &&
            sealedArchive.Read(A)[^1].Data.GetProperty("phase").GetString() == "postgame",
            "only the new session's own playing-to-postgame transition seals the continuous live log");
    }

    private static void Switch(string root, Action<bool, string> check)
    {
        using var a = new MatchArchive(root);
        var s = new LiveMatchState(a, () => Now);
        s.ObserveFrame(Frame(A, 60, 1)); s.ObserveFrame(Frame(A, 80, 2));
        s.ObserveFrame(Frame(B, 5, 1, "second", "dire", Lion));
        var beforeA = a.GetLiveResume(A); var beforeB = a.GetLiveResume(B);
        s.ObserveFrame(Frame(A, 82, 3, "other", "dire", Lion));
        s.ObserveFrame(Frame(A, 79, 4));
        s.ObserveFrame(Frame(A, 90, 5, phase: "postgame"));
        check(s.ActiveMatch == B && a.GetLiveResume(A) == beforeA && a.GetLiveResume(B) == beforeB &&
            beforeA.Fence?.MaxKnownAt == 80 && !a.IsSealed(A) && !a.IsSealed(B),
            "switch-away/back rejects foreign, backwards and terminal frames without withdrawing or writing either game");
        s.ObserveFrame(Frame(A, 83, 1, session: "return"));
        var rows = a.Read(A).Where(f => f.Sequence > beforeA.Sequence).ToArray();
        check(s.ActiveMatch == A && s.CanAudit && rows.Length == 2 && ResumeGap(rows[0]) && rows[0].KnownAt == 83 &&
            rows[0].Sequence == beforeA.Sequence + 1 && a.Read(B)[^1] is { Kind: "gap", KnownAt: 5 } &&
            a.GetLiveResume(A).Fence == new LiveResumeFence("radiant", Luna, 83, true),
            "same-process return continues A at its own sequence; the accepted switch closes only B's active interval");
    }

    private static void PregameAndSparse(string root, Action<bool, string> check)
    {
        var expected = new LiveResumeFence("radiant", Luna, -30, false);
        using (var a = new MatchArchive(root)) {
            check(a.GetLiveResume(A) == (0L, (LiveResumeFence?)null) && !Directory.Exists(Path.Combine(root, A)),
                "a resume lookup cannot create a new match");
            a.Append(Fact(1, A, -65, -30, "team-vision", "team-sight", new { heroes = Array.Empty<object>() }));
            a.Append(Fact(2, A, -62, -62, "own-gsi", "phase", new { team = "radiant", hero = Luna, phase = "pregame" }));
            a.Append(Fact(3, A, -59, -55, "own-gsi", "phase", new { team = "dire", hero = Lion, phase = "playing" })); // invalid phase/clock
            a.Append(Fact(4, A, -60, -56, "own-gsi", "phase", new { phase = "pregame" })); // partial archive shape, not a new perspective
            try {
                a.Append(Fact(4, A, 900, 900, "own-gsi", "phase", new { team = "dire", hero = Lion, phase = "playing" }));
                throw new Exception("Duplicate sequence unexpectedly accepted.");
            } catch (ArgumentException) { }
            check(a.GetLiveResume(A) == (4L, expected), "highest known-at may precede the only verified phase; sparse, invalid and rejected rows do not replace it");
            a.Append(Fact(1, B, 30, 30, "own-gsi", "own-state", new { team = "radiant", hero = Luna }));
            a.Append(Fact(2, B, 32, 32, "own-gsi", "phase", new { team = "radiant", phase = "playing" }));
        }
        using var reopened = new MatchArchive(root);
        var s = new LiveMatchState(reopened, () => Now);
        s.ObserveFrame(Frame(B, 50, 1));
        check(s.ActiveMatch == null && reopened.GetLiveResume(B) == (2L, (LiveResumeFence?)null) && !reopened.IsSealed(B),
            "nonempty legacy journal with no verified phase identity remains reviewable but cannot receive somebody else's perspective");
        s.ObserveFrame(Frame(A, -40, 1, phase: "pregame"));
        check(s.ActiveMatch == null && reopened.GetLiveResume(A) == (4L, expected), "negative pregame high-water survives restart without defaulting to zero or the last row");
        s.ObserveFrame(Frame(A, -29, 1, phase: "pregame"));
        s.ObserveFrame(Frame(A, 0, 2, phase: "postgame"));
        check(s.ActiveMatch == A && reopened.LastSequence(A) == 6 && !reopened.IsSealed(A) &&
            reopened.GetLiveResume(A).Fence == expected with { MaxKnownAt = -29 }, "compatible pregame may resume, but is not same-session playing evidence");
    }

    private static void Unreadable(string root, Action<bool, string> check)
    {
        string log = Path.Combine(Directory.CreateDirectory(Path.Combine(root, A)).FullName, "live.jsonl");
        File.WriteAllText(log, "corrupt committed record\n");
        using var a = new MatchArchive(root);
        var s = new LiveMatchState(a, () => Now);
        s.ObserveFrame(Frame(A, 80, 1));
        check(s.ActiveMatch == A && !s.CanAudit && s.StorageWarning != null,
            "unreadable resume cursor permits fresh in-memory delivery only and latches the archive warning");
        s.ObserveFrame(Frame(A, 81, 2, phase: "postgame")); s.ObserveFrame(Frame(B, 1, 1));
        check(s.ActiveMatch == B && !s.CanAudit && File.ReadAllText(log) == "corrupt committed record\n" &&
            !File.Exists(Path.Combine(root, A, "sealed.json")) && !Directory.Exists(Path.Combine(root, B)),
            "unreadable archive is never repaired or sealed by live-only fallback, which stays in memory on match switch");
    }

    private static void CachedDamage(string root, Action<bool, string> check)
    {
        using var a = new MatchArchive(root);
        a.Append(Fact(1, A, 80, 80, "own-gsi", "phase", new { team = "radiant", hero = Luna, phase = "playing" }));
        var cached = a.GetLiveResume(A);
        File.WriteAllText(Path.Combine(root, A, "live.jsonl"), "{}\n");
        try { a.Read(A); throw new Exception("Damaged committed prefix unexpectedly readable."); } catch (IOException) { }
        bool rejected = false;
        try { a.GetLiveResume(A); } catch (IOException) { rejected = true; }
        check(cached.Fence != null && rejected, "a diagnosed damaged live prefix cannot supply its formerly valid cached resume fence");
    }
}
