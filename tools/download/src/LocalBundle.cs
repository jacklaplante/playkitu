using Playreel.Issuer;
using System.Globalization;
using System.Text.Json;

namespace Playreel.Retrieval;

public sealed class MappingResult
{
    public string Status { get; set; } = "unverified";
    public string Reason { get; set; } = "producer_to_playback_anchor_unavailable";
    public decimal? PlaybackOffsetSeconds { get; set; }
    public string SessionElapsedNs { get; set; } = "";
    public string? UtcAnchorUncertaintyNs { get; set; }
    public string TimestampMeaning { get; set; } = "session-anchor estimate; log ranges describe supplied-call intervals, not per-line emission";
    public IReadOnlyList<LogRange> LogRanges { get; set; } = [];
    public string[] EvidenceUncertainties { get; set; } = [];
    public object? SelectedPoint { get; set; }
    public string EvidenceProvenance { get; set; } = "recorded_bundle_not_independently_attested";
    public string[] ConstructionSourceSha256 { get; set; } = [];
    public bool SameRunLiveProofVerified => false;
    public bool PlaybackOffsetVerified => false; // Global proof/coverage flags stay unchanged by a selected point.
    public string Coverage => "unverified";
    public bool ContinuousCoverageClaim => false;
    public bool SourceOpeningKnown => false;
    public bool SourceTailKnown => false;
}

