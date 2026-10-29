using NUnit.Framework;
using Playkitu.Issuer;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.Runtime;

namespace Issuer.Tests;

public sealed class FakeSessionNotifier : ISessionNotifier
{
    public List<SessionNotification> Sent = [];
    public bool Fail;
    public Task SendAsync(SessionNotification notification, CancellationToken ct)
    {
        if (Fail) throw new HttpRequestException("offline email failure");
        lock (Sent) Sent.Add(notification);
        return Task.CompletedTask;
    }
}

[TestFixture]
public sealed class SessionNotificationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static Fixture Create(FakeSessionNotifier? notifier)
    {
        var options = Playkitu.Tests.DeploymentFixture.Create(streamMode: "onDemand");
        var fixture = new Fixture(options, new FakeSessionStreams(options), notifier);
        fixture.Db.ChangeControl(g => { g.SchemaVersion = 2; g.Slots = []; });
        return fixture;
    }
    private static async Task<StreamWork> Work(Fixture f, StartRequest request)
    {
        var session = (await f.Store.GetAsync<SessionState>(DbKey.Session(request.SessionId), Ct))!.Value;
        return (await f.Store.GetAsync<StreamWork>(StreamWork.Key(session.AdmissionId), Ct))!.Value;
    }

    [Test] public async Task NormalCloseQueuesAtomicallyAndDuplicateCloseDoesNotSendAgain()
    {
        var notifier = new FakeSessionNotifier(); var f = Create(notifier);
        var request = f.Start() with { Build = new("test-version", "123", "Tester") };
        await f.Service.StartAsync(request, Ct);
        f.Clock.Advance(TimeSpan.FromSeconds(25));
        await f.Service.AuthorizeAsync(request.SessionId, request.SessionProof, f.Authorize(true), Ct);
        await f.Service.CloseAsync(request.SessionId, request.SessionProof, new(1, Guid.NewGuid(), "game_exit"), Ct);
        Assert.That(notifier.Sent, Is.Empty, "Email must not be sent from the gameplay request.");
        var work = await Work(f, request);
        Assert.That(work.CapacityHeld, Is.False);
        Assert.That(work.Notification!.Reason, Is.EqualTo("game_exit"));
        Assert.That(work.Notification.ApproximateSeconds, Is.EqualTo(25));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await Task.WhenAll(f.Service.CleanupAsync(Ct), f.Service.CleanupAsync(Ct));
        await f.Service.CloseAsync(request.SessionId, request.SessionProof, new(1, Guid.NewGuid(), "failure"), Ct);
        await f.Service.CleanupAsync(Ct);
        Assert.That(notifier.Sent, Has.Count.EqualTo(1));
        Assert.That(notifier.Sent[0].Player, Is.EqualTo("Tester"));
        Assert.That((await Work(f, request)).NotificationSentAt, Is.Not.Null);
    }

    [Test] public async Task LostClientCloseIsReportedAsInterruptedAfterInactivity()
    {
        var notifier = new FakeSessionNotifier(); var f = Create(notifier); var request = f.Start();
        await f.Service.StartAsync(request, Ct);
        f.Clock.Advance(TimeSpan.FromSeconds(IssuerOptions.IdleSeconds + 1));
        await f.Service.CleanupAsync(Ct);
        Assert.That((await Work(f, request)).Notification!.Reason, Is.EqualTo("interrupted"));
        f.Clock.Advance(TimeSpan.FromMinutes(1)); await f.Service.CleanupAsync(Ct);
        Assert.That(notifier.Sent.Single().Subject, Is.EqualTo("Playtest session interrupted"));
    }

    [Test] public async Task DeliveryFailureRetriesWithoutHoldingCapacityOrLosingOutbox()
    {
        var notifier = new FakeSessionNotifier { Fail = true }; var f = Create(notifier); var request = f.Start();
        await f.Service.StartAsync(request, Ct);
        await f.Service.CloseAsync(request.SessionId, request.SessionProof, new(1, Guid.NewGuid(), "normal"), Ct);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<IssuerError>(() => f.Service.CleanupAsync(Ct));
        var failed = await Work(f, request);
        Assert.That(failed.NotificationSentAt, Is.Null); Assert.That(failed.CapacityHeld, Is.False);
        Assert.That(failed.NotificationAttempts, Is.EqualTo(1));
        notifier.Fail = false; f.Clock.Advance(TimeSpan.FromMinutes(6));
        await f.Service.CleanupAsync(Ct);
        Assert.That(notifier.Sent, Has.Count.EqualTo(1));
    }

    [Test] public async Task EnablingAfterAdmissionDoesNotNotifyExistingSessions()
    {
        var f = Create(null); var request = f.Start(); await f.Service.StartAsync(request, Ct);
        var notifier = new FakeSessionNotifier();
        var enabled = new OnDemandSessions(f.Store, f.Options, new FakeSessionStreams(f.Options), f.Clock, notifier);
        f.Clock.Advance(TimeSpan.FromSeconds(IssuerOptions.IdleSeconds + 1));
        await enabled.ReconcileAsync(await Work(f, request), Ct);
        Assert.That((await Work(f, request)).Notification, Is.Null);
        Assert.That(notifier.Sent, Is.Empty);
    }

    [Test] public void NotificationContainsOnlyBoundedDisplayMetadata()
    {
        var notification = new SessionNotification(Guid.NewGuid(), "Tester\nInjected", "build\r\nvalue",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), 60, "normal");
        Assert.That(notification.Message, Does.Contain("Player: TesterInjected\n"));
        Assert.That(notification.Message, Does.Contain("does not verify"));
        var options = Playkitu.Tests.DeploymentFixture.Create(streamMode: "onDemand");
        Assert.Throws<InvalidOperationException>(() => SnsSessionNotifier.ValidateTopic("arn:other", options));
        Assert.DoesNotThrow(() => SnsSessionNotifier.ValidateTopic($"arn:aws:sns:{options.AwsRegion}:{options.AwsAccountId}:{options.Project}-alerts", options));
    }

    private sealed class FakeSns() : AmazonSimpleNotificationServiceClient(new AnonymousAWSCredentials(), new AmazonSimpleNotificationServiceConfig {
        RegionEndpoint = Amazon.RegionEndpoint.USEast2, HttpClientFactory = new BlockedFactory()
    })
    {
        public PublishRequest? Request;
        public override Task<PublishResponse> PublishAsync(PublishRequest request, CancellationToken ct = default)
        { Request = request; return Task.FromResult(new PublishResponse { MessageId = "offline-only" }); }
    }
    [Test] public async Task SnsAdapterPublishesOnlyToConfiguredTopic()
    {
        using var sns = new FakeSns(); var notifier = new SnsSessionNotifier(sns, "offline-topic");
        var notification = new SessionNotification(Guid.NewGuid(), "Tester", "build", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(1), 60, "failure");
        await notifier.SendAsync(notification, Ct);
        Assert.That(sns.Request!.TopicArn, Is.EqualTo("offline-topic"));
        Assert.That(sns.Request.Message, Is.EqualTo(notification.Message));
        Assert.That(sns.Request.Subject, Is.EqualTo("Playtest session interrupted"));
    }
}
