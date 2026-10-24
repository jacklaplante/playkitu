using System;
namespace Playkitu.Client
{
    public enum RecordingHardwareEncoder { MediaFoundationHardware = 1 }
    // Developer-only serialized values. Freeze copies every field; running sessions
    // never observe later Inspector/preset edits. D6 gains/Off remain live controls.
    [Serializable] public sealed class RecordingProfile
    {
        public int Width=1280, Height=720, Fps=30, VideoKbps=1800, MaximumVideoKbps=2000, AudioBitsPerSecond=96000, KeyframeSeconds=1;
        public RecordingHardwareEncoder Encoder=RecordingHardwareEncoder.MediaFoundationHardware;
        public int VideoQueue=30, AudioQueue=100, InputVideoQueue=2, InputAudioQueue=8, ReadbackSlots=3, AudioRingSlots=8;
        public int MediaBytesPerSecond=294912, CreditBytes=4096, ReadQuantumBytes=4096, SenderWaitMilliseconds=100, SenderSliceMilliseconds=2;
        public int LogBytesPerSecond=32768, LogBurstBytes=262144, OverheadReserveBytesPerSecond=65536;
        public int LogFlushSeconds=5, LogBatchBytes=65536, PostAttempts=3, GrantCycles=3, RetryBaseSeconds=1;
        public float GameGain=1, MicrophoneGain=1, QuitGraceSeconds=8;
        public static RecordingProfile Standard() => new RecordingProfile();
        // This Windows Media Foundation encoder selects HE-AAC at64kbps; the maintained
        // decoder rejects that header. Both presets keep verified48kHz stereo AAC-LC96kbps.
        public static RecordingProfile Low() => new RecordingProfile { Width=640,Height=360,Fps=15,VideoKbps=800,MaximumVideoKbps=1000,AudioBitsPerSecond=96000,
            VideoQueue=15,AudioQueue=50,InputVideoQueue=1,InputAudioQueue=4,ReadbackSlots=2,AudioRingSlots=4,
            MediaBytesPerSecond=163840,LogBytesPerSecond=16384,OverheadReserveBytesPerSecond=32768,LogFlushSeconds=10,LogBatchBytes=32768,PostAttempts=2,GrantCycles=2 };
        public RecordingRunProfile Freeze() => new RecordingRunProfile(this);
    }
    public sealed class RecordingRunProfile
    {
        public readonly int Width,Height,Fps,VideoKbps,MaximumVideoKbps,AudioBitsPerSecond,GopFrames,VideoQueue,AudioQueue,InputVideoQueue,InputAudioQueue,ReadbackSlots,AudioRingSlots;
        public readonly int MediaBytesPerSecond,CreditBytes,ReadQuantumBytes,SenderWaitMilliseconds,SenderSliceMilliseconds,OverheadReserveBytesPerSecond;
        public readonly float GameGain,MicrophoneGain,QuitGraceSeconds;
        public readonly RecordingUploadOptions Upload;
        public int TotalProfileBytesPerSecond => MediaBytesPerSecond+Upload.BytesPerSecond+OverheadReserveBytesPerSecond;
        public bool UsesLegacyNativeDefaults => VideoKbps==1800&&MaximumVideoKbps==2000&&AudioBitsPerSecond==96000&&GopFrames==Fps&&
            VideoQueue==30&&AudioQueue==100&&InputVideoQueue==2&&InputAudioQueue==8&&MediaBytesPerSecond==294912&&CreditBytes==4096&&ReadQuantumBytes==4096&&SenderWaitMilliseconds==100&&SenderSliceMilliseconds==2;
        internal RecordingRunProfile(RecordingProfile p)
        {
            if(p==null||p.Encoder!=RecordingHardwareEncoder.MediaFoundationHardware||p.Width<64||p.Width>1280||p.Height<64||p.Height>720||
                p.Width%2!=0||p.Height%2!=0||p.Fps<1||p.Fps>30||p.VideoKbps<200||p.MaximumVideoKbps<p.VideoKbps||p.MaximumVideoKbps>2000||
                p.AudioBitsPerSecond!=96000||p.KeyframeSeconds<1||p.KeyframeSeconds>2||
                p.VideoQueue<1||p.VideoQueue>30||p.AudioQueue<1||p.AudioQueue>100||p.InputVideoQueue<1||p.InputVideoQueue>2||p.InputAudioQueue<1||p.InputAudioQueue>8||
                p.ReadbackSlots<1||p.ReadbackSlots>3||p.AudioRingSlots<1||p.AudioRingSlots>8||
                p.MediaBytesPerSecond<65536||p.MediaBytesPerSecond>294912||p.MediaBytesPerSecond<(p.MaximumVideoKbps*1000+p.AudioBitsPerSecond)/8+8192||
                p.CreditBytes<512||p.CreditBytes>4096||p.ReadQuantumBytes<512||p.ReadQuantumBytes>p.CreditBytes||
                p.SenderWaitMilliseconds<20||p.SenderWaitMilliseconds>100||p.SenderSliceMilliseconds<1||p.SenderSliceMilliseconds>5||(long)p.ReadQuantumBytes*1000>(long)p.MediaBytesPerSecond*(p.SenderWaitMilliseconds-p.SenderSliceMilliseconds)||
                p.LogBytesPerSecond<1||p.LogBytesPerSecond>32768||p.LogBurstBytes!=RecordingUploadOptions.MaximumFramedAttemptBytes||
                p.OverheadReserveBytesPerSecond<32768||p.OverheadReserveBytesPerSecond>65536||
                !FiniteRange(p.GameGain,0,2)||!FiniteRange(p.MicrophoneGain,0,2)||!FiniteRange(p.QuitGraceSeconds,0,10))
                throw new RecordingFailure("invalid_developer_profile");
            Width=p.Width;Height=p.Height;Fps=p.Fps;VideoKbps=p.VideoKbps;MaximumVideoKbps=p.MaximumVideoKbps;AudioBitsPerSecond=p.AudioBitsPerSecond;GopFrames=p.Fps*p.KeyframeSeconds;
            VideoQueue=p.VideoQueue;AudioQueue=p.AudioQueue;InputVideoQueue=p.InputVideoQueue;InputAudioQueue=p.InputAudioQueue;ReadbackSlots=p.ReadbackSlots;AudioRingSlots=p.AudioRingSlots;
            MediaBytesPerSecond=p.MediaBytesPerSecond;CreditBytes=p.CreditBytes;ReadQuantumBytes=p.ReadQuantumBytes;SenderWaitMilliseconds=p.SenderWaitMilliseconds;SenderSliceMilliseconds=p.SenderSliceMilliseconds;
            OverheadReserveBytesPerSecond=p.OverheadReserveBytesPerSecond;GameGain=p.GameGain;MicrophoneGain=p.MicrophoneGain;QuitGraceSeconds=p.QuitGraceSeconds;
            Upload=new RecordingUploadOptions(bytesPerSecond:p.LogBytesPerSecond,burstBytes:p.LogBurstBytes,logFlushSeconds:p.LogFlushSeconds,logBatchBytes:p.LogBatchBytes,
                postAttempts:p.PostAttempts,grantCycles:p.GrantCycles,retryBaseSeconds:p.RetryBaseSeconds);
        }
        private static bool FiniteRange(float value,float min,float max) => !float.IsNaN(value)&&!float.IsInfinity(value)&&value>=min&&value<=max;
    }
}
