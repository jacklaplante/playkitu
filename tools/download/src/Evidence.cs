using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Playreel.Client;

namespace Playreel.Retrieval;

public sealed record LogRange(int Sequence, long ByteStart, long ByteLength, long StartNs, long EndNs);
public sealed record LogGap(long? AcceptedByteOffset, ulong Bytes, int? Sequence, string Reason);
public sealed record ManagedBridge(Guid Run, ulong Epoch, ulong Qpc, ulong Frequency, long Before, long After,
    ulong ManagedTicks, ulong ManagedAfterTicks, ulong ManagedFrequency, ulong NativeUtcUncertainty100ns, ulong CorrespondenceUncertainty100ns);
public sealed record SchedulingPoint(Guid Run, ulong ProducerEpoch, ulong ClockEpoch, ulong Sequence, ulong FrameOrdinal,
    ulong TargetNs, long BeforeNs, long AfterNs, ulong NativeUncertaintyNs, long ManagedEnclosureNs, ulong PadArrivalQpc);

public sealed class TimingEvidence
{
    public List<ProducerRange> Ranges { get; } = [];
    public List<FragmentClaim> Claims { get; } = [];
    public List<LogRange> Logs { get; } = [];
    public List<LogGap> LogGaps { get; } = [];
    public ulong DroppedLogBytes { get; private set; }
    public HashSet<int> ReferencedLogSequences { get; } = [];
    public List<string> Uncertainties { get; } = [];
    public DateTimeOffset? StartUtc { get; private set; }
    public ulong? ClockUncertaintyNs { get; private set; }
    public int LatestManifest { get; private set; } = -1;
    private readonly HashSet<string> dedup = [];
    private readonly List<ManagedBridge> bridges = [];
    private readonly Dictionary<(Guid Run, ulong Sequence), JsonElement> groups = [];
    private readonly HashSet<Guid> blockedRuns = [];
    private bool managedLoss;
    // Owner-only estimated log navigation uses existing external-input ranges;
    // these never become generated scheduling groups or strict map support.
    internal List<JsonElement> NavigationRuns { get; } = [];
    internal bool NavigationRangeOverflow { get; private set; }
    internal IReadOnlyList<ManagedBridge> NavigationBridges => bridges;
    internal bool NavigationBlocked => managedLoss || blockedRuns.Count != 0 || Uncertainties.Count != 0;
    public HashSet<string> ConstructedSources { get; } = [];

