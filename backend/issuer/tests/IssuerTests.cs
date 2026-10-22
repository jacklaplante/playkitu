using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using NUnit.Framework;
using Playreel.Issuer;

namespace Issuer.Tests;

[TestFixture]
public sealed class IssuerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static APIGatewayHttpApiV2ProxyRequest Event(string path, object? body = null, string? proof = null, string method = "POST") => new()
    {
        Version = "2.0", RawPath = path, Body = body is null ? "" : JsonSerializer.Serialize(body, Wire.Json),
        Headers = proof is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["authorization"] = "Bearer " + proof },
        RequestContext = new() { Http = new() { Method = method } }
    };
    private static async Task<IssuerError> ErrorAsync(Func<Task> operation, int status)
    {
        try { await operation(); Assert.Fail("Expected bounded issuer error."); }
        catch (IssuerError e) { Assert.That(e.Status, Is.EqualTo(status)); return e; }
        throw new InvalidOperationException();
    }

    [TestCase("00112233-4455-6677-8899-AABBCCDDEEFF", 1792000000L, false, "prrec2:f3xyqizKHT2FlXV-Zk3cwg")]
    [TestCase("00112233-4455-6677-8899-aabbccddeefe", 1792000000L, false, "prrec2:e4J8ha1le9BPIO12OxzXcw")]
    [TestCase("00112233-4455-6677-8899-aabbccddeeff", 1792000001L, false, "prrec2:19QsDJxgLDKX3TrjXQX5CQ")]
    [TestCase("00112233-4455-6677-8899-aabbccddeeff", 1792000000L, true, "prrec2:osLh5KkzgaNs-v92ypKwcw")]
    [TestCase("00112233-4455-6677-8899-aabbccddeeff", -1L, false, "prrec2:uWd4ib5h-J7M8tfNcf8n6A")]
    public void CompactIdentityBindsCanonicalTuple(string guid, long seconds, bool otherProof, string expected)
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try {
            var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.NegativeSign = "~";
            System.Globalization.CultureInfo.CurrentCulture = culture;
            string proof = otherProof ? new string('_', 42) + "8" : new string('A', 43);
            Assert.That(Wire.ValidProof(proof), Is.True);
            var identity = Wire.Identity(Guid.Parse(guid), seconds, proof);
            Assert.That(identity, Is.EqualTo(expected));
            Assert.That(identity, Does.Match("^prrec2:[A-Za-z0-9_-]{22}$"));
            Assert.That(Encoding.UTF8.GetByteCount(identity), Is.EqualTo(29));
        } finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }

    [Test]
    public async Task ConfigAdvertisesOnlyCurrentIdentityVersion()
    {
        var response = await new Fixture().Handler.HandleAsync(Event("/v1/config", method: "GET"), null);
        Assert.That(response.StatusCode, Is.EqualTo(200));
        using var body = JsonDocument.Parse(response.Body);
        Assert.That(body.RootElement.GetProperty("identityPrefix").GetString(), Is.EqualTo("prrec2:"));
        Assert.That(body.RootElement.GetProperty("protocolVersion").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public void CanonicalIdentityAndProof()
    {
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"); var proof = new string('A', 43);
        Assert.That(Wire.ValidProof(proof), Is.True);
        Assert.That(Wire.Identity(id, 1792000000, proof), Is.EqualTo("prrec2:f3xyqizKHT2FlXV-Zk3cwg"));
        Assert.That(Wire.Identity(id, 1792000000, proof).Length, Is.EqualTo(29));
        Assert.That(Wire.ValidProof(proof[..42] + "B"), Is.False, "Reject alternate noncanonical padding bits.");
    }

    [TestCase(false, false, "baseline", "str:prrec2:f3xyqizKHT2FlXV-Zk3cwg")]
    [TestCase(true, false, "baseline", "str:prrec2:f3xyqizKHT2FlXV-Zk3cwg")]
    [TestCase(false, true, "baseline", "str:prrec2:f3xyqizKHT2FlXV-Zk3cwg")]
    [TestCase(true, true, "baseline", "str:prrec2:f3xyqizKHT2FlXV-Zk3cwg")]
    [TestCase(false, false, "session", "str:prrec2:e4J8ha1le9BPIO12OxzXcw")]
    [TestCase(false, false, "time", "str:prrec2:19QsDJxgLDKX3TrjXQX5CQ")]
    [TestCase(false, false, "proof", "str:prrec2:osLh5KkzgaNs-v92ypKwcw")]
    public async Task FixedClientWireRequestPreservesCompleteValveIdentity(bool base64Body, bool alternateJsonSpelling, string bindingCase, string expectedWireIdentity)
    {
        var f = new Fixture(); f.Clock.Utc = DateTimeOffset.FromUnixTimeSeconds(1792000000);
        var request = Event("/v1/sessions");
        // Independent synthetic client-wire fixture: do not serialize StartRequest or
        // calculate the expectation with Wire.Identity/RecordingIdentity. This exercises
        // actual gateway-body decoding, GUID/long/proof parsing, reconstruction and the
        // complete URI-escaped Valve generic-string query. Hardcoded vectors retain
        // the exact SDK identity after str: and prove each bound input still matters.
        request.Body = """
            {"protocolVersion":1,"sessionId":"00112233-4455-6677-8899-aabbccddeeff","admissionUnixSeconds":1792000000,"sessionProof":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","steamTicketHex":"000A","build":{"gameVersion":"wire-fixture","steamBuildId":null},"consent":{"schemaVersion":1,"gameplayAudioAndLogs":true,"microphone":false,"acceptedAtUtc":"2026-10-14T17:46:40.0000000+00:00"}}
            """;
        if (bindingCase == "session") request.Body = request.Body.Replace("aabbccddeeff", "aabbccddeefe", StringComparison.Ordinal);
        if (bindingCase == "time") request.Body = request.Body.Replace("1792000000", "1792000001", StringComparison.Ordinal);
        if (bindingCase == "proof") request.Body = request.Body.Replace(new string('A', 43), new string('_', 42) + "8", StringComparison.Ordinal);
        // Equivalent JSON spellings must recover the same GUID and proof bytes. The
        // two gateway encodings cover both actual Function.Read body-decoding paths.
        if (alternateJsonSpelling)
            request.Body = request.Body.Replace("aabbccddeeff", "AABBCCDDEEFF", StringComparison.Ordinal)
                .Replace("\"sessionProof\":\"A", "\"sessionProof\":\"\\u0041", StringComparison.Ordinal);
        request.IsBase64Encoded = base64Body;
        if (base64Body) request.Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Body));

        var response = await f.Handler.HandleAsync(request, null);
        Assert.That(response.StatusCode, Is.EqualTo(201), response.Body);
        Assert.That(f.Valve.Requests, Has.Count.EqualTo(2), "Successful fake authentication must reach the existing ownership check.");
        var authentication = new Uri(f.Valve.Requests[0].Uri);
        var query = authentication.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(authentication.Scheme, Is.EqualTo("https"));
            Assert.That(authentication.Host, Is.EqualTo("partner.steam-api.com"));
            Assert.That(authentication.AbsolutePath, Is.EqualTo("/ISteamUserAuth/AuthenticateUserTicket/v1/"));
            Assert.That(query.Keys, Is.EquivalentTo(new[] { "appid", "ticket", "identity" }));
            Assert.That(query["appid"], Is.EqualTo("123456"));
            Assert.That(query["ticket"], Is.EqualTo("000A"));
            Assert.That(query["identity"], Is.EqualTo(expectedWireIdentity));
            Assert.That(f.Secrets.Calls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task HandlerConsentAndStrictSchemaRejectBeforeMutation()
    {
        var f = new Fixture(); var start = f.Start();
        var withoutConsent = await f.Handler.HandleAsync(Event("/v1/sessions", start with { Consent = start.Consent with { GameplayAudioAndLogs = false } }), null);
        Assert.That(withoutConsent.StatusCode, Is.EqualTo(400));
        foreach (var body in new[] { "{\"protocolVersion\":1,\"protocolVersion\":2}", "{\"unexpected\":true}", new string('x', 24001) })
        {
            var ev = Event("/v1/sessions"); ev.Body = body;
            Assert.That((await f.Handler.HandleAsync(ev, null)).StatusCode, Is.EqualTo(400));
        }
        Assert.That(f.Db.RowCount, Is.EqualTo(1)); Assert.That(f.Valve.Requests, Is.Empty); Assert.That(f.Secrets.Calls, Is.Zero);
    }

    [Test]
    public async Task OptionalSteamBuildIdCanBeOmitted()
    {
        var f = new Fixture(); var ev = Event("/v1/sessions", f.Start());
        ev.Body = ev.Body.Replace(",\"steamBuildId\":\"123\"", "", StringComparison.Ordinal);
        Assert.That((await f.Handler.HandleAsync(ev, null)).StatusCode, Is.EqualTo(201));
    }

    [Test]
    public async Task ActualHandlerCreatesSessionAndExactSdkPolicies()
    {
        var f = new Fixture(); var start = f.Start();
        var response = await f.Handler.HandleAsync(Event("/v1/sessions", start), null);
        Assert.That(response.StatusCode, Is.EqualTo(201), response.Body);
        var authorization = await f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(true, Fixture.Object()), Ct);
        Assert.That(authorization.MediaStatus, Is.EqualTo("authorized")); Assert.That(authorization.Uploads, Has.Length.EqualTo(1));
        var grant = authorization.Uploads[0];
        Assert.That(grant.Key, Is.EqualTo($"sessions/2026-10-14/{start.SessionId:N}/log/000000.gz"));
        Assert.That(grant.Url, Does.StartWith("https://"));
        using var policy = JsonDocument.Parse(Convert.FromBase64String(grant.Fields.Single(p => p.Key.Equals("policy", StringComparison.OrdinalIgnoreCase)).Value));
        var lengthCondition = policy.RootElement.GetProperty("conditions").EnumerateArray().Single(x => x.ValueKind == JsonValueKind.Array && x[0].GetString() == "content-length-range");
        Assert.That(lengthCondition[1].GetInt64(), Is.EqualTo(100)); Assert.That(lengthCondition[2].GetInt64(), Is.EqualTo(100));
        Assert.That(DateTimeOffset.Parse(policy.RootElement.GetProperty("expiration").GetString()!), Is.EqualTo(grant.ExpiresAtUtc));
        var conditions = policy.RootElement.GetProperty("conditions").ToString();
        Assert.That(conditions, Does.Contain("content-length-range").And.Contain("100").And.Contain("x-amz-checksum-sha256").And.Contain("AES256").And.Contain(grant.Key));
        var sts = f.Sts.Requests.Single(); Assert.That(sts.DurationSeconds, Is.EqualTo(900));
        Assert.That(sts.Policy, Does.Contain(f.Options.StreamArns[0]).And.Not.Contain("GetMedia").And.Not.Contain("CreateStream"));
        Assert.That(f.Db.Control.Slots[0].MaxCredentialExpiry, Is.EqualTo(authorization.Media!.ExpiresAtUtc));
        Assert.That(f.Db.AllStoredText, Does.Not.Contain(start.SessionProof).And.Not.Contain("OFFLINE-MEDIA-SECRET").And.Not.Contain("OFFLINE-PUBLISHER-KEY"));
        Assert.That(f.Valve.Requests, Has.Count.EqualTo(2));
        Assert.That(f.Valve.Requests[0].Uri, Does.Contain("appid=123456").And.Contain("identity=str%3Aprrec2").And.Not.Contain("OFFLINE-PUBLISHER-KEY"));
        Assert.That(f.Valve.Requests.All(x => x.Key == "OFFLINE-PUBLISHER-KEY"), Is.True);
    }

    [Test]
    public async Task DuplicateAdmissionAndBindingConflictDoNotReallocate()
    {
        var f = new Fixture(); var start = f.Start();
        var result = await f.Service.StartAsync(start, Ct);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        var duplicate = await f.Service.StartAsync(start, Ct);
        Assert.That(duplicate, Is.EqualTo(result)); Assert.That(f.Valve.Requests, Has.Count.EqualTo(2));
        Assert.That(f.Db.Control.MonthlyAdmissions, Is.EqualTo(1));
        await ErrorAsync(() => f.Service.StartAsync(start with { Build = new("changed", null) }, Ct), 409);
        await ErrorAsync(() => f.Service.StartAsync(start with { SessionProof = f.Start().SessionProof }, Ct), 401);
    }

    [Test]
    public async Task TesterDailyCandidateAllowsEighthAndDeniesNinthWithoutRefundOrAccountingLoss()
    {
        var f = new Fixture();
        for (int admission = 0; admission < IssuerOptions.MaxTesterAdmissionsPerUtcDay; admission++)
        {
            var request = f.Start(admission);
            Assert.That((await f.Service.StartAsync(request, Ct)).State, Is.EqualTo("active"));
            await f.Service.CloseAsync(request.SessionId, request.SessionProof, new(1, Guid.NewGuid(), "normal"), Ct);
            f.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.That(IssuerOptions.MaxTesterAdmissionsPerUtcDay, Is.EqualTo(8), "C11 is the explicit eight/day policy candidate.");
        var ninth = f.Start(8);
        var error = await ErrorAsync(() => f.Service.StartAsync(ninth, Ct), 429);
        var denied = (await f.Store.GetAsync<SessionState>(DbKey.Session(ninth.SessionId), Ct))!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(error.Code, Is.EqualTo("session_quota")); Assert.That(error.RetryAfter, Is.EqualTo(60));
            Assert.That(denied.Status, Is.EqualTo("failed")); Assert.That(denied.AdmittedAt, Is.Null);
            Assert.That(f.Db.Control.Pending, Is.Empty); Assert.That(f.Db.Control.Active, Is.Empty);
            Assert.That(f.Db.Control.DailyAdmissions, Is.EqualTo(8)); Assert.That(f.Db.Control.MonthlyAdmissions, Is.EqualTo(8));
            Assert.That(f.Db.Control.TesterDailyAdmissions[Wire.Hash(f.Valve.SteamId)], Is.EqualTo(8));
            Assert.That(f.Sts.Requests, Is.Empty);
        });
    }

    [TestCase("day")]
    [TestCase("month")]
    [TestCase("active-count")]
    [TestCase("same-tester")]
    public async Task TesterDailyCandidatePreservesIndependentGlobalAndActiveLimits(string limit)
    {
        var f = new Fixture();
        f.Db.ChangeControl(state =>
        {
            state.Reclaim(f.Clock.Utc);
            if (limit == "day") state.DailyAdmissions = 8;
            if (limit == "month") state.MonthlyAdmissions = 50;
            if (limit is "active-count" or "same-tester")
                for (int active = 0; active < (limit == "active-count" ? 8 : 1); active++)
                    state.Active.Add(Guid.NewGuid(), new() { TesterHash = limit == "same-tester" ? Wire.Hash(f.Valve.SteamId) : "other-" + active,
                        LastSeen = f.Clock.Utc, EndsAt = f.Clock.Utc.AddHours(1) });
        });
        int daily = f.Db.Control.DailyAdmissions, monthly = f.Db.Control.MonthlyAdmissions;
        var request = f.Start();
        var error = await ErrorAsync(() => f.Service.StartAsync(request, Ct), 429);
        Assert.Multiple(() =>
        {
            Assert.That(error.Code, Is.EqualTo("session_quota"));
            Assert.That(f.Db.Control.DailyAdmissions, Is.EqualTo(daily)); Assert.That(f.Db.Control.MonthlyAdmissions, Is.EqualTo(monthly));
            Assert.That(f.Db.Control.TesterDailyAdmissions, Is.Empty); Assert.That(f.Db.Control.Pending, Is.Empty);
            Assert.That(f.Sts.Requests, Is.Empty);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TesterDailyCandidateUsesUtcCalendarRollover(bool monthBoundary)
    {
        var f = new Fixture();
        f.Clock.Utc = DateTimeOffset.Parse(monthBoundary ? "2026-10-31T23:59:59Z" : "2026-10-16T23:59:59Z");
        f.Db.ChangeControl(state =>
        {
            state.Reclaim(f.Clock.Utc); state.DailyAdmissions = 8; state.MonthlyAdmissions = 17;
            state.TesterDailyAdmissions[Wire.Hash(f.Valve.SteamId)] = 8;
        });
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.That((await f.Service.StartAsync(f.Start(), Ct)).State, Is.EqualTo("active"));
        Assert.That(f.Db.Control.DailyAdmissions, Is.EqualTo(1));
        Assert.That(f.Db.Control.TesterDailyAdmissions[Wire.Hash(f.Valve.SteamId)], Is.EqualTo(1));
        Assert.That(f.Db.Control.MonthlyAdmissions, Is.EqualTo(monthBoundary ? 1 : 18));
    }

    [Test]
    public async Task LogsOnlyAdmissionWhenFourVideoSlotsOccupied()
    {
        var f = new Fixture(); StartRequest? fifth = null;
        for (var i = 0; i < 5; i++)
        {
            f.Valve.SteamId = (76561198000000001UL + (ulong)i).ToString(); var start = f.Start(i); fifth = start;
            var result = await f.Service.StartAsync(start, Ct);
            Assert.That(result.VideoAvailability, Is.EqualTo(i == 4 ? "unavailable" : "leased"));
        }
        var auth = await f.Service.AuthorizeAsync(fifth!.SessionId, fifth.SessionProof, f.Authorize(true, Fixture.Object()), Ct);
        Assert.That(auth.Media, Is.Null); Assert.That(auth.Uploads, Has.Length.EqualTo(1)); Assert.That(f.Sts.Requests, Is.Empty);
    }

    [Test]
    public async Task PendingWorkerDeathTakeoverFencesLateCompletion()
    {
        var f = new Fixture(); var start = f.Start(); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource(); var first = true;
        f.Valve.Intercept = async (request, ct) =>
        {
            if (first) { first = false; entered.SetResult(); await release.Task; }
            return Json(request.RequestUri!.AbsolutePath.Contains("AuthenticateUserTicket")
                ? "{\"response\":{\"params\":{\"result\":\"OK\",\"steamid\":\"76561198000000001\"}}}"
                : "{\"appownership\":{\"ownsapp\":true}}");
        };
        var original = f.Service.StartAsync(start, Ct); await entered.Task;
        var pending = await f.Service.StartAsync(start, Ct); Assert.That(pending.State, Is.EqualTo("pending"));
        Assert.That(f.Valve.Requests, Has.Count.EqualTo(1));
        f.Clock.Advance(TimeSpan.FromSeconds(21));
        var replacement = start with { SteamTicketHex = "ABCD", AdmissionUnixSeconds = f.Clock.GetUtcNow().ToUnixTimeSeconds() };
        var accepted = await f.Service.StartAsync(replacement, Ct);
        Assert.That(accepted.Generation, Is.EqualTo(2)); release.SetResult();
        await ErrorAsync(async () => await original, 409);
        Assert.That(f.Db.Control.MonthlyAdmissions, Is.EqualTo(1)); Assert.That(f.Db.Control.Active, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task PendingDeadlineAndThreeGenerationsAreBoundedWithoutTtl()
    {
        var f = new Fixture(); var start = f.Start();
        f.Valve.Intercept = (_, _) => throw new HttpRequestException("OFFLINE ticket=SECRET");
        for (var i = 0; i < 3; i++)
        {
            await ErrorAsync(() => f.Service.StartAsync(start with { SteamTicketHex = (100 + i).ToString("X4") }, Ct), 503);
        }
        await ErrorAsync(() => f.Service.StartAsync(start with { SteamTicketHex = "FFFF" }, Ct), 410);
        Assert.That(f.Db.Control.Pending, Is.Empty); Assert.That(f.Db.Control.MonthlyAdmissions, Is.Zero);
        var other = f.Start(20); var taskEntered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        f.Valve.Intercept = async (_, _) => { taskEntered.TrySetResult(); await release.Task; return Json("{}"); };
        var original = f.Service.StartAsync(other, Ct); await taskEntered.Task;
        f.Clock.Advance(TimeSpan.FromSeconds(91));
        await ErrorAsync(() => f.Service.StartAsync(other with { SteamTicketHex = "FFFE" }, Ct), 410);
        release.SetResult(); await ErrorAsync(async () => await original, 401);
        Assert.That(f.Db.Control.Pending, Is.Empty);
    }

    [Test]
    public async Task EightDeadPendingReservationsAreReclaimedByApplicationDeadline()
    {
        var f = new Fixture(); var entered = new SemaphoreSlim(0); var release = new TaskCompletionSource();
        f.Valve.Intercept = async (_, _) => { entered.Release(); await release.Task; return Json("{}"); };
        var abandoned = new List<Task<StartResult>>();
        for (var i = 0; i < 8; i++) { abandoned.Add(f.Service.StartAsync(f.Start(i), Ct)); await entered.WaitAsync(); }
        await ErrorAsync(() => f.Service.StartAsync(f.Start(20), Ct), 429);
        Assert.That(f.Db.Control.Pending, Has.Count.EqualTo(8)); f.Clock.Advance(TimeSpan.FromSeconds(21));
        f.Valve.Intercept = null;
        Assert.That((await f.Service.StartAsync(f.Start(21), Ct)).State, Is.EqualTo("active"));
        Assert.That(f.Db.Control.Pending, Is.Empty);
        release.SetResult();
        foreach (var task in abandoned) await ErrorAsync(async () => await task, 401);
    }

    [Test]
    public async Task AmbiguousSuccessfulAdmissionCommitIsRecoveredExactlyOnce()
    {
        var f = new Fixture(); var start = f.Start();
        f.Db.BeforeCommit = request =>
        {
            if (request.TransactItems.Any(x => x.Put.Item["data"].S.Contains("\"status\":\"active\""))) { f.Db.ThrowAfterNextCommit = true; f.Db.BeforeCommit = null; }
        };
        Assert.That((await f.Service.StartAsync(start, Ct)).State, Is.EqualTo("active"));
        await f.Service.StartAsync(start, Ct); Assert.That(f.Db.Control.MonthlyAdmissions, Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentObjectRetriesCountVolumeOnceAndConflictOnChange()
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, Ct);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(false, Fixture.Object()), Ct)));
        var session = (await f.Store.GetAsync<SessionState>(DbKey.Session(start.SessionId), Ct))!.Value;
        Assert.That(session.BytesReserved, Is.EqualTo(100)); Assert.That(session.ObjectsReserved, Is.EqualTo(1));
        await ErrorAsync(() => f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(false, Fixture.Object(bytes: 101)), Ct), 409);
        var tampered = f.Authorize(false, Fixture.Object() with { Kind = "../other" });
        await ErrorAsync(() => f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, tampered, Ct), 400);
    }

    [Test]
    public async Task ObjectAndRateQuotaBoundaries()
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, Ct);
        f.Db.ChangeSession(start.SessionId, s => s.BytesReserved = IssuerOptions.MaxBytes - 50);
        await ErrorAsync(() => f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(false, Fixture.Object()), Ct), 429);
        f.Db.ChangeSession(start.SessionId, s => { s.BytesReserved = 0; s.ObjectsReserved = 4096; });
        await ErrorAsync(() => f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(false, Fixture.Object()), Ct), 429);
        f.Db.ChangeSession(start.SessionId, s => s.ObjectsReserved = 0);
        for (var i = 0; i < 30; i++) await f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(), Ct);
        await ErrorAsync(() => f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(), Ct), 429);
    }

    [Test]
    public async Task QuietFifteenMinuteSessionRefreshesThenEndedIsTerminal()
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, Ct);
        for (var minute = 0; minute <= 15; minute++)
        {
            var response = await f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(minute % 10 == 0), Ct);
            Assert.That(response.Uploads, Is.Empty);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.That(f.Sts.Requests, Has.Count.EqualTo(2));
        await f.Service.CloseAsync(start.SessionId, start.SessionProof, new(1, Guid.NewGuid(), "normal"), Ct);
        await ErrorAsync(() => f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(true), Ct), 410);
        await f.Service.CloseAsync(start.SessionId, start.SessionProof, new(1, Guid.NewGuid(), "normal"), Ct);
        Assert.That(f.Sts.Requests, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task InactivityExpiryAndCredentialQuarantineSurviveTtlLag()
    {
        var f = new Fixture(); var first = f.Start(); await f.Service.StartAsync(first, Ct);
        var credentials = (await f.Service.AuthorizeAsync(first.SessionId, first.SessionProof, f.Authorize(true), Ct)).Media!;
        f.Clock.Advance(TimeSpan.FromSeconds(181));
        await ErrorAsync(() => f.Service.AuthorizeAsync(first.SessionId, first.SessionProof, f.Authorize(), Ct), 410);
        f.Valve.SteamId = "76561198000000002"; var second = f.Start(1); var result = await f.Service.StartAsync(second, Ct);
        Assert.That(result.VideoAvailability, Is.EqualTo("leased"));
        var secondState = (await f.Store.GetAsync<SessionState>(DbKey.Session(second.SessionId), Ct))!.Value;
        Assert.That(secondState.Slot, Is.EqualTo(1)); Assert.That(f.Db.Control.Slots[0].QuarantineUntil, Is.EqualTo(credentials.ExpiresAtUtc.AddMinutes(50)));
        f.Clock.Utc = credentials.ExpiresAtUtc.AddMinutes(50); f.Valve.SteamId = "76561198000000003";
        var third = f.Start(2); await f.Service.StartAsync(third, Ct);
        Assert.That((await f.Store.GetAsync<SessionState>(DbKey.Session(third.SessionId), Ct))!.Value.Slot, Is.Zero);
        await f.Service.CloseAsync(first.SessionId, first.SessionProof, new(1, Guid.NewGuid(), "normal"), Ct);
        Assert.That(f.Db.Control.Slots[0].SessionId, Is.EqualTo(third.SessionId), "Stale close cannot release a reused slot.");
    }

    [Test]
    public async Task StsFailurePreservesLogsAndRetriesAreThrottled()
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, Ct); f.Sts.Fail = true;
        var first = await f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(true, Fixture.Object()), Ct);
        Assert.That(first.MediaStatus, Is.EqualTo("unavailable")); Assert.That(first.Uploads, Has.Length.EqualTo(1));
        await f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(true), Ct);
        Assert.That(f.Sts.Requests, Has.Count.EqualTo(1)); f.Clock.Advance(TimeSpan.FromSeconds(61)); f.Sts.Fail = false;
        Assert.That((await f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(true), Ct)).Media, Is.Not.Null);
    }

    [Test]
    public async Task KillBetweenStsAndPublicationWithholdsCredentialsAndGrants()
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, Ct);
        f.Sts.DuringIssue = () => f.Db.ChangeControl(enabled: false);
        var response = await f.Handler.HandleAsync(Event($"/v1/sessions/{start.SessionId:D}/authorize", f.Authorize(true, Fixture.Object()), start.SessionProof), null);
        Assert.That(response.StatusCode, Is.EqualTo(503));
        Assert.That(response.Body, Does.Contain("recording_disabled").And.Not.Contain("OFFLINE").And.Not.Contain("policy"));
    }

    [Test]
    public async Task CloseBetweenReservationAndCredentialPublicationIsFenced()
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, Ct);
        f.Sts.DuringIssue = () => f.Service.CloseAsync(start.SessionId, start.SessionProof, new(1, Guid.NewGuid(), "normal"), Ct).GetAwaiter().GetResult();
        await ErrorAsync(() => f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(true), Ct), 410);
        Assert.That(f.Db.Control.Slots[0].MaxCredentialExpiry, Is.Null, "Undisclosed credentials cannot enter a client producer.");
    }

    [TestCase(false, 403)]
    [TestCase(true, 201)]
    public async Task ValveUsesActiveLicenseNotPermanentPurchase(bool owns, int status)
    {
        var f = new Fixture(); f.Valve.Owns = owns; var start = f.Start();
        Assert.That((await f.Handler.HandleAsync(Event("/v1/sessions", start), null)).StatusCode, Is.EqualTo(status));
    }

    [TestCase("{\"response\":{\"error\":{\"errorcode\":102}}}", 401)]
    [TestCase("{\"response\":{\"params\":{\"result\":\"OK\",\"steamid\":\"playtest\"}}}", 401)]
    [TestCase("{\"response\":{},\"response\":{}}", 503)]
    public async Task InvalidValveRepliesCannotAuthenticate(string json, int status)
    {
        var f = new Fixture(); f.Valve.AuthJson = json;
        await ErrorAsync(() => f.Service.StartAsync(f.Start(), Ct), status);
        Assert.That(f.Db.Control.MonthlyAdmissions, Is.Zero);
    }

    [TestCase(302)]
    [TestCase(500)]
    public async Task ValveRedirectAndFailuresAreSanitized(int status)
    {
        var f = new Fixture(); f.Valve.Intercept = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("PRIVATE-TICKET-AND-KEY") });
        var response = await f.Handler.HandleAsync(Event("/v1/sessions", f.Start()), null);
        Assert.That(response.StatusCode, Is.EqualTo(503)); Assert.That(response.Body, Does.Not.Contain("PRIVATE")); Assert.That(f.Valve.Requests, Has.Count.EqualTo(1));
    }

    [TestCase("{}", 401, "fresh_ticket_required", "missing_response", 1, 1)]
    [TestCase("{\"response\":{}}", 401, "fresh_ticket_required", "missing_params", 1, 1)]
    [TestCase("{\"response\":{\"error\":{}}}", 401, "fresh_ticket_required", "valve_error", 1, 1)]
    [TestCase("{\"response\":{\"error\":\"PRIVATE-ERROR\"}}", 401, "fresh_ticket_required", "missing_params", 1, 1)]
    [TestCase("{\"response\":{\"params\":{}}}", 401, "fresh_ticket_required", "result_not_ok", 1, 1)]
    [TestCase("{\"response\":{\"params\":{\"result\":\"PRIVATE-RESULT\"}}}", 401, "fresh_ticket_required", "result_not_ok", 1, 1)]
    [TestCase("{\"response\":{\"params\":{\"result\":null}}}", 401, "fresh_ticket_required", "result_not_ok", 1, 1)]
    [TestCase("{\"response\":{\"params\":{\"result\":\"OK\"}}}", 401, "fresh_ticket_required", "missing_steamid", 1, 1)]
    [TestCase("{\"response\":{\"params\":{\"result\":\"OK\",\"steamid\":123}}}", 401, "fresh_ticket_required", "missing_steamid", 1, 1)]
    [TestCase("{\"response\":{\"params\":{\"result\":\"OK\",\"steamid\":null}}}", 401, "fresh_ticket_required", "missing_steamid", 1, 1)]
    [TestCase("{\"response\":{\"params\":{\"result\":\"OK\",\"steamid\":\"PRIVATE-STEAMID\"}}}", 401, "invalid_steam_ticket", null, null, 1)]
    [TestCase("[]", 503, "steam_unavailable", null, 2, 1)]
    [TestCase("null", 503, "steam_unavailable", null, 2, 1)]
    [TestCase("{\"response\":null}", 503, "steam_unavailable", null, 2, 1)]
    [TestCase("{\"response\":[]}", 503, "steam_unavailable", null, 2, 1)]
    [TestCase("{\"response\":\"PRIVATE-RESPONSE\"}", 503, "steam_unavailable", null, 2, 1)]
    [TestCase("{\"response\":{\"params\":null}}", 503, "steam_unavailable", null, 2, 1)]
    [TestCase("{\"response\":{\"params\":[]}}", 503, "steam_unavailable", null, 2, 1)]
    [TestCase("{\"response\":{\"params\":{\"result\":1}}}", 503, "steam_unavailable", null, 2, 1)]
    [TestCase("{\"response\":{\"params\":{\"result\":true}}}", 503, "steam_unavailable", null, 2, 1)]
    [TestCase("{\"response\":{\"params\":{\"result\":{}}}}", 503, "steam_unavailable", null, 2, 1)]
    [TestCase("{\"response\":{\"params\":{\"result\":[]}}}", 503, "steam_unavailable", null, 2, 1)]
    [TestCase("{\"response\":{},\"response\":{}}", 503, "steam_unavailable", null, 2, 1)]
    [TestCase("{\"response\":{\"error\":{\"errorcode\":102,\"errorcode\":103}}}", 503, "steam_unavailable", null, 2, 1)]
    [TestCase("PRIVATE-NON-JSON", 503, "steam_unavailable", null, 2, 1)]
    // An error envelope must never take precedence over the original params path.
    [TestCase("{\"response\":{\"params\":{\"result\":\"OK\",\"steamid\":\"76561198000000001\"},\"error\":{\"errorcode\":102,\"errordesc\":\"PRIVATE invalid key\"}}}", 201, null, null, null, 2)]
    [TestCase("{\"response\":{\"params\":null,\"error\":{\"errorcode\":102,\"errordesc\":\"PRIVATE invalid key\"}}}", 503, "steam_unavailable", null, 2, 1)]
    [TestCase("{\"response\":{\"params\":{\"result\":\"PRIVATE-FAILURE\"},\"error\":{\"errorcode\":102}}}", 401, "fresh_ticket_required", "result_not_ok", 1, 1)]
    public async Task HandlerAuthenticationDiagnosticsPreserveExistingBranches(string json, int status, string? code,
        string? category, int? retryAfter, int valveRequests)
    {
        var f = new Fixture(); f.Valve.AuthJson = json;
        var response = await f.Handler.HandleAsync(Event("/v1/sessions", f.Start()), null);
        using var body = JsonDocument.Parse(response.Body); var root = body.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(status));
            Assert.That(root.TryGetProperty("code", out var actualCode) ? actualCode.GetString() : null, Is.EqualTo(code));
            Assert.That(root.TryGetProperty("steamAuthFailure", out var actualCategory) ? actualCategory.GetString() : null, Is.EqualTo(category));
            Assert.That(root.TryGetProperty("valveErrorCode", out _), Is.False);
            Assert.That(root.TryGetProperty("valveErrorTerms", out _), Is.False);
            Assert.That(root.TryGetProperty("publisherKeyShape", out var shape) ? shape.GetString() : null,
                Is.EqualTo(category == "valve_error" ? "unexpected_shape" : null));
            Assert.That(response.Headers.TryGetValue("retry-after", out var retry) ? retry : null, Is.EqualTo(retryAfter?.ToString()));
            Assert.That(root.GetProperty("protocolVersion").GetInt32(), Is.EqualTo(1));
            Assert.That(root.GetProperty("serverUtc").GetDateTimeOffset(), Is.EqualTo(f.Clock.GetUtcNow()));
            Assert.That(response.Headers["cache-control"], Is.EqualTo("no-store"));
            Assert.That(response.Body, Does.Not.Contain("PRIVATE").And.Not.Contain("76561198000000001"));
            Assert.That(f.Valve.Requests, Has.Count.EqualTo(valveRequests));
            Assert.That(f.Db.Control.MonthlyAdmissions, Is.EqualTo(status == 201 ? 1 : 0));
        });
    }

    [TestCase("0", 0)]
    [TestCase("102", 102)]
    [TestCase("65535", 65535)]
    [TestCase("-1", null)]
    [TestCase("65536", null)]
    [TestCase("2147483648", null)]
    [TestCase("999999999999999999999999999999999999", null)]
    [TestCase("102.5", null)]
    [TestCase("102.0", null)]
    [TestCase("1e2", null)]
    [TestCase("1e1000", null)]
    [TestCase("\"102\"", null)]
    [TestCase("\"PRIVATE-CODE\"", null)]
    [TestCase("null", null)]
    [TestCase("true", null)]
    [TestCase("{}", null)]
    [TestCase("[]", null)]
    public async Task HandlerValveErrorCodeIsOnlyABoundedInteger(string number, int? expected)
    {
        var f = new Fixture();
        f.Valve.AuthJson = "{\"response\":{\"error\":{\"errorcode\":" + number
            + ",\"errordesc\":\"PRIVATE-TICKET-KEY-URL-IDENTITY-STEAMID\",\"unknown\":\"PRIVATE-EXTRA\"}}}";
        var response = await f.Handler.HandleAsync(Event("/v1/sessions", f.Start()), null);
        using var body = JsonDocument.Parse(response.Body); var root = body.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(401));
            Assert.That(root.GetProperty("code").GetString(), Is.EqualTo("fresh_ticket_required"));
            Assert.That(response.Headers["retry-after"], Is.EqualTo("1"));
            Assert.That(root.GetProperty("steamAuthFailure").GetString(), Is.EqualTo("valve_error"));
            Assert.That(root.TryGetProperty("valveErrorCode", out var actual) ? (int?)actual.GetInt32() : null, Is.EqualTo(expected));
            Assert.That(root.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(expected.HasValue
                ? new[] { "protocolVersion", "serverUtc", "code", "steamAuthFailure", "valveErrorCode", "valveErrorTerms", "publisherKeyShape" }
                : new[] { "protocolVersion", "serverUtc", "code", "steamAuthFailure", "valveErrorTerms", "publisherKeyShape" }));
            Assert.That(root.GetProperty("valveErrorTerms").EnumerateArray().Select(t => t.GetString()), Is.EqualTo(new[] { "identity", "ticket", "key" }));
            Assert.That(root.GetProperty("publisherKeyShape").GetString(), Is.EqualTo("unexpected_shape"));
            Assert.That(response.Body, Does.Not.Contain("PRIVATE").And.Not.Contain("errorcode").And.Not.Contain("errordesc"));
            Assert.That(f.Valve.Requests, Has.Count.EqualTo(1));
            Assert.That(f.Db.Control.MonthlyAdmissions, Is.Zero);
        });
    }

    [Test]
    public async Task NonValveRetryAndOwnershipResponsesDoNotCarryAuthenticationAnnotations()
    {
        var f = new Fixture(); var start = f.Start();
        f.Valve.AuthJson = "{\"response\":{\"error\":{\"errorcode\":102}}}";
        Assert.That((await f.Handler.HandleAsync(Event("/v1/sessions", start), null)).StatusCode, Is.EqualTo(401));
        // The durable same-ticket retry and stale config timestamp share the public code,
        // but must not accidentally inherit a prior Valve diagnostic or numeric code.
        var sameTicket = await f.Handler.HandleAsync(Event("/v1/sessions", start), null);
        var stale = await f.Handler.HandleAsync(Event("/v1/sessions", f.Start() with { AdmissionUnixSeconds = start.AdmissionUnixSeconds - 301 }), null);
        f.Valve.AuthJson = ""; f.Valve.Owns = false;
        var ownership = await f.Handler.HandleAsync(Event("/v1/sessions", f.Start(1)), null);
        foreach (var (response, status, code, retry) in new[]
        {
            (sameTicket, 409, "fresh_ticket_required", "1"),
            (stale, 400, "fresh_ticket_required", (string?)null),
            (ownership, 403, "playtest_license_required", (string?)null)
        })
        {
            using var body = JsonDocument.Parse(response.Body); var root = body.RootElement;
            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(status));
                Assert.That(root.GetProperty("code").GetString(), Is.EqualTo(code));
                Assert.That(response.Headers.TryGetValue("retry-after", out var value) ? value : null, Is.EqualTo(retry));
                Assert.That(root.TryGetProperty("steamAuthFailure", out _), Is.False);
                Assert.That(root.TryGetProperty("valveErrorCode", out _), Is.False);
                Assert.That(root.TryGetProperty("valveErrorTerms", out _), Is.False);
                Assert.That(root.TryGetProperty("publisherKeyShape", out _), Is.False);
            });
        }
    }

    [TestCase(null, null)]
    [TestCase("null", null)]
    [TestCase("123", null)]
    [TestCase("true", null)]
    [TestCase("{}", null)]
    [TestCase("[]", null)]
    [TestCase("\"\"", "")]
    [TestCase("\"unrecognized private server message\"", "")]
    [TestCase("\"IDENTITY Ticket APPID app KEY eXpIrEd CANCELED cancelled MISMATCH invalid\"", "identity,appid,app,ticket,key,expired,canceled,cancelled,mismatch,invalid")]
    [TestCase("\"invalid INVALID invalid ticket ticket KEY key identity identity\"", "identity,ticket,key,invalid")]
    [TestCase("\"identity-appid/app:ticket;key expired(canceled),cancelled!mismatch?invalid\"", "identity,appid,app,ticket,key,expired,canceled,cancelled,mismatch,invalid")]
    [TestCase("\"identity0 0identity xidentity identityx identity_ _identity appid_hash appid0 hashappid ticketing tickets key1 _key_ expired2 canceled_ cancelled1 mismatch2 invalid9\"", "")]
    [TestCase("\"identity appid app ticket key expired canceled cancelled mismatch invalid\u005f\"", "identity,appid,app,ticket,key,expired,canceled,cancelled,mismatch")]
    [TestCase("\"éidentityé ıdentity İDENTITY ｉｄｅｎｔｉｔｙ\"", "identity")]
    [TestCase("\"idEntity\\tkey\\nexpired\\u0000mismatch\"", "identity,key,expired,mismatch")]
    [TestCase("\"PRIVATE-TICKET-KEY-URL-IDENTITY-STEAMID https://private.invalid/PRIVATE?key=PRIVATE-CREDENTIAL ticket=PRIVATE-PROOF\"", "identity,ticket,key,invalid")]
    public async Task HandlerValveDescriptionEmitsOnlyFixedWholeAsciiTerms(string? descriptionJson, string? expectedTerms)
    {
        var f = new Fixture();
        f.Valve.AuthJson = "{\"response\":{\"error\":{\"errorcode\":105"
            + (descriptionJson is null ? "" : ",\"errordesc\":" + descriptionJson) + "}}}";
        var response = await f.Handler.HandleAsync(Event("/v1/sessions", f.Start()), null);
        using var body = JsonDocument.Parse(response.Body); var root = body.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(401));
            Assert.That(root.GetProperty("code").GetString(), Is.EqualTo("fresh_ticket_required"));
            Assert.That(response.Headers["retry-after"], Is.EqualTo("1"));
            Assert.That(root.GetProperty("steamAuthFailure").GetString(), Is.EqualTo("valve_error"));
            Assert.That(root.GetProperty("valveErrorCode").GetInt32(), Is.EqualTo(105));
            Assert.That(root.TryGetProperty("valveErrorTerms", out _), Is.EqualTo(expectedTerms is not null));
            if (expectedTerms is not null)
            {
                var terms = root.GetProperty("valveErrorTerms").EnumerateArray().Select(t => t.GetString()).ToArray();
                Assert.That(terms, Is.EqualTo(expectedTerms.Length == 0 ? Array.Empty<string>() : expectedTerms.Split(',')));
                Assert.That(terms.Distinct().Count(), Is.EqualTo(terms.Length));
                Assert.That(terms, Has.Length.LessThanOrEqualTo(10));
            }
            Assert.That(root.GetProperty("publisherKeyShape").GetString(), Is.EqualTo("unexpected_shape"));
            Assert.That(response.Body, Does.Not.Contain("PRIVATE").And.Not.Contain("errordesc").And.Not.Contain("https://").And.Not.Contain("OFFLINE-PUBLISHER-KEY"));
            Assert.That(f.Secrets.Calls, Is.EqualTo(1));
            Assert.That(f.Valve.Requests, Has.Count.EqualTo(1));
            Assert.That(f.Db.Control.MonthlyAdmissions, Is.Zero);
        });
    }

    [TestCase(512, true)]
    [TestCase(513, false)]
    [TestCase(16000, false)]
    public async Task HandlerValveDescriptionObservationIsBounded(int length, bool inspected)
    {
        var f = new Fixture();
        string description = new string('x', length - 7) + " ticket";
        f.Valve.AuthJson = JsonSerializer.Serialize(new { response = new { error = new { errorcode = 105, errordesc = description } } });
        var response = await f.Handler.HandleAsync(Event("/v1/sessions", f.Start()), null);
        using var body = JsonDocument.Parse(response.Body); var root = body.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(401));
            Assert.That(response.Headers["retry-after"], Is.EqualTo("1"));
            Assert.That(root.TryGetProperty("valveErrorTerms", out _), Is.EqualTo(inspected));
            if (inspected) Assert.That(root.GetProperty("valveErrorTerms").EnumerateArray().Select(t => t.GetString()), Is.EqualTo(new[] { "ticket" }));
            Assert.That(response.Body, Does.Not.Contain(new string('x', 32)));
        });
    }

    [TestCase("0123456789abcdef0123456789abcdef", "expected_shape")]
    [TestCase("0123456789ABCDEF0123456789ABCDEF", "expected_shape")]
    [TestCase("00000000000000000000000000000000", "expected_shape")]
    [TestCase("0123456789abcdef0123456789abcde", "unexpected_shape")]
    [TestCase("0123456789abcdef0123456789abcdef0", "unexpected_shape")]
    [TestCase("0123456789abcdef0123456789abcdeg", "unexpected_shape")]
    [TestCase(" 0123456789abcdef0123456789abcdef ", "unexpected_shape")]
    [TestCase("\"0123456789abcdef0123456789abcdef\"", "unexpected_shape")]
    [TestCase("{\"key\":\"0123456789abcdef0123456789abcdef\"}", "unexpected_shape")]
    public async Task HandlerPublisherKeyShapeIsOnlyAHeuristicWithoutAnotherReadOrMutation(string key, string expectedShape)
    {
        var f = new Fixture(); f.Secrets.Value = key;
        f.Valve.AuthJson = "{\"response\":{\"error\":{\"errorcode\":105}}}";
        for (int generation = 0; generation < 2; generation++)
        {
            var response = await f.Handler.HandleAsync(Event("/v1/sessions", f.Start(generation)), null);
            using var body = JsonDocument.Parse(response.Body); var root = body.RootElement;
            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(401));
                Assert.That(root.GetProperty("code").GetString(), Is.EqualTo("fresh_ticket_required"));
                Assert.That(root.GetProperty("steamAuthFailure").GetString(), Is.EqualTo("valve_error"));
                Assert.That(root.GetProperty("publisherKeyShape").GetString(), Is.EqualTo(expectedShape));
                Assert.That(root.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[]
                    { "protocolVersion", "serverUtc", "code", "steamAuthFailure", "valveErrorCode", "publisherKeyShape" }));
                Assert.That(response.Headers["retry-after"], Is.EqualTo("1"));
                Assert.That(f.Valve.Requests[generation].Key, Is.EqualTo(key), "Shape observation must not trim/unwrap the transmitted key.");
                Assert.That(response.Body, Does.Not.Contain(key));
                Assert.That(f.Secrets.Calls, Is.EqualTo(1), "Observation reuses the already-held cached key.");
                Assert.That(f.Db.Control.MonthlyAdmissions, Is.Zero);
            });
        }
    }

    [Test]
    public async Task OversizedValveBodyIsBoundedAndSecretKeyIsCached()
    {
        var f = new Fixture(); f.Valve.Intercept = (_, _) => Task.FromResult(Json(new string('x', 32769)));
        await ErrorAsync(() => f.Service.StartAsync(f.Start(), Ct), 503);
        f.Valve.Intercept = null; await f.Service.StartAsync(f.Start(1), Ct);
        Assert.That(f.Secrets.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task CrossSessionAndCombinedAuthorizationHeadersAreRejected()
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, Ct); var wrong = f.Start(1).SessionProof;
        await ErrorAsync(() => f.Service.AuthorizeAsync(start.SessionId, wrong, f.Authorize(), Ct), 401);
        var ev = Event($"/v1/sessions/{start.SessionId:D}/authorize", f.Authorize(), start.SessionProof);
        ev.Headers["authorization"] += ",Bearer " + wrong;
        Assert.That((await f.Handler.HandleAsync(ev, null)).StatusCode, Is.EqualTo(401));
    }

    [Test]
    public async Task TransactionRequestsHaveUniqueTargetsAndControlFences()
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, Ct);
        await f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(false, Fixture.Object()), Ct);
        foreach (var request in f.Db.Transactions)
        {
            Assert.That(request.TransactItems.Select(x => x.Put.Item["pk"].S + "/" + x.Put.Item["sk"].S).Distinct().Count(), Is.EqualTo(request.TransactItems.Count));
            var control = request.TransactItems.Single(x => x.Put.Item["pk"].S == "CONTROL").Put;
            Assert.That(control.ConditionExpression, Does.Contain("#v = :v").And.Contain("#ae = :ae").And.Contain("#ue = :ue"));
        }
    }

    [Test]
    public async Task LostReservationResponseRecoversWithFreshAutomaticTicket()
    {
        var f = new Fixture(); var start = f.Start(); f.Db.ThrowAfterNextCommit = true;
        await ErrorAsync(() => f.Service.StartAsync(start, Ct), 503);
        Assert.That((await f.Service.StartAsync(start, Ct)).State, Is.EqualTo("pending"));
        Assert.That(f.Valve.Requests, Is.Empty);
        f.Clock.Advance(TimeSpan.FromSeconds(21));
        await ErrorAsync(() => f.Service.StartAsync(start, Ct), 409);
        Assert.That((await f.Service.StartAsync(start with { SteamTicketHex = "DCBA", AdmissionUnixSeconds = f.Clock.GetUtcNow().ToUnixTimeSeconds() }, Ct)).Generation, Is.EqualTo(2));
        Assert.That(f.Db.Control.MonthlyAdmissions, Is.EqualTo(1));
    }

    [Test]
    public async Task LostCredentialPublicationResponseStillTracksExpiryAndAllowsRetry()
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, Ct);
        f.Db.BeforeCommit = request =>
        {
            if (request.TransactItems.Any(x => x.Put.Item["data"].S.Contains("\"maxCredentialExpiry\":\"")))
            { f.Db.ThrowAfterNextCommit = true; f.Db.BeforeCommit = null; }
        };
        await ErrorAsync(() => f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(true), Ct), 503);
        var firstExpiry = f.Db.Control.Slots[0].MaxCredentialExpiry ?? throw new InvalidOperationException("Missing durable credential expiry.");
        Assert.That(firstExpiry, Is.EqualTo(f.Clock.GetUtcNow().AddMinutes(15)));
        f.Clock.Advance(TimeSpan.FromSeconds(61));
        var retry = await f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(true), Ct);
        Assert.That(f.Db.Control.Slots[0].MaxCredentialExpiry, Is.EqualTo(retry.Media!.ExpiresAtUtc).And.GreaterThan(firstExpiry));
    }

    [Test]
    public async Task KillAtActualTransactionBoundaryCannotPublishPreviouslySignedGrants()
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, Ct);
        f.Db.BeforeCommit = request =>
        {
            if (request.TransactItems.Any(x => x.Put.Item["data"].S.Contains("\"maxCredentialExpiry\":\"")))
            { f.Db.BeforeCommit = null; f.Db.ChangeControl(enabled: false); }
        };
        await ErrorAsync(() => f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(true, Fixture.Object()), Ct), 503);
        Assert.That(f.Db.Control.Slots[0].MaxCredentialExpiry, Is.Null);
    }

    [Test]
    public async Task CaptureActualAdapterCostInputsForOneAndEightActiveSessions()
    {
        var samples = new List<object>();
        foreach (var activeCount in new[] { 1, 8 })
        {
            var f = new Fixture(); StartRequest? first = null;
            for (var i = 0; i < activeCount; i++)
            {
                f.Valve.SteamId = (76561198000000001UL + (ulong)i).ToString(); var start = f.Start(i); first ??= start;
                await f.Service.StartAsync(start, Ct);
            }
            var reads = f.Db.Reads; var transactions = f.Db.Transactions.Count;
            await f.Service.AuthorizeAsync(first!.SessionId, first.SessionProof, f.Authorize(false, Fixture.Object()), Ct);
            var puts = f.Db.Transactions.Skip(transactions).SelectMany(x => x.TransactItems.Select(i => i.Put)).ToArray();
            // String/attribute-name byte counts are measured from production requests. Numeric
            // representation is conservatively counted as UTF8 text; real metering remains live evidence.
            var sizes = puts.Select(p => p.Item.Sum(a => System.Text.Encoding.UTF8.GetByteCount(a.Key)
                + (a.Value.S is { } s ? System.Text.Encoding.UTF8.GetByteCount(s) : a.Value.N is { } n ? n.Length : 1))).ToArray();
            samples.Add(new { activeCount, strongGetItemCalls = f.Db.Reads - reads, transactWriteItemsCalls = f.Db.Transactions.Count - transactions,
                putItems = puts.Length, estimatedItemBytes = sizes, estimatedTransactionalWriteUnits = sizes.Sum(s => 2 * (int)Math.Ceiling(s / 1024d)) });
        }
        var path = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../../private/evidence/cost-inputs.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StaggeredTtlAndReusedUuidReserveNewVolumeOnce(bool changeDescriptor)
    {
        var f = new Fixture(); f.Clock.Utc = DateTimeOffset.Parse("2026-09-13T21:00:00Z");
        var start = f.Start(); await f.Service.StartAsync(start, Ct);
        var original = await f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(false, Fixture.Object()), Ct);
        var oldReceipt = (await f.Store.GetAsync<StoredObject>(DbKey.Object(start.SessionId, Fixture.Object()), Ct))!;
        f.Clock.Advance(TimeSpan.FromDays(31));
        Assert.That(oldReceipt.Value.PurgeAt, Is.LessThan(f.Clock.GetUtcNow().ToUnixTimeSeconds()));
        f.Db.DeleteRow(DbKey.Session(start.SessionId)); // TTL deletes each item independently.
        var again = start with { AdmissionUnixSeconds = f.Clock.GetUtcNow().ToUnixTimeSeconds(), SteamTicketHex = "ABCD" };
        await f.Service.StartAsync(again, Ct); // Even the same proof/build/consent cannot reuse an old admission.
        var descriptor = Fixture.Object(bytes: changeDescriptor ? 200 : 100);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            f.Service.AuthorizeAsync(again.SessionId, again.SessionProof, f.Authorize(false, descriptor), Ct)));
        foreach (var result in results)
        {
            Assert.That(result.Uploads.Single().Key, Is.EqualTo($"sessions/2026-10-14/{start.SessionId:N}/log/000000.gz"));
            Assert.That(result.Uploads.Single().Key, Is.Not.EqualTo(original.Uploads.Single().Key));
            Assert.That(result.RemainingBytes, Is.EqualTo(IssuerOptions.MaxBytes - descriptor.CompressedBytes));
            Assert.That(result.RemainingObjects, Is.EqualTo(4095));
        }
        var receipt = (await f.Store.GetAsync<StoredObject>(DbKey.Object(start.SessionId, descriptor), Ct))!;
        var session = (await f.Store.GetAsync<SessionState>(DbKey.Session(start.SessionId), Ct))!.Value;
        Assert.That(receipt.Value.AdmissionId, Is.EqualTo(session.AdmissionId).And.Not.EqualTo(oldReceipt.Value.AdmissionId));
        Assert.That(receipt.Version, Is.EqualTo(oldReceipt.Version + 1));
        Assert.That(session.BytesReserved, Is.EqualTo(descriptor.CompressedBytes));
        Assert.That(session.ObjectsReserved, Is.EqualTo(1));
        var replacement = f.Db.Transactions.SelectMany(t => t.TransactItems).Select(i => i.Put)
            .Last(p => p.Item["sk"].S == "OBJECT#log#000000");
        Assert.That(replacement.ConditionExpression, Is.EqualTo("#v = :v"));
        Assert.That(replacement.ExpressionAttributeValues[":v"].N, Is.EqualTo(oldReceipt.Version.ToString()));
    }

    [Test]
    public async Task ScheduledConfigPathClearsIdleSessionReferencesWithoutTtlOrTraffic()
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, Ct);
        await f.Service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(true), Ct);
        f.Clock.Advance(TimeSpan.FromDays(35));
        await f.Service.ConfigAsync(Ct);
        Assert.That(f.Db.Control.Active, Is.Empty); Assert.That(f.Db.Control.Pending, Is.Empty);
        Assert.That(f.Db.Control.TesterDailyAdmissions, Is.Empty);
        Assert.That(f.Db.Control.Slots.All(s => s.SessionId is null && s.Generation == "" && s.MaxCredentialExpiry is null), Is.True);
        Assert.That(f.Sts.Requests, Has.Count.EqualTo(1));
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    [Test]
    public void PortableClientGoldenStartJsonIsAcceptedByActualIssuerSerializer()
    {
        // Same byte-level fixture asserted by Unity's client tests, including UUID-D,
        // explicit nullable Steam build ID and round-trip UTC consent timestamp.
        const string body = """{"protocolVersion":1,"sessionId":"00112233-4455-6677-8899-aabbccddeeff","admissionUnixSeconds":1792000000,"sessionProof":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","steamTicketHex":"0102","build":{"gameVersion":"test","steamBuildId":null},"consent":{"schemaVersion":1,"gameplayAudioAndLogs":true,"microphone":false,"acceptedAtUtc":"2026-10-15T12:00:00.0000000+00:00"}}""";
        var request = JsonSerializer.Deserialize<StartRequest>(body, Wire.Json)!;
        Assert.DoesNotThrow(request.Validate);
        Assert.That(request.Build.SteamBuildId, Is.Null);
        Assert.That(request.Consent.AcceptedAtUtc.Offset, Is.EqualTo(TimeSpan.Zero));
        Assert.That(Wire.Identity(request.SessionId, request.AdmissionUnixSeconds, request.SessionProof), Is.EqualTo("prrec2:f3xyqizKHT2FlXV-Zk3cwg"));
    }
}
