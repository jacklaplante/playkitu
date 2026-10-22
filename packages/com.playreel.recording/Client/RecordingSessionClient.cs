using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Playreel.Client
{
    public enum RecordingSessionState { Idle, Admitting, Active, Ending, Ended, Unavailable }

    // Explicitly owned and main-thread driven: construction has no I/O or bootstrap.
    // The future consent/Play owner calls Start then Tick; gameplay never awaits it.
    public sealed class RecordingSessionClient : IDisposable
    {
        public RecordingTrust Trust { get; }
        private readonly IRecordingTransport transport;
        private readonly IRecordingTickets tickets;
        private readonly IRecordingClock clock;
        private readonly Func<double> jitter;
        private readonly List<RecordingObject> queued = new List<RecordingObject>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private IEnumerator worker, ticketFlow;
        private IRecordingHttpOperation http;
        private RecordingHttpResponse reply;
        private RecordingBuild build;
        private string proof, streamArn, closeReason;
        private bool microphone, admitted, ticking, closeStarted, disposed, logQuotaEnded;
        private DateTimeOffset consentUtc, endsAt;
        private DateTimeOffset? metadataUntil;
        private int metadataFinalizationSeconds;
        private double metadataEnd;
        private double lastTick, admissionEnd, httpEnd, retryAt, heartbeatAt, refreshAt, mediaAttemptAt, mediaExpiresAt, sessionEnd, serverObservedAt;
        private DateTimeOffset serverUtc;
        private int failures, objectFailures;
        private RecordingAuthorization publication;
        private double httpBeganAt, lastHttpDuration;
        public RecordingSessionState State { get; private set; }
        public Guid SessionId { get; private set; }
        public int AdmissionGeneration { get; private set; }
        public DateTimeOffset EndsAtUtc => endsAt;
        public DateTimeOffset? MetadataUntilUtc => metadataUntil;
        public bool CaptureDeadlineReached => admitted && clock.Seconds >= sessionEnd;
        // Admission diagnostics count local calls, not confirmed wire requests.
        // Only bounded numeric status/finite categories survive; never retain
        // response bodies, tickets, proofs or request/identity metadata here.
        public int TicketAttempts { get; private set; }
        public int AdmissionAttempts { get; private set; }
        public int? LastAdmissionHttpStatus { get; private set; }
        public string LastAdmissionResponseCode { get; private set; }
        public string AdmissionExhaustionReason { get; private set; }
        public string LastSteamAuthFailure { get; private set; }
        public int? LastValveErrorCode { get; private set; }
        public IReadOnlyList<string> LastValveErrorTerms { get; private set; }
        public string LastPublisherKeyShape { get; private set; }
        // Protocol vocabulary/order is validated independently of the backend;
        // never copy unmatched description text or partially accept an array.
        private static readonly string[] ValveErrorTermOrder = {
            "identity", "appid", "app", "ticket", "key", "expired", "canceled", "cancelled", "mismatch", "invalid" };
        public string LastError { get; private set; }
        public string MediaStatus { get; private set; } = "not_started";
        public string LogStatus { get; private set; } = "not_started";
        public int QueuedObjects => queued.Count;
        public int DroppedObjects { get; private set; }
        public event Action<RecordingAuthorization> Authorized;
        public RecordingSessionClient(RecordingTrust trust, IRecordingTransport transport, IRecordingTickets tickets, IRecordingClock clock = null, Func<double> jitter = null)
        {
            Trust = trust ?? throw new ArgumentNullException(nameof(trust));
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
            this.clock = clock ?? new RecordingClock();
            var random = new Random(); this.jitter = jitter ?? (() => random.NextDouble());
            lastTick = this.clock.Seconds;
        }
        public override string ToString() => "Recording session client (sensitive values omitted)";
        public void Start(bool mandatoryConsent, RecordingBuild frozenBuild, bool microphoneConsent = false)
        {
            if (State != RecordingSessionState.Idle || disposed) return;
            if (!mandatoryConsent) { State = RecordingSessionState.Unavailable; LastError = "consent_required"; return; }
            if (frozenBuild == null) { State = RecordingSessionState.Unavailable; LastError = "invalid_build"; return; }
            try { SessionId = Guid.NewGuid(); proof = RecordingIdentity.NewProof(); }
            catch { StopUnavailable("credential_unavailable"); return; }
            build = frozenBuild;
            microphone = microphoneConsent; consentUtc = clock.UtcNow; admissionEnd = clock.Seconds + 90;
            State = RecordingSessionState.Admitting; worker = Admit();
        }
        public bool QueueObjects(params RecordingObject[] objects)
        {
            if (State != RecordingSessionState.Active || logQuotaEnded || objects == null || objects.Length > 16 || objects.Any(o => o == null)) return false;
            var result = new List<RecordingObject>(queued);
            foreach (var item in objects)
            {
                var existing = result.FirstOrDefault(o => o.Kind == item.Kind && o.Sequence == item.Sequence);
                if (existing != null) { if (!existing.Same(item)) return false; }
                else result.Add(item);
            }
            if (result.Count > 16) return false;
            queued.Clear(); queued.AddRange(result); return true;
        }
        public void WithdrawObjects(params RecordingObject[] objects)
        {
            // Cancel only this owner's queued descriptors. An already in-flight
            // authorization also carries heartbeat/media work and finishes normally;
            // an unsubscribed/stopped uploader ignores those late grants.
            if (objects == null || objects.Length > 16) return;
            queued.RemoveAll(item => objects.Any(other => item.Same(other)));
        }
        public void Tick()
        {
            if (disposed || State == RecordingSessionState.Ended || State == RecordingSessionState.Unavailable || State == RecordingSessionState.Idle) return;
            double now = clock.Seconds;
            if (double.IsNaN(now) || double.IsInfinity(now) || now < lastTick) { StopUnavailable("invalid_clock"); return; }
            lastTick = now;
            if (State == RecordingSessionState.Ending) { TickClose(); return; }
            if (State == RecordingSessionState.Admitting && now >= admissionEnd)
            { AdmissionExhaustionReason = "deadline"; StopUnavailable("admission_exhausted"); return; }
            // E34: capture has the original absolute four-hour ceiling. A negotiated
            // grace keeps ONLY metadata authorization alive; late ticks beyond its
            // hard deadline terminate before starting HTTP work or publishing leases.
            if (State == RecordingSessionState.Active && now >= metadataEnd) { End("normal"); return; }
            if (State == RecordingSessionState.Active && CaptureDeadlineReached) { MediaStatus = "unavailable"; mediaExpiresAt = 0; }
            if (State == RecordingSessionState.Active && mediaExpiresAt > 0 && now >= mediaExpiresAt)
            { MediaStatus = "unavailable"; mediaExpiresAt = 0; }
            if (worker == null && State == RecordingSessionState.Active && now >= retryAt
                && (queued.Count > 0 || now >= heartbeatAt || (now >= refreshAt && now >= mediaAttemptAt))) worker = Authorize();
            ticking = true;
            try
            {
                if (worker != null && !worker.MoveNext()) { Release(worker as IDisposable); worker = null; }
            }
            catch { if (State == RecordingSessionState.Admitting) StopUnavailable("admission_unavailable"); else { LastError = "authorization_unavailable"; End("failure"); } }
            finally
            {
                ticking = false;
                if (State == RecordingSessionState.Ending || State == RecordingSessionState.Unavailable || State == RecordingSessionState.Ended) DisposeWork();
            }
            // Publish only after coroutine execution/disposal. A consumer may reenter
            // End/Dispose; no executing iterator or subsequent credentials survive it.
            var ready = publication; publication = null;
            if (ready != null && State == RecordingSessionState.Active && Authorized != null)
                foreach (Action<RecordingAuthorization> consumer in Authorized.GetInvocationList())
                {
                    if (State != RecordingSessionState.Active) break;
                    try { consumer(ready); } catch { LastError = "consumer_failed"; }
                }
        }
        private IEnumerator Admit()
        {
            for (int generation = 0; generation < 3 && State == RecordingSessionState.Admitting; generation++)
            {
                JObject config = null;
                for (int attempt = 0; attempt < 3 && config == null; attempt++)
                {
                    BeginHttp(new RecordingHttpRequest("GET", "/v1/config"));
                    while (!HttpDone()) yield return null;
                    if (reply.Status == 200) config = RecordingProtocol.Read(reply);
                    else { double until = clock.Seconds + Backoff(attempt, reply); while (clock.Seconds < until) yield return null; }
                }
                if (config == null) { StopUnavailable("issuer_unavailable"); yield break; }
                ObserveServer(config);
                if (!RecordingProtocol.Boolean(config, "enabled")) { StopUnavailable("recording_disabled"); yield break; }
                RecordingProtocol.Integer(config, "appId", Trust.SteamAppId, Trust.SteamAppId);
                RecordingProtocol.Integer(config, "consentSchemaVersion", 1, 1);
                if (RecordingProtocol.Text(config, "identityPrefix", 16) != Trust.IdentityPrefix) throw new RecordingFailure("invalid_response");
                long seconds = RecordingProtocol.Integer(config, "admissionUnixSeconds", 1, long.MaxValue);
                if (Math.Abs((decimal)seconds - serverUtc.ToUnixTimeSeconds()) > 300 || !(config["limits"] is JObject limits)) throw new RecordingFailure("invalid_response");
                // These protocol-v1 constraints determine local scheduling/queue bounds.
                // Reject an incompatible config rather than silently running unsafe timers.
                RecordingProtocol.Integer(limits, "heartbeatSeconds", 60, 60);
                RecordingProtocol.Integer(limits, "sessionSeconds", RecordingDiagnosticsLimits.SessionSeconds, RecordingDiagnosticsLimits.SessionSeconds);
                metadataFinalizationSeconds = limits["metadataFinalizationSeconds"] == null ? 0
                    : (int)RecordingProtocol.Integer(limits, "metadataFinalizationSeconds", 0, RecordingDiagnosticsLimits.MetadataFinalizationSeconds);
                if (metadataFinalizationSeconds != 0 && metadataFinalizationSeconds != RecordingDiagnosticsLimits.MetadataFinalizationSeconds) throw new RecordingFailure("invalid_response");
                RecordingProtocol.Integer(limits, "admissionDeadlineSeconds", 90, 90);
                RecordingProtocol.Integer(limits, "maxAdmissionGenerations", 3, 3);
                RecordingProtocol.Integer(limits, "maxObjectsPerRequest", 16, 16);
                string ticketFailure = null;
                TicketAttempts++;
                ticketFlow = tickets.Begin(RecordingIdentity.Identity(SessionId, seconds, proof, Trust.IdentityPrefix, Trust.IdentityDomain), hex => SubmitAdmission(hex, seconds),
                    code => ticketFailure = SanitizeTicketFailure(code), lifetime.Token);
                if (ticketFlow == null) { StopUnavailable("ticket_unavailable"); yield break; }
                while (State == RecordingSessionState.Admitting && ticketFlow.MoveNext()) yield return null;
                Release(ticketFlow as IDisposable); ticketFlow = null;
                if (ticketFailure != null && State == RecordingSessionState.Admitting) { StopUnavailable(ticketFailure); yield break; }
            }
            if (State == RecordingSessionState.Admitting)
            { AdmissionExhaustionReason = "generation_limit"; StopUnavailable("admission_exhausted"); }
        }
        private IEnumerator SubmitAdmission(string hex, long seconds)
        {
            byte[] body = RecordingProtocol.Encode(RecordingProtocol.Start(SessionId, proof, seconds, hex, build, microphone, consentUtc, metadataFinalizationSeconds));
            // Keep this exact body and its ticket lease through pending/lost-response
            // recovery. A fresh ticket/time is acquired only for a fenced takeover.
            for (int poll = 0; poll < 24 && State == RecordingSessionState.Admitting; poll++)
            {
                AdmissionAttempts++;
                bool responded = false;
                try {
                    BeginHttp(new RecordingHttpRequest("POST", "/v1/sessions", body));
                    while (!HttpDone()) yield return null;
                    responded = true;
                } finally {
                    // Preserve the existing consumer exception/cancellation
                    // behavior. Only annotate a failed request with no reply;
                    // an owner's End/deadline must not invent a transport error.
                    if (!responded && State == RecordingSessionState.Admitting) {
                        LastAdmissionHttpStatus = 0; LastAdmissionResponseCode = "transport_error";
                    }
                }
                string responseCode = reply.Status == 201 || reply.Status == 202 ? "response_unverified" : ErrorCode(reply, diagnostic: true);
                LastAdmissionHttpStatus = reply.Status >= 100 && reply.Status <= 599 ? reply.Status : 0;
                LastAdmissionResponseCode = LastAdmissionHttpStatus == 0 && responseCode == "opaque_response" ? "transport_error" : responseCode;
                if (reply.Status == 201 || reply.Status == 202)
                {
                    var result = RecordingProtocol.Read(reply); RecordingProtocol.Session(result, SessionId); ObserveServer(result);
                    RecordingProtocol.Integer(result, "generation", 1, 3);
                    string status = RecordingProtocol.Text(result, "state", 16);
                    string video = RecordingProtocol.Text(result, "videoAvailability", 20);
                    if (reply.Status == 201 && status == "active" && (video == "leased" || video == "unavailable"))
                    {
                        endsAt = RecordingProtocol.Date(result, "endsAtUtc");
                        var duration = (endsAt - EstimatedServerNow).TotalSeconds;
                        if (duration <= 0 || duration > RecordingDiagnosticsLimits.SessionSeconds) throw new RecordingFailure("invalid_response");
                        AdmissionGeneration = (int)RecordingProtocol.Integer(result, "generation", 1, 3);
                        metadataUntil = RecordingProtocol.MetadataDeadline(result, endsAt, metadataFinalizationSeconds);
                        admitted = true; sessionEnd = clock.Seconds + duration;
                        metadataEnd = sessionEnd + (metadataUntil.HasValue ? (metadataUntil.Value - endsAt).TotalSeconds : 0);
                        State = RecordingSessionState.Active;
                        LastAdmissionResponseCode = "active";
                        heartbeatAt = refreshAt = retryAt = clock.Seconds; MediaStatus = video; yield break;
                    }
                    if (reply.Status != 202 || status != "pending" || video != "not_started" || result["endsAtUtc"]?.Type != JTokenType.Null)
                        throw new RecordingFailure("invalid_response");
                    LastAdmissionResponseCode = "pending";
                }
                else
                {
                    string code = responseCode;
                    if (code == "fresh_ticket_required") yield break;
                    if (reply.Status == 401 || reply.Status == 403 || reply.Status == 409 || reply.Status == 410 || code == "recording_disabled")
                    { StopUnavailable("admission_denied"); yield break; }
                }
                double until = clock.Seconds + Backoff(poll, reply);
                while (clock.Seconds < until) yield return null;
            }
        }
        private IEnumerator Authorize()
        {
            var objects = queued.ToArray();
            bool needMedia = !CaptureDeadlineReached && clock.Seconds >= refreshAt && clock.Seconds >= mediaAttemptAt;
            if (needMedia) mediaAttemptAt = clock.Seconds + 60;
            var body = RecordingProtocol.Encode(RecordingProtocol.Authorize(needMedia, objects));
            BeginHttp(new RecordingHttpRequest("POST", SessionPath("authorize"), body, proof));
            while (!HttpDone()) yield return null;
            if (reply.Status != 200)
            {
                string code = ErrorCode(reply);
                if (code == "log_quota")
                {
                    // Log reservation exhaustion must not end otherwise healthy video.
                    // Retry an empty heartbeat, never endlessly resubmit denied objects.
                    logQuotaEnded = true; LogStatus = "unavailable"; LastError = "log_quota";
                    DroppedObjects += queued.Count; queued.Clear(); failures = 0;
                    retryAt = clock.Seconds + 2.1; heartbeatAt = clock.Seconds;
                    if (needMedia) mediaAttemptAt = clock.Seconds;
                    yield break;
                }
                if (reply.Status == 401 || reply.Status == 403 || reply.Status == 409 || reply.Status == 410 || code == "recording_disabled" || ++failures >= 3)
                { LastError = "authorization_unavailable"; End("failure"); yield break; }
                retryAt = clock.Seconds + Backoff(failures - 1, reply); heartbeatAt = Math.Min(heartbeatAt, retryAt); yield break;
            }
            var json = RecordingProtocol.Read(reply);
            var result = RecordingProtocol.Authorization(Trust, json, SessionId, objects, endsAt, clock.Seconds, lastHttpDuration, metadataUntil); ObserveServer(json);
            // A pre-ceiling request can finish after it. Do not hand a late STS
            // response to the native runner, even though metadata is still authorized.
            if (CaptureDeadlineReached && result.Media != null)
                result = new RecordingAuthorization("unavailable", result.LogStatus, null, result.Uploads, result.RemainingBytes, result.RemainingObjects,
                    result.ServerUtc, result.ReceivedMonotonicSeconds, result.RequestDurationSeconds);
            if (result.Media != null)
            {
                if (!needMedia || (streamArn != null && streamArn != result.Media.StreamArn)) throw new RecordingFailure("invalid_response");
                streamArn = result.Media.StreamArn;
                mediaExpiresAt = clock.Seconds + (result.Media.ExpiresAtUtc - EstimatedServerNow).TotalSeconds;
                refreshAt = clock.Seconds + Math.Min(600, Math.Max(0, (result.Media.ExpiresAtUtc - EstimatedServerNow).TotalSeconds - 60));
            }
            else if (needMedia) refreshAt = mediaAttemptAt;
            failures = 0; retryAt = clock.Seconds + 2.1; heartbeatAt = clock.Seconds + 45;
            MediaStatus = result.MediaStatus; LogStatus = logQuotaEnded ? "unavailable" : result.LogStatus;
            if (result.LogStatus == "authorized" || ++objectFailures >= 3)
            {
                if (result.LogStatus != "authorized") { DroppedObjects += objects.Length; LastError = "log_authorization_unavailable"; }
                foreach (var item in objects) queued.Remove(item);
                objectFailures = 0;
            }
            else retryAt = clock.Seconds + 5;
            publication = result;
        }
        private string SessionPath(string operation) => "/v1/sessions/" + SessionId.ToString("D") + "/" + operation;
        private void ObserveServer(JObject json) { serverUtc = RecordingProtocol.Date(json, "serverUtc"); serverObservedAt = clock.Seconds; }
        private DateTimeOffset EstimatedServerNow => serverUtc.AddSeconds(clock.Seconds - serverObservedAt);
        private double Backoff(int attempt, RecordingHttpResponse response)
        {
            double random = jitter(); if (double.IsNaN(random) || random < 0 || random > 1) random = 0.5;
            return Math.Max(response.RetryAfterSeconds, Math.Min(5, Math.Pow(2, Math.Min(attempt, 3))) * (0.8 + 0.4 * random));
        }
        // Shared by the recording failure callback and proof's safe error
        // display. The client stays independent of Main/Steam enum assemblies;
        // future/unknown adapter errors remain opaque, including null inputs.
        public static string SanitizeTicketFailure(string code)
        {
            switch (code) {
                case "ticket_invalid_request": case "ticket_invalid_handle": case "ticket_rejected": case "ticket_invalid_response":
                case "ticket_timed_out": case "ticket_cancelled": case "ticket_disposed": case "ticket_too_many_requests":
                case "admission_consumer_failed": case "ticket_consent_required": case "ticket_unavailable":
                    return code;
                default: return "ticket_unavailable";
            }
        }
        private string ErrorCode(RecordingHttpResponse response, bool diagnostic = false)
        {
            // Preserve the original control-flow vocabulary. Additional finite
            // issuer limit codes belong only to admission diagnostics/health;
            // non-diagnostic callers retain their fallback and retry semantics.
            string fallback = diagnostic ? "opaque_response" : "issuer_unavailable";
            if (diagnostic) ClearSteamAuthDiagnostics();
            try {
                var json = RecordingProtocol.Read(response); string code = RecordingProtocol.Text(json, "code", 64);
                // Optional backend annotations are diagnostic only and accepted
                // exclusively on the existing401 fresh-ticket control branch.
                // Older responses remain valid; malformed optional fields must
                // never change the fresh-ticket retry or LastError behavior.
                if (diagnostic && response.Status == 401 && code == "fresh_ticket_required") {
                    var category = json["steamAuthFailure"];
                    if (category?.Type == JTokenType.String) {
                        switch ((string)category) {
                            case "missing_response": case "valve_error": case "missing_params": case "result_not_ok": case "missing_steamid":
                                LastSteamAuthFailure = (string)category; break;
                        }
                    }
                    if (LastSteamAuthFailure == "valve_error") {
                        if (json["valveErrorCode"] != null) {
                            try { LastValveErrorCode = (int)RecordingProtocol.Integer(json, "valveErrorCode", 0, 65535); }
                            catch (RecordingFailure) { } // Wrong type/range stays unknown; no guessed numeric meaning.
                        }
                        if (json["valveErrorTerms"] is JArray terms && terms.Count <= ValveErrorTermOrder.Length) {
                            var accepted = new string[terms.Count]; int previous = -1; bool valid = true;
                            for (int i = 0; i < terms.Count; i++) {
                                int index = terms[i].Type == JTokenType.String ? Array.IndexOf(ValveErrorTermOrder, (string)terms[i]) : -1;
                                if (index <= previous) { valid = false; break; } // Reject unknown, repeated or reordered terms as one invalid optional field.
                                accepted[i] = ValveErrorTermOrder[index]; previous = index;
                            }
                            if (valid) LastValveErrorTerms = Array.AsReadOnly(accepted); // [] means inspected/unmatched; null means unknown.
                        }
                        if (json["publisherKeyShape"]?.Type == JTokenType.String) {
                            switch ((string)json["publisherKeyShape"]) {
                                case "expected_shape": case "unexpected_shape": LastPublisherKeyShape = (string)json["publisherKeyShape"]; break;
                            }
                        }
                    }
                }
                return code == "fresh_ticket_required" || code == "recording_disabled" || code == "log_quota"
                    || (diagnostic && RecordingDiagnostics.IsIssuerLimitCode(code)) ? code : fallback; }
            catch { return fallback; }
        }
        private void ClearSteamAuthDiagnostics()
        {
            LastSteamAuthFailure = null; LastValveErrorCode = null;
            LastValveErrorTerms = null; LastPublisherKeyShape = null;
        }
        private void BeginHttp(RecordingHttpRequest request)
        {
            if (State != RecordingSessionState.Admitting && State != RecordingSessionState.Active && State != RecordingSessionState.Ending)
                throw new RecordingFailure("session_ended");
            // A new admission config/POST request invalidates the prior optional
            // Steam failure annotation, including while a new reply is pending.
            if (State == RecordingSessionState.Admitting) ClearSteamAuthDiagnostics();
            request.Validate(); Release(http); reply = null; httpEnd = clock.Seconds + request.TimeoutSeconds;
            httpBeganAt = clock.Seconds;
            http = transport.Begin(request) ?? throw new RecordingFailure("transport_unavailable");
        }
        private bool HttpDone()
        {
            if (http == null) throw new RecordingFailure("transport_unavailable");
            if (!http.IsDone && clock.Seconds < httpEnd) return false;
            try { reply = http.IsDone ? http.Response : new RecordingHttpResponse(0, null); lastHttpDuration = clock.Seconds - httpBeganAt; }
            finally { Release(http); http = null; }
            if (reply == null) reply = new RecordingHttpResponse(0, null);
            return true;
        }
        public void End(string reason = "normal")
        {
            if (State == RecordingSessionState.Ended || State == RecordingSessionState.Ending || State == RecordingSessionState.Unavailable || disposed) return;
            closeReason = reason == "normal" || reason == "withdrawn" || reason == "game_exit" ? reason : "failure";
            State = RecordingSessionState.Ending; lifetime.Cancel(); publication = null; queued.Clear();
            if (!ticking) DisposeWork();
            if (!admitted) Finish();
        }
        private void TickClose()
        {
            try
            {
                if (!closeStarted)
                {
                    closeStarted = true;
                    BeginHttp(new RecordingHttpRequest("POST", SessionPath("close"), RecordingProtocol.Encode(new JObject {
                        ["protocolVersion"] = 1, ["requestId"] = Guid.NewGuid().ToString("D"), ["reason"] = closeReason }), proof, 3));
                }
                if (HttpDone())
                {
                    if (reply.Status == 200)
                    {
                        var json = RecordingProtocol.Read(reply); RecordingProtocol.Session(json, SessionId);
                        if (RecordingProtocol.Text(json, "state", 16) != "closed") throw new RecordingFailure("invalid_response");
                    }
                    else LastError = "close_unavailable";
                    Finish(); // One attempt; a lost close relies on inactivity/quarantine.
                }
            }
            catch { LastError = "close_unavailable"; Finish(); }
        }
        private void StopUnavailable(string error)
        {
            LastError = error; State = RecordingSessionState.Unavailable; lifetime.Cancel(); publication = null; queued.Clear(); proof = null;
            if (!ticking) DisposeWork();
        }
        private void Finish() { Release(http); http = null; proof = null; publication = null; State = RecordingSessionState.Ended; }
        private void DisposeWork()
        {
            Release(http); http = null;
            Release(ticketFlow as IDisposable); ticketFlow = null;
            var owned = worker; worker = null; Release(owned as IDisposable);
        }
        // Cleanup is a fail-open boundary too: a native/transport disposal failure
        // must not escape End into the future consent/Play owner's gameplay path.
        private static void Release(IDisposable value) { try { value?.Dispose(); } catch { } }
        public void Dispose()
        {
            if (disposed) return;
            lifetime.Cancel(); State = RecordingSessionState.Ended; disposed = true; publication = null; queued.Clear(); proof = null;
            if (!ticking) DisposeWork();
            lifetime.Dispose();
        }
    }
}
