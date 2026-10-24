using Amazon;
using Amazon.DynamoDBv2;
using Amazon.KinesisVideo;
using Amazon.KinesisVideoArchivedMedia;
using Amazon.S3;
using Amazon.SecurityToken;
using Playkitu.Issuer;
using System.Globalization;
using System.Text.Json;

namespace Playkitu.Retrieval;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is not ("browse" or "list" or "fetch" or "metadata" or "inspect" or "map" or "locate-log")) throw new RetrievalError("usage_browse_list_fetch_metadata_inspect_map_locate_log");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i += 2)
                if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[i + 1])) throw new RetrievalError("invalid_arguments");
            var allowed = args[0] switch {
                "browse" => new[] { "--profile", "--deployment", "--output", "--tool-root", "--native-runtime", "--no-open" },
                "list" => new[] { "--day", "--profile", "--deployment", "--limit", "--tool-root", "--native-runtime" },
                "fetch" => new[] { "--session", "--profile", "--deployment", "--output", "--tool-root", "--native-runtime", "--metadata-page", "--producer-from-utc", "--producer-to-utc" },
                "metadata" => new[] { "--session", "--profile", "--deployment", "--output", "--tool-root", "--native-runtime", "--previous-page" },
                "inspect" => new[] { "--directory", "--tool-root", "--native-runtime" },
                "locate-log" => new[] { "--directory", "--log-file", "--line", "--byte-offset", "--output", "--tool-root", "--native-runtime" },
                _ => new[] { "--directory", "--timestamp", "--elapsed-ns", "--tool-root", "--native-runtime" } };
            if (options.Keys.Any(k => !allowed.Contains(k))) throw new RetrievalError("unknown_option");
            string Required(string key) => options.TryGetValue(key, out var value) && value.Length > 0 ? value : throw new RetrievalError("missing_argument");
            var nativeRuntime = options.GetValueOrDefault("--native-runtime");
            // An explicitly selected decoder must never silently fall back to the
            // default runtime, including local commands with no AWS access.
            if (nativeRuntime is not null && (!Path.IsPathFullyQualified(nativeRuntime)
                || !File.Exists(Path.Combine(nativeRuntime, "bin", "native-media-probe.exe"))))
                throw new RetrievalError("invalid_native_runtime");
            if (args[0] == "browse") {
                var output = Required("--output");
                if (!Path.IsPathFullyQualified(output)) throw new RetrievalError("absolute_library_directory_required");
                using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var owner = await OwnerConnection.OpenAsync(Required("--profile"), Deployment.Load(Required("--deployment")), verification.Token);
                var tool = Required("--tool-root");
                await LibraryBrowser.RunAsync(new PlaytestLibrary(owner.Store, new ProbeProcess(tool, nativeRuntime),
                    new PlaybackConverter(tool, nativeRuntime)), output, options.GetValueOrDefault("--no-open") != "true", CancellationToken.None);
                return 0;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(300));
            var ct = deadline.Token;
            if (args[0] == "locate-log")
            {
                var navigation = await LogNavigation.LocateAsync(Required("--directory"), Required("--log-file"),
                    options.GetValueOrDefault("--line"), options.GetValueOrDefault("--byte-offset"), Required("--output"),
                    new ProbeProcess(Required("--tool-root"), nativeRuntime), ct);
                Print(navigation); return navigation.Status == "estimated" ? 0 : 3;
            }
            if (args[0] == "inspect") { Print(await LocalBundle.InspectAsync(Required("--directory"), ct)); return 0; }
            if (args[0] == "map")
            {
                var mapping = await LocalBundle.MapAsync(Required("--directory"), options.GetValueOrDefault("--timestamp"), options.GetValueOrDefault("--elapsed-ns"), ct);
                Print(mapping); return mapping.Status == "mapped" ? 0 : 3;
            }

            // Validate command inputs before constructing any credential provider.
            DateOnly day = default; Guid session = default; var count = 20; ProducerWindow? window = null;
            if (args[0] == "list")
            {
                if (!DateOnly.TryParseExact(Required("--day"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) throw new RetrievalError("invalid_day");
                if (options.TryGetValue("--limit", out var text) && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count is < 1 or > 50)) throw new RetrievalError("invalid_list_limit");
            }
            else
            {
                if (!Guid.TryParseExact(Required("--session"), "D", out session) || session == Guid.Empty) throw new RetrievalError("invalid_session_id");
                var output = Required("--output");
                if (!Path.IsPathFullyQualified(output) || Directory.Exists(output) || File.Exists(output)) throw new RetrievalError("new_absolute_output_required");
                _ = Required("--tool-root");
                if (args[0] == "metadata" && options.TryGetValue("--previous-page", out var previous)
                    && (!Path.IsPathFullyQualified(previous) || !Directory.Exists(previous))) throw new RetrievalError("metadata_page_directory_required");
                if (args[0] == "fetch" && new[] { "--metadata-page", "--producer-from-utc", "--producer-to-utc" }.Any(options.ContainsKey)) {
                    var page = Required("--metadata-page");
                    if (!Path.IsPathFullyQualified(page) || !Directory.Exists(page)) throw new RetrievalError("metadata_page_directory_required");
                    window = ProducerWindow.Parse(Required("--producer-from-utc"), Required("--producer-to-utc"));
                }
            }
            var deployment = Deployment.Load(Required("--deployment"));
            using var connection = await OwnerConnection.OpenAsync(Required("--profile"), deployment, ct);
            var store = connection.Store;
            var service = new RetrievalService(store, new ProbeProcess(options.GetValueOrDefault("--tool-root") ?? AppContext.BaseDirectory, nativeRuntime));
            if (args[0] == "list") { Print(new { sessions = await service.ListAsync(day, count, ct), limit = count, indexConsistency = "eventual" }); return 0; }
            if (args[0] == "metadata") {
                var page = await new MetadataCatalog(store, new ProbeProcess(Required("--tool-root"), nativeRuntime))
                    .PageAsync(session, Required("--output"), options.GetValueOrDefault("--previous-page"), ct);
                Print(new { page.Status, page.SessionId, objects = page.Summaries.Count, page.EnumerationComplete, page.AtomicSnapshot, page.SnapshotMeaning });
                return page.Status == "retrieved" ? 0 : 3;
            }
            var bundle = window is null ? await service.FetchAsync(session, Required("--output"), ct)
                : await new MetadataCatalog(store, new ProbeProcess(Required("--tool-root"), nativeRuntime))
                    .FetchWindowAsync(session, Required("--metadata-page"), Required("--output"), window, ct);
            Print(new { bundle.Status, bundle.SessionId, bundle.Issues, bundle.DownloadedBytes, coverage = "unverified", playbackOffsetVerified = false });
            return bundle.Status == "retrieved" ? 0 : 3;
        }
        catch (OperationCanceledException) { Print(new { status = "incomplete", code = "deadline_or_cancelled" }); return 124; }
        catch (RetrievalError e) { Print(new { status = "failed", code = e.Code }); return 2; }
        catch (Exception) { Print(new { status = "failed", code = "read_or_input_failed" }); return 2; }
    }
    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Wire.Json));
}
