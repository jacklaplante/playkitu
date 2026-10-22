using Playreel.Issuer;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Playreel.Retrieval;

public sealed record SavedFile(string File, string Sha256, long Bytes, string Kind, int Sequence = -1);
public sealed record SavedObject(string Kind, int Sequence, SavedFile Compressed, SavedFile Raw);
public sealed record SavedMedia(Guid RunId, ulong Epoch, ulong Upload, string[] Fragments, SavedFile Media, SavedFile? Probe, bool ProbeComplete = false);
public sealed class RetrievalBundle
{
    public int Schema { get; set; } = 1;
    public string Kind { get; set; } = "playreel.owner-retrieval";
    public Guid SessionId { get; set; }
    public string AdmissionId { get; set; } = "";
    public int AdmissionGeneration { get; set; }
    public DateTimeOffset AdmittedAt { get; set; }
    public string Status { get; set; } = "incomplete";
    public string Coverage { get; set; } = "unverified";
    public List<string> Issues { get; set; } = [];
    public List<SavedObject> Objects { get; set; } = [];
    public List<SavedMedia> Media { get; set; } = [];
    public List<SavedFile> LogParts { get; set; } = [];
    public List<LogGap> LogGaps { get; set; } = [];
    public RetrievalLimits Limits { get; set; } = new();
    public long DownloadedBytes { get; set; }
    public long DecompressedBytes { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ProducerWindow? Window { get; set; }
}
public interface IMediaProbe { Task<int> RunAsync(string input, string output, CancellationToken ct, IReadOnlyList<ulong>? selectedTargets = null); }

public sealed class RetrievalService(IReadStore store, IMediaProbe probe, RetrievalLimits? configuredLimits = null)
{
    private const long FragmentMetadataToleranceTicks = TimeSpan.TicksPerMillisecond;
    private readonly RetrievalLimits limits = configuredLimits ?? new();
    public async Task<IReadOnlyList<object>> ListAsync(DateOnly day, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 50) throw new RetrievalError("invalid_list_limit");
        var results = new List<object>();
        foreach (var id in await store.ListSessionsAsync(day, limit, ct))
        {
            var session = await store.SessionAsync(id, ct);
            if (session is null) continue; // GSI entries/TTL can lag the base row.
            ValidateSession(id, session);
            results.Add(new { sessionId = id, session.Status, admittedAt = session.AdmittedAt, session.Build, videoSlot = session.Slot, generation = session.Generation });
        }
        return results;
    }
    public static void ValidateSession(Guid id, SessionState s)
    {
        if (id == Guid.Empty || s.SessionId != id || s.AdmittedAt is null || s.AdmittedAt.Value.Offset != TimeSpan.Zero
            || s.Generation is < 1 or > 3 || !Guid.TryParseExact(s.AdmissionId, "N", out _) || s.Slot is < -1 or > 3)
            throw new RetrievalError("invalid_session_metadata");
    }
    public async Task<RetrievalBundle> FetchAsync(Guid id, string output, CancellationToken ct, ProducerWindow? window = null, bool metadataOnly = false)
    {
        limits.Validate(); output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new RetrievalError("output_exists");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(limits.OverallSeconds));
        var token = deadline.Token;
        var session = await store.SessionAsync(id, token) ?? throw new RetrievalError("session_not_found");
        ValidateSession(id, session);
        Directory.CreateDirectory(output);
        window?.Validate();
        var bundle = new RetrievalBundle { SessionId = id, AdmissionId = session.AdmissionId, AdmissionGeneration = session.Generation, AdmittedAt = session.AdmittedAt!.Value, Limits = limits,
            Kind = metadataOnly ? "playreel.owner-metadata-page" : "playreel.owner-retrieval", Window = window };
        var budget = new TransferBudget(limits);
        try
        {
            var entries = await store.ObjectsAsync(session.Prefix, limits.ObjectCount, token);
            if (entries.Count > limits.ObjectCount) throw new RetrievalError("object_limit");
            var seen = new HashSet<(string, int)>();
            foreach (var entry in entries.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var match = Regex.Match(entry.Key, "^" + Regex.Escape(session.Prefix) + "(log|time-index|manifest)/([0-9]{6})\\.gz$");
                    if (!match.Success) throw new RetrievalError("unexpected_object_key");
                    var kind = match.Groups[1].Value; var sequence = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (sequence >= 4096 || !seen.Add((kind, sequence))) throw new RetrievalError("duplicate_or_invalid_sequence");
                    var receipt = await store.ReceiptAsync(id, kind, sequence, token);
                    if (receipt is null || receipt.AdmissionId != session.AdmissionId) throw new RetrievalError("stale_or_missing_receipt");
                    var d = receipt.Descriptor; d.Validate();
                    if (d.Kind != kind || d.Sequence != sequence || receipt.Key != entry.Key || entry.Bytes != d.CompressedBytes)
                        throw new RetrievalError("receipt_mismatch");
                    var stem = $"{kind}-{sequence:D6}";
                    var partial = Path.Combine(output, stem + ".gz.partial");
                    await using (var source = await store.ObjectAsync(entry.Key, token))
                    await using (var target = NewFile(partial))
                    {
                        var count = await budget.CopyAsync(source, target, d.CompressedBytes, false, token);
                        if (count != d.CompressedBytes) throw new RetrievalError("object_length_mismatch");
                    }
                    var hash = await HashAsync(partial, token);
                    if (Convert.ToBase64String(Convert.FromHexString(hash)) != d.Sha256Base64) throw new RetrievalError("object_checksum_mismatch");
                    var compressedPath = Path.Combine(output, stem + ".gz"); File.Move(partial, compressedPath);
                    var rawPartial = Path.Combine(output, stem + ".raw.partial");
                    await using (var file = File.OpenRead(compressedPath))
                    await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                    await using (var target = NewFile(rawPartial))
                        await budget.CopyAsync(gzip, target, limits.ExpandedObjectBytes, true, token);
                    if (kind != "log")
                    {
                        using var document = TimingEvidence.Parse(await File.ReadAllBytesAsync(rawPartial, token));
                        // Validate this immutable object before including it. A
                        // separate aggregate pass detects inter-object conflicts.
                        new TimingEvidence().Add(document.RootElement, id, session.Generation, kind, sequence);
                    }
                    var rawPath = Path.Combine(output, stem + ".raw"); File.Move(rawPartial, rawPath);
                    bundle.Objects.Add(new(kind, sequence, await SavedAsync(compressedPath, kind, sequence, token), await SavedAsync(rawPath, kind, sequence, token)));
                }
                catch (RetrievalError error) when (!error.Code.EndsWith("limit", StringComparison.Ordinal)) { bundle.Issues.Add(error.Code); }
                catch (Exception error) when (error is IssuerError or JsonException or InvalidDataException or FormatException or OverflowException)
                { bundle.Issues.Add("invalid_object_evidence"); }
            }
            if (!metadataOnly) {
            var timing = await ReadTimingAsync(output, bundle, token);
            if (timing.LatestManifest < 0) bundle.Issues.Add("manifest_unavailable");
            bundle.Issues.AddRange(timing.Uncertainties.Distinct());
            await ReconstructLogsAsync(output, bundle, timing, token);
            if (session.Slot >= 0) await FetchMediaAsync(output, bundle, timing, session.Slot, budget, token);
            else bundle.Issues.Add("logs_only_session");
            }
            bundle.Status = bundle.Issues.Count == 0 ? "retrieved" : "incomplete";
        }
        catch (OperationCanceledException) { bundle.Issues.Add("deadline_or_cancelled"); }
        catch (RetrievalError error) { bundle.Issues.Add(error.Code); }
        catch (Exception) { bundle.Issues.Add("read_failed"); } // Never persist SDK/HTTP exception text.
        bundle.DownloadedBytes = budget.Downloaded; bundle.DecompressedBytes = budget.Expanded;
        bundle.Issues = bundle.Issues.Distinct().ToList();
        await using var result = NewFile(Path.Combine(output, "bundle.json"));
        await JsonSerializer.SerializeAsync(result, bundle, Wire.Json, CancellationToken.None);
        return bundle;
    }

    private async Task FetchMediaAsync(string output, RetrievalBundle bundle, TimingEvidence timing, int slot, TransferBudget budget, CancellationToken ct)
    {
        var ranges = timing.MergedRanges(limits, bundle.Window);
        if (ranges.Count == 0) { bundle.Issues.Add("producer_range_unavailable"); return; }
        var arn = await store.StreamArnAsync(slot, ct); var selectedIds = new HashSet<string>();
        var claimOwners = timing.Claims.Where(c => c.IsPersisted).GroupBy(c => c.Id);
        if (claimOwners.Any(g => g.Select(c => (c.RunId, c.Epoch, c.Upload)).Distinct().Count() != 1
            || g.Where(c => c.TimecodeMs.HasValue).Select(c => c.TimecodeMs).Distinct().Count() > 1)) throw new RetrievalError("ambiguous_fragment_claim");
        foreach (var range in ranges)
        {
            var targets = ProbeEvidence.ValidateTargets(timing.SchedulingPoints().Where(p => p.Run == range.RunId && p.ProducerEpoch == range.Epoch).Select(p => p.TargetNs).Distinct().OrderBy(n => n).ToArray());
            // KVS timecodes have an explicit 1ms scale. Preserve original 100ns
            // values; floor/ceil only the query envelope, never a playback anchor.
            var begin = TimingEvidence.Unix(range.First100ns / 10000 * 10000);
            var end = TimingEvidence.Unix(checked((range.Last100ns / 10000 + 1) * 10000));
            var returned = await store.FragmentsAsync(arn, begin, end, limits.FragmentCount, ct);
            if (returned.Count > limits.FragmentCount) throw new RetrievalError("fragment_limit");
            var claims = timing.Claims.Where(c => c.RunId == range.RunId && c.Epoch == range.Epoch && c.IsPersisted
                && c.TimecodeMs.HasValue && c.TimecodeMs >= range.First100ns / 10000 && c.TimecodeMs <= range.Last100ns / 10000).ToArray();
            var attributed = new List<(FragmentEntry Fragment, FragmentClaim Claim)>();
            foreach (var f in returned)
            {
                var matching = claims.Where(c => c.Id == f.Id).ToArray();
                if (matching.Length == 0) continue; // Adjacent/unacknowledged data is not attributed by a clock guess.
                // AWS SDK numeric Unix timestamp parsing can put an exact1ms
                // boundary one100ns tick early. Apply the same metadata tolerance
                // here and below; the exact persisted ID, run/epoch/upload and
                // in-range ACK time are still mandatory before this check.
                if (f.ProducerUtc.UtcTicks < begin.UtcTicks - FragmentMetadataToleranceTicks
                    || f.ProducerUtc.UtcTicks >= end.UtcTicks + FragmentMetadataToleranceTicks
                    || f.DurationMs <= 0 || f.Bytes <= 0) throw new RetrievalError("fragment_range_mismatch");
                var producerTicks = checked((ulong)(f.ProducerUtc - DateTimeOffset.UnixEpoch).Ticks);
                var ackTicks = checked(matching[0].TimecodeMs!.Value * 10000);
                var delta = producerTicks > ackTicks ? producerTicks - ackTicks : ackTicks - producerTicks;
                if (delta > (ulong)FragmentMetadataToleranceTicks) throw new RetrievalError("fragment_timecode_mismatch");
                if (selectedIds.Add(f.Id)) attributed.Add((f, matching[0]));
                if (selectedIds.Count > limits.FragmentCount) throw new RetrievalError("fragment_limit");
            }
            if (claims.Any(c => !returned.Any(f => f.Id == c.Id))) bundle.Issues.Add("claimed_fragment_not_listed");
            if (attributed.Count == 0) { bundle.Issues.Add("media_attribution_unavailable"); continue; }
            foreach (var group in attributed.GroupBy(x => x.Claim.Upload))
            {
                var batch = new List<FragmentEntry>(); long bytes = 0;
                foreach (var item in group.OrderBy(x => x.Fragment.ProducerUtc))
                {
                    var f = item.Fragment;
                    if (f.Bytes > limits.AdvertisedBatchBytes || f.DurationMs > limits.BatchSeconds * 1000L) { bundle.Issues.Add("fragment_exceeds_batch_limits"); continue; }
                    if (batch.Count > 0 && (bytes + f.Bytes > limits.AdvertisedBatchBytes || f.ProducerUtc.AddMilliseconds(f.DurationMs) - batch[0].ProducerUtc > TimeSpan.FromSeconds(limits.BatchSeconds)))
                    { await DownloadMediaAsync(output, bundle, range, group.Key, batch, arn, budget, targets, ct); batch = []; bytes = 0; }
                    batch.Add(f); bytes = checked(bytes + f.Bytes);
                }
                if (batch.Count > 0) await DownloadMediaAsync(output, bundle, range, group.Key, batch, arn, budget, targets, ct);
            }
        }
    }
    private async Task DownloadMediaAsync(string output, RetrievalBundle bundle, ProducerRange range, ulong upload, List<FragmentEntry> fragments, string arn, TransferBudget budget, IReadOnlyList<ulong> targets, CancellationToken ct)
    {
        var stem = $"media-{bundle.Media.Count:D4}"; var partial = Path.Combine(output, stem + ".mkv.partial");
        await using (var source = await store.MediaAsync(arn, fragments.Select(f => f.Id).ToArray(), ct))
        await using (var target = NewFile(partial)) await budget.CopyAsync(source, target, limits.MediaFileBytes, false, ct);
        var mediaPath = Path.Combine(output, stem + ".mkv"); File.Move(partial, mediaPath);
        var media = await SavedAsync(mediaPath, "media", -1, ct); SavedFile? probeFile = null;
        var mediaIndex = bundle.Media.Count;
        bundle.Media.Add(new(range.RunId, range.Epoch, upload, fragments.Select(f => f.Id).ToArray(), media, null));
        var probePath = Path.Combine(output, stem + ".probe.json");
        var complete = false;
        var code = await probe.RunAsync(mediaPath, probePath, ct, targets);
        if (code != 0) bundle.Issues.Add("probe_incomplete");
        if (File.Exists(probePath) && new FileInfo(probePath).Length <= 2 * RetrievalLimits.MiB)
        {
            using var document = TimingEvidence.Parse(await File.ReadAllBytesAsync(probePath, ct));
            var root = document.RootElement; _ = ProbeEvidence.Summary(root);
            if (TimingEvidence.Text(root, "fileSha256") != media.Sha256 || ProbeEvidence.Decimal(root, "fileBytes") is { } reportedBytes && reportedBytes != (ulong)media.Bytes)
                throw new RetrievalError("probe_file_mismatch");
            if (TimingEvidence.Text(root, "status") != "decoded" || !root.GetProperty("decodeEos").GetBoolean()) bundle.Issues.Add("probe_incomplete");
            complete = code == 0 && TimingEvidence.Text(root, "status") == "decoded" && root.GetProperty("decodeEos").GetBoolean()
                && (!root.TryGetProperty("reason", out var reason) || reason.GetString() == "");
            if (!complete) bundle.Issues.Add("probe_incomplete");
            if (!ProbeEvidence.Selections(root).Select(s => s.TargetNs).OrderBy(n => n).SequenceEqual(targets.OrderBy(n => n)))
            { complete = false; bundle.Issues.Add("probe_query_mismatch"); }
            probeFile = await SavedAsync(probePath, "probe", -1, ct);
        }
        else bundle.Issues.Add("probe_unavailable");
        bundle.Media[mediaIndex] = bundle.Media[mediaIndex] with { Probe = probeFile, ProbeComplete = complete };
    }
    public static async Task<TimingEvidence> ReadTimingAsync(string output, RetrievalBundle bundle, CancellationToken ct)
    {
        var evidence = new TimingEvidence();
        if (bundle.Window is not null) {
            bundle.Window.Validate(); evidence.Uncertainties.AddRange(bundle.Window.CatalogIssues);
            foreach (var hash in bundle.Window.ConstructedSources) evidence.ConstructedSources.Add(hash);
        }
        foreach (var item in bundle.Objects.Where(o => o.Kind != "log").OrderBy(o => o.Kind).ThenBy(o => o.Sequence))
        {
            if (item.Raw.Bytes > RetrievalLimits.MiB) throw new RetrievalError("evidence_size_limit");
            using var document = TimingEvidence.Parse(await File.ReadAllBytesAsync(LocalFile(output, item.Raw.File), ct));
            evidence.Add(document.RootElement, bundle.SessionId, bundle.AdmissionGeneration, item.Kind, item.Sequence, bundle.Window);
        }
        return evidence;
    }
    private static async Task ReconstructLogsAsync(string output, RetrievalBundle bundle, TimingEvidence timing, CancellationToken ct)
    {
        FileStream? part = null; string? partPath = null; int previous = -1;
        // Sealed indices consume their pending gap rows, so each retained row
        // accounts for one loss exactly once. The cumulative counter overlaps
        // those rows; only its unaccounted remainder is additional loss. Do not
        // deduplicate equal rows: two rejected calls can have identical fields.
        var accountedBytes = timing.LogGaps.Aggregate(0UL, (sum, gap) => checked(sum + gap.Bytes));
        var unaccountedBytes = timing.DroppedLogBytes > accountedBytes ? timing.DroppedLogBytes - accountedBytes : 0;
        if (unaccountedBytes > 0) bundle.LogGaps.Add(new(null, unaccountedBytes, null, "cumulative_loss_without_position"));
        // Evicted log-range observations can leave a known rejected-input gap
        // with no position in retained bytes. Preserve each batch separately in
        // that case; joining adjacent sequences would imply unknown continuity.
        var unplacedGap = unaccountedBytes > 0 || timing.LogGaps.Any(g => g.Bytes > 0 && (!g.AcceptedByteOffset.HasValue
            || !timing.Logs.Any(r => g.AcceptedByteOffset.Value >= r.ByteStart && g.AcceptedByteOffset.Value - r.ByteStart <= r.ByteLength)));
        if (unplacedGap)
        {
            bundle.Issues.Add("unplaced_log_gap");
            bundle.Issues.Add("within_batch_log_continuity_unknown");
        }
        async Task EndPart()
        {
            if (part is not null) { await part.DisposeAsync(); bundle.LogParts.Add(await SavedAsync(partPath!, "log-part", -1, ct)); part = null; }
        }
        try
        {
            foreach (var log in bundle.Objects.Where(o => o.Kind == "log").OrderBy(o => o.Sequence))
            {
                if (unplacedGap) await EndPart();
                if (log.Sequence != previous + 1)
                {
                    await EndPart();
                    // A selected window deliberately omits earlier/unreferenced
                    // batches. Always split parts, but do not invent data loss
                    // for those exclusions. Missing REQUIRED batches still fail.
                    if (bundle.Window is null || Enumerable.Range(previous + 1, log.Sequence - previous - 1).Any(bundle.Window.LogSequences.Contains)) {
                        bundle.LogGaps.Add(new(null, 0, previous + 1, "missing_sequence")); bundle.Issues.Add("missing_log_sequence");
                    }
                }
                previous = log.Sequence;
                var rows = timing.Logs.Where(r => r.Sequence == log.Sequence).Distinct().ToArray();
                if (rows.Length > 1 || rows.Length == 1 && rows[0].ByteLength != log.Raw.Bytes) throw new RetrievalError("conflicting_log_range");
                var bytes = await File.ReadAllBytesAsync(LocalFile(output, log.Raw.File), ct);
                var cuts = rows.Length == 1 ? timing.LogGaps.Where(g => g.Bytes > 0 && g.AcceptedByteOffset.HasValue
                    && g.AcceptedByteOffset.Value >= rows[0].ByteStart && g.AcceptedByteOffset.Value <= rows[0].ByteStart + rows[0].ByteLength)
                    .Select(g => checked((int)(g.AcceptedByteOffset!.Value - rows[0].ByteStart))).Distinct().OrderBy(v => v).ToArray() : [];
                var offset = 0;
                foreach (var end in cuts.Append(bytes.Length))
                {
                    if (end > offset)
                    {
                        if (part is null) { partPath = Path.Combine(output, $"logs-{bundle.LogParts.Count:D4}.log"); part = NewFile(partPath); }
                        await part.WriteAsync(bytes.AsMemory(offset, end - offset), ct);
                    }
                    if (cuts.Contains(end)) await EndPart();
                    offset = end;
                }
            }
            await EndPart(); bundle.LogGaps.AddRange(timing.LogGaps);
            foreach (var sequence in timing.ReferencedLogSequences.Where(s => !bundle.Objects.Any(o => o.Kind == "log" && o.Sequence == s)).OrderBy(s => s))
            { bundle.LogGaps.Add(new(null, 0, sequence, "referenced_object_unavailable")); bundle.Issues.Add("referenced_log_unavailable"); }
        }
        finally { if (part is not null) await part.DisposeAsync(); }
    }
    public static FileStream NewFile(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, true);
    public static async Task<string> HashAsync(string path, CancellationToken ct)
    { await using var file = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)); }
    public static async Task<SavedFile> SavedAsync(string path, string kind, int sequence, CancellationToken ct) =>
        new(Path.GetFileName(path), await HashAsync(path, ct), new FileInfo(path).Length, kind, sequence);
    public static string LocalFile(string directory, string file)
    {
        if (file != Path.GetFileName(file) || !Regex.IsMatch(file, @"^[a-z0-9][a-z0-9.-]{0,100}$") || file.Contains("..", StringComparison.Ordinal))
            throw new RetrievalError("invalid_local_reference");
        var path = Path.Combine(directory, file);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new RetrievalError("reparse_reference_rejected");
        return path;
    }
}
