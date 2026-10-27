using NUnit.Framework;
using Playkitu.Retrieval;
using System.Net;
using System.Text.Json;

namespace Retrieval.Tests;

[TestFixture]
public sealed class PlaytestLibraryTests
{
    private sealed class Converter(bool succeed = true) : IPlaybackConverter
    {
        public int Calls;
        public async Task<bool> ConvertAsync(string input, string output, CancellationToken ct) {
            Calls++; if (succeed) await File.WriteAllBytesAsync(output, [0, 1, 2, 3], ct); return succeed;
        }
    }
    private string root = "";
    [SetUp] public void Setup() { root = Path.Combine(Path.GetTempPath(), "playkitu-library-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TearDown] public void Cleanup() { Directory.Delete(root, true); }

    [Test] public async Task FutureRecordingsShowDeclaredUsernameWithoutALocalLabel()
    {
        var f = new FixtureStore();
        f.State.Build = new("test", PlayerDisplayName: "Fozo");
        var listing = await new PlaytestLibrary(f, new FixtureProbe(), new Converter())
            .ListAsync(new(2026, 10, 15), new(2026, 10, 15), default);
        Assert.That(listing.Sessions.Single().PlayerName, Is.EqualTo("Fozo"));
    }

    [Test] public async Task ListingShowsObservedSessionLengthWithoutUsingTheFourHourDeadline()
    {
        var f = new FixtureStore();
        f.State.OnDemand = true;
        f.State.LastSeen = f.State.AdmittedAt!.Value.AddSeconds(137);
        f.State.EndsAt = f.State.AdmittedAt.Value.AddHours(4);
        var listing = await new PlaytestLibrary(f, new FixtureProbe(), new Converter())
            .ListAsync(new(2026, 10, 15), new(2026, 10, 15), default);
        Assert.That(listing.Sessions.Single().DurationSeconds, Is.EqualTo(137));
        f.State.LastSeen = default; // Legacy rows have no trustworthy session end.
        listing = await new PlaytestLibrary(f, new FixtureProbe(), new Converter())
            .ListAsync(new(2026, 10, 15), new(2026, 10, 15), default);
        Assert.That(listing.Sessions.Single().DurationSeconds, Is.Null);
    }

    [Test] public async Task LogsOnlyDownloadIncludesUnreferencedLogsWithoutReadingMedia()
    {
        var f = new FixtureStore(); f.Ready();
        f.Add("log", 3, System.Text.Encoding.UTF8.GetBytes("A real uploaded log line\n"));
        var converter = new Converter();
        var output = Path.Combine(root, "logs");
        var result = await new PlaytestLibrary(f, new FixtureProbe(), converter)
            .DownloadAsync(FixtureStore.Id, output, _ => { }, default, includeVideo: false);
        Assert.That(result.Status, Is.EqualTo("logs_downloaded"));
        Assert.That(f.MediaReads, Is.Zero);
        Assert.That(converter.Calls, Is.Zero);
        Assert.That(result.Logs.Single().Sequence, Is.EqualTo(3));
        var file = PlaytestLibrary.LogFile(output, result.Logs.Single().File);
        Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("A real uploaded log line\n"));
        Assert.That(() => PlaytestLibrary.LogFile(output, "../private.txt"), Throws.TypeOf<RetrievalError>());
    }

    [Test] public async Task ListingUsesAdmissionDayIgnoresStaleIndexAndDeduplicates()
    {
        var f = new FixtureStore(); var lib = new PlaytestLibrary(f, new FixtureProbe(), new Converter());
        var listing = await lib.ListAsync(new(2026, 10, 14), new(2026, 10, 16), default);
        Assert.That(listing.Sessions, Has.Count.EqualTo(1));
        Assert.That(listing.Sessions[0].SessionId, Is.EqualTo(FixtureStore.Id));
        Assert.That(listing.MayBeTruncated, Is.False);
        Assert.That(async () => await lib.ListAsync(new(2026, 1, 1), new(2026, 2, 2), default), Throws.TypeOf<RetrievalError>());
    }
    [Test] public void WindowsRetainLastFragmentStartAndSplitAtTenMinutesWithoutOverlap()
    {
        var windows = PlaytestLibrary.Windows(new ulong[] { 5000, 5000, 604999, 605000, 99999999 }.Select(t => new ClaimDigest("hash", "owner", t)));
        Assert.That(windows, Has.Length.EqualTo(3));
        Assert.That(windows[0].Contains(604999), Is.True); Assert.That(windows[0].Contains(605000), Is.False);
        Assert.That(windows[1].Contains(605000), Is.True);
        Assert.That(windows.All(w => w.To100ns - w.From100ns <= 600UL * 10000000), Is.True);
    }
    [Test] public async Task PaginationReturnsAllOneHundredFortyTwoSessionsAcrossDatesWithoutTruncation()
    {
        var store = new FixtureStore();
        for (var n = 0; n < 142; n++) store.SessionRows.Add(new() { SessionId = Guid.NewGuid(), AdmissionId = Guid.NewGuid().ToString("N"), Generation = 1,
            AdmittedAt = DateTimeOffset.Parse(n < 135 ? "2026-10-16T00:00:00Z" : "2026-10-15T00:00:00Z").AddMinutes(n), Status = "active", OnDemand = true,
            StreamArn = "owned-stream", Slot = -1 });
        var library = new PlaytestLibrary(store, new FixtureProbe(), new Converter());
        var all = new List<PlaytestSummary>(); string? cursor = null;
        do {
            var page = await library.ListAsync(new(2026, 10, 15), new(2026, 10, 16), default, cursor);
            Assert.That(page.Sessions.Count, Is.LessThanOrEqualTo(50));
            Assert.That(page.MayBeTruncated, Is.False); all.AddRange(page.Sessions); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.That(all.Select(s => s.SessionId).Distinct().Count(), Is.EqualTo(142));
        Assert.That(all.Select(s => s.StartedUtc), Is.Ordered.Descending);
        Assert.That(all.All(s => s.HasVideoSlot), Is.True);
        Assert.That(store.SessionPageLimits.All(n => n is >= 1 and <= 50), Is.True);
    }
    [Test] public async Task PageCursorCannotBeReusedForAnotherDateRange()
    {
        var store = new FixtureStore();
        for (var n = 0; n < 51; n++) store.SessionRows.Add(new() { SessionId = Guid.NewGuid(), AdmissionId = Guid.NewGuid().ToString("N"), Generation = 1,
            AdmittedAt = DateTimeOffset.Parse("2026-10-16T00:00:00Z").AddMinutes(n) });
        var library = new PlaytestLibrary(store, new FixtureProbe(), new Converter());
        var page = await library.ListAsync(new(2026, 10, 15), new(2026, 10, 16), default);
        await Assert.ThrowsAsync<RetrievalError>(() => library.ListAsync(new(2026, 10, 14), new(2026, 10, 16), default, page.NextCursor));
    }
    [Test] public async Task DedicatedSessionDownloadsMediaWithoutAnyPoolSlot()
    {
        var store = new FixtureStore(); store.State.OnDemand = true; store.State.Slot = -1;
        store.State.StreamArn = "arn:aws:kinesisvideo:us-east-2:123456789012:stream/playkitu-slot-session-" + store.State.AdmissionId + "/1234";
        store.Ready();
        var result = await new PlaytestLibrary(store, new FixtureProbe(), new Converter()).DownloadAsync(FixtureStore.Id, Path.Combine(root, "dedicated"), _ => { }, default);
        Assert.That(result.Status, Is.EqualTo("downloaded")); Assert.That(result.Parts, Has.Count.EqualTo(1)); Assert.That(store.MediaReads, Is.EqualTo(1));
    }
    [TestCase(true)] [TestCase(false)]
    public async Task DownloadReusesActualReceiptAndClaimAttributionAndRetainsOriginalWhenPreviewFails(bool converted)
    {
        var f = new FixtureStore(); f.Ready(); var converter = new Converter(converted);
        var result = await new PlaytestLibrary(f, new FixtureProbe(), converter).DownloadAsync(FixtureStore.Id, Path.Combine(root, "download"), _ => { }, default);
        Assert.That(result.Status, Is.EqualTo("downloaded")); Assert.That(result.Parts, Has.Count.EqualTo(1));
        Assert.That(f.RequestedFragments.SelectMany(x => x), Is.EqualTo(new[] { "frag1" }));
        Assert.That(File.ReadAllBytes(PlaytestLibrary.PlaybackFile(Path.Combine(root, "download"), result.Parts[0].Original)), Is.EqualTo(f.Media));
        Assert.That(result.Parts[0].Preview is not null, Is.EqualTo(converted));
        Assert.That(File.Exists(Path.Combine(root, "download", "metadata-00", "page.json")), Is.True);
    }
    [Test] public async Task MissingClaimDoesNotGuessMediaFromTheSharedStream()
    {
        var f = new FixtureStore(); var timing = f.Index(); timing["records"] = new System.Text.Json.Nodes.JsonArray(); f.Ready(timing);
        var result = await new PlaytestLibrary(f, new FixtureProbe(), new Converter()).DownloadAsync(FixtureStore.Id, Path.Combine(root, "download"), _ => { }, default);
        Assert.That(f.MediaReads, Is.Zero); Assert.That(result.Parts, Is.Empty);
        Assert.That(result.Issues, Does.Contain("no_persisted_video_fragments"));
    }
    [TestCase("../secret.txt")] [TestCase("section-00/../../secret.txt")] [TestCase("metadata-00/bundle.json")]
    public void MediaRoutesRejectArbitraryLocalPaths(string path) => Assert.That(() => PlaytestLibrary.PlaybackFile(root, path), Throws.TypeOf<RetrievalError>());

    [Test] public async Task CancellationOfBlockedMediaRetainsMetadataAndReportsCancelled()
    {
        var f = new FixtureStore { BlockMedia = true }; f.Ready();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var directory = Path.Combine(root, "cancelled");
        var result = await new PlaytestLibrary(f, new FixtureProbe(), new Converter()).DownloadAsync(FixtureStore.Id, directory, _ => { }, cancel.Token);
        Assert.That(result.Status, Is.EqualTo("cancelled"));
        Assert.That(File.Exists(Path.Combine(directory, "playtest.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(directory, "metadata-00", "page.json")), Is.True);
        Assert.That(f.MediaReads, Is.EqualTo(1));
    }

    [Test] public async Task LocalHttpBrowserListsDownloadsSupportsRangeAndBlocksCrossSiteRequests()
    {
        var f = new FixtureStore(); f.Ready();
        f.Add("log", 3, System.Text.Encoding.UTF8.GetBytes("Uploaded log contents\n"));
        var lib = new PlaytestLibrary(f, new FixtureProbe(), new Converter());
        using var stop = new CancellationTokenSource(); var ready = new TaskCompletionSource<string>();
        var server = LibraryBrowser.RunAsync(lib, root, false, stop.Token, url => ready.SetResult(url));
        try {
            var url = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // The suite deliberately blocks outbound proxies. This client talks
            // only to our ephemeral loopback listener, never the cloud fixture.
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(url) };
            Assert.That(await http.GetStringAsync(""), Does.Contain("Playtest library"));
            using var list = JsonDocument.Parse(await http.GetStringAsync("sessions?from=2026-10-15&to=2026-10-15"));
            Assert.That(list.RootElement.GetProperty("sessions").GetArrayLength(), Is.EqualTo(1));
            var blocked = await http.PostAsync("download/" + FixtureStore.Id, null); Assert.That(blocked.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            using var hostile = new HttpRequestMessage(HttpMethod.Get, "jobs"); hostile.Headers.Add("Origin", "https://example.invalid");
            Assert.That((await http.SendAsync(hostile)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            http.DefaultRequestHeaders.Add("X-Playkitu", "1");
            using var name = new StringContent(JsonSerializer.Serialize("Test player <&>"), System.Text.Encoding.UTF8, "application/json");
            Assert.That((await http.PostAsync("player-name/" + FixtureStore.Id, name)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var namedListing = JsonDocument.Parse(await http.GetStringAsync("sessions?from=2026-10-15&to=2026-10-15"));
            Assert.That(namedListing.RootElement.GetProperty("sessions")[0].GetProperty("playerName").GetString(), Is.EqualTo("Test player <&>"));
            Assert.That(File.ReadAllText(Path.Combine(root, "player-names.json")), Does.Contain("Test player"));
            f.State.Build = f.State.Build with { PlayerDisplayName = "Actual game username" };
            using var recordedListing = JsonDocument.Parse(await http.GetStringAsync("sessions?from=2026-10-15&to=2026-10-15"));
            var recorded = recordedListing.RootElement.GetProperty("sessions")[0];
            Assert.That(recorded.GetProperty("playerName").GetString(), Is.EqualTo("Actual game username"));
            Assert.That(recorded.GetProperty("hasRecordedPlayerName").GetBoolean(), Is.True);
            using var download = JsonDocument.Parse(await (await http.PostAsync("download/" + FixtureStore.Id, null)).Content.ReadAsStringAsync());
            var id = download.RootElement.GetProperty("jobId").GetGuid();
            var done = false;
            for (var n = 0; n < 100 && !done; n++) { using var state = JsonDocument.Parse(await http.GetStringAsync("jobs")); done = state.RootElement[0].GetProperty("finished").GetBoolean(); if (!done) await Task.Delay(25); }
            Assert.That(done, Is.True);
            using var media = new HttpRequestMessage(HttpMethod.Get, $"media/{id}/0/preview"); media.Headers.Range = new(1, 2);
            using var response = await http.SendAsync(media);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(new byte[] { 1, 2 }));
            Assert.That((await http.GetAsync("media/" + id + "/0/unknown")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(await http.GetByteArrayAsync("logs/" + id)));
            using var log = new StreamReader(archive.GetEntry("logs-000003.log")!.Open());
            Assert.That(await log.ReadToEndAsync(), Is.EqualTo("Uploaded log contents\n"));
            Assert.That(archive.GetEntry("download-notes.txt"), Is.Not.Null);
        } finally { stop.Cancel(); try { await server; } catch (OperationCanceledException) { } }
    }
}
