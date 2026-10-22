using System;
using System.Collections.Generic;
using System.Text;
using Playreel.Client;
using UnityEngine.Networking;

namespace Playreel.Unity
{
    public sealed class UnityRecordingUploadTransport : IRecordingUploadTransport
    {
        // Grants have already passed the canonical protocol validator. Binding the
        // immutable payload here prevents a caller substituting different gzip bytes.
        // Copy fields verbatim; the file section must be last in an S3 POST form.
        public UnityWebRequest CreateRequest(RecordingGrant grant, RecordingPayload payload)
            => Build(grant, payload, out _);
        private UnityWebRequest Build(RecordingGrant grant, RecordingPayload payload, out int attemptBytes)
        {
            if (payload == null || !payload.Matches(grant)) throw new RecordingFailure("upload_binding_mismatch");
            var sections = new List<IMultipartFormSection>();
            foreach (var field in grant.Fields)
            {
                // S3 field names are header parameters. Reject ambiguous framing;
                // valid signed field values remain byte-for-byte unchanged.
                if (field.Key.Length == 0 || field.Key.Length > 128 || string.Equals(field.Key, "file", StringComparison.OrdinalIgnoreCase))
                    throw new RecordingFailure("invalid_upload_field");
                foreach (char c in field.Key) if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_'))
                    throw new RecordingFailure("invalid_upload_field");
                sections.Add(new MultipartFormDataSection(field.Key, field.Value));
            }
            sections.Add(new MultipartFormFileSection("file", payload.CopyCompressedBytes(), "batch.gz", "application/gzip"));
            var boundary = UnityWebRequest.GenerateBoundary();
            var bytes = UnityWebRequest.SerializeFormSections(sections, boundary);
            if (bytes.Length > 262144) throw new RecordingFailure("framing_limit");
            attemptBytes = bytes.Length;
            return new UnityWebRequest(grant.Url, "POST") { redirectLimit = 0, timeout = 8,
                uploadHandler = new UploadHandlerRaw(bytes) { contentType = "multipart/form-data; boundary=" + Encoding.ASCII.GetString(boundary) },
                downloadHandler = new BoundedRecordingDownload(), disposeDownloadHandlerOnDispose = true,
                disposeUploadHandlerOnDispose = true, disposeCertificateHandlerOnDispose = true };
        }
        public IRecordingPreparedUpload Prepare(RecordingGrant grant, RecordingPayload payload)
        {
            var request = Build(grant, payload, out int bytes); return new Prepared(request, bytes);
        }
        private sealed class Prepared : IRecordingPreparedUpload
        {
            private UnityWebRequest request;
            public int AttemptBytes { get; }
            public Prepared(UnityWebRequest request, int bytes) { this.request = request; AttemptBytes = bytes; }
            public IRecordingHttpOperation Begin()
            {
                if (request == null) throw new RecordingFailure("upload_already_started");
                var owned = request; request = null; return UnityRecordingOperation.Start(owned);
            }
            public void Dispose() { request?.Dispose(); request = null; }
            public override string ToString() => "Prepared recording upload (authorization and content omitted)";
        }
    }
}
