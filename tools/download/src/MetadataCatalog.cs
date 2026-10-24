using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Playkitu.Client;
using Playkitu.Issuer;

namespace Playkitu.Retrieval;

// Selection is in KVS producer fragment-start time, not physical capture time.
// Original objects remain immutable; this scope only bounds their aggregation.
public sealed class ProducerWindow
{
    public ulong From100ns { get; set; }
    public ulong To100ns { get; set; }
    public int[] LogSequences { get; set; } = [];
    public string[] CatalogIssues { get; set; } = [];
    public string[] ConstructedSources { get; set; } = [];
    public string CatalogPageSha256 { get; set; } = "";
    public bool AtomicSnapshot => false;
    public string Meaning => "selected producer fragment-start interval; logs referenced by selected indexes; not full-session coverage";
    public bool Contains(ulong? milliseconds) => milliseconds.HasValue
        && (UInt128)milliseconds.Value * 10000 >= From100ns && (UInt128)milliseconds.Value * 10000 < To100ns;
    public void Validate()
    {
        if (To100ns <= From100ns || To100ns - From100ns > 600UL * 10000000
            || LogSequences.Length > 256 || LogSequences.Distinct().Count() != LogSequences.Length || LogSequences.Any(n => n is < 0 or >= 4096)
            || CatalogIssues.Length > 8 || CatalogIssues.Any(s => s is not ("catalog_timing_loss_or_reset" or "catalog_clock_ambiguity"))
            || ConstructedSources.Length > 4096 || ConstructedSources.Any(s => !MetadataCatalog.Hash(s)) || !MetadataCatalog.Hash(CatalogPageSha256))
            throw new RetrievalError("invalid_producer_window");
        _ = TimingEvidence.Unix(From100ns); _ = TimingEvidence.Unix(To100ns);
    }
    public static ProducerWindow Parse(string from, string to)
    {
        ulong Convert(string text) => checked((ulong)(TimingEvidence.ExplicitUtc(text) - DateTimeOffset.UnixEpoch).Ticks);
        var window = new ProducerWindow { From100ns = Convert(from), To100ns = Convert(to), CatalogPageSha256 = new string('0', 64) };
        window.Validate(); return window;
    }
}

public sealed record MetadataPageReference(string Directory, string Sha256);
public sealed record ClaimDigest(string FragmentHash, string OwnerHash, ulong? TimecodeMs);
public sealed record MetadataSummary(string Kind, int Sequence, ulong? FirstAckMs, ulong? LastAckMs, int[] Logs,
    bool LossOrReset, string[] ConstructedSources, ClaimDigest[] Claims, string? SessionClock, string[] Bridges);
public sealed class MetadataPage
{
    public int Schema { get; set; } = 1;
    public string Kind { get; set; } = "playreel.owner-metadata-page-index";
    public Guid SessionId { get; set; }
    public string AdmissionId { get; set; } = "";
    public int Generation { get; set; }
    public DateTimeOffset AdmittedAt { get; set; }
    public string Prefix { get; set; } = "";
    public string ObservedSessionStatus { get; set; } = "";
    public string Status { get; set; } = "incomplete";
    public DateTimeOffset ListedAtUtc { get; set; }
    public bool AtomicSnapshot => false;
    public string SnapshotMeaning => "one verified listing page; active uploads or insertions before the cursor may be absent";
    public MetadataPageReference? Previous { get; set; }
    public string? NextToken { get; set; }
    public bool EnumerationComplete => NextToken is null;
    public SavedFile Bundle { get; set; } = new("bundle.json", "", 0, "metadata-bundle");
    public List<MetadataSummary> Summaries { get; set; } = [];
}

