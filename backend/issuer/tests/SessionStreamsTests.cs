using Amazon;
using Amazon.KinesisVideo;
using Amazon.KinesisVideo.Model;
using Amazon.Runtime;
using NUnit.Framework;
using Playkitu.Issuer;

namespace Issuer.Tests;

public sealed class MemoryVideo(Deployment deployment) : AmazonKinesisVideoClient(new BasicAWSCredentials("OFFLINE", "OFFLINE"),
    new AmazonKinesisVideoConfig { RegionEndpoint = RegionEndpoint.USEast2, HttpClientFactory = new BlockedFactory() })
{
    public StreamInfo? Info;
    public Dictionary<string, string> Tags = [];
    public List<CreateStreamRequest> Creates = [];
    public List<DeleteStreamRequest> Deletes = [];
    public bool LoseCreateResponse, LoseDeleteResponse;
    public override Task<DescribeStreamResponse> DescribeStreamAsync(DescribeStreamRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Info is null) throw new ResourceNotFoundException("offline absent");
        return Task.FromResult(new DescribeStreamResponse { StreamInfo = Info });
    }
    public override Task<ListTagsForStreamResponse> ListTagsForStreamAsync(ListTagsForStreamRequest request, CancellationToken ct = default) =>
        Task.FromResult(new ListTagsForStreamResponse { Tags = Tags });
    public override Task<CreateStreamResponse> CreateStreamAsync(CreateStreamRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Creates.Add(request); Tags = request.Tags;
        Info = new StreamInfo { StreamName = request.StreamName, StreamARN = $"arn:aws:kinesisvideo:{deployment.AwsRegion}:{deployment.AwsAccountId}:stream/{request.StreamName}/1234",
            MediaType = request.MediaType, DataRetentionInHours = request.DataRetentionInHours, Status = Status.CREATING, Version = "one" };
        if (LoseCreateResponse) { LoseCreateResponse = false; throw new HttpRequestException("offline uncertain create"); }
        return Task.FromResult(new CreateStreamResponse { StreamARN = Info.StreamARN });
    }
    public override Task<DeleteStreamResponse> DeleteStreamAsync(DeleteStreamRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Deletes.Add(request);
        if (Info?.StreamARN != request.StreamARN || request.CurrentVersion != Info.Version) throw new InvalidOperationException("Unfenced delete");
        Info = null;
        if (LoseDeleteResponse) { LoseDeleteResponse = false; throw new HttpRequestException("offline uncertain delete"); }
        return Task.FromResult(new DeleteStreamResponse());
    }
}

[TestFixture]
public sealed class SessionStreamsTests
{
    private const string Admission = "00112233445566778899aabbccddeeff";
    [Test] public async Task LostCreateResponseRecoversExactOwnedNameWithoutAnotherCreate()
    {
        var options = Playkitu.Tests.DeploymentFixture.Create(streamMode: "onDemand");
        using var aws = new MemoryVideo(options) { LoseCreateResponse = true }; var streams = new SessionStreams(aws, options);
        await Assert.ThrowsAsync<IssuerError>(async () => await streams.EnsureAsync(Admission, CancellationToken.None));
        Assert.That(await streams.EnsureAsync(Admission, CancellationToken.None), Is.Null);
        aws.Info!.Status = Status.ACTIVE;
        Assert.That(await streams.EnsureAsync(Admission, CancellationToken.None), Is.EqualTo(aws.Info.StreamARN));
        Assert.That(aws.Creates, Has.Count.EqualTo(1));
        Assert.That(aws.Creates[0].DataRetentionInHours, Is.EqualTo(720));
        Assert.That(aws.Tags["Admission"], Is.EqualTo(Admission));
    }
    [TestCase("account")][TestCase("name")][TestCase("tags")][TestCase("retention")][TestCase("media")]
    public async Task AnExistingUntrustedStreamIsNeverAdopted(string changed)
    {
        var options = Playkitu.Tests.DeploymentFixture.Create(streamMode: "onDemand");
        using var aws = new MemoryVideo(options); var streams = new SessionStreams(aws, options);
        await streams.EnsureAsync(Admission, CancellationToken.None); aws.Info!.Status = Status.ACTIVE;
        switch (changed) {
            case "account": aws.Info.StreamARN = aws.Info.StreamARN.Replace(options.AwsAccountId, "999999999999"); break;
            case "name": aws.Info.StreamName += "other"; break;
            case "tags": aws.Tags["Admission"] = new string('f', 32); break;
            case "retention": aws.Info.DataRetentionInHours = 1; break;
            case "media": aws.Info.MediaType = "video/other"; break;
        }
        await Assert.ThrowsAsync<IssuerError>(async () => await streams.EnsureAsync(Admission, CancellationToken.None));
        Assert.That(aws.Creates, Has.Count.EqualTo(1));
    }
    [Test] public async Task LostDeleteResponseIsIdempotentAndDeletionUsesVerifiedVersion()
    {
        var options = Playkitu.Tests.DeploymentFixture.Create(streamMode: "onDemand");
        using var aws = new MemoryVideo(options); var streams = new SessionStreams(aws, options);
        await streams.EnsureAsync(Admission, CancellationToken.None); var arn = aws.Info!.StreamARN; aws.LoseDeleteResponse = true;
        await Assert.ThrowsAsync<IssuerError>(async () => await streams.DeleteAsync(Admission, arn, CancellationToken.None));
        await streams.DeleteAsync(Admission, arn, CancellationToken.None);
        Assert.That(aws.Deletes, Has.Count.EqualTo(1)); Assert.That(aws.Deletes[0].CurrentVersion, Is.EqualTo("one"));
    }
    [Test] public async Task CleanupNeverDeletesWrongAdmissionOrChangedArn()
    {
        var options = Playkitu.Tests.DeploymentFixture.Create(streamMode: "onDemand");
        using var aws = new MemoryVideo(options); var streams = new SessionStreams(aws, options);
        await streams.EnsureAsync(Admission, CancellationToken.None);
        await Assert.ThrowsAsync<IssuerError>(async () => await streams.DeleteAsync(Admission, aws.Info!.StreamARN + "0", CancellationToken.None));
        aws.Tags["Admission"] = new string('f', 32);
        await Assert.ThrowsAsync<IssuerError>(async () => await streams.DeleteAsync(Admission, null, CancellationToken.None));
        Assert.That(aws.Deletes, Is.Empty);
    }
}
