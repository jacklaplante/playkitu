using NUnit.Framework;
using Playreel.Retrieval;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Retrieval.Tests;

[TestFixture]
public sealed class RetrievalTests
{
    private string root = "";
    [SetUp] public void SetUp() { root = Path.Combine(Path.GetTempPath(), "sbrec-retrieval-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TearDown] public void TearDown()
    {
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "sbrec-retrieval-test-"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected temporary test directory.");
        Directory.Delete(resolved, true);
    }
    private string Output => Path.Combine(root, "bundle");
    private async Task<RetrievalBundle> Fetch(FixtureStore store, RetrievalLimits? limits = null, FixtureProbe? probe = null) =>
        await new RetrievalService(store, probe ?? new FixtureProbe(), limits).FetchAsync(FixtureStore.Id, Output, CancellationToken.None);

    [Test] public async Task FullReceiptVerifiedRetrievalPreservesSplitUtf8AndMultilineLogs()
    {
        var store = new FixtureStore(); store.Ready();
        var bytes = Encoding.UTF8.GetBytes("line α\nException:\n  at X\n"); var split = Array.IndexOf(bytes, (byte)0xCE) + 1;
        store.Add("log", 0, bytes[..split]); store.Add("log", 1, bytes[split..]);
        var result = await Fetch(store);
        Assert.Multiple(() => { Assert.That(result.Status, Is.EqualTo("retrieved")); Assert.That(result.Media, Has.Count.EqualTo(1)); Assert.That(result.Coverage, Is.EqualTo("unverified")); Assert.That(store.RequestedFragments.Single(), Is.EqualTo(new[] { "frag1" })); });
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(Output, result.LogParts.Single().File)), Is.EqualTo(bytes));
        var inspection = JsonSerializer.Serialize(await LocalBundle.InspectAsync(Output, CancellationToken.None));
        Assert.That(inspection, Does.Contain("False").IgnoreCase.Or.Contain("false"));
        var mapping = JsonSerializer.Serialize(await LocalBundle.MapAsync(Output, "2026-10-15T12:00:01Z", null, CancellationToken.None));
        Assert.That(mapping, Does.Contain("producer_to_playback_anchor_unavailable").And.Contain("1000000000"));
        var evidence = Environment.GetEnvironmentVariable("SBREC_RETRIEVAL_TEST_ROOT");
        if (!string.IsNullOrEmpty(evidence))
        {
            var saved = Path.Combine(evidence, "cli-bundle"); Directory.CreateDirectory(saved);
            foreach (var file in Directory.EnumerateFiles(Output)) File.Copy(file, Path.Combine(saved, Path.GetFileName(file)), false);
        }
    }
    [Test] public async Task MissingLogSequenceCreatesSeparateReadableParts()
    {
        var s = new FixtureStore(); s.Ready(); s.Add("log", 0, "one"u8.ToArray()); s.Add("log", 2, "three"u8.ToArray());
        var b = await Fetch(s); Assert.That(b.LogParts, Has.Count.EqualTo(2)); Assert.That(b.LogGaps.Any(g => g.Sequence == 1), Is.True);
    }
    [Test] public async Task RejectedInputInsertionSplitsWithoutInventingBytesOrNewlines()
    {
        var s = new FixtureStore(); var index = s.Index(); var records = index["records"]!.AsArray();
        records.Add(new JsonObject { ["kind"] = "log-range", ["logSequence"] = 0, ["byteStart"] = 0, ["byteLength"] = 6, ["elapsedStartNs"] = 0, ["elapsedEndNs"] = 1 });
        records.Add(new JsonObject { ["kind"] = "gap", ["reason"] = "raw_rejected", ["logBytes"] = 9, ["acceptedLogByteOffset"] = 3, ["objectSequence"] = null });
        s.Ready(index); s.Add("log", 0, "abcdef"u8.ToArray()); var b = await Fetch(s);
        Assert.That(b.LogParts, Has.Count.EqualTo(2)); Assert.That(await File.ReadAllTextAsync(Path.Combine(Output, b.LogParts[0].File)), Is.EqualTo("abc"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(Output, b.LogParts[1].File)), Is.EqualTo("def"));
    }
    [Test] public async Task UnplacedRejectedInputGapDoesNotJoinBatches()
    {
        var s = new FixtureStore(); var index = s.Index();
        index["records"]!.AsArray().Add(new JsonObject { ["kind"] = "gap", ["reason"] = "raw_rejected", ["logBytes"] = 9, ["acceptedLogByteOffset"] = 3, ["objectSequence"] = null });
        s.Ready(index); s.Add("log", 0, "abc"u8.ToArray()); s.Add("log", 1, "def"u8.ToArray());
        var b = await Fetch(s); Assert.That(b.LogParts, Has.Count.EqualTo(2)); Assert.That(b.Issues, Does.Contain("unplaced_log_gap"));
    }
    [TestCase(true)][TestCase(false)]
    public async Task ActualWriterEvictedGapStillSeparatesBatches(bool includeIndex)
    {
        // Exact independent-review captures from RecordingTiming: LogRange0,
        // Drop(input_overflow,9,offset3), LogRange1, then48 distinct ACKs evict
        // all three rows before TakeIndex. The manifest repeats cumulative9.
        var s = new FixtureStore();
        if (includeIndex) s.Add("time-index", 0, await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/canonical-evicted-index.json")));
        s.Add("manifest", 0, await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/canonical-evicted-manifest.json")));
        s.Add("log", 0, "abc"u8.ToArray()); s.Add("log", 1, "def"u8.ToArray());
        var b = await Fetch(s);
        Assert.That(b.LogGaps.Single(), Is.EqualTo(new LogGap(null, 9, null, "cumulative_loss_without_position")));
        Assert.That(b.LogParts, Has.Count.EqualTo(2));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(Output, b.LogParts[0].File)), Is.EqualTo("abc"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(Output, b.LogParts[1].File)), Is.EqualTo("def"));
        Assert.That(b.Issues, Does.Contain("unplaced_log_gap").And.Contain("within_batch_log_continuity_unknown"));
        var evidence = Environment.GetEnvironmentVariable("SBREC_RETRIEVAL_TEST_ROOT");
        if (includeIndex && !string.IsNullOrEmpty(evidence))
        {
            var saved = Path.Combine(evidence, "evicted-gap-bundle"); Directory.CreateDirectory(saved);
            foreach (var file in Directory.EnumerateFiles(Output)) File.Copy(file, Path.Combine(saved, Path.GetFileName(file)), false);
        }
    }
    [TestCase(9UL, 0UL)][TestCase(18UL, 9UL)]
    public async Task PositionedLossOverlappingCumulativeSnapshotsIsCountedOnce(ulong total, ulong unknown)
    {
        var s = new FixtureStore(); var index = s.Index(); index["droppedLogBytes"] = 9;
        var records = index["records"]!.AsArray();
        for (var n = 0; n < 3; n++) records.Add(new JsonObject { ["kind"] = "log-range", ["logSequence"] = n, ["byteStart"] = n * 3,
            ["byteLength"] = 3, ["elapsedStartNs"] = n, ["elapsedEndNs"] = n + 1 });
        records.Add(new JsonObject { ["kind"] = "gap", ["reason"] = "input_overflow", ["logBytes"] = 9, ["acceptedLogByteOffset"] = 3, ["objectSequence"] = null });
        s.Ready(index);
        var laterIndex = s.Timing("time-index", 1); laterIndex["droppedLogBytes"] = total; s.AddJson("time-index", 1, laterIndex);
        var olderManifest = s.Timing("manifest", 0); olderManifest["droppedLogBytes"] = 9; s.AddJson("manifest", 0, olderManifest);
        var laterManifest = s.Timing("manifest", 1); laterManifest["droppedLogBytes"] = total; s.AddJson("manifest", 1, laterManifest);
        s.Add("log", 0, "abc"u8.ToArray()); s.Add("log", 1, "def"u8.ToArray()); s.Add("log", 2, "ghi"u8.ToArray());
        var b = await Fetch(s);
        Assert.That(b.LogGaps.Aggregate(0UL, (sum, gap) => checked(sum + gap.Bytes)), Is.EqualTo(total));
        Assert.That(b.LogGaps.Where(g => g.Reason == "cumulative_loss_without_position").Select(g => g.Bytes).DefaultIfEmpty(0UL).Single(), Is.EqualTo(unknown));
        Assert.That(b.LogParts.Count, Is.EqualTo(unknown == 0 ? 2 : 3));
        Assert.That(b.Issues.Contains("within_batch_log_continuity_unknown"), Is.EqualTo(unknown > 0));
    }
    [TestCase(9007199254740993UL)][TestCase(18446744073709551614UL)]
    public async Task CumulativeLossCountsRemainExactUnsignedIntegers(ulong total)
    {
        var s = new FixtureStore(); var index = s.Index(); index["droppedLogBytes"] = total; s.Ready(index);
        var manifest = s.Timing("manifest", 0); manifest["droppedLogBytes"] = total; s.AddJson("manifest", 0, manifest);
        s.Add("log", 0, "abc"u8.ToArray()); s.Add("log", 1, "def"u8.ToArray());
        var b = await Fetch(s); Assert.That(b.LogGaps.Single().Bytes, Is.EqualTo(total)); Assert.That(b.LogParts, Has.Count.EqualTo(2));
        using var saved = TimingEvidence.Parse(await File.ReadAllBytesAsync(Path.Combine(Output, "bundle.json")));
        Assert.That(saved.RootElement.GetProperty("logGaps")[0].GetProperty("bytes").GetUInt64(), Is.EqualTo(total));
        Assert.That((await LocalBundle.OpenAsync(Output, CancellationToken.None)).LogGaps.Single().Bytes, Is.EqualTo(total));
    }
    [Test] public async Task ZeroCumulativeLossKeepsKnownAdjacentBatchesTogether()
    {
        var s = new FixtureStore(); s.Ready(); s.Add("log", 0, "abc"u8.ToArray()); s.Add("log", 1, "def"u8.ToArray());
        var b = await Fetch(s); Assert.That(b.LogGaps, Is.Empty); Assert.That(b.LogParts, Has.Count.EqualTo(1));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(Output, b.LogParts.Single().File)), Is.EqualTo("abcdef"));
        Assert.That(b.Issues, Does.Not.Contain("within_batch_log_continuity_unknown"));
    }
    [Test] public async Task ConflictingAckClocksStopMediaEvenWithSameOwner()
    {
        var s = new FixtureStore(); var index = s.Index(); var ack = index["records"]![0]!.DeepClone();
        ack["ackTimecodeMs"] = FixtureStore.First / 10000 + 1; index["records"]!.AsArray().Add(ack); s.Ready(index);
        var b = await Fetch(s); Assert.That(s.MediaReads, Is.Zero); Assert.That(b.Issues, Does.Contain("ambiguous_fragment_claim"));
    }
    [TestCase("admission")][TestCase("key")][TestCase("length")][TestCase("checksum")]
    public async Task StaleOrChangedReceiptsNeverPublishRawLogs(string error)
    {
        var s = new FixtureStore(); s.Ready(); s.Add("log", 0, "secret fixture"u8.ToArray()); var r = s.Receipts["log:0"];
        s.Receipts["log:0"] = error switch { "admission" => r with { AdmissionId = "older-admission" }, "key" => r with { Key = "sessions/other/log/000000.gz" },
            "length" => r with { Descriptor = r.Descriptor with { CompressedBytes = r.Descriptor.CompressedBytes + 1 } }, _ => r with { Descriptor = r.Descriptor with { Sha256Base64 = Convert.ToBase64String(new byte[32]) } } };
        var b = await Fetch(s); Assert.That(b.Objects.Any(o => o.Kind == "log"), Is.False); Assert.That(b.Issues.Count, Is.GreaterThan(0));
    }
    [Test] public async Task TraversalAndWrongPrefixNeverBecomeLocalPaths()
    {
        var s = new FixtureStore(); s.EntriesOverride = [new(s.State.Prefix + "log/../../escape", 1)]; var b = await Fetch(s);
        Assert.That(s.ObjectReads, Is.Zero); Assert.That(b.Issues, Does.Contain("unexpected_object_key")); Assert.That(Directory.GetFiles(root), Is.Empty);
    }
    [Test] public async Task WrongManifestGenerationIsExcluded()
    {
        var s = new FixtureStore(); s.Ready(); var manifest = s.Timing("manifest", 1); manifest["admissionGeneration"] = 2; s.AddJson("manifest", 1, manifest);
        var b = await Fetch(s); Assert.That(b.Objects.Any(o => o.Kind == "manifest" && o.Sequence == 1), Is.False);
    }
    [Test] public async Task GzipExpansionStopsAtConfiguredOutputBound()
    {
        var s = new FixtureStore(); s.Add("log", 0, new byte[100000]);
        var b = await Fetch(s, new() { ExpandedObjectBytes = 1000 }); Assert.That(b.Issues, Does.Contain("decompression_limit"));
        Assert.That(b.Objects, Is.Empty); Assert.That(new FileInfo(Directory.GetFiles(Output, "*.raw.partial").Single()).Length, Is.LessThanOrEqualTo(1000));
    }
    [TestCase(2, 200)] [TestCase(4, 200)] [TestCase(3, 0)] [TestCase(3, 201)] [TestCase(3, 403)]
    public async Task NonPersistedOrUnsuccessfulAckDoesNotAuthorizeFragmentAttribution(int type, int result)
    {
        var s = new FixtureStore(); var index = s.Index(); index["records"]![0]!["ackType"] = type; index["records"]![0]!["result"] = result; s.Ready(index);
        var b = await Fetch(s); Assert.That(s.MediaReads, Is.Zero); Assert.That(b.Issues, Does.Contain("media_attribution_unavailable"));
    }
    [TestCase("normal", 12)] [TestCase("forced", 1)]
    public async Task ActualGeneratedPersistedAcksSelectOnlyTheirListedFragments(string name, int expected)
    {
        // Generated-run ACKs and bounded ListFragments metadata, not invented
        // zero-result success. Media bytes/probing remain fake: this proves
        // attribution only, never playback, decoded content or tail coverage.
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/generated-persisted-acks.json")))![name]!;
        var s = new FixtureStore(); var index = s.Index();
        var run = index["producerRuns"]![0]!; run["producerRunId"] = fixture["runId"]!.GetValue<string>();
        run["ranges"]!["submitted:video:0"]!["first"]!["sdkPts100ns"] = fixture["first100ns"]!.GetValue<ulong>();
        run["ranges"]!["submitted:video:0"]!["last"]!["sdkPts100ns"] = fixture["last100ns"]!.GetValue<ulong>();
        index["records"] = fixture["acks"]!.DeepClone();
        s.Fragments = fixture["fragments"]!.AsArray().Select(f => new FragmentEntry(f!["id"]!.GetValue<string>(),
            DateTimeOffset.Parse(f["producer"]!.GetValue<string>()), f["durationMs"]!.GetValue<long>(), f["bytes"]!.GetValue<long>())).ToList();
        s.Ready(index); var bundle = await Fetch(s);
        var selected = s.RequestedFragments.SelectMany(ids => ids).ToArray();
        Assert.That(selected, Has.Length.EqualTo(expected));
        Assert.That(selected, Is.EquivalentTo(fixture["acks"]!.AsArray().Select(a => a!["fragment"]!.GetValue<string>())));
        Assert.That(bundle.Issues, Does.Not.Contain("media_attribution_unavailable").And.Not.Contain("fragment_timecode_mismatch"));
    }
    [Test] public async Task NeighborFragmentWithoutMatchingClaimIsNeverDownloaded()
    {
        var s = new FixtureStore(); s.Ready(); s.Fragments.Add(new("neighbor", TimingEvidence.Unix(FixtureStore.First + 1000000), 1000, 10));
        await Fetch(s); Assert.That(s.RequestedFragments.Single(), Is.EqualTo(new[] { "frag1" }));
    }
    [TestCase(0, true, null)]
    [TestCase(-9999, true, null)]
    [TestCase(-10000, false, "fragment_range_mismatch")]
    [TestCase(10002, false, "fragment_timecode_mismatch")]
    public async Task PinnedSdkTimestampRoundingUsesTheSameBoundedMetadataTolerance(long extraTicks, bool expectedMedia, string? issue)
    {
        // Exact numeric timestamp from the generated normal run. Exercise the
        // pinned AWS response parser itself, with no HTTP/client/credential path.
        const string json = """{"Fragments":[{"FragmentNumber":"frag1","ProducerTimestamp":1792157037.346,"ServerTimestamp":1792157037.432,"FragmentLengthInMilliseconds":999,"FragmentSizeInBytes":278185}]}""";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var context = new Amazon.Runtime.Internal.Transform.JsonUnmarshallerContext(input, false, null!, false);
        var parsed = (Amazon.KinesisVideoArchivedMedia.Model.ListFragmentsResponse)
            Amazon.KinesisVideoArchivedMedia.Model.Internal.MarshallTransformations.ListFragmentsResponseUnmarshaller.Instance.Unmarshall(context);
        var fragment = parsed.Fragments.Single();
        var producer = new DateTimeOffset(DateTime.SpecifyKind(fragment.ProducerTimestamp!.Value, DateTimeKind.Utc));
        var begin = TimingEvidence.Unix(17921570373460000UL);
        Assert.That(producer.UtcTicks - begin.UtcTicks, Is.EqualTo(-1), "Pinned SDK .346 numeric timestamp rounds100ns before the exact range boundary.");
        var s = new FixtureStore(); var index = s.Index();
        var range = index["producerRuns"]![0]!["ranges"]!["submitted:video:0"]!;
        range["first"]!["sdkPts100ns"] = 17921570373468794UL;
        range["last"]!["sdkPts100ns"] = 17921570383468794UL;
        index["records"]![0]!["ackTimecodeMs"] = 1792157037346UL;
        s.Fragments = [new(fragment.FragmentNumber, producer.AddTicks(extraTicks), fragment.FragmentLengthInMilliseconds!.Value, fragment.FragmentSizeInBytes!.Value)];
        s.Ready(index); var bundle = await Fetch(s);
        Assert.That(s.MediaReads, Is.EqualTo(expectedMedia ? 1 : 0));
        if (issue is not null) Assert.That(bundle.Issues, Does.Contain(issue));
        else Assert.That(bundle.Issues, Does.Not.Contain("fragment_range_mismatch").And.Not.Contain("fragment_timecode_mismatch"));
    }
    [Test] public async Task ConflictingFragmentClaimsStopMedia()
    {
        var s = new FixtureStore(); var i = s.Index(); var ack = i["records"]![0]!.DeepClone(); ack["upload"] = 2; i["records"]!.AsArray().Add(ack); s.Ready(i);
        var b = await Fetch(s); Assert.That(s.MediaReads, Is.Zero); Assert.That(b.Issues, Does.Contain("ambiguous_fragment_claim"));
    }
    [Test] public async Task OversizedFragmentIsReportedWithoutDownloading()
    {
        var s = new FixtureStore(); s.Ready(); s.Fragments[0] = s.Fragments[0] with { Bytes = 49 * RetrievalLimits.MiB };
        var b = await Fetch(s); Assert.That(s.MediaReads, Is.Zero); Assert.That(b.Issues, Does.Contain("fragment_exceeds_batch_limits"));
    }
    [Test] public async Task MediaActualByteCapRejectsLyingAdvertisedSize()
    {
        var s = new FixtureStore(); s.Ready(); s.Media = new byte[100]; s.Fragments[0] = s.Fragments[0] with { Bytes = 10 };
        var b = await Fetch(s, new() { MediaFileBytes = 64, AdvertisedBatchBytes = 48 });
        Assert.That(b.Issues, Does.Contain("download_limit")); Assert.That(Directory.GetFiles(Output, "*.mkv"), Is.Empty);
    }
    [Test] public async Task DeadlineCancelsFakeMediaAndKeepsIncompleteBundle()
    {
        var s = new FixtureStore { BlockMedia = true }; s.Ready();
        var b = await Fetch(s, new() { OverallSeconds = 1 }); Assert.That(b.Issues, Does.Contain("deadline_or_cancelled")); Assert.That(b.Status, Is.EqualTo("incomplete"));
    }
    [Test] public void ExistingDirectoryRejectedBeforeAnyRead()
    {
        Directory.CreateDirectory(Output); var s = new FixtureStore();
        Assert.That(async () => await Fetch(s), Throws.TypeOf<RetrievalError>().With.Message.EqualTo("output_exists")); Assert.That(s.SessionReads, Is.Zero);
    }
    [Test] public async Task TamperedSavedFileAndPathEscapeAreRejectedLocally()
    {
        var s = new FixtureStore(); s.Ready(); var b = await Fetch(s);
        await File.AppendAllTextAsync(Path.Combine(Output, b.Objects[0].Raw.File), "changed");
        Assert.That(async () => await LocalBundle.OpenAsync(Output, CancellationToken.None), Throws.TypeOf<RetrievalError>().With.Message.EqualTo("local_file_mismatch"));
        Assert.That(() => RetrievalService.LocalFile(Output, "../escape"), Throws.TypeOf<RetrievalError>());
    }
    [Test] public async Task LocalMapNeedsNoProfileAndRejectsTimezoneGuess()
    {
        var s = new FixtureStore(); s.Ready(); await Fetch(s);
        Assert.That(async () => await LocalBundle.MapAsync(Output, "2026-10-15T12:00:00", null, CancellationToken.None), Throws.TypeOf<RetrievalError>().With.Message.EqualTo("timestamp_offset_required"));
        Assert.That(await Program.Main(["map", "--directory", Output, "--elapsed-ns", "0"]), Is.EqualTo(3));
    }
    [Test] public void AcceptedTimingFixturesPreserveUnsignedValuesAndUnknowns()
    {
        var e = new TimingEvidence();
        foreach (var name in new[] { "offset", "reset" })
        {
            using var doc = TimingEvidence.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, $"Fixtures/timing-schema1-{name}.json")));
            e.Add(doc.RootElement, FixtureStore.Id, 1, "time-index", name == "offset" ? 0 : 1);
        }
        Assert.That(e.Claims, Has.Count.EqualTo(2)); Assert.That(e.Claims.All(c => c.Upload == ulong.MaxValue - 1), Is.True);
        Assert.That(e.Claims[1].TimecodeMs, Is.Null); Assert.That(e.Uncertainties, Does.Contain("reset"));
    }
    [TestCase("-1")][TestCase("1.5")][TestCase("18446744073709551616")]
    public void InvalidUnsignedTimingNeverRounds(string value)
    { using var doc = TimingEvidence.Parse(Encoding.UTF8.GetBytes("{\"x\":" + value + "}")); Assert.That(() => TimingEvidence.Unsigned(doc.RootElement, "x"), Throws.TypeOf<RetrievalError>()); }
    [Test] public void AcceptedProbeRemainsUnverifiedDespiteSuccessfulDecode()
    {
        using var doc = TimingEvidence.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/sdk-decoded-probe-v1.json")));
        var result = JsonSerializer.Serialize(ProbeEvidence.Summary(doc.RootElement)); Assert.That(result, Does.Contain("playbackOffsetVerified\":false"));
        Assert.That(ProbeEvidence.Decimal(doc.RootElement.GetProperty("tracks")[0].GetProperty("decoded"), "firstPtsNs"), Is.EqualTo(1792073107910000000UL));
    }
    [Test] public void OverlappingRunsAndUnboundedSourceRangesAreRejected()
    {
        var e = new TimingEvidence(); e.Ranges.Add(new(FixtureStore.Run, 0, FixtureStore.First, FixtureStore.First + 10000000));
        e.Ranges.Add(new(Guid.NewGuid(), 0, FixtureStore.First, FixtureStore.First + 5000000));
        Assert.That(() => e.MergedRanges(new()), Throws.TypeOf<RetrievalError>().With.Message.EqualTo("ambiguous_producer_ranges"));
        e.Ranges.Clear(); e.Ranges.Add(new(FixtureStore.Run, 0, FixtureStore.First, FixtureStore.First + 6010000000));
        Assert.That(() => e.MergedRanges(new()), Throws.TypeOf<RetrievalError>().With.Message.EqualTo("source_range_limit"));
    }
    [Test] public async Task TransferBudgetChargesAcrossMultipleFiles()
    {
        var budget = new TransferBudget(new() { DownloadBytes = 5 });
        await budget.CopyAsync(new MemoryStream(new byte[3]), Stream.Null, 10, false, CancellationToken.None);
        Assert.That(async () => await budget.CopyAsync(new MemoryStream(new byte[3]), Stream.Null, 10, false, CancellationToken.None), Throws.TypeOf<RetrievalError>());
        Assert.That(budget.Downloaded, Is.EqualTo(6));
    }
    [Test] public async Task NativeProbeRunsOnlyAgainstExplicitExistingLocalFixture()
    {
        var fixture = Environment.GetEnvironmentVariable("SBREC_RETRIEVAL_PROBE_FIXTURE");
        var tools = Environment.GetEnvironmentVariable("SBREC_RETRIEVAL_TOOL_ROOT");
        if (string.IsNullOrEmpty(fixture) || string.IsNullOrEmpty(tools)) Assert.Ignore("No explicit local native fixture selected.");
        var output = Path.Combine(root, "actual-probe.json");
        Assert.That(await new ProbeProcess(tools!, Environment.GetEnvironmentVariable("SBREC_RETRIEVAL_NATIVE_RUNTIME")).RunAsync(fixture!, output, CancellationToken.None), Is.Zero);
        using var doc = TimingEvidence.Parse(await File.ReadAllBytesAsync(output));
        Assert.That(TimingEvidence.Text(doc.RootElement, "fileSha256"), Is.EqualTo(await RetrievalService.HashAsync(fixture!, CancellationToken.None)));
        Assert.That(doc.RootElement.GetProperty("producerAnchor").GetProperty("available").GetBoolean(), Is.False);
        _ = ProbeEvidence.Summary(doc.RootElement);
        var evidence = Environment.GetEnvironmentVariable("SBREC_RETRIEVAL_TEST_ROOT");
        if (!string.IsNullOrEmpty(evidence)) File.Copy(output, Path.Combine(evidence, "native-probe.json"), false);
    }
    [Test] public async Task IncompleteProbeCannotBePromotedByEosOrExitSuccess()
    {
        var s = new FixtureStore(); s.Ready(); var b = await Fetch(s, probe: new FixtureProbe { Exit = 3 });
        Assert.That(b.Status, Is.EqualTo("incomplete")); Assert.That(b.Issues, Does.Contain("probe_incomplete"));
        Assert.That(b.Media.Single().Media.File, Does.EndWith(".mkv"));
    }
    [Test] public async Task WrongProbeHashLeavesMediaMarkedIncomplete()
    {
        var s = new FixtureStore(); s.Ready(); var b = await Fetch(s, probe: new FixtureProbe { WrongHash = true });
        Assert.That(b.Media, Has.Count.EqualTo(1)); Assert.That(b.Media[0].Probe, Is.Null); Assert.That(b.Issues, Does.Contain("probe_file_mismatch"));
    }
    [Test] public async Task ReferencedButAbsentTailObjectIsNotSilentlyComplete()
    {
        var s = new FixtureStore(); s.Ready(); var m = s.Timing("manifest", 1); m["latestPostConfirmed"] = new JsonObject { ["log"] = 7 }; s.AddJson("manifest", 1, m);
        var b = await Fetch(s); Assert.That(b.LogGaps.Any(g => g.Sequence == 7 && g.Reason == "referenced_object_unavailable"), Is.True);
        Assert.That(b.Issues, Does.Contain("referenced_log_unavailable"));
    }
    [Test] public async Task FailedSdkSubmissionEndpointsCannotSelectMedia()
    {
        var s = new FixtureStore(); var i = s.Index(); i["producerRuns"]![0]!["ranges"]!["submitted:video:0"]!["first"]!["result"] = 1; s.Ready(i);
        var b = await Fetch(s); Assert.That(s.MediaReads, Is.Zero); Assert.That(b.Issues, Does.Contain("unconfirmed_submitted_range"));
    }
    [Test] public async Task MediaIsPartitionedBeforeItExceedsTwentySecondBatch()
    {
        var s = new FixtureStore(); var i = s.Index();
        i["producerRuns"]![0]!["ranges"]!["submitted:video:0"]!["last"]!["sdkPts100ns"] = FixtureStore.First + 200000000;
        var ack = i["records"]![0]!.DeepClone(); ack["fragment"] = "frag2"; ack["ackTimecodeMs"] = FixtureStore.First / 10000 + 19000;
        i["records"]!.AsArray().Add(ack); s.Ready(i);
        s.Fragments.Add(new("frag2", TimingEvidence.Unix(FixtureStore.First + 190000000), 2000, 32));
        var b = await Fetch(s); Assert.That(b.Media, Has.Count.EqualTo(2)); Assert.That(s.RequestedFragments.All(f => f.Count == 1), Is.True);
    }
    [Test] public async Task MetadataObjectLimitStopsBeforeReadingPayloads()
    {
        var s = new FixtureStore { EntriesOverride = Enumerable.Range(0, 257).Select(i => new ObjectEntry($"fixture-{i}", 1)).ToList() };
        var b = await Fetch(s); Assert.That(b.Issues, Does.Contain("object_limit")); Assert.That(s.ObjectReads, Is.Zero);
    }
    [Test] public void DuplicateJsonPropertiesAndProbeNumberEncodingAreRejected()
    {
        Assert.That(() => TimingEvidence.Parse("{\"x\":1,\"x\":2}"u8.ToArray()), Throws.TypeOf<JsonException>());
        using var d = TimingEvidence.Parse("{\"buffers\":12}"u8.ToArray());
        Assert.That(() => ProbeEvidence.Decimal(d.RootElement, "buffers"), Throws.TypeOf<RetrievalError>());
    }
}
