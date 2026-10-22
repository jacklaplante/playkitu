using NUnit.Framework;
using Playreel.Retrieval;
using Playreel.Issuer;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Retrieval.Tests;

[TestFixture]
public sealed class SelectedMappingTests
{
    private string root = "";
    [SetUp] public void Begin() { root = Path.Combine(Path.GetTempPath(), "sbrec-selected-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TearDown] public void End()
    {
        var path = Path.GetFullPath(root);
        if (!path.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "sbrec-selected-test-"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(path, true);
    }
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    // Two constructed observed frames isolate navigation from sparse generated
    // clock-group mapping. No live log is rebound to historical fixture media.
    internal sealed class NavigationProbe : IMediaProbe
    {
        public bool Gap, Wide, QueryFails; public int Calls;
        public ulong FirstPtsNs = 10000000000;
        public async Task<int> RunAsync(string input, string output, CancellationToken ct, IReadOnlyList<ulong>? selectedTargets = null)
        {
            Calls++;
            if (QueryFails && selectedTargets is { Count: > 0 }) return 3;
            ulong first = FirstPtsNs; const ulong duration = 33333333;
            var spans = new JsonArray(); var queries = new JsonArray();
            for (int n = 0; n < 2; n++)
            {
                ulong local = (ulong)n * (Wide ? 2000000000UL : duration), pts = first + local, length = Gap && n == 0 ? 5000000UL : duration;
                var span = new JsonObject { ["segmentIndex"] = 1, ["buffers"] = "1", ["lastBufferDurationNs"] = length.ToString(CultureInfo.InvariantCulture) };
                foreach (var coordinate in new[] { "PtsNs", "RunningNs", "StreamNs" })
                {
                    ulong value = coordinate == "PtsNs" ? pts : local;
                    span["first" + coordinate] = value.ToString(CultureInfo.InvariantCulture); span["last" + coordinate] = value.ToString(CultureInfo.InvariantCulture);
                    span["observedEnd" + coordinate] = (value + length).ToString(CultureInfo.InvariantCulture);
                }
                spans.Add(span);
                if (selectedTargets?.Contains(pts) == true) queries.Add(new JsonObject { ["queryId"] = (ulong)n + 1,
                    ["targetPtsNs"] = pts.ToString(CultureInfo.InvariantCulture), ["toleranceNs"] = "1000000", ["matchCount"] = "1", ["status"] = "unique_observed",
                    ["bufferOrdinal"] = (n + 1).ToString(CultureInfo.InvariantCulture), ["segmentIndex"] = 1, ["ptsNs"] = pts.ToString(CultureInfo.InvariantCulture),
                    ["runningNs"] = local.ToString(CultureInfo.InvariantCulture), ["streamNs"] = local.ToString(CultureInfo.InvariantCulture), ["durationNs"] = length.ToString(CultureInfo.InvariantCulture) });
            }
            var decoded = new JsonObject { ["buffers"] = "2", ["eosObserved"] = true, ["spans"] = spans,
                ["segments"] = new JsonArray(new JsonObject { ["format"] = 3, ["rate"] = 1, ["appliedRate"] = 1 }) };
            var data = new JsonObject { ["schema"] = 1, ["kind"] = "playreel.decoded-media-probe", ["status"] = "decoded", ["reason"] = "", ["decodeEos"] = true,
                ["fileSha256"] = await RetrievalService.HashAsync(input, ct), ["fileBytes"] = new FileInfo(input).Length.ToString(CultureInfo.InvariantCulture),
                ["tracks"] = new JsonArray(new JsonObject { ["kind"] = "video", ["decoded"] = decoded, ["demux"] = decoded.DeepClone() }),
                ["frameSelections"] = new JsonObject { ["schema"] = 1, ["scope"] = "observed_file", ["queries"] = queries } };
            await File.WriteAllTextAsync(output, data.ToJsonString(), ct); return 0;
        }
    }
    internal static (FixtureStore Store, JsonObject Index, NavigationProbe Probe) NavigationSource(string fault = "normal", ulong offsetNs = 0, int logSequence = 0)
    {
        var store = new FixtureStore(); var index = store.Timing("time-index", 0); string run = FixtureStore.Run.ToString("D");
        JsonObject Input(ulong qpc, ulong pts) => new() { ["result"] = 0, ["flags"] = 1, ["provenance"] = 3, ["captureQpc"] = qpc, ["ptsNs"] = pts };
        JsonObject Submitted(ulong pts, ulong running) => new() { ["result"] = 0, ["sdkPts100ns"] = pts, ["runningNs"] = running };
        var bridge = new JsonObject { ["kind"] = "clock", ["producerRunId"] = run, ["epoch"] = 0, ["managedTicks"] = 1000,
            ["managedAfterTicks"] = 1001, ["managedFrequencyHz"] = 10000000, ["managedBeforeElapsedNs"] = 0, ["managedAfterElapsedNs"] = 100,
            ["correspondenceQpc"] = 2000, ["correspondenceUncertainty100ns"] = 1,
            ["observationClock"] = new JsonObject { ["qpc"] = 2000, ["frequencyHz"] = 10000000, ["uncertainty100ns"] = 1 } };
        index["producerRuns"] = new JsonArray(new JsonObject { ["producerRunId"] = run, ["clockCorrespondence"] = bridge.DeepClone(),
            ["ranges"] = new JsonObject { ["external-input:video:0"] = new JsonObject { ["first"] = Input(12000, 0), ["last"] = Input(1012000, 100000000) },
                ["source:video:0"] = new JsonObject { ["first"] = new JsonObject { ["result"] = 0, ["sourcePtsNs"] = 0 }, ["last"] = new JsonObject { ["result"] = 0, ["sourcePtsNs"] = 100000000 } },
                ["encoded:video:0"] = new JsonObject { ["first"] = new JsonObject { ["result"] = 0, ["runningNs"] = fault == "encoded" ? 1 : 0 }, ["last"] = new JsonObject { ["result"] = 0, ["runningNs"] = 100000000 } },
                ["submitted:video:0"] = new JsonObject { ["first"] = Submitted(100000000, 0), ["last"] = Submitted(fault == "origin" ? 101000001UL : 101000000UL, fault == "rounding" ? 100000050UL : 100000000UL) } } });
        if (fault == "epochs") index["producerRuns"]![0]!["ranges"]!["encoded:video:1"] = index["producerRuns"]![0]!["ranges"]!["encoded:video:0"]!.DeepClone();
        var records = new JsonArray(bridge, new JsonObject { ["kind"] = "ack", ["producerRunId"] = run, ["epoch"] = 0, ["upload"] = 1,
            ["fragment"] = "navigation", ["ackType"] = 3, ["result"] = 200, ["ackTimecodeMs"] = 10000 }); index["records"] = records;
        index["retrievalFixture"] = new JsonObject { ["schema"] = 1, ["kind"] = "constructed", ["sourceSha256"] = new JsonArray(new string('a', 64)) };
        if (fault == "reset") records.Add(new JsonObject { ["kind"] = "reset", ["producerRunId"] = run });
        if (fault == "flags") index["producerRuns"]![0]!["ranges"]!["external-input:video:0"]!["last"]!["flags"] = 2;
        if (offsetNs > 0) {
            // Constructed long cumulative source range with the SAME immutable
            // bridge/origins. Later observed media/logs are not rebound live data.
            var ranges = index["producerRuns"]![0]!["ranges"]!;
            ranges["external-input:video:0"]!["last"]!["captureQpc"] = 1012000UL + offsetNs / 100;
            ranges["external-input:video:0"]!["last"]!["ptsNs"] = 100000000UL + offsetNs;
            ranges["source:video:0"]!["last"]!["sourcePtsNs"] = 100000000UL + offsetNs;
            ranges["encoded:video:0"]!["last"]!["runningNs"] = 100000000UL + offsetNs;
            ranges["submitted:video:0"]!["last"]!["sdkPts100ns"] = 101000000UL + offsetNs / 100;
            ranges["submitted:video:0"]!["last"]!["runningNs"] = 100000000UL + offsetNs;
            records[1]!["ackTimecodeMs"] = 10000UL + offsetNs / 1000000;
        }
        store.Fragments = [new("navigation", TimingEvidence.Unix(100000000 + offsetNs / 100), 100, 4)];
        var log = System.Text.Encoding.UTF8.GetBytes($"é\n[callbackManagedTicks={(fault == "overflow" ? ulong.MaxValue : 101000UL + offsetNs / 100)} frequency={(fault == "frequency" ? 42 : 10000000)}] fixture\n");
        store.Add("log", logSequence, log);
        if (offsetNs > 0 || logSequence != 0) records.Add(new JsonObject { ["kind"] = "log-range", ["logSequence"] = logSequence,
            ["byteStart"] = 100000, ["byteLength"] = log.Length, ["elapsedStartNs"] = offsetNs, ["elapsedEndNs"] = offsetNs + 100000000 });
        return (store, index, new NavigationProbe { Gap = fault == "gap", Wide = fault == "wide", QueryFails = fault == "query-incomplete", FirstPtsNs = 10000000000 + offsetNs });
    }
    private async Task<(string Directory, RetrievalBundle Bundle, NavigationProbe Probe)> NavigationBundle(string fault = "normal")
    {
        var (store, index, probe) = NavigationSource(fault); store.Ready(index); var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var bundle = await new RetrievalService(store, probe).FetchAsync(FixtureStore.Id, directory, CancellationToken.None);
        if (fault == "incomplete") { bundle.Issues.Add("probe_incomplete"); await File.WriteAllTextAsync(Path.Combine(directory, "bundle.json"), JsonSerializer.Serialize(bundle, Wire.Json)); }
        if (fault == "ambiguous")
        {
            var media = bundle.Media.Single();
            File.Copy(Path.Combine(directory, media.Media.File), Path.Combine(directory, "media-0001.mkv"));
            File.Copy(Path.Combine(directory, media.Probe!.File), Path.Combine(directory, "media-0001.probe.json"));
            bundle.Media.Add(media with { Media = media.Media with { File = "media-0001.mkv" }, Probe = media.Probe with { File = "media-0001.probe.json" } });
            await File.WriteAllTextAsync(Path.Combine(directory, "bundle.json"), JsonSerializer.Serialize(bundle, Wire.Json));
        }
        return (directory, bundle, probe);
    }
    [TestCase(false)][TestCase(true)] public async Task LocateLogBindsActualUtf8HeaderAndObservedEndpointsWithoutChangingStrictMap(bool rounding)
    {
        var run = await NavigationBundle(rounding ? "rounding" : "normal");
        var a = await LogNavigation.LocateAsync(run.Directory, "logs-0000.log", "2", null, Path.Combine(root, "line"), run.Probe, CancellationToken.None);
        var b = await LogNavigation.LocateAsync(run.Directory, "logs-0000.log", null, "3", Path.Combine(root, "byte"), run.Probe, CancellationToken.None);
        Assert.That(a.Status, Is.EqualTo("estimated")); Assert.That(a.EvidenceProvenance, Is.EqualTo("constructed_fixture"));
        Assert.That(JsonSerializer.Serialize(a.Log), Is.EqualTo(JsonSerializer.Serialize(b.Log)));
        Assert.That(a.PhysicalCaptureOrEmissionErrorBoundNs, Is.Null); Assert.That(a.PlaybackOffsetVerified, Is.False);
        var frames = JsonSerializer.SerializeToElement(a.Frames, Wire.Json).GetProperty("observed");
        Assert.That(frames.GetArrayLength(), Is.EqualTo(2)); Assert.That(frames[0].GetProperty("bufferOrdinal").GetString(), Is.EqualTo("1"));
        Assert.That((await LocalBundle.MapAsync(run.Directory, null, "10000000", CancellationToken.None)).Status, Is.EqualTo("unverified"));
    }
    [TestCase("gap", "estimated_interval_in_decoded_gap")][TestCase("reset", "bundle_incomplete")]
    [TestCase("ambiguous", "ambiguous_observed_bracket")][TestCase("incomplete", "bundle_incomplete")]
    [TestCase("frequency", "managed_bridge_ambiguous_or_frequency_mismatch")][TestCase("origin", "inconsistent_sdk_clock_origin")]
    [TestCase("flags", "unknown_input_flags")]
    [TestCase("epochs", "sdk_epoch_ambiguous")][TestCase("encoded", "source_encoded_clock_relation_inconsistent")]
    [TestCase("wide", "observed_bracket_gap_or_too_wide")][TestCase("query-incomplete", "selected_probe_incomplete")]
    [TestCase("overflow", "callback_clock_overflow_or_before_session")]
    public async Task LocateLogRefusesInsufficientEvidence(string fault, string reason)
    {
        var run = await NavigationBundle(fault);
        var result = await LogNavigation.LocateAsync(run.Directory, "logs-0000.log", "2", null, Path.Combine(root, "query"), run.Probe, CancellationToken.None);
        Assert.That(result.Status, Is.EqualTo("unavailable")); Assert.That(result.Reason, Is.EqualTo(reason)); Assert.That(result.Frames, Is.Null);
    }
    [TestCase("header")][TestCase("boundary")][TestCase("tamper")]
    public async Task LocateLogRejectsNonHeadersAndTamperedBytes(string fault)
    {
        var run = await NavigationBundle();
        if (fault == "tamper") await File.AppendAllTextAsync(Path.Combine(run.Directory, "logs-0000.log"), "tampered");
        Assert.That(async () => await LogNavigation.LocateAsync(run.Directory, "logs-0000.log", fault == "boundary" ? null : "1",
            fault == "boundary" ? "1" : null, Path.Combine(root, "bad"), run.Probe, CancellationToken.None), Throws.TypeOf<RetrievalError>());
    }
    [Test] public async Task ExplicitRuntimeOverrideReachesOfflineLocatorProbe()
    {
        var run = await NavigationBundle();
        var tools = Path.Combine(root, "override-tools"); var runtime = Path.Combine(root, "override-runtime");
        Directory.CreateDirectory(tools); Directory.CreateDirectory(Path.Combine(runtime, "bin"));
        await File.WriteAllTextAsync(Path.Combine(runtime, "bin", "native-media-probe.exe"), "constructed marker; no executable runs");
        var selected = Path.Combine(root, "selected-fixture.json");
        await run.Probe.RunAsync(Path.Combine(run.Directory, run.Bundle.Media.Single().Media.File), selected, CancellationToken.None, [10000000000, 10033333333]);
        string Quote(string path) => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";
        // Constructed process-boundary fixture checks the CLI wiring; the separate
        // native interoperability test runs the actual freshly built decoder.
        var script = "param($InputFile,$Output,$SelectedVideoPtsNs,$ToleranceNs,$RuntimeDirectory)\n"
            + "if($RuntimeDirectory -cne " + Quote(runtime) + "){exit 17}\n"
            + "if($env:AWS_ACCESS_KEY_ID -or $env:AWS_CONFIG_FILE){exit 18}\n"
            + "Set-Content -LiteralPath " + Quote(Path.Combine(runtime,"used")) + " -Value 'explicit'\n"
            + "Copy-Item -LiteralPath " + Quote(selected) + " -Destination $Output\nexit 0\n";
        await File.WriteAllTextAsync(Path.Combine(tools, "probe-media.ps1"), script);
        var code = await Playreel.Retrieval.Program.Main(["locate-log", "--directory", run.Directory,
            "--log-file", "logs-0000.log", "--line", "2", "--output", Path.Combine(root,"override-result"),
            "--tool-root", tools, "--native-runtime", runtime]);
        Assert.That(code, Is.Zero); Assert.That(File.Exists(Path.Combine(runtime,"used")), Is.True);
    }

    [TestCase("relative")][TestCase("missing")][TestCase("without-probe")]
    public async Task BadExplicitRuntimeIsRejectedBeforeLocalOrCloudWork(string fault)
    {
        var runtime = fault == "relative" ? "relative-runtime" : fault == "missing" ? Path.Combine(root,"missing") : root;
        foreach (var command in new[] { "inspect", "map", "metadata" }) {
            var original = Console.Out; using var output = new StringWriter();
            int code;
            try { Console.SetOut(output); code = await Playreel.Retrieval.Program.Main([command,"--native-runtime",runtime]); }
            finally { Console.SetOut(original); }
            Assert.That(code, Is.EqualTo(2)); Assert.That(output.ToString(), Does.Contain("invalid_native_runtime"));
        }
    }

    private static JsonObject Read(string name) => JsonNode.Parse(File.ReadAllText(Fixture(name)))!.AsObject();
    [Test] public void NavigationCapacityDoesNotChangeExistingStrictTimingAcceptance()
    {
        var index = ConstructedIndex(new FixtureStore());
        index["producerRuns"]![0]!["ranges"]!["external-input:video:0"] = new JsonObject { ["first"] = new JsonObject(), ["last"] = new JsonObject() };
        using var doc = TimingEvidence.Parse(JsonSerializer.SerializeToUtf8Bytes(index));
        var timing = new TimingEvidence();
        for (int n = 0; n < 1025; n++) timing.Add(doc.RootElement, FixtureStore.Id, 1, "time-index", 0);
        Assert.That(timing.SchedulingPoints(), Has.Count.EqualTo(3));
    }

    private static JsonObject ConstructedIndex(FixtureStore store, string mode = "normal")
    {
        var native = Read($"clock-{mode}-native-v2.json");
        var index = store.Timing("time-index", 0); var run = native["runId"]!.GetValue<string>();
        // This fixture deliberately constructs the managed association. No real
        // B2i sample is rebound to the historical B2h run. Original native/probe
        // fixtures remain linked and byte-identical; output retains construction.
        var sourceNames = mode == "overflow" ? new[] { "clock-overflow-native-v2.json" } : new[] { $"clock-{mode}-native-v2.json", $"clock-{mode}-decoded-v2.json" };
        index["retrievalFixture"] = new JsonObject { ["schema"] = 1, ["kind"] = "constructed", ["sourceSha256"] = new JsonArray(sourceNames
            .Select(name => (JsonNode?)JsonValue.Create(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Fixture(name)))))).ToArray()) };
        var bridge = new JsonObject { ["kind"] = "clock", ["producerRunId"] = run, ["epoch"] = 0, ["sequence"] = 0,
            ["managedTicks"] = 123, ["managedAfterTicks"] = 123, ["managedFrequencyHz"] = 3000,
            ["managedBeforeElapsedNs"] = 0, ["managedAfterElapsedNs"] = 100,
            ["correspondenceQpc"] = ulong.Parse(native["sessionAnchorQpc"]!.GetValue<string>(), CultureInfo.InvariantCulture), ["correspondenceUncertainty100ns"] = 52,
            ["observationClock"] = new JsonObject { ["qpc"] = ulong.Parse(native["sessionAnchorQpc"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                ["frequencyHz"] = ulong.Parse(native["qpcFrequency"]!.GetValue<string>(), CultureInfo.InvariantCulture), ["unix100ns"] = null, ["uncertainty100ns"] = 5 } };
        var records = new JsonArray(bridge); index["records"] = records;
        var rows = native["anchors"]!.AsArray().Select(row => {
            var result = row!.DeepClone().AsObject();
            foreach (var p in result.ToArray()) if (p.Value is JsonValue value && value.TryGetValue<string>(out var text)) result[p.Key] = ulong.Parse(text, CultureInfo.InvariantCulture);
            return result;
        }).ToArray();
        for (var n = 0; n < rows.Length;)
        {
            var count = rows[n]["kind"]!.GetValue<uint>() == 3 && n + 2 < rows.Length ? 3 : 1;
            records.Add(new JsonObject { ["kind"] = "clock-group", ["clockSchemaVersion"] = 2, ["producerRunId"] = run,
                ["meaning"] = "generated-scheduling", ["usable"] = count == 3, ["droppedTotal"] = ulong.Parse(native["clockDropped"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                ["records"] = new JsonArray(rows.Skip(n).Take(count).Select(r => r.DeepClone()).ToArray()) }); n += count;
        }
        var frames = rows.Where(r => r["kind"]!.GetValue<uint>() == 1 && r["sdkPts100ns"] is not null).ToArray();
        var first = frames.Length == 0 ? FixtureStore.First : frames[0]["sdkPts100ns"]!.GetValue<ulong>();
        var last = frames.Length == 0 ? first : frames[^1]["sdkPts100ns"]!.GetValue<ulong>();
        index["producerRuns"] = new JsonArray(new JsonObject { ["producerRunId"] = run, ["epoch"] = 0,
            ["clockCorrespondence"] = bridge.DeepClone(), ["ranges"] = new JsonObject { ["submitted:video:0"] = new JsonObject {
                ["first"] = new JsonObject { ["sdkPts100ns"] = first, ["result"] = 0 }, ["last"] = new JsonObject { ["sdkPts100ns"] = last + 10000000, ["result"] = 0 } } } });
        records.Add(new JsonObject { ["kind"] = "ack", ["producerRunId"] = run, ["epoch"] = 0, ["upload"] = 1, ["fragment"] = "constructed-fragment", ["ackType"] = 3, ["result"] = 200, ["ackTimecodeMs"] = first / 10000 });
        store.Fragments = [new("constructed-fragment", TimingEvidence.Unix(first / 10000 * 10000), 3000, 4)];
        return index;
    }
    private static TimingEvidence Evidence(JsonObject index)
    {
        using var doc = TimingEvidence.Parse(JsonSerializer.SerializeToUtf8Bytes(index)); var result = new TimingEvidence();
        result.Add(doc.RootElement, FixtureStore.Id, 1, "time-index", 0); return result;
    }
    private async Task<(RetrievalBundle Bundle, string Directory, MappingResult Mapping)> Run(JsonObject index, FixtureStore store, SelectionProbe? probe = null, long? time = null)
    {
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N")); store.Ready(index);
        var bundle = await new RetrievalService(store, probe ?? new SelectionProbe()).FetchAsync(FixtureStore.Id, directory, CancellationToken.None);
        var points = Evidence(index).SchedulingPoints();
        var elapsed = time ?? (points.Count > 0 ? points[0].BeforeNs + (points[0].AfterNs - points[0].BeforeNs) / 2 : 1);
        return (bundle, directory, await LocalBundle.MapAsync(directory, null, elapsed.ToString(CultureInfo.InvariantCulture), CancellationToken.None));
    }
    [Test] public async Task ExistingCallbackTickPrefixMapsThroughItsManagedBridgeInCompleteConstructedBundle()
    {
        var store = new FixtureStore(); var index = ConstructedIndex(store);
        const ulong frequency = 10000000, bridgeTicks = 5000000000;
        // A constructed complete bundle, not a rebinding of C13's real log to
        // historical native footage. The unchanged logger already supplies ticks.
        foreach (var bridge in new[] { index["records"]![0]!.AsObject(), index["producerRuns"]![0]!["clockCorrespondence"]!.AsObject() }) {
            bridge["managedTicks"] = bridgeTicks; bridge["managedAfterTicks"] = bridgeTicks; bridge["managedFrequencyHz"] = frequency;
        }
        var points = Evidence(index).SchedulingPoints(); var point = points[0];
        long center = point.BeforeNs + (point.AfterNs - point.BeforeNs) / 2;
        ulong callbackTicks = bridgeTicks + (ulong)center / 100;
        string prefix = $"[callbackManagedTicks={callbackTicks} frequency={frequency}] Log fixture";
        var parsed = System.Text.RegularExpressions.Regex.Match(prefix, @"^\[callbackManagedTicks=(\d+) frequency=(\d+)\]");
        Assert.That(parsed.Success, Is.True);
        ulong ticks = ulong.Parse(parsed.Groups[1].Value, CultureInfo.InvariantCulture);
        ulong callbackFrequency = ulong.Parse(parsed.Groups[2].Value, CultureInfo.InvariantCulture);
        Assert.That(callbackFrequency, Is.EqualTo(frequency));
        // Reuse the existing exact, outward-rounded relative-clock arithmetic.
        // Both operands are explicitly in the same managed counter domain here;
        // no assumption that managed ticks equal native QPC is introduced.
        Assert.That(Playreel.Client.RecordingSchedulingMath.TryInterval(0, 100,
            bridgeTicks, callbackFrequency, ticks, 0, out long low, out long high), Is.True);
        Assert.That(low, Is.GreaterThanOrEqualTo(point.BeforeNs)); Assert.That(high, Is.LessThanOrEqualTo(point.AfterNs));
        Assert.That(points.Count(p => low <= p.AfterNs && high >= p.BeforeNs), Is.EqualTo(1));
        var result = await Run(index, store, time: low + (high - low) / 2);
        Assert.That(result.Bundle.Status, Is.EqualTo("retrieved")); Assert.That(result.Mapping.Status, Is.EqualTo("mapped"));
        Assert.That(result.Mapping.EvidenceProvenance, Is.EqualTo("constructed_fixture"));
        Assert.That(result.Mapping.SameRunLiveProofVerified, Is.False);
        TestContext.WriteLine($"Existing callback ticks={ticks} frequency={frequency}; elapsed-ns=[{low},{high}]; complete constructed bundle mapped.");
    }
    [TestCase("normal")][TestCase("delay")]
    public async Task ConstructedAssociationMapsLaterFrameAndRetainsProvenance(string mode)
    {
        var store = new FixtureStore(); var index = ConstructedIndex(store, mode); var points = Evidence(index).SchedulingPoints();
        Assert.That(points, Has.Count.EqualTo(3)); var point = points[1];
        var measuredReference = Read($"clock-{mode}-correspondence-v2.json")["Mappings"]![1]!;
        var expectedCenter = long.Parse(measuredReference["SessionElapsedNs"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        Assert.That(point.BeforeNs, Is.EqualTo(expectedCenter - (long)point.NativeUncertaintyNs));
        Assert.That(point.AfterNs, Is.EqualTo(expectedCenter + (long)point.NativeUncertaintyNs + 100));
        if (mode == "delay")
        {
            // The historical delayed callback belongs to the first selected
            // frame; later frames remain useful independent scheduling points.
            var source = Read("clock-delay-native-v2.json")["anchors"]!.AsArray().Single(r => r!["frameOrdinal"]!.GetValue<string>() == "31" && r["kind"]!.GetValue<uint>() == 1)!;
            Assert.That((decimal)points[0].PadArrivalQpc - ulong.Parse(source["estimatedScheduleQpc"]!.GetValue<string>(), CultureInfo.InvariantCulture), Is.GreaterThan((decimal)points[0].NativeUncertaintyNs / 100));
        }
        var probe = new SelectionProbe { Mode = mode }; var run = await Run(index, store, probe, point.BeforeNs + (point.AfterNs - point.BeforeNs) / 2);
        Assert.Multiple(() => { Assert.That(run.Bundle.Status, Is.EqualTo("retrieved")); Assert.That(run.Mapping.Status, Is.EqualTo("mapped"));
            Assert.That(run.Mapping.PlaybackOffsetSeconds, Is.EqualTo(2m)); Assert.That(run.Mapping.EvidenceProvenance, Is.EqualTo("constructed_fixture"));
            Assert.That(run.Mapping.ConstructionSourceSha256, Has.Length.EqualTo(2)); Assert.That(run.Mapping.SameRunLiveProofVerified, Is.False);
            Assert.That(run.Mapping.PlaybackOffsetVerified, Is.False); Assert.That(run.Mapping.Coverage, Is.EqualTo("unverified"));
            Assert.That(probe.Targets, Has.Count.EqualTo(3)); });
        Assert.That(await Playreel.Retrieval.Program.Main(["map", "--directory", run.Directory, "--elapsed-ns", (point.BeforeNs + (point.AfterNs - point.BeforeNs) / 2).ToString(CultureInfo.InvariantCulture)]), Is.Zero);
        var json = JsonSerializer.SerializeToElement(run.Mapping.SelectedPoint, Wire.Json);
        Assert.That(json.GetProperty("decoded").GetProperty("bufferOrdinal").GetString(), Is.EqualTo("61"));
        Assert.That(json.GetProperty("managedEnclosureNs").GetString(), Is.EqualTo("100"));
        var evidence = Environment.GetEnvironmentVariable("SBREC_RETRIEVAL_TEST_ROOT");
        if (!string.IsNullOrEmpty(evidence))
        {
            var saved = Path.Combine(evidence, $"constructed-{mode}-bundle"); Directory.CreateDirectory(saved);
            foreach (var f in Directory.EnumerateFiles(run.Directory)) File.Copy(f, Path.Combine(saved, Path.GetFileName(f)), false);
            await File.WriteAllTextAsync(Path.Combine(evidence, $"constructed-{mode}-mapping.json"), JsonSerializer.Serialize(run.Mapping, Wire.Json));
        }
    }
    [TestCase("partial")][TestCase("flag")][TestCase("reason")][TestCase("unknown")][TestCase("epoch")][TestCase("sequence")][TestCase("bracket")][TestCase("calibration")][TestCase("stage")][TestCase("version")]
    public void InvalidGroupsNeverSupplySchedulingPoints(string fault)
    {
        var s = new FixtureStore(); var index = ConstructedIndex(s); var groups = index["records"]!.AsArray().Where(r => r?["kind"]?.GetValue<string>() == "clock-group" && r["usable"]!.GetValue<bool>()).ToArray();
        foreach (var group in groups)
        {
            var rows = group!["records"]!.AsArray(); var frame = rows[^1]!;
            switch (fault) {
                case "partial": rows.RemoveAt(0); break; case "flag": frame["flags"] = 7; break; case "reason": frame["reason"] = 4; break;
                case "unknown": frame["sourceRunningNs"] = null; break; case "epoch": rows[0]!["clockEpoch"] = 2; break;
                case "sequence": frame["sequence"] = 999; break; case "bracket": rows[0]!["qpcAfter"] = ulong.MaxValue; break;
                case "calibration": rows[1]!["calibrationRateNumerator"] = 0; break; case "stage": frame["sdkInputRunningNs"] = 42; break;
                case "version": frame["version"] = 3; break;
            }
        }
        Assert.That(Evidence(index).SchedulingPoints(), Is.Empty);
    }
    [TestCase("clock-loss")][TestCase("abi1-loss")][TestCase("managed-eviction")][TestCase("group-loss")][TestCase("reset")]
    public void IndependentUnlocalizedLossPreventsMapping(string kind)
    {
        var s = new FixtureStore(); var index = ConstructedIndex(s); var rows = index["records"]!.AsArray(); var run = index["producerRuns"]![0]!["producerRunId"]!.GetValue<string>();
        if (kind == "managed-eviction") index["droppedRecords"] = 1;
        else if (kind == "group-loss") foreach (var group in rows.Where(r => r?["kind"]?.GetValue<string>() == "clock-group")) group!["droppedTotal"] = ulong.MaxValue - 1;
        else if (kind == "clock-loss") rows.Add(new JsonObject { ["kind"] = "clock-loss", ["clockSchemaVersion"] = 2, ["producerRunId"] = run, ["droppedTotal"] = ulong.MaxValue - 1, ["position"] = "unlocalized" });
        else rows.Add(new JsonObject { ["kind"] = kind == "reset" ? "reset" : "gap", ["producerRunId"] = run, ["epoch"] = 0, ["droppedObservations"] = 1 });
        Assert.That(Evidence(index).SchedulingPoints(), Is.Empty);
    }
    [Test] public void RealManagedAndHistoricalNativeFixturesAreNotOneObservedRun()
    {
        var safe = Read("managed-bridge-safe-abi.json"); var s = new FixtureStore(); var constructed = ConstructedIndex(s);
        using var actual = TimingEvidence.Parse(JsonSerializer.SerializeToUtf8Bytes(safe)); var e = new TimingEvidence();
        Assert.That(() => e.Add(actual.RootElement, FixtureStore.Id, 1, "time-index", 0), Throws.TypeOf<RetrievalError>().With.Message.EqualTo("timing_binding_mismatch"));
        // Even explicitly rebinding the enclosing test session cannot make the
        // real bridge run match the historical native run's selected groups.
        constructed["records"]!.AsArray().RemoveAt(0);
        constructed["producerRuns"]![0]!.AsObject().Remove("clockCorrespondence");
        constructed["records"]!.AsArray().Add(safe["records"]![0]!.DeepClone());
        Assert.That(Evidence(constructed).SchedulingPoints(), Is.Empty);
    }
    [TestCase("sdk-overflow")][TestCase("selector-range")][TestCase("frequency")][TestCase("missing-managed")]
    public void ExactBoundsAndMissingCorrespondenceStayUnavailable(string fault)
    {
        var s = new FixtureStore(); var index = ConstructedIndex(s);
        if (fault is "sdk-overflow" or "selector-range") foreach (var g in index["records"]!.AsArray().Where(r => r?["kind"]?.GetValue<string>() == "clock-group" && r["usable"]!.GetValue<bool>()))
            g!["records"]![2]!["sdkPts100ns"] = fault == "sdk-overflow" ? ulong.MaxValue - 1 : (ulong)long.MaxValue / 100 + 1;
        else
        {
            var bridge = index["records"]![0]!; bridge["managedAfterTicks"] = fault == "missing-managed" ? null : 124;
            if (fault == "frequency") bridge["observationClock"]!["frequencyHz"] = 3;
            index["producerRuns"]![0]!["clockCorrespondence"] = bridge.DeepClone();
        }
        Assert.That(Evidence(index).SchedulingPoints(), Is.Empty);
    }
    [TestCase("missing")][TestCase("ambiguous")][TestCase("incomplete")][TestCase("exit")][TestCase("eos")][TestCase("reason")]
    public async Task DecoderFailuresCannotBecomeMappedPoints(string fault)
    {
        var s = new FixtureStore(); var index = ConstructedIndex(s); var run = await Run(index, s, new SelectionProbe { Fault = fault });
        Assert.That(run.Mapping.Status, Is.EqualTo("unverified")); Assert.That(run.Mapping.PlaybackOffsetSeconds, Is.Null);
        if (fault == "reason") Assert.That(run.Bundle.Status, Is.EqualTo("incomplete"));
    }
    [Test] public async Task SparseTimesAndWideLogIntervalsNeverUseNearestFallback()
    {
        var s = new FixtureStore(); var i = ConstructedIndex(s); var p = Evidence(i).SchedulingPoints();
        var middle = p[0].AfterNs + (p[1].BeforeNs - p[0].AfterNs) / 2;
        var absent = await Run(i, s, time: middle); Assert.That(absent.Mapping.Status, Is.EqualTo("unverified"));
        var s2 = new FixtureStore(); var i2 = ConstructedIndex(s2);
        i2["records"]!.AsArray().Add(new JsonObject { ["kind"] = "log-range", ["logSequence"] = 0, ["byteStart"] = 0, ["byteLength"] = 3,
            ["elapsedStartNs"] = p[0].BeforeNs, ["elapsedEndNs"] = p[2].AfterNs }); s2.Add("log", 0, "abc"u8.ToArray());
        var ambiguous = await Run(i2, s2); Assert.That(ambiguous.Mapping.Reason, Is.EqualTo("ambiguous_scheduling_point"));
    }
    [Test] public async Task DuplicateEligibleFilesAndIncompleteCompetitorsRemainUnavailable()
    {
        var s = new FixtureStore(); var i = ConstructedIndex(s); var run = await Run(i, s); var media = run.Bundle.Media.Single();
        File.Copy(Path.Combine(run.Directory, media.Media.File), Path.Combine(run.Directory, "media-0001.mkv"));
        File.Copy(Path.Combine(run.Directory, media.Probe!.File), Path.Combine(run.Directory, "media-0001.probe.json"));
        run.Bundle.Media.Add(media with { Media = media.Media with { File = "media-0001.mkv" }, Probe = media.Probe with { File = "media-0001.probe.json" } });
        await File.WriteAllTextAsync(Path.Combine(run.Directory, "bundle.json"), JsonSerializer.Serialize(run.Bundle, Wire.Json));
        Assert.That((await LocalBundle.MapAsync(run.Directory, null, run.Mapping.SessionElapsedNs, CancellationToken.None)).Reason, Is.EqualTo("ambiguous_media_file"));
        run.Bundle.Media[1] = run.Bundle.Media[1] with { ProbeComplete = false };
        await File.WriteAllTextAsync(Path.Combine(run.Directory, "bundle.json"), JsonSerializer.Serialize(run.Bundle, Wire.Json));
        Assert.That((await LocalBundle.MapAsync(run.Directory, null, run.Mapping.SessionElapsedNs, CancellationToken.None)).Reason, Is.EqualTo("file_selection_incomplete"));
    }
    [Test] public async Task ExplicitUtcExpandsOnlyByItsAnchorUncertainty()
    {
        var s = new FixtureStore(); var i = ConstructedIndex(s); var p = Evidence(i).SchedulingPoints()[0];
        var run = await Run(i, s); var elapsed = p.BeforeNs + (p.AfterNs - p.BeforeNs) / 2;
        var timestamp = DateTimeOffset.Parse("2026-10-15T12:00:00Z", CultureInfo.InvariantCulture).AddTicks(elapsed / 100).ToString("O");
        var mapped = await LocalBundle.MapAsync(run.Directory, timestamp, null, CancellationToken.None);
        Assert.That(mapped.Status, Is.EqualTo("mapped")); var json = JsonSerializer.SerializeToElement(mapped.SelectedPoint, Wire.Json);
        Assert.That(long.Parse(json.GetProperty("alignedSchedulingBeforeNs").GetString()!, CultureInfo.InvariantCulture), Is.EqualTo(p.BeforeNs - 100));
        Assert.That(long.Parse(json.GetProperty("alignedSchedulingAfterNs").GetString()!, CultureInfo.InvariantCulture), Is.EqualTo(p.AfterNs + 100));
        Assert.That(json.GetProperty("utcInputUncertaintyNs").GetString(), Is.EqualTo("100"));
    }
    [TestCase("target")][TestCase("count")][TestCase("encoding")][TestCase("duplicate")][TestCase("tolerance")][TestCase("unknown")]
    public void MalformedSelectedEvidenceCannotClaimUniqueness(string fault)
    {
        var root = Read("clock-normal-decoded-v2.json"); var q = root["frameSelections"]!["queries"]![0]!;
        switch (fault) {
            case "target": q["targetPtsNs"] = ulong.MaxValue.ToString(CultureInfo.InvariantCulture); break;
            case "count": q["status"] = "missing"; q["matchCount"] = "1"; break;
            case "encoding": q["ptsNs"] = 1792087083350000000UL; break;
            case "duplicate": root["frameSelections"]!["queries"]!.AsArray().Add(q.DeepClone()); break;
            case "tolerance": q["toleranceNs"] = "1000001"; break;
            case "unknown": q["streamNs"] = ulong.MaxValue.ToString(CultureInfo.InvariantCulture); break;
        }
        using var doc = TimingEvidence.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
        Assert.That(() => ProbeEvidence.Selections(doc.RootElement), Throws.TypeOf<RetrievalError>());
    }
    [Test] public async Task NativeSelectedQueriesUseExistingWrapperAndAcceptedLocalMediaOnly()
    {
        var input = Environment.GetEnvironmentVariable("SBREC_RETRIEVAL_PROBE_FIXTURE"); var tool = Environment.GetEnvironmentVariable("SBREC_RETRIEVAL_TOOL_ROOT");
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(tool)) Assert.Ignore("No explicit local probe fixture.");
        var expected = Read("clock-normal-decoded-v2.json");
        if (await RetrievalService.HashAsync(input!, CancellationToken.None) != expected["fileSha256"]!.GetValue<string>()) Assert.Ignore("Selected-query proof requires the accepted B2h normal media file.");
        using var accepted = TimingEvidence.Parse(JsonSerializer.SerializeToUtf8Bytes(expected));
        var targets = ProbeEvidence.Selections(accepted.RootElement).Select(s => s.TargetNs).Concat([0UL, (ulong)long.MaxValue]).ToArray();
        var output = Path.Combine(root, "actual-selected.json");
        Assert.That(await new ProbeProcess(tool!).RunAsync(input!, output, CancellationToken.None, targets), Is.Zero);
        using var result = TimingEvidence.Parse(await File.ReadAllBytesAsync(output));
        var selected = ProbeEvidence.Selections(result.RootElement); Assert.That(selected, Has.Count.EqualTo(5));
        Assert.That(selected.Where(q => q.Status == "unique_observed").Select(q => q.BufferOrdinal), Is.EqualTo(new ulong?[] { 31, 61, 91 }));
        Assert.That(selected.Where(q => q.Status == "unique_observed").Select(q => q.StreamNs), Is.EqualTo(new ulong?[] { 1000000000, 2000000000, 3000000000 }));
        Assert.That(selected.Count(q => q.Status == "missing"), Is.EqualTo(2));
        Assert.That(result.RootElement.GetProperty("producerAnchor").GetProperty("available").GetBoolean(), Is.False);
        var evidence = Environment.GetEnvironmentVariable("SBREC_RETRIEVAL_TEST_ROOT");
        if (!string.IsNullOrEmpty(evidence)) File.Copy(output, Path.Combine(evidence, "native-selected-probe.json"), false);
    }
    [TestCase("duplicate")][TestCase("count")][TestCase("range")]
    public void ProbeTargetBoundsRejectBeforeAnyChildOrOutput(string fault)
    {
        ulong[] targets = fault switch { "duplicate" => [1, 1], "count" => Enumerable.Range(0, 65).Select(n => (ulong)n).ToArray(), _ => [(ulong)long.MaxValue + 1] };
        var output = Path.Combine(root, "must-not-exist.json");
        Assert.That(async () => await new ProbeProcess(root).RunAsync("not-read", output, CancellationToken.None, targets),
            Throws.TypeOf<RetrievalError>().With.Message.EqualTo("probe_target_limit")); Assert.That(File.Exists(output), Is.False);
    }
    [Test] public void ConflictingImmutableGroupsAndIndependentBridgeValuesCannotPickWinner()
    {
        var s = new FixtureStore(); var i = ConstructedIndex(s); var duplicate = i["records"]!.AsArray().First(r => r?["kind"]?.GetValue<string>() == "clock-group" && r["usable"]!.GetValue<bool>())!.DeepClone();
        duplicate["records"]![2]!["estimatedScheduleQpc"] = 123;
        i["records"]!.AsArray().Add(duplicate); Assert.That(Evidence(i).SchedulingPoints(), Is.Empty);
        var i2 = ConstructedIndex(new FixtureStore()); var bridge = i2["records"]![0]!.DeepClone(); bridge["managedAfterElapsedNs"] = 101;
        i2["records"]!.AsArray().Add(bridge); Assert.That(Evidence(i2).SchedulingPoints(), Is.Empty);
    }
    [Test] public void EpochAndFrameIdentitiesRetainFullUnsignedRange()
    {
        var s = new FixtureStore(); var i = ConstructedIndex(s); var rows = i["records"]!.AsArray();
        rows[0]!["epoch"] = ulong.MaxValue; i["producerRuns"]![0]!["clockCorrespondence"] = rows[0]!.DeepClone();
        foreach (var g in rows.Where(r => r?["kind"]?.GetValue<string>() == "clock-group")) foreach (var r in g!["records"]!.AsArray())
        { r!["producerEpoch"] = ulong.MaxValue; r["frameOrdinal"] = ulong.MaxValue - 1; }
        var points = Evidence(i).SchedulingPoints(); Assert.That(points, Has.Count.EqualTo(3));
        Assert.That(points.All(p => p.ProducerEpoch == ulong.MaxValue && p.FrameOrdinal == ulong.MaxValue - 1), Is.True);
    }
    [Test] public void AcceptedNativeOverflowFixtureNeverSuppliesSelectedPoint()
    {
        var s = new FixtureStore(); var index = ConstructedIndex(s, "overflow");
        Assert.That(Evidence(index).SchedulingPoints(), Is.Empty);
    }
    [TestCase("zero")][TestCase("resolution-only")][TestCase("shifted-estimate")][TestCase("fractional-bound")][TestCase("generous-valid")]
    public void ClaimedNativeUncertaintyMustCoverItsSupportEnclosure(string fault)
    {
        var s = new FixtureStore(); var index = ConstructedIndex(s);
        foreach (var group in index["records"]!.AsArray().Where(r => r?["kind"]?.GetValue<string>() == "clock-group" && r["usable"]!.GetValue<bool>()))
        {
            var rows = group!["records"]!.AsArray(); var frame = rows[2]!;
            if (fault == "zero") frame["scheduleUncertaintyNs"] = 0;
            if (fault == "resolution-only") frame["scheduleUncertaintyNs"] = 100;
            if (fault == "shifted-estimate") frame["estimatedScheduleQpc"] = rows[0]!["qpcBefore"]!.DeepClone();
            if (fault == "fractional-bound") { frame["scheduleUncertaintyNs"] = 100; foreach (var row in rows) row!["qpcFrequency"] = ulong.MaxValue - 1; }
            if (fault == "generous-valid") frame["scheduleUncertaintyNs"] = frame["scheduleUncertaintyNs"]!.GetValue<ulong>() * 2;
        }
        if (fault == "fractional-bound")
        { index["records"]![0]!["observationClock"]!["frequencyHz"] = ulong.MaxValue - 1; index["producerRuns"]![0]!["clockCorrespondence"] = index["records"]![0]!.DeepClone(); }
        Assert.That(Evidence(index).SchedulingPoints().Count, Is.EqualTo(fault == "generous-valid" ? 3 : 0));
    }
    [TestCase("segment")][TestCase("ordinal")][TestCase("ordinal-other-span")][TestCase("pts")][TestCase("running")][TestCase("stream")][TestCase("duration")]
    public void SelectedDecodedCoordinatesMustAgreeWithTheirActualSpan(string fault)
    {
        var root = Read("clock-normal-decoded-v2.json"); var q = root["frameSelections"]!["queries"]![0]!;
        switch (fault) {
            case "segment": q["segmentIndex"] = 99999999; break;
            case "ordinal": q["bufferOrdinal"] = "999999999"; break;
            case "ordinal-other-span": q["bufferOrdinal"] = "34"; break;
            case "pts": q["ptsNs"] = "1792087083350000001"; break;
            case "running": q["runningNs"] = "1000000001"; break;
            case "stream": q["streamNs"] = "9999999999999"; break;
            case "duration": q["durationNs"] = "9999999999999"; break;
        }
        using var document = TimingEvidence.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
        Assert.That(() => ProbeEvidence.Selections(document.RootElement), Throws.TypeOf<RetrievalError>().With.Message.EqualTo("inconsistent_decoded_selection"));
    }
    [Test] public void LastDecodedFrameAndSpanEndpointAreValidControls()
    {
        var root = Read("clock-normal-decoded-v2.json"); var video = root["tracks"]!.AsArray().Single(t => t!["kind"]!.GetValue<string>() == "video")!["decoded"]!;
        var q = root["frameSelections"]!["queries"]![0]!; var last = video["spans"]!.AsArray()[^1]!;
        q["bufferOrdinal"] = video["buffers"]!.DeepClone(); q["segmentIndex"] = last["segmentIndex"]!.DeepClone();
        foreach (var field in new[] { "PtsNs", "RunningNs", "StreamNs" }) q[char.ToLowerInvariant(field[0]) + field[1..]] = last["last" + field]!.DeepClone();
        q["targetPtsNs"] = q["ptsNs"]!.DeepClone(); q["durationNs"] = last["lastBufferDurationNs"]!.DeepClone();
        using var document = TimingEvidence.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
        Assert.That(ProbeEvidence.Selections(document.RootElement)[0].BufferOrdinal, Is.EqualTo(117UL));
    }
    [TestCase("baseline")][TestCase("uncertainty")][TestCase("decoded-position")][TestCase("wide-single-log")]
    public async Task IndependentReviewCounterexamplesUseTheActualAcceptedMedia(string fault)
    {
        var media = Environment.GetEnvironmentVariable("SBREC_RETRIEVAL_PROBE_FIXTURE");
        if (string.IsNullOrEmpty(media)) Assert.Ignore("No explicit accepted local media file.");
        if (await RetrievalService.HashAsync(media!, CancellationToken.None) != Read("clock-normal-decoded-v2.json")["fileSha256"]!.GetValue<string>()) Assert.Ignore("Accepted B2h normal media required.");
        var store = new FixtureStore(); var index = ConstructedIndex(store); var original = Evidence(index).SchedulingPoints()[0];
        var requested = original.BeforeNs + (original.AfterNs - original.BeforeNs) / 2;
        store.Media = await File.ReadAllBytesAsync(media!); store.Fragments[0] = store.Fragments[0] with { Bytes = store.Media.Length };
        if (fault == "uncertainty")
        {
            foreach (var group in index["records"]!.AsArray().Where(r => r?["kind"]?.GetValue<string>() == "clock-group" && r["usable"]!.GetValue<bool>())) group!["records"]![2]!["scheduleUncertaintyNs"] = 0;
            Assert.That(Evidence(index).SchedulingPoints(), Is.Empty);
        }
        if (fault == "wide-single-log")
        {
            requested = original.BeforeNs - 1000000000;
            index["records"]!.AsArray().Add(new JsonObject { ["kind"] = "log-range", ["logSequence"] = 0, ["byteStart"] = 0, ["byteLength"] = 3,
                ["elapsedStartNs"] = requested, ["elapsedEndNs"] = original.AfterNs }); store.Add("log", 0, "abc"u8.ToArray());
        }
        var run = await Run(index, store, new SelectionProbe { Fault = fault }, requested);
        Assert.That(run.Mapping.Status, Is.EqualTo(fault == "baseline" ? "mapped" : "unverified"));
        Assert.That(run.Mapping.PlaybackOffsetSeconds, Is.EqualTo(fault == "baseline" ? 1m : (decimal?)null));
        Assert.That(run.Mapping.EvidenceProvenance, Is.EqualTo("constructed_fixture"));
        Assert.That(run.Mapping.SameRunLiveProofVerified, Is.False); Assert.That(run.Mapping.PlaybackOffsetVerified, Is.False);
        if (fault == "wide-single-log")
        {
            // A time actually within that same wide log's sole sparse point is
            // still supported; only manufacturing a match outside it is denied.
            var control = await LocalBundle.MapAsync(run.Directory, null, (original.BeforeNs + 1).ToString(CultureInfo.InvariantCulture), CancellationToken.None);
            Assert.That(control.Status, Is.EqualTo("mapped"));
        }
        var evidence = Environment.GetEnvironmentVariable("SBREC_RETRIEVAL_TEST_ROOT");
        if (!string.IsNullOrEmpty(evidence)) await File.WriteAllTextAsync(Path.Combine(evidence, $"review-{fault}-mapping.json"), JsonSerializer.Serialize(run.Mapping, Wire.Json));
    }

    private sealed class SelectionProbe : IMediaProbe
    {
        public string Mode = "normal", Fault = ""; public IReadOnlyList<ulong> Targets = [];
        public async Task<int> RunAsync(string input, string output, CancellationToken ct, IReadOnlyList<ulong>? selectedTargets = null)
        {
            Targets = selectedTargets ?? []; var root = Read($"clock-{Mode}-decoded-v2.json");
            root["fileSha256"] = await RetrievalService.HashAsync(input, ct); root["fileBytes"] = new FileInfo(input).Length.ToString(CultureInfo.InvariantCulture);
            if (Targets.Count == 0) root.Remove("frameSelections");
            if (Fault == "decoded-position") foreach (var q in root["frameSelections"]!["queries"]!.AsArray())
            { q!["segmentIndex"] = 99999999; q["bufferOrdinal"] = "999999999"; q["streamNs"] = "9999999999999"; }
            if (Fault is "missing" or "ambiguous" or "incomplete") foreach (var q in root["frameSelections"]!["queries"]!.AsArray())
            {
                q!["status"] = Fault; q["matchCount"] = Fault == "ambiguous" ? "2" : "0";
                foreach (var n in new[] { "segmentIndex", "bufferOrdinal", "ptsNs", "runningNs", "streamNs", "durationNs" }) q[n] = null;
            }
            if (Fault == "eos") root["decodeEos"] = false;
            if (Fault == "reason") root["reason"] = "incomplete_test_fixture";
            await File.WriteAllTextAsync(output, root.ToJsonString(), ct); return Fault == "exit" ? 3 : 0;
        }
    }
}

