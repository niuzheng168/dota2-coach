using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DotaCoach.TwoPc;

/// <summary>Clock is the observation time; KnownAt is when the coach could first know it (game seconds).</summary>
public sealed record MatchFact(long Sequence, string MatchId, double Clock, double KnownAt, string Source, string Kind, JsonElement Data);
public sealed record MatchArchiveEntry(string MatchId, bool Ended, bool HasReplay, bool Damaged = false);
/// <summary>Admission constraints only. Not a current participant, an observation, or evidence to seal a match.</summary>
public readonly record struct LiveResumeFence(string Team, string Hero, double MaxKnownAt, bool WasPlaying);

/// <summary>
/// One local writer per root. After the initial restart lookup, Append queues an owned record without waiting
/// for disk; a full queue rejects it without consuming its sequence. Read/GetLiveResume/LastSequence, Seal and Dispose are durability barriers.
/// Only Seal (the caller's explicit participant-side end proof) authorizes the separate postgame sidecar.
/// </summary>
public sealed class MatchArchive : IDisposable
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotaCoach", "MatchArchive");
    public string Root { get; }
    public const int MaxReplayBytes = 32 * 1024 * 1024, MaxReplayEvents = 100_000;
    public const int MaxLiveBytes = 128 * 1024 * 1024, MaxLineBytes = 64 * 1024; // includes LF
    private const string Live = "live.jsonl", Manifest = "sealed.json", Replay = "replay.json", ReplaySource = "postgame-replay";
    private static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 12, AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    private static readonly JsonDocumentOptions Strict = new() { MaxDepth = 12, AllowDuplicateProperties = false };
    private sealed record SealProof(int SchemaVersion, string MatchId, bool Ended, double Clock, long LastSequence, long LiveBytes);
    private readonly record struct ResumeCursor(string? Team, string? Hero, double? MaxKnownAt, bool WasPlaying)
    {
        // Fold the identical validated, LF-committed live prefix on recovery and on successful reservation.
        // Every known-at counts, even if a late sighting's observation clock or the next row is older.
        public ResumeCursor Advance(MatchFact fact)
        {
            var next = this with { MaxKnownAt = MaxKnownAt is double prior ? Math.Max(prior, fact.KnownAt) : fact.KnownAt };
            if (fact is not { Source: "own-gsi", Kind: "phase" }) return next;
            string? Field(string name) => fact.Data.TryGetProperty(name, out var v) ? v.GetString() : null;
            var team = Field("team"); var hero = Field("hero"); var phase = Field("phase");
            if (team is not ("radiant" or "dire") || !MatchWitness.HeroOk(hero) || !MatchWitness.PhaseClockOk(phase, fact.Clock)) return next;
            return next with { Team = team, Hero = hero, WasPlaying = WasPlaying || phase == "playing" };
        }
        public LiveResumeFence? Fence => Team != null && Hero != null && MaxKnownAt is double at
            ? new LiveResumeFence(Team, Hero, at, WasPlaying) : null;
    }
    private sealed class Cursor(string id, string dir)
    {
        public readonly string Id = id, Dir = dir;
        public long Sequence, Bytes, Torn, Ticket;
        public ResumeCursor Resume;
        public bool Sealing;
        public double? End;
        public volatile bool Damaged; // in memory only; never repair or rewrite a historical record while reading it
    }
    private sealed record Line(Cursor Match, long Ticket, long Offset, long OldLength, byte[] Json);
    private readonly Dictionary<string, Lazy<Cursor>> matches = new(StringComparer.Ordinal);
    // gate protects reservations/watermarks only; slow review I/O never holds it. admin orders seal/import/close.
    private readonly object gate = new(), admin = new();
    private readonly BlockingCollection<Line> queue = new(256);
    private readonly FileStream lease;
    private readonly Task writer;
    private long issued, durable;
    private bool closed;
    private IOException? failure;

    public MatchArchive(string root)
    {
        Root = LocalPath(root);
        Exists(Root);
        Directory.CreateDirectory(Root);
        string lockFile = Path.Combine(Root, ".writer.lock");
        Exists(lockFile);
        // A stale filename after a crash is harmless: ownership is the open OS handle, not file existence.
        lease = new(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try {
            foreach (var dir in Directory.EnumerateDirectories(Root)) {
                string id = Path.GetFileName(dir);
                if (ValidId(id)) matches.Add(id, new(() => Recover(id, dir)));
            }
            writer = Task.Factory.StartNew(WriteLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        } catch { lease.Dispose(); throw; }
    }

    private static bool ValidId(string? id) => id is { Length: > 0 and <= 20 } && id[0] != '0' &&
        id.All(char.IsAsciiDigit) && ulong.TryParse(id, out _);
    private static string LocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && (full.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network))
            throw new ArgumentException("Match archives require a local path, not a network or device path.", nameof(path));
        return full;
    }
    // Do not follow a junction/symlink when reading, appending or repairing a user-selected archive.
    private static bool Exists(string path)
    {
        try {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Archive path is a link.");
            return true;
        } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
    }
    private void Ready()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (failure != null) throw new IOException("The archive writer failed; reopen to recover the durable prefix.", failure);
    }
    private Cursor? Get(string id, bool create = false)
    {
        if (!ValidId(id)) throw new ArgumentException("Expected a nonzero canonical decimal match ID.", nameof(id));
        Lazy<Cursor>? lazy;
        lock (gate) {
            Ready();
            if (!matches.TryGetValue(id, out lazy) && create)
                matches.Add(id, lazy = new(() => new(id, Path.Combine(Root, id))));
        }
        return lazy?.Value; // Only a pre-existing match needs a one-time disk scan, outside the producer lock.
    }
    private static Cursor Recover(string id, string dir)
    {
        var c = new Cursor(id, dir);
        (c.Sequence, c.Bytes, c.Torn, c.Resume) = Scan(c, null, null);
        string path = Path.Combine(dir, Manifest);
        if (Exists(path)) {
            var proof = Decode<SealProof>(ReadBytes(path, 4096));
            if (proof.SchemaVersion != 1 || proof.MatchId != id || !proof.Ended || !double.IsFinite(proof.Clock) ||
                proof.LastSequence != c.Sequence || proof.LiveBytes != c.Bytes || c.Torn != 0)
                throw new IOException("Seal does not match the committed live log.");
            c.End = proof.Clock;
        }
        return c;
    }

    public void Append(MatchFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!ValidLive(fact)) throw new ArgumentException("Invalid or non-projected live fact.", nameof(fact));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(fact, Json); // detaches from the producer's JsonDocument
        if (bytes.Length >= MaxLineBytes) throw new ArgumentException("Live fact exceeds the line limit.", nameof(fact));
        var c = Get(fact.MatchId, true)!;
        lock (gate) {
            Ready(); RequireIntact(c);
            if (c.End.HasValue || c.Sealing) throw new InvalidOperationException("Sealed matches are immutable.");
            if (fact.Sequence <= c.Sequence) throw new ArgumentException("Sequence must advance within this match.", nameof(fact));
            if (c.Bytes + bytes.Length + 1 > MaxLiveBytes) throw new IOException("Live match size limit reached.");
            var resume = c.Resume.Advance(fact);
            var line = new Line(c, issued + 1, c.Bytes, c.Bytes + c.Torn, bytes);
            if (!queue.TryAdd(line)) throw new IOException("Archive queue is full; this sequence was not accepted.");
            c.Ticket = ++issued; c.Sequence = fact.Sequence; c.Bytes += bytes.Length + 1; c.Torn = 0; c.Resume = resume;
        }
    }

    private static FileStream OpenPrefix(Cursor c, long offset, long oldLength)
    {
        Exists(c.Dir);
        Directory.CreateDirectory(c.Dir);
        if (Exists(Path.Combine(c.Dir, Manifest))) throw new IOException("Cannot alter a sealed live log.");
        string path = Path.Combine(c.Dir, Live);
        Exists(path);
        var s = new FileStream(path, oldLength == 0 ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.Write, FileShare.Read);
        try {
            if (s.Length != oldLength) throw new IOException("Live log changed outside the archive.");
            if (oldLength != offset) s.SetLength(offset); // only the previously identified, unterminated suffix
            s.Position = offset;
            return s;
        } catch { s.Dispose(); throw; }
    }
    private void WriteLoop()
    {
        var batch = new List<Line>(64);
        try {
            foreach (var first in queue.GetConsumingEnumerable()) {
                batch.Add(first);
                while (batch.Count < 64 && queue.TryTake(out var next)) batch.Add(next);
                foreach (var group in batch.GroupBy(l => l.Match)) {
                    var start = group.First();
                    using var s = OpenPrefix(group.Key, start.Offset, start.OldLength);
                    foreach (var line in group) { s.Write(line.Json); s.WriteByte((byte)'\n'); }
                    s.Flush(flushToDisk: true);
                }
                lock (gate) { durable = batch[^1].Ticket; Monitor.PulseAll(gate); }
                batch.Clear();
            }
        } catch (Exception e) {
            lock (gate) {
                failure = new IOException("Could not persist live match facts.", e);
                queue.CompleteAdding();
                while (queue.TryTake(out _)) { }
                Monitor.PulseAll(gate);
            }
        }
    }
    private void Wait(long ticket) // under gate; releases it while waiting for fsync
    {
        Ready();
        while (durable < ticket) { Monitor.Wait(gate); Ready(); }
    }

    /// <summary>Same durable live prefix for both values; never opens a replay or returns a fact.
    /// A nonempty legacy log without a verified own phase has no usable perspective fence.</summary>
    public (long Sequence, LiveResumeFence? Fence) GetLiveResume(string matchId)
    {
        var c = Get(matchId);
        lock (gate) {
            var result = (c?.Sequence ?? 0, c?.Resume.Fence);
            Wait(c?.Ticket ?? 0); // Snapshot BEFORE releasing the gate to wait for this reservation.
            RequireIntact(c); // A live prefix already diagnosed as damaged is not resume evidence.
            return result;
        }
    }
    public long LastSequence(string matchId)
    {
        var c = Get(matchId);
        lock (gate) { long seq = c?.Sequence ?? 0; Wait(c?.Ticket ?? 0); return seq; }
    }
    public bool IsSealed(string matchId)
    {
        var c = Get(matchId);
        lock (gate) { Ready(); RequireIntact(c); return c?.End != null; }
    }

    public void Seal(string matchId, double clock)
    {
        if (!double.IsFinite(clock)) throw new ArgumentOutOfRangeException(nameof(clock));
        lock (admin) {
            var c = Get(matchId, true)!;
            RequireIntact(c);
            if (c.End.HasValue) return; // the first proof wins
            lock (gate) { c.Sealing = true; }
            try {
                lock (gate) Wait(c.Ticket);
                using (var s = OpenPrefix(c, c.Bytes, c.Bytes + c.Torn)) { c.Torn = 0; s.Flush(true); }
                var proof = new SealProof(1, matchId, true, clock, c.Sequence, c.Bytes);
                AtomicWrite(c.Dir, Manifest, JsonSerializer.SerializeToUtf8Bytes(proof, Json), replace: false);
                lock (gate) c.End = clock;
            } finally { lock (gate) c.Sealing = false; }
        }
    }

    public IReadOnlyList<MatchArchiveEntry> List()
    {
        lock (admin) {
            KeyValuePair<string, Lazy<Cursor>>[] snapshot;
            lock (gate) { snapshot = [.. matches]; Wait(issued); }
            var entries = new List<MatchArchiveEntry>();
            foreach (var (id, lazy) in snapshot) {
                var unavailable = new MatchArchiveEntry(id, false, false, Damaged: true);
                Cursor c;
                try { c = lazy.Value; } // Recover validates this ID's live log and seal, not any other match.
                catch (Exception e) when (EntryReadFailure(e)) { entries.Add(unavailable); continue; }
                // Wait/Ready errors belong to the writer/store, NOT to this directory. Do not hide them.
                lock (gate) { if (c.Sequence == 0 && c.End == null) continue; Wait(c.Ticket); }
                if (c.Damaged) { entries.Add(unavailable); continue; }
                try {
                    if (!Exists(c.Dir)) throw new IOException("Match directory is missing.");
                    entries.Add(new(id, c.End.HasValue, c.End.HasValue && ReplayPresent(c)));
                } catch (Exception e) when (EntryReadFailure(e)) { c.Damaged = true; entries.Add(unavailable); }
            }
            return entries.OrderByDescending(e => e.MatchId.Length).ThenByDescending(e => e.MatchId, StringComparer.Ordinal).ToArray();
        }
    }

    private static bool EntryReadFailure(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException;
    private static void RequireIntact(Cursor? c)
    {
        if (c?.Damaged == true) throw new IOException("This match archive is damaged or unreadable. No replay access or replacement.");
    }
    // Catalog inspection is metadata-only; do not parse all-map evidence as a side effect of listing matches.
    private static bool ReplayPresent(Cursor c)
    {
        string path = Path.Combine(c.Dir, Replay);
        if (!Exists(path)) return false;
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.Directory) != 0 || info.Length is <= 0 or > MaxReplayBytes)
            throw new InvalidDataException("Invalid stored replay file.");
        return true;
    }

    public IReadOnlyList<MatchFact> Read(string matchId, bool includeReplay = false)
    {
        lock (admin) {
            var c = Get(matchId);
            if (includeReplay && c?.End == null) throw new InvalidOperationException("Replay requires a sealed match.");
            if (includeReplay) RequireIntact(c);
            if (c == null) return [];
            long bytes, seq;
            lock (gate) { bytes = c.Bytes; seq = c.Sequence; Wait(c.Ticket); }
            var facts = new List<MatchFact>();
            try {
                if (Scan(c, bytes, facts).Sequence != seq) throw new IOException("Live sequence changed outside the archive.");
                if (includeReplay && ReplayPresent(c)) ValidateReplay(ReadBytes(Path.Combine(c.Dir, Replay), MaxReplayBytes), c, facts);
            } catch (Exception e) when (EntryReadFailure(e)) { c.Damaged = true; throw; }
            return facts;
        }
    }

    // LF is the commit marker. Bound the file AND the individual line before parsing. A short final line can
    // be a crash-torn write; do not rewrite it on a read, or silently skip corruption in a committed line.
    private static (long Sequence, long Bytes, long Torn, ResumeCursor Resume) Scan(Cursor c, long? prefix, List<MatchFact>? into)
    {
        Exists(c.Dir);
        string path = Path.Combine(c.Dir, Live);
        if (!Exists(path)) return prefix is null or 0 ? default : throw new IOException("Live log is missing.");
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long remaining = prefix ?? s.Length, bytes = 0, seq = 0;
        ResumeCursor resume = default;
        if (s.Length > MaxLiveBytes || remaining > s.Length) throw new IOException("Invalid live log size.");
        var buffer = new byte[MaxLineBytes];
        int used = 0;
        while (remaining > 0) {
            int n = s.Read(buffer, used, (int)Math.Min(buffer.Length - used, remaining));
            if (n == 0) throw new IOException("Live log was shortened.");
            remaining -= n;
            int start = 0, end = used + n;
            int lf;
            while ((lf = Array.IndexOf(buffer, (byte)'\n', start, end - start)) >= 0) {
                var fact = DecodeFact(buffer.AsSpan(start, lf - start), c.Id, seq);
                seq = fact.Sequence; bytes += lf - start + 1;
                if (into == null) resume = resume.Advance(fact); else into.Add(fact);
                start = lf + 1;
            }
            used = end - start;
            if (used == buffer.Length) throw new IOException("Oversized live line.");
            if (start != 0 && used != 0) Buffer.BlockCopy(buffer, start, buffer, 0, used);
        }
        if (prefix != null && used != 0) throw new IOException("Expected a committed live prefix.");
        return (seq, bytes, used, resume);
    }

    private static MatchFact DecodeFact(ReadOnlySpan<byte> line, string id, long previous)
    {
        // JsonElement can defer decoding a bad UTF-8 string / unpaired escaped surrogate until a shape
        // checks its value. Normalize only the untrusted row decode, never a writer/lock/lifecycle error.
        try {
            var f = Decode<MatchFact>(line);
            if (f.MatchId != id || !ValidLive(f) || f.Sequence <= previous) throw new IOException("Invalid committed live fact.");
            return f;
        } catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException and not ObjectDisposedException) {
            throw new IOException("Malformed committed live fact.", e);
        }
    }

    public void ImportReplay(string matchId, string file)
    {
        lock (admin) {
            var c = Get(matchId);
            if (c?.End == null) throw new InvalidOperationException("Replay requires a sealed match.");
            RequireIntact(c);
            // Read ONCE. Never validate a path and then copy from it after its contents could have changed.
            string local = LocalPath(file);
            Exists(local);
            byte[] bytes = ReadBytes(local, MaxReplayBytes);
            ValidateReplay(bytes, c, null);
            // A rejected candidate says nothing about the existing archive. A damaged existing sidecar, however,
            // must not be silently 'repaired' by replacement, even before someone has explicitly viewed it.
            try {
                if (!Exists(c.Dir)) throw new IOException("Match directory is missing.");
                if (ReplayPresent(c)) ValidateReplay(ReadBytes(Path.Combine(c.Dir, Replay), MaxReplayBytes), c, null);
            } catch (Exception e) when (EntryReadFailure(e)) { c.Damaged = true; throw; }
            AtomicWrite(c.Dir, Replay, bytes, replace: true);
        }
    }
    private static byte[] ReadBytes(string path, int max)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long size = s.Length;
        if (size <= 0 || size > max) throw new IOException("Archive input is empty or oversized.");
        byte[] bytes = new byte[(int)size]; s.ReadExactly(bytes);
        if (s.ReadByte() != -1) throw new IOException("Archive input changed during the read.");
        return bytes;
    }
    private static T Decode<T>(ReadOnlySpan<byte> bytes) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new IOException("Null archive record."); }
        catch (JsonException e) { throw new IOException("Invalid archive record.", e); }
    }
    private void AtomicWrite(string dir, string name, byte[] bytes, bool replace)
    {
        Exists(dir);
        string dest = Path.Combine(dir, name), temp = Path.Combine(dir, $".{name}.{Guid.NewGuid():N}.tmp");
        Exists(dest);
        var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try {
            using (s) { s.Write(bytes); s.Flush(true); }
            lock (gate) Ready(); // A writer fault during validation/staging must not publish the staged result.
            File.Move(temp, dest, overwrite: replace);
        } finally { File.Delete(temp); }
    }

    // Allowlisted projections, not arbitrary JSON under a trusted-looking kind. "Partial" allows unknown
    // fields to be absent, NOT additional field names. No generic blobs, credentials, GSI sections or pixels.
    private delegate bool Field(JsonElement e);
    private static Field Shape(bool all, params (string Names, Field Test)[] groups)
    {
        var fields = new Dictionary<string, (ulong Bit, Field Test)>(StringComparer.Ordinal);
        ulong required = 0;
        foreach (var (names, test) in groups) foreach (var name in names.Split(' ')) {
            if (fields.Count == 64) throw new InvalidOperationException("Projection exceeds the field mask.");
            ulong bit = 1UL << fields.Count;
            bool optional = name.EndsWith('?'); // in an Exact shape: optional to omit, still checked when present
            fields.Add(optional ? name[..^1] : name, (bit, test));
            if (!optional) required |= bit;
        }
        return e => {
            if (e.ValueKind != JsonValueKind.Object || e.GetPropertyCount() > fields.Count) return false;
            ulong seen = 0;
            foreach (var p in e.EnumerateObject()) {
                if (!fields.TryGetValue(p.Name, out var f) || (seen & f.Bit) != 0 || !f.Test(p.Value)) return false;
                seen |= f.Bit;
            }
            return !all || (seen & required) == required;
        };
    }
    private static Field Partial(params (string, Field)[] fields) => Shape(false, fields);
    private static Field Exact(params (string, Field)[] fields) => Shape(true, fields);
    private static Field Maybe(Field f) => e => e.ValueKind == JsonValueKind.Null || f(e);
    private static Field OneOf(params string[] values) => e => e.ValueKind == JsonValueKind.String && values.Contains(e.GetString(), StringComparer.Ordinal);
    private static Field ArrayOf(int max, Field f, int min = 0) => e => e.ValueKind == JsonValueKind.Array &&
        e.GetArrayLength() >= min && e.GetArrayLength() <= max && e.EnumerateArray().All(f.Invoke);
    private static bool Boolean(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool Number(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double n) && double.IsFinite(n);
    private static bool Integer(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out _);
    private static bool Int32(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _);
    private static bool Count(JsonElement e) => Integer(e) && e.GetInt64() >= 0;
    private static bool TeamId(JsonElement e) => Integer(e) && e.GetInt64() is 2 or 3;
    private static bool PlayerId(JsonElement e) => Integer(e) && e.GetInt64() is >= 0 and < 10;
    private static readonly Regex PrivateText = new(
        @"DCP1\.|\bsk-[\w-]+|\bBearer\s+|(?:api[_-]?key|password|token|secret|foundry_key)\s*[:=]|data:\w+/|[a-f0-9]{32,}|[a-z0-9+/=]{64,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static bool Text(JsonElement e) => e.ValueKind == JsonValueKind.String && e.GetString() is { Length: <= 512 } s &&
        !s.Any(c => char.IsControl(c) || c is '{' or '}') && !PrivateText.IsMatch(s);
    private static bool Token(JsonElement e) => Text(e) && e.GetString() is { Length: > 0 and <= 100 } s &&
        s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static readonly Field StateEntry = Partial(("name slot", Maybe(Token)), ("cooldown", Maybe(Number)), ("level", Maybe(Integer)));
    private static readonly Field Equipment = Partial(("hero neutral", Maybe(Token)), ("level", Maybe(Integer)),
        ("age", Maybe(Number)), ("items", Maybe(ArrayOf(40, Maybe(Token)))));
    private static readonly Field Side = OneOf("radiant", "dire");
    private static readonly Field VisibleHero = Exact(("hero", Token), ("team", Side), ("x y", Number));
    private static readonly Field SeenWard = Exact(("kind", OneOf("observer", "sentry")), ("team", Side), ("x y", Number));
    private static readonly Field Estimate = Partial(("hero kind", Token), ("x y share age left", Number), ("rank", Integer), ("rough", Boolean));
    private static readonly Field Stop = Partial(("id label kind costBasis", Text), ("x y", Number),
        ("arrivalSeconds finishSeconds walkUnits modeledRosters catalogRosters", Integer), ("packWindowSeconds", Maybe(Integer)));

    // Foundry deployment aliases are operator-chosen. Even a syntactically valid alias can be a key,
    // OAuth token or credential filename. Omit an ambiguous name without losing the request audit.
    private static readonly Regex PrivateDeployment = new(
        @"(?:^|[._-])(?:sk[-_]|gh[pousr]_|github_pat_|eyJ|key(?:[._-]|$))" +
        @"|api[._-]?key|auth|bearer|credential|token|secret|password|[a-z0-9]{32,}" +
        @"|\.env(?:\.|\z)|\.(?:json|pem|key|txt|azure\.com)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static (string? ProviderId, string? ModelId) MinuteTarget(string? providerId, string? modelId) => providerId switch {
        CopilotGateway.ProviderId => (providerId, modelId is CopilotGateway.Astra or CopilotGateway.Sol ? modelId : null),
        LiveCoachSettings.FoundryProvider => (providerId, modelId is { Length: > 0 and <= 100 } &&
            modelId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-') &&
            !PrivateText.IsMatch(modelId) && !PrivateDeployment.IsMatch(modelId) && !Guid.TryParse(modelId, out _) ? modelId : null),
        _ => default
    };

    // The adapter's DATA contract is unchanged. The optional target belongs to the local wrapper only.
    private static Field MinuteInput()
    {
        Field entries(Field slot) => ArrayOf(40, Exact(("slot", slot), ("name", Token), ("level", Maybe(Int32)), ("cooldownSeconds", Maybe(Number))));
        var self = Exact(("hero", Token), ("team", Side), ("alive", Boolean), ("health mana x y", Number),
            ("maxHealth maxMana moveSpeed attackDamage attackSpeed", Maybe(Number)), ("level lastHits", Maybe(Int32)),
            ("muted silenced", Maybe(Boolean)), ("abilities", entries(Maybe(Token))),
            ("items", entries(OneOf("slot0", "slot1", "slot2", "slot3", "slot4", "slot5", "neutral0"))));
        var timing = Exact(("level", Int32), ("levelBasis moveBasis damageBasis attackSpeedBasis", Text),
            ("moveSpeed damagePerHit attackSpeed baseAttackTime", Number));
        var stop = Exact(("id label", Text), ("kind", OneOf("camp", "lane")), ("x y", Number), ("costBasis", Maybe(Text)),
            ("arrivalSeconds finishSeconds walkUnits modeledRosters catalogRosters", Int32), ("packWindowSeconds", Maybe(Int32)));
        var option = Exact(("id", Text), ("stops", ArrayOf(3, stop, min: 1)));
        var prior = Exact(("hero", OneOf("luna", "troll_warlord")), ("fromMinute toMinuteExclusive replayMatches matchedMinutes laneOnly campOnly both neither", Int32));
        var economy = Exact(("gold netWorth lastHits denies kills deaths assists gpm xpm", Maybe(Int32)));
        var equipment = Exact(("hero", Token), ("level", Maybe(Int32)), ("items", Maybe(ArrayOf(6, Maybe(Token)))),
            ("neutral", Maybe(Token)), ("lastSeenClock", Maybe(Number)));
        var ward = Exact(("hero", Token), ("kind", OneOf("observer", "sentry", "ward")), ("rank", Int32), ("rough", Boolean),
            ("share worldX worldY estimatedClock expiresClock", Number));
        var team = Exact(("knowledgeClock", Number), ("equipmentObservedClock sightObservedClock wardSightObservedClock estimateObservedClock", Maybe(Number)),
            ("lastReadEquipment", ArrayOf(5, equipment)), ("visibleAtSightSample", ArrayOf(10, VisibleHero)),
            ("visibleAtWardSample", ArrayOf(64, SeenWard)), ("possibleEnemyWards", ArrayOf(12, ward)));
        var model = Exact(("minute clock horizonSeconds", Int32), ("mapBasis conditions", Text), ("self", self),
            ("timing", Maybe(timing)), ("options", ArrayOf(32, option, min: 1)), ("historicalPrior", Maybe(prior)),
            ("ownEconomy?", economy), ("teamKnowledge?", team));
        var fact = Exact(("inputSequence", Count), ("inputClock", Number), ("includesTeamKnowledge", Boolean),
            ("providerId? modelId?", e => e.ValueKind == JsonValueKind.String),
            ("input", e => JsonMarshal.GetRawUtf8Value(e).Length <= 48 * 1024 && model(e)));
        return e => {
            if (!fact(e)) return false;
            string? providerId = e.TryGetProperty("providerId", out var p) ? p.GetString() : null;
            string? modelId = e.TryGetProperty("modelId", out var m) ? m.GetString() : null;
            return MinuteTarget(providerId, modelId) == (providerId, modelId) &&
                e.GetProperty("includesTeamKnowledge").GetBoolean() == e.GetProperty("input").TryGetProperty("teamKnowledge", out _) &&
                e.GetProperty("input").GetProperty("clock").GetInt32() == Math.Floor(e.GetProperty("inputClock").GetDouble());
        };
    }
    private static readonly Dictionary<(string Source, string Kind), Field> LiveShapes = new() {
        [("own-gsi", "phase")] = Partial(("phase team hero", Token), ("paused resumed", Boolean)),
        [("own-gsi", "own-state")] = Partial(("hero team", Token), ("alive", Boolean),
            ("silenced muted", Maybe(Boolean)),
            ("x y health mana maxHealth maxMana moveSpeed attackDamage attackSpeed", Maybe(Number)),
            ("level lastHits installedBuild gold netWorth denies kills deaths assists gpm xpm", Maybe(Int32)),
            ("items abilities", ArrayOf(40, StateEntry))),
        [("team-vision", "equipment")] = Partial(("available", Boolean), ("heroes", ArrayOf(10, Equipment))),
        [("team-vision", "team-sight")] = Partial(("available", Boolean), ("heroes", ArrayOf(10, VisibleHero))),
        [("team-vision", "ward-observed")] = Partial(("available", Boolean), ("coordinates", Text), ("wards", ArrayOf(64, SeenWard))),
        [("team-vision", "ward-charges")] = Exact(("hero", Token), ("observer sentry unknown", e => Int32(e) && e.GetInt32() >= 0),
            ("firstReadClock", Maybe(Number)), ("lastReadClock", Number)),
        [("inference", "ward-estimate")] = Partial(("available", Boolean), ("coordinates", Text), ("wards", ArrayOf(64, Estimate))),
        [("team-vision", "vision-alert")] = Partial(("hero", Token), ("text", Text)),
        [("inference", "ward-alert")] = Partial(("hero", Token), ("text", Text)),
        [("coach", "farm-input")] = MinuteInput(),
        [("coach", "farm-decision")] = Partial(("status", Text), ("minute inputSequence", Integer), ("inputClock", Number),
            ("includesTeamKnowledge", Boolean), ("chosen", Maybe(Text)), ("offered", ArrayOf(64, Text)), ("stops", ArrayOf(16, Stop))),
        [("coach", "gap")] = Partial(("reason", Text))
    };
    private static bool ValidLive(MatchFact f) => ValidId(f.MatchId) && f.Sequence > 0 && double.IsFinite(f.Clock) &&
        double.IsFinite(f.KnownAt) && f.KnownAt >= f.Clock && LiveShapes.TryGetValue((f.Source, f.Kind), out var shape) &&
        f.Data.ValueKind == JsonValueKind.Object && JsonMarshal.GetRawUtf8Value(f.Data).Length < MaxLineBytes && shape(f.Data) &&
        (f.Kind != "farm-input" || f.Data.GetProperty("inputSequence").GetInt64() < f.Sequence && f.Data.GetProperty("inputClock").GetDouble() <= f.Clock);

    // Worker v1: scripts/replays/postgame/timeline.py. Null in a sampled slot means unknown, not zero/empty.
    private static readonly Field Coverage = Exact(
        ("hero", Exact(("sampleSeconds frames", Count), ("basis", Text), ("knownRows", Partial(
            ("selected hero team x y level hp maxHp alive items itemSlotsRead itemSlotsKnown", Count))))),
        ("ward", Exact(("openAtEnd", Count), ("basis", Text), ("observed", Partial(("placed removed placedHiddenToOpponent", Count))))),
        ("kill", Exact(("basis", Text), ("excluded", Partial(("excludedIllusionOrReincarnation unconfirmedTargetFlags", Count))))),
        ("objective", Exact(("basis", Text))),
        ("observedEvents", Partial(("replay-hero replay-ward replay-objective replay-kill replay-end", Count))));
    private static readonly Field CombatObjective = Exact(("tick", Count),
        ("type", OneOf("roshan", "tormentor", "courier", "tower", "barracks", "ancient")), ("target", Token),
        ("attacker damageSource", Maybe(Token)), ("attackerTeam targetTeam", Maybe(Integer)));
    // The first v1 export used hyphenated Aegis labels; both spellings represent the same typed notification.
    private static readonly Field Aegis = Exact(("tick", Count), ("type", OneOf("aegis_pickup", "aegis_stolen", "aegis_denied",
        "aegis-pickup", "aegis-stolen", "aegis-denied")), ("playerId", Maybe(PlayerId)));
    // Optional only on replay samples. Unreported counters are omitted; zero must be an observed integer.
    private static readonly Field ReplayFarmCounters = Partial(("lastHits netWorth totalEarnedGold creepKillGold denies currentXP",
        e => Int32(e) && e.GetInt32() >= 0));
    private static readonly Field ReplayHero = Exact(("tick", Count), ("playerId", PlayerId), ("hero", Maybe(Token)),
        ("team", Maybe(TeamId)), ("x y", Maybe(Number)), ("level hp maxHp", Maybe(Integer)), ("alive", Maybe(Boolean)),
        ("items", Maybe(ArrayOf(32, Maybe(Token)))), ("terminal", Boolean),
        ("economy?", e => ReplayFarmCounters(e) && e.GetPropertyCount() > 0));
    private static readonly Dictionary<string, Field> ReplayShapes = new(StringComparer.Ordinal) {
        ["replay-hero"] = e => ReplayHero(e) && (!e.TryGetProperty("economy", out _) ||
            (e.GetProperty("hero").ValueKind == JsonValueKind.String && TeamId(e.GetProperty("team")))),
        ["replay-ward"] = Exact(("tick", Count), ("wardId", Text), ("action", OneOf("placed", "removed")),
            ("evidence", OneOf("first-observed-alive", "entity-deleted", "life-state")), ("type", OneOf("observer", "sentry")),
            ("team", Maybe(TeamId)), ("ownerPlayerId", Maybe(PlayerId)), ("x y", Maybe(Number)), ("visibleRadiant visibleDire", Maybe(Boolean))),
        ["replay-kill"] = Exact(("tick", Count), ("target", Token), ("attacker damageSource", Maybe(Token)), ("attackerTeam targetTeam", Maybe(Integer))),
        ["replay-objective"] = e => CombatObjective(e) || Aegis(e),
        ["replay-end"] = Exact(("tick hornTick playbackTicks playbackFrames pauseIntervals", Count), ("winnerTeam", TeamId),
            ("gameBuild", Maybe(Integer)), ("parser clockBasis", Text), ("coverage", Coverage))
    };
    private static void ValidateReplay(byte[] bytes, Cursor c, List<MatchFact>? into)
    {
        try {
            // Windows exporters sometimes use UTF-8 with BOM. Other encodings and trailing JSON are not accepted.
            int bom = bytes.AsSpan().StartsWith("\uFEFF"u8) ? 3 : 0;
            using var doc = JsonDocument.Parse(bytes.AsMemory(bom), Strict);
            var root = doc.RootElement;
            if (root.GetPropertyCount() != 6 || root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("matchId").GetString() != c.Id || root.GetProperty("complete").ValueKind != JsonValueKind.True ||
                root.GetProperty("source").GetString() != ReplaySource ||
                root.GetProperty("replaySha256").GetString() is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid normalized replay header.");
            // This digest identifies the DEM verified by the offline worker. It is not the hash of this JSON.
            var events = root.GetProperty("events");
            int count = events.GetArrayLength();
            if (count is < 1 or > MaxReplayEvents || c.Sequence > long.MaxValue - count)
                throw new InvalidDataException("Invalid replay event count.");
            double end = events[count - 1].GetProperty("clock").GetDouble();
            if (!double.IsFinite(end) || end <= 0) throw new InvalidDataException("Invalid postgame clock.");
            double known = Math.Max(c.End!.Value, end), previous = double.NegativeInfinity;
            int index = 0;
            long previousTick = -1;
            foreach (var ev in events.EnumerateArray()) {
                index++;
                double at = ev.GetProperty("clock").GetDouble();
                string? kind = ev.GetProperty("kind").GetString();
                var data = ev.GetProperty("data");
                if (ev.GetPropertyCount() != 3 || !double.IsFinite(at) || at < previous || kind == null ||
                    !ReplayShapes.TryGetValue(kind, out var shape) || (kind == "replay-end") != (index == count) ||
                    JsonMarshal.GetRawUtf8Value(data).Length >= MaxLineBytes || !shape(data))
                    throw new InvalidDataException($"Invalid normalized replay event at index {index - 1}, or missing terminal end.");
                long tick = data.GetProperty("tick").GetInt64();
                if (tick < previousTick) throw new InvalidDataException("Replay ticks are not in recording order.");
                if (kind == "replay-end") {
                    long horn = data.GetProperty("hornTick").GetInt64();
                    if (horn <= 0 || horn >= tick || tick > data.GetProperty("playbackTicks").GetInt64() ||
                        data.GetProperty("playbackFrames").GetInt64() <= 0) throw new InvalidDataException("Invalid terminal replay evidence.");
                }
                previousTick = tick;
                previous = at;
                into?.Add(new(c.Sequence + index, c.Id, at, known, ReplaySource, kind, data.Clone()));
            }
        } catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) {
            throw new InvalidDataException("Malformed normalized replay.", e);
        }
    }

    public void Dispose()
    {
        lock (admin) {
            lock (gate) { if (closed) return; closed = true; queue.CompleteAdding(); }
            try {
                writer.GetAwaiter().GetResult();
                if (failure != null) throw new IOException("Archive closed with unwritten facts.", failure);
            } finally { lease.Dispose(); queue.Dispose(); }
        }
    }
}
