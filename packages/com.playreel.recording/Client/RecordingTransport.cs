using System;
using System.Collections;
using System.Diagnostics;
using System.Threading;

namespace Playreel.Client
{
    public interface IRecordingClock { double Seconds { get; } DateTimeOffset UtcNow { get; } }
    public sealed class RecordingClock : IRecordingClock
    {
        public double Seconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
    // Begin and polling run on the owner's main thread. The existing Steam flow
    // retains its handle while the supplied issuer consumer enumerator runs.
    public interface IRecordingTickets
    {
        // Failure carries only an allowlisted local code, never ticket bytes,
        // provider exception text or an admission HTTP response.
        IEnumerator Begin(string identity, Func<string, IEnumerator> consumer, Action<string> failure, CancellationToken cancellation);
    }
    public interface IRecordingTransport { IRecordingHttpOperation Begin(RecordingHttpRequest request); }
    public interface IRecordingHttpOperation : IDisposable
    {
        bool IsDone { get; }
        RecordingHttpResponse Response { get; }
    }
    public sealed class RecordingHttpRequest
    {
        public readonly string Method, Path, Proof;
        public readonly byte[] Body;
        public readonly int TimeoutSeconds;
        public RecordingHttpRequest(string method, string path, byte[] body = null, string proof = null, int timeoutSeconds = 10)
        { Method = method; Path = path; Body = body; Proof = proof; TimeoutSeconds = timeoutSeconds; }
        public override string ToString() => "Recording HTTP request (sensitive values omitted)";
        public void Validate()
        {
            // A session route must start with the empty component before its root
            // slash. In particular, never allow an absolute URL's authority in that
            // component: System.Uri also treats backslashes as URL separators.
            if (string.IsNullOrEmpty(Path) || Path[0] != '/' || Path.IndexOf('\\') >= 0)
                throw new RecordingFailure("invalid_request");
            bool config = Method == "GET" && Path == "/v1/config";
            bool start = Method == "POST" && Path == "/v1/sessions";
            var parts = (Path ?? "").Split('/');
            bool session = Method == "POST" && parts.Length == 5 && parts[0].Length == 0 && parts[1] == "v1" && parts[2] == "sessions"
                && Guid.TryParseExact(parts[3], "D", out var id) && id != Guid.Empty && (parts[4] == "authorize" || parts[4] == "close");
            if ((!config && !start && !session) || TimeoutSeconds < 1 || TimeoutSeconds > 10
                || (session ? !RecordingIdentity.ValidProof(Proof) : Proof != null)
                || (config ? Body != null : Body == null || Body.Length == 0 || Body.Length > RecordingProtocol.MaxRequestBytes))
                throw new RecordingFailure("invalid_request");
        }
    }
    public sealed class RecordingHttpResponse
    {
        public readonly int Status, RetryAfterSeconds;
        public readonly byte[] Body;
        public RecordingHttpResponse(int status, byte[] body, int retryAfterSeconds = 0)
        { Status = status; Body = body; RetryAfterSeconds = Math.Max(0, Math.Min(60, retryAfterSeconds)); }
        public override string ToString() => "Recording HTTP response (sensitive values omitted)";
    }
    public sealed class RecordingFailure : Exception
    { public RecordingFailure(string code) : base(code) { } }
}
