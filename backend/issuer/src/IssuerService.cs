using Amazon.DynamoDBv2.Model;
using System.Diagnostics;

namespace Playkitu.Issuer;

// identity is the AuthenticateUserTicket wire representation. Recording wraps its
// session-bound SDK generic string with str: before verification below.
public interface IValveVerifier { Task<string> VerifyAsync(string ticket, string identity, CancellationToken ct); }
public interface IMediaIssuer { Task<TemporaryMedia> IssueAsync(Guid session, string streamArn, DateTimeOffset endsAt, CancellationToken ct); }
public interface IUploadSigner { Task<UploadGrant> SignAsync(StoredObject item, DateTimeOffset expires, CancellationToken ct); }

public sealed class IssuerService(DynamoIssuerStore store, IValveVerifier valve, IMediaIssuer media, IUploadSigner uploads,
    IssuerOptions options, TimeProvider clock)
{
    private async Task<Control> ReadControlAsync(CancellationToken ct)
    {
        var control = await store.GetControlAsync(ct);
        // An existing CONTROL lease pool is migrated separately; changing a
        // configuration file must not silently remove/reassign another lease.
        if (control.State.Value.Slots.Length != options.StreamArns.Length) throw new IssuerError(503, "slot_configuration_mismatch");
        return control;
    }
    private DateTimeOffset Now => clock.GetUtcNow();
    private static IssuerError Busy() => new(429, "issuer_busy", 2);
    private static StartResult Result(SessionState s) => new(s.SessionId, s.Status, s.Generation, s.EndsAt,
        s.Status == "active" ? (s.Slot >= 0 ? "leased" : "unavailable") : "not_started", s.Status == "pending" ? 2 : null, s.MetadataUntilUtc);

    public async Task<object> ConfigAsync(CancellationToken ct)
    {
        // Reuse this harmless path for a future daily scheduled Lambda invocation.
        // Without traffic/housekeeping, a persistent control item could retain bounded
        // session/tester references after separate session rows have expired by TTL.
        for (var retry = 0; retry < 8; retry++)
        {
            var control = await ReadControlAsync(ct);
            var before = System.Text.Json.JsonSerializer.Serialize(control.State.Value, Wire.Json);
            control.State.Value.Reclaim(Now);
            if (before != System.Text.Json.JsonSerializer.Serialize(control.State.Value, Wire.Json)
                && !await store.CommitAsync([store.WriteControl(control)], ct)) continue;
            return new { appId = options.SteamAppId, consentSchemaVersion = 1, identityPrefix = options.IdentityPrefix,
                admissionUnixSeconds = Now.ToUnixTimeSeconds(), enabled = control.AdmissionsEnabled && control.UploadsEnabled,
                limits = IssuerOptions.Limits() };
        }
        throw Busy();
    }

    public async Task<StartResult> StartAsync(StartRequest request, CancellationToken ct)
    {
        request.Validate(); // Consent/schema rejection occurs before any state or external call.
        SessionState? claim = null;
        for (var retry = 0; retry < 8; retry++)
        {
            var control = await ReadControlAsync(ct); var global = control.State.Value; var now = Now;
            global.Reclaim(now);
            var existing = await store.GetAsync<SessionState>(DbKey.Session(request.SessionId), ct);
            if (existing is not null)
            {
                var s = existing.Value;
                if (!Wire.SameProof(request.SessionProof, s.ProofHash)) throw new IssuerError(401, "invalid_session_proof");
                if (s.BindingHash != request.Binding()) throw new IssuerError(409, "session_binding_conflict");
                // Recover a lost committed response even after Steam ticket cancellation. No Valve retry.
                if (s.Status == "active")
                {
                    if (!global.Active.ContainsKey(s.SessionId)) throw new IssuerError(410, "session_ended");
                    return Result(s);
                }
                if (s.Status != "pending") throw new IssuerError(410, "admission_ended");
                if (s.AdmissionDeadline <= now || (s.Generation >= 3 && s.ReservationUntil <= now))
                {
                    s.Status = "failed"; s.PurgeAt = now.AddDays(1).ToUnixTimeSeconds(); global.Pending.Remove(s.SessionId);
                    if (await store.CommitAsync([store.WriteControl(control), store.Write(DbKey.Session(s.SessionId), s, existing.Version, s.PurgeAt)], ct))
                        throw new IssuerError(410, "admission_exhausted");
                    continue;
                }
                if (s.ReservationUntil > now && global.Pending.TryGetValue(s.SessionId, out var p) && p.Generation == s.Generation) return Result(s);
                if (s.TicketHash == Wire.Hash(request.SteamTicketHex.ToLowerInvariant())) throw new IssuerError(409, "fresh_ticket_required", 1);
            }
            if (!control.AdmissionsEnabled || !control.UploadsEnabled) throw new IssuerError(503, "recording_disabled");
            Wire.Require(Math.Abs((decimal)now.ToUnixTimeSeconds() - request.AdmissionUnixSeconds) <= 300, "fresh_ticket_required");
            if (global.Pending.Count >= 8) throw new IssuerError(429, "admission_capacity", 5);
            var minute = now.ToUnixTimeSeconds() / 60;
            if (global.AdmissionWindow != minute) { global.AdmissionWindow = minute; global.AdmissionAttempts = 0; }
            if (global.AdmissionAttempts >= 12) throw new IssuerError(429, "admission_rate", 60);
            global.AdmissionAttempts++;
            var session = existing?.Value ?? new SessionState { SessionId = request.SessionId, AdmissionId = Guid.NewGuid().ToString("N"), ProofHash = Wire.Hash(request.SessionProof),
                BindingHash = request.Binding(), Consent = request.Consent, Build = request.Build, CreatedAt = now, MetadataFinalizationSeconds = request.MetadataFinalizationSeconds,
                AdmissionDeadline = now.AddSeconds(90), PurgeAt = now.AddDays(1).ToUnixTimeSeconds() };
            session.Generation++; session.ReservationUntil = now.AddSeconds(20);
            session.TicketHash = Wire.Hash(request.SteamTicketHex.ToLowerInvariant());
            global.Pending[session.SessionId] = new(session.Generation, session.ReservationUntil);
            var ticketKey = new DbKey("TICKET#" + session.TicketHash);
            if (await store.GetAsync<TicketReceipt>(ticketKey, ct) is not null) throw new IssuerError(409, "ticket_replayed");
            var writes = new[] { store.WriteControl(control), store.Write(DbKey.Session(session.SessionId), session, existing?.Version, session.PurgeAt),
                store.Write(ticketKey, new TicketReceipt(session.SessionId, session.Generation), null, now.AddDays(1).ToUnixTimeSeconds()) };
            try { if (await store.CommitAsync(writes, ct)) { claim = session; break; } }
            catch (UnknownCommitException) { throw new IssuerError(503, "admission_pending", 2); }
        }
        if (claim is null) throw Busy();
        string steamId;
        // Steam AuthenticateUserTicket parses the remote identity in generic-string
        // wire form: str:{SDK identity}. Controlled real B2s exchanges verified the
        // SAME prrec2 ticket rejects the raw value (105) and accepts this explicit
        // type prefix. Keep the SDK per-session/time/proof identity unchanged; never
        // retry a raw/omitted/shared identity.
        try { steamId = await valve.VerifyAsync(request.SteamTicketHex, "str:" + Playkitu.Client.RecordingIdentity.Identity(request.SessionId, request.AdmissionUnixSeconds, request.SessionProof, options.IdentityPrefix, options.IdentityDomain), ct); }
        catch (IssuerError error)
        {
            await ReleaseClaimAsync(claim, error.Status == 403, ct);
            throw;
        }
        var testerHash = Wire.Hash(steamId);
        for (var retry = 0; retry < 8; retry++)
        {
            var control = await ReadControlAsync(ct); var global = control.State.Value; var now = Now;
            global.Reclaim(now);
            var current = await store.GetAsync<SessionState>(DbKey.Session(claim.SessionId), ct) ?? throw new IssuerError(409, "stale_admission");
            var s = current.Value;
            if (s.Status == "active" && s.Generation == claim.Generation) return Result(s);
            if (s.Status != "pending" || s.Generation != claim.Generation || s.ReservationUntil <= now || s.AdmissionDeadline <= now
                || !global.Pending.TryGetValue(s.SessionId, out var pending) || pending.Generation != s.Generation)
                throw new IssuerError(409, "stale_admission");
            if (!control.AdmissionsEnabled || !control.UploadsEnabled) throw new IssuerError(503, "recording_disabled");
            if (global.Active.Count >= 8 || global.Active.Values.Any(x => x.TesterHash == testerHash)
                || global.DailyAdmissions >= 8 || global.MonthlyAdmissions >= 50
                || global.TesterDailyAdmissions.GetValueOrDefault(testerHash) >= IssuerOptions.MaxTesterAdmissionsPerUtcDay)
            { await ReleaseClaimAsync(claim, true, ct); throw new IssuerError(429, "session_quota", 60); }
            s.Status = "active"; s.AdmittedAt = now; s.EndsAt = now.AddSeconds(IssuerOptions.SessionSeconds);
            s.PurgeAt = s.EndsAt.Value.AddDays(IssuerOptions.RetentionDays).ToUnixTimeSeconds();
            s.Slot = Array.FindIndex(global.Slots, slot => slot.SessionId is null && slot.QuarantineUntil <= now);
            if (s.Slot >= 0)
            {
                s.SlotGeneration = Guid.NewGuid().ToString("N");
                var slot = global.Slots[s.Slot]; slot.SessionId = s.SessionId; slot.Generation = s.SlotGeneration; slot.MaxCredentialExpiry = null;
            }
            global.Pending.Remove(s.SessionId);
            global.Active.Add(s.SessionId, new() { TesterHash = testerHash, LastSeen = now, EndsAt = s.EndsAt.Value, MetadataUntilUtc = s.MetadataUntilUtc });
            global.DailyAdmissions++; global.MonthlyAdmissions++;
            global.TesterDailyAdmissions[testerHash] = global.TesterDailyAdmissions.GetValueOrDefault(testerHash) + 1;
            try
            {
                if (await store.CommitAsync([store.WriteControl(control), store.Write(DbKey.Session(s.SessionId), s, current.Version, s.PurgeAt)], ct)) return Result(s);
            }
            catch (UnknownCommitException)
            {
                var committed = await store.GetAsync<SessionState>(DbKey.Session(s.SessionId), ct);
                if (committed?.Value is { Status: "active" } done && done.Generation == claim.Generation) return Result(done);
                throw new IssuerError(503, "admission_pending", 2);
            }
        }
        throw Busy();
    }

    private async Task ReleaseClaimAsync(SessionState claim, bool terminal, CancellationToken ct)
    {
        for (var retry = 0; retry < 8; retry++)
        {
            var control = await ReadControlAsync(ct); var current = await store.GetAsync<SessionState>(DbKey.Session(claim.SessionId), ct);
            if (current?.Value is not { Status: "pending" } s || s.Generation != claim.Generation) return;
            s.ReservationUntil = Now;
            if (terminal) s.Status = "failed";
            control.State.Value.Pending.Remove(claim.SessionId);
            try { if (await store.CommitAsync([store.WriteControl(control), store.Write(DbKey.Session(s.SessionId), s, current.Version, s.PurgeAt)], ct)) return; }
            catch (UnknownCommitException) { return; } // Application deadline remains the crash recovery fallback.
        }
    }

    private static ActiveReference Authenticate(Control control, SessionState session, string proof, DateTimeOffset now)
    {
        if (!Wire.ValidProof(proof) || !Wire.SameProof(proof, session.ProofHash)) throw new IssuerError(401, "invalid_session_proof");
        if (session.Status != "active" || (session.MetadataUntilUtc ?? session.EndsAt) <= now || !control.State.Value.Active.TryGetValue(session.SessionId, out var active)
            || active.LastSeen.AddSeconds(IssuerOptions.IdleSeconds) <= now) throw new IssuerError(410, "session_ended");
        if (active.EndsAt != session.EndsAt || active.MetadataUntilUtc != session.MetadataUntilUtc) throw new IssuerError(410, "lease_ended");
        if (!control.UploadsEnabled) throw new IssuerError(503, "recording_disabled");
        if (session.Slot >= 0)
        {
            var slot = control.State.Value.Slots[session.Slot];
            if (slot.SessionId != session.SessionId || slot.Generation != session.SlotGeneration) throw new IssuerError(410, "lease_ended");
        }
        return active;
    }

    public async Task<AuthorizationResult> AuthorizeAsync(Guid id, string proof, AuthorizeRequest request, CancellationToken ct, TimeSpan? requestBudget = null)
    {
        var elapsed = Stopwatch.StartNew();
        request.Validate(); SessionState? reserved = null; StoredObject[] objects = []; var issueMedia = false;
        for (var retry = 0; retry < 8; retry++)
        {
            var control = await ReadControlAsync(ct); control.State.Value.Reclaim(Now);
            var current = await store.GetAsync<SessionState>(DbKey.Session(id), ct) ?? throw new IssuerError(401, "invalid_session_proof");
            var s = current.Value; var now = Now; var active = Authenticate(control, s, proof, now); var global = control.State.Value;
            var minute = now.ToUnixTimeSeconds() / 60;
            if (active.RequestWindow != minute) { active.RequestWindow = minute; active.Requests = 0; }
            if (global.AuthorizeWindow != minute) { global.AuthorizeWindow = minute; global.AuthorizeRequests = 0; }
            if (active.Requests >= 30 || global.AuthorizeRequests >= 120) throw new IssuerError(429, "authorization_rate", 60);
            active.Requests++; global.AuthorizeRequests++; active.LastSeen = now;
            var attemptMedia = request.MediaCredentialsNeeded && s.Slot >= 0 && now < s.EndsAt
                && (s.LastMediaAttempt is null || s.LastMediaAttempt.Value.AddSeconds(60) <= now);
            if (attemptMedia) s.LastMediaAttempt = now;
            var writes = new List<Put>(); var reservedObjects = new List<StoredObject>();
            foreach (var descriptor in request.Objects)
            {
                var key = DbKey.Object(id, descriptor); var existing = await store.GetAsync<StoredObject>(key, ct);
                if (existing is not null && existing.Value.AdmissionId == s.AdmissionId)
                {
                    if (existing.Value.Descriptor != descriptor) throw new IssuerError(409, "object_conflict");
                    reservedObjects.Add(existing.Value); continue;
                }
                s.BytesReserved = checked(s.BytesReserved + descriptor.CompressedBytes); s.ObjectsReserved++;
                if (s.BytesReserved > s.DiagnosticByteLimit || s.ObjectsReserved > s.DiagnosticObjectLimit) throw new IssuerError(429, "log_quota");
                // DynamoDB TTL can delete session metadata before object receipts. A new
                // admission of the same UUID must reserve new volume and replace the old
                // receipt conditionally, never reuse its old key or uncharged allowance.
                var item = new StoredObject(descriptor, $"{s.Prefix}{descriptor.Kind}/{descriptor.Sequence:D6}.gz", s.PurgeAt, s.AdmissionId);
                reservedObjects.Add(item); writes.Add(store.Write(key, item, existing?.Version, s.PurgeAt));
            }
            writes.Add(store.WriteControl(control)); writes.Add(store.Write(DbKey.Session(id), s, current.Version, s.PurgeAt));
            try
            {
                if (await store.CommitAsync(writes, ct)) { reserved = s; objects = reservedObjects.ToArray(); issueMedia = attemptMedia; break; }
            }
            catch (UnknownCommitException) { throw new IssuerError(503, "authorization_pending", 2); }
        }
        if (reserved is null) throw Busy();
        // Optional STS and S3 credential resolution run concurrently, with one bounded
        // two-second window for the entire object batch. Reserve two seconds of the
        // caller's remaining budget for strong reads/final publication; a stalled path
        // cannot consume the healthy path's window or publish secrets after cancellation.
        var optionalBudget = (requestBudget ?? TimeSpan.FromSeconds(8)) - elapsed.Elapsed - TimeSpan.FromSeconds(2);
        using var optional = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (optionalBudget <= TimeSpan.Zero) optional.Cancel();
        else optional.CancelAfter(optionalBudget < TimeSpan.FromSeconds(2) ? optionalBudget : TimeSpan.FromSeconds(2));
        var mediaTask = IssueMediaAsync(); var uploadsTask = SignUploadsAsync();
        await Task.WhenAll(mediaTask, uploadsTask);
        var (credentials, mediaStatus) = await mediaTask;
        var (grants, logStatus) = await uploadsTask;
        ct.ThrowIfCancellationRequested();

        async Task<(TemporaryMedia?, string)> IssueMediaAsync()
        {
            var status = !request.MediaCredentialsNeeded ? "not_requested" : reserved.Slot < 0 || Now >= reserved.EndsAt ? "unavailable" : "retry_later";
            // The reservation transaction awaited after deciding issueMedia.
            // Recheck the immutable media ceiling before starting external STS,
            // including when that commit crossed into metadata-only grace.
            if (!issueMedia || Now >= reserved.EndsAt) return (null, status);
            try
            {
                optional.Token.ThrowIfCancellationRequested();
                var credentials = await media.IssueAsync(id, options.StreamArns[reserved.Slot], reserved.EndsAt!.Value, optional.Token).WaitAsync(optional.Token);
                if (credentials.StreamArn != options.StreamArns[reserved.Slot] || credentials.ExpiresAtUtc <= Now || credentials.ExpiresAtUtc > Now.AddMinutes(16)
                    || string.IsNullOrEmpty(credentials.AccessKeyId) || string.IsNullOrEmpty(credentials.SecretAccessKey) || string.IsNullOrEmpty(credentials.SessionToken))
                    throw new IssuerError(503, "media_unavailable");
                return (credentials, "authorized");
            }
            catch (IssuerError) { return (null, "unavailable"); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, "unavailable"); }
        }
        async Task<(UploadGrant[], string)> SignUploadsAsync()
        {
            var signed = new List<UploadGrant>();
            try
            {
                foreach (var item in objects)
                {
                    optional.Token.ThrowIfCancellationRequested();
                    signed.Add(await uploads.SignAsync(item, Min(Now.AddMinutes(2), (reserved.MetadataUntilUtc ?? reserved.EndsAt)!.Value), optional.Token).WaitAsync(optional.Token));
                }
                return (signed.ToArray(), "authorized");
            }
            catch (IssuerError) { return ([], "unavailable"); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ([], "unavailable"); }
        }
        // Linearize publication after external work: a concurrent close, kill, lease reuse or
        // inactivity timeout denies this response. Credentials are never returned before this
        // transaction durably advances the stream's maximum credential-expiry quarantine.
        for (var retry = 0; retry < 8; retry++)
        {
            var control = await ReadControlAsync(ct); control.State.Value.Reclaim(Now);
            var current = await store.GetAsync<SessionState>(DbKey.Session(id), ct) ?? throw new IssuerError(410, "session_ended");
            var s = current.Value; Authenticate(control, s, proof, Now);
            if (s.AdmissionId != reserved.AdmissionId || s.SlotGeneration != reserved.SlotGeneration) throw new IssuerError(410, "lease_ended");
            // An STS request may straddle capture expiry. Never publish a fresh
            // media credential during metadata-only finalization; its inline
            // policy still uses the original four-hour EndsAt in every case.
            if (Now >= s.EndsAt && credentials is not null) { credentials = null; mediaStatus = "unavailable"; }
            if (credentials is not null)
            {
                var slot = control.State.Value.Slots[s.Slot];
                slot.MaxCredentialExpiry = slot.MaxCredentialExpiry is { } previous ? Max(previous, credentials.ExpiresAtUtc) : credentials.ExpiresAtUtc;
                s.LastMediaIssued = Now;
            }
            try
            {
                if (await store.CommitAsync([store.WriteControl(control), store.Write(DbKey.Session(id), s, current.Version, s.PurgeAt)], ct))
                {
                    // Publication itself is awaited: its commit can cross either
                    // deadline after Authenticate/media suppression above. Quota
                    // reservations stay durable, but expired authorizations must
                    // not escape in a response. Valid metadata survives media end.
                    if (Now >= (s.MetadataUntilUtc ?? s.EndsAt)) throw new IssuerError(410, "session_ended");
                    if (request.MediaCredentialsNeeded && Now >= s.EndsAt) { credentials = null; mediaStatus = "unavailable"; }
                    return new(id, s.EndsAt!.Value, mediaStatus, credentials, logStatus, grants, s.DiagnosticByteLimit - s.BytesReserved, s.DiagnosticObjectLimit - s.ObjectsReserved, s.MetadataUntilUtc);
                }
            }
            catch (UnknownCommitException) { throw new IssuerError(503, "authorization_pending", 2); }
        }
        throw Busy();
    }

    public async Task<object> CloseAsync(Guid id, string proof, CloseRequest request, CancellationToken ct)
    {
        request.Validate();
        for (var retry = 0; retry < 8; retry++)
        {
            var control = await ReadControlAsync(ct); var current = await store.GetAsync<SessionState>(DbKey.Session(id), ct);
            if (current is null || !Wire.ValidProof(proof) || !Wire.SameProof(proof, current.Value.ProofHash)) throw new IssuerError(401, "invalid_session_proof");
            var s = current.Value;
            if (s.Status == "closed") return new { sessionId = id, state = "closed" };
            s.Status = "closed"; control.State.Value.Pending.Remove(id); control.State.Value.End(id, Now);
            try
            {
                if (await store.CommitAsync([store.WriteControl(control), store.Write(DbKey.Session(id), s, current.Version, s.PurgeAt)], ct))
                    return new { sessionId = id, state = "closed" };
            }
            catch (UnknownCommitException) { throw new IssuerError(503, "close_pending", 2); }
        }
        throw Busy();
    }
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    public sealed record TicketReceipt(Guid SessionId, int Generation);
}
