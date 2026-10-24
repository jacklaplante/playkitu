using System.Text.Json;
using NUnit.Framework;
using Playkitu.Client;
using Playkitu.Issuer;
using Playkitu.Tests;

namespace Issuer.Tests;

[TestFixture]
public sealed class FourHourDiagnosticsTests
{
    private static async Task<IssuerError> Error(Func<Task> operation, int status)
    {
        try { await operation(); Assert.Fail("Expected issuer refusal."); }
        catch (IssuerError error) { Assert.That(error.Status, Is.EqualTo(status)); return error; }
        throw new InvalidOperationException();
    }
    private static StartRequest OptIn(Fixture f) => f.Start() with { MetadataFinalizationSeconds = RecordingDiagnosticsLimits.MetadataFinalizationSeconds };
    private static async Task HeartbeatUntil(Fixture f, StartRequest request, DateTimeOffset until)
    {
        while (f.Clock.Utc.AddMinutes(1) < until) {
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            await f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(), default);
        }
        f.Clock.Utc = until;
    }

    [Test] public async Task LegacyBindingQuotaAndExpiryRemainExactAndCapabilityIsImmutable()
    {
        var f = new Fixture(); var request = f.Start();
        Assert.That(request.Binding(), Is.EqualTo(Wire.Hash(JsonSerializer.Serialize(new {
            request.SessionId, ProofHash = Wire.Hash(request.SessionProof), request.Build, request.Consent }, Wire.Json))));
        var result = await f.Service.StartAsync(request, default);
        Assert.That(result.MetadataUntilUtc, Is.Null);
        var auth = await f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(false, Fixture.Object()), default);
        Assert.That(auth.RemainingBytes, Is.EqualTo(IssuerOptions.MaxBytes - 100));
        Assert.That(auth.RemainingObjects, Is.EqualTo(4095)); Assert.That(auth.Uploads.Single().ExpiresAtUtc, Is.LessThanOrEqualTo(result.EndsAtUtc!.Value));
        await Error(() => f.Service.StartAsync(request with { MetadataFinalizationSeconds = 60 }, default), 409);
        await HeartbeatUntil(f, request, result.EndsAtUtc!.Value);
        await Error(() => f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(), default), 410);
        Assert.That(() => (request with { MetadataFinalizationSeconds = 61 }).Validate(), Throws.TypeOf<IssuerError>());
    }

    [Test] public async Task GracePreservesMediaCeilingAndReclaimBindingThenExpiresExactly()
    {
        var f = new Fixture(); var request = OptIn(f); var result = await f.Service.StartAsync(request, default);
        var end = result.EndsAtUtc!.Value;
        Assert.That(end, Is.EqualTo(f.Clock.Utc.AddSeconds(14400))); Assert.That(result.MetadataUntilUtc, Is.EqualTo(end.AddSeconds(60)));
        await Error(() => f.Service.StartAsync(request with { MetadataFinalizationSeconds = 0 }, default), 409);
        await HeartbeatUntil(f, request, end.AddSeconds(-1));
        var before = await f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(true), default);
        Assert.That(before.Media, Is.Not.Null);
        using (var policy = JsonDocument.Parse(f.Sts.Requests.Single().Policy))
            Assert.That(policy.RootElement.GetProperty("Statement")[0].GetProperty("Condition").GetProperty("DateLessThan").GetProperty("aws:CurrentTime").GetString(),
                Is.EqualTo(end.ToString("yyyy-MM-ddTHH:mm:ssZ")));
        f.Clock.Utc = end; await f.Service.ConfigAsync(default);
        var control = await f.Store.GetControlAsync(default);
        Assert.That(control.State.Value.Active.ContainsKey(request.SessionId), Is.True);
        Assert.That(control.State.Value.Slots.Any(s => s.SessionId == request.SessionId), Is.True);
        var auth = await f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(true, Fixture.Object()), default);
        Assert.That(auth.Media, Is.Null); Assert.That(auth.MediaStatus, Is.EqualTo("unavailable")); Assert.That(f.Sts.Requests, Has.Count.EqualTo(1));
        Assert.That(auth.Uploads.Single().ExpiresAtUtc, Is.EqualTo(end.AddSeconds(60)));
        Assert.That(auth.MetadataUntilUtc, Is.EqualTo(result.MetadataUntilUtc));
        f.Clock.Utc = end.AddSeconds(59); await f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(), default);
        f.Clock.Utc = end.AddSeconds(60); await Error(() => f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(), default), 410);
        await f.Service.ConfigAsync(default); control = await f.Store.GetControlAsync(default);
        Assert.That(control.State.Value.Active.ContainsKey(request.SessionId), Is.False);
        Assert.That(control.State.Value.Slots.All(s => s.SessionId != request.SessionId), Is.True);
        Assert.That(control.State.Value.Slots[0].QuarantineUntil, Is.EqualTo(before.Media!.ExpiresAtUtc.AddMinutes(50)));
    }

    [TestCase("close")] [TestCase("disabled")] [TestCase("inactivity")] [TestCase("wrong-proof")] [TestCase("wrong-admission")]
    public async Task GraceCannotBypassExistingRevocationAndIdentityChecks(string fault)
    {
        var f = new Fixture(); var request = OptIn(f); var result = await f.Service.StartAsync(request, default);
        await HeartbeatUntil(f, request, result.EndsAtUtc!.Value);
        var proof = request.SessionProof; var status = 410;
        if (fault == "close") await f.Service.CloseAsync(request.SessionId, proof, new(1, Guid.NewGuid(), "normal"), default);
        if (fault == "disabled") { f.Db.ChangeControl(enabled: false); status = 503; }
        if (fault == "inactivity") f.Db.ChangeControl(g => g.Active[request.SessionId].LastSeen = f.Clock.Utc.AddSeconds(-180));
        if (fault == "wrong-proof") { proof = new string('a', 43); status = 401; }
        if (fault == "wrong-admission") f.Db.ChangeSession(request.SessionId, s => s.SlotGeneration = "other");
        await Error(() => f.Service.AuthorizeAsync(request.SessionId, proof, f.Authorize(false, Fixture.Object()), default), status);
        Assert.That(f.Sts.Requests, Is.Empty);
    }

    [Test] public async Task MediaResolutionCrossingCaptureExpiryCannotPublishCredentials()
    {
        var f = new Fixture(); var request = OptIn(f); var result = await f.Service.StartAsync(request, default);
        await HeartbeatUntil(f, request, result.EndsAtUtc!.Value.AddSeconds(-1));
        f.Sts.DuringIssue = () => f.Clock.Advance(TimeSpan.FromSeconds(1));
        var auth = await f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(true, Fixture.Object()), default);
        Assert.That(auth.Media, Is.Null); Assert.That(auth.MediaStatus, Is.EqualTo("unavailable")); Assert.That(auth.Uploads, Has.Length.EqualTo(1));
    }

    [Test] public async Task ReservationCommitCrossingCaptureExpiryCannotStartSts()
    {
        var f = new Fixture(); var request = OptIn(f); var start = await f.Service.StartAsync(request, default);
        await HeartbeatUntil(f, request, start.EndsAtUtc!.Value.AddSeconds(-1));
        f.Db.BeforeCommit = transaction => {
            if (transaction.TransactItems.Count != 3) return;
            f.Db.BeforeCommit = null; f.Clock.Advance(TimeSpan.FromSeconds(1));
        };
        var auth = await f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(true, Fixture.Object()), default);
        Assert.That(f.Sts.Requests, Is.Empty, "Reservation awaited across EndsAt; media authorization must recheck before invoking STS.");
        Assert.That(auth.Media, Is.Null); Assert.That(auth.MediaStatus, Is.EqualTo("unavailable"));
        Assert.That(auth.Uploads, Has.Length.EqualTo(1)); Assert.That(auth.MetadataUntilUtc, Is.EqualTo(start.MetadataUntilUtc));
    }

    [TestCase(false)] [TestCase(true)]
    public async Task PublicationCommitCrossingDeadlineRechecksResponse(bool metadataDeadline)
    {
        var f = new Fixture(); var request = OptIn(f); var start = await f.Service.StartAsync(request, default);
        await HeartbeatUntil(f, request, start.EndsAtUtc!.Value.AddSeconds(-1));
        f.Db.BeforeCommit = transaction => {
            if (transaction.TransactItems.Count != 2) return;
            f.Db.BeforeCommit = null;
            f.Clock.Utc = metadataDeadline ? start.MetadataUntilUtc!.Value : start.EndsAtUtc.Value;
        };
        Task<AuthorizationResult> Authorize() => f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(true, Fixture.Object()), default);
        if (metadataDeadline) await Error(Authorize, 410);
        else {
            var auth = await Authorize(); Assert.That(auth.Media, Is.Null, "Publication transaction awaited across EndsAt; response must suppress media.");
            Assert.That(auth.MediaStatus, Is.EqualTo("unavailable")); Assert.That(auth.Uploads, Has.Length.EqualTo(1));
            Assert.That(auth.Uploads[0].ExpiresAtUtc, Is.GreaterThan(f.Clock.Utc));
        }
        Assert.That(f.Sts.Requests, Has.Count.EqualTo(1));
    }

    [TestCase(false)] [TestCase(true)] public async Task OptInQuotaEdgesAreAtomicAndRetriesRemainIdempotent(bool bytes)
    {
        var f = new Fixture(); var request = OptIn(f); await f.Service.StartAsync(request, default);
        f.Db.ChangeSession(request.SessionId, s => { s.BytesReserved = bytes ? RecordingDiagnosticsLimits.StoredBytesPerSession - 100 : 0;
            s.ObjectsReserved = bytes ? 0 : RecordingDiagnosticsLimits.ObjectsPerSession - 1; });
        var a = await f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(false, Fixture.Object()), default);
        var b = await f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(false, Fixture.Object()), default);
        Assert.That((a.RemainingBytes, a.RemainingObjects), Is.EqualTo((b.RemainingBytes, b.RemainingObjects)));
        Assert.That(bytes ? a.RemainingBytes : a.RemainingObjects, Is.Zero);
        Assert.That((await Error(() => f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(false, Fixture.Object(1)), default), 429)).Code, Is.EqualTo("log_quota"));
    }

    [Test] public async Task OptInRetainsRatePerKindAndConcurrentAdmissionPublicationGuards()
    {
        var f = new Fixture(); var request = OptIn(f); await f.Service.StartAsync(request, default);
        Fixture.Object(4095).Validate(); Assert.That(() => Fixture.Object(4096).Validate(), Throws.TypeOf<IssuerError>());
        for (int n = 0; n < 30; n++) await f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(), default);
        Assert.That((await Error(() => f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(), default), 429)).Code, Is.EqualTo("authorization_rate"));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.Db.BeforeCommit = transaction => {
            if (transaction.TransactItems.Count != 2) return;
            f.Db.BeforeCommit = null; f.Db.ChangeSession(request.SessionId, s => s.AdmissionId = Guid.NewGuid().ToString("N"));
        };
        await Error(() => f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(false, Fixture.Object()), default), 410);
    }

    [Test] public async Task ExactGeneratedFourHourObjectsPassProductionIssuerIncludingFinalization()
    {
        var export = GeneratedDiagnosticsFixture.Load(); await export.VerifyAsync();
        var f = new Fixture(); f.Clock.Utc = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
        var admitted = f.Clock.Utc; var request = OptIn(f) with { SessionId = export.SessionId };
        var start = await f.Service.StartAsync(request, default); AuthorizationResult? last = null;
        for (int n = 0; n < export.Objects.Length; n++) {
            f.Clock.Utc = admitted.AddSeconds(n < export.Objects.Length - 3 ? n * 3 : 14400 + n - (export.Objects.Length - 3));
            // Every descriptor uses the production service, reservation transaction,
            // SDK POST signer and publication transaction; no new business-policy fake.
            last = await f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(false, export.Objects[n].Descriptor), default);
            Assert.That(last.Uploads.Single().Key, Is.EqualTo(export.Objects[n].Key));
        }
        var state = (await f.Store.GetAsync<SessionState>(DbKey.Session(request.SessionId), default))!.Value;
        Assert.That(state.ObjectsReserved, Is.EqualTo(export.Objects.Length)); Assert.That(state.ObjectsReserved, Is.GreaterThan(4096));
        Assert.That(state.BytesReserved, Is.EqualTo(export.Objects.Sum(o => o.CompressedBytes)));
        Assert.That(last!.RemainingObjects, Is.EqualTo(RecordingDiagnosticsLimits.ObjectsPerSession - export.Objects.Length)); Assert.That(last.MetadataUntilUtc, Is.EqualTo(start.EndsAtUtc!.Value.AddSeconds(60)));
        // Persist exact production receipts/state for the subsequent retrieval test.
        var evidence = Environment.GetEnvironmentVariable("SBREC_FOUR_HOUR_EVIDENCE")!; Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence, "issuer-session.json"), JsonSerializer.Serialize(state, Wire.Json));
        var receipts = new List<StoredObject>();
        foreach (var item in export.Objects) receipts.Add((await f.Store.GetAsync<StoredObject>(DbKey.Object(export.SessionId, item.Descriptor), default))!.Value);
        await File.WriteAllTextAsync(Path.Combine(evidence, "issuer-receipts.json"), JsonSerializer.Serialize(receipts, Wire.Json));
        await f.Service.CloseAsync(request.SessionId, request.SessionProof, new(1, Guid.NewGuid(), "normal"), default);
        Assert.That((await f.Store.GetControlAsync(default)).State.Value.Active, Is.Empty);
    }
}
