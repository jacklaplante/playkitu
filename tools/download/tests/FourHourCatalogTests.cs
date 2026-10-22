using System.IO.Compression;
using System.Text.Json;
using NUnit.Framework;
using Playreel.Tests;
using Playreel.Issuer;
using Playreel.Retrieval;

namespace Retrieval.Tests;

[TestFixture]
public sealed class FourHourCatalogTests
{
    // Production client gzip objects and production issuer receipts are inputs;
    // only cloud transport/listing/media/probe are fake. Never rewrite ACK results.
    private sealed class ExportStore(GeneratedDiagnosticsFixture export, SessionState state, StoredObject[] receipts) : IReadStore
    {
        private readonly ObjectEntry[] entries = export.Objects.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => new ObjectEntry(o.Key, o.CompressedBytes)).ToArray();
        public readonly List<FragmentEntry> Persisted = [];
        public int Pages, MediaReads;
        public Task<SessionState?> SessionAsync(Guid id, CancellationToken ct) => Task.FromResult<SessionState?>(id == state.SessionId ? state : null);
        public Task<IReadOnlyList<Guid>> ListSessionsAsync(DateOnly day, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>([state.SessionId]);
        public Task<IReadOnlyList<ObjectEntry>> ObjectsAsync(string prefix, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<ObjectEntry>>(entries);
        public Task<ObjectPage> ObjectsPageAsync(string prefix, int limit, string? continuation, CancellationToken ct)
        {
            Assert.That(prefix, Is.EqualTo(state.Prefix)); Assert.That(limit, Is.EqualTo(256)); Pages++;
            var page = entries.Where(o => continuation is null || string.CompareOrdinal(o.Key, continuation) > 0).Take(limit).ToArray();
            return Task.FromResult(new ObjectPage(page, page.Length > 0 && entries.Any(o => string.CompareOrdinal(o.Key, page[^1].Key) > 0) ? page[^1].Key : null));
        }
        public Task<StoredObject?> ReceiptAsync(Guid id, string kind, int sequence, CancellationToken ct) => Task.FromResult(receipts.SingleOrDefault(o => o.Descriptor.Kind == kind && o.Descriptor.Sequence == sequence));
        public Task<Stream> ObjectAsync(string key, CancellationToken ct) => Task.FromResult<Stream>(File.OpenRead(GeneratedDiagnosticsFixture.FilePath(export.Objects.Single(o => o.Key == key))));
        public Task<string> StreamArnAsync(int slot, CancellationToken ct) => Task.FromResult("arn:aws:kinesisvideo:us-east-2:123456789012:stream/playreel-slot-01/1234");
        public Task<IReadOnlyList<FragmentEntry>> FragmentsAsync(string arn, DateTimeOffset start, DateTimeOffset end, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FragmentEntry>>(Persisted.Where(f => f.ProducerUtc >= start && f.ProducerUtc < end).ToArray());
        public Task<Stream> MediaAsync(string arn, IReadOnlyList<string> fragments, CancellationToken ct) { MediaReads++; return Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4])); }
    }

    [Test] public async Task ProductionFourHourCatalogueAndLastWindowKeepEvidenceAndOperationBounds()
    {
        var export = GeneratedDiagnosticsFixture.Load(); var evidence = Environment.GetEnvironmentVariable("SBREC_FOUR_HOUR_EVIDENCE")!;
        var stateFile = Path.Combine(evidence, "issuer-session.json");
        Assert.That(File.Exists(stateFile), Is.True, "Run production issuer export validation before retrieval.");
        var state = JsonSerializer.Deserialize<SessionState>(await File.ReadAllBytesAsync(stateFile), Wire.Json)!;
        var receipts = JsonSerializer.Deserialize<StoredObject[]>(await File.ReadAllBytesAsync(Path.Combine(evidence, "issuer-receipts.json")), Wire.Json)!;
        Assert.That(state.SessionId, Is.EqualTo(export.SessionId)); Assert.That(receipts, Has.Length.EqualTo(export.Objects.Length));
        var store = new ExportStore(export, state, receipts); var probe = new FixtureProbe();
        var tool = new MetadataCatalog(store, probe); string? previous = null; int count = 0; long indexBytes = 0; long largest = 0;
        var outputRoot = Path.Combine(evidence, "catalogue-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(outputRoot);
        while (true) {
            var pageDir = Path.Combine(outputRoot, $"page-{count:D2}"); var page = await tool.PageAsync(export.SessionId, pageDir, previous, default);
            Assert.That(page.Status, Is.EqualTo("retrieved")); Assert.That(page.Summaries.Count, Is.LessThanOrEqualTo(256));
            var pageBytes = new FileInfo(Path.Combine(pageDir, "page.json")).Length;
            indexBytes += pageBytes + new FileInfo(Path.Combine(pageDir, "bundle.json")).Length; largest = Math.Max(largest, pageBytes);
            previous = pageDir; count++; if (page.EnumerationComplete) break;
        }
        Assert.That(count, Is.EqualTo((export.Objects.Length + 255) / 256)); Assert.That(store.Pages, Is.EqualTo(count));
        Assert.That(indexBytes, Is.LessThanOrEqualTo(8 * RetrievalLimits.MiB)); Assert.That(largest, Is.LessThanOrEqualTo(2 * RetrievalLimits.MiB));
        // Extract actual persisted claims solely to fake ListFragments. Non-200
        // fixture ACKs remain non-persisted and must not authorize media reads.
        foreach (var item in export.Objects.Where(o => o.Kind == "time-index")) {
            await using var file = File.OpenRead(GeneratedDiagnosticsFixture.FilePath(item)); await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var doc = await JsonDocument.ParseAsync(gzip); var timing = new TimingEvidence(); timing.Add(doc.RootElement, export.SessionId, state.Generation, item.Kind, item.Sequence);
            foreach (var claim in timing.Claims.Where(c => c.IsPersisted && c.TimecodeMs.HasValue))
                store.Persisted.Add(new(claim.Id, TimingEvidence.Unix(checked(claim.TimecodeMs!.Value * 10000)), 1000, 4));
        }
        var window = ProducerWindow.Parse("2026-10-15T15:59:50Z", "2026-10-15T16:00:01Z"); var output = Path.Combine(outputRoot, "tail-window");
        var bundle = await tool.FetchWindowAsync(export.SessionId, previous!, output, window, default);
        Assert.That(bundle.Objects.Count, Is.LessThanOrEqualTo(256)); Assert.That(bundle.Objects.Any(o => o.Kind == "manifest" && o.Sequence == 81), Is.True);
        Assert.That(bundle.Objects.Any(o => o.Kind == "time-index" && o.Sequence == 1799), Is.True);
        Assert.That(bundle.Objects.Any(o => o.Kind == "log" && o.Sequence == 2880 && o.Raw.Bytes == 6), Is.True);
        Assert.That(bundle.LogGaps, Is.Empty); Assert.That(bundle.DownloadedBytes, Is.LessThanOrEqualTo(1024 * RetrievalLimits.MiB));
        if (store.Persisted.Count == 0) { Assert.That(bundle.Status, Is.EqualTo("incomplete")); Assert.That(bundle.Media, Is.Empty); Assert.That(store.MediaReads, Is.Zero); }
        else { Assert.That(bundle.Status, Is.EqualTo("retrieved"), string.Join(',', bundle.Issues)); Assert.That(bundle.Media, Is.Not.Empty); }
        var map = await LocalBundle.MapAsync(output, "2026-10-15T15:59:59Z", null, default);
        Assert.That(map.Status, Is.Not.EqualTo("mapped")); Assert.That(map.PlaybackOffsetVerified, Is.False);
        await File.WriteAllTextAsync(Path.Combine(evidence, "catalogue-result.json"), JsonSerializer.Serialize(new {
            export.SessionId, Fixture = GeneratedDiagnosticsFixture.DirectoryPath, Output = outputRoot,
            Objects = export.Objects.Length, Pages = count, CatalogueBytes = indexBytes, LargestPageBytes = largest,
            PersistedClaims = store.Persisted.Count, TailStatus = bundle.Status, TailIssues = bundle.Issues,
            TailObjects = bundle.Objects.Count, MediaReads = store.MediaReads, StrictMapStatus = map.Status,
            ActualNativeOrHardwareProof = false, RawObjectsRewritten = false }, Wire.Json));
    }
}
