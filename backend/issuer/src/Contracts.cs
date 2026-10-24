using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Playkitu.Client;

namespace Playkitu.Issuer;

// Recording admission diagnostics are closed categories, never Valve response text.
public enum SteamAuthFailure { MissingResponse, ValveError, MissingParams, ResultNotOk, MissingSteamId }

[Flags]
public enum ValveErrorTerms
{
    None = 0, Identity = 1, AppId = 2, App = 4, Ticket = 8, Key = 16,
    Expired = 32, Canceled = 64, Cancelled = 128, Mismatch = 256, Invalid = 512
}
public enum PublisherKeyShape { ExpectedShape, UnexpectedShape }

internal static class SteamRejectionDiagnostics
{
    // One vocabulary/order for both lexical observation and wire emission. These
    // complete ASCII words are observations, never an interpretation of error105.
    internal static readonly (ValveErrorTerms Term, string Name)[] Terms =
    [
        (ValveErrorTerms.Identity, "identity"), (ValveErrorTerms.AppId, "appid"),
        (ValveErrorTerms.App, "app"), (ValveErrorTerms.Ticket, "ticket"),
        (ValveErrorTerms.Key, "key"), (ValveErrorTerms.Expired, "expired"),
        (ValveErrorTerms.Canceled, "canceled"), (ValveErrorTerms.Cancelled, "cancelled"),
        (ValveErrorTerms.Mismatch, "mismatch"), (ValveErrorTerms.Invalid, "invalid")
    ];
}

public sealed class IssuerError(int status, string code, int? retryAfter = null,
    SteamAuthFailure? steamAuthFailure = null, int? valveErrorCode = null,
    ValveErrorTerms? valveErrorTerms = null, PublisherKeyShape? publisherKeyShape = null) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public int? RetryAfter { get; } = retryAfter;
    public SteamAuthFailure? SteamAuthFailure { get; } = steamAuthFailure;
    public int? ValveErrorCode { get; } = valveErrorCode;
    public ValveErrorTerms? ValveErrorTerms { get; } = valveErrorTerms;
    public PublisherKeyShape? PublisherKeyShape { get; } = publisherKeyShape;
}

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Identity(Guid sessionId, long unixSeconds, string proof) =>
        Playkitu.Client.RecordingIdentity.Identity(sessionId, unixSeconds, proof);
    public static void Require([DoesNotReturnIf(false)] bool condition, string code = "invalid_request")
    { if (!condition) throw new IssuerError(400, code); }
    // Preserve the existing issuer API while compiling the same portable canonical
    // identity/proof source as Unity. This does not replace the deployed ZIP.
    public static bool ValidProof(string? value) => value is not null && Playkitu.Client.RecordingIdentity.ValidProof(value);
    public static bool SameProof(string proof, string expectedHash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(proof)), Encoding.ASCII.GetBytes(expectedHash));
}

