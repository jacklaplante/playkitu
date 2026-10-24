using Playkitu.Issuer;
namespace Playkitu.Tests;

public static class DeploymentFixture
{
    public static IssuerOptions Create(string account = "123456789012", string region = "us-east-2", int app = 123456, string identityPrefix = "pkrec2:", string identityDomain = "playkitu-recording:") => new()
    {
        AwsAccountId = account, AwsRegion = region, SteamAppId = app,
        IdentityPrefix = identityPrefix, IdentityDomain = identityDomain,
        Project = "playkitu", RolePath = "/playkitu/", Table = "playkitu",
        IssuerOrigin = $"https://abc123def4.execute-api.{region}.amazonaws.com",
        StreamNamePrefix = "playkitu-slot-", Bucket = "playkitu-offline",
        PublisherSecretArn = $"arn:aws:secretsmanager:{region}:{account}:secret:playkitu/publisher-key-AbCd12",
        MediaRoleArn = $"arn:aws:iam::{account}:role/playkitu/playkitu-media",
        RetrievalRoleArn = $"arn:aws:iam::{account}:role/playkitu/playkitu-retrieval",
        StreamArns = Enumerable.Range(1, 4).Select(i => $"arn:aws:kinesisvideo:{region}:{account}:stream/playkitu-slot-{i:D2}/1234").ToArray()
    };
}
