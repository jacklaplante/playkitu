using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Playreel.Steam
{
    public enum SteamTicketStatus
    {
        Pending, Ready, Unavailable, InvalidRequest, InvalidHandle, Rejected,
        InvalidResponse, TimedOut, Cancelled, Disposed, TooManyRequests,
        ConsumerFailed, ConsentRequired
    }

    // Small native boundary for deterministic tests without Steam initialization,
    // real credentials, backend calls or game login. Callbacks are dispatched on
    // the same main thread as Request/Poll/Dispose, as SteamAPI.RunCallbacks does.
    public interface ISteamWebTicketBackend
    {
        bool Available { get; }
        event Action<SteamWebTicketResponse> Received;
        uint Request(string identity);
        void Cancel(uint handle);
    }

    public readonly struct SteamWebTicketResponse
    {
        public readonly uint Handle;
        public readonly int Result, Length;
        public readonly byte[] Bytes;
        public SteamWebTicketResponse(uint handle, int result, byte[] bytes, int length)
        { Handle = handle; Result = result; Bytes = bytes; Length = length; }
    }

    /// <summary>Host-owned bounded Web API ticket lifecycle.</summary>
    public sealed class SteamWebTicketClient : IDisposable
    {
        // A host login service may use a separate identity.
        // Valve specifies null when that service has not provided an identifier.
        // Never use this legacy compatibility identity for recording authorization.
        
        // Conservative application policy, not a claimed documented Valve limit.
        // The proposed ASCII per-admission identity is 61 bytes; leave room below
        // a 64-byte C string boundary until live SDK/issuer compatibility is proven.
        public const int MaximumIdentityBytes = 63;
        private const int MaximumRequests = 16;
        private readonly ISteamWebTicketBackend backend;
        private readonly Func<double> clock;
        private readonly Dictionary<uint, SteamWebTicketRequest> requests = new Dictionary<uint, SteamWebTicketRequest>();
        private bool subscribed, disposed;

        public SteamWebTicketClient(ISteamWebTicketBackend backend, Func<double> monotonicClock = null)
        {
            this.backend = backend;
            clock = monotonicClock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        }

        public SteamWebTicketRequest Request(string identity, double timeoutSeconds = 5, CancellationToken cancellation = default)
        {
            if (cancellation.IsCancellationRequested) return SteamWebTicketRequest.Failed(SteamTicketStatus.Cancelled);
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds <= 0 || timeoutSeconds > 60
                || (identity != null && (identity.Length == 0 || identity.IndexOf('\0') >= 0 || Encoding.UTF8.GetByteCount(identity) > MaximumIdentityBytes)))
                return SteamWebTicketRequest.Failed(SteamTicketStatus.InvalidRequest);
            if (disposed || backend == null) return SteamWebTicketRequest.Failed(SteamTicketStatus.Unavailable);
            if (requests.Count >= MaximumRequests) return SteamWebTicketRequest.Failed(SteamTicketStatus.TooManyRequests);
            var request = new SteamWebTicketRequest(this, clock, clock() + timeoutSeconds, cancellation);
            try {
                if (!backend.Available) return SteamWebTicketRequest.Failed(SteamTicketStatus.Unavailable);
                // Register first; Steam produces the asynchronous callback later through
                // RunCallbacks. No token data is usable before the matching callback.
                if (!subscribed) { backend.Received += OnReceived; subscribed = true; }
                uint handle = backend.Request(identity);
                if (handle == 0 || requests.ContainsKey(handle)) {
                    request.Fail(SteamTicketStatus.InvalidHandle);
                    if (requests.Count == 0) Unsubscribe();
                    return request;
                }
                request.Handle = handle;
                requests.Add(handle, request);
                return request;
            } catch {
                // Exception text from a transport/native provider must not expose ticket data.
                request.Fail(SteamTicketStatus.Unavailable);
                if (requests.Count == 0) Unsubscribe();
                return request;
            }
        }

        private void OnReceived(SteamWebTicketResponse response)
        {
            if (!requests.TryGetValue(response.Handle, out var request)) return;
            request.Poll(); // Cancellation/deadline wins even when a callback arrives late.
            if (request.Status != SteamTicketStatus.Pending) return;
            if (response.Result != 1) { request.Fail(SteamTicketStatus.Rejected); return; }
            if (response.Bytes == null || response.Length <= 0 || response.Length > response.Bytes.Length || response.Length > 2560) {
                request.Fail(SteamTicketStatus.InvalidResponse);
                return;
            }
            request.Complete(BitConverter.ToString(response.Bytes, 0, response.Length).Replace("-", ""));
        }

        internal void Release(SteamWebTicketRequest request)
        {
            uint handle = request.Handle;
            request.Handle = 0; // Remove ownership before Cancel can reenter the callback dispatcher.
            if (handle != 0 && requests.TryGetValue(handle, out var owned) && ReferenceEquals(owned, request)) {
                requests.Remove(handle);
                try { backend.Cancel(handle); } catch { request.CleanupFailed = true; }
            }
            if (requests.Count == 0) Unsubscribe();
        }

        private void Unsubscribe()
        {
            if (!subscribed) return;
            subscribed = false;
            backend.Received -= OnReceived;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var request in new List<SteamWebTicketRequest>(requests.Values)) request.Dispose();
            Unsubscribe();
        }
    }

    // Owns the handle through backend verification, not merely through acquisition.
    // Dispose after use/cancellation; neither ToString nor diagnostics exposes Hex.
    public sealed class SteamWebTicketRequest : IEnumerator, IDisposable
    {
        private readonly SteamWebTicketClient owner;
        private readonly Func<double> clock;
        private readonly double deadline;
        private readonly CancellationToken cancellation;
        private string hex;
        private bool disposed;
        internal uint Handle;
        public bool CleanupFailed { get; internal set; }
        public SteamTicketStatus Status { get; private set; }
        public string Hex => Status == SteamTicketStatus.Ready && !disposed ? hex : throw new InvalidOperationException("Steam Web API ticket is not available.");
        public object Current => null;

        internal SteamWebTicketRequest(SteamWebTicketClient owner, Func<double> clock, double deadline, CancellationToken cancellation)
        { this.owner = owner; this.clock = clock; this.deadline = deadline; this.cancellation = cancellation; }
        private SteamWebTicketRequest(SteamTicketStatus status) { Status = status; }
        public static SteamWebTicketRequest Failed(SteamTicketStatus status)
        {
            if (status == SteamTicketStatus.Pending || status == SteamTicketStatus.Ready) throw new ArgumentException("A failure status is required.");
            return new SteamWebTicketRequest(status);
        }
        internal void Complete(string value) { hex = value; Status = SteamTicketStatus.Ready; }
        internal void Fail(SteamTicketStatus status) { hex = null; Status = status; owner?.Release(this); }

        public void Poll()
        {
            if (disposed || (Status != SteamTicketStatus.Pending && Status != SteamTicketStatus.Ready)) return;
            if (cancellation.IsCancellationRequested) Fail(SteamTicketStatus.Cancelled);
            else if (Status == SteamTicketStatus.Pending && clock() >= deadline) Fail(SteamTicketStatus.TimedOut);
        }
        public bool MoveNext() { Poll(); return Status == SteamTicketStatus.Pending; }
        public void Reset() { throw new NotSupportedException(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            hex = null;
            if (Status == SteamTicketStatus.Pending) Status = SteamTicketStatus.Cancelled;
            else if (Status == SteamTicketStatus.Ready) Status = SteamTicketStatus.Disposed;
            owner?.Release(this);
        }
    }

    /// <summary>Holds a ticket until its actual consumer finishes, and closes it on every failure/owner shutdown.</summary>
    public sealed class SteamWebTicketFlow : IEnumerator, IDisposable
    {
        private readonly SteamWebTicketRequest ticket;
        private readonly Func<string, IEnumerator> consumer;
        private readonly Action<SteamTicketStatus> failure;
        private readonly Stack<IEnumerator> operations = new Stack<IEnumerator>();
        private IEnumerator executing, deferredDisposal;
        private bool started, completed;
        public SteamTicketStatus Status { get; private set; } = SteamTicketStatus.Pending;
        public bool FailureCallbackThrew { get; private set; }
        public bool CleanupFailed { get; private set; }
        public object Current { get; private set; }

        public SteamWebTicketFlow(SteamWebTicketRequest ticket, Func<string, IEnumerator> consumer, Action<SteamTicketStatus> failure)
        { this.ticket = ticket ?? throw new ArgumentNullException(nameof(ticket)); this.consumer = consumer; this.failure = failure; }

        public bool MoveNext()
        {
            if (completed) return false;
            ticket.Poll();
            if (ticket.Status == SteamTicketStatus.Pending) { Current = null; return true; }
            if (ticket.Status != SteamTicketStatus.Ready) return Fail(ticket.Status);
            try {
                if (!started) {
                    started = true;
                    var operation = consumer?.Invoke(ticket.Hex);
                    if (completed) { DisposeOperation(operation); return false; }
                    if (operation == null) return Fail(SteamTicketStatus.ConsumerFailed);
                    operations.Push(operation);
                }
                // Drive nested enumerators here so their exceptions and Dispose/finally
                // are inside this lease boundary, instead of being lost in Unity coroutines.
                for (int steps = 0; steps < 64 && operations.Count > 0; steps++) {
                    var operation = operations.Peek();
                    bool hasNext;
                    executing = operation;
                    try { hasNext = operation.MoveNext(); }
                    finally {
                        executing = null;
                        if (deferredDisposal != null) { var deferred = deferredDisposal; deferredDisposal = null; DisposeOperation(deferred); }
                    }
                    // A response callback may close SignInMenu from inside MoveNext.
                    // Its OnDisable has already disposed this flow and emptied the stack.
                    if (completed) return false;
                    if (!hasNext) { operations.Pop(); DisposeOperation(operation); continue; }
                    object yielded = operation.Current;
                    if (yielded is IEnumerator nested) {
                        if (operations.Count >= 32) return Fail(SteamTicketStatus.ConsumerFailed);
                        operations.Push(nested);
                    } else { Current = yielded; return true; }
                }
                if (operations.Count > 0) { Current = null; return true; }
                Status = SteamTicketStatus.Ready;
                completed = true;
                DisposeResources();
                return false;
            } catch { return Fail(SteamTicketStatus.ConsumerFailed); }
        }

        private bool Fail(SteamTicketStatus status)
        {
            Status = status;
            completed = true;
            DisposeResources(); // Revoke before invoking user code, which may itself throw.
            try { failure?.Invoke(status); } catch { FailureCallbackThrew = true; }
            return false;
        }
        private void DisposeResources()
        {
            while (operations.Count > 0) DisposeOperation(operations.Pop());
            ticket.Dispose();
            CleanupFailed |= ticket.CleanupFailed;
            Current = null;
        }
        private void DisposeOperation(IEnumerator operation)
        {
            // C# iterator finally blocks cannot reliably be disposed while that
            // iterator is executing. Defer only that one iterator until it yields.
            if (operation != null && ReferenceEquals(operation, executing)) { deferredDisposal = operation; return; }
            try { (operation as IDisposable)?.Dispose(); } catch { CleanupFailed = true; }
        }
        public void Dispose()
        {
            if (completed) return;
            completed = true;
            Status = SteamTicketStatus.Cancelled;
            DisposeResources();
        }
        public void Reset() { throw new NotSupportedException(); }
    }
}
