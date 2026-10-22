using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Playreel.Native
{
    // Recorder-only mixed PCM boundary. Call on one owner thread, never the DSP
    // callback. No microphone, playback, pipeline, plugin discovery or native Push.
    public interface IRecordingPcmConverter : IDisposable
    {
        int LatencyInputFrames { get; }
        double AppliedRatio { get; }
        bool TryConvert(float[] stereo, int frames, double outputPerInput, float[] output, int capacityFrames, out int produced);
    }

    // Registered GTypes refer into these DLLs even after the LAST recorder owner
    // closes. Pin the selected scoped cores for PROCESS lifetime, not session
    // lifetime. Converters/PCM still have explicit short-lived ownership.
    public sealed class RecordingPcmLibrary : IDisposable
    {
        private readonly string runtime;
        private readonly IntPtr[] modules = new IntPtr[3];
        private int converters;
        private bool disposed;
        public RecordingPcmLibrary(string runtime)
        {
            this.runtime = runtime;
            string bin = Path.GetFullPath(Path.Combine(runtime, "bin"));
            RecordingPcmConverter.ValidateLoadedCore(bin);
            try {
                modules[0] = RecordingPcmConverter.Load(bin, "gstreamer-1.0-0.dll");
                modules[1] = RecordingPcmConverter.Load(bin, "gobject-2.0-0.dll");
                modules[2] = RecordingPcmConverter.Load(bin, "gstaudio-1.0-0.dll");
                RecordingPcmConverter.ValidateLoadedCore(bin, pin: true);
            } catch { Dispose(); throw; }
        }
        public IRecordingPcmConverter CreateConverter()
        {
            if (disposed) throw new ObjectDisposedException(nameof(RecordingPcmLibrary));
            var result = new RecordingPcmConverter(runtime);
            converters++; result.Released = () => converters--;
            return result;
        }
        public void Dispose()
        {
            if (disposed) return;
            if (converters != 0) throw new InvalidOperationException("resampler_live_owner");
            disposed = true;
            for (int i = modules.Length - 1; i >= 0; i--) if (modules[i] != IntPtr.Zero) { RecordingPcmConverter.FreeLibrary(modules[i]); modules[i] = IntPtr.Zero; }
        }
    }

    public sealed class RecordingPcmConverter : IRecordingPcmConverter
    {
        public const int VirtualRate = 48000000, MaximumInputFrames = 4800, MaximumOutputFrames = 4864;
        private const int CanarySamples = 16;
        private const float Canary = 12345.25f;
        private readonly float[] input = new float[MaximumInputFrames * 2 + CanarySamples], output = new float[MaximumOutputFrames * 2 + CanarySamples];
        private readonly IntPtr[] modules = new IntPtr[3];
        private GCHandle inputPin, outputPin;
        private IntPtr inputPlane, outputPlane, converter;
        private New create; private Update update; private Count count;
        private Resample resample; private Free release;
        private int currentRate;
        public int LatencyInputFrames { get; private set; }
        public double AppliedRatio => VirtualRate / (double)currentRate;
        public string LoadedAudioModule { get; private set; }
        public bool Disposed { get; private set; }
        internal Action Released;
        internal RecordingPcmConverter(string runtime)
        {
            try {
                if (IntPtr.Size != 8) throw new InvalidOperationException("resampler_platform");
                string bin = Path.GetFullPath(Path.Combine(runtime, "bin"));
                ValidateLoadedCore(bin);
                modules[0] = Load(bin, "gstreamer-1.0-0.dll");
                modules[1] = Load(bin, "gobject-2.0-0.dll");
                modules[2] = Load(bin, "gstaudio-1.0-0.dll");
                LoadedAudioModule = Path.Combine(bin, "gstaudio-1.0-0.dll");
                create = Function<New>(2, "gst_audio_resampler_new"); update = Function<Update>(2, "gst_audio_resampler_update");
                count = Function<Count>(2, "gst_audio_resampler_get_out_frames");
                resample = Function<Resample>(2, "gst_audio_resampler_resample"); release = Function<Free>(2, "gst_audio_resampler_free");
                var empty = Function<Empty>(0, "gst_structure_new_empty"); var set = Function<Set>(0, "gst_structure_set_value");
                var freeStructure = Function<Free>(0, "gst_structure_free");
                var init = Function<ValueInit>(1, "g_value_init"); var unset = Function<ValueUnset>(1, "g_value_unset");
                var setInt = Function<ValueInt>(1, "g_value_set_int"); var setUInt = Function<ValueUInt>(1, "g_value_set_uint");
                var setDouble = Function<ValueDouble>(1, "g_value_set_double"); var setEnum = Function<ValueInt>(1, "g_value_set_enum");
                IntPtr options = empty("recording-resampler");
                if (options == IntPtr.Zero) throw new InvalidOperationException("resampler_options");
                try {
                    Value v = new Value();
                    init(ref v, new UIntPtr(24)); setInt(ref v, 64); set(options, "GstAudioResampler.n-taps", ref v); unset(ref v);
                    v = new Value(); init(ref v, new UIntPtr(28)); setUInt(ref v, 8); set(options, "GstAudioResampler.filter-oversample", ref v); unset(ref v);
                    v = new Value(); init(ref v, new UIntPtr(60)); setDouble(ref v, 0); set(options, "GstAudioResampler.max-phase-error", ref v); unset(ref v);
                    UIntPtr filterType = Function<TypeId>(2, "gst_audio_resampler_filter_mode_get_type")();
                    if (filterType == UIntPtr.Zero) throw new InvalidOperationException("resampler_type");
                    v = new Value(); init(ref v, filterType);
                    setEnum(ref v, 0); set(options, "GstAudioResampler.filter-mode", ref v); unset(ref v);
                    // GstAudioFormat is queried by name, not a guessed enum ordinal.
                    int format = Function<Format>(2, "gst_audio_format_from_string")("F32LE");
                    currentRate = VirtualRate + 1; // E39: avoid exact-unity copy-path impulse shift.
                    converter = create(4, 4, format, 2, currentRate, VirtualRate, options);
                } finally { freeStructure(options); }
                if (converter == IntPtr.Zero) throw new InvalidOperationException("resampler_create");
                // get_max_latency has only ONE argument, unlike get_out_frames.
                LatencyInputFrames = checked((int)Function<Latency>(2, "gst_audio_resampler_get_max_latency")(converter).ToUInt64());
                if (LatencyInputFrames > 480) throw new InvalidOperationException("resampler_latency");
                inputPin = GCHandle.Alloc(input, GCHandleType.Pinned); outputPin = GCHandle.Alloc(output, GCHandleType.Pinned);
                inputPlane = Marshal.AllocHGlobal(IntPtr.Size); outputPlane = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(inputPlane, inputPin.AddrOfPinnedObject()); Marshal.WriteIntPtr(outputPlane, outputPin.AddrOfPinnedObject());
            } catch { Dispose(); throw; }
        }

        public bool TryConvert(float[] stereo, int frames, double ratio, float[] destination, int capacityFrames, out int produced)
        {
            produced = 0;
            if (Disposed || stereo == null || destination == null || frames < 1 || frames > MaximumInputFrames
                || stereo.Length < frames * 2 || capacityFrames < 0 || capacityFrames > MaximumOutputFrames
                || destination.Length < capacityFrames * 2 || double.IsNaN(ratio) || ratio < .9998 || ratio > 1.0002) return false;
            for (int i = 0; i < frames * 2; i++) if (float.IsNaN(stereo[i]) || float.IsInfinity(stereo[i])) return false;
            int rate = checked((int)Math.Round(VirtualRate / ratio, MidpointRounding.AwayFromZero));
            if (rate == VirtualRate) rate++;
            if (rate != currentRate) {
                if (update(converter, rate, VirtualRate, IntPtr.Zero) == 0) return false;
                currentRate = rate;
            }
            ulong needed = count(converter, new UIntPtr((uint)frames)).ToUInt64();
            if (needed > (ulong)capacityFrames) return false; // Never consume input on capacity refusal.
            try {
                Array.Copy(stereo, input, frames * 2);
                for (int i = 0; i < CanarySamples; i++) { input[MaximumInputFrames * 2 + i] = Canary; output[MaximumOutputFrames * 2 + i] = Canary; }
                resample(converter, inputPlane, new UIntPtr((uint)frames), outputPlane, new UIntPtr(needed));
                for (int i = 0; i < CanarySamples; i++)
                    if (input[MaximumInputFrames * 2 + i] != Canary || output[MaximumOutputFrames * 2 + i] != Canary) throw new InvalidOperationException("resampler_canary");
                produced = (int)needed;
                for (int i = 0; i < produced * 2; i++) if (float.IsNaN(output[i]) || float.IsInfinity(output[i])) throw new InvalidOperationException("resampler_nonfinite");
                Array.Copy(output, destination, produced * 2);
                return true;
            } finally { Array.Clear(input, 0, input.Length); Array.Clear(output, 0, output.Length); }
        }
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if (converter != IntPtr.Zero) { release(converter); converter = IntPtr.Zero; }
            Array.Clear(input, 0, input.Length); Array.Clear(output, 0, output.Length);
            if (inputPin.IsAllocated) inputPin.Free(); if (outputPin.IsAllocated) outputPin.Free();
            if (inputPlane != IntPtr.Zero) Marshal.FreeHGlobal(inputPlane); if (outputPlane != IntPtr.Zero) Marshal.FreeHGlobal(outputPlane);
            for (int i = modules.Length - 1; i >= 0; i--) if (modules[i] != IntPtr.Zero) { FreeLibrary(modules[i]); modules[i] = IntPtr.Zero; }
            Released?.Invoke(); Released = null;
        }
        private T Function<T>(int module, string name) where T : Delegate
        {
            IntPtr p = GetProcAddress(modules[module], name);
            if (p == IntPtr.Zero) throw new InvalidOperationException("resampler_export");
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }
        internal static void ValidateLoadedCore(string directory, bool pin = false)
        {
            // Unity2022.3 IL2CPP Process.GetModules_icall is unimplemented. Query
            // Windows directly; GetModuleHandle(name) alone would miss a foreign
            // same-basename DLL when the expected copy was loaded first.
            const int MaximumModules = 4096;
            var modules = new IntPtr[MaximumModules];
            if (!EnumProcessModulesEx(GetCurrentProcess(), modules, (uint)(modules.Length * IntPtr.Size), out uint needed, 3)
                || needed > modules.Length * IntPtr.Size || needed % IntPtr.Size != 0)
                throw new InvalidOperationException("resampler_module_query");
            const int MaximumPathCharacters = 32768;
            var actual = new StringBuilder(MaximumPathCharacters);
            string[] corePaths = pin ? new string[7] : null; int cores = 0;
            for (int i = 0; i < needed / IntPtr.Size; i++) {
                actual.Length = 0;
                // IL2CPP MarshalWStringBuilderResult replaces the backing array
                // with resultLength+1 chars. Mono retains capacity. Restore the
                // full bound BEFORE every call and compare against the size passed,
                // never the possibly shrunken Capacity after native marshalling.
                actual.Capacity = MaximumPathCharacters;
                uint length = GetModuleFileNameW(modules[i], actual, MaximumPathCharacters);
                if (length == 0 || length >= MaximumPathCharacters) throw new InvalidOperationException("resampler_module_query");
                string path = actual.ToString(), name = Path.GetFileName(path);
                if (!IsCore(name)) continue;
                if (!string.Equals(Path.GetFullPath(path), Path.Combine(directory, name), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("resampler_foreign_core");
                if (pin) {
                    if (cores == corePaths.Length) throw new InvalidOperationException("resampler_module_query");
                    corePaths[cores++] = path;
                }
            }
            // Validate EVERY matching path before irreversible process pins. No
            // directory/search-policy changes, CLR Process.Modules or new DLLs.
            for (int i = 0; i < cores; i++)
                if (!GetModuleHandleExW(1, corePaths[i], out _)) throw new InvalidOperationException("resampler_pin_failed");
        }
        private static bool IsCore(string name) => name.Equals("glib-2.0-0.dll", StringComparison.OrdinalIgnoreCase)
            || name.Equals("gobject-2.0-0.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("gstreamer-1.0-0.dll", StringComparison.OrdinalIgnoreCase)
            || name.Equals("gstbase-1.0-0.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("gsttag-1.0-0.dll", StringComparison.OrdinalIgnoreCase)
            || name.Equals("gstaudio-1.0-0.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("ffi-7.dll", StringComparison.OrdinalIgnoreCase);
        internal static IntPtr Load(string directory, string name)
        {
            string requested = Path.Combine(directory, name);
            IntPtr module = LoadLibraryExW(requested, IntPtr.Zero, 0x1100);
            if (module == IntPtr.Zero) throw new InvalidOperationException("resampler_load");
            var actual = new StringBuilder(32768);
            if (GetModuleFileNameW(module, actual, actual.Capacity) == 0 || !string.Equals(Path.GetFullPath(actual.ToString()), requested, StringComparison.OrdinalIgnoreCase)) {
                FreeLibrary(module); throw new InvalidOperationException("resampler_module_mismatch");
            }
            return module;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Value { public UIntPtr Type; public ulong A, B; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr New(int method, int flags, int format, int channels, int inputRate, int outputRate, IntPtr options);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Update(IntPtr p, int inputRate, int outputRate, IntPtr options);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate UIntPtr Count(IntPtr p, UIntPtr frames);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate UIntPtr Latency(IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Resample(IntPtr p, IntPtr input, UIntPtr frames, IntPtr output, UIntPtr outFrames);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Empty([MarshalAs(UnmanagedType.LPStr)] string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Set(IntPtr p, [MarshalAs(UnmanagedType.LPStr)] string name, ref Value v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ValueInit(ref Value v, UIntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ValueUnset(ref Value v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ValueInt(ref Value v, int x);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ValueUInt(ref Value v, uint x);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ValueDouble(ref Value v, double x);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate UIntPtr TypeId();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Format([MarshalAs(UnmanagedType.LPStr)] string name);
        [DllImport("kernel32", ExactSpelling = true)] private static extern IntPtr GetCurrentProcess();
        [DllImport("psapi.dll", ExactSpelling = true, SetLastError = true)] private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, uint bytes, out uint needed, uint filter);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder path, int capacity);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern bool GetModuleHandleExW(uint flags, string name, out IntPtr module);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32")] internal static extern bool FreeLibrary(IntPtr module);
    }
}
