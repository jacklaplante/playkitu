using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using NUnit.Framework;
using Playkitu.Issuer;

namespace Issuer.Tests;

public sealed class FakeSessionStreams(Deployment deployment) : ISessionStreams
{
    public ConcurrentDictionary<string, string> Owned = new();
    public ConcurrentDictionary<string, byte> Deleted = new();
    public Func<string, CancellationToken, Task>? BeforeEnsure;
    public bool Preparing;
    public bool FailDelete;
    public Task<string?> EnsureAsync(string id, CancellationToken ct) => EnsureCoreAsync(id, ct);
    private async Task<string?> EnsureCoreAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (BeforeEnsure is not null) await BeforeEnsure(id, ct);
        var arn = Owned.GetOrAdd(id, key => $"arn:aws:kinesisvideo:{deployment.AwsRegion}:{deployment.AwsAccountId}:stream/{deployment.SessionStreamName(key)}/1234");
        return Preparing ? null : arn;
    }
    public Task DeleteAsync(string id, string? arn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (FailDelete) throw new IssuerError(503, "stream_cleanup_unavailable");
        if (arn is not null && Owned.TryGetValue(id, out var owned) && arn != owned) throw new IssuerError(503, "stream_binding_mismatch");
        Owned.TryRemove(id, out _); Deleted[id] = 0;
        return Task.CompletedTask;
    }
}

