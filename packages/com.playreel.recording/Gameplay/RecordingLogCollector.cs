using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Playreel.Client;
using UnityEngine;

namespace Playreel.Gameplay
{
    // Subscribe only after disclosure/consent. No historical Player.log/Editor.log
    // fallback; timestamps describe callback receipt, not original message emission.
    public sealed class RecordingLogCollector : IDisposable
    {
        private readonly object sync = new object();
        private struct Line { public string Text; public long Ticks; }
        private readonly Queue<Line> lines = new Queue<Line>(64);
        private int dropped, truncated, totalDropped;
        private volatile bool active, stopped;
        public bool Collecting => active;
        public int DroppedCount => Volatile.Read(ref totalDropped);
        private static readonly Regex Sensitive = new Regex(@"(?i)(authorization|bearer|password|passwd|secret|token|ticket|credential|jwt|steam.?id|username|email|access.?key)|eyJ[A-Za-z0-9_-]{12,}\.|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));
        private static readonly Regex Location = new Regex(@"https?://[^\s]+|[A-Za-z]:[\\/][^\r\n]*|/(?:Users|home)/[^\r\n]*", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));
        public static string Redact(string line)
        {
            try { return Sensitive.IsMatch(line) ? "[redacted sensitive diagnostic]" : Location.Replace(line, "[redacted location]"); }
            catch (RegexMatchTimeoutException) { return "[redacted diagnostic]"; }
        }
        public void Begin() { lock (sync) { if (active || stopped) return; active = true; Application.logMessageReceivedThreaded += Receive; } }
        private void Receive(string message, string stack, LogType type)
        {
            if (!active) return;
            if (!Monitor.TryEnter(sync)) { Drop(1); return; }
            try {
                if (!active) return;
                if (lines.Count == 64) { Drop(1); return; }
                bool shortened = (message?.Length ?? 0) > 1536 || (stack?.Length ?? 0) > 384;
                string text = type + " " + (message == null ? "" : message.Substring(0, Math.Min(1536, message.Length))) + "\n"
                    + (stack == null ? "" : stack.Substring(0, Math.Min(384, stack.Length)));
                if (shortened) Interlocked.Increment(ref truncated);
                lines.Enqueue(new Line { Text = text, Ticks = System.Diagnostics.Stopwatch.GetTimestamp() });
            } finally { Monitor.Exit(sync); }
        }
        public void Drain(RecordingBatchUploader uploader) => Drain(uploader, 8);
        private void Drain(RecordingBatchUploader uploader, int budget)
        {
            if (uploader == null) return;
            for (int i = 0; i < budget; i++) {
                Line line;
                lock (sync) { if (lines.Count == 0) break; line = lines.Dequeue(); }
                var bytes = Encoding.UTF8.GetBytes("[callbackManagedTicks=" + line.Ticks + " frequency=" + System.Diagnostics.Stopwatch.Frequency + "] " + Redact(line.Text) + "\n");
                if (!uploader.AppendLog(bytes)) Drop(1);
            }
            int lost = Interlocked.Exchange(ref dropped, 0), shortened = Interlocked.Exchange(ref truncated, 0);
            if (lost != 0 || shortened != 0) uploader.AppendLog(Encoding.UTF8.GetBytes("Recording diagnostic collector: dropped=" + lost + " truncated=" + shortened + "\n"));
        }
        private void Drop(int count) { Interlocked.Add(ref dropped, count); Interlocked.Add(ref totalDropped, count); }
        public void StopAndDrain(RecordingBatchUploader uploader)
        {
            // End/quit first fences threaded admission, then retains at most the
            // existing 64 queued callbacks through the same redaction/AppendLog path.
            // In-flight Receive checks active again under the same queue lock.
            lock (sync) { stopped = true; active = false; }
            Application.logMessageReceivedThreaded -= Receive;
            if (uploader != null) Drain(uploader, 64);
            else lock (sync) { Drop(lines.Count); lines.Clear(); }
        }
        public void Dispose() => StopAndDrain(null);
    }
}
