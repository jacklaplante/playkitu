using Amazon.Runtime;
using System.Net;

namespace Playreel.Retrieval;

// Cap SDK metadata before JSON unmarshalling as well as payloads later copied by
// the service. Verified HTTPS and zero redirects are the only production path.
public sealed class BoundedHttpFactory(Func<HttpMessageHandler>? handlerFactory = null) : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig config) => new(new BoundHandler(handlerFactory is null ? new SocketsHttpHandler
    { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5), MaxConnectionsPerServer = 2 } : handlerFactory())) { Timeout = Timeout.InfiniteTimeSpan };
    public override bool UseSDKHttpClientCaching(IClientConfig config) => false;
    public override bool DisposeHttpClientsAfterUse(IClientConfig config) => true;

    private sealed class BoundHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if ((int)response.StatusCode is >= 300 and < 400) { response.Dispose(); throw new RetrievalError("redirect_rejected"); }
            var media = response.Content.Headers.ContentType?.MediaType ?? "";
            var maximum = media.Contains("json", StringComparison.OrdinalIgnoreCase) || media.Contains("xml", StringComparison.OrdinalIgnoreCase)
                ? 1024 * 1024L : 64 * RetrievalLimits.MiB;
            if (response.Content.Headers.ContentLength > maximum) { response.Dispose(); throw new RetrievalError("response_limit"); }
            response.Content = new BoundContent(response.Content, maximum);
            return response;
        }
    }
    private sealed class BoundContent : HttpContent
    {
        private readonly HttpContent inner; private readonly long maximum;
        public BoundContent(HttpContent inner, long maximum)
        {
            this.inner = inner; this.maximum = maximum;
            foreach (var h in inner.Headers) Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        protected override bool TryComputeLength(out long length) { length = inner.Headers.ContentLength ?? 0; return inner.Headers.ContentLength.HasValue; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { using var input = await CreateContentReadStreamAsync(); await input.CopyToAsync(stream); }
        protected override async Task<Stream> CreateContentReadStreamAsync() => new BoundStream(await inner.ReadAsStreamAsync(), maximum);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => new BoundStream(await inner.ReadAsStreamAsync(ct), maximum);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class BoundStream(Stream inner, long maximum) : Stream
    {
        private long count;
        private int Charge(int n) { count = checked(count + n); if (count > maximum) throw new RetrievalError("response_limit"); return n; }
        public override int Read(byte[] buffer, int offset, int length) => Charge(inner.Read(buffer, offset, length));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Charge(await inner.ReadAsync(buffer, ct));
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int length) => throw new NotSupportedException();
    }
}
