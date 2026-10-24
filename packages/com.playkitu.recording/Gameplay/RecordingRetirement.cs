using System;
using System.Threading;
using Playkitu.Client;
using Playkitu.Producer;
using UnityEngine;

namespace Playkitu.Gameplay
{
    // Exists only after an explicitly started host is disabled/destroyed. This
    // enabled main-thread pump survives that object; it cannot begin a session.
    internal sealed class RecordingRetirement : MonoBehaviour
    {
        internal sealed class Completion
        {
            private readonly RecordingController controller;
            private readonly RecordingHealthFile health;
            private readonly IRecordingClock clock;
            private readonly double deadline;
            private RecordingHealthMedia media;
            private string failure;
            private bool complete, destroyed;
            private int released;
            private double nextHealth;
            internal RecordingControllerState State => controller.State;
            internal bool Complete => complete;
            internal bool OwnershipReleased => complete && Volatile.Read(ref released) != 0;
            internal RecordingHealthSnapshot FinalSnapshot { get; private set; }
            internal Completion(RecordingController controller, RecordingHealthFile health, IRecordingClock clock,
                double grace, RecordingHealthMedia media, string failure, double? originalQuitDeadline)
            {
                this.controller = controller; this.health = health; this.clock = clock; this.media = media; this.failure = failure;
                if (double.IsNaN(grace) || double.IsInfinity(grace)) grace = RecordingQuitGate.DefaultGraceSeconds;
                deadline = originalQuitDeadline ?? (clock.Seconds + Math.Max(0, Math.Min(RecordingQuitGate.MaximumGraceSeconds, grace)));
                if (double.IsNaN(deadline) || double.IsInfinity(deadline)) deadline = clock.Seconds;
            }
            internal void MarkOwnerDestroyed() { destroyed = true; }
            private void Publish()
            {
                RecordingHealthProjection.ReadNative(ref media, controller.Runner as NativeRecordingRunner);
                FinalSnapshot = RecordingHealthSnapshot.Capture(controller, controller.Dependencies?.RuntimeSteamApp,
                    true, destroyed, true, false, media, failure);
                health?.Publish(FinalSnapshot);
            }
            internal void Tick()
            {
                if (complete) return;
                try { controller.Tick(); }
                catch { failure = failure ?? "recording_unavailable"; }
                if (controller.State == RecordingControllerState.Finished) { Finish(); return; }
                if (double.IsNaN(clock.Seconds) || double.IsInfinity(clock.Seconds) || clock.Seconds >= deadline) {
                    failure = failure ?? "recording_unavailable"; Finish(); return;
                }
                if (clock.Seconds >= nextHealth) { nextHealth = clock.Seconds + 1; Publish(); }
            }
            internal void Finish()
            {
                if (complete) return;
                complete = true;
                try { Publish(); }
                finally {
                    // Disposal ends managed HTTP/upload work. BUSY native code
                    // retains its DLL/environment lease; never force-unload it.
                    try { controller.Dispose(); }
                    finally {
                        var dependencies = controller.Dependencies;
                        if (controller.Runner == null || controller.Runner.Released) {
                            dependencies?.Dispose(); Volatile.Write(ref released, 1);
                        } else if (controller.Runner is NativeRecordingRunner native) {
                            native.ReleaseAfterOwnerDestroyed(() => { dependencies?.Dispose(); Volatile.Write(ref released, 1); }, health == null ? (Action<RecordingNativeRelease>)null : health.ObserveNativeRelease);
                        }
                    }
                }
            }
        }
        private Completion completion;
        private bool quitRequested;
        private Action quitApplication = () => Application.Quit();
        internal static Completion Begin(RecordingController controller, RecordingHealthFile health, IRecordingClock clock,
            double grace, RecordingHealthMedia media, string failure, bool quitRequested, double originalQuitDeadline)
        {
            // Preserve request-quit -> disable/destroy ordering as well as the
            // reverse ordering. Handoff may not extend an existing quit deadline.
            var ticket = new Completion(controller, health, clock, grace, media, failure, quitRequested ? (double?)originalQuitDeadline : null);
            var go = new GameObject("playkitu retirement") { hideFlags = HideFlags.HideAndDontSave };
            if (Application.isPlaying) DontDestroyOnLoad(go);
            var owner = go.AddComponent<RecordingRetirement>();
            owner.completion = ticket; owner.quitRequested = quitRequested;
            return ticket;
        }
        private void Update()
        {
            completion?.Tick();
            if (completion?.Complete == true) {
                if (quitRequested) quitApplication();
                if (Application.isPlaying) Destroy(gameObject); else DestroyImmediate(gameObject);
            }
        }
        private void OnEnable() { Application.wantsToQuit += WantsToQuit; }
        private void OnDisable() { Application.wantsToQuit -= WantsToQuit; }
        private bool WantsToQuit()
        {
            // A quit during retirement uses its EXISTING deadline, never grants
            // another grace period or depends on the now-disabled host updating.
            quitRequested = true;
            completion?.Tick();
            return completion == null || completion.Complete;
        }
        private void OnDestroy() { completion?.Finish(); }
    }
}
