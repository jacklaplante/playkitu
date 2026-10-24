using System.Text.Json;
using System.Text.RegularExpressions;

namespace Playkitu.Issuer;

// Owner-supplied deployment trust. Never construct this from an issuer response,
// player request, downloaded manifest, environment endpoint override or ARN discovery.
public class Deployment
{
    public const int DefaultVideoStreams = 4, MaxVideoStreams = 8;
    public string AwsAccountId { get; init; } = "";
    public string AwsRegion { get; init; } = "";
    public int SteamAppId { get; init; }
    public string IssuerOrigin { get; init; } = "";
    public string Project { get; init; } = "";
    public string RolePath { get; init; } = "/playkitu/";
    public string StreamNamePrefix { get; init; } = "";
    public string Table { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("uploadBucket")]
    public string Bucket { get; init; } = "";
    public string PublisherSecretArn { get; init; } = "";
    public string MediaRoleArn { get; init; } = "";
    public string RetrievalRoleArn { get; init; } = "";
    public string[] StreamArns { get; init; } = [];
    // Omitted mode retains explicit compatibility with existing pooled installs.
    // New installations select onDemand and never allocate reusable slots.
    public string StreamMode { get; init; } = "pooled";
    public bool OnDemand => StreamMode == "onDemand";
    public RecordingLimits RecordingLimits { get; init; } = new();
    public string IdentityPrefix { get; init; } = Playkitu.Client.RecordingIdentity.Prefix;
    public string IdentityDomain { get; init; } = "playkitu-recording:";

    public void Validate()
    {
        RecordingLimits.Validate();
        static bool Match(string? text, string pattern) => text is not null && Regex.IsMatch(text, "\\A" + pattern + "\\z", RegexOptions.CultureInvariant);
        if (!Match(AwsAccountId, "[0-9]{12}") || !Match(AwsRegion, "[a-z]{2}-[a-z]+-[0-9]") || SteamAppId <= 0
            || !Match(Project, "[a-z][a-z0-9-]{1,40}") || !Match(RolePath, "/[A-Za-z0-9_/-]+/")
            || !Match(StreamNamePrefix, "[A-Za-z0-9_.-]{1,64}") || !Match(Table, "[A-Za-z0-9_.-]{3,255}")
            || !Match(Bucket, "[a-z0-9][a-z0-9-]{1,61}[a-z0-9]")
            || !Match(IdentityPrefix, "[a-z0-9]{6}:") || !Match(IdentityDomain, "[a-z0-9-]{1,64}:"))
            throw new InvalidOperationException("Invalid deployment trust.");
        if (!Uri.TryCreate(IssuerOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" || origin.Port != 443
            || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0
            || !Match(origin.Host, "[a-z0-9]{10}\\.execute-api\\." + Regex.Escape(AwsRegion) + "\\.amazonaws\\.com"))
            throw new InvalidOperationException("Invalid issuer origin.");
        var role = $"arn:aws:iam::{AwsAccountId}:role{RolePath}{Project}-";
        if (MediaRoleArn != role + "media" || RetrievalRoleArn != role + "retrieval"
            || !Match(PublisherSecretArn, $"arn:aws:secretsmanager:{Regex.Escape(AwsRegion)}:{AwsAccountId}:secret:{Regex.Escape(Project)}/publisher-key-[A-Za-z0-9]{{6}}")
            || StreamMode is not ("pooled" or "onDemand")
            // In on-demand mode this optional list is read-only compatibility
            // metadata for retained pooled recordings, never an allocation pool.
            || (OnDemand ? StreamArns.Length > MaxVideoStreams : StreamArns.Length is < 1 or > MaxVideoStreams)
            || StreamArns.Distinct().Count() != StreamArns.Length
            || StreamArns.Where((arn, i) => !Match(arn, $"arn:aws:kinesisvideo:{Regex.Escape(AwsRegion)}:{AwsAccountId}:stream/{Regex.Escape(StreamNamePrefix)}{i + 1:D2}/[0-9]+")).Any())
            throw new InvalidOperationException("Deployment resources must match owner trust.");
    }

    public string SessionStreamName(string admissionId)
    {
        if (!Regex.IsMatch(admissionId, "\\A[a-f0-9]{32}\\z")) throw new InvalidOperationException("Invalid admission identity.");
        return StreamNamePrefix + "session-" + admissionId;
    }
    public bool IsSessionStream(string arn, string admissionId) => Regex.IsMatch(arn ?? "",
        "\\Aarn:aws:kinesisvideo:" + Regex.Escape(AwsRegion) + ":" + AwsAccountId + ":stream/"
        + Regex.Escape(SessionStreamName(admissionId)) + "/[0-9]+\\z");

    public static Deployment Load(string path)
    {
        if (!Path.IsPathFullyQualified(path) || new FileInfo(path).Length > 32768) throw new InvalidOperationException("Explicit bounded deployment file required.");
        var value = JsonSerializer.Deserialize<Deployment>(File.ReadAllBytes(path), Wire.Json) ?? throw new InvalidOperationException("Invalid deployment file.");
        value.Validate(); return value;
    }
}