    public static JsonDocument Parse(ReadOnlyMemory<byte> bytes) => JsonDocument.Parse(bytes,
        new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 24, AllowDuplicateProperties = false });
    public static ulong? Unsigned(JsonElement e, string name, bool timestamp = false)
    {
        if (!e.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var number)) throw new RetrievalError("invalid_unsigned_timing");
        return timestamp && number == ulong.MaxValue ? null : number;
    }
    public static long Nonnegative(JsonElement e, string name)
    {
        var value = Unsigned(e, name);
        if (value is null || value > long.MaxValue) throw new RetrievalError("invalid_elapsed_range");
        return (long)value;
    }
    public static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
        ? p.GetString()! : throw new RetrievalError("invalid_evidence_shape");
    public static DateTimeOffset ExplicitUtc(string text)
    {
        if (!Regex.IsMatch(text, @"(Z|[+-][0-9]{2}:[0-9]{2})$") || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time)) throw new RetrievalError("timestamp_offset_required");
        return time.ToUniversalTime();
    }
    public void Add(JsonElement root, Guid session, int generation, string kind, int sequence, ProducerWindow? window = null)
    {
        if (Unsigned(root, "schemaVersion") != 1 || Text(root, "sessionId") != session.ToString("D")
            || Unsigned(root, "admissionGeneration") != (ulong)generation || Text(root, "objectKind") != kind || Unsigned(root, "sequence") != (ulong)sequence)
            throw new RetrievalError("timing_binding_mismatch");
        if (root.TryGetProperty("sessionClock", out var clock))
        {
            if (Unsigned(clock, "frequencyHz") is null or 0 || Nonnegative(clock, "sessionElapsedNs") != 0) throw new RetrievalError("invalid_session_clock");
            var utc = ExplicitUtc(Text(clock, "utc")); var uncertainty = Unsigned(clock, "uncertaintyNs");
            _ = Unsigned(clock, "startTicks");
            if (StartUtc.HasValue && (StartUtc != utc || ClockUncertaintyNs != uncertainty)) throw new RetrievalError("conflicting_session_clock");
            StartUtc = utc; ClockUncertaintyNs = uncertainty;
        }
        foreach (var name in new[] { "droppedRecords", "droppedRuns", "droppedObjects", "droppedLogBytes" })
            if (Unsigned(root, name) is > 0) Uncertainties.Add(name);
        // Every index/manifest repeats the same session cumulative counter.
        // Take its high-water mark across verified snapshots, never their sum:
        // event eviction can remove a gap row without removing this evidence.
        DroppedLogBytes = Math.Max(DroppedLogBytes, Unsigned(root, "droppedLogBytes") ?? 0);
        managedLoss |= new[] { "droppedRecords", "droppedRuns", "droppedObjects", "droppedLogBytes" }.Any(n => Unsigned(root, n) is > 0);
        // Optional retrieval-fixture annotation can only downgrade provenance.
        // It is never an attestation of an observed managed/media same-run chain.
        if (root.TryGetProperty("retrievalFixture", out var fixture))
        {
            if (Unsigned(fixture, "schema") != 1 || Text(fixture, "kind") != "constructed" || fixture.GetProperty("sourceSha256").GetArrayLength() is < 1 or > 8)
                throw new RetrievalError("invalid_fixture_provenance");
            foreach (var hash in fixture.GetProperty("sourceSha256").EnumerateArray())
            {
                var value = hash.GetString() ?? "";
                if (value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c))) throw new RetrievalError("invalid_fixture_provenance");
                ConstructedSources.Add(value.ToLowerInvariant());
            }
        }
        if (kind == "manifest")
        {
            LatestManifest = Math.Max(LatestManifest, sequence);
            if (root.TryGetProperty("latestPostConfirmed", out var confirmed) && confirmed.TryGetProperty("log", out var mentioned))
            {
                if (!mentioned.TryGetInt32(out var number) || number is < -1 or >= 4096) throw new RetrievalError("invalid_log_sequence");
                if (number >= 0 && (window is null || window.LogSequences.Contains(number))) ReferencedLogSequences.Add(number); // A maximum is not contiguous success.
            }
        }
        if (root.TryGetProperty("producerRuns", out var runs)) foreach (var run in runs.EnumerateArray())
        {
            var id = Guid.ParseExact(Text(run, "producerRunId"), "D");
            if (run.TryGetProperty("ranges", out var navigationRanges) && navigationRanges.EnumerateObject().Any(r => r.Name.StartsWith("external-input:video:", StringComparison.Ordinal)))
            {
                // Navigation-only exhaustion must not change existing strict
                // fetch/inspect/map acceptance or grow an unbounded collection.
                if (NavigationRuns.Count < 1024) NavigationRuns.Add(run.Clone());
                else NavigationRangeOverflow = true;
            }
            if (run.TryGetProperty("clockCorrespondence", out var bridge)) AddBridge(bridge, id);
            if (run.TryGetProperty("ranges", out var ranges)) foreach (var range in ranges.EnumerateObject())
            {
                var parts = range.Name.Split(':');
                if (parts.Length != 3 || !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)) throw new RetrievalError("invalid_range_key");
                if (parts[0] != "submitted" || parts[1] != "video") continue;
                var firstRecord = range.Value.GetProperty("first"); var lastRecord = range.Value.GetProperty("last");
                if (Unsigned(firstRecord, "result") is not 0 || Unsigned(lastRecord, "result") is > 0)
                { Uncertainties.Add("unconfirmed_submitted_range"); continue; }
                var first = Unsigned(firstRecord, "sdkPts100ns", true);
                var last = Unsigned(lastRecord, "sdkPts100ns", true);
                if (first is null || last is null) { Uncertainties.Add("unknown_submitted_range"); continue; }
                if (last < first) throw new RetrievalError("regressing_producer_range");
                Ranges.Add(new(id, epoch, first.Value, last.Value));
            }
        }
        if (root.TryGetProperty("records", out var records)) foreach (var e in records.EnumerateArray())
        {
            var eventKind = Text(e, "kind");
            // Integer validation covers the common clock and native fields even
            // when a record is not needed for selecting downloads. Never round
            // uint64 through double or reinterpret unsigned unknowns as -1.
            foreach (var name in new[] { "sequence", "epoch", "flags", "result", "upload", "ackType", "droppedObservations", "runningNs", "sdkPts100ns", "ackTimecodeMs" }) _ = Unsigned(e, name);
            if (Unsigned(e, "droppedObservations") is > 0) Uncertainties.Add("native_observation_loss");
            if (eventKind == "clock") AddBridge(e, Guid.ParseExact(Text(e, "producerRunId"), "D"));
            if (eventKind is "clock-group" or "clock-loss") AddClock(e);
            if (eventKind is "reset" or "gap" || Unsigned(e, "droppedObservations") is > 0)
            {
                if (e.TryGetProperty("producerRunId", out var runId) && Guid.TryParse(runId.GetString(), out var id)) blockedRuns.Add(id);
                else managedLoss = true;
            }
            if (eventKind == "ack")
            {
                var id = Guid.ParseExact(Text(e, "producerRunId"), "D"); var epoch = Unsigned(e, "epoch") ?? throw new RetrievalError("invalid_ack");
                var upload = Unsigned(e, "upload") ?? throw new RetrievalError("invalid_ack"); var fragment = Text(e, "fragment");
                var type = Unsigned(e, "ackType") ?? throw new RetrievalError("invalid_ack"); var result = Unsigned(e, "result") ?? throw new RetrievalError("invalid_ack");
                if (fragment.Length is < 1 or > 128 || fragment.Any(c => c < 33 || c > 126)) throw new RetrievalError("invalid_ack");
                var timecode = Unsigned(e, "ackTimecodeMs", true);
                // Keep original JSON intact. Only this window's claims enter the
                // bounded aggregate; global loss/reset summaries remain blocking.
                if (window is not null && !window.Contains(timecode)) continue;
                // Repeated snapshots are expected. Preserve conflicting ACK
                // clocks so attribution can reject them instead of keeping the
                // first arrival and silently treating it as authoritative.
                var key = $"{id:N}:{epoch}:{upload}:{fragment}:{type}:{result}:{timecode}";
                if (dedup.Add(key)) Claims.Add(new(id, epoch, upload, fragment, timecode, type, result));
            }
            if (eventKind == "log-range")
            {
                var s = checked((int)Nonnegative(e, "logSequence")); var offset = Nonnegative(e, "byteStart"); var length = Nonnegative(e, "byteLength");
                var start = Nonnegative(e, "elapsedStartNs"); var end = Nonnegative(e, "elapsedEndNs");
                if (s >= 4096 || end < start) throw new RetrievalError("invalid_log_range");
                Logs.Add(new(s, offset, length, start, end));
                ReferencedLogSequences.Add(s);
            }
            if (eventKind == "reset" || eventKind == "gap") Uncertainties.Add(eventKind);
            if (eventKind == "gap" && e.TryGetProperty("logBytes", out _))
            {
                var at = Unsigned(e, "acceptedLogByteOffset"); var seq = Unsigned(e, "objectSequence");
                if (at > long.MaxValue || seq >= 4096) throw new RetrievalError("invalid_log_gap");
                LogGaps.Add(new(at is null ? null : (long)at, Unsigned(e, "logBytes") ?? throw new RetrievalError("invalid_log_gap"), seq is null ? null : (int)seq,
                    e.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "gap" : "gap"));
            }
        }
        if (Ranges.Count > 4096 || Claims.Count > 4096 || Logs.Count > 12288 || Uncertainties.Count > 12288) throw new RetrievalError("evidence_record_limit");
    }

    private void AddBridge(JsonElement e, Guid run)
    {
        var clock = e.GetProperty("observationClock");
        foreach (var n in new[] { "managedTicks", "managedAfterTicks", "managedFrequencyHz", "correspondenceQpc", "correspondenceUncertainty100ns" }) _ = Unsigned(e, n, true);
        foreach (var n in new[] { "qpc", "frequencyHz", "unix100ns", "uncertainty100ns" }) _ = Unsigned(clock, n, true);
        // Old clock rows remain readable but cannot supply an invented bracket.
        if (Unsigned(e, "epoch") is null || new[] { "managedTicks", "managedAfterTicks", "managedFrequencyHz", "correspondenceQpc", "correspondenceUncertainty100ns", "managedBeforeElapsedNs", "managedAfterElapsedNs" }.Any(n => Unsigned(e, n, true) is null)
            || new[] { "qpc", "frequencyHz", "uncertainty100ns" }.Any(n => Unsigned(clock, n, true) is null)) return;
        var before = Nonnegative(e, "managedBeforeElapsedNs"); var after = Nonnegative(e, "managedAfterElapsedNs");
        var ticks = Unsigned(e, "managedTicks")!.Value; var last = Unsigned(e, "managedAfterTicks")!.Value;
        var frequency = Unsigned(clock, "frequencyHz")!.Value; var managedFrequency = Unsigned(e, "managedFrequencyHz")!.Value;
        var qpc = Unsigned(e, "correspondenceQpc")!.Value;
        if (run == Guid.Empty || after < before || last < ticks || frequency == 0 || managedFrequency == 0 || Unsigned(clock, "qpc") != qpc)
            throw new RetrievalError("invalid_managed_bridge");
        var value = new ManagedBridge(run, Unsigned(e, "epoch")!.Value, qpc, frequency, before, after, ticks, last, managedFrequency,
            Unsigned(clock, "uncertainty100ns")!.Value, Unsigned(e, "correspondenceUncertainty100ns")!.Value);
        if (!bridges.Contains(value)) bridges.Add(value);
        if (bridges.Count > 256) throw new RetrievalError("clock_record_limit");
    }

    private static readonly string[] ClockFields = ["size", "version", "kind", "flags", "track", "reason", "sourceSegmentSeq", "encodedSegmentSeq",
        "sequence", "frameOrdinal", "clockEpoch", "producerEpoch", "qpcBefore", "qpcAfter", "qpcFrequency", "gstClockTimeNs", "pipelineBaseTimeNs", "clockResolutionNs",
        "calibrationInternalNs", "calibrationExternalNs", "calibrationRateNumerator", "calibrationRateDenominator", "padArrivalQpc", "estimatedScheduleQpc", "scheduleUncertaintyNs",
        "sourcePtsNs", "sourceDurationNs", "sourceRunningNs", "sourceSegmentStartNs", "sourceSegmentBaseNs", "encodedPtsNs", "encodedRunningNs", "encodedSegmentStartNs", "encodedSegmentBaseNs", "sdkInputRunningNs", "sdkPts100ns"];
    private void AddClock(JsonElement e)
    {
        var run = Guid.ParseExact(Text(e, "producerRunId"), "D");
        if (run == Guid.Empty || Unsigned(e, "clockSchemaVersion") != 2 || Unsigned(e, "droppedTotal") is null) throw new RetrievalError("invalid_clock_group");
        if (Unsigned(e, "droppedTotal") is > 0) blockedRuns.Add(run);
        if (Text(e, "kind") == "clock-loss")
        { if (Text(e, "position") != "unlocalized") throw new RetrievalError("invalid_clock_loss"); Uncertainties.Add("clock_loss"); return; }
        if (Text(e, "meaning") != "generated-scheduling" || e.GetProperty("records").GetArrayLength() is < 1 or > 3) throw new RetrievalError("invalid_clock_group");
        foreach (var row in e.GetProperty("records").EnumerateArray()) foreach (var name in ClockFields)
        {
            var value = Unsigned(row, name);
            if (Array.IndexOf(ClockFields, name) < 8 && (value is null || value > uint.MaxValue)) throw new RetrievalError("invalid_clock_field");
        }
        var rows = e.GetProperty("records"); var sequence = Unsigned(rows[rows.GetArrayLength() - 1], "sequence");
        if (sequence is null) { Uncertainties.Add("clock_group_unavailable"); return; }
        if (groups.TryGetValue((run, sequence.Value), out var previous))
        {
            // Immutable repeated evidence must agree semantically, irrespective
            // of property order/whitespace. Conflicts cannot pick a winning row.
            if (previous.GetProperty("usable").GetBoolean() != e.GetProperty("usable").GetBoolean()
                || previous.GetProperty("records").GetArrayLength() != rows.GetArrayLength()
                || previous.GetProperty("records").EnumerateArray().SelectMany(r => ClockFields.Select(n => Unsigned(r, n)))
                    .SequenceEqual(rows.EnumerateArray().SelectMany(r => ClockFields.Select(n => Unsigned(r, n)))) == false) blockedRuns.Add(run);
        }
        else groups.Add((run, sequence.Value), e.Clone());
        if (groups.Count > 256) throw new RetrievalError("clock_record_limit");
    }

    public IReadOnlyList<SchedulingPoint> SchedulingPoints()
    {
        var result = new List<SchedulingPoint>();
        if (managedLoss) return result; // Eviction/loss is unlocalized: never silently bridge it.
        foreach (var pair in groups)
        {
            var run = pair.Key.Run; var group = pair.Value; var rows = group.GetProperty("records");
            if (blockedRuns.Contains(run) || !group.GetProperty("usable").GetBoolean() || rows.GetArrayLength() != 3) continue;
            var a = rows[0]; var b = rows[1]; var f = rows[2];
            ulong? U(JsonElement row, string name) => Unsigned(row, name, Array.IndexOf(ClockFields, name) >= 12);
            if (rows.EnumerateArray().Any(r => ClockFields.Any(n => U(r, n) is null && (r.GetProperty("kind").GetUInt32() == 1 || Array.IndexOf(ClockFields, n) < 22)))) continue;
            ulong V(JsonElement row, string name) => U(row, name)!.Value;
            if (rows.EnumerateArray().Any(r => V(r, "size") != 256 || V(r, "version") != 2 || V(r, "track") != 1 || V(r, "reason") != 0
                || V(r, "frameOrdinal") != V(f, "frameOrdinal") || V(r, "clockEpoch") != V(f, "clockEpoch") || V(r, "producerEpoch") != V(f, "producerEpoch")
                || V(r, "qpcFrequency") != V(f, "qpcFrequency"))
                || V(a, "kind") != 3 || V(b, "kind") != 3 || V(f, "kind") != 1 || (V(f, "flags") & 15) != 15
                || V(a, "sequence") == 0 || V(a, "sequence") >= ulong.MaxValue - 1 || V(a, "sequence") + 1 != V(b, "sequence") || V(b, "sequence") + 1 != V(f, "sequence")
                || V(f, "clockEpoch") == 0 || V(f, "sourceSegmentSeq") == 0 || V(f, "encodedSegmentSeq") == 0 || V(f, "qpcFrequency") == 0
                || V(a, "qpcBefore") > V(a, "qpcAfter") || V(a, "qpcAfter") >= V(b, "qpcBefore") || V(b, "qpcBefore") > V(b, "qpcAfter")
                || V(f, "estimatedScheduleQpc") < V(a, "qpcBefore") || V(f, "estimatedScheduleQpc") > V(b, "qpcAfter")
                || V(f, "sourceRunningNs") != V(f, "encodedRunningNs") || V(f, "sourceRunningNs") != V(f, "sdkInputRunningNs")) continue;
            var common = new[] { "pipelineBaseTimeNs", "calibrationInternalNs", "calibrationExternalNs", "calibrationRateNumerator", "calibrationRateDenominator" };
            if (common.Any(n => V(a, n) != V(b, n) || V(a, n) != V(f, n)) || V(a, "calibrationRateNumerator") == 0 || V(a, "calibrationRateDenominator") == 0) continue;
            var target = (decimal)V(f, "pipelineBaseTimeNs") + V(f, "sourceRunningNs");
            if (target > ulong.MaxValue || target <= V(a, "gstClockTimeNs") || target >= V(b, "gstClockTimeNs")) continue;
            // Audit the claimed native scheduling error against its retained
            // SUPPORT enclosure. This is an exact inequality, not a new clock
            // calibration or an uncertainty term to add to the interval again.
            // UInt128 holds every uint64*uint64 product without rounding.
            var resolution = Math.Max(V(f, "clockResolutionNs"), Math.Max(V(a, "clockResolutionNs"), V(b, "clockResolutionNs")));
            var uncertainty = V(f, "scheduleUncertaintyNs"); var estimate = V(f, "estimatedScheduleQpc");
            var distance = Math.Max(estimate - V(a, "qpcBefore"), V(b, "qpcAfter") - estimate);
            if (uncertainty < resolution || (UInt128)(uncertainty - resolution) * V(f, "qpcFrequency") < (UInt128)distance * 1000000000UL) continue;
            var candidates = bridges.Where(c => c.Run == run && c.Epoch == V(f, "producerEpoch") && c.Frequency == V(f, "qpcFrequency")).ToArray();
            if (candidates.Length != 1 || V(f, "sdkPts100ns") > (ulong)long.MaxValue / 100) continue;
            var bridge = candidates[0];
            if (!RecordingSchedulingMath.TryInterval(bridge.Before, bridge.After, bridge.Qpc, bridge.Frequency, V(f, "estimatedScheduleQpc"), V(f, "scheduleUncertaintyNs"), out var low, out var high)) continue;
            result.Add(new(run, V(f, "producerEpoch"), V(f, "clockEpoch"), V(f, "sequence"), V(f, "frameOrdinal"), checked(V(f, "sdkPts100ns") * 100), low, high,
                V(f, "scheduleUncertaintyNs"), bridge.After - bridge.Before, V(f, "padArrivalQpc")));
        }
        return result;
    }

    public IReadOnlyList<ProducerRange> MergedRanges(RetrievalLimits limits, ProducerWindow? window = null)
    {
        var merged = new List<ProducerRange>();
        foreach (var group in Ranges.GroupBy(r => (r.RunId, r.Epoch)))
        {
            ProducerRange? previous = null;
            foreach (var range in group.OrderBy(r => r.First100ns))
            {
                if (previous is not null && range.First100ns <= previous.Last100ns)
                    previous = previous with { Last100ns = Math.Max(previous.Last100ns, range.Last100ns) };
                else { if (previous is not null) merged.Add(previous); previous = range; }
            }
            if (previous is not null) merged.Add(previous);
        }
        if (window is not null) merged = merged.Where(r => r.Last100ns >= window.From100ns && r.First100ns < window.To100ns)
            .Select(r => r with { First100ns = Math.Max(r.First100ns, window.From100ns), Last100ns = Math.Min(r.Last100ns, window.To100ns - 1) }).ToList();
        ulong total = 0;
        foreach (var r in merged) { total = checked(total + r.Last100ns - r.First100ns); _ = Unix(r.First100ns); _ = Unix(r.Last100ns); }
        if (total > (ulong)limits.SourceSeconds * 10000000) throw new RetrievalError("source_range_limit");
        // A fragment returned for overlapping run/epoch ranges has no reliable
        // attribution. Stop media selection rather than picking one arbitrarily.
        var sorted = merged.OrderBy(r => r.First100ns).ToArray();
        for (var i = 1; i < sorted.Length; i++) if (sorted[i].First100ns <= sorted[i - 1].Last100ns)
            throw new RetrievalError("ambiguous_producer_ranges");
        return sorted;
    }
    public static DateTimeOffset Unix(ulong ticks)
    {
        if (ticks > (ulong)(DateTimeOffset.MaxValue - DateTimeOffset.UnixEpoch).Ticks) throw new RetrievalError("producer_timestamp_overflow");
        return DateTimeOffset.UnixEpoch.AddTicks((long)ticks);
    }
}

