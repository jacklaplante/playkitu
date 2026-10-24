using System;
using System.Globalization;
using UnityEngine.Networking;
using Playkitu.Client;

namespace Playkitu.Unity
{
    public sealed class UnityRecordingTransport : IRecordingTransport
    {
        private readonly Uri origin;
        public UnityRecordingTransport(RecordingTrust trust)
        {
            if (!Uri.TryCreate(trust.IssuerOrigin, UriKind.Absolute, out origin) || origin.Scheme != "https" || !origin.IsDefaultPort
                || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0
                || origin.AbsoluteUri != trust.IssuerOrigin) throw new RecordingFailure("invalid_issuer_origin");
        }
        // Construction is exposed separately so offline tests inspect the real Unity
        // request without sending it. No custom CertificateHandler or gameplay headers.
        public UnityWebRequest CreateRequest(RecordingHttpRequest message)
        {
            message.Validate();
            // Check the resolved destination independently before constructing a
            // request or attaching the bearer. URI normalization must never turn a
            // purported recording route into a different origin or normalized path.
            if (!Uri.TryCreate(origin, message.Path, out var destination) || destination.Scheme != origin.Scheme
                || !string.Equals(destination.Authority, origin.Authority, StringComparison.OrdinalIgnoreCase)
                || destination.UserInfo.Length != 0 || destination.Query.Length != 0 || destination.Fragment.Length != 0
                || !string.Equals(destination.AbsolutePath, message.Path, StringComparison.Ordinal))
                throw new RecordingFailure("invalid_request");
            var request = new UnityWebRequest(destination, message.Method) {
                redirectLimit = 0, timeout = message.TimeoutSeconds, downloadHandler = new BoundedRecordingDownload(),
                disposeDownloadHandlerOnDispose = true, disposeUploadHandlerOnDispose = true, disposeCertificateHandlerOnDispose = true };
            if (message.Body != null) { request.uploadHandler = new UploadHandlerRaw(message.Body); request.SetRequestHeader("Content-Type", "application/json"); }
            if (message.Proof != null) request.SetRequestHeader("Authorization", "Bearer " + message.Proof);
            return request;
        }
        public IRecordingHttpOperation Begin(RecordingHttpRequest message)
        {
            return UnityRecordingOperation.Start(CreateRequest(message));
        }
    }
    // Issuer JSON and S3 multipart requests share precisely the same owned
    // operation/disposal boundary, while their construction/authentication differ.
    internal sealed class UnityRecordingOperation : IRecordingHttpOperation
    {
        private UnityWebRequest request;
        private UnityRecordingOperation(UnityWebRequest request) { this.request = request; }
        internal static IRecordingHttpOperation Start(UnityWebRequest request)
        {
            try { request.SendWebRequest(); return new UnityRecordingOperation(request); }
            catch { request.Dispose(); throw new RecordingFailure("transport_unavailable"); }
        }
        public bool IsDone => request == null || request.isDone;
        public RecordingHttpResponse Response
        {
            get {
                if (request == null || !request.isDone) return new RecordingHttpResponse(0, null);
                var handler = (BoundedRecordingDownload)request.downloadHandler;
                int.TryParse(request.GetResponseHeader("Retry-After"), NumberStyles.None, CultureInfo.InvariantCulture, out int retry);
                return new RecordingHttpResponse(handler.Exceeded ? 0 : (int)request.responseCode, handler.Exceeded ? null : handler.Bytes, retry);
            }
        }
        public void Dispose() { if (request == null) return; if (!request.isDone) request.Abort(); request.Dispose(); request = null; }
        public override string ToString() => "Recording HTTP operation (sensitive values omitted)";
    }
    public class BoundedRecordingDownload : DownloadHandlerScript
    {
        private readonly byte[] buffer = new byte[RecordingProtocol.MaxResponseBytes];
        private int used;
        public bool Exceeded { get; private set; }
        public byte[] Bytes { get { var result = new byte[used]; Buffer.BlockCopy(buffer, 0, result, 0, used); return result; } }
        public BoundedRecordingDownload() : base(new byte[4096]) { }
        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (Exceeded || data == null || dataLength < 0 || dataLength > data.Length || dataLength > buffer.Length - used)
            { Exceeded = true; return false; }
            Buffer.BlockCopy(data, 0, buffer, used, dataLength); used += dataLength; return true;
        }
    }
}
