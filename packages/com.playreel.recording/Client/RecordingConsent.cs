using System;

namespace Playreel.Client
{
    // Owns a deferred join across the consent menu. Recorder failure must never
    // cancel an accepted network join, and transfers reuse this session's consent.
    public sealed class RecordingConsent
    {
        private Action pending;
        public bool Granted { get; private set; }
        public bool HasPending => pending != null;
        public bool Request(Action connect)
        {
            if (Granted) { connect(); return false; }
            pending = connect; return true;
        }
        public bool Accept(Action startRecorder, Action recorderFailed)
        {
            if (pending == null) return false;
            Action connect = pending; pending = null; Granted = true;
            connect();
            try { startRecorder(); } catch { recorderFailed(); }
            return true;
        }
        public bool CancelPending() { bool existed = pending != null; pending = null; return existed; }
        public void End() { pending = null; Granted = false; }
    }
}
