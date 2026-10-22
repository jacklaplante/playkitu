using Playreel.Issuer;
namespace Playreel.Tests;

public static class DeploymentFixture
{
    public static IssuerOptions Create(string account = "123456789012", string region = "us-east-2", int app = 123456, string identityPrefix = "prrec2:", string identityDomain = "playreel-recording:") => new()
    {
        AwsAccountId = account, AwsRegion = region, SteamAppId = app,
        IdentityPrefix = identityPrefix, IdentityDomain = identityDomain,
        Project = "playreel", RolePath = "/playreel/", Table = "playreel",
        IssuerOrigin = $"https://abc123def4.execute-api.{region}.amazonaws.com",
        StreamNamePrefix = "playreel-slot-", Bucket = "playreel-offline",
        PublisherSecretArn = $"arn:aws:secretsmanager:{region}:{account}:secret:playreel/publisher-key-AbCd12",
        MediaRoleArn = $"arn:aws:iam::{account}:role/playreel/playreel-media",
        RetrievalRoleArn = $"arn:aws:iam::{account}:role/playreel/playreel-retrieval",
        StreamArns = Enumerable.Range(1, 4).Select(i => $"arn:aws:kinesisvideo:{region}:{account}:stream/playreel-slot-{i:D2}/1234").ToArray()
    };
}
