using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.SecretsManager;
using Amazon.SecurityToken;
using Amazon.KinesisVideo;
using Amazon.SimpleNotificationService;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
namespace Playkitu.Issuer;

public sealed class Function
{
    private readonly Lazy<IssuerService> service;
    private readonly TimeProvider clock;
    public Function() { clock = TimeProvider.System; service = new(CreateProduction); }
    public Function(IssuerService issuer, TimeProvider time) { clock = time; service = new(() => issuer); }

    public async Task<APIGatewayHttpApiV2ProxyResponse> HandleAsync(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext? context)
    {
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource();
        var budget = context is null ? TimeSpan.FromSeconds(8) : context.RemainingTime - TimeSpan.FromMilliseconds(250);
        if (budget <= TimeSpan.Zero) {
            if (request.Version == "playkitu.cleanup.v1") throw new InvalidOperationException("Scheduled recording cleanup did not complete.");
            return Reply(503, new { code = "issuer_timeout" }, 2);
        }
        var ceiling = TimeSpan.FromSeconds(request.Version == "playkitu.cleanup.v1" ? 55 : 8);
        budget = budget < ceiling ? budget : ceiling;
        deadline.CancelAfter(budget);
        // A scheduled failure must be a Lambda error so the service error alarm
        // fires. Returning an API-style 503 would count as a successful invocation.
        // API Gateway fixes Version=2.0; a public body cannot choose this event.
        if (request.Version == "playkitu.cleanup.v1") {
            try { return Reply(200, await service.Value.CleanupAsync(deadline.Token), null); }
            catch { throw new InvalidOperationException("Scheduled recording cleanup did not complete."); }
        }
        try
        {
            Wire.Require(request.Version == "2.0" && request.RequestContext?.Http is not null, "invalid_gateway_event");
            var method = request.RequestContext!.Http.Method; var path = request.RawPath;
            object data;
            if (method == "GET" && path == "/v1/config") data = await service.Value.ConfigAsync(deadline.Token);
            else if (method == "POST" && path == "/v1/sessions")
                data = await service.Value.StartAsync(Read<StartRequest>(request), deadline.Token);
            else if (method == "POST" && path is not null)
            {
                var parts = path.Split('/');
                Wire.Require(parts.Length == 5 && parts[1] == "v1" && parts[2] == "sessions" && Guid.TryParseExact(parts[3], "D", out _));
                var id = Guid.Parse(parts[3]);
                var authorizationHeaders = request.Headers?.Where(p => p.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
                var header = authorizationHeaders.Length == 1 ? authorizationHeaders[0].Value : null;
                if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal) || !Wire.ValidProof(header[7..])) throw new IssuerError(401, "invalid_session_proof");
                data = parts[4] switch
                {
                    "authorize" => await service.Value.AuthorizeAsync(id, header[7..], Read<AuthorizeRequest>(request), deadline.Token, budget - elapsed.Elapsed),
                    "close" => await service.Value.CloseAsync(id, header[7..], Read<CloseRequest>(request), deadline.Token),
                    _ => throw new IssuerError(404, "route_not_found")
                };
            }
            else throw new IssuerError(404, "route_not_found");
            var pending = data is StartResult { State: "pending" };
            return Reply(pending ? 202 : data is StartResult ? 201 : 200, data, pending ? 2 : null);
        }
        catch (IssuerError error)
        {
            var data = new Dictionary<string, object?> { ["code"] = error.Code };
            // B2p admission diagnostics are optional protocol-v1 annotations. Only this
            // exact existing retry response may carry the five static authentication
            // categories; neither arbitrary exception text nor Valve strings are emitted.
            string? failure = error.Status == 401 && error.Code == "fresh_ticket_required" ? error.SteamAuthFailure switch
            {
                SteamAuthFailure.MissingResponse => "missing_response",
                SteamAuthFailure.ValveError => "valve_error",
                SteamAuthFailure.MissingParams => "missing_params",
                SteamAuthFailure.ResultNotOk => "result_not_ok",
                SteamAuthFailure.MissingSteamId => "missing_steamid",
                _ => null
            } : null;
            if (failure is not null) data["steamAuthFailure"] = failure;
            if (failure == "valve_error" && error.ValveErrorCode is >= 0 and <= 65535)
                data["valveErrorCode"] = error.ValveErrorCode.Value;
            if (failure == "valve_error")
            {
                // B2q adds bounded lexical observations only. Emit constants from the
                // shared vocabulary, in static order; never serialize an upstream string.
                if (error.ValveErrorTerms is { } terms)
                    data["valveErrorTerms"] = SteamRejectionDiagnostics.Terms.Where(t => (terms & t.Term) != 0).Select(t => t.Name).ToArray();
                string? shape = error.PublisherKeyShape switch
                {
                    PublisherKeyShape.ExpectedShape => "expected_shape",
                    PublisherKeyShape.UnexpectedShape => "unexpected_shape",
                    _ => null
                };
                if (shape is not null) data["publisherKeyShape"] = shape;
            }
            return Reply(error.Status, data, error.RetryAfter);
        }
        catch (JsonException) { return Reply(400, new { code = "invalid_json" }); }
        catch (FormatException) { return Reply(400, new { code = "invalid_request" }); }
        catch (OperationCanceledException) { return Reply(503, new { code = "issuer_timeout" }, 2); }
        catch (Exception)
        {
            // Deliberately no exception interpolation: upstream exceptions can contain Valve
            // ticket URLs and SDK authorization details. Only this fixed diagnostic is logged.
            context?.Logger.LogLine("Recording issuer request failed; sensitive details omitted.");
            return Reply(503, new { code = "issuer_unavailable" }, 2);
        }
    }
    private APIGatewayHttpApiV2ProxyResponse Reply(int status, object data, int? retry = null)
    {
        var json = JsonSerializer.SerializeToElement(data, Wire.Json);
        var body = new Dictionary<string, object?> { ["protocolVersion"] = 1, ["serverUtc"] = clock.GetUtcNow() };
        foreach (var property in json.EnumerateObject()) body[property.Name] = property.Value;
        var headers = new Dictionary<string, string> { ["content-type"] = "application/json", ["cache-control"] = "no-store" };
        if (retry is { } seconds) headers["retry-after"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new() { StatusCode = status, Headers = headers, Body = JsonSerializer.Serialize(body, Wire.Json), IsBase64Encoded = false };
    }
    private static T Read<T>(APIGatewayHttpApiV2ProxyRequest request)
    {
        Wire.Require(request.Body is not null && request.Body.Length <= 24000, "request_too_large");
        var bytes = request.IsBase64Encoded ? Convert.FromBase64String(request.Body!) : Encoding.UTF8.GetBytes(request.Body!);
        Wire.Require(bytes.Length <= 16384, "request_too_large");
        return JsonSerializer.Deserialize<T>(bytes, Wire.Json) ?? throw new IssuerError(400, "invalid_json");
    }
    private static IssuerService CreateProduction()
    {
        // Configuration validation precedes construction of AWS clients. Offline tests never
        // invoke this factory; their clients use explicit fake credentials and blocked transports.
        string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Missing issuer configuration.");
        var options = JsonSerializer.Deserialize<IssuerOptions>(Required("PLAYKITU_DEPLOYMENT"), Wire.Json)
            ?? throw new InvalidOperationException("Missing issuer configuration.");
        options.Validate();
        AWSConfigs.LoggingConfig.LogTo = LoggingOptions.None;
        var region = RegionEndpoint.GetBySystemName(options.AwsRegion);
        var notificationTopic = Environment.GetEnvironmentVariable("PLAYKITU_NOTIFICATION_TOPIC_ARN");
        ISessionNotifier? notifier = null;
        if (!string.IsNullOrEmpty(notificationTopic)) {
            SnsSessionNotifier.ValidateTopic(notificationTopic, options);
            notifier = new SnsSessionNotifier(new AmazonSimpleNotificationServiceClient(new AmazonSimpleNotificationServiceConfig {
                RegionEndpoint = region, MaxErrorRetry = 0, IgnoreConfiguredEndpointUrls = true }), notificationTopic);
        }
        var dynamo = new AmazonDynamoDBClient(new AmazonDynamoDBConfig { RegionEndpoint = region, MaxErrorRetry = 0, IgnoreConfiguredEndpointUrls = true });
        var sts = new AmazonSecurityTokenServiceClient(new AmazonSecurityTokenServiceConfig { RegionEndpoint = region, MaxErrorRetry = 0, IgnoreConfiguredEndpointUrls = true });
        var secrets = new AmazonSecretsManagerClient(new AmazonSecretsManagerConfig { RegionEndpoint = region, MaxErrorRetry = 0, IgnoreConfiguredEndpointUrls = true });
        var s3 = new AmazonS3Client(new AmazonS3Config { RegionEndpoint = region, MaxErrorRetry = 0, IgnoreConfiguredEndpointUrls = true });
        var time = TimeProvider.System;
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(2), MaxConnectionsPerServer = 4 }) { Timeout = Timeout.InfiniteTimeSpan };
        return new(new(dynamo, options.Table), new ValveVerifier(http, new(secrets, options.PublisherSecretArn, time), options.SteamAppId),
            new StsMediaIssuer(sts, options.MediaRoleArn), new S3UploadSigner(s3, options.Bucket), options, time,
            options.OnDemand ? new SessionStreams(new AmazonKinesisVideoClient(new AmazonKinesisVideoConfig {
                RegionEndpoint = region, MaxErrorRetry = 0, IgnoreConfiguredEndpointUrls = true }), options) : null, notifier);
    }
}
