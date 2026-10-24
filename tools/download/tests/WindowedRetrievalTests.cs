using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Globalization;
using NUnit.Framework;
using Playkitu.Retrieval;

namespace Retrieval.Tests;

[TestFixture]
public sealed class WindowedRetrievalTests
{
    private static string Fresh(string name) => Path.Combine(Environment.GetEnvironmentVariable("SBREC_RETRIEVAL_TEST_ROOT")
        ?? Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N"));

    [Test] public async Task LegacyFetchReportsObjectCapBeforeReadingAnOversizedSession()
    {
        var store = new FixtureStore(); store.Ready();
        for (int n = 0; n < 257; n++) store.Add("log", n, Encoding.UTF8.GetBytes("bounded fixture\n"));
        var bundle = await new RetrievalService(store, new FixtureProbe()).FetchAsync(FixtureStore.Id, Fresh("legacy-object-cap"), default);
        Assert.That(bundle.Status, Is.EqualTo("incomplete")); Assert.That(bundle.Issues, Does.Contain("object_limit"));
        Assert.That(store.ObjectReads, Is.Zero); Assert.That(store.MediaReads, Is.Zero);
    }

    [Test] public async Task LegacyFetchReportsSourceCapForATwentyMinuteProducerRange()
    {
        var store = new FixtureStore(); var index = store.Index();
        index["producerRuns"]![0]!["ranges"]!["submitted:video:0"]!["last"]!["sdkPts100ns"] = FixtureStore.First + 1200UL * 10000000;
        store.Ready(index);
        var bundle = await new RetrievalService(store, new FixtureProbe()).FetchAsync(FixtureStore.Id, Fresh("legacy-source-cap"), default);
        Assert.That(bundle.Status, Is.EqualTo("incomplete")); Assert.That(bundle.Issues, Does.Contain("source_range_limit"));
        Assert.That(store.MediaReads, Is.Zero);
    }

    private static (FixtureStore Store, SelectedMappingTests.NavigationProbe Probe, ProducerWindow Window) LongSource(int minutes = 20)
    {
        ulong offset = (ulong)minutes * 60 * 1000000000;
        var (store, index, probe) = SelectedMappingTests.NavigationSource(offsetNs: offset, logSequence: 300);
        for (int n = 0; n < 300; n++) store.Add("log", n, Encoding.UTF8.GetBytes("outside selected window\n"));
        store.Ready(index);
        var first = TimingEvidence.Unix(100000000 + offset / 100);
        return (store, probe, ProducerWindow.Parse(first.ToString("o"), first.AddSeconds(1).ToString("o")));
    }
    private static async Task<(string First, string Last)> Pages(FixtureStore store, IMediaProbe probe)
    {
        var tool = new MetadataCatalog(store, probe); var first = Fresh("page-1"); var last = Fresh("page-2");
        var a = await tool.PageAsync(FixtureStore.Id, first, null, default);
        Assert.That(a.Summaries, Has.Count.EqualTo(256)); Assert.That(a.EnumerationComplete, Is.False); Assert.That(a.AtomicSnapshot, Is.False);
        var b = await tool.PageAsync(FixtureStore.Id, last, first, default);
        Assert.That(b.EnumerationComplete, Is.True); Assert.That(b.Status, Is.EqualTo("retrieved"));
        return (first, last);
    }

    [TestCase(20)] [TestCase(30)]
    public async Task BoundedPagesFetchLaterCumulativeWindowAndNavigateActualConstructedLog(int minutes)
    {
        var (store, probe, window) = LongSource(minutes); var pages = await Pages(store, probe);
        var output = Fresh("later-window");
        var bundle = await new MetadataCatalog(store, probe).FetchWindowAsync(FixtureStore.Id, pages.Last, output, window, default);
        Assert.That(bundle.Status, Is.EqualTo("retrieved"), string.Join(',', bundle.Issues));
        Assert.That(bundle.Objects.Count, Is.EqualTo(3)); Assert.That(bundle.Objects.Single(o => o.Kind == "log").Sequence, Is.EqualTo(300));
        Assert.That(bundle.LogGaps, Is.Empty); Assert.That(bundle.Media.Single().Fragments, Is.EqualTo(new[] { "navigation" }));
        var source = await LocalBundle.OpenAsync(pages.Last, default, "playreel.owner-metadata-page");
        Assert.That(bundle.Objects.Single(o => o.Kind == "time-index").Raw.Sha256,
            Is.EqualTo(source.Objects.Single(o => o.Kind == "time-index").Raw.Sha256), "No raw JSON projection/rewrite.");
        var estimate = await LogNavigation.LocateAsync(output, "logs-0000.log", "2", null, Fresh("later-query"), probe, default);
        Assert.That(estimate.Status, Is.EqualTo("estimated"), estimate.Reason);
        Assert.That(estimate.EvidenceProvenance, Is.EqualTo("constructed_fixture"));
        Assert.That(estimate.PlaybackOffsetVerified, Is.False); Assert.That(estimate.ContinuousCoverageClaim, Is.False);
        Assert.That(estimate.PhysicalCaptureOrEmissionErrorBoundNs, Is.Null);
        var strict = await LocalBundle.MapAsync(output, null, ((long)minutes * 60 * 1000000000 + 9900000).ToString(CultureInfo.InvariantCulture), default);
        Assert.That(strict.Status, Is.Not.EqualTo("mapped"));
    }

    [Test] public async Task IncompleteCatalogueAndWrongAdmissionGenerationOrSessionRefuse()
    {
        var (store, probe, window) = LongSource(); var tool = new MetadataCatalog(store, probe); var first = Fresh("partial-page");
        await tool.PageAsync(FixtureStore.Id, first, null, default);
        Assert.That(async () => await tool.FetchWindowAsync(FixtureStore.Id, first, Fresh("partial-window"), window, default),
            Throws.TypeOf<RetrievalError>().With.Message.EqualTo("metadata_catalog_incomplete"));
        var oldAdmission = store.State.AdmissionId; store.State.AdmissionId = new string('a', 32);
        Assert.That(async () => await tool.PageAsync(FixtureStore.Id, Fresh("wrong-admission"), first, default), Throws.TypeOf<RetrievalError>());
        store.State.AdmissionId = oldAdmission; store.State.Generation = 2;
        Assert.That(async () => await tool.PageAsync(FixtureStore.Id, Fresh("wrong-generation"), first, default), Throws.TypeOf<RetrievalError>());
        store.State.Generation = 1; store.State.SessionId = Guid.NewGuid();
        Assert.That(async () => await tool.PageAsync(store.State.SessionId, Fresh("wrong-session"), first, default), Throws.TypeOf<RetrievalError>());
        Assert.That(store.MediaReads, Is.Zero);
    }

    [TestCase("linked-page")] [TestCase("selected-raw")] [TestCase("selected-compressed")]
    public async Task TamperedPageOrSelectedPayloadCannotProduceWindow(string fault)
    {
        var (store, probe, window) = LongSource(); var pages = await Pages(store, probe);
        var target = fault == "linked-page" ? Path.Combine(pages.First, "page.json")
            : Path.Combine(pages.Last, fault == "selected-raw" ? "time-index-000000.raw" : "time-index-000000.gz");
        var bytes = await File.ReadAllBytesAsync(target); bytes[bytes.Length / 2] ^= 1; await File.WriteAllBytesAsync(target, bytes);
        Assert.That(async () => await new MetadataCatalog(store, probe).FetchWindowAsync(FixtureStore.Id, pages.Last, Fresh("tampered-window"), window, default),
            Throws.TypeOf<RetrievalError>());
        Assert.That(store.MediaReads, Is.Zero);
    }

    [TestCase("loss")] [TestCase("reset")]
    public async Task OutOfWindowLossOrResetIsNotHiddenBySelectingLaterIndexes(string fault)
    {
        var (store, probe, window) = LongSource();
        var old = store.Timing("manifest", 0);
        if (fault == "loss") old["droppedLogBytes"] = 1;
        else old["records"] = new JsonArray(new JsonObject { ["kind"] = "reset", ["producerRunId"] = FixtureStore.Run.ToString("D") });
        store.AddJson("manifest", 0, old); store.AddJson("manifest", 1, store.Timing("manifest", 1));
        var pages = await Pages(store, probe); var output = Fresh("loss-window");
        var bundle = await new MetadataCatalog(store, probe).FetchWindowAsync(FixtureStore.Id, pages.Last, output, window, default);
        Assert.That(bundle.Status, Is.EqualTo("incomplete")); Assert.That(bundle.Issues, Does.Contain("catalog_timing_loss_or_reset"));
        var nav = await LogNavigation.LocateAsync(output, "logs-0000.log", "2", null, Fresh("loss-query"), probe, default);
        Assert.That(nav.Status, Is.EqualTo("unavailable"));
    }

    [Test] public async Task AConflictingPersistedClaimOutsideTheWindowStillRefusesAttribution()
    {
        var (store, probe, window) = LongSource(); var old = store.Timing("manifest", 0);
        old["records"] = new JsonArray(new JsonObject { ["kind"] = "ack", ["producerRunId"] = Guid.NewGuid().ToString("D"),
            ["epoch"] = 0, ["upload"] = 9, ["fragment"] = "navigation", ["ackType"] = 3, ["result"] = 200, ["ackTimecodeMs"] = 1 });
        store.AddJson("manifest", 0, old); store.AddJson("manifest", 1, store.Timing("manifest", 1));
        var pages = await Pages(store, probe);
        Assert.That(async () => await new MetadataCatalog(store, probe).FetchWindowAsync(FixtureStore.Id, pages.Last, Fresh("ambiguous-window"), window, default),
            Throws.TypeOf<RetrievalError>().With.Message.EqualTo("ambiguous_fragment_claim"));
        Assert.That(store.MediaReads, Is.Zero);
    }

    [Test] public void ProducerWindowIsHalfOpenAndKeepsTheExistingTenMinuteCeiling()
    {
        var window = ProducerWindow.Parse("2026-10-19T00:20:00Z", "2026-10-19T00:30:00Z");
        Assert.That(window.Contains(window.From100ns / 10000), Is.True);
        Assert.That(window.Contains(window.To100ns / 10000), Is.False);
        Assert.That(() => ProducerWindow.Parse("2026-10-19T00:20:00Z", "2026-10-19T00:30:01Z"), Throws.TypeOf<RetrievalError>());
    }

    [Test] public async Task InsertionsBeforeTheCursorRemainExplicitlyNonAtomic()
    {
        var (store, probe, _) = LongSource(); var tool = new MetadataCatalog(store, probe);
        store.Payloads.Remove($"{store.State.Prefix}log/000000.gz");
        var first = Fresh("before-insertion"); await tool.PageAsync(FixtureStore.Id, first, null, default);
        // A lexically earlier key inserted after listing cannot be discovered by
        // the remaining cursor. Enumeration completion must never imply snapshot completeness.
        store.Add("log", 0, Encoding.UTF8.GetBytes("inserted after first page\n"));
        var last = await tool.PageAsync(FixtureStore.Id, Fresh("after-insertion"), first, default);
        Assert.That(last.EnumerationComplete, Is.True); Assert.That(last.AtomicSnapshot, Is.False);
        Assert.That(last.SnapshotMeaning, Does.Contain("insertions before the cursor may be absent"));
        Assert.That(last.Summaries.Any(s => s.Kind == "log" && s.Sequence == 0), Is.False);
    }

    [Test] public async Task HistoricalClaimsAndNavigationSnapshotsAreSelectedBeforeAggregateCaps()
    {
        var (store, probe, window) = LongSource();
        // Six individually bounded indexes exceed 4096 claims and 1024
        // navigation snapshots in total. Only the neighboring index is relevant
        // to this later window; its historical ACKs must not enter the aggregate.
        var (_, source, _) = SelectedMappingTests.NavigationSource();
        for (var sequence = 1; sequence <= 6; sequence++) {
            var old = (JsonObject)source.DeepClone(); old["sequence"] = sequence;
            var run = old["producerRuns"]![0]!.DeepClone();
            old["producerRuns"] = new JsonArray(Enumerable.Range(0, 200).Select(_ => run.DeepClone()).ToArray());
            old["records"] = new JsonArray(Enumerable.Range(0, 1000).Select(n => (JsonNode)new JsonObject {
                ["kind"] = "ack", ["producerRunId"] = FixtureStore.Run.ToString("D"), ["epoch"] = 0,
                ["upload"] = 0, ["fragment"] = $"old-{sequence}-{n}", ["ackType"] = 3, ["result"] = 200, ["ackTimecodeMs"] = 1 }).ToArray());
            store.AddJson("time-index", sequence, old);
        }
        var pages = await Pages(store, probe); var output = Fresh("dense-history-window");
        var bundle = await new MetadataCatalog(store, probe).FetchWindowAsync(FixtureStore.Id, pages.Last, output, window, default);
        Assert.That(bundle.Status, Is.EqualTo("retrieved"), string.Join(',', bundle.Issues));
        var nav = await LogNavigation.LocateAsync(output, "logs-0000.log", "2", null, Fresh("dense-history-query"), probe, default);
        Assert.That(nav.Status, Is.EqualTo("estimated"), nav.Reason);
    }

    [Test] public async Task MissingCurrentReceiptCannotBeHiddenByVerifiedLocalPages()
    {
        var (store, probe, window) = LongSource(); var pages = await Pages(store, probe);
        store.Receipts.Remove("log:300");
        var bundle = await new MetadataCatalog(store, probe).FetchWindowAsync(FixtureStore.Id, pages.Last, Fresh("missing-receipt"), window, default);
        Assert.That(bundle.Status, Is.EqualTo("incomplete")); Assert.That(bundle.Objects.Any(o => o.Kind == "log"), Is.False);
    }
}