// One decorator shares the existing receipt/gzip/hash/JSON download path for
// both a single remote page and a window selected from verified local pages.
internal sealed class SelectedObjectsStore(IReadStore inner, IReadOnlyList<ObjectEntry> entries,
    IReadOnlyDictionary<string, string>? local = null) : IReadStore
{
    public Task<IReadOnlyList<Guid>> ListSessionsAsync(DateOnly d, int n, CancellationToken ct) => inner.ListSessionsAsync(d, n, ct);
    public Task<SessionState?> SessionAsync(Guid id, CancellationToken ct) => inner.SessionAsync(id, ct);
    public Task<IReadOnlyList<ObjectEntry>> ObjectsAsync(string prefix, int limit, CancellationToken ct) =>
        Task.FromResult(entries.Count <= limit ? entries : throw new RetrievalError("window_selection_limit"));
    public Task<StoredObject?> ReceiptAsync(Guid id, string kind, int sequence, CancellationToken ct) => inner.ReceiptAsync(id, kind, sequence, ct);
    public Task<Stream> ObjectAsync(string key, CancellationToken ct) => local is null ? inner.ObjectAsync(key, ct)
        : Task.FromResult<Stream>(File.OpenRead(local.TryGetValue(key, out var path) ? path : throw new RetrievalError("window_object_unavailable")));
    public Task<string> StreamArnAsync(int slot, CancellationToken ct) => inner.StreamArnAsync(slot, ct);
    public Task<string> SessionStreamArnAsync(SessionState session, CancellationToken ct) => inner.SessionStreamArnAsync(session, ct);
    public Task<IReadOnlyList<FragmentEntry>> FragmentsAsync(string arn, DateTimeOffset start, DateTimeOffset end, int limit, CancellationToken ct) => inner.FragmentsAsync(arn, start, end, limit, ct);
    public Task<Stream> MediaAsync(string arn, IReadOnlyList<string> fragments, CancellationToken ct) => inner.MediaAsync(arn, fragments, ct);
}

