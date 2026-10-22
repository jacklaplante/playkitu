using System;
using System.Text.RegularExpressions;

namespace Playreel.Client
{
    // Supply from the developer's deployment configuration, never an issuer reply
    // or player input. Freeze the trust boundary before consent/admission starts.
    public sealed class RecordingTrust
    {
        public uint SteamAppId { get; }
        public string AwsAccountId { get; }
        public string AwsRegion { get; }
        public string IssuerOrigin { get; }
        public string StreamNamePrefix { get; }
        public string UploadBucket { get; }
        public string IdentityPrefix { get; }
        public string IdentityDomain { get; }
        private readonly Regex streamPattern;

        public RecordingTrust(uint steamAppId, string awsAccountId, string awsRegion,
            string issuerOrigin, string streamNamePrefix, string uploadBucket,
            string identityPrefix = RecordingIdentity.Prefix, string identityDomain = "playreel-recording:")
        {
            if (steamAppId == 0 || !Match(awsAccountId, "[0-9]{12}")
                || !Match(awsRegion, "[a-z]{2}-[a-z]+-[0-9]")
                || !Match(streamNamePrefix, "[A-Za-z0-9_.-]{1,64}")
                || !Match(uploadBucket, "[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]")
                || uploadBucket.Contains("..") || !Match(identityPrefix, "[a-z0-9]{6}:")
                || !Match(identityDomain, "[a-z0-9-]{1,64}:")) throw new RecordingFailure("invalid_trust");
            if (!Uri.TryCreate(issuerOrigin, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/"
                || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || !Regex.IsMatch(uri.Host, "\\A[a-z0-9]+\\.execute-api\\." + Regex.Escape(awsRegion) + "\\.amazonaws\\.com\\z"))
                throw new RecordingFailure("invalid_issuer_origin");
            SteamAppId = steamAppId; AwsAccountId = awsAccountId; AwsRegion = awsRegion;
            IssuerOrigin = uri.AbsoluteUri; StreamNamePrefix = streamNamePrefix; UploadBucket = uploadBucket;
            IdentityPrefix = identityPrefix; IdentityDomain = identityDomain;
            streamPattern = new Regex("\\Aarn:aws:kinesisvideo:" + Regex.Escape(awsRegion) + ":" + awsAccountId
                + ":stream/(" + Regex.Escape(streamNamePrefix) + "[A-Za-z0-9_.-]{1,96})/[0-9]+\\z", RegexOptions.CultureInvariant);
        }

        private static bool Match(string value, string pattern) => value != null && Regex.IsMatch(value, "\\A" + pattern + "\\z", RegexOptions.CultureInvariant);
        public bool TryStream(string arn, out string streamName)
        {
            var match = streamPattern.Match(arn ?? "");
            streamName = match.Success && match.Groups[1].Length <= 128 ? match.Groups[1].Value : null;
            return streamName != null;
        }
        public bool IsUploadDestination(Uri uri) => uri != null && uri.Scheme == "https" && uri.IsDefaultPort
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && ((uri.Host == UploadBucket + ".s3." + AwsRegion + ".amazonaws.com" && uri.AbsolutePath == "/")
                || (uri.Host == "s3." + AwsRegion + ".amazonaws.com" && uri.AbsolutePath == "/" + UploadBucket));
    }
}
