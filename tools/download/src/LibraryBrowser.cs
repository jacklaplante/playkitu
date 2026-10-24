using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Playkitu.Issuer;

namespace Playkitu.Retrieval;

public sealed class LibraryJob(Guid id, Guid session, string directory)
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public Guid Id { get; } = id;
    public Guid SessionId { get; } = session;
    public string Directory { get; } = directory;
    // Four-hour recordings can contain hundreds of bounded media parts. Keep
    // per-operation limits while allowing the owner's whole download to finish.
    public CancellationTokenSource Cancel { get; } = new(TimeSpan.FromHours(2));
    public string Progress { get; set; } = "Starting download";
    public Task? Work { get; set; }
    public PlaytestDownload? Result { get; set; }
}

public static class LibraryBrowser
{
    public static async Task RunAsync(PlaytestLibrary library, string output, bool open, CancellationToken ct, Action<string>? started = null)
    {
        if (!Path.IsPathFullyQualified(output)) throw new RetrievalError("absolute_library_directory_required");
        Directory.CreateDirectory(output);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        var token = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var jobs = new ConcurrentDictionary<Guid, LibraryJob>();
        // Saved downloads stay usable after closing/reopening the app. Only our
        // fixed library layout is served; a saved JSON path never grants access
        // to arbitrary files or symlinked directories on this computer.
        foreach (var directory in System.IO.Directory.EnumerateDirectories(output).Take(500)) {
            var name = Path.GetFileName(directory);
            if (name.Length != 65 || name[32] != '-' || !Guid.TryParseExact(name[..32], "N", out var session)
                || !Guid.TryParseExact(name[33..], "N", out var id)) continue;
            try {
                var manifest = RetrievalService.LocalFile(directory, "playtest.json");
                if (new FileInfo(manifest).Length > 2 * RetrievalLimits.MiB) continue;
                var result = JsonSerializer.Deserialize<PlaytestDownload>(File.ReadAllBytes(manifest), Wire.Json);
                if (result is null || result.SessionId != session || result.Parts.Count > 4096) continue;
                foreach (var part in result.Parts) {
                    _ = PlaytestLibrary.PlaybackFile(directory, part.Original);
                    if (part.Preview is not null) _ = PlaytestLibrary.PlaybackFile(directory, part.Preview);
                }
                jobs[id] = new(id, session, directory) { Result = result, Progress = result.Status, CreatedAt = File.GetLastWriteTimeUtc(manifest) };
            } catch { /* Invalid/unfinished downloads remain on disk, never served. */ }
        }
        var gate = new SemaphoreSlim(1); string origin = "";
        app.Use(async (context, next) => {
            // Bind loopback AND reject Host/Origin tricks. A fresh secret URL is
            // required even for media reads; no CORS or remote listening exists.
            if (origin.Length == 0 || context.Request.Host.Value != new Uri(origin).Authority || !context.Request.Path.StartsWithSegments(token)
                || context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin.ToString() != origin
                || context.Request.Method == "POST" && context.Request.Headers["X-Playkitu"] != "1") {
                context.Response.StatusCode = 403; return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; media-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'";
            try { await next(context); }
            catch (OperationCanceledException) { context.Response.StatusCode = 408; }
            catch (Exception error) {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = error is RetrievalError r ? r.Code : "read_failed_check_owner_login" }, Wire.Json);
            }
        });
        var resource = typeof(LibraryBrowser).Assembly.GetManifestResourceStream("Playkitu.Retrieval.Browser.html")!;
        using var reader = new StreamReader(resource); var html = await reader.ReadToEndAsync(ct);
        app.MapGet(token + "/", () => Results.Content(html, "text/html"));
        app.MapGet(token + "/logo", () => Results.Stream(typeof(LibraryBrowser).Assembly.GetManifestResourceStream("Playkitu.Logo.png")!, "image/png"));
        app.MapGet(token + "/sessions", async (string from, string to, string? cursor, CancellationToken cancel) => {
            if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
                || !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
                throw new RetrievalError("invalid_day");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromMinutes(2));
            return Results.Json(await library.ListAsync(first, last, deadline.Token, cursor), Wire.Json);
        });
        app.MapPost(token + "/download/{session:guid}", async (Guid session) => {
            if (session == Guid.Empty) return Results.BadRequest();
            if (!await gate.WaitAsync(0)) return Results.Conflict(new { error = "one_download_at_a_time" });
            var id = Guid.NewGuid(); var directory = Path.Combine(output, $"{session:N}-{id:N}");
            var job = new LibraryJob(id, session, directory); jobs[id] = job;
            job.Work = Task.Run(async () => {
                try { job.Result = await library.DownloadAsync(session, directory, text => job.Progress = text, job.Cancel.Token); }
                catch { job.Result = new() { SessionId = session, Status = "incomplete", Issues = ["download_failed"] }; }
                finally { job.Progress = job.Result?.Status ?? "incomplete"; gate.Release(); }
            }, CancellationToken.None);
            return Results.Json(new { jobId = id }, Wire.Json);
        });
        app.MapGet(token + "/jobs", () => Results.Json(jobs.Values.OrderBy(j => j.CreatedAt).Select(j => new { j.Id, j.SessionId, j.Progress,
            finished = j.Result is not null, result = j.Result, directory = j.Directory }), Wire.Json));
        app.MapPost(token + "/cancel/{id:guid}", (Guid id) => {
            if (!jobs.TryGetValue(id, out var job)) return Results.NotFound(); job.Cancel.Cancel(); return Results.Ok();
        });
        app.MapPost(token + "/folder/{id:guid}", (Guid id) => {
            if (!jobs.TryGetValue(id, out var job) || !Directory.Exists(job.Directory)) return Results.NotFound();
            Process.Start(new ProcessStartInfo(job.Directory) { UseShellExecute = true }); return Results.Ok();
        });
        app.MapGet(token + "/media/{id:guid}/{part:int}/{kind}", (Guid id, int part, string kind, HttpContext context) => {
            if (!jobs.TryGetValue(id, out var job) || job.Result is null || part < 0 || part >= job.Result.Parts.Count
                || kind is not ("original" or "preview")) return Results.NotFound();
            var item = job.Result.Parts[part]; var relative = kind == "original" ? item.Original : item.Preview;
            if (relative is null) return Results.NotFound();
            var file = PlaytestLibrary.PlaybackFile(job.Directory, relative);
            return Results.File(file, kind == "original" ? "video/x-matroska" : "video/mp4",
                fileDownloadName: context.Request.Query.ContainsKey("download") ? Path.GetFileName(file) : null, enableRangeProcessing: true);
        });
        await app.StartAsync(ct);
        origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var url = origin + token + "/";
        Console.WriteLine("playkitu library: " + url);
        Console.WriteLine("Downloads: " + output + " · close this terminal or press Ctrl+C to stop.");
        started?.Invoke(url);
        if (open) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        try { await app.WaitForShutdownAsync(ct); }
        finally {
            foreach (var job in jobs.Values) job.Cancel.Cancel();
            try { await Task.WhenAll(jobs.Values.Select(j => j.Work ?? Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
        }
    }
}
