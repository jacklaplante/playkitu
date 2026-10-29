using Amazon.DynamoDBv2.Model;

namespace Playkitu.Issuer;

public interface ISessionStreams
{
    // Null means the exact owned stream exists but is not ACTIVE yet. Retrying
    // resolves the same immutable admission name, never another allocation.
    Task<string?> EnsureAsync(string admissionId, CancellationToken ct);
    Task DeleteAsync(string admissionId, string? expectedArn, CancellationToken ct);
}

// Independent admission/lease/lifecycle records replace the global slot and
// active-session maps. Exact counters serialize admissions/retirement, never
// heartbeats. Counts remain available when caps are disabled, so enabling a cap
// does not silently forget admissions already made in the current period.
public sealed class OnDemandSessions(DynamoIssuerStore store, IssuerOptions options, ISessionStreams streams, TimeProvider clock, ISessionNotifier? notifier = null)
{
    private DateTimeOffset Now => clock.GetUtcNow();
    private static DbKey Tester(string hash) => new("TESTER#" + hash);
    private static readonly DbKey ActiveCount = new("USAGE#ACTIVE");
    private DateTimeOffset LeaseUntil(SessionState s) => Min(Now.AddSeconds(IssuerOptions.IdleSeconds), (s.MetadataUntilUtc ?? s.EndsAt)!.Value);
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private async Task CountAsync(DbKey key, int? limit, string error, List<Put> writes, CancellationToken ct, long? purge = null)
    {
        var row = await store.GetAsync<UsageCounter>(key, ct);
        var value = row?.Value ?? new();
        if (value.Count < 0) throw new IssuerError(503, "invalid_usage_state");
        if (limit is { } cap && value.Count >= cap) throw new IssuerError(429, error, 60);
        value.Count = checked(value.Count + 1);
        writes.Add(store.Write(key, value, row?.Version, purge));
    }
    public Task AttemptAsync(List<Put> writes, CancellationToken ct) => CountAsync(
        new("RATE#ADMISSION#" + Now.ToUnixTimeSeconds() / 60), options.RecordingLimits.AdmissionAttemptsPerMinute,
        "admission_rate", writes, ct, Now.AddMinutes(2).ToUnixTimeSeconds());

    public async Task ReserveAsync(SessionState s, string testerHash, List<Put> writes, CancellationToken ct)
    {
        var now = Now;
        var lease = await store.GetAsync<TesterLease>(Tester(testerHash), ct);
        if (lease is not null && lease.Value.Until > now) throw new IssuerError(429, "tester_session_active", 5);
        var limits = options.RecordingLimits;
        await CountAsync(ActiveCount, limits.ConcurrentSessions, "concurrent_session_limit", writes, ct);
        var purge = now.AddDays(32).ToUnixTimeSeconds();
        var day = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var month = now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        await CountAsync(new("USAGE#DAY#" + day), limits.AdmissionsPerDay, "daily_admission_limit", writes, ct, purge);
        await CountAsync(new("USAGE#MONTH#" + month), limits.AdmissionsPerMonth, "monthly_admission_limit", writes, ct, purge);
        await CountAsync(new("USAGE#TESTER#" + testerHash + "#" + day), limits.TesterAdmissionsPerDay, "tester_daily_admission_limit", writes, ct, purge);
        s.OnDemand = true; s.TesterHash = testerHash; s.Status = "preparing";
        s.NotifyOnEnd = notifier is not null;
        s.AdmittedAt = now; s.EndsAt = now.AddSeconds(IssuerOptions.SessionSeconds); s.LastSeen = now;
        // Keep metadata until after the last possible retained media. The durable
        // stream work record itself has no TTL until deletion is acknowledged.
        s.PurgeAt = s.EndsAt.Value.AddDays(IssuerOptions.RetentionDays).AddHours(1).ToUnixTimeSeconds();
        writes.Add(store.Write(Tester(testerHash), new TesterLease { SessionId = s.SessionId, AdmissionId = s.AdmissionId,
            Until = Min(s.AdmissionDeadline, LeaseUntil(s)) }, lease?.Version, s.PurgeAt));
        writes.Add(store.Write(StreamWork.Key(s.AdmissionId), new StreamWork { SessionId = s.SessionId, AdmissionId = s.AdmissionId,
            TesterHash = testerHash, Due = Min(s.AdmissionDeadline, LeaseUntil(s)),
            RetireAfter = s.EndsAt.Value.AddDays(IssuerOptions.RetentionDays).AddHours(1), CapacityHeld = true }, null));
    }

