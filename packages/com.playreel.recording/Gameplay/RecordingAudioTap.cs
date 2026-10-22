using System.Threading;
using Playreel.Client;
using UnityEngine;

namespace Playreel.Gameplay
{
    // Attach to the existing active AudioListener, never an individual voice.
    // Unity's listener OnAudioFilterRead mix coverage remains a consented proof gate.
    public sealed class RecordingAudioTap : MonoBehaviour
    {
        public RecordingAudioRing Ring;
        private int collecting;
        public void SetCollecting(bool enabled) => Volatile.Write(ref collecting, enabled ? 1 : 0);
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (Volatile.Read(ref collecting) != 0) Ring.Write(data, channels, AudioSettings.dspTime);
        }
        private void OnDisable() => Volatile.Write(ref collecting, 0);
    }
}
