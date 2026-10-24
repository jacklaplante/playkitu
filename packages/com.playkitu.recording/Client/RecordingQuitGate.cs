using System;

namespace Playkitu.Client
{
    // Ordinary quit is a bounded opportunity to finish the existing Stop/Tick flow,
    // not another uploader/native owner. Forced process termination cannot use it.
    public sealed class RecordingQuitGate
    {
        public const double DefaultGraceSeconds = 8, MaximumGraceSeconds = 10;
        public bool Requested { get; private set; }
        public bool Allowed { get; private set; }
        public bool TimedOut { get; private set; }
        public double Deadline { get; private set; }
        public bool Request(double now, double graceSeconds, RecordingController controller)
        {
            if (Requested) return CanQuit(now, controller);
            Requested = true;
            if (controller == null || controller.State == RecordingControllerState.Consent || controller.State == RecordingControllerState.Finished)
                return Allowed = true;
            if (double.IsNaN(graceSeconds) || double.IsInfinity(graceSeconds)) graceSeconds = DefaultGraceSeconds;
            graceSeconds = Math.Max(0, Math.Min(MaximumGraceSeconds, graceSeconds));
            Deadline = now + graceSeconds;
            controller.Stop();
            return CanQuit(now, controller);
        }
        public bool CanQuit(double now, RecordingController controller)
        {
            if (!Requested) return false;
            if (Allowed || controller == null || controller.State == RecordingControllerState.Finished) return Allowed = true;
            // Invalid clocks fail open too; never trap the game in a deferred quit.
            if (double.IsNaN(now) || double.IsInfinity(now) || double.IsNaN(Deadline) || double.IsInfinity(Deadline) || now >= Deadline) {
                TimedOut = true; Allowed = true;
            }
            return Allowed;
        }
    }
}