    public async Task<StartResult> PrepareAsync(SessionState expected, CancellationToken ct)
    {
        // The work ledger was committed before any AWS creation. Close/crash/kill
        // during Ensure cannot strand an untracked stream or publish credentials.
        string? arn = null; var ensured = false;
        for (var retry = 0; retry < 8; retry++) {
            var control = await store.GetControlAsync(ct);
            if (control.State.Value.SchemaVersion != 2 || !control.AdmissionsEnabled || !control.UploadsEnabled) throw new IssuerError(503, "recording_disabled");
            var current = await store.GetAsync<SessionState>(DbKey.Session(expected.SessionId), ct) ?? throw new IssuerError(410, "admission_ended");
            var s = current.Value;
            if (!s.OnDemand || s.AdmissionId != expected.AdmissionId || s.ProofHash != expected.ProofHash) throw new IssuerError(410, "admission_ended");
            if (s.Status == "active") { Authenticate(s, Now); return Result(s); }
            if (s.Status != "preparing" || s.AdmissionDeadline <= Now) throw new IssuerError(410, "admission_exhausted");
            if (!ensured) {
                // Check the current switches/session before provisioning, then
                // read them again afterwards: a close or maintenance switch can
                // race the external AWS operation. No ready state escapes it.
                arn = await streams.EnsureAsync(expected.AdmissionId, ct).WaitAsync(ct);
                if (arn is not null && !options.IsSessionStream(arn, expected.AdmissionId)) throw new IssuerError(503, "stream_binding_mismatch");
                ensured = true; continue;
            }
            if (arn is null) return Result(s);
            var writes = new List<Put>();
            s.StreamArn = arn; s.Status = "active";
            await HeartbeatAsync(s, writes, ct);
            writes.Add(store.Write(DbKey.Session(s.SessionId), s, current.Version, s.PurgeAt));
            if (await store.CommitAsync(writes, ct, control)) return Result(s);
        }
        throw new IssuerError(429, "issuer_busy", 2);
    }
    // Existing protocol-v1 clients require null deadlines in a pending response.
    // Capacity/deadlines are already frozen in storage while AWS prepares the
    // stream, but publish them only with active/leased admission, never early.
    public static StartResult Result(SessionState s) => new(s.SessionId, s.Status == "preparing" ? "pending" : s.Status,
        s.Generation, s.Status == "preparing" ? null : s.EndsAt, s.Status == "active" ? "leased" : "not_started",
        s.Status is "pending" or "preparing" ? 2 : null, s.Status == "preparing" ? null : s.MetadataUntilUtc);

    public static ActiveReference Authenticate(SessionState s, DateTimeOffset now)
    {
        if (!s.OnDemand || s.Status != "active" || (s.MetadataUntilUtc ?? s.EndsAt) <= now
            || s.LastSeen.AddSeconds(IssuerOptions.IdleSeconds) <= now || s.StreamArn is null)
            throw new IssuerError(410, "session_ended");
        return new() { TesterHash = s.TesterHash, EndsAt = s.EndsAt!.Value, MetadataUntilUtc = s.MetadataUntilUtc,
            LastSeen = s.LastSeen, RequestWindow = s.RequestWindow, Requests = s.Requests };
    }
    public async Task HeartbeatAsync(SessionState s, List<Put> writes, CancellationToken ct)
    {
        var lease = await store.GetAsync<TesterLease>(Tester(s.TesterHash), ct);
        var work = await store.GetAsync<StreamWork>(StreamWork.Key(s.AdmissionId), ct);
        if (lease is null || lease.Value.AdmissionId != s.AdmissionId || lease.Value.SessionId != s.SessionId || lease.Value.Until <= Now
            || work is null || work.Value.Closed || work.Value.Deleted) throw new IssuerError(410, "lease_ended");
        s.LastSeen = Now; lease.Value.Until = LeaseUntil(s); work.Value.Due = lease.Value.Until;
        work.Value.StreamArn = s.StreamArn;
        writes.Add(store.Write(Tester(s.TesterHash), lease.Value, lease.Version, s.PurgeAt));
        writes.Add(store.Write(StreamWork.Key(s.AdmissionId), work.Value, work.Version));
    }
    public async Task CloseAsync(StreamWork work, long version, List<Put> writes, CancellationToken ct, SessionState? ended = null)
    {
        var lease = await store.GetAsync<TesterLease>(Tester(work.TesterHash), ct);
        // An expired tester lease may already belong to a newer admission. Never
        // clear that lease while retiring the old session's capacity or stream.
        if (lease is not null && lease.Value.AdmissionId == work.AdmissionId) {
            lease.Value.Until = DateTimeOffset.UnixEpoch;
            writes.Add(store.Write(Tester(work.TesterHash), lease.Value, lease.Version, work.RetireAfter.ToUnixTimeSeconds()));
        }
        if (work.CapacityHeld) {
            var count = await store.GetAsync<UsageCounter>(ActiveCount, ct);
            if (count is null || count.Value.Count <= 0) throw new IssuerError(503, "invalid_usage_state");
            count.Value.Count--; work.CapacityHeld = false;
            writes.Add(store.Write(ActiveCount, count.Value, count.Version));
        }
        work.Closed = true; work.Due = work.RetireAfter;
        // Stream-ready sessions can still lack footage. Email describes a session
        // ending, never promises that all video/audio/log uploads succeeded.
        if (ended is { NotifyOnEnd: true, StreamArn: not null, AdmittedAt: not null }) {
            work.Notification = SessionNotification.From(ended);
            work.Due = Now.AddMinutes(1);
        }
        writes.Add(store.Write(StreamWork.Key(work.AdmissionId), work, version));
    }

