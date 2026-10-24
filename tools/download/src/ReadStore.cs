using Playkitu.Issuer;

namespace Playkitu.Retrieval;

public sealed class RetrievalError(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record ObjectEntry(string Key, long Bytes);
public sealed record ObjectPage(IReadOnlyList<ObjectEntry> Entries, string? NextToken);
public sealed record SessionPage(IReadOnlyList<Guid> Sessions, string? NextToken);
public sealed record FragmentEntry(string Id, DateTimeOffset ProducerUtc, long DurationMs, long Bytes);
public sealed record ProducerRange(Guid RunId, ulong Epoch, ulong First100ns, ulong Last100ns);
public sealed record FragmentClaim(Guid RunId, ulong Epoch, ulong Upload, string Id, ulong? TimecodeMs, ulong AckType, ulong Result)
{
    // KVS FragmentAck.result is SERVICE_CALL_RESULT, whose OK value is 200.
    // Submitted-frame/native operation results use STATUS_SUCCESS=0 instead;
    // these are separate contracts and must never share a success predicate.
    public bool IsPersisted => AckType == 3 && Result == 200;
}

// Read-only boundary. Production adapters use the existing owner profile; tests
// inject SDK clients/HTTP transports with explicit fake keys, never discovery.
public interface IReadStore
{
    Task<IReadOnlyList<Guid>> ListSessionsAsync(DateOnly day, int limit, CancellationToken ct);
    async Task<SessionPage> SessionsPageAsync(DateOnly day, int limit, string? continuation, CancellationToken ct)
    {
        if (continuation is not null) throw new RetrievalError("session_paging_unavailable");
        return new(await ListSessionsAsync(day, limit, ct), null);
    }
    Task<SessionState?> SessionAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ObjectEntry>> ObjectsAsync(string prefix, int limit, CancellationToken ct);
    Task<ObjectPage> ObjectsPageAsync(string prefix, int limit, string? continuation, CancellationToken ct) =>
        throw new RetrievalError("metadata_paging_unavailable");
    Task<StoredObject?> ReceiptAsync(Guid id, string kind, int sequence, CancellationToken ct);
    Task<Stream> ObjectAsync(string key, CancellationToken ct);
    Task<string> StreamArnAsync(int slot, CancellationToken ct);
    Task<string> SessionStreamArnAsync(SessionState session, CancellationToken ct) => StreamArnAsync(session.Slot, ct);
    Task<IReadOnlyList<FragmentEntry>> FragmentsAsync(string arn, DateTimeOffset start, DateTimeOffset end, int limit, CancellationToken ct);
    Task<Stream> MediaAsync(string arn, IReadOnlyList<string> fragments, CancellationToken ct);
}

public sealed record RetrievalLimits
{
    public const long MiB = 1024 * 1024;
    public int ObjectCount { get; init; } = 256;
    public int FragmentCount { get; init; } = 1024;
    public long DownloadBytes { get; init; } = 1024 * MiB;
    public long ExpandedBytes { get; init; } = 64 * MiB;
    public long ExpandedObjectBytes { get; init; } = 1024 * 1024;
    public long MediaFileBytes { get; init; } = 64 * MiB;
    public long AdvertisedBatchBytes { get; init; } = 48 * MiB;
    public int SourceSeconds { get; init; } = 600;
    public int BatchSeconds { get; init; } = 20;
    public int OverallSeconds { get; init; } = 300;
    public void Validate()
    {
        if (ObjectCount is < 1 or > 256 || FragmentCount is < 1 or > 1024 || DownloadBytes is < 1 or > 1024 * MiB
            || ExpandedBytes is < 1 or > 64 * MiB || ExpandedObjectBytes is < 1 or > MiB || MediaFileBytes is < 1 or > 64 * MiB
            || AdvertisedBatchBytes < 1 || AdvertisedBatchBytes > Math.Min(48 * MiB, MediaFileBytes)
            || SourceSeconds is < 1 or > 600 || BatchSeconds is < 1 or > 20 || OverallSeconds is < 1 or > 300)
            throw new RetrievalError("invalid_limits");
    }
}

public sealed class TransferBudget(RetrievalLimits limits)
{
    public long Downloaded { get; private set; }
    public long Expanded { get; private set; }
    // Charge every received byte, including a rejected over-limit chunk. Reads
    // have a fixed small buffer; limits bound accepted output, not wire overhead.
    public async Task<long> CopyAsync(Stream input, Stream output, long fileLimit, bool expanded, CancellationToken ct)
    {
        long count = 0;
        var buffer = new byte[16384];
        while (true)
        {
            var n = await input.ReadAsync(buffer, ct);
            if (n == 0) return count;
            count = checked(count + n);
            if (expanded) Expanded = checked(Expanded + n); else Downloaded = checked(Downloaded + n);
            if (count > fileLimit || (expanded ? Expanded > limits.ExpandedBytes : Downloaded > limits.DownloadBytes))
                throw new RetrievalError(expanded ? "decompression_limit" : "download_limit");
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
        }
    }
}
