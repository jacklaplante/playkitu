using Amazon;
using Amazon.DynamoDBv2;
using Amazon.KinesisVideo;
using Amazon.KinesisVideoArchivedMedia;
using Amazon.S3;
using Amazon.SecurityToken;
using Playkitu.Issuer;

namespace Playkitu.Retrieval;

// Both CLI and browser use the same verified, read-only owner boundary. No AWS
// credentials or deployment configuration are sent to the local browser.
public sealed class OwnerConnection : IDisposable
{
    private readonly List<IDisposable> clients = [];
    public IReadStore Store { get; private set; } = null!;
    public static async Task<OwnerConnection> OpenAsync(string profile, Deployment deployment, CancellationToken ct)
    {
        var owner = new OwnerConnection();
        try {
            var credentials = OwnerProfile.Load(profile);
            var region = RegionEndpoint.GetBySystemName(deployment.AwsRegion); var http = new BoundedHttpFactory();
            var sts = new AmazonSecurityTokenServiceClient(credentials, new AmazonSecurityTokenServiceConfig {
                RegionEndpoint = region, MaxErrorRetry = 0, IgnoreConfiguredEndpointUrls = true, HttpClientFactory = http });
            owner.clients.Add(sts); await OwnerProfile.VerifyAsync(sts, deployment, ct);
            var dynamo = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig {
                RegionEndpoint = region, MaxErrorRetry = 0, IgnoreConfiguredEndpointUrls = true, HttpClientFactory = http });
            owner.clients.Add(dynamo);
            var s3 = new AmazonS3Client(credentials, new AmazonS3Config {
                RegionEndpoint = region, MaxErrorRetry = 0, IgnoreConfiguredEndpointUrls = true, HttpClientFactory = http });
            owner.clients.Add(s3);
            var video = new AmazonKinesisVideoClient(credentials, new AmazonKinesisVideoConfig {
                RegionEndpoint = region, MaxErrorRetry = 0, IgnoreConfiguredEndpointUrls = true, HttpClientFactory = http });
            owner.clients.Add(video);
            owner.Store = new AwsReadStore(dynamo, s3, video, endpoint => new AmazonKinesisVideoArchivedMediaClient(credentials,
                new() { ServiceURL = endpoint, AuthenticationRegion = deployment.AwsRegion, MaxErrorRetry = 0,
                    IgnoreConfiguredEndpointUrls = true, HttpClientFactory = http }), deployment);
            return owner;
        } catch (RetrievalError) { owner.Dispose(); throw; }
        catch { owner.Dispose(); throw new RetrievalError("owner_login_failed_run_aws_login"); }
    }
    public void Dispose() { foreach (var client in clients) client.Dispose(); }
}