    public async Task ReconcileAsync(StreamWork candidate, CancellationToken ct)
    {
        for (var retry = 0; retry < 8; retry++) {
            var row = await store.GetAsync<StreamWork>(StreamWork.Key(candidate.AdmissionId), ct);
            if (row is null || row.Value.Deleted || row.Value.Due > Now) return;
            var work = row.Value;
            if (!work.Closed) {
                var current = await store.GetAsync<SessionState>(DbKey.Session(work.SessionId), ct);
                var writes = new List<Put>();
                SessionState? ended = null;
                if (current?.Value is { } s && s.AdmissionId == work.AdmissionId) {
                    if (s.Status == "active" && s.LastSeen.AddSeconds(IssuerOptions.IdleSeconds) > Now && (s.MetadataUntilUtc ?? s.EndsAt) > Now)
                        throw new IssuerError(503, "cleanup_due_mismatch");
                    if (s.Status == "preparing" && s.AdmissionDeadline > Now) return;
                    s.EndReason = "interrupted"; s.EndedAt = Now; s.Status = "closed";
                    ended = s;
                    writes.Add(store.Write(DbKey.Session(s.SessionId), s, current.Version, s.PurgeAt));
                }
                await CloseAsync(work, row.Version, writes, ct, ended);
                if (await store.CommitAsync(writes, ct)) return;
                continue;
            }
            if (work.Notification is not null && work.NotificationSentAt is null && work.RetireAfter > Now) {
                // Claim before calling SNS. Concurrent cleanup invocations skip
                // this lease; a crash/unknown publish retries after the lease.
                // SNS standard email is at-least-once: rare duplicates are possible.
                work.NotificationAttempts++;
                work.Due = Now.AddMinutes(Math.Min(60, 1 << Math.Min(6, work.NotificationAttempts)));
                if (!await store.CommitAsync([store.Write(StreamWork.Key(work.AdmissionId), work, row.Version)], ct)) continue;
                if (notifier is null) throw new IssuerError(503, "notification_unavailable");
                try { await notifier.SendAsync(work.Notification, ct); }
                catch (OperationCanceledException) { throw; }
                catch { throw new IssuerError(503, "notification_unavailable"); }
                var claimed = await store.GetAsync<StreamWork>(StreamWork.Key(work.AdmissionId), ct);
                if (claimed is not null && claimed.Value.NotificationSentAt is null) {
                    claimed.Value.NotificationSentAt = Now; claimed.Value.Due = claimed.Value.RetireAfter;
                    if (!await store.CommitAsync([store.Write(StreamWork.Key(work.AdmissionId), claimed.Value, claimed.Version)], ct))
                        throw new IssuerError(503, "notification_ack_pending");
                }
                return;
            }
            if (work.RetireAfter > Now) return;
            // Admission names are never reused. The adapter validates ownership
            // and version before deleting; unknown results safely retry this name.
            await streams.DeleteAsync(work.AdmissionId, work.StreamArn, ct);
            work.Deleted = true;
            if (await store.CommitAsync([store.Write(StreamWork.Key(work.AdmissionId), work, row.Version, Now.AddDays(7).ToUnixTimeSeconds())], ct)) return;
        }
        throw new IssuerError(429, "cleanup_busy", 2);
    }

    public async Task DeferAsync(StreamWork candidate, CancellationToken ct)
    {
        var current = await store.GetAsync<StreamWork>(StreamWork.Key(candidate.AdmissionId), ct);
        if (current is null || current.Value.Deleted || current.Value.Due > Now) return;
        // A poison item must not monopolize the first page of its work shard.
        // This changes retry scheduling only, never retention or ownership.
        current.Value.Due = Now.AddMinutes(5);
        await store.CommitAsync([store.Write(StreamWork.Key(candidate.AdmissionId), current.Value, current.Version)], ct);
    }
}