[TestFixture]
public sealed class OnDemandTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static (Fixture F, FakeSessionStreams Streams) Create(RecordingLimits? limits = null, bool uniqueTesters = false)
    {
        var options = Playkitu.Tests.DeploymentFixture.Create(streamMode: "onDemand", limits: limits);
        options.Validate(); var streams = new FakeSessionStreams(options); var f = new Fixture(options, streams);
        f.Db.ChangeControl(g => { g.SchemaVersion = 2; g.Slots = []; });
        if (uniqueTesters) f.Valve.Intercept = (request, ct) => {
            var uri = request.RequestUri!;
            var ticket = uri.Query.TrimStart('?').Split('&').SingleOrDefault(x => x.StartsWith("ticket="))?.Split('=')[1];
            var body = ticket is not null
                ? JsonSerializer.Serialize(new { response = new { @params = new { result = "OK", steamid = (76561198000000000L + Convert.ToInt64(ticket, 16)).ToString() } } })
                : "{\"appownership\":{\"ownsapp\":true}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        };
        return (f, streams);
    }
    private static async Task<StartResult> StartEventually(Fixture f, StartRequest request)
    {
        for (var n = 0; n < 100; n++) {
            try { var result = await f.Service.StartAsync(request, Ct); if (result.State == "active") return result; }
            catch (IssuerError e) when (e.Code is "issuer_busy" or "admission_pending") { }
            await Task.Delay(1);
        }
        throw new InvalidOperationException("Admission never converged.");
    }
    private static async Task<string> Error(Func<Task> action)
    {
        try { await action(); Assert.Fail("Expected issuer error"); }
        catch (IssuerError e) { return e.Code; }
        throw new InvalidOperationException();
    }
    private static Task Close(Fixture f, StartRequest s) => f.Service.CloseAsync(s.SessionId, s.SessionProof, new(1, Guid.NewGuid(), "normal"), Ct);
    private static async Task<SessionState> Session(Fixture f, StartRequest s) => (await f.Store.GetAsync<SessionState>(DbKey.Session(s.SessionId), Ct))!.Value;

    [Test, Repeat(3)] public async Task TwentyFourConcurrentRecordingsHaveDistinctStreamsAndNoGlobalActiveMap()
    {
        var (f, streams) = Create(uniqueTesters: true);
        var requests = Enumerable.Range(0, 24).Select(f.Start).ToArray();
        var results = await Task.WhenAll(requests.Select(r => StartEventually(f, r)));
        Assert.That(results.All(r => r.State == "active" && r.VideoAvailability == "leased"), Is.True);
        Assert.That(streams.Owned, Has.Count.EqualTo(24));
        Assert.That(streams.Owned.Values.Distinct().Count(), Is.EqualTo(24));
        Assert.That(f.Db.Control.Active, Is.Empty); Assert.That(f.Db.Control.Pending, Is.Empty);
        Assert.That(f.Db.Control.Slots, Is.Empty);
        Assert.That(f.Db.Transactions.SelectMany(t => t.TransactItems).Any(a => a.Put?.Item["pk"].S == "CONTROL"), Is.False);
    }
    [Test] public async Task SixtyShortRecordingsHaveNoDailyMonthlyOrReuseCeilingByDefault()
    {
        var (f, streams) = Create();
        for (var n = 0; n < 60; n++) {
            var request = f.Start(n); await StartEventually(f, request);
            var result = await f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(true), Ct);
            Assert.That(result.Media, Is.Not.Null);
            await Close(f, request);
        }
        Assert.That(streams.Owned, Has.Count.EqualTo(60)); // Old footage is retained, never recycled.
        Assert.That(f.Db.Control.MonthlyAdmissions, Is.Zero);
    }
    [Test, Repeat(10)] public async Task DuplicateStartsConvergeOnOneAllocationAndOneCount()
    {
        var (f, streams) = Create(new() { AdmissionsPerDay = 1 }); var request = f.Start();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => StartEventually(f, request)));
        Assert.That(streams.Owned, Has.Count.EqualTo(1));
        var count = await f.Store.GetAsync<UsageCounter>(new("USAGE#DAY#2026-10-14"), Ct);
        Assert.That(count!.Value.Count, Is.EqualTo(1));
    }
    [Test] public async Task SameTesterCannotOwnTwoActiveAdmissions()
    {
        var (f, _) = Create(); var first = f.Start(); await StartEventually(f, first);
        Assert.That(await Error(() => f.Service.StartAsync(f.Start(1), Ct)), Is.EqualTo("tester_session_active"));
        await Close(f, first);
        Assert.That((await StartEventually(f, f.Start(2))).State, Is.EqualTo("active"));
    }
    [Test] public async Task ConfiguredConcurrentLimitReleasesImmediatelyOnClose()
    {
        var (f, _) = Create(new() { ConcurrentSessions = 1 }, uniqueTesters: true);
        var first = f.Start(); await StartEventually(f, first);
        Assert.That(await Error(() => f.Service.StartAsync(f.Start(1), Ct)), Is.EqualTo("concurrent_session_limit"));
        await Close(f, first);
        await StartEventually(f, f.Start(2));
        Assert.That((await f.Store.GetAsync<UsageCounter>(new("USAGE#ACTIVE"), Ct))!.Value.Count, Is.EqualTo(1));
    }
    [TestCase("day")][TestCase("month")][TestCase("tester")]
    public async Task CountCapsAreExplicitAndDoNotResetOnClose(string kind)
    {
        var limits = new RecordingLimits { AdmissionsPerDay = kind == "day" ? 1 : null,
            AdmissionsPerMonth = kind == "month" ? 1 : null, TesterAdmissionsPerDay = kind == "tester" ? 1 : null };
        var (f, _) = Create(limits); var first = f.Start(); await StartEventually(f, first); await Close(f, first);
        Assert.That(await Error(() => f.Service.StartAsync(f.Start(1), Ct)), Is.EqualTo(kind switch {
            "day" => "daily_admission_limit", "month" => "monthly_admission_limit", _ => "tester_daily_admission_limit" }));
    }
    [Test] public async Task DailyCounterUsesUtcDateAndRollsOverWithoutResettingState()
    {
        var (f, _) = Create(new() { AdmissionsPerDay = 1 }); var first = f.Start(); await StartEventually(f, first); await Close(f, first);
        f.Clock.Advance(TimeSpan.FromDays(1));
        await StartEventually(f, f.Start(1));
        Assert.That((await f.Store.GetAsync<UsageCounter>(new("USAGE#DAY#2026-10-14"), Ct))!.Value.Count, Is.EqualTo(1));
        Assert.That((await f.Store.GetAsync<UsageCounter>(new("USAGE#DAY#2026-10-15"), Ct))!.Value.Count, Is.EqualTo(1));
    }
    [Test] public async Task PreparingStreamPollsDoNotReauthenticateOrAllocateAnotherStream()
    {
        var (f, streams) = Create(); streams.Preparing = true; var request = f.Start();
        Assert.That((await f.Service.StartAsync(request, Ct)).State, Is.EqualTo("pending"));
        var calls = f.Valve.Requests.Count;
        var preparing = await f.Service.StartAsync(request, Ct);
        Assert.That(preparing.State, Is.EqualTo("pending"));
        Assert.That(preparing.EndsAtUtc, Is.Null, "Protocol-v1 clients reject a non-null pending deadline.");
        Assert.That(preparing.MetadataUntilUtc, Is.Null);
        streams.Preparing = false;
        Assert.That((await f.Service.StartAsync(request, Ct)).State, Is.EqualTo("active"));
        Assert.That(f.Valve.Requests, Has.Count.EqualTo(calls)); Assert.That(streams.Owned, Has.Count.EqualTo(1));
    }
    [Test] public async Task LostAdmissionCommitResponseDoesNotDuplicateQuotaOrStream()
    {
        var (f, streams) = Create(new() { AdmissionsPerDay = 1 }); var request = f.Start();
        f.Db.BeforeCommit = transaction => {
            if (transaction.TransactItems.Any(a => a.Put?.Item["data"].S.Contains("\"status\":\"preparing\"") == true)) {
                f.Db.BeforeCommit = null; f.Db.ThrowAfterNextCommit = true;
            }
        };
        Assert.That((await StartEventually(f, request)).State, Is.EqualTo("active"));
        Assert.That(streams.Owned, Has.Count.EqualTo(1));
        Assert.That((await f.Store.GetAsync<UsageCounter>(new("USAGE#DAY#2026-10-14"), Ct))!.Value.Count, Is.EqualTo(1));
    }
    [Test] public async Task DisabledPreparingPollDoesNotCallProvisioning()
    {
        var (f, streams) = Create(); streams.Preparing = true; var request = f.Start();
        await f.Service.StartAsync(request, Ct);
        f.Db.ChangeControl(enabled: false);
        streams.BeforeEnsure = (_, _) => throw new InvalidOperationException("Provisioning must not run while disabled.");
        Assert.That(await Error(() => f.Service.StartAsync(request, Ct)), Is.EqualTo("recording_disabled"));
        Assert.That(f.Sts.Requests, Is.Empty);
    }
    [Test] public async Task PreparingGatewayKeepsProtocolOnePendingDeadlinesNull()
    {
        var (f, streams) = Create(); streams.Preparing = true;
        var request = f.Start() with { MetadataFinalizationSeconds = 60 };
        var config = await f.Handler.HandleAsync(new() { Version = "2.0", RawPath = "/v1/config",
            RequestContext = new() { Http = new() { Method = "GET" } } }, null);
        var gateway = new APIGatewayHttpApiV2ProxyRequest { Version = "2.0", RawPath = "/v1/sessions",
            RequestContext = new() { Http = new() { Method = "POST" } }, Body = JsonSerializer.Serialize(request, Wire.Json) };
        var pending = await f.Handler.HandleAsync(gateway, null);
        using var pendingJson = JsonDocument.Parse(pending.Body);
        Assert.That(pending.StatusCode, Is.EqualTo(202));
        Assert.That(pendingJson.RootElement.GetProperty("state").GetString(), Is.EqualTo("pending"));
        Assert.That(pendingJson.RootElement.GetProperty("videoAvailability").GetString(), Is.EqualTo("not_started"));
        Assert.That(pendingJson.RootElement.GetProperty("endsAtUtc").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(pendingJson.RootElement.TryGetProperty("metadataUntilUtc", out _), Is.False);
        Assert.That((await Session(f, request)).EndsAt, Is.Not.Null, "Stored deadlines are already frozen before AWS provisioning.");
        streams.Preparing = false;
        var active = await f.Handler.HandleAsync(gateway, null);
        Assert.That(active.StatusCode, Is.EqualTo(201));
        using var activeJson = JsonDocument.Parse(active.Body);
        Assert.That(activeJson.RootElement.GetProperty("endsAtUtc").ValueKind, Is.EqualTo(JsonValueKind.String));
        // Explicit opt-in fixture export lets a host exercise its actual pinned
        // Unity client against these real gateway envelopes, without AWS or Steam.
        // Every value here is generated by the isolated fixture, never a live grant.
        if (Environment.GetEnvironmentVariable("PLAYKITU_PROTOCOL_FIXTURES") is { } directory) {
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new InvalidOperationException("Existing absolute fixture output directory required.");
            using var configJson = JsonDocument.Parse(config.Body);
            await File.WriteAllTextAsync(Path.Combine(directory, "on-demand-v1.json"), JsonSerializer.Serialize(new {
                config = configJson.RootElement, pending = pendingJson.RootElement, active = activeJson.RootElement
            }, Wire.Json));
        }
    }
    [Test] public async Task CreationFailureLeavesDurableCleanupWorkAndNoMediaGrant()
    {
        var (f, streams) = Create(); var request = f.Start();
        streams.BeforeEnsure = (_, _) => throw new IssuerError(503, "stream_provisioning_unavailable");
        Assert.That(await Error(() => f.Service.StartAsync(request, Ct)), Is.EqualTo("stream_provisioning_unavailable"));
        var s = await Session(f, request);
        Assert.That(s.Status, Is.EqualTo("preparing"));
        Assert.That(await f.Store.GetAsync<StreamWork>(StreamWork.Key(s.AdmissionId), Ct), Is.Not.Null);
        Assert.That(f.Sts.Requests, Is.Empty);
    }
    [Test] public async Task CloseDuringCreationPreventsReadyPublicationAndPreservesCleanup()
    {
        var (f, streams) = Create(); var request = f.Start();
        streams.BeforeEnsure = async (_, _) => await Close(f, request);
        Assert.That(await Error(() => f.Service.StartAsync(request, Ct)), Is.EqualTo("admission_exhausted"));
        var s = await Session(f, request); var work = (await f.Store.GetAsync<StreamWork>(StreamWork.Key(s.AdmissionId), Ct))!.Value;
        Assert.That(work.Closed, Is.True); Assert.That(streams.Owned, Has.Count.EqualTo(1)); Assert.That(f.Sts.Requests, Is.Empty);
    }
    [Test] public async Task HeartbeatsWriteOnlyOwnSessionLeaseAndWorkAndCannotSurviveKill()
    {
        var (f, _) = Create(); var request = f.Start(); await StartEventually(f, request); f.Db.Transactions.Clear();
        await f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(true, Fixture.Object()), Ct);
        Assert.That(f.Db.Transactions.SelectMany(t => t.TransactItems).Any(a => a.Put?.Item["pk"].S == "CONTROL"), Is.False);
        Assert.That(f.Db.Transactions.All(t => t.TransactItems.Any(a => a.ConditionCheck?.Key["pk"].S == "CONTROL")), Is.True);
        f.Clock.Advance(TimeSpan.FromSeconds(61)); f.Sts.DuringIssue = () => f.Db.ChangeControl(enabled: false);
        Assert.That(await Error(() => f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(true), Ct)), Is.EqualTo("recording_disabled"));
    }
    [Test] public async Task InactivityCleanupReturnsConfiguredCapacityWithoutDeletingFootage()
    {
        var (f, streams) = Create(new() { ConcurrentSessions = 1 }); var request = f.Start(); await StartEventually(f, request);
        f.Clock.Advance(TimeSpan.FromSeconds(181)); await f.Service.CleanupAsync(Ct);
        Assert.That((await Session(f, request)).Status, Is.EqualTo("closed"));
        Assert.That(streams.Deleted, Is.Empty);
        Assert.That((await f.Store.GetAsync<UsageCounter>(new("USAGE#ACTIVE"), Ct))!.Value.Count, Is.Zero);
        await StartEventually(f, f.Start(1));
    }
    [Test] public async Task CleanupOfExpiredOwnerCannotClearNewTesterLease()
    {
        var (f, _) = Create(); var old = f.Start(); await StartEventually(f, old);
        f.Clock.Advance(TimeSpan.FromSeconds(181)); var fresh = f.Start(1); await StartEventually(f, fresh);
        await f.Service.CleanupAsync(Ct);
        Assert.That((await f.Service.AuthorizeAsync(fresh.SessionId, fresh.SessionProof, f.Authorize(true), Ct)).Media, Is.Not.Null);
    }
    [Test] public async Task RetentionCleanupSurvivesMissingSessionAndDoesNotDeleteEarly()
    {
        var (f, streams) = Create(); var request = f.Start(); await StartEventually(f, request); await Close(f, request);
        var s = await Session(f, request); f.Db.DeleteRow(DbKey.Session(request.SessionId));
        await f.Service.CleanupAsync(Ct); Assert.That(streams.Deleted, Is.Empty);
        var work = (await f.Store.GetAsync<StreamWork>(StreamWork.Key(s.AdmissionId), Ct))!.Value;
        f.Clock.Utc = work.RetireAfter; await f.Service.CleanupAsync(Ct); await f.Service.CleanupAsync(Ct);
        Assert.That(streams.Deleted, Has.Count.EqualTo(1));
        Assert.That((await f.Store.GetAsync<StreamWork>(StreamWork.Key(s.AdmissionId), Ct))!.Value.Deleted, Is.True);
    }
    [Test] public async Task OldControlSchemaFailsClosedBeforeSteamOrStreamCreation()
    {
        var options = Playkitu.Tests.DeploymentFixture.Create(streamMode: "onDemand"); var streams = new FakeSessionStreams(options);
        var f = new Fixture(options, streams);
        Assert.That(await Error(() => f.Service.StartAsync(f.Start(), Ct)), Is.EqualTo("slot_configuration_mismatch"));
        Assert.That(f.Valve.Requests, Is.Empty); Assert.That(streams.Owned, Is.Empty);
    }
    [Test] public async Task FailedScheduledDeleteIsObservableAndDeferredWithoutLosingItsLedger()
    {
        var (f, streams) = Create(); var request = f.Start(); await StartEventually(f, request); await Close(f, request);
        var s = await Session(f, request); var key = StreamWork.Key(s.AdmissionId);
        f.Clock.Utc = (await f.Store.GetAsync<StreamWork>(key, Ct))!.Value.RetireAfter; streams.FailDelete = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Handler.HandleAsync(new() { Version = "playkitu.cleanup.v1" }, null));
        Assert.That(error!.Message, Is.EqualTo("Scheduled recording cleanup did not complete."));
        var work = (await f.Store.GetAsync<StreamWork>(key, Ct))!.Value;
        Assert.That(work.Deleted, Is.False); Assert.That(work.Due, Is.EqualTo(f.Clock.Utc.AddMinutes(5)));
        streams.FailDelete = false; f.Clock.Advance(TimeSpan.FromMinutes(5)); await f.Service.CleanupAsync(Ct);
        Assert.That((await f.Store.GetAsync<StreamWork>(key, Ct))!.Value.Deleted, Is.True);
    }
    [TestCase(0)][TestCase(-1)][TestCase(100000001)]
    public void InvalidCountSettingsAreRejected(int count) => Assert.Throws<InvalidOperationException>(() => new RecordingLimits { ConcurrentSessions = count }.Validate());
}
