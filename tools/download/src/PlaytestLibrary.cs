using System.Text.Json;
using System.Text.RegularExpressions;
using Playkitu.Issuer;

namespace Playkitu.Retrieval;

public sealed record PlaytestSummary(Guid SessionId, DateTimeOffset StartedUtc, string Status,
    string GameVersion, bool HasVideoSlot, bool MicrophoneConsented);
public sealed record LibraryListing(IReadOnlyList<PlaytestSummary> Sessions, bool MayBeTruncated);
public sealed record PlaybackPart(string Original, string? Preview);
public sealed class PlaytestDownload
{
    public Guid SessionId { get; set; }
    public string Status { get; set; } = "downloading";
    public List<PlaybackPart> Parts { get; set; } = [];
    public List<string> Issues { get; set; } = [];
}
public interface IPlaybackConverter { Task<bool> ConvertAsync(string input, string output, CancellationToken ct); }

// The library orchestrates the existing receipt/hash/fragment-attribution path;
// it never guesses a stream's time interval or downloads a neighbouring tester.
public sealed class PlaytestLibrary(IReadStore store, IMediaProbe probe, IPlaybackConverter converter)
{
    public static string PlaybackFile(string directory, string relative)
    {
        var match = Regex.Match(relative.Replace('\\', '/'), @"\A(section-[0-9]{2})/(media-[0-9]{4}\.(?:mkv|mp4))\z");
        if (!match.Success || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new RetrievalError("invalid_playback_reference");
        var section = Path.Combine(directory, match.Groups[1].Value);
        if ((File.GetAttributes(section) & FileAttributes.ReparsePoint) != 0) throw new RetrievalError("invalid_playback_reference");
        return RetrievalService.LocalFile(section, match.Groups[2].Value);
    }
    public async Task<LibraryListing> ListAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > 30) throw new RetrievalError("date_range_maximum_31_days");
        var sessions = new Dictionary<Guid, PlaytestSummary>(); var truncated = false;
        for (var day = to; day >= from; day = day.AddDays(-1)) {
            var ids = await store.ListSessionsAsync(day, 50, ct); truncated |= ids.Count == 50;
            foreach (var id in ids) {
                var s = await store.SessionAsync(id, ct); if (s is null || s.AdmittedAt is null) continue;
                RetrievalService.ValidateSession(id, s);
                // Ignore stale/mismatched day-index entries; Dynamo TTL/GSI updates lag.
                if (DateOnly.FromDateTime(s.AdmittedAt.Value.UtcDateTime) != day) continue;
                sessions[id] = new(id, s.AdmittedAt.Value, s.Status, s.Build.GameVersion, s.Slot >= 0, s.Consent.Microphone);
            }
        }
        return new(sessions.Values.OrderByDescending(s => s.StartedUtc).ToArray(), truncated);
    }

    public static ProducerWindow[] Windows(IEnumerable<ClaimDigest> claims)
    {
        // Persisted ACKs, not the wall clock, select media. A millisecond beyond
        // the last fragment START makes the final interval half-open and inclusive
        // of that fragment. Gaps do not cause a query spanning unowned footage.
        var times = claims.Where(c => c.TimecodeMs.HasValue).Select(c => c.TimecodeMs!.Value).Distinct().Order().ToArray();
        var windows = new List<ProducerWindow>();
        for (var i = 0; i < times.Length;) {
            var first = times[i++]; var last = first;
            while (i < times.Length && times[i] - first < 600000) last = times[i++];
            var window = new ProducerWindow { From100ns = checked(first * 10000), To100ns = checked((last + 1) * 10000),
                CatalogPageSha256 = new string('0', 64) };
            window.Validate(); windows.Add(window);
            if (windows.Count > 64) throw new RetrievalError("playtest_window_limit");
        }
        return windows.ToArray();
    }

    public async Task<PlaytestDownload> DownloadAsync(Guid session, string output, Action<string> progress, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(output) || Directory.Exists(output) || File.Exists(output)) throw new RetrievalError("new_absolute_output_required");
        Directory.CreateDirectory(output); var result = new PlaytestDownload { SessionId = session };
        try {
            var catalog = new MetadataCatalog(store, probe); string? previous = null;
            var claims = new List<ClaimDigest>(); var finished = false;
            for (var n = 0; n < 32; n++) {
                progress($"Reading session metadata · page {n + 1}");
                var directory = Path.Combine(output, $"metadata-{n:D2}");
                var page = await catalog.PageAsync(session, directory, previous, ct); previous = directory;
                ct.ThrowIfCancellationRequested();
                claims.AddRange(page.Summaries.SelectMany(s => s.Claims));
                if (page.Status != "retrieved") result.Issues.Add("metadata_incomplete");
                if (page.EnumerationComplete) { finished = true; break; }
            }
            if (!finished) throw new RetrievalError("metadata_page_chain_limit");
            var windows = Windows(claims); if (windows.Length == 0) result.Issues.Add("no_persisted_video_fragments");
            long bytes = 0;
            for (var n = 0; n < windows.Length; n++) {
                ct.ThrowIfCancellationRequested();
                if (new DriveInfo(Path.GetPathRoot(output)!).AvailableFreeSpace < 2L * 1024 * 1024 * 1024)
                    throw new RetrievalError("download_disk_space_low");
                progress($"Downloading video and audio · section {n + 1} of {windows.Length}");
                var directory = Path.Combine(output, $"section-{n:D2}");
                var bundle = await catalog.FetchWindowAsync(session, previous!, directory, windows[n], ct);
                ct.ThrowIfCancellationRequested();
                bytes = checked(bytes + bundle.DownloadedBytes);
                if (bytes > 16L * 1024 * 1024 * 1024) throw new RetrievalError("playtest_download_limit");
                result.Issues.AddRange(bundle.Issues);
                foreach (var media in bundle.Media) {
                    var original = RetrievalService.LocalFile(directory, media.Media.File);
                    var preview = Path.ChangeExtension(original, ".mp4");
                    progress($"Preparing playback · part {result.Parts.Count + 1}");
                    var converted = await converter.ConvertAsync(original, preview, ct);
                    if (!converted) result.Issues.Add("browser_preview_unavailable_use_original_mkv");
                    result.Parts.Add(new(Path.GetRelativePath(output, original), converted ? Path.GetRelativePath(output, preview) : null));
                }
            }
            result.Status = result.Parts.Count > 0 ? "downloaded" : "no_video";
        } catch (OperationCanceledException) { result.Status = "cancelled"; result.Issues.Add("download_cancelled"); }
        catch (RetrievalError error) { result.Status = "incomplete"; result.Issues.Add(error.Code); }
        catch { result.Status = "incomplete"; result.Issues.Add("read_failed_check_owner_login"); }
        result.Issues = result.Issues.Distinct().ToList();
        await File.WriteAllTextAsync(Path.Combine(output, "playtest.json"), JsonSerializer.Serialize(result, Wire.Json), CancellationToken.None);
        return result;
    }
}