public sealed record BuildDeclaration(string GameVersion, string? SteamBuildId = null);
public sealed record ConsentDeclaration(int SchemaVersion, bool GameplayAudioAndLogs, bool Microphone, DateTimeOffset AcceptedAtUtc);
public sealed record StartRequest(int ProtocolVersion, Guid SessionId, long AdmissionUnixSeconds, string SessionProof,
    string SteamTicketHex, BuildDeclaration Build, ConsentDeclaration Consent, int MetadataFinalizationSeconds = 0)
{
    public override string ToString() => "Recording admission (credentials omitted)";
    public void Validate()
    {
        Wire.Require(ProtocolVersion == 1 && SessionId != Guid.Empty && Wire.ValidProof(SessionProof));
        Wire.Require(MetadataFinalizationSeconds is 0 or RecordingDiagnosticsLimits.MetadataFinalizationSeconds);
        Wire.Require(Consent is { SchemaVersion: 1, GameplayAudioAndLogs: true }, "consent_required");
        Wire.Require(Build is not null && !string.IsNullOrWhiteSpace(Build.GameVersion) && Build.GameVersion.Length <= 64);
        Wire.Require(Build!.SteamBuildId is null || (Build.SteamBuildId.Length <= 20 && Build.SteamBuildId.All(char.IsAsciiDigit)));
        Wire.Require(SteamTicketHex is not null && SteamTicketHex.Length is > 0 and <= 5120 && SteamTicketHex.Length % 2 == 0
            && SteamTicketHex.All(char.IsAsciiHexDigit));
    }
    // Authentication-attempt fields deliberately do not enter the immutable binding:
    // a timed-out fenced generation can use a fresh Steam ticket without another consent step.
    // Preserve the exact legacy binding JSON/hash for old session replays. The
    // opt-in is immutable consent-owner protocol capability, never a retry knob.
    public string Binding() => MetadataFinalizationSeconds == 0
        ? Wire.Hash(JsonSerializer.Serialize(new { SessionId, ProofHash = Wire.Hash(SessionProof), Build, Consent }, Wire.Json))
        : Wire.Hash(JsonSerializer.Serialize(new { SessionId, ProofHash = Wire.Hash(SessionProof), Build, Consent, MetadataFinalizationSeconds }, Wire.Json));
}
public sealed record ObjectDescriptor(string Kind, int Sequence, long CompressedBytes, string Sha256Base64)
{
    public void Validate()
    {
        Wire.Require(Kind is "log" or "time-index" or "manifest" && Sequence is >= 0 and < RecordingDiagnosticsLimits.ObjectsPerKind);
        Wire.Require(CompressedBytes > 0 && CompressedBytes <= (Kind == "manifest" ? 32768 : 131072));
        try { Wire.Require(Convert.FromBase64String(Sha256Base64).Length == 32 && Convert.ToBase64String(Convert.FromBase64String(Sha256Base64)) == Sha256Base64); }
        catch (FormatException) { throw new IssuerError(400, "invalid_checksum"); }
    }
}
public sealed record AuthorizeRequest(int ProtocolVersion, Guid RequestId, bool MediaCredentialsNeeded, ObjectDescriptor[] Objects)
{
    public void Validate()
    {
        Wire.Require(ProtocolVersion == 1 && RequestId != Guid.Empty && Objects is not null && Objects.Length <= 16);
        foreach (var item in Objects!) { Wire.Require(item is not null); item.Validate(); }
        Wire.Require(Objects!.Select(x => (x.Kind, x.Sequence)).Distinct().Count() == Objects.Length);
    }
}
public sealed record CloseRequest(int ProtocolVersion, Guid RequestId, string Reason)
{
    public void Validate() => Wire.Require(ProtocolVersion == 1 && RequestId != Guid.Empty && Reason is "normal" or "withdrawn" or "game_exit" or "failure");
}
public sealed record StartResult(Guid SessionId, string State, int Generation, DateTimeOffset? EndsAtUtc, string VideoAvailability, int? RetryAfter,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? MetadataUntilUtc = null);
public sealed record TemporaryMedia(string AccessKeyId, string SecretAccessKey, string SessionToken, DateTimeOffset ExpiresAtUtc, string StreamArn)
{ public override string ToString() => "Temporary media credentials (omitted)"; }
public sealed record UploadGrant(string Kind, int Sequence, string Key, DateTimeOffset ExpiresAtUtc, string Url, Dictionary<string, string> Fields)
{ public override string ToString() => "Private upload authorization (omitted)"; }
public sealed record AuthorizationResult(Guid SessionId, DateTimeOffset EndsAtUtc, string MediaStatus, TemporaryMedia? Media,
    string LogStatus, UploadGrant[] Uploads, long RemainingBytes, int RemainingObjects,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? MetadataUntilUtc = null);

public sealed class IssuerOptions : Deployment
{
    public const int SessionSeconds = RecordingDiagnosticsLimits.SessionSeconds, IdleSeconds = 180, RetentionDays = 30;
    public const int MaxTesterAdmissionsPerUtcDay = 8;
    public const long MaxBytes = 64L * 1024 * 1024;
    public static object Limits(bool onDemand = false, RecordingLimits? configured = null) => new { sessionSeconds = SessionSeconds, inactivitySeconds = IdleSeconds, heartbeatSeconds = 60,
        maxBytes = RecordingDiagnosticsLimits.StoredBytesPerSession, maxObjects = RecordingDiagnosticsLimits.ObjectsPerSession,
        metadataFinalizationSeconds = RecordingDiagnosticsLimits.MetadataFinalizationSeconds,
        maxObjectsPerRequest = 16, logObjectBytes = 131072, manifestObjectBytes = 32768,
        uploadGrantSeconds = 120, mediaCredentialSeconds = 900, refreshAfterSeconds = 600, pendingReservationSeconds = 20,
        admissionDeadlineSeconds = 90, maxAdmissionGenerations = 3,
        maxActiveSessions = onDemand ? configured?.ConcurrentSessions : 8, maxPendingAdmissions = onDemand ? (int?)null : 8 };
}
