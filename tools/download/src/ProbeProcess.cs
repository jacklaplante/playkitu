using System.Diagnostics;

namespace Playkitu.Retrieval;

public sealed class ProbeProcess(string toolRoot, string? nativeRuntime = null) : IMediaProbe
{
    public async Task<int> RunAsync(string input, string output, CancellationToken ct, IReadOnlyList<ulong>? selectedTargets = null)
    {
        var targets = ProbeEvidence.ValidateTargets(selectedTargets);
        var script = Path.Combine(Path.GetFullPath(toolRoot), "probe-media.ps1");
        if (!File.Exists(script)) throw new RetrievalError("probe_tool_unavailable");
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // -File cannot bind a multi-element string[] from ProcessStartInfo.
        // Invoke the unchanged wrapper with an actual PS array. Paths are
        // single-quoted literals (embedded quotes doubled); targets are already
        // bounded integers. No credential material enters command arguments.
        var paths = new[] { script, Path.GetFullPath(input), Path.GetFullPath(output) }.Select(p => "'" + p.Replace("'", "''", StringComparison.Ordinal) + "'").ToArray();
        var command = $"& {paths[0]} -InputFile {paths[1]} -Output {paths[2]}";
        if (nativeRuntime is not null) {
            if (!Path.IsPathFullyQualified(nativeRuntime) || !Directory.Exists(nativeRuntime)) throw new RetrievalError("invalid_native_runtime");
            command += " -RuntimeDirectory '" + nativeRuntime.Replace("'", "''", StringComparison.Ordinal) + "'";
        }
        if (targets.Length > 0) command += " -SelectedVideoPtsNs @(" + string.Join(',', targets.Select(n => "'" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "'")) + ") -ToleranceNs 1000000";
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)) }) start.ArgumentList.Add(arg);
        // The decoder needs no owner credentials. Its existing wrapper supplies
        // the private GStreamer environment and its own 35s process watchdog.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(stdout, stderr);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            // Do not block indefinitely on a decoder/OS teardown after timeout.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            throw;
        }
    }
}
