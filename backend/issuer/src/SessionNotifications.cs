using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using System.Globalization;

namespace Playkitu.Issuer;

public interface ISessionNotifier
{
    Task SendAsync(SessionNotification notification, CancellationToken ct);
}

public sealed record SessionNotification(Guid SessionId, string Player, string Build, DateTimeOffset StartedAt,
    DateTimeOffset EndedAt, long ApproximateSeconds, string Reason)
{
    public static SessionNotification From(SessionState s) => new(s.SessionId,
        s.Build.PlayerDisplayName ?? "Unknown player", s.Build.GameVersion, s.AdmittedAt!.Value,
        s.EndedAt!.Value, Math.Max(0, (long)(s.LastSeen - s.AdmittedAt.Value).TotalSeconds), s.EndReason!);

    public string Subject => Reason is "interrupted" or "failure" ? "Playtest session interrupted" : "Playtest session ended";
    public string Message => string.Join("\n", new[] {
        Subject,
        $"Player: {SingleLine(Player)}",
        $"Approximate duration: {ApproximateSeconds / 60}m {ApproximateSeconds % 60}s",
        $"Build: {SingleLine(Build)}",
        $"Session ID: {SessionId:D}",
        $"Started (UTC): {StartedAt.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)}",
        $"End detected (UTC): {EndedAt.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)}",
        $"Ending: {Reason switch { "normal" => "Client ended recording", "game_exit" => "Client exited game", "withdrawn" => "Player ended consent", "failure" => "Client reported a recording failure", _ => "Interrupted or disconnected; no client close received" }}",
        "",
        "Open Playtest Recordings on your computer and find this session ID.",
        "This alert does not verify that all video, audio or logs arrived. Duration is approximate.",
        "Recordings and logs remain private in the library."
    });

    // Names/build labels are untrusted display metadata. Keep them on one line
    // and bounded; never include proofs, credentials, media or diagnostic contents.
    private static string SingleLine(string value) => new(value.Where(c => !char.IsControl(c)).Take(160).ToArray());
}

public sealed class SnsSessionNotifier(IAmazonSimpleNotificationService sns, string topicArn) : ISessionNotifier
{
    public Task SendAsync(SessionNotification notification, CancellationToken ct) => sns.PublishAsync(new PublishRequest {
        TopicArn = topicArn, Subject = notification.Subject, Message = notification.Message
    }, ct);

    public static void ValidateTopic(string topicArn, IssuerOptions options)
    {
        if (!options.OnDemand || topicArn != $"arn:aws:sns:{options.AwsRegion}:{options.AwsAccountId}:{options.Project}-alerts")
            throw new InvalidOperationException("Notification topic must be the existing deployment alert topic in on-demand mode.");
    }
}