public static class ProbeEvidence
{
    public sealed record Selection(ulong QueryId, ulong TargetNs, ulong ToleranceNs, string Status, ulong MatchCount,
        ulong? BufferOrdinal, ulong? SegmentIndex, ulong? PtsNs, ulong? RunningNs, ulong? StreamNs, ulong? DurationNs, JsonElement Evidence);
    public static ulong[] ValidateTargets(IReadOnlyList<ulong>? targets)
    {
        var values = targets?.ToArray() ?? [];
        if (values.Length > 64 || values.Distinct().Count() != values.Length || values.Any(n => n > long.MaxValue)) throw new RetrievalError("probe_target_limit");
        return values;
    }
    public static IReadOnlyList<Selection> Selections(JsonElement root)
    {
        if (!root.TryGetProperty("frameSelections", out var selections)) return [];
        if (TimingEvidence.Unsigned(selections, "schema") != 1 || TimingEvidence.Text(selections, "scope") != "observed_file"
            || selections.GetProperty("queries").GetArrayLength() > 64) throw new RetrievalError("invalid_probe_selection");
        var result = new List<Selection>(); var ids = new HashSet<ulong>(); var targets = new HashSet<ulong>();
        foreach (var q in selections.GetProperty("queries").EnumerateArray())
        {
            var id = TimingEvidence.Unsigned(q, "queryId") ?? throw new RetrievalError("invalid_probe_selection");
            var target = Decimal(q, "targetPtsNs") ?? throw new RetrievalError("invalid_probe_selection");
            var tolerance = Decimal(q, "toleranceNs") ?? throw new RetrievalError("invalid_probe_selection");
            var count = Decimal(q, "matchCount") ?? throw new RetrievalError("invalid_probe_selection");
            var status = TimingEvidence.Text(q, "status"); var ordinal = Decimal(q, "bufferOrdinal"); var segment = TimingEvidence.Unsigned(q, "segmentIndex");
            var pts = Decimal(q, "ptsNs"); var running = Decimal(q, "runningNs"); var stream = Decimal(q, "streamNs"); var duration = Decimal(q, "durationNs");
            if (id == 0 || !ids.Add(id) || !targets.Add(target) || target > long.MaxValue || tolerance > 1000000
                || status is not ("unique_observed" or "missing" or "ambiguous" or "incomplete")
                || status == "missing" && count != 0 || status == "ambiguous" && count < 2) throw new RetrievalError("invalid_probe_selection");
            if (status == "unique_observed")
            {
                if (count != 1 || ordinal is null or 0 || segment is null or 0 || pts is null || running is null || stream is null || duration is null
                    || running == ulong.MaxValue || stream == ulong.MaxValue || duration == ulong.MaxValue
                    || pts > long.MaxValue || (target > pts ? target - pts : pts - target) > tolerance) throw new RetrievalError("invalid_probe_selection");
            }
            else if (ordinal is not null || segment is not null || pts is not null || running is not null || stream is not null || duration is not null)
                throw new RetrievalError("invalid_probe_selection");
            var value = new Selection(id, target, tolerance, status, count, ordinal, segment, pts, running, stream, duration, q.Clone());
            if (status == "unique_observed") ValidateDecodedPosition(root, value);
            result.Add(value);
        }
        return result;
    }
    private static void ValidateDecodedPosition(JsonElement root, Selection selection)
    {
        var video = root.GetProperty("tracks").EnumerateArray().Where(t => TimingEvidence.Text(t, "kind") == "video").ToArray();
        if (video.Length != 1) throw new RetrievalError("inconsistent_decoded_selection");
        var decoded = video[0].GetProperty("decoded"); var segments = decoded.GetProperty("segments"); var spans = decoded.GetProperty("spans");
        var count = Decimal(decoded, "buffers");
        if (count is null || selection.BufferOrdinal > count || selection.SegmentIndex > (ulong)segments.GetArrayLength()
            || !decoded.GetProperty("eosObserved").GetBoolean()) throw new RetrievalError("inconsistent_decoded_selection");
        var segment = segments[checked((int)selection.SegmentIndex!.Value - 1)];
        // Initial selected-point support is TIME/1x only. Reuse observed span
        // coordinates below; do not rebuild GStreamer's segment transform.
        if (TimingEvidence.Unsigned(segment, "format") != 3 || segment.GetProperty("rate").GetDecimal() != 1
            || segment.GetProperty("appliedRate").GetDecimal() != 1) throw new RetrievalError("inconsistent_decoded_selection");
        ulong ordinal = 0; JsonElement? owner = null; ulong firstOrdinal = 0, lastOrdinal = 0;
        foreach (var span in spans.EnumerateArray())
        {
            var buffers = Decimal(span, "buffers");
            if (buffers is null or 0 || buffers > ulong.MaxValue - ordinal) throw new RetrievalError("inconsistent_decoded_selection");
            var end = ordinal + buffers.Value;
            if (selection.BufferOrdinal > ordinal && selection.BufferOrdinal <= end)
            { owner = span; firstOrdinal = ordinal + 1; lastOrdinal = end; }
            ordinal = end;
        }
        if (ordinal != count || owner is null) throw new RetrievalError("inconsistent_decoded_selection");
        var observed = owner.Value;
        if (TimingEvidence.Unsigned(observed, "segmentIndex") != selection.SegmentIndex) throw new RetrievalError("inconsistent_decoded_selection");
        var selectedTimes = new[] { selection.PtsNs!.Value, selection.RunningNs!.Value, selection.StreamNs!.Value };
        var coordinates = new[] { "PtsNs", "RunningNs", "StreamNs" };
        decimal? selectedDelta = null, observedDelta = null;
        for (var n = 0; n < coordinates.Length; n++)
        {
            var first = Decimal(observed, "first" + coordinates[n]); var last = Decimal(observed, "last" + coordinates[n]);
            var end = Decimal(observed, "observedEnd" + coordinates[n]); var lastDuration = Decimal(observed, "lastBufferDurationNs");
            if (first is null || last is null || end is null || lastDuration is null || first > last
                || (UInt128)last.Value + lastDuration.Value != end.Value || selectedTimes[n] < first || selectedTimes[n] > last
                || (UInt128)selectedTimes[n] + selection.DurationNs!.Value > end.Value
                || selection.BufferOrdinal == firstOrdinal && selectedTimes[n] != first
                || selection.BufferOrdinal == lastOrdinal && (selectedTimes[n] != last || selection.DurationNs != lastDuration))
                throw new RetrievalError("inconsistent_decoded_selection");
            var delta = (decimal)selectedTimes[n] - first.Value; var extent = (decimal)last.Value - first.Value;
            if (selectedDelta is not null && delta != selectedDelta || observedDelta is not null && extent != observedDelta)
                throw new RetrievalError("inconsistent_decoded_selection");
            selectedDelta = delta; observedDelta = extent;
        }
    }
    public static ulong? Decimal(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String || !ulong.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            throw new RetrievalError("invalid_probe_integer");
        return n;
    }
    public static object Summary(JsonElement root)
    {
        if (root.GetProperty("schema").GetInt32() != 1 || TimingEvidence.Text(root, "kind") != "playreel.decoded-media-probe") throw new RetrievalError("unsupported_probe");
        var tracks = new List<object>();
        foreach (var t in root.GetProperty("tracks").EnumerateArray())
        {
            foreach (var stage in new[] { "demux", "decoded" })
            {
                var data = t.GetProperty(stage);
                foreach (var name in new[] { "buffers", "firstPtsNs", "lastPtsNs", "firstRunningNs", "lastRunningNs", "lastBufferDurationNs" }) _ = Decimal(data, name);
                foreach (var span in data.GetProperty("spans").EnumerateArray()) foreach (var p in span.EnumerateObject())
                    if (p.Name.EndsWith("Ns", StringComparison.Ordinal) || p.Name == "buffers") _ = Decimal(span, p.Name);
            }
            tracks.Add(new { kind = TimingEvidence.Text(t, "kind"), demuxBuffers = Decimal(t.GetProperty("demux"), "buffers")?.ToString(CultureInfo.InvariantCulture),
                decodedBuffers = Decimal(t.GetProperty("decoded"), "buffers")?.ToString(CultureInfo.InvariantCulture),
                spans = t.GetProperty("decoded").GetProperty("spans").Clone() });
        }
        var selections = Selections(root);
        return new { status = TimingEvidence.Text(root, "status"), decodeEos = root.GetProperty("decodeEos").GetBoolean(),
            fileCompleteness = "unknown", continuousCoverageClaim = false, playbackOffsetVerified = false, tracks, selections = selections.Select(s => s.Evidence).ToArray() };
    }
}
