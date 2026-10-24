using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Playkitu.Client
{
    public interface IRecordingUploadTransport { IRecordingPreparedUpload Prepare(RecordingGrant grant, RecordingPayload payload); }
    public interface IRecordingPreparedUpload : IDisposable { int AttemptBytes { get; } IRecordingHttpOperation Begin(); }
    public interface IRecordingCompression { IRecordingCompressionJob Begin(Guid sessionId, string kind, int sequence, byte[] ownedRawBytes); }
    public interface IRecordingCompressionJob : IDisposable { bool IsDone { get; } RecordingPayload Result { get; } }

    public sealed class RecordingPayload
    {
        private readonly byte[] compressed;
        public readonly RecordingObject Descriptor;
        public readonly Guid SessionId;
        public readonly int RawBytes;
        private RecordingPayload(Guid sessionId, string kind, int sequence, byte[] bytes, int rawBytes)
        {
            compressed = bytes; SessionId = sessionId; RawBytes = rawBytes;
            using (var sha = SHA256.Create()) Descriptor = new RecordingObject(kind, sequence, bytes.Length, Convert.ToBase64String(sha.ComputeHash(bytes)));
        }
        public byte[] CopyCompressedBytes() => (byte[])compressed.Clone();
        public bool Matches(RecordingGrant grant) => grant != null && grant.SessionId == SessionId && Descriptor.Same(grant.Descriptor);
        public static RecordingPayload Compress(Guid sessionId, string kind, int sequence, byte[] raw)
        {
            if (sessionId == Guid.Empty || raw == null || raw.Length > (kind == "manifest" ? 16384 : 65536)) throw new RecordingFailure("invalid_batch");
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(raw, 0, raw.Length);
                return new RecordingPayload(sessionId, kind, sequence, output.ToArray(), raw.Length);
            }
        }
        public override string ToString() => "Sealed recording payload (content omitted)";
    }
    public sealed class RecordingCompression : IRecordingCompression
    {
        public IRecordingCompressionJob Begin(Guid sessionId, string kind, int sequence, byte[] ownedRawBytes) => new Job(sessionId, kind, sequence, ownedRawBytes);
        private sealed class Job : IRecordingCompressionJob
        {
            private Task<RecordingPayload> task;
            public Job(Guid sessionId, string kind, int sequence, byte[] raw)
            {
                // Exactly one bounded work item, with no Unity calls, callbacks or
                // reference to the uploader. Disposal cannot publish a late result.
                task = Task.Run(() => { try { return RecordingPayload.Compress(sessionId, kind, sequence, raw); } catch { return null; } });
            }
            public bool IsDone => task == null || task.IsCompleted;
            public RecordingPayload Result => task != null && task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            public void Dispose() { task = null; }
        }
    }
    public sealed class RecordingUploadOptions
    {
        public const int MaximumFramedAttemptBytes = RecordingDiagnosticsLimits.MaximumFramedAttemptBytes;
        public readonly int LogFlushSeconds,LogBatchBytes,PostAttemptLimit,GrantCycleLimit,RetryBaseSeconds;
        public readonly long AttemptBudgetBytes, FinalAttemptReserveBytes;
        public readonly int BytesPerSecond, BurstBytes, IndexIntervalSeconds;
        public RecordingUploadOptions(long attemptBudgetBytes = RecordingDiagnosticsLimits.AttemptBytesPerSession, int bytesPerSecond = 32768, int burstBytes = 262144, int indexIntervalSeconds = 60, int logFlushSeconds = 5, int logBatchBytes = 65536, int postAttempts = 3, int grantCycles = 3, int retryBaseSeconds = 1)
        {
            if (indexIntervalSeconds < 1 || indexIntervalSeconds > 60 || attemptBudgetBytes < 1 || attemptBudgetBytes > RecordingDiagnosticsLimits.AttemptBytesPerSession || bytesPerSecond < 1 || bytesPerSecond > 131072 || burstBytes < 1 || burstBytes > 262144)
                throw new RecordingFailure("invalid_upload_options");
            if(logFlushSeconds<1||logFlushSeconds>15||logBatchBytes<16384||logBatchBytes>65536||postAttempts<1||postAttempts>3||grantCycles<1||grantCycles>3||retryBaseSeconds<1||retryBaseSeconds>5)
                throw new RecordingFailure("invalid_upload_options");
            LogFlushSeconds=logFlushSeconds;LogBatchBytes=logBatchBytes;PostAttemptLimit=postAttempts;GrantCycleLimit=grantCycles;RetryBaseSeconds=retryBaseSeconds;
            // Reserve up to three maximum-framed attempts for each of the final
            // log/index/stopping-manifest identities. Small explicit test budgets
            // reserve a proportional part; they cannot promise successful final posts.
            FinalAttemptReserveBytes = Math.Min(attemptBudgetBytes / 4, 3L * postAttempts * MaximumFramedAttemptBytes);
            IndexIntervalSeconds = indexIntervalSeconds; AttemptBudgetBytes = attemptBudgetBytes; BytesPerSecond = bytesPerSecond; BurstBytes = burstBytes;
        }
    }
    public enum RecordingUploaderState { Idle, Running, Draining, Ended }

    // Owns bounded bytes explicitly supplied by a consent owner. No log
    // callback/file watcher/bootstrap, native capture, disk spool or credential discovery.
    public sealed class RecordingBatchUploader : IDisposable
    {
        private sealed class Pending
        {
            public RecordingPayload Payload;
            public double Created, NextPost, GrantRequestedAt = double.NegativeInfinity, GrantDeadline;
            public int PostAttempts, GrantCycles;
            public RecordingGrant Grant;
        }
        private readonly RecordingSessionClient session;
        private readonly IRecordingUploadTransport transport;
        private readonly IRecordingClock clock;
        private readonly RecordingTiming timing;
        private readonly RecordingUploadOptions options;
        private readonly IRecordingCompression compression;
        private readonly List<Pending> pending = new List<Pending>();
        private readonly Dictionary<string, int> sequences = new Dictionary<string, int> { ["log"] = 0, ["time-index"] = 0, ["manifest"] = 0 };
        private readonly byte[] log;
        private IRecordingCompressionJob job;
        private string jobKind;
        private int jobSequence, jobRawBytes;
        private double jobCreated, logFirst, logLast, logDue, indexDue, manifestPeriodic, manifestEventDue, lastManifest = double.NegativeInfinity;
        private long sealedRevision, logOffset;
        private int logLength, logNewlines, compressionFailures;
        private bool logStartsMidLine, previousMidLine, subscribed, capacityLimited;
        private long reservedCompressedBytes;
        private int scheduledObjects;
        private IRecordingPreparedUpload prepared;
        private IRecordingHttpOperation post;
        private Pending sending;
        private double postEnd, drainEnd, lastTick, tokens;
        public RecordingUploaderState State { get; private set; }
        public string LastError { get; private set; }
        public long AttemptedBytes { get; private set; }
        public int PostAttempts { get; private set; }
        public int UploadedObjects { get; private set; }
        public bool CapacityLimited => capacityLimited;
        public long ReservedCompressedBytes => reservedCompressedBytes;
        public int ScheduledObjects => scheduledObjects;
        public int PendingPayloads => pending.Count + (job == null ? 0 : 1);
        public long CompressedQueueBytes => pending.Sum(p => p.Payload.Descriptor.CompressedBytes);
        public RecordingBatchUploader(RecordingSessionClient session, IRecordingUploadTransport transport, IRecordingClock clock, RecordingTiming timing,
            RecordingUploadOptions options = null, IRecordingCompression compression = null)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session)); this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); this.timing = timing ?? throw new ArgumentNullException(nameof(timing));
            this.options = options ?? new RecordingUploadOptions(); log = new byte[this.options.LogBatchBytes]; this.compression = compression ?? new RecordingCompression();
        }
        public override string ToString() => "Recording uploader (content and authorization omitted)";
        public bool Begin()
        {
            if (State != RecordingUploaderState.Idle || session.State != RecordingSessionState.Active || session.SessionId != timing.SessionId
                || session.AdmissionGeneration != timing.AdmissionGeneration) return false;
            if (!RecordingTiming.Finite(clock.Seconds) || clock.Seconds < timing.StartMonotonicSeconds) return false;
            lastTick = clock.Seconds; tokens = options.BurstBytes;
            logDue = lastTick + options.LogFlushSeconds; indexDue = lastTick + options.IndexIntervalSeconds; manifestPeriodic = lastTick + 180; manifestEventDue = lastTick;
            State = RecordingUploaderState.Running; session.Authorized += OnAuthorized; subscribed = true; return true;
        }
        public bool AppendLog(byte[] bytes, int offset = 0, int count = -1)
        {
            if (State != RecordingUploaderState.Running || session.State != RecordingSessionState.Active) return false;
            if (bytes == null || offset < 0 || offset > bytes.Length) return false;
            if (count == -1) count = bytes.Length - offset;
            if (count < 0 || count > bytes.Length - offset) return false;
            if (count > 65536 || count > log.Length - logLength)
            { Gap("input_overflow", count, false, logOffset + logLength); return false; }
            if (count == 0) return true;
            if (capacityLimited) { Gap(LastError ?? "upload_budget", count, false, logOffset + logLength); return false; }
            if (logLength == 0) { logFirst = clock.Seconds; logStartsMidLine = previousMidLine; }
            Buffer.BlockCopy(bytes, offset, log, logLength, count);
            for (int i = offset; i < offset + count; i++) if (bytes[i] == 10) logNewlines++;
            logLength += count; logLast = clock.Seconds; previousMidLine = bytes[offset + count - 1] != 10; return true;
        }
        public bool ObserveTiming(RecordingTimingObservation observation)
        { return State == RecordingUploaderState.Running && session.State == RecordingSessionState.Active && timing.Observe(observation); }
        public void BeginDrain(double seconds = 6)
        {
            if (State != RecordingUploaderState.Running) return;
            // Final log/index/stopping-manifest compression spans several frames.
            // The first authorization can snapshot only the log; the remaining
            // objects then respect the session's 2.1s authorization cooldown.
            // Six seconds accommodates that healthy sequence without bypassing
            // rate limits or adding staging/parallel uploads. Ordinary End keeps
            // ticking asynchronously; the independent absolute 8s quit gate may
            // still cut this best-effort drain short after native retirement.
            State = RecordingUploaderState.Draining; drainEnd = clock.Seconds + (RecordingTiming.Finite(seconds) ? Math.Max(0, Math.Min(6, seconds)) : 0);
            logDue = indexDue = manifestEventDue = clock.Seconds; sealedRevision = -1;
        }
        public void Tick()
        {
            if (State == RecordingUploaderState.Idle || State == RecordingUploaderState.Ended) return;
            double now = clock.Seconds;
            if (!RecordingTiming.Finite(now) || now < lastTick || session.State != RecordingSessionState.Active)
            { Stop("session_ended"); return; }
            tokens = Math.Min(options.BurstBytes, tokens + (now - lastTick) * options.BytesPerSecond); lastTick = now;
            if (State == RecordingUploaderState.Draining && now >= drainEnd) { Stop("drain_timeout"); return; }
            try
            {
                if (job != null && now - jobCreated >= 30) { Stop("compression_timeout"); return; }
                if (job != null && job.IsDone)
                {
                    var payload = job.Result; Release(job); job = null;
                    if (payload == null || payload.SessionId != session.SessionId || CompressedQueueBytes + payload.Descriptor.CompressedBytes > 1024 * 1024)
                    {
                        Gap("compression_failed", jobKind == "log" ? jobRawBytes : 0, true, null, jobKind, jobSequence); LastError = "compression_failed";
                        if (++compressionFailures >= 3) { Stop("compression_failed"); return; }
                    }
                    else {
                        compressionFailures = 0;
                        long ceiling = RecordingDiagnosticsLimits.StoredBytesPerSession - (State == RecordingUploaderState.Running ? RecordingDiagnosticsLimits.FinalStoredBytesReserve : 0);
                        if (reservedCompressedBytes + payload.Descriptor.CompressedBytes > ceiling) {
                            Gap("log_quota", jobKind == "log" ? jobRawBytes : 0, true, null, jobKind, jobSequence);
                            LimitCapacity("log_quota");
                        } else { reservedCompressedBytes += payload.Descriptor.CompressedBytes; pending.Add(new Pending { Payload = payload, Created = jobCreated }); }
                    }
                }
                foreach (var item in pending.ToArray()) if (now - item.Created >= 30) Drop(item, "object_expired");
                PollPost(now);
                if (State == RecordingUploaderState.Ended) return;
                ScheduleCompression(now);
                foreach (var item in pending.ToArray())
                {
                    if (item == sending) continue;
                    if (item.Grant != null && item.GrantDeadline <= now + 8) item.Grant = null;
                    if (item.Grant == null && now - item.GrantRequestedAt >= 10)
                    {
                        if (item.GrantCycles >= options.GrantCycleLimit) { Drop(item, "grant_exhausted"); continue; }
                        if (session.QueueObjects(item.Payload.Descriptor)) { item.GrantCycles++; item.GrantRequestedAt = now; }
                        else if (session.LastError == "log_quota") { Stop("log_quota"); return; }
                    }
                }
                if (sending == null)
                {
                    var item = pending.FirstOrDefault(p => p.Grant != null && now >= p.NextPost && p.GrantDeadline > now + 8);
                    if (item != null) StartPost(item, now);
                }
                if (State == RecordingUploaderState.Draining && job == null && pending.Count == 0 && logLength == 0 && !timing.HasPendingIndexData && sealedRevision == timing.Revision)
                    Stop(null);
            }
            catch { Stop("upload_unavailable"); }
        }
        private void ScheduleCompression(double now)
        {
            if (job != null || pending.Count >= 8 || capacityLimited && State == RecordingUploaderState.Running) return;
            string kind = null; byte[] raw = null; int sequence = 0;
            bool manifest = (sealedRevision != timing.Revision && now >= Math.Max(manifestEventDue, lastManifest + 2)) || now >= manifestPeriodic;
            if (State == RecordingUploaderState.Draining) manifest = sealedRevision != timing.Revision;
            // C13 healthy ACK traffic filled the 48-row timing queue before its
            // first minute index. Seal at half capacity through the same bounded
            // job/payload path; pressure precedes logs/manifests so they cannot
            // starve it. The capacity guard above leaves unsealed rows untouched.
            if (timing.PendingRecords >= RecordingTiming.MaximumRecords / 2) kind = "time-index";
            // A full batch is ready immediately; timer drift behind timing-index work
            // must not leave a full bounded buffer waiting until the next offer.
            else if (logLength > 0 && (State == RecordingUploaderState.Draining || logLength >= options.LogBatchBytes || now >= logDue)) kind = "log";
            // Drain also catches final range updates after an earlier snapshot
            // sealed; the normal minute interval must not postpone that evidence.
            else if (timing.HasPendingIndexData && (State == RecordingUploaderState.Draining || now >= indexDue)) kind = "time-index";
            else if (manifest) kind = "manifest";
            if (kind == null) return;
            sequence = sequences[kind];
            // Bound attempted identities as well as accepted compressed reservations;
            // retries of immutable bytes reuse an identity. Keep final identities free.
            if (State == RecordingUploaderState.Running && (sequence >= RecordingDiagnosticsLimits.ObjectsPerKind - RecordingDiagnosticsLimits.FinalPerKindReserve
                || scheduledObjects >= RecordingDiagnosticsLimits.ObjectsPerSession - RecordingDiagnosticsLimits.FinalObjectsReserve)) { LimitCapacity("log_quota"); return; }
            if (sequence >= RecordingDiagnosticsLimits.ObjectsPerKind || scheduledObjects >= RecordingDiagnosticsLimits.ObjectsPerSession) { Stop("sequence_exhausted"); return; }
            scheduledObjects++;
            sequences[kind]++; // Burn identity even if compression fails; never reuse it for different bytes.
            if (kind == "log")
            {
                raw = new byte[logLength]; Buffer.BlockCopy(log, 0, raw, 0, logLength);
                timing.LogRange(sequence, logOffset, logLength, logNewlines, logStartsMidLine, previousMidLine, timing.ElapsedNs(logFirst), timing.ElapsedNs(logLast));
                logOffset += logLength; logLength = logNewlines = 0; logDue = now + options.LogFlushSeconds;
            }
            if (kind == "time-index") { raw = timing.TakeIndex(sequence); indexDue = now + options.IndexIntervalSeconds; }
            if (kind == "manifest")
            {
                raw = timing.Manifest(sequence, State == RecordingUploaderState.Draining ? "stopping" : "running");
                sealedRevision = timing.Revision; lastManifest = now; manifestPeriodic = now + 180; manifestEventDue = now;
            }
            jobCreated = now; jobKind = kind; jobSequence = sequence; jobRawBytes = raw.Length;
            job = compression.Begin(session.SessionId, kind, sequence, raw) ?? throw new RecordingFailure("compression_failed");
        }
        private void OnAuthorized(RecordingAuthorization result)
        {
            if (State != RecordingUploaderState.Running && State != RecordingUploaderState.Draining || session.State != RecordingSessionState.Active) return;
            if (!RecordingTiming.Finite(result.ReceivedMonotonicSeconds) || !RecordingTiming.Finite(result.RequestDurationSeconds)
                || result.RequestDurationSeconds < 0 || result.RequestDurationSeconds > 10 || result.ReceivedMonotonicSeconds > clock.Seconds) return;
            foreach (var grant in result.Uploads)
            {
                var item = pending.FirstOrDefault(p => p.Payload.Matches(grant));
                if (item == null || item == sending) continue;
                // Subtract the whole observed request duration and a five-second
                // safety margin. Local UTC adjustments cannot extend a signed grant.
                double deadline = result.ReceivedMonotonicSeconds + (grant.ExpiresAtUtc - result.ServerUtc).TotalSeconds - result.RequestDurationSeconds - 5;
                if (deadline <= clock.Seconds + 8) { item.GrantRequestedAt = Math.Min(item.GrantRequestedAt, clock.Seconds - 10); continue; }
                if (item.Grant == null || deadline > item.GrantDeadline) { item.Grant = grant; item.GrantDeadline = deadline; }
            }
        }
        private void StartPost(Pending item, double now)
        {
            if (prepared == null) { prepared = transport.Prepare(item.Grant, item.Payload); sending = item; }
            int bytes = prepared.AttemptBytes;
            if (bytes <= 0 || bytes > RecordingUploadOptions.MaximumFramedAttemptBytes || bytes > options.BurstBytes) { Drop(item, "framing_limit"); return; }
            long ceiling = options.AttemptBudgetBytes - (State == RecordingUploaderState.Running ? options.FinalAttemptReserveBytes : 0);
            if (AttemptedBytes + bytes > ceiling) { LimitCapacity("upload_budget"); return; }
            if (tokens < bytes) return;
            tokens -= bytes; AttemptedBytes += bytes; PostAttempts++; item.PostAttempts++;
            postEnd = now + 8; post = prepared.Begin(); Release(prepared); prepared = null;
            if (post == null) throw new RecordingFailure("upload_unavailable");
        }
        private void PollPost(double now)
        {
            if (sending == null) return;
            if (post == null)
            {
                if (sending.Grant == null || sending.GrantDeadline <= now + 8) { Release(prepared); prepared = null; sending = null; }
                else StartPost(sending, now);
                return;
            }
            if (!post.IsDone && now < postEnd) return;
            var response = post.IsDone ? post.Response : null;
            Release(post); post = null; var item = sending; sending = null;
            if (response != null && (response.Status == 200 || response.Status == 201 || response.Status == 204))
            { pending.Remove(item); UploadedObjects++; timing.Uploaded(item.Payload.Descriptor); return; }
            LastError = "post_failed";
            if (item.PostAttempts >= options.PostAttemptLimit) { Drop(item, "post_exhausted"); return; }
            if (response != null && (response.Status == 400 || response.Status == 403)) { item.Grant = null; item.GrantRequestedAt = now - 10; }
            item.NextPost = now + Math.Max(response?.RetryAfterSeconds ?? 0, item.PostAttempts * options.RetryBaseSeconds);
        }
        private void LimitCapacity(string reason)
        {
            if (State == RecordingUploaderState.Draining) { Stop(reason); return; }
            // A log/metadata ceiling must not stop capture, discard the final reserve
            // or keep minting denied objects. Retain only bounded current ranges/logs;
            // counters and final gap rows disclose loss when drain later resumes.
            capacityLimited = true; LastError = reason;
            foreach (var item in pending.ToArray()) Drop(item, reason);
        }
        private void Drop(Pending item, string reason)
        {
            if (item == sending) { Release(post); Release(prepared); post = null; prepared = null; sending = null; }
            if (pending.Remove(item))
            {
                session.WithdrawObjects(item.Payload.Descriptor);
                Gap(reason, item.Payload.Descriptor.Kind == "log" ? item.Payload.RawBytes : 0, true, null,
                    item.Payload.Descriptor.Kind, item.Payload.Descriptor.Sequence);
            }
            LastError = reason;
        }
        private void Stop(string reason)
        {
            if (State == RecordingUploaderState.Ended) return;
            State = RecordingUploaderState.Ended; if (reason != null) LastError = reason;
            if (subscribed) { session.Authorized -= OnAuthorized; subscribed = false; }
            bool lostJob = job != null;
            Release(post); Release(prepared); Release(job); post = null; prepared = null; job = null; sending = null;
            session.WithdrawObjects(pending.Select(p => p.Payload.Descriptor).ToArray());
            foreach (var item in pending) Gap("stopped", item.Payload.Descriptor.Kind == "log" ? item.Payload.RawBytes : 0,
                true, null, item.Payload.Descriptor.Kind, item.Payload.Descriptor.Sequence);
            pending.Clear();
            if (lostJob) Gap("stopped", jobKind == "log" ? jobRawBytes : 0, true, null, jobKind, jobSequence);
            if (logLength > 0) Gap("stopped", logLength, false);
            logLength = 0;
        }
        private void Gap(string reason, int bytes = 0, bool droppedObject = true, long? offset = null, string kind = null, int? sequence = null)
        {
            double now = clock.Seconds;
            if (!RecordingTiming.Finite(now) || now < timing.StartMonotonicSeconds) now = Math.Max(timing.StartMonotonicSeconds, lastTick);
            try { timing.Drop(reason, now, bytes, droppedObject, offset, kind, sequence); } catch { LastError = "timing_unavailable"; }
        }
        private static void Release(IDisposable value) { try { value?.Dispose(); } catch { } }
        public void Dispose() { Stop("disposed"); }
    }
}
