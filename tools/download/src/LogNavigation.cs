using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Playreel.Client;
using Playreel.Issuer;

namespace Playreel.Retrieval;

public sealed class LogNavigationResult
{
    public string Status { get; set; } = "unavailable";
    public string Reason { get; set; } = "navigation_evidence_unavailable";
    public object? Log { get; set; }
    public object? CallbackInterval { get; set; }
    public object? EstimatedProducerInterval { get; set; }
    public object? Frames { get; set; }
    public string Meaning => "estimated callback-observation navigation; not original log emission or physical frame capture";
    public string? PhysicalCaptureOrEmissionErrorBoundNs => null;
    public bool PlaybackOffsetVerified => false;
    public bool ContinuousCoverageClaim => false;
    public string Coverage => "unverified";
    public string EvidenceProvenance { get; set; } = "recorded_bundle_not_independently_attested";
}

// This optional local command deliberately does not extend strict generated map.
// It binds an actual callback header, estimates a clock coordinate, then confirms
// two observed endpoints. Missing continuity evidence is a refusal, not a model.
public static class LogNavigation
{
    public static async Task<LogNavigationResult> LocateAsync(string directory, string logFile, string? lineText,
        string? byteText, string output, IMediaProbe probe, CancellationToken ct)
    {
        if ((lineText is null) == (byteText is null)) throw new RetrievalError("one_log_selector_required");
        var bundle = await LocalBundle.OpenAsync(directory, ct);
        var log = bundle.LogParts.SingleOrDefault(f => f.File == logFile) ?? throw new RetrievalError("bundle_log_required");
        if (log.Bytes > 64 * RetrievalLimits.MiB) throw new RetrievalError("log_selection_limit");
        var bytes = await File.ReadAllBytesAsync(RetrievalService.LocalFile(directory, log.File), ct);
        int offset = 0, line = 1;
        if (lineText is not null)
        {
            if (!int.TryParse(lineText, NumberStyles.None, CultureInfo.InvariantCulture, out var requested) || requested < 1) throw new RetrievalError("invalid_log_line");
            while (line < requested && offset < bytes.Length) if (bytes[offset++] == 10) line++;
            if (line != requested || offset == bytes.Length) throw new RetrievalError("invalid_log_line");
        }
        else
        {
            if (!int.TryParse(byteText, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0 || offset >= bytes.Length
                || offset > 0 && bytes[offset - 1] != 10) throw new RetrievalError("log_header_boundary_required");
            for (int n = 0; n < offset; n++) if (bytes[n] == 10) line++;
        }
        int end = offset;
        while (end < bytes.Length && bytes[end] != 10 && end - offset <= 16384) end++;
        if (end - offset > 16384) throw new RetrievalError("log_header_limit");
        var header = new UTF8Encoding(false, true).GetString(bytes, offset, end - offset);
        var match = Regex.Match(header, @"^\[callbackManagedTicks=(\d{1,20}) frequency=(\d{1,20})\]", RegexOptions.CultureInvariant);
        if (!match.Success || !ulong.TryParse(match.Groups[1].Value, out var ticks) || !ulong.TryParse(match.Groups[2].Value, out var frequency) || frequency == 0)
            throw new RetrievalError("callback_header_required");
        var timing = await RetrievalService.ReadTimingAsync(directory, bundle, ct);
        var result = new LogNavigationResult { Log = new { file = log.File, sha256 = log.Sha256, line, utf8ByteOffset = offset,
            callbackManagedTicks = ticks.ToString(CultureInfo.InvariantCulture), frequencyHz = frequency.ToString(CultureInfo.InvariantCulture) },
            EvidenceProvenance = timing.ConstructedSources.Count > 0 ? "constructed_fixture" : "recorded_bundle_not_independently_attested" };
        if (bundle.Status != "retrieved" || bundle.Issues.Count != 0) { result.Reason = "bundle_incomplete"; return result; }
        if (timing.NavigationRangeOverflow) { result.Reason = "navigation_range_limit"; return result; }
        if (timing.NavigationBlocked || bundle.LogGaps.Count != 0) { result.Reason = "timing_loss_or_reset"; return result; }
        var snapshots = timing.NavigationRuns.SelectMany(r => r.GetProperty("ranges").EnumerateObject()
            .Where(p => p.Name.StartsWith("external-input:video:", StringComparison.Ordinal))
            .Select(p => (Run: Guid.Parse(TimingEvidence.Text(r, "producerRunId")), Epoch: ulong.Parse(p.Name.Split(':')[2], CultureInfo.InvariantCulture),
                Input: p.Value, Ranges: r.GetProperty("ranges")))).ToArray();
        var identities = snapshots.Select(s => (s.Run, s.Epoch)).Distinct().ToArray();
        if (identities.Length != 1) { result.Reason = "ambiguous_or_missing_epoch"; return result; }
        var identity = identities[0];
        if (timing.Ranges.Any(r => r.RunId != identity.Run || r.Epoch != identity.Epoch))
        { result.Reason = "sdk_epoch_ambiguous"; return result; }
        var bridges = timing.NavigationBridges.Where(b => b.Run == identity.Run && b.Epoch == identity.Epoch).ToArray();
        if (bridges.Length != 1 || bridges[0].ManagedFrequency != frequency) { result.Reason = "managed_bridge_ambiguous_or_frequency_mismatch"; return result; }
        var bridge = bridges[0];
        if (!RecordingSchedulingMath.TryInterval(bridge.Before, bridge.After, bridge.ManagedTicks, frequency, ticks, 0, out var callbackLow, out var callbackHigh))
        { result.Reason = "callback_clock_overflow_or_before_session"; return result; }
        result.CallbackInterval = new { beforeNs = callbackLow.ToString(CultureInfo.InvariantCulture), afterNs = callbackHigh.ToString(CultureInfo.InvariantCulture) };
        long originLow = long.MinValue, originHigh = long.MaxValue, capturedLow = long.MaxValue, capturedHigh = 0;
        long sdkOriginLow = long.MinValue, sdkOriginHigh = long.MaxValue;
        bool inputDiscontinuityObserved = false;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Ranges.EnumerateObject().Any(r => (r.Name.StartsWith("submitted:video:", StringComparison.Ordinal)
                || r.Name.StartsWith("encoded:video:", StringComparison.Ordinal)) && r.Name.Split(':')[2] != identity.Epoch.ToString(CultureInfo.InvariantCulture)))
            { result.Reason = "sdk_epoch_ambiguous"; return result; }
            if (!snapshot.Ranges.TryGetProperty($"submitted:video:{identity.Epoch}", out var submitted)) { result.Reason = "sdk_clock_origin_missing"; return result; }
            if (!snapshot.Ranges.TryGetProperty($"source:video:{identity.Epoch}", out var source)
                || !snapshot.Ranges.TryGetProperty($"encoded:video:{identity.Epoch}", out var encoded)) { result.Reason = "source_encoded_clock_relation_missing"; return result; }
            var firstInputPts = TimingEvidence.Unsigned(snapshot.Input.GetProperty("first"), "ptsNs", true);
            var lastInputPts = TimingEvidence.Unsigned(snapshot.Input.GetProperty("last"), "ptsNs", true);
            if (firstInputPts is null || lastInputPts is null || firstInputPts > lastInputPts
                || TimingEvidence.Unsigned(source.GetProperty("first"), "sourcePtsNs", true) != firstInputPts
                || TimingEvidence.Unsigned(encoded.GetProperty("first"), "runningNs", true) != firstInputPts)
            { result.Reason = "source_encoded_clock_relation_inconsistent"; return result; }
            foreach (var range in new[] { (Range: source, Field: "sourcePtsNs"), (Range: encoded, Field: "runningNs") })
            {
                var last = TimingEvidence.Unsigned(range.Range.GetProperty("last"), range.Field, true);
                if (last is null || last < firstInputPts || last > lastInputPts || TimingEvidence.Unsigned(range.Range.GetProperty("first"), "result") is not 0
                    || TimingEvidence.Unsigned(range.Range.GetProperty("last"), "result") is not 0)
                { result.Reason = "source_encoded_clock_relation_inconsistent"; return result; }
            }
            foreach (var side in new[] { "first", "last" })
            {
                var input = snapshot.Input.GetProperty(side); var sdk = submitted.GetProperty(side);
                foreach (var record in new[] { input, sdk, source.GetProperty(side), encoded.GetProperty(side) })
                    if (record.TryGetProperty("producerRunId", out var run) && run.GetString() != identity.Run.ToString("D")
                        || record.TryGetProperty("epoch", out _) && TimingEvidence.Unsigned(record, "epoch") != identity.Epoch)
                    { result.Reason = "range_identity_mismatch"; return result; }
                // native-synthetic-runner::push_input scales captureQpc against
                // one immutable originQpc before computing its DISCONT bit. A
                // gap does not rebase that clock. Preserve its presence without
                // inventing a gap start; require local decoded coverage below.
                var flags = TimingEvidence.Unsigned(input, "flags");
                if (flags is null || (flags.Value & ~1UL) != 0) { result.Reason = "unknown_input_flags"; return result; }
                inputDiscontinuityObserved |= flags != 0;
                if (TimingEvidence.Unsigned(input, "result") is not 0 || TimingEvidence.Unsigned(sdk, "result") is not 0
                    || TimingEvidence.Unsigned(input, "provenance") is not (1 or 3)) { result.Reason = "unconfirmed_external_input"; return result; }
                var qpc = TimingEvidence.Unsigned(input, "captureQpc", true); var pts = TimingEvidence.Unsigned(input, "ptsNs", true);
                var sdkPts = TimingEvidence.Unsigned(sdk, "sdkPts100ns", true); var running = TimingEvidence.Unsigned(sdk, "runningNs", true);
                if (qpc is null || pts is null || pts > long.MaxValue || sdkPts is null || sdkPts > (ulong)long.MaxValue / 100 || running is null || running > long.MaxValue
                    || bridge.CorrespondenceUncertainty100ns > ulong.MaxValue / 100
                    || !RecordingSchedulingMath.TryInterval(bridge.Before, bridge.After, bridge.Qpc, bridge.Frequency, qpc.Value,
                        bridge.CorrespondenceUncertainty100ns * 100, out var low, out var high)) { result.Reason = "clock_origin_unavailable_or_overflow"; return result; }
                originLow = Math.Max(originLow, checked(low - (long)pts.Value)); originHigh = Math.Min(originHigh, checked(high - (long)pts.Value));
                capturedLow = Math.Min(capturedLow, low); capturedHigh = Math.Max(capturedHigh, high);
                // The SDK floors nanoseconds to100ns. A submitted pair defines
                // an origin interval, not an exact origin; retain all99ns.
                var origin = checked((long)(sdkPts.Value * 100) - (long)running.Value);
                sdkOriginLow = Math.Max(sdkOriginLow, origin);
                sdkOriginHigh = Math.Min(sdkOriginHigh, origin > long.MaxValue - 99 ? long.MaxValue : origin + 99);
                if (sdkOriginLow > sdkOriginHigh) { result.Reason = "inconsistent_sdk_clock_origin"; return result; }
            }
        }
        if (originLow > originHigh || callbackLow < capturedLow || callbackHigh > capturedHigh) { result.Reason = "inconsistent_origin_or_outside_capture_range"; return result; }
        decimal translatedLow = (decimal)callbackLow - originHigh + sdkOriginLow, translatedHigh = (decimal)callbackHigh - originLow + sdkOriginHigh;
        if (translatedLow < 0 || translatedHigh < translatedLow || translatedHigh > long.MaxValue) { result.Reason = "invalid_estimated_producer_interval"; return result; }
        long targetLow = (long)translatedLow, targetHigh = (long)translatedHigh;
        result.EstimatedProducerInterval = new { runId = identity.Run, epoch = identity.Epoch.ToString(CultureInfo.InvariantCulture),
            beforeNs = targetLow.ToString(CultureInfo.InvariantCulture), afterNs = targetHigh.ToString(CultureInfo.InvariantCulture),
            inputDiscontinuityObserved, inputGapStartKnown = false,
            clockModel = "same-epoch immutable native clock origins consistent at retained endpoints; not a physical capture bound" };
        var candidates = new List<(SavedMedia Media, ulong Before, ulong After)>();
        foreach (var media in bundle.Media.Where(m => m.RunId == identity.Run && m.Epoch == identity.Epoch))
        {
            if (!media.ProbeComplete || media.Probe is null || media.Fragments.Length == 0 || media.Fragments.Distinct().Count() != media.Fragments.Length
                || media.Fragments.Any(f => !timing.Claims.Any(c => c.RunId == media.RunId && c.Epoch == media.Epoch && c.Upload == media.Upload && c.Id == f && c.IsPersisted)))
            { result.Reason = "media_attribution_or_probe_incomplete"; return result; }
            using var doc = TimingEvidence.Parse(await File.ReadAllBytesAsync(RetrievalService.LocalFile(directory, media.Probe.File), ct));
            var root = doc.RootElement; _ = ProbeEvidence.Summary(root);
            if (TimingEvidence.Text(root, "fileSha256") != media.Media.Sha256 || ProbeEvidence.Decimal(root, "fileBytes") != (ulong)media.Media.Bytes
                || TimingEvidence.Text(root, "status") != "decoded" || !root.GetProperty("decodeEos").GetBoolean()) { result.Reason = "media_probe_binding_or_completeness"; return result; }
            var videos = root.GetProperty("tracks").EnumerateArray().Where(t => TimingEvidence.Text(t, "kind") == "video").ToArray();
            if (videos.Length != 1) { result.Reason = "video_track_ambiguous"; return result; }
            // Candidate coordinates are actual span endpoints, never invented
            // interior frames. The fresh selector must confirm their identities,
            // and the query interval itself must fit inside the preceding frame.
            // A hole elsewhere in this epoch is not continuous-coverage proof.
            ulong? before = null, after = null;
            foreach (var span in videos[0].GetProperty("decoded").GetProperty("spans").EnumerateArray())
            {
                var first = ProbeEvidence.Decimal(span, "firstPtsNs"); var last = ProbeEvidence.Decimal(span, "lastPtsNs");
                foreach (var point in new[] { first, last }.Where(p => p.HasValue).Select(p => p!.Value))
                {
                    if (point <= (ulong)targetLow && (before is null || point > before)) before = point;
                    if (point >= (ulong)targetHigh && (after is null || point < after)) after = point;
                }
            }
            if (before is not null && after is not null && after >= before && after - before <= 1000000000)
                candidates.Add((media, before.Value, after.Value));
        }
        if (candidates.Count != 1) { result.Reason = candidates.Count == 0 ? "observed_bracket_gap_or_too_wide" : "ambiguous_observed_bracket"; return result; }
        if (!Path.IsPathFullyQualified(output) || Directory.Exists(output) || File.Exists(output)) throw new RetrievalError("new_absolute_output_required");
        Directory.CreateDirectory(output);
        var candidate = candidates[0]; var probePath = Path.Combine(output, "navigation-probe.json");
        var targets = ProbeEvidence.ValidateTargets(new[] { candidate.Before, candidate.After }.Distinct().ToArray());
        if (await probe.RunAsync(RetrievalService.LocalFile(directory, candidate.Media.Media.File), probePath, ct, targets) != 0
            || !File.Exists(probePath) || new FileInfo(probePath).Length > 2 * RetrievalLimits.MiB) { result.Reason = "selected_probe_incomplete"; return result; }
        using var selectedDoc = TimingEvidence.Parse(await File.ReadAllBytesAsync(probePath, ct));
        var selectedRoot = selectedDoc.RootElement; _ = ProbeEvidence.Summary(selectedRoot);
        if (TimingEvidence.Text(selectedRoot, "fileSha256") != candidate.Media.Media.Sha256 || ProbeEvidence.Decimal(selectedRoot, "fileBytes") != (ulong)candidate.Media.Media.Bytes
            || TimingEvidence.Text(selectedRoot, "status") != "decoded" || !selectedRoot.GetProperty("decodeEos").GetBoolean()) { result.Reason = "selected_probe_binding_or_completeness"; return result; }
        var selections = ProbeEvidence.Selections(selectedRoot);
        if (!selections.Select(s => s.TargetNs).Order().SequenceEqual(targets.Order()) || selections.Any(s => s.Status != "unique_observed" || s.PtsNs != s.TargetNs)
            || selections.Select(s => s.SegmentIndex).Distinct().Count() != 1) { result.Reason = "selected_endpoint_ambiguous_or_changed"; return result; }
        var preceding = selections.Single(s => s.TargetNs == candidate.Before);
        if (preceding.DurationNs is null || (UInt128)preceding.TargetNs + preceding.DurationNs.Value <= (ulong)targetHigh)
        { result.Reason = "estimated_interval_in_decoded_gap"; return result; }
        result.Frames = new { file = candidate.Media.Media.File, sha256 = candidate.Media.Media.Sha256,
            probeSha256 = await RetrievalService.HashAsync(probePath, ct), observed = selections.OrderBy(s => s.TargetNs).Select(s => new { ptsNs = s.PtsNs!.Value.ToString(CultureInfo.InvariantCulture),
                bufferOrdinal = s.BufferOrdinal!.Value.ToString(CultureInfo.InvariantCulture), segment = s.SegmentIndex!.Value.ToString(CultureInfo.InvariantCulture),
                playbackOffsetSeconds = s.StreamNs!.Value / 1000000000m, distanceFromEstimateNs = s.TargetNs < (ulong)targetLow ? (targetLow - (long)s.TargetNs).ToString(CultureInfo.InvariantCulture)
                    : ((long)s.TargetNs - targetHigh).ToString(CultureInfo.InvariantCulture) }) };
        result.Status = "estimated"; result.Reason = "observed_endpoints_bracket_clock_estimate";
        await File.WriteAllTextAsync(Path.Combine(output, "navigation.json"), JsonSerializer.Serialize(result, Wire.Json), ct);
        return result;
    }
}
