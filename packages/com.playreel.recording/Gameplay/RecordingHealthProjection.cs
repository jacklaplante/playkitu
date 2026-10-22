using System;
using Playreel.Client;
using Playreel.Producer;

namespace Playreel.Gameplay
{
    // One complete source projection serves live capture and stopped retirement.
    // Preserve captured facts when Unity objects disappear; native terminal/SDK
    // observations may still advance while the SAME owner drains.
    internal static class RecordingHealthProjection
    {
        internal static void ReadNative(ref RecordingHealthMedia value, NativeRecordingRunner native)
        {
            if (native == null) return;
            var input = native.InputStatus;
            value.ProducerRun = native.RunId; value.InputReady = native.InputReady;
            value.NativeStartupPhase = native.StartupPhase; value.NativeStartupPhaseElapsedMs = native.StartupPhaseElapsedMs;
            value.ObservedInputOriginQpc = native.ObservedInputOriginQpc; value.ObservedInputFrequencyHz = native.ObservedInputFrequencyHz;
            value.SdkAcceptedVideo = native.SdkAcceptedVideo; value.SdkAcceptedAudio = native.SdkAcceptedAudio;
            value.AcceptedVideo = input.AcceptedVideo; value.AcceptedAudio = input.AcceptedAudio;
            value.DroppedVideo = input.DroppedVideo; value.DroppedAudio = input.DroppedAudio;
            value.InvalidInputs = input.InvalidInputs; value.Contention = input.ProcessContentionReturns; value.ObservationDrops = input.ObservationDrops;
        }
        internal static void Read(ref RecordingHealthMedia value, NativeRecordingRunner native, RecordingCapture capture,
            RecordingMicrophone microphone, RecordingMicrophoneFailure microphoneFailure)
        {
            ReadNative(ref value, native);
            if (capture != null) {
                value.Startup = capture.Startup;
                value.CallerVideoDrops = capture.DroppedVideo; value.CallerAudioDrops = capture.DroppedAudio; value.CallerAudioRejected = capture.RejectedAudio;
                value.AudioClockFailure = capture.ClockFailure; value.AudioClockUncertaintyMs = capture.ClockUncertaintyMs;
                value.AudioClockPhaseMs = capture.ClockPhaseMs; value.AudioClockEnvelopeMs = capture.ClockEnvelopeMs;
                value.AudioClockBracketMs = capture.ClockBracketMs; value.AudioClockEstimatedPpm = capture.EstimatedAudioPpm;
                value.AudioClockAppliedPpm = capture.AppliedAudioPpm; value.AudioClockProjectionMs = capture.AudioProjectionMs;
                value.AudioSourceConsumedFrames = capture.ConsumedAudioFrames; value.AudioOutputProducedFrames = capture.ProducedAudioFrames;
                if (microphone != null) {
                    value.MicrophoneSubmittedFrames = capture.SubmittedMicrophoneFrames;
                    value.MicrophoneGain = RecordingMicrophone.ClampGain(capture.MicrophoneGain);
                }
            }
            if (microphone != null) {
                value.MicrophoneFailure = microphoneFailure != RecordingMicrophoneFailure.None ? microphoneFailure : microphone.Failure;
                value.MicrophoneFacts = microphone.Facts; value.MicrophoneCompletedFrames = microphone.CompletedFrames;
                value.MicrophoneMixedFrames = microphone.MixedFrames; value.MicrophoneInputPeak = microphone.InputPeak;
            } else if (microphoneFailure != RecordingMicrophoneFailure.None) value.MicrophoneFailure = microphoneFailure;
        }
    }
}