public sealed class MetadataCatalog(IReadStore store, IMediaProbe probe)
{
    private sealed record PageData(string Directory, MetadataPage Page, RetrievalBundle Bundle, string Hash);
    internal static bool Hash(string s) => s.Length == 64 && s.All(char.IsAsciiHexDigit);
    private static void Bind(MetadataPage p, SessionState s)
    {
        if (p.Schema != 1 || p.Kind != "playreel.owner-metadata-page-index" || p.SessionId != s.SessionId
            || p.AdmissionId != s.AdmissionId || p.Generation != s.Generation || p.Prefix != s.Prefix || p.AdmittedAt != s.AdmittedAt
            || p.NextToken?.Length > 8192 || p.Summaries.Count > 256 || p.ListedAtUtc.Offset != TimeSpan.Zero)
            throw new RetrievalError("metadata_page_binding_mismatch");
    }
    private static async Task<List<PageData>> ChainAsync(string directory, SessionState session, CancellationToken ct)
    {
        var pages = new List<PageData>(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? expectedHash = null; long indexBytes = 0;
        while (true) {
            directory = Path.GetFullPath(directory);
            if (!Path.IsPathFullyQualified(directory) || !paths.Add(directory) || pages.Count == 32) throw new RetrievalError("metadata_page_chain_limit");
            var path = RetrievalService.LocalFile(directory, "page.json");
            indexBytes = checked(indexBytes + new FileInfo(path).Length);
            if (new FileInfo(path).Length > 2 * RetrievalLimits.MiB || indexBytes > 8 * RetrievalLimits.MiB) throw new RetrievalError("metadata_catalog_index_limit");
            var hash = await RetrievalService.HashAsync(path, ct);
            if (expectedHash is not null && hash != expectedHash) throw new RetrievalError("metadata_page_hash_mismatch");
            using var doc = TimingEvidence.Parse(await File.ReadAllBytesAsync(path, ct));
            var page = doc.RootElement.Deserialize<MetadataPage>(Wire.Json) ?? throw new RetrievalError("invalid_metadata_page"); Bind(page, session);
            if (page.Bundle.File != "bundle.json" || page.Bundle.Bytes > 2 * RetrievalLimits.MiB || !Hash(page.Bundle.Sha256)) throw new RetrievalError("invalid_metadata_page");
            var bundlePath = RetrievalService.LocalFile(directory, "bundle.json");
            indexBytes = checked(indexBytes + new FileInfo(bundlePath).Length);
            if (indexBytes > 8 * RetrievalLimits.MiB || new FileInfo(bundlePath).Length != page.Bundle.Bytes
                || await RetrievalService.HashAsync(bundlePath, ct) != page.Bundle.Sha256) throw new RetrievalError("metadata_bundle_hash_mismatch");
            // Discovery already verified payload hashes. A later catalogue read
            // checks immutable indexes only; selected payloads are rehashed below.
            // Reading/hashing every historical raw object here would defeat the
            // bounded-window workflow. This remains owner-local, not attestation.
            var bundle = await LocalBundle.OpenAsync(directory, ct, "playreel.owner-metadata-page", verifyFiles: false);
            if (bundle.Status != "retrieved" || bundle.SessionId != session.SessionId || bundle.AdmissionId != session.AdmissionId
                || bundle.AdmissionGeneration != session.Generation || bundle.Window is not null) throw new RetrievalError("metadata_page_incomplete");
            if (!bundle.Objects.Select(o => (o.Kind, o.Sequence)).Order().SequenceEqual(page.Summaries.Select(o => (o.Kind, o.Sequence)).Order()))
                throw new RetrievalError("metadata_summary_binding_mismatch");
            foreach (var summary in page.Summaries)
                if (summary.Logs.Length > 4096 || summary.Logs.Any(n => n is < 0 or >= 4096) || summary.ConstructedSources.Length > 8
                    || summary.ConstructedSources.Any(s => !Hash(s)) || summary.FirstAckMs > summary.LastAckMs || summary.Claims.Length > 4096
                    || summary.Claims.Any(c => !Hash(c.FragmentHash) || !Hash(c.OwnerHash)) || summary.Bridges.Length > 256
                    || summary.Bridges.Any(s => !Hash(s)) || summary.SessionClock is not null && !Hash(summary.SessionClock)) throw new RetrievalError("invalid_metadata_summary");
            pages.Add(new(directory, page, bundle, hash));
            if (page.Previous is null) break;
            if (!Path.IsPathFullyQualified(page.Previous.Directory) || !Hash(page.Previous.Sha256)) throw new RetrievalError("invalid_metadata_previous");
            expectedHash = page.Previous.Sha256; directory = page.Previous.Directory;
        }
        pages.Reverse();
        if (pages.Sum(p => p.Bundle.Objects.Count) > RecordingDiagnosticsLimits.ObjectsPerSession || pages.SelectMany(p => p.Bundle.Objects).Select(o => (o.Kind, o.Sequence)).Distinct().Count() != pages.Sum(p => p.Bundle.Objects.Count))
            throw new RetrievalError("metadata_catalog_duplicate_or_limit");
        for (int i = 1; i < pages.Count; i++) if (pages[i - 1].Page.NextToken is null) throw new RetrievalError("metadata_page_after_end");
        return pages;
    }
    public async Task<MetadataPage> PageAsync(Guid id, string output, string? previous, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(300)); ct = deadline.Token;
        var session = await store.SessionAsync(id, ct) ?? throw new RetrievalError("session_not_found"); RetrievalService.ValidateSession(id, session);
        var chain = previous is null ? [] : await ChainAsync(previous, session, ct);
        if (chain.Count == 32 || chain.Count > 0 && chain[^1].Page.NextToken is null) throw new RetrievalError("metadata_enumeration_finished_or_limit");
        var page = await store.ObjectsPageAsync(session.Prefix, 256, chain.LastOrDefault()?.Page.NextToken, ct);
        if (page.Entries.Count > 256 || chain.Sum(p => p.Bundle.Objects.Count) + page.Entries.Count > RecordingDiagnosticsLimits.ObjectsPerSession) throw new RetrievalError("metadata_catalog_limit");
        if (page.NextToken is not null && chain.Any(p => p.Page.NextToken == page.NextToken)) throw new RetrievalError("repeated_page");
        // The original downloader validates every server-derived key and receipt.
        var selected = new SelectedObjectsStore(store, page.Entries);
        var bundle = await new RetrievalService(selected, probe).FetchAsync(id, output, ct, metadataOnly: true);
        var summaries = new List<MetadataSummary>();
        foreach (var item in bundle.Objects) {
            if (item.Kind == "log") { summaries.Add(new(item.Kind, item.Sequence, null, null, [], false, [], [], null, [])); continue; }
            using var doc = TimingEvidence.Parse(await File.ReadAllBytesAsync(RetrievalService.LocalFile(output, item.Raw.File), ct));
            var evidence = new TimingEvidence(); evidence.Add(doc.RootElement, id, session.Generation, item.Kind, item.Sequence);
            var times = evidence.Claims.Where(c => c.TimecodeMs.HasValue).Select(c => c.TimecodeMs!.Value).ToArray();
            var digests = evidence.Claims.Where(c => c.IsPersisted).Select(c => new ClaimDigest(Digest(c.Id),
                Digest($"{c.RunId:N}:{c.Epoch}:{c.Upload}"), c.TimecodeMs)).ToArray();
            summaries.Add(new(item.Kind, item.Sequence, times.Length == 0 ? null : times.Min(), times.Length == 0 ? null : times.Max(),
                evidence.Logs.Select(l => l.Sequence).Distinct().ToArray(), evidence.NavigationBlocked, evidence.ConstructedSources.ToArray(), digests,
                evidence.StartUtc.HasValue ? Digest($"{evidence.StartUtc.Value.UtcTicks}:{evidence.ClockUncertaintyNs}") : null,
                evidence.NavigationBridges.Select(b => Digest(JsonSerializer.Serialize(b, Wire.Json))).ToArray()));
        }
        if (chain.SelectMany(p => p.Bundle.Objects).Select(o => (o.Kind, o.Sequence)).Intersect(bundle.Objects.Select(o => (o.Kind, o.Sequence))).Any())
            throw new RetrievalError("metadata_catalog_duplicate_or_limit");
        var result = new MetadataPage { SessionId = id, AdmissionId = session.AdmissionId, Generation = session.Generation,
            AdmittedAt = session.AdmittedAt!.Value, Prefix = session.Prefix, ObservedSessionStatus = session.Status, Status = bundle.Status, ListedAtUtc = DateTimeOffset.UtcNow,
            Previous = chain.Count == 0 ? null : new(Path.GetFullPath(previous!), chain[^1].Hash), NextToken = page.NextToken,
            Bundle = await RetrievalService.SavedAsync(Path.Combine(output, "bundle.json"), "metadata-bundle", -1, ct), Summaries = summaries };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, Wire.Json);
        if (bytes.Length > 2 * RetrievalLimits.MiB) throw new RetrievalError("metadata_page_index_limit");
        await using var file = new FileStream(Path.Combine(output, "page.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(bytes, ct); return result;
    }

    public async Task<RetrievalBundle> FetchWindowAsync(Guid id, string lastPage, string output, ProducerWindow window, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(300)); ct = deadline.Token; window.Validate();
        var session = await store.SessionAsync(id, ct) ?? throw new RetrievalError("session_not_found"); RetrievalService.ValidateSession(id, session);
        var pages = await ChainAsync(lastPage, session, ct);
        if (pages.Count == 0 || !pages[^1].Page.EnumerationComplete) throw new RetrievalError("metadata_catalog_incomplete");
        var all = pages.SelectMany(p => p.Bundle.Objects.Select(o => (Page: p, Object: o,
            Summary: p.Page.Summaries.Single(s => s.Kind == o.Kind && s.Sequence == o.Sequence)))).ToArray();
        var indexes = all.Where(o => o.Object.Kind == "time-index").OrderBy(o => o.Object.Sequence).ToArray();
        var chosen = indexes.Select((o, n) => (o, n)).Where(x => x.o.Summary.FirstAckMs.HasValue && x.o.Summary.LastAckMs.HasValue
            && (UInt128)x.o.Summary.LastAckMs.Value * 10000 >= window.From100ns && (UInt128)x.o.Summary.FirstAckMs.Value * 10000 < window.To100ns).ToArray();
        if (chosen.Length == 0) throw new RetrievalError("window_timing_unavailable");
        // Adjacent indexes retain boundary log ranges/clock context. Selection
        // never edits their raw producer ranges or claims into invented evidence.
        var selected = indexes.Skip(Math.Max(0, chosen[0].n - 1)).Take(Math.Min(indexes.Length - 1, chosen[^1].n + 1) - Math.Max(0, chosen[0].n - 1) + 1).ToList();
        var manifest = all.Where(o => o.Object.Kind == "manifest").OrderBy(o => o.Object.Sequence).LastOrDefault();
        if (manifest.Object is null) throw new RetrievalError("manifest_unavailable"); selected.Add(manifest);
        window.LogSequences = selected.SelectMany(o => o.Summary.Logs).Distinct().Order().ToArray();
        foreach (var seq in window.LogSequences) {
            var log = all.SingleOrDefault(o => o.Object.Kind == "log" && o.Object.Sequence == seq);
            if (log.Object is null) throw new RetrievalError("window_required_log_unavailable"); selected.Add(log);
        }
        if (selected.Count > 256) throw new RetrievalError("window_selection_limit");
        if (all.Select(o => o.Summary.SessionClock).Where(s => s is not null).Distinct().Count() > 1) throw new RetrievalError("conflicting_session_clock");
        var selectedClaims = selected.SelectMany(o => o.Summary.Claims).Where(c => window.Contains(c.TimecodeMs)).GroupBy(c => c.FragmentHash)
            .ToDictionary(g => g.Key, g => g.First());
        if (selectedClaims.Count > 1024) throw new RetrievalError("fragment_limit");
        foreach (var claim in all.SelectMany(o => o.Summary.Claims)) if (selectedClaims.TryGetValue(claim.FragmentHash, out var wanted)
            && (claim.OwnerHash != wanted.OwnerHash || claim.TimecodeMs.HasValue && wanted.TimecodeMs.HasValue && claim.TimecodeMs != wanted.TimecodeMs))
            throw new RetrievalError("ambiguous_fragment_claim");
        var issues = new List<string>();
        if (all.Any(o => o.Summary.LossOrReset)) issues.Add("catalog_timing_loss_or_reset");
        // Multiple bridge observations retain the existing conservative refusal;
        // never hide a conflicting earlier clock merely by selecting a later page.
        if (all.SelectMany(o => o.Summary.Bridges).Distinct().Count() > 1) issues.Add("catalog_clock_ambiguity");
        window.CatalogIssues = issues.ToArray();
        window.ConstructedSources = all.SelectMany(o => o.Summary.ConstructedSources).Distinct().ToArray();
        window.CatalogPageSha256 = pages[^1].Hash; window.Validate();
        var entries = new List<ObjectEntry>(); var local = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in selected) {
            var o = item.Object; var key = $"{session.Prefix}{o.Kind}/{o.Sequence:D6}.gz";
            foreach (var f in new[] { o.Compressed, o.Raw }) {
                var path = RetrievalService.LocalFile(item.Page.Directory, f.File);
                if (new FileInfo(path).Length != f.Bytes || await RetrievalService.HashAsync(path, ct) != f.Sha256) throw new RetrievalError("local_file_mismatch");
            }
            entries.Add(new(key, o.Compressed.Bytes)); local.Add(key, RetrievalService.LocalFile(item.Page.Directory, o.Compressed.File));
        }
        // Reuses current strong receipt/admission validation and download limits.
        // Local compressed metadata is conservatively charged to the same budget.
        return await new RetrievalService(new SelectedObjectsStore(store, entries, local), probe).FetchAsync(id, output, ct, window);
    }
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
