using Playkitu.Issuer;
using Playkitu.Retrieval;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Retrieval.Tests;

public sealed class FixtureStore : IReadStore
{
    public static readonly Guid Id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    public static readonly Guid Run = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");
    public const ulong First = 17920702470000000;
    public SessionState State = new() { SessionId = Id, AdmissionId = "123456781234123412341234567890ab", Status = "active", Generation = 1,
        AdmittedAt = DateTimeOffset.Parse("2026-10-15T12:00:00Z"), Slot = 0, EndsAt = DateTimeOffset.Parse("2026-10-15T16:00:00Z") };
    public Dictionary<string, byte[]> Payloads = [];
    public Dictionary<string, StoredObject> Receipts = [];
    public List<FragmentEntry> Fragments = [new("frag1", TimingEvidence.Unix(First), 1000, 32)];
    public byte[] Media = [1, 2, 3, 4];
    public int SessionReads, ObjectReads, MediaReads;
    public List<SessionState> SessionRows = [];
    public List<int> SessionPageLimits = [];
    public bool BlockMedia;
    public List<IReadOnlyList<string>> RequestedFragments = [];
    public List<ObjectEntry>? EntriesOverride;
    public async Task<SessionState?> SessionAsync(Guid id, CancellationToken ct) { SessionReads++; await Task.CompletedTask; return SessionRows.Count > 0 ? SessionRows.SingleOrDefault(s => s.SessionId == id) : id == State.SessionId ? State : null; }
    public Task<SessionPage> SessionsPageAsync(DateOnly day, int limit, string? continuation, CancellationToken ct)
    {
        SessionPageLimits.Add(limit);
        if (SessionRows.Count == 0) return Task.FromResult(new SessionPage([State.SessionId], null));
        var rows = SessionRows.Where(s => DateOnly.FromDateTime(s.AdmittedAt!.Value.UtcDateTime) == day).OrderByDescending(s => s.AdmittedAt).ToArray();
        var start = continuation is null ? 0 : int.Parse(continuation);
        var page = rows.Skip(start).Take(limit).Select(s => s.SessionId).ToArray();
        return Task.FromResult(new SessionPage(page, start + page.Length < rows.Length ? (start + page.Length).ToString() : null));
    }
    public Task<IReadOnlyList<Guid>> ListSessionsAsync(DateOnly day, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>([State.SessionId]);
    public Task<IReadOnlyList<ObjectEntry>> ObjectsAsync(string prefix, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<ObjectEntry>>(EntriesOverride ?? Payloads.Select(p => new ObjectEntry(p.Key, p.Value.Length)).ToList());
    public Task<ObjectPage> ObjectsPageAsync(string prefix, int limit, string? continuation, CancellationToken ct)
    {
        var all = (EntriesOverride ?? Payloads.Select(p => new ObjectEntry(p.Key, p.Value.Length)).ToList()).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var page = all.Where(p => continuation is null || string.CompareOrdinal(p.Key, continuation) > 0).Take(limit).ToArray();
        var more = page.Length > 0 && all.Any(p => string.CompareOrdinal(p.Key, page[^1].Key) > 0);
        return Task.FromResult(new ObjectPage(page, more ? page[^1].Key : null));
    }
    public Task<StoredObject?> ReceiptAsync(Guid id, string kind, int sequence, CancellationToken ct) => Task.FromResult(Receipts.GetValueOrDefault($"{kind}:{sequence}"));
    public Task<Stream> ObjectAsync(string key, CancellationToken ct) { ObjectReads++; return Task.FromResult<Stream>(new MemoryStream(Payloads[key], false)); }
    public Task<string> StreamArnAsync(int slot, CancellationToken ct) => Task.FromResult($"arn:aws:kinesisvideo:us-east-2:123456789012:stream/playkitu-slot-{slot + 1:D2}/1234");
    public Task<string> SessionStreamArnAsync(SessionState session, CancellationToken ct) => session.OnDemand ? Task.FromResult(session.StreamArn!) : StreamArnAsync(session.Slot, ct);
    public Task<IReadOnlyList<FragmentEntry>> FragmentsAsync(string arn, DateTimeOffset start, DateTimeOffset end, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<FragmentEntry>>(Fragments);
    public async Task<Stream> MediaAsync(string arn, IReadOnlyList<string> fragments, CancellationToken ct)
    {
        MediaReads++; RequestedFragments.Add(fragments);
        if (BlockMedia) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new MemoryStream(Media, false);
    }
    public void Add(string kind, int sequence, byte[] raw)
    {
        using var memory = new MemoryStream(); using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, true)) gzip.Write(raw);
        var compressed = memory.ToArray(); var key = $"{State.Prefix}{kind}/{sequence:D6}.gz";
        Payloads[key] = compressed; Receipts[$"{kind}:{sequence}"] = new(new(kind, sequence, compressed.Length, Convert.ToBase64String(SHA256.HashData(compressed))), key, 9999999999, State.AdmissionId);
    }
    public JsonObject Timing(string kind, int sequence) => new()
    {
        ["schemaVersion"] = 1, ["sessionId"] = State.SessionId.ToString("D"), ["admissionGeneration"] = State.Generation, ["objectKind"] = kind, ["sequence"] = sequence,
        ["sessionClock"] = new JsonObject { ["clockId"] = "fixture", ["frequencyHz"] = 10000000, ["startTicks"] = null, ["sessionElapsedNs"] = 0,
            ["utc"] = "2026-10-15T12:00:00Z", ["uncertaintyNs"] = 100 },
        ["droppedRecords"] = 0, ["droppedRuns"] = 0, ["droppedObjects"] = 0, ["droppedLogBytes"] = 0
    };
    public JsonObject Index()
    {
        var root = Timing("time-index", 0);
        root["producerRuns"] = new JsonArray(new JsonObject { ["producerRunId"] = Run.ToString("D"), ["epoch"] = 0,
            ["ranges"] = new JsonObject { ["submitted:video:0"] = new JsonObject { ["first"] = new JsonObject { ["sdkPts100ns"] = First, ["result"] = 0 }, ["last"] = new JsonObject { ["sdkPts100ns"] = First + 10000000, ["result"] = 0 } } } });
        root["records"] = new JsonArray(new JsonObject { ["kind"] = "ack", ["producerRunId"] = Run.ToString("D"), ["epoch"] = 0, ["sequence"] = 4,
            ["upload"] = ulong.MaxValue - 1, ["fragment"] = "frag1", ["ackType"] = 3, ["flags"] = 3, ["result"] = 200, ["ackTimecodeMs"] = First / 10000 });
        return root;
    }
    public void AddJson(string kind, int sequence, JsonObject value) => Add(kind, sequence, JsonSerializer.SerializeToUtf8Bytes(value));
    public void Ready(JsonObject? index = null) { AddJson("manifest", 0, Timing("manifest", 0)); AddJson("time-index", 0, index ?? Index()); }
}

public sealed class FixtureProbe : IMediaProbe
{
    public int Exit; public bool WrongHash;
    public async Task<int> RunAsync(string input, string output, CancellationToken ct, IReadOnlyList<ulong>? selectedTargets = null)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/sdk-decoded-probe-v1.json"), ct))!;
        root["fileSha256"] = WrongHash ? new string('0', 64) : await RetrievalService.HashAsync(input, ct);
        root["fileBytes"] = new FileInfo(input).Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(output, root.ToJsonString(), ct);
        return Exit;
    }
}
