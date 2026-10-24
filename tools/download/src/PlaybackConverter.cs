using System.Diagnostics;

namespace Playkitu.Retrieval;

public sealed class PlaybackConverter(string toolRoot, string? nativeRuntime) : IPlaybackConverter
{
    public async Task<bool> ConvertAsync(string input, string output, CancellationToken ct)
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(toolRoot, "prepare-playback.ps1"),
            "-InputFile", input, "-Output", output }) start.ArgumentList.Add(arg);
        if (nativeRuntime is not null) { start.ArgumentList.Add("-RuntimeDirectory"); start.ArgumentList.Add(nativeRuntime); }
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        using var process = new Process { StartInfo = start }; process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var reads = Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token),
            process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token));
        try { await process.WaitForExitAsync(timeout.Token); await reads; return process.ExitCode == 0 && File.Exists(output); }
        catch (OperationCanceledException) {
            if (!process.HasExited) process.Kill(true);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            ct.ThrowIfCancellationRequested(); return false;
        }
    }
}
