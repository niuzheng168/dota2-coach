using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotaCoach.TwoPc;

internal static class MatchArchiveTests
{
    private const string A = "9097559249", B = "8070000001";
    private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
    private static MatchFact Fact(long seq, string id = A) => new(seq, id, 12.25, 18.5, "team-vision", "team-sight",
        Data(new { heroes = new[] { new { hero = "npc_dota_hero_luna", team = "dire", x = 30.5, y = -7 } } }));
    private static bool Reject<T>(Action action) where T : Exception
    {
        try { action(); return false; } catch (T) { return true; }
    }

    public static void Run(Action<bool, string> check)
    {
        var temp = Directory.CreateTempSubdirectory("dota-match-archive-");
        try {
            LiveAndRecovery(Path.Combine(temp.FullName, "matches"), check);
            ReplayImport(temp.FullName, check);
            ThreadingAndFaults(temp.FullName, check);
        } finally { temp.Delete(recursive: true); }
    }

    private static void LiveAndRecovery(string root, Action<bool, string> check)
    {
        string log = Path.Combine(root, A, "live.jsonl"), proof = Path.Combine(root, A, "sealed.json");
        using (var a = new MatchArchive(root)) {
            check(a.Root == Path.GetFullPath(root) && a.LastSequence(A) == 0 && !a.IsSealed(A) &&
                a.List().Count == 0 && a.Read(A).Count == 0 && !Directory.Exists(Path.Combine(root, A)), "empty queries create no match");
            check(Reject<IOException>(() => new MatchArchive(root).Dispose()), "two archive writers cannot reserve the same sequence");
            using (var d = JsonDocument.Parse("""{"phase":"playing","paused":false}"""))
                a.Append(new(3, A, -90.125, -89, "own-gsi", "phase", d.RootElement));
            a.Append(Fact(8));
            a.Append(Fact(9) with { Clock = 3, KnownAt = 22 }); // older evidence, learned later
            a.Append(new(1, B, 20, 20, "coach", "gap", Data(new { reason = "connection-lost" })));
            var read = a.Read(A);
            check(read.Select(f => f.Sequence).SequenceEqual([3L, 8, 9]) &&
                read[0].Data.GetProperty("phase").GetString() == "playing" && read[0].Clock == -90.125 &&
                read[2] is { Clock: 3, KnownAt: 22 } && File.ReadLines(log).Count() == 3,
                "immediate active read owns detached data and preserves the distinct clocks and receipt order");
            check(a.LastSequence(A) == 9 && a.LastSequence(B) == 1 && a.List().All(e => !e.Ended && !e.HasReplay),
                "per-match live cursors and a disconnect is not end proof");
            a.Append(Fact(2, B) with { Data = Data(new { available = false, heroes = Array.Empty<object>() }) });
            a.Append(Fact(3, B) with { Kind = "ward-observed", Data = Data(new { available = false, wards = Array.Empty<object>() }) });
            a.Append(Fact(4, B) with { Source = "own-gsi", Kind = "own-state", Data = Data(new {
                silenced = (bool?)null, muted = false, gold = 0, netWorth = 1800, denies = 2, kills = 0,
                deaths = 1, assists = 3, gpm = (int?)null, xpm = 309
            }) });
            var charges = new { hero = "npc_dota_hero_lion", observer = 2, sentry = 0, unknown = 1, firstReadClock = (double?)null, lastReadClock = 12.25 };
            a.Append(Fact(5, B) with { Kind = "ward-charges", Data = Data(charges) });
            var withdrawn = a.Read(B);
            check(!withdrawn[1].Data.GetProperty("available").GetBoolean() && !withdrawn[2].Data.GetProperty("available").GetBoolean() &&
                withdrawn[2].Kind == "ward-observed" && withdrawn[3].Data.GetProperty("silenced").ValueKind == JsonValueKind.Null &&
                !withdrawn[3].Data.GetProperty("muted").GetBoolean(),
                "explicit sight/ward withdrawals and unknown versus false own-state restrictions roundtrip without inferred events");
            var economy = withdrawn[3].Data;
            check(economy.GetProperty("netWorth").GetInt32() == 1800 && economy.GetProperty("gold").GetInt32() == 0 &&
                economy.GetProperty("gpm").ValueKind == JsonValueKind.Null && economy.GetProperty("xpm").GetInt32() == 309,
                "own economy is a typed projection, with no farming eligibility requirement and no fabricated unknown counters");
            check(withdrawn[4] is { Kind: "ward-charges", Source: "team-vision", KnownAt: 18.5 } charge &&
                charge.Data.GetProperty("firstReadClock").ValueKind == JsonValueKind.Null && charge.Data.GetProperty("lastReadClock").GetDouble() == 12.25 &&
                charge.Data.GetProperty("unknown").GetInt32() == 1 && charge.Data.GetProperty("observer").GetInt32() == 2,
                "directly read ward charges retain their reading times and unknown count, distinct from an inferred ward");
            check(Reject<ArgumentException>(() => a.Append(Fact(9))) && Reject<ArgumentException>(() => a.Append(Fact(7))),
                "duplicate and reversed sequence do not advance the cursor");
            foreach (var bad in new[] {
                Fact(10) with { Clock = double.NaN }, Fact(10) with { KnownAt = double.PositiveInfinity },
                Fact(10) with { KnownAt = 1 }, Fact(10) with { Source = "postgame-replay" }, Fact(10) with { Kind = "replay-hero" },
                Fact(10) with { Source = "inference" }, Fact(10) with { Data = Data(new { pixels = new byte[100] }) },
                Fact(10) with { Data = Data(new { heroes = new[] { new { hero = "axe", auth = new { token = "private" } } } }) },
                Fact(10) with { Kind = "ward-observed", Data = Data(new { wards = new[] { new { kind = "destroyed", team = "dire", x = 0, y = 0 } } }) },
                Fact(10) with { Source = "coach", Kind = "gap", Data = Data(new { reason = "Bearer private-credential" }) },
                Fact(10) with { Data = JsonElement.Parse("""{"heroes":[],"heroes":[]}""") },
                Fact(10) with { Source = "own-gsi", Kind = "own-state", Data = Data(new { gold = "10" }) },
                Fact(10) with { Source = "own-gsi", Kind = "own-state", Data = Data(new { gpm = 2147483648L }) },
                Fact(10) with { Source = "own-gsi", Kind = "own-state", Data = Data(new { player = new { gold = 10 } }) },
                Fact(10) with { Source = "inference", Kind = "ward-charges", Data = Data(charges) },
                Fact(10) with { Kind = "ward-charges", Data = Data(new { charges.hero, charges.observer, charges.sentry, charges.unknown,
                    charges.firstReadClock, charges.lastReadClock, key = "not-to-be-saved" }) },
                Fact(10) with { Data = Data(new { hero = new string('X', MatchArchive.MaxLineBytes) }) },
            }) check(Reject<ArgumentException>(() => a.Append(bad)), "noncanonical, oversized or private live fact rejected");
            foreach (string id in new[] { "", "0", "01", "../4", @"4\..\5", "4:5", "４", "18446744073709551616" })
                check(Reject<ArgumentException>(() => a.LastSequence(id)), "unsafe/noncanonical match ID rejected: " + id);
            check(Reject<ArgumentOutOfRangeException>(() => a.Seal(A, double.NaN)) && !a.IsSealed(A), "invalid end proof cannot freeze a live match");
            // This is a real pending write at close; there is intentionally NO intervening read/barrier.
            a.Append(Fact(10));
        }
        byte[] prefix = File.ReadAllBytes(log);
        File.AppendAllText(log, """{"sequence":11,"data":{"hero":"torn""");
        long torn = new FileInfo(log).Length;
        string old = Path.Combine(root, A, ".old-export.tmp");
        File.WriteAllText(old, "not ours");
        using (var a = new MatchArchive(root)) {
            check(a.LastSequence(A) == 10 && !a.IsSealed(A) && a.Read(A).Count == 4 &&
                new FileInfo(log).Length == torn, "restart sees the flushed prefix; read alone never rewrites the torn tail");
            a.Append(Fact(11));
            a.Seal(A, 100);
            byte[] original = File.ReadAllBytes(proof);
            a.Seal(A, 999);
            using var manifest = JsonDocument.Parse(original);
            check(manifest.RootElement.GetProperty("clock").GetDouble() == 100 &&
                manifest.RootElement.GetProperty("liveBytes").GetInt64() == new FileInfo(log).Length &&
                File.ReadAllBytes(proof).SequenceEqual(original) && File.ReadAllBytes(log).AsSpan().StartsWith(prefix) &&
                !File.ReadAllText(log).Contains("torn") && File.ReadAllText(old) == "not ours",
                "repair changes only the uncommitted suffix and the first durable seal is immutable");
            check(a.IsSealed(A) && Reject<InvalidOperationException>(() => a.Append(Fact(12))) && a.Read(A, true).Count == 5,
                "seal waits for pending facts; a sealed match needs no replay to be reviewed");
        }
        using (var a = new MatchArchive(root))
            check(a.IsSealed(A) && a.LastSequence(A) == 11 && !a.IsSealed(B) && Reject<InvalidOperationException>(() => a.Append(Fact(12))),
                "durable end proof and immutable live cursor survive restart");
        File.WriteAllText(proof, File.ReadAllText(proof).Replace("\"lastSequence\":11", "\"lastSequence\":10"));
        using (var a = new MatchArchive(root))
            check(Reject<IOException>(() => a.IsSealed(A)) && Reject<IOException>(() => a.Append(Fact(12))) && a.LastSequence(B) == 5,
                "a corrupt manifest cannot authorize an import or reopen the live history; other matches still work");
        string damaged = Path.Combine(root, B, "live.jsonl");
        File.AppendAllText(damaged, "corrupt but LF-terminated\n");
        long length = new FileInfo(damaged).Length;
        string over = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "33")).FullName, "live.jsonl");
        using (var huge = File.Create(over)) huge.SetLength(MatchArchive.MaxLiveBytes + 1L);
        using (var a = new MatchArchive(root))
            check(Reject<IOException>(() => a.Read(B)) && Reject<IOException>(() => a.Append(Fact(12, B))) &&
                new FileInfo(damaged).Length == length && Reject<IOException>(() => a.LastSequence("33")),
                "neither committed corruption nor an oversized log is mistaken for a repairable torn suffix");
    }

    // Normalized worker v1, including explicit unknown slots and sparse coverage counters.
    private static JsonObject Worker() => new() {
        ["schemaVersion"] = 1, ["matchId"] = A, ["complete"] = true, ["source"] = "postgame-replay",
        ["replaySha256"] = new string('a', 64), ["events"] = JsonNode.Parse("""
        [
          {"clock":-10,"kind":"replay-hero","data":{"tick":1,"playerId":2,"hero":null,"team":null,"x":null,"y":null,"level":null,"hp":null,"maxHp":null,"alive":null,"items":["empty",null,"item_blink"],"terminal":false}},
          {"clock":2,"kind":"replay-ward","data":{"tick":100,"wardId":"1:2:100","action":"placed","evidence":"first-observed-alive","type":"observer","team":3,"ownerPlayerId":null,"x":-100,"y":20,"visibleRadiant":false,"visibleDire":null}},
          {"clock":3,"kind":"replay-kill","data":{"tick":110,"target":"npc_dota_hero_luna","attacker":null,"damageSource":null,"attackerTeam":2,"targetTeam":3}},
          {"clock":4,"kind":"replay-objective","data":{"tick":120,"type":"roshan","target":"npc_dota_roshan","attacker":null,"damageSource":null,"attackerTeam":3,"targetTeam":4}},
          {"clock":4,"kind":"replay-objective","data":{"tick":120,"type":"aegis-pickup","playerId":5}},
          {"clock":90,"kind":"replay-end","data":{"tick":2700,"winnerTeam":2,"gameBuild":null,"parser":"gem-dota/0.14.0","hornTick":42,"playbackTicks":2860,"playbackFrames":1000,"pauseIntervals":0,
             "clockBasis":"horn-relative","coverage":{"hero":{"sampleSeconds":5,"frames":2,"knownRows":{"selected":5},"basis":"selected handles"},"ward":{"observed":{"placed":1},"openAtEnd":1,"basis":"entity lifecycle"},
             "kill":{"excluded":{},"basis":"confirmed DEATH"},"objective":{"basis":"typed event"},"observedEvents":{"replay-hero":1,"replay-ward":1,"replay-objective":2,"replay-kill":1}}}}
        ]
        """)
    };

    private static void ReplayImport(string temp, Action<bool, string> check)
    {
        string root = Path.Combine(temp, "review"), file = Path.Combine(temp, "worker.json");
        string dest = Path.Combine(root, A, "replay.json"), log = Path.Combine(root, A, "live.jsonl");
        using var a = new MatchArchive(root);
        a.Append(Fact(41)); a.Append(Fact(1, B));
        a.Read(A); File.WriteAllText(dest, Worker().ToJsonString());
        check(Reject<InvalidOperationException>(() => a.Read(A, true)) &&
            Reject<InvalidOperationException>(() => a.ImportReplay(A, Path.Combine(temp, "missing.json"))) &&
            Reject<InvalidOperationException>(() => a.Read("999", true)) &&
            !a.List().Single(e => e.MatchId == A).HasReplay && a.Read(A).All(f => f.Source != "postgame-replay"),
            "active/unknown matches refuse replay before opening input, even with a manually planted sidecar");
        File.Delete(dest);
        a.Seal(A, 100);
        byte[] live = File.ReadAllBytes(log);
        string valid = Worker().ToJsonString();
        File.WriteAllText(file, valid, new UTF8Encoding(true));
        a.ImportReplay(A, file);
        var combined = a.Read(A, true);
        check(a.List().Single(e => e.MatchId == A) is { Ended: true, HasReplay: true } && a.Read(A).Count == 1 &&
            combined.Count == 7 && combined.Skip(1).All(f => f.Source == "postgame-replay" && f.KnownAt == 100) &&
            combined.Select(f => f.Sequence).SequenceEqual([41L, 42, 43, 44, 45, 46, 47]) &&
            combined[1].Data.GetProperty("items")[1].ValueKind == JsonValueKind.Null && a.LastSequence(A) == 41,
            "all five worker kinds remain offline-only, with stable distinct sequences, postgame knowledge time and nullable slots");
        var replacement = Worker(); replacement["events"]![1]!["data"]!["x"] = -120; replacement["events"]![5]!["clock"] = 105;
        File.WriteAllText(file, replacement.ToJsonString()); a.ImportReplay(A, file); a.ImportReplay(A, file);
        byte[] good = File.ReadAllBytes(dest);
        check(a.Read(A, true) is { Count: 7 } newer && newer[2].Data.GetProperty("x").GetInt32() == -120 &&
            newer[1].KnownAt == 105 && File.ReadAllBytes(log).SequenceEqual(live), "successful reimport replaces only the review sidecar");

        foreach (var (name, mutate) in new (string, Action<JsonObject>)[] {
            ("version", j => j["schemaVersion"] = 2), ("integer ID", j => j["matchId"] = 9097559249L),
            ("wrong match", j => j["matchId"] = B), ("completion", j => j["complete"] = false),
            ("source", j => j["source"] = "team-vision"), ("bad hash", j => j["replaySha256"] = new string('z', 64)),
            ("short hash", j => j["replaySha256"] = "1234"), ("extra header", j => j["raw"] = true),
            ("extra event key", j => j["events"]![0]!["sequence"] = 9), ("live kind", j => j["events"]![0]!["kind"] = "own-state"),
            ("null event data", j => j["events"]![0]!["data"] = null), ("sparse end without result", j => j["events"]![5]!["data"] = new JsonObject()),
            ("duplicate terminal end", j => j["events"]![0] = j["events"]![5]!.DeepClone()),
            ("missing terminal end", j => j["events"]!.AsArray().RemoveAt(5)), ("time reversal", j => j["events"]![4]!["clock"] = 1),
            ("nested credential", j => j["events"]![5]!["data"]!["coverage"]!["hero"]!["apiKey"] = "private"),
            ("invalid enum", j => j["events"]![5]!["data"]!["winnerTeam"] = 99),
            ("postgame beyond recorded ticks", j => j["events"]![5]!["data"]!["playbackTicks"] = 2),
            ("reversed tick", j => j["events"]![3]!["data"]!["tick"] = 1),
            ("wrong scalar type", j => j["events"]![1]!["data"]!["x"] = "100"),
            ("too many slots", j => j["events"]![0]!["data"]!["items"] = new JsonArray(new JsonNode?[33])),
        }) {
            var bad = Worker(); mutate(bad); File.WriteAllText(file, bad.ToJsonString());
            check(Reject<InvalidDataException>(() => a.ImportReplay(A, file)) && File.ReadAllBytes(dest).SequenceEqual(good), "bad replacement keeps old replay: " + name);
        }
        foreach (string bad in new[] { valid + "{}", valid.Replace("\"complete\":true", "\"complete\":true,\"complete\":true"),
            valid.Replace("\"x\":-100", "\"x\":1e999"), valid.Replace("\"clock\":2,", "\"clock\":1e999,"),
            valid.Replace("\"placed\":1", "\"placed\":1,\"placed\":1"), """{"schemaVersion":1""",
            valid[..valid.IndexOf("[", StringComparison.Ordinal)] + "[" + string.Join(",", Enumerable.Repeat("{}", MatchArchive.MaxReplayEvents + 1)) + "]}" }) {
            File.WriteAllText(file, bad);
            check(Reject<InvalidDataException>(() => a.ImportReplay(A, file)), "malformed, ambiguous, nonfinite or over-count worker file rejected");
        }
        using (var large = File.Create(file)) large.SetLength(MatchArchive.MaxReplayBytes + 1L);
        check(Reject<IOException>(() => a.ImportReplay(A, file)) && File.ReadAllBytes(dest).SequenceEqual(good), "byte cap before parse, prior import intact");
        if (OperatingSystem.IsWindows()) {
            File.WriteAllText(file, valid);
            using var locked = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read);
            bool denied = false;
            try { a.ImportReplay(A, file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { denied = true; }
            check(denied && File.ReadAllBytes(dest).SequenceEqual(good) &&
                !Directory.EnumerateFiles(Path.GetDirectoryName(dest)!, "*.tmp").Any(), "failed atomic rename never deletes the last good review or leaks staging files");
        }
        a.Dispose();
        using var reopened = new MatchArchive(root);
        check(reopened.Read(A, true) is { Count: 7 } r && r[^1].Sequence == 47 && reopened.Read(A).Count == 1 &&
            reopened.Read(B).Count == 1 && !reopened.IsSealed(B), "postgame suffix is stable after restart and isolated between matches");
        File.WriteAllText(dest, """{"broken":true}""");
        check(Reject<InvalidDataException>(() => reopened.Read(A, true)) && reopened.Read(A).Count == 1,
            "read revalidates a changed review file and the live view never opens it");
    }

    private static void ThreadingAndFaults(string temp, Action<bool, string> check)
    {
        string root = Path.Combine(temp, "threads");
        using (var a = new MatchArchive(root)) {
            int wins = 0;
            Parallel.For(0, 20, _ => { try { a.Append(Fact(1)); Interlocked.Increment(ref wins); } catch (ArgumentException) { } });
            check(wins == 1 && a.LastSequence(A) == 1, "concurrent duplicate producers reserve the sequence once");
            var producers = new[] { A, B, "44" }.Select(id => Task.Run(() => {
                for (int i = 2; i < 75; i++) a.Append(Fact(i, id));
            })).ToArray();
            var reads = Enumerable.Range(0, 10).Select(_ => a.Read(A)).ToArray();
            Task.WaitAll(producers);
            check(reads.All(rows => rows.Select(f => f.Sequence).SequenceEqual(Enumerable.Range(1, rows.Count).Select(x => (long)x))) &&
                a.Read(A).Count == 74 && a.Read(B).Count == 73 && a.LastSequence("44") == 74,
                "concurrent unsealed reads see a complete prefix while independent producers advance");
            var sample = Fact(1, "55");
            for (int i = 1; i <= 1800; i++) {
                try { a.Append(sample with { Sequence = i }); }
                catch (IOException) {
                    // The bounded producer may reject pressure, never silently drop a fact or burn its sequence.
                    if (a.LastSequence("55") != i - 1) throw;
                    a.Append(sample with { Sequence = i });
                }
            }
            check(a.Read("55").Select(f => f.Sequence).SequenceEqual(Enumerable.Range(1, 1800).Select(i => (long)i)),
                "burst beyond queue capacity preserves all acknowledged facts across multi-buffer line boundaries");
        }
        var failed = new MatchArchive(Path.Combine(temp, "io-fault"));
        File.WriteAllText(Path.Combine(failed.Root, A), "pre-existing unrelated file"); // cannot create match directory
        failed.Append(Fact(1));
        check(Reject<IOException>(() => failed.Read(A)) && Reject<IOException>(() => failed.Dispose()) &&
            File.ReadAllText(Path.Combine(failed.Root, A)) == "pre-existing unrelated file", "an async writer fault wakes barriers and close reports it");
        failed.Dispose();
    }
}
