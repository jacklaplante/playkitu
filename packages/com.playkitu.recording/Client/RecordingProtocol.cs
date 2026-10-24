using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Playkitu.Client
{
    public sealed class RecordingBuild
    {
        public readonly string GameVersion, SteamBuildId;
        public RecordingBuild(string gameVersion, string steamBuildId = null)
        {
            if (string.IsNullOrWhiteSpace(gameVersion) || gameVersion.Length > 64 || (steamBuildId != null
                && (steamBuildId.Length > 20 || steamBuildId.Any(c => c < '0' || c > '9')))) throw new RecordingFailure("invalid_build");
            GameVersion = gameVersion; SteamBuildId = steamBuildId;
        }
    }
    public sealed class RecordingObject
    {
        public readonly string Kind, Sha256Base64;
        public readonly int Sequence;
        public readonly long CompressedBytes;
        public RecordingObject(string kind, int sequence, long compressedBytes, string sha256Base64)
        {
            bool checksum = false;
            try { checksum = sha256Base64 != null && Convert.FromBase64String(sha256Base64).Length == 32 && Convert.ToBase64String(Convert.FromBase64String(sha256Base64)) == sha256Base64; }
            catch (FormatException) { }
            if ((kind != "log" && kind != "time-index" && kind != "manifest") || sequence < 0 || sequence >= RecordingDiagnosticsLimits.ObjectsPerKind || compressedBytes < 1
                || compressedBytes > (kind == "manifest" ? 32768 : 131072) || !checksum) throw new RecordingFailure("invalid_object");
            Kind = kind; Sequence = sequence; CompressedBytes = compressedBytes; Sha256Base64 = sha256Base64;
        }
        public bool Same(RecordingObject other) => other != null && Kind == other.Kind && Sequence == other.Sequence
            && CompressedBytes == other.CompressedBytes && Sha256Base64 == other.Sha256Base64;
    }
    public sealed class RecordingMedia
    {
        public readonly string AccessKeyId, SecretAccessKey, SessionToken, StreamArn;
        public readonly DateTimeOffset ExpiresAtUtc;
        internal RecordingMedia(string access, string secret, string token, string stream, DateTimeOffset expiry)
        { AccessKeyId = access; SecretAccessKey = secret; SessionToken = token; StreamArn = stream; ExpiresAtUtc = expiry; }
        public override string ToString() => "Recording media credentials (omitted)";
    }
    public sealed class RecordingGrant
    {
        public readonly RecordingObject Descriptor;
        public readonly Guid SessionId;
        public readonly string Kind, Key, Url;
        public readonly int Sequence;
        public readonly DateTimeOffset ExpiresAtUtc;
        public readonly IReadOnlyDictionary<string, string> Fields;
        internal RecordingGrant(Guid sessionId, RecordingObject descriptor, string key, string url, DateTimeOffset expires, Dictionary<string, string> fields)
        { SessionId = sessionId; Descriptor = descriptor; Kind = descriptor.Kind; Sequence = descriptor.Sequence; Key = key; Url = url; ExpiresAtUtc = expires; Fields = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(fields); }
        public override string ToString() => "Recording upload grant (omitted)";
    }
    public sealed class RecordingAuthorization
    {
        public readonly string MediaStatus, LogStatus;
        public readonly RecordingMedia Media;
        public readonly RecordingGrant[] Uploads;
        public readonly long RemainingBytes;
        public readonly int RemainingObjects;
        public readonly DateTimeOffset ServerUtc;
        public readonly double ReceivedMonotonicSeconds, RequestDurationSeconds;
        internal RecordingAuthorization(string mediaStatus, string logStatus, RecordingMedia media, RecordingGrant[] uploads, long bytes, int count,
            DateTimeOffset serverUtc, double receivedMonotonicSeconds, double requestDurationSeconds)
        { MediaStatus = mediaStatus; LogStatus = logStatus; Media = media; Uploads = uploads; RemainingBytes = bytes; RemainingObjects = count;
            ServerUtc = serverUtc; ReceivedMonotonicSeconds = receivedMonotonicSeconds; RequestDurationSeconds = requestDurationSeconds; }
        public override string ToString() => "Recording authorization (sensitive values omitted)";
    }
    public static class RecordingProtocol
    {
        public const int MaxRequestBytes = 16384, MaxResponseBytes = 65536;
        public const long MaxBytes = RecordingDiagnosticsLimits.StoredBytesPerSession;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        // Explicit JSON construction/parsing avoids reflection-only DTO constructors and
        // .NET10 serializer features under Unity IL2CPP stripping. Never interpolate raw
        // JSON/reader exceptions: those can contain ticket/proof/signed field values.
        public static byte[] Encode(JObject value)
        {
            var bytes = Utf8.GetBytes(value.ToString(Formatting.None));
            if (bytes.Length > MaxRequestBytes) throw new RecordingFailure("request_too_large");
            return bytes;
        }
        public static JObject Read(RecordingHttpResponse response)
        {
            try
            {
                if (response == null || response.Body == null || response.Body.Length > MaxResponseBytes) throw new RecordingFailure("invalid_response");
                using (var reader = new JsonTextReader(new StringReader(Utf8.GetString(response.Body))) { DateParseHandling = DateParseHandling.None, MaxDepth = 16 })
                {
                    var json = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read() || Integer(json, "protocolVersion", 1, 1) != 1) throw new RecordingFailure("invalid_response");
                    Date(json, "serverUtc");
                    return json;
                }
            }
            catch { throw new RecordingFailure("invalid_response"); }
        }
        public static string Text(JObject json, string key, int max = 4096)
        {
            var token = json[key];
            if (token == null || token.Type != JTokenType.String || ((string)token).Length > max || ((string)token).Length == 0) throw new RecordingFailure("invalid_response");
            return (string)token;
        }
        public static long Integer(JObject json, string key, long min, long max)
        {
            var token = json[key];
            if (token == null || token.Type != JTokenType.Integer || !long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                || value < min || value > max) throw new RecordingFailure("invalid_response");
            return value;
        }
        public static bool Boolean(JObject json, string key)
        { if (json[key] == null || json[key].Type != JTokenType.Boolean) throw new RecordingFailure("invalid_response"); return (bool)json[key]; }
        public static DateTimeOffset Date(JObject json, string key)
        {
            if (!DateTimeOffset.TryParseExact(Text(json, key, 40), new[] { "O", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) || date.Offset != TimeSpan.Zero) throw new RecordingFailure("invalid_response");
            return date;
        }
        public static void Session(JObject json, Guid id)
        { if (!Guid.TryParseExact(Text(json, "sessionId", 36), "D", out var actual) || actual != id) throw new RecordingFailure("invalid_response"); }
        public static JObject Start(Guid id, string proof, long seconds, string ticket, RecordingBuild build, bool microphone, DateTimeOffset consentUtc, int metadataFinalizationSeconds = 0)
        {
            if (ticket == null || ticket.Length < 2 || ticket.Length > 5120 || ticket.Length % 2 != 0 || ticket.Any(c => !Uri.IsHexDigit(c))) throw new RecordingFailure("invalid_ticket");
            var result = new JObject { ["protocolVersion"] = 1, ["sessionId"] = id.ToString("D"), ["admissionUnixSeconds"] = seconds,
                ["sessionProof"] = proof, ["steamTicketHex"] = ticket,
                ["build"] = new JObject { ["gameVersion"] = build.GameVersion, ["steamBuildId"] = build.SteamBuildId },
                ["consent"] = new JObject { ["schemaVersion"] = 1, ["gameplayAudioAndLogs"] = true, ["microphone"] = microphone,
                    ["acceptedAtUtc"] = consentUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) } };
            // Advertised opt-in preserves legacy admission bodies/binding hashes.
            if (metadataFinalizationSeconds != 0) {
                if (metadataFinalizationSeconds != RecordingDiagnosticsLimits.MetadataFinalizationSeconds) throw new RecordingFailure("invalid_request");
                result["metadataFinalizationSeconds"] = metadataFinalizationSeconds;
            }
            return result;
        }
        public static JObject Authorize(bool media, RecordingObject[] objects)
        {
            return new JObject { ["protocolVersion"] = 1, ["requestId"] = Guid.NewGuid().ToString("D"), ["mediaCredentialsNeeded"] = media,
                ["objects"] = new JArray(objects.Select(o => new JObject { ["kind"] = o.Kind, ["sequence"] = o.Sequence,
                    ["compressedBytes"] = o.CompressedBytes, ["sha256Base64"] = o.Sha256Base64 })) };
        }
        public static DateTimeOffset? MetadataDeadline(JObject json, DateTimeOffset end, int maximumSeconds)
        {
            var value = json["metadataUntilUtc"];
            if (value == null || value.Type == JTokenType.Null) return null;
            var deadline = Date(json, "metadataUntilUtc");
            if (maximumSeconds < 1 || maximumSeconds > RecordingDiagnosticsLimits.MetadataFinalizationSeconds
                || deadline <= end || deadline > end.AddSeconds(maximumSeconds)) throw new RecordingFailure("invalid_response");
            return deadline;
        }
        public static RecordingAuthorization Authorization(RecordingTrust trust, JObject json, Guid id, RecordingObject[] requested, DateTimeOffset end,
            double receivedMonotonicSeconds = double.NaN, double requestDurationSeconds = double.NaN, DateTimeOffset? metadataUntilUtc = null)
        {
            Session(json, id); if (Date(json, "endsAtUtc") != end) throw new RecordingFailure("invalid_response");
            if (MetadataDeadline(json, end, metadataUntilUtc.HasValue ? RecordingDiagnosticsLimits.MetadataFinalizationSeconds : 0) != metadataUntilUtc)
                throw new RecordingFailure("invalid_response");
            var now = Date(json, "serverUtc"); var mediaStatus = Text(json, "mediaStatus", 20); var logStatus = Text(json, "logStatus", 20);
            if (!new[] { "not_requested", "unavailable", "retry_later", "authorized" }.Contains(mediaStatus)
                || (logStatus != "authorized" && logStatus != "unavailable")) throw new RecordingFailure("invalid_response");
            RecordingMedia media = null;
            if (json["media"] is JObject m)
            {
                var expiry = Date(m, "expiresAtUtc"); var stream = Text(m, "streamArn", 512);
                if (mediaStatus != "authorized" || expiry <= now || expiry > now.AddMinutes(16)
                    || !trust.TryStream(stream, out _) || stream.Contains("*") || stream.Any(char.IsControl))
                    throw new RecordingFailure("invalid_response");
                media = new RecordingMedia(Text(m, "accessKeyId", 256), Text(m, "secretAccessKey", 256), Text(m, "sessionToken", 8192), stream, expiry);
            }
            else if (json["media"] == null || json["media"].Type != JTokenType.Null || mediaStatus == "authorized") throw new RecordingFailure("invalid_response");
            if (!(json["uploads"] is JArray array) || array.Count > 16 || (logStatus == "unavailable" ? array.Count != 0 : array.Count != requested.Length)) throw new RecordingFailure("invalid_response");
            var grants = new List<RecordingGrant>(); var seen = new HashSet<string>();
            foreach (var token in array)
            {
                if (!(token is JObject g)) throw new RecordingFailure("invalid_response");
                string kind = Text(g, "kind", 16); int sequence = (int)Integer(g, "sequence", 0, RecordingDiagnosticsLimits.ObjectsPerKind - 1);
                if (!seen.Add(kind + ":" + sequence) || !requested.Any(o => o.Kind == kind && o.Sequence == sequence)) throw new RecordingFailure("invalid_response");
                string key = Text(g, "key", 512), url = Text(g, "url", 2048); var expiry = Date(g, "expiresAtUtc");
                string expectedKey = "sessions/" + end.AddHours(-4).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    + "/" + id.ToString("N") + "/" + kind + "/" + sequence.ToString("D6", CultureInfo.InvariantCulture) + ".gz";
                if (key != expectedKey
                    || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
                    || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !trust.IsUploadDestination(uri) || expiry <= now || expiry > now.AddMinutes(2) || expiry > (metadataUntilUtc ?? end)
                    || !(g["fields"] is JObject fields) || fields.Count > 32) throw new RecordingFailure("invalid_response");
                var values = new Dictionary<string, string>();
                foreach (var field in fields.Properties()) values.Add(field.Name, Text(fields, field.Name, 8192));
                var descriptor = requested.Single(o => o.Kind == kind && o.Sequence == sequence);
                var bound = new Dictionary<string, string> { ["key"] = key, ["Content-Type"] = "application/gzip", ["Content-Encoding"] = "gzip",
                    ["x-amz-server-side-encryption"] = "AES256", ["x-amz-checksum-algorithm"] = "SHA256", ["x-amz-checksum-sha256"] = descriptor.Sha256Base64 };
                if (bound.Any(pair => !values.TryGetValue(pair.Key, out var actual) || actual != pair.Value)) throw new RecordingFailure("invalid_response");
                grants.Add(new RecordingGrant(id, descriptor, key, url, expiry, values));
            }
            return new RecordingAuthorization(mediaStatus, logStatus, media, grants.ToArray(), Integer(json, "remainingBytes", 0, MaxBytes), (int)Integer(json, "remainingObjects", 0, RecordingDiagnosticsLimits.ObjectsPerSession),
                now, receivedMonotonicSeconds, requestDurationSeconds);
        }
    }
}
