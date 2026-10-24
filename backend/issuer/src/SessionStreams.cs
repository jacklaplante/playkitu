using Amazon.KinesisVideo;
using Amazon.KinesisVideo.Model;

namespace Playkitu.Issuer;

public sealed class SessionStreams(IAmazonKinesisVideo client, Deployment deployment) : ISessionStreams
{
    private async Task<StreamInfo?> DescribeAsync(string admissionId, CancellationToken ct)
    {
        try {
            var response = await client.DescribeStreamAsync(new DescribeStreamRequest {
                StreamName = deployment.SessionStreamName(admissionId) }, ct);
            var info = response.StreamInfo;
            if (info is null || info.StreamName != deployment.SessionStreamName(admissionId)
                || !deployment.IsSessionStream(info.StreamARN, admissionId)) throw new IssuerError(503, "stream_binding_mismatch");
            var tags = await client.ListTagsForStreamAsync(new ListTagsForStreamRequest { StreamARN = info.StreamARN }, ct);
            if (tags.Tags is null || tags.Tags.GetValueOrDefault("Project") != deployment.Project
                || tags.Tags.GetValueOrDefault("ManagedBy") != "playkitu" || tags.Tags.GetValueOrDefault("Admission") != admissionId)
                throw new IssuerError(503, "stream_ownership_mismatch");
            return info;
        } catch (ResourceNotFoundException) { return null; }
    }
    public async Task<string?> EnsureAsync(string admissionId, CancellationToken ct)
    {
        try {
            var info = await DescribeAsync(admissionId, ct);
            if (info is null) {
                try {
                    var created = await client.CreateStreamAsync(new CreateStreamRequest {
                        StreamName = deployment.SessionStreamName(admissionId), MediaType = "video/h264", DataRetentionInHours = 720,
                        Tags = new() { ["Project"] = deployment.Project, ["ManagedBy"] = "playkitu", ["Admission"] = admissionId } }, ct);
                    if (!deployment.IsSessionStream(created.StreamARN, admissionId)) throw new IssuerError(503, "stream_binding_mismatch");
                } catch (ResourceInUseException) { /* Another retry owns this same admission name; inspect on the next poll. */ }
                return null;
            }
            if (info.DataRetentionInHours != 720 || info.MediaType != "video/h264") throw new IssuerError(503, "stream_configuration_mismatch");
            return info.Status == Status.ACTIVE ? info.StreamARN : null;
        } catch (Amazon.Runtime.AmazonServiceException) { throw new IssuerError(503, "stream_provisioning_unavailable", 2); }
        catch (HttpRequestException) { throw new IssuerError(503, "stream_provisioning_unavailable", 2); }
    }
    public async Task DeleteAsync(string admissionId, string? expectedArn, CancellationToken ct)
    {
        try {
            var info = await DescribeAsync(admissionId, ct);
            if (info is null) return; // Recover a lost successful delete response.
            if (expectedArn is not null && info.StreamARN != expectedArn) throw new IssuerError(503, "stream_binding_mismatch");
            if (string.IsNullOrEmpty(info.Version)) throw new IssuerError(503, "stream_version_unavailable");
            await client.DeleteStreamAsync(new DeleteStreamRequest { StreamARN = info.StreamARN, CurrentVersion = info.Version }, ct);
        } catch (ResourceNotFoundException) { }
        catch (Amazon.Runtime.AmazonServiceException) { throw new IssuerError(503, "stream_cleanup_unavailable"); }
        catch (HttpRequestException) { throw new IssuerError(503, "stream_cleanup_unavailable"); }
    }
}