public static class LocalBundle
{
    public static async Task<RetrievalBundle> OpenAsync(string directory, CancellationToken ct, string expectedKind = "playreel.owner-retrieval", bool verifyFiles = true)
    {
        directory = Path.GetFullPath(directory);
        if (!verifyFiles && expectedKind != "playreel.owner-metadata-page") throw new RetrievalError("invalid_metadata_validation_mode");
        var index = RetrievalService.LocalFile(directory, "bundle.json");
        if (new FileInfo(index).Length > 2 * RetrievalLimits.MiB) throw new RetrievalError("bundle_index_limit");
        var bundle = JsonSerializer.Deserialize<RetrievalBundle>(await File.ReadAllBytesAsync(index, ct), Wire.Json) ?? throw new RetrievalError("invalid_bundle");
        bundle.Limits.Validate();
        if (bundle.Schema != 1 || bundle.Kind != expectedKind || bundle.Coverage != "unverified"
            || bundle.SessionId == Guid.Empty || !Guid.TryParseExact(bundle.AdmissionId, "N", out _) || bundle.AdmissionGeneration is < 1 or > 3
            || bundle.Objects.Count > bundle.Limits.ObjectCount || bundle.Media.Count > bundle.Limits.FragmentCount || bundle.LogParts.Count > 12288 || bundle.Issues.Count > 256)
            throw new RetrievalError("invalid_bundle");
        if (bundle.Window is not null) {
            bundle.Window.Validate();
            if (bundle.Objects.Where(o => o.Kind == "log").Any(o => !bundle.Window.LogSequences.Contains(o.Sequence))
                || bundle.Status == "retrieved" && !bundle.Objects.Where(o => o.Kind == "log").Select(o => o.Sequence).Order().SequenceEqual(bundle.Window.LogSequences.Order()))
                throw new RetrievalError("window_log_selection_mismatch");
        }
        if (bundle.Media.Sum(m => (long)m.Fragments.Length) > bundle.Limits.FragmentCount
            || bundle.Media.Any(m => m.Probe is not null && m.Probe.Bytes > 2 * RetrievalLimits.MiB)) throw new RetrievalError("bundle_size_limit");
        var files = bundle.Objects.SelectMany(o => new[] { o.Compressed, o.Raw }).Concat(bundle.Media.SelectMany(m => m.Probe is null ? new[] { m.Media } : new[] { m.Media, m.Probe })).Concat(bundle.LogParts).ToArray();
        if (files.Select(f => f.File).Distinct(StringComparer.Ordinal).Count() != files.Length) throw new RetrievalError("duplicate_local_reference");
        long total = 0;
        foreach (var item in files)
        {
            if (item.Bytes < 0 || item.Bytes > Math.Max(bundle.Limits.MediaFileBytes, 2 * RetrievalLimits.MiB)
                || item.Sha256.Length != 64 || item.Sha256.Any(c => !char.IsAsciiHexDigit(c))) throw new RetrievalError("invalid_file_metadata");
            total = checked(total + item.Bytes);
            if (total > bundle.Limits.DownloadBytes + 2 * bundle.Limits.ExpandedBytes + (long)bundle.Limits.FragmentCount * 2 * RetrievalLimits.MiB)
                throw new RetrievalError("bundle_size_limit");
            var path = RetrievalService.LocalFile(directory, item.File);
            if (new FileInfo(path).Length != item.Bytes || verifyFiles && await RetrievalService.HashAsync(path, ct) != item.Sha256) throw new RetrievalError("local_file_mismatch");
        }
        foreach (var o in bundle.Objects)
            if (o.Sequence is < 0 or >= 4096 || o.Kind is not ("log" or "time-index" or "manifest")
                || o.Compressed.File != $"{o.Kind}-{o.Sequence:D6}.gz" || o.Raw.File != $"{o.Kind}-{o.Sequence:D6}.raw") throw new RetrievalError("invalid_object_reference");
        return bundle;
    }
    public static async Task<object> InspectAsync(string directory, CancellationToken ct)
    {
        var bundle = await OpenAsync(directory, ct); var probes = new List<object>();
        foreach (var m in bundle.Media.Where(m => m.Probe is not null))
        {
            using var doc = TimingEvidence.Parse(await File.ReadAllBytesAsync(RetrievalService.LocalFile(directory, m.Probe!.File), ct));
            if (TimingEvidence.Text(doc.RootElement, "fileSha256") != m.Media.Sha256 || ProbeEvidence.Decimal(doc.RootElement, "fileBytes") != (ulong)m.Media.Bytes)
                throw new RetrievalError("probe_file_mismatch");
            probes.Add(new { file = m.Media.File, evidence = ProbeEvidence.Summary(doc.RootElement) });
        }
        var timing = await RetrievalService.ReadTimingAsync(directory, bundle, ct);
        return new { bundle.SessionId, bundle.Status, bundle.Issues, bundle.LogParts, bundle.LogGaps,
            objectCount = bundle.Objects.Count, mediaFiles = bundle.Media.Count, timingRanges = timing.Ranges.Count,
            coverage = "unverified", playbackOffsetVerified = false, probes };
    }
    public static async Task<MappingResult> MapAsync(string directory, string? timestamp, string? elapsedText, CancellationToken ct)
    {
        if ((timestamp is null) == (elapsedText is null)) throw new RetrievalError("one_time_selector_required");
        var bundle = await OpenAsync(directory, ct); var timing = await RetrievalService.ReadTimingAsync(directory, bundle, ct);
        long elapsed;
        if (elapsedText is not null)
        {
            if (!long.TryParse(elapsedText, NumberStyles.None, CultureInfo.InvariantCulture, out elapsed) || elapsed < 0) throw new RetrievalError("invalid_elapsed_time");
        }
        else
        {
            var utc = TimingEvidence.ExplicitUtc(timestamp!);
            if (timing.StartUtc is null) throw new RetrievalError("session_clock_unavailable");
            elapsed = checked((utc - timing.StartUtc.Value).Ticks * 100);
            if (elapsed < 0) throw new RetrievalError("time_before_session");
        }
        var result = new MappingResult { SessionElapsedNs = elapsed.ToString(CultureInfo.InvariantCulture),
            UtcAnchorUncertaintyNs = timestamp is null ? null : timing.ClockUncertaintyNs?.ToString(CultureInfo.InvariantCulture),
            EvidenceUncertainties = timing.Uncertainties.Distinct().ToArray(), ConstructionSourceSha256 = timing.ConstructedSources.OrderBy(s => s).ToArray(),
            EvidenceProvenance = timing.ConstructedSources.Count > 0 ? "constructed_fixture" : "recorded_bundle_not_independently_attested" };
        if (bundle.Issues.Count > 0) { result.Reason = "bundle_incomplete"; return result; }
        var uncertainty = timestamp is null ? 0UL : timing.ClockUncertaintyNs;
        if (uncertainty is null || uncertainty > long.MaxValue || elapsed > long.MaxValue - (long)uncertainty.Value)
        { result.Reason = "utc_anchor_uncertainty_unavailable"; return result; }
        long low = Math.Max(0, elapsed - (long)uncertainty.Value), high = elapsed + (long)uncertainty.Value;
        var logs = timing.Logs.Where(r => low <= r.EndNs && high >= r.StartNs).Distinct().ToArray(); result.LogRanges = logs;
        if (logs.Length > 1) { result.Reason = "ambiguous_log_interval"; return result; }
        // Sparse point association only: no nearest point, interpolation,
        // first/last bridging or fixed SDK/demux residual calibration.
        var available = timing.SchedulingPoints();
        var points = available.Where(p => low <= p.AfterNs && high >= p.BeforeNs).ToArray();
        if (points.Length != 1) { if (points.Length > 1) result.Reason = "ambiguous_scheduling_point"; return result; }
        var point = points[0];
        // A supplied-call log interval may expose additional ambiguity, but it
        // cannot manufacture support for an explicit time outside every sparse
        // scheduling interval. Preserve the original requested interval above.
        if (logs.Length == 1 && available.Any(p => p != point && logs[0].StartNs <= p.AfterNs && logs[0].EndNs >= p.BeforeNs))
        { result.Reason = "ambiguous_scheduling_point"; return result; }
        var files = bundle.Media.Where(m => m.RunId == point.Run && m.Epoch == point.ProducerEpoch).ToArray();
        var matches = new List<(SavedMedia Media, ProbeEvidence.Selection Selection)>();
        foreach (var media in files)
        {
            // Native anchors have no upload identity. Existing persisted-fragment
            // attribution scopes candidate files; a unique result across ALL
            // such files is required. An incomplete competitor cannot be ignored.
            if (media.Fragments.Length == 0 || media.Fragments.Distinct().Count() != media.Fragments.Length
                || media.Fragments.Any(id => !timing.Claims.Any(c => c.RunId == point.Run && c.Epoch == point.ProducerEpoch && c.Upload == media.Upload && c.Id == id && c.IsPersisted))
                || media.Probe is null || !media.ProbeComplete)
            { result.Reason = "file_selection_incomplete"; return result; }
            using var doc = TimingEvidence.Parse(await File.ReadAllBytesAsync(RetrievalService.LocalFile(directory, media.Probe.File), ct));
            var root = doc.RootElement; _ = ProbeEvidence.Summary(root);
            if (TimingEvidence.Text(root, "fileSha256") != media.Media.Sha256 || ProbeEvidence.Decimal(root, "fileBytes") != (ulong)media.Media.Bytes)
                throw new RetrievalError("probe_file_mismatch");
            if (TimingEvidence.Text(root, "status") != "decoded" || !root.GetProperty("decodeEos").GetBoolean()
                || root.TryGetProperty("reason", out var reason) && reason.GetString() != "")
            { result.Reason = "file_selection_incomplete"; return result; }
            var selected = ProbeEvidence.Selections(root).SingleOrDefault(s => s.TargetNs == point.TargetNs);
            if (selected is null || selected.Status is "incomplete" or "ambiguous") { result.Reason = "file_selection_incomplete_or_ambiguous"; return result; }
            if (selected.Status == "unique_observed") matches.Add((media, selected));
        }
        if (matches.Count != 1) { result.Reason = matches.Count == 0 ? "decoded_point_unavailable" : "ambiguous_media_file"; return result; }
        var match = matches[0]; var s = match.Selection;
        if (point.AfterNs > long.MaxValue - (long)uncertainty.Value) { result.Reason = "aligned_interval_overflow"; return result; }
        result.Status = "mapped"; result.Reason = "selected_point_only"; result.PlaybackOffsetSeconds = s.StreamNs!.Value / 1000000000m;
        result.SelectedPoint = new { file = match.Media.Media.File, fileSha256 = match.Media.Media.Sha256, runId = point.Run,
            producerEpoch = point.ProducerEpoch.ToString(CultureInfo.InvariantCulture), clockEpoch = point.ClockEpoch.ToString(CultureInfo.InvariantCulture),
            sequence = point.Sequence.ToString(CultureInfo.InvariantCulture), frameOrdinal = point.FrameOrdinal.ToString(CultureInfo.InvariantCulture),
            schedulingBeforeNs = point.BeforeNs.ToString(CultureInfo.InvariantCulture), schedulingAfterNs = point.AfterNs.ToString(CultureInfo.InvariantCulture),
            alignedSchedulingBeforeNs = Math.Max(0, point.BeforeNs - (long)uncertainty.Value).ToString(CultureInfo.InvariantCulture),
            alignedSchedulingAfterNs = (point.AfterNs + (long)uncertainty.Value).ToString(CultureInfo.InvariantCulture),
            managedEnclosureNs = point.ManagedEnclosureNs.ToString(CultureInfo.InvariantCulture), nativeSchedulingUncertaintyNs = point.NativeUncertaintyNs.ToString(CultureInfo.InvariantCulture),
            utcInputUncertaintyNs = result.UtcAnchorUncertaintyNs, decoderToleranceNs = s.ToleranceNs.ToString(CultureInfo.InvariantCulture),
            targetPtsNs = s.TargetNs.ToString(CultureInfo.InvariantCulture), decoded = s.Evidence,
            meaning = "generated scheduling interval associated with one observed decoded frame; not physical capture", associationValidated = true };
        return result;
    }
}
