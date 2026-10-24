using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Playkitu.Client;
using Playkitu.Native;
using Playkitu.Producer;
using UnityEngine;

namespace Playkitu.Unity
{
    // The game owns authentication, menu flow and consent UI. Construction is
    // inert: observe its existing authentication owner only from Begin(consent).
    public sealed class UnityRecordingFactory : IRecordingFactory
    {
        private static int environmentLease;
        private readonly RecordingTrust trust;
        private readonly IRecordingTickets tickets;
        private readonly Func<uint> currentAppId;
        private readonly string runtime;
        private readonly RecordingRunProfile profile;
        private readonly Dictionary<string, string> previous = new Dictionary<string, string>();
        public bool IsLive => true;
        public UnityRecordingFactory(RecordingTrust trust, IRecordingTickets tickets, Func<uint> currentAppId,
            string runtimeDirectory, RecordingRunProfile profile)
        {
            this.trust = trust ?? throw new ArgumentNullException(nameof(trust));
            this.tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
            this.currentAppId = currentAppId ?? throw new ArgumentNullException(nameof(currentAppId));
            runtime = Path.GetFullPath(runtimeDirectory); this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }
        public RecordingDependencies CreateAfterConsent()
        {
            uint app;
            try { app = currentAppId(); } catch { throw new RecordingFailure("steam_unavailable"); }
            if (app != trust.SteamAppId) throw new RecordingFailure("steam_app_context_unavailable");
            if (Interlocked.CompareExchange(ref environmentLease, 1, 0) != 0) throw new RecordingFailure("recording_owner_busy");
            try {
                var values = new Dictionary<string, string> {
                    // The native capability requires this explicit immutable trust value;
                    // Restore retains it until the same native owner's retirement completes.
                    ["PLAYREEL_AWS_REGION"] = trust.AwsRegion,
                    ["PATH"] = Path.Combine(runtime, "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                    ["GST_PLUGIN_PATH"] = "", ["GST_PLUGIN_PATH_1_0"] = "",
                    ["GST_PLUGIN_SYSTEM_PATH"] = Path.Combine(runtime, "plugins"), ["GST_PLUGIN_SYSTEM_PATH_1_0"] = Path.Combine(runtime, "plugins"),
                    ["GST_PLUGIN_SCANNER"] = Path.Combine(runtime, "bin", "gst-plugin-scanner.exe"), ["GST_PLUGIN_SCANNER_1_0"] = Path.Combine(runtime, "bin", "gst-plugin-scanner.exe"),
                    ["GST_REGISTRY"] = Path.Combine(Application.temporaryCachePath, "playkitu-" + Guid.NewGuid().ToString("N") + ".bin"),
                    ["GST_DEBUG"] = "0", ["GST_DEBUG_FILE"] = "", ["GST_TRACERS"] = "" };
                foreach (var pair in values) { previous.Add(pair.Key, Environment.GetEnvironmentVariable(pair.Key)); Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
                return new RecordingDependencies {
                    Trust = trust, RuntimeSteamApp = RuntimeSteamAppObservation.Match,
                    Issuer = new UnityRecordingTransport(trust), Tickets = tickets, Uploads = new UnityRecordingUploadTransport(),
                    Runner = session => new NativeRecordingRunner(session, new RecordingClock(), new ProductionNativeRun(runtime, external: true, profile: profile),
                        Path.Combine(runtime, "certs", "cert.pem"), new RecordingMediaSettings(true, 14400, (uint)profile.Width, (uint)profile.Height, 4UL * 1024 * 1024 * 1024, profile)),
                    Release = Restore
                };
            } catch { Restore(); throw; }
        }
        private void Restore()
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            previous.Clear(); Interlocked.Exchange(ref environmentLease, 0);
        }
    }
}
