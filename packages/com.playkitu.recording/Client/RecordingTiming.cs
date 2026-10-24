using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Playkitu.Client
{
    // Optional unsigned values use null for unknown, including native UINT64_MAX.
    // Do not convert QPC/PTS/SDK values through double: SDK100ns exceeds JS's exact
    // integer range. Native callback clock samples describe observation, not capture.
    public sealed class RecordingTimingObservation
    {
        public Guid RunId;
        public string Kind, Track, Fragment;
        public ulong Sequence, Epoch, Flags, Result, Upload;
        public ulong? PtsNs, SegmentStartNs, SegmentBaseNs, RunningNs, SdkPts100ns, TimecodeMs;
        public ulong? ObservationQpc, ObservationFrequency, ObservationUnix100ns, ObservationUncertainty100ns;
        public ulong? ManagedTicks, ManagedAfterTicks, ManagedFrequencyHz, CorrespondenceQpc, CorrespondenceUncertainty100ns;
        public ulong? Bytes, DroppedObservations, CaptureQpc, SubmissionQpc, DurationNs, InputOriginQpc;
        public uint InputProvenance;
        public double? DspTimeSeconds;
        public long? ManagedBeforeElapsedNs, ManagedAfterElapsedNs;
        public long? CaptureElapsedNs, CaptureUncertaintyNs;
        public bool KnownSyntheticCaptureCorrespondence;
        internal JObject Snapshot()
        {
            if (RunId == Guid.Empty || !new[] { "source", "encoded", "submitted", "ack", "reset", "gap", "clock", "external-input" }.Contains(Kind)
                || (Track != null && Track != "video" && Track != "audio") || (Fragment != null && (Fragment.Length > 128 || Fragment.Any(c => c < 33 || c > 126)))
                || CaptureElapsedNs < 0 || CaptureUncertaintyNs < 0
                || ManagedBeforeElapsedNs < 0 || ManagedAfterElapsedNs < 0 || ManagedAfterElapsedNs < ManagedBeforeElapsedNs
                || ManagedBeforeElapsedNs.HasValue != ManagedAfterElapsedNs.HasValue
                || (KnownSyntheticCaptureCorrespondence && (!CaptureElapsedNs.HasValue || !CaptureUncertaintyNs.HasValue || Kind != "submitted")))
                throw new RecordingFailure("invalid_timing");
            var json = new JObject { ["kind"] = Kind, ["producerRunId"] = RunId.ToString("D"), ["sequence"] = Sequence,
                ["epoch"] = Epoch, ["track"] = Track, ["flags"] = Flags, ["result"] = Result,
                ["bytes"] = Bytes.HasValue ? new JValue(Bytes.Value) : JValue.CreateNull(),
                ["droppedObservations"] = DroppedObservations.HasValue ? new JValue(DroppedObservations.Value) : JValue.CreateNull(),
                ["observationClock"] = new JObject { ["qpc"] = Value(ObservationQpc), ["frequencyHz"] = Value(ObservationFrequency),
                    ["unix100ns"] = Value(ObservationUnix100ns), ["uncertainty100ns"] = Value(ObservationUncertainty100ns) } };
            if (Kind == "external-input") {
                json["captureQpc"] = Value(CaptureQpc); json["submissionQpc"] = Value(SubmissionQpc);
                json["inputOriginQpc"] = Value(InputOriginQpc);
                json["ptsNs"] = Value(PtsNs); json["durationNs"] = Value(DurationNs); json["provenance"] = InputProvenance;
                json["timestampMeaning"] = "caller-reported-frame-or-DSP-block-boundary";
            }
            if (Kind == "source") {
                json["sourcePtsNs"] = Value(PtsNs); json["segmentStartNs"] = Value(SegmentStartNs);
                json["segmentBaseNs"] = Value(SegmentBaseNs); json["runningNs"] = Value(RunningNs);
            }
            if (Kind == "clock")
            {
                if (DspTimeSeconds.HasValue) {
                    if (double.IsNaN(DspTimeSeconds.Value) || double.IsInfinity(DspTimeSeconds.Value) || DspTimeSeconds.Value < 0) throw new RecordingFailure("invalid_timing");
                    json["dspTimeSeconds"] = DspTimeSeconds.Value;
                    json["captureUncertaintyNs"] = CaptureUncertaintyNs.HasValue ? (JToken)CaptureUncertaintyNs.Value : JValue.CreateNull();
                    json["meaning"] = "approximate-DSP-block-to-native-QPC";
                }
                json["managedTicks"] = Value(ManagedTicks); json["managedFrequencyHz"] = Value(ManagedFrequencyHz);
                json["managedAfterTicks"] = Value(ManagedAfterTicks);
                json["correspondenceQpc"] = Value(CorrespondenceQpc); json["correspondenceUncertainty100ns"] = Value(CorrespondenceUncertainty100ns);
                json["managedBeforeElapsedNs"] = ManagedBeforeElapsedNs.HasValue ? new JValue(ManagedBeforeElapsedNs.Value) : JValue.CreateNull();
                json["managedAfterElapsedNs"] = ManagedAfterElapsedNs.HasValue ? new JValue(ManagedAfterElapsedNs.Value) : JValue.CreateNull();
            }
            if (Kind == "encoded")
            {
                json["gstPtsNs"] = Value(PtsNs); json["segmentStartNs"] = Value(SegmentStartNs);
                json["segmentBaseNs"] = Value(SegmentBaseNs); json["runningNs"] = Value(RunningNs);
            }
            if (Kind == "submitted")
            {
                json["runningNs"] = Value(RunningNs); json["sdkPts100ns"] = Value(SdkPts100ns);
                json["captureMapping"] = KnownSyntheticCaptureCorrespondence ? "known-synthetic-source" : "unavailable";
                json["captureElapsedNs"] = KnownSyntheticCaptureCorrespondence ? (JToken)CaptureElapsedNs : JValue.CreateNull();
                json["captureUncertaintyNs"] = KnownSyntheticCaptureCorrespondence ? (JToken)CaptureUncertaintyNs : JValue.CreateNull();
            }
            if (Kind == "ack")
            {
                json["upload"] = Upload; json["fragment"] = Fragment; json["ackTimecodeMs"] = Value(TimecodeMs); json["ackType"] = Flags;
                // ACK flags/type, result and upload identity stay independent from
                // source/encoded/submitted evidence. No ACK-to-capture inference.
            }
            return json;
        }
        private static JToken Value(ulong? value) => value.HasValue && value.Value != ulong.MaxValue ? new JValue(value.Value) : JValue.CreateNull();
    }

    public sealed class RecordingTiming
    {
        public const int MaximumRecords = 48, MaximumRuns = 4;
        public readonly Guid SessionId;
        public readonly int AdmissionGeneration;
        public readonly double StartMonotonicSeconds;
        private readonly RecordingBuild build;
        private readonly JObject clockAnchor;
        private readonly List<JObject> records = new List<JObject>();
        private readonly List<JObject> runs = new List<JObject>();
        private readonly HashSet<string> ackKeys = new HashSet<string>();
        private readonly Queue<string> ackOrder = new Queue<string>();
        private JObject recordingStart;
        private bool rangesDirty;
        public long Revision { get; private set; } = 1;
        public long DroppedRecords { get; private set; }
        public long DroppedRuns { get; private set; }
        public long DroppedObjects { get; private set; }
        public long DroppedLogBytes { get; private set; }
        public int LatestUploadedLog { get; private set; } = -1;
        public int LatestUploadedIndex { get; private set; } = -1;
        public int LatestUploadedManifest { get; private set; } = -1;
        public int PendingRecords => records.Count;
        // Native source/encoded/submitted ranges live outside the event rows.
        // They still need periodic and shutdown indices even in a quiet session.
        // Keep this separate from Revision so frame updates do not emit manifests.
        public bool HasPendingIndexData => records.Count > 0 || rangesDirty;
        public RecordingTiming(Guid sessionId, int generation, RecordingBuild build, DateTimeOffset startUtc, double startMonotonicSeconds,
            string clockId = "managed-stopwatch", ulong frequencyHz = 0, ulong uncertaintyNs = 0, ulong? startTicks = null)
        {
            if (sessionId == Guid.Empty || generation < 1 || generation > 3 || build == null || !Finite(startMonotonicSeconds)
                || string.IsNullOrEmpty(clockId) || clockId.Length > 64 || clockId.Any(char.IsControl))
                throw new RecordingFailure("invalid_timing");
            SessionId = sessionId; AdmissionGeneration = generation; this.build = build; StartMonotonicSeconds = startMonotonicSeconds;
            clockAnchor = new JObject { ["clockId"] = clockId, ["frequencyHz"] = frequencyHz == 0 ? (ulong)Stopwatch.Frequency : frequencyHz, ["sessionElapsedNs"] = 0,
                ["utc"] = startUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), ["uncertaintyNs"] = uncertaintyNs,
                ["startTicks"] = startTicks.HasValue ? new JValue(startTicks.Value) : JValue.CreateNull() };
        }
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public long ElapsedNs(double now)
        {
            double elapsed = now - StartMonotonicSeconds;
            if (!Finite(elapsed) || elapsed < 0 || elapsed > 9000000000) throw new RecordingFailure("invalid_timing");
            return checked((long)Math.Round(elapsed * 1000000000));
        }
        // A sparse generated scheduling point only. Native QPC frequency and
        // the measured session-elapsed bracket are authoritative; never assume
        // Stopwatch/QPC frequencies or optional startTicks share an origin.
        public static bool TrySchedulingInterval(RecordingTimingObservation bridge, RecordingFrameClock frame, out long beforeNs, out long afterNs)
        {
            beforeNs = afterNs = 0;
            if (bridge == null || frame == null || !bridge.ManagedBeforeElapsedNs.HasValue || !bridge.ManagedAfterElapsedNs.HasValue
                || bridge.ManagedBeforeElapsedNs < 0 || bridge.ManagedAfterElapsedNs < bridge.ManagedBeforeElapsedNs
                || !bridge.CorrespondenceQpc.HasValue || bridge.CorrespondenceQpc == ulong.MaxValue
                || !bridge.ObservationFrequency.HasValue || bridge.ObservationFrequency == 0 || bridge.ObservationFrequency == ulong.MaxValue
                || frame.QpcFrequency != bridge.ObservationFrequency || frame.EstimatedScheduleQpc == ulong.MaxValue
                || frame.ScheduleUncertaintyNs == ulong.MaxValue || frame.Reason != 0 || (frame.Flags & 15) != 15) return false;
            return RecordingSchedulingMath.TryInterval(bridge.ManagedBeforeElapsedNs.Value, bridge.ManagedAfterElapsedNs.Value,
                bridge.CorrespondenceQpc.Value, bridge.ObservationFrequency.Value, frame.EstimatedScheduleQpc,
                frame.ScheduleUncertaintyNs, out beforeNs, out afterNs);
        }
        public bool Observe(RecordingTimingObservation observation)
        {
            if (observation == null) return false;
            JObject record;
            try { record = observation.Snapshot(); } catch { return false; }
            var run = runs.FirstOrDefault(r => (string)r["producerRunId"] == observation.RunId.ToString("D"));
            if (run == null)
            {
                if (runs.Count == MaximumRuns) { runs.RemoveAt(0); DroppedRuns++; }
                run = new JObject { ["producerRunId"] = observation.RunId.ToString("D"), ["epoch"] = observation.Epoch, ["ranges"] = new JObject() };
                runs.Add(run); Revision++;
            }
            // External input has no reset epoch in the native ABI. Once the run resets
            // or changes epoch, never reinterpret an old input against a new bridge.
            if (ReadUnsigned(run["epoch"]) != observation.Epoch) {
                run["applicationAcquisitionInvalidated"] = true; run["epoch"] = observation.Epoch; Revision++;
            }
            if (observation.Kind == "source" || observation.Kind == "encoded" || observation.Kind == "submitted" || observation.Kind == "external-input")
            {
                if (observation.Kind == "external-input" && observation.Track == "video") {
                    var first = run["firstAcceptedVideoInput"] as JObject;
                    if (first != null && run.Value<bool?>("applicationAcquisitionInvalidated") != true
                        && (!JToken.DeepEquals(first["inputOriginQpc"], record["inputOriginQpc"])
                        || !JToken.DeepEquals(first["observationClock"]["frequencyHz"], record["observationClock"]["frequencyHz"]))) {
                        run["applicationAcquisitionInvalidated"] = true; Revision++;
                    }
                    if (observation.Result != 0) {
                        run["observedRejectedVideoInputs"] = (run.Value<ulong?>("observedRejectedVideoInputs") ?? 0) + 1; Revision++;
                    } else if (first == null) {
                        // One bounded candidate per run, distinct from range.first (which may
                        // be rejected). This is appsrc acceptance, never encoded/persisted proof.
                        run["firstAcceptedVideoInput"] = record.DeepClone(); Revision++;
                    }
                }
                string rangeKey = observation.Kind + ":" + (observation.Track ?? "unknown") + ":" + observation.Epoch;
                var ranges = (JObject)run["ranges"];
                if (ranges[rangeKey] == null)
                {
                    // Retain only the current epoch's ranges; earlier immutable index
                    // chunks/manifest snapshots retain prior evidence and known gaps.
                    if (ranges.Count >= 8) { ranges.Remove(ranges.Properties().First().Name); DroppedRecords++; }
                    ranges[rangeKey] = new JObject { ["first"] = record.DeepClone() };
                }
                ranges[rangeKey]["last"] = record;
                rangesDirty = true;
                if (observation.Kind == "submitted" && observation.Track == "video" && observation.Result == 0
                    && observation.KnownSyntheticCaptureCorrespondence && observation.SdkPts100ns.HasValue && observation.SdkPts100ns != ulong.MaxValue
                    && run["recordingStart"] == null)
                {
                    run["recordingStart"] = record.DeepClone(); recordingStart = recordingStart ?? (JObject)record.DeepClone(); Revision++;
                }
            }
            else if (observation.Kind == "ack")
            {
                string key = observation.RunId.ToString("N") + ":" + observation.Epoch + ":" + observation.Upload + ":" + observation.Fragment + ":" + observation.Flags + ":" + observation.Result;
                if (!ackKeys.Add(key)) return true;
                ackOrder.Enqueue(key); if (ackOrder.Count > 64) ackKeys.Remove(ackOrder.Dequeue());
                Add(record);
            }
            else
            {
                Add(record); run["epoch"] = observation.Epoch;
                if (observation.Kind == "reset") { run.Remove("recordingStart"); run["applicationAcquisitionInvalidated"] = true; }
                if (observation.Kind == "clock") {
                    var prior = run["clockCorrespondence"] as JObject;
                    if (!observation.DspTimeSeconds.HasValue && prior != null
                        && !JToken.DeepEquals(prior["observationClock"]["frequencyHz"], record["observationClock"]["frequencyHz"]))
                        run["applicationAcquisitionInvalidated"] = true;
                    run[observation.DspTimeSeconds.HasValue ? "dspClockCorrespondence" : "clockCorrespondence"] = record.DeepClone();
                }
                Revision++;
            }
            return true;
        }
        // One atomic queue entry retains both clock SUPPORT records and FRAME.
        // Eviction and the 64 KiB index limit remove the entire group, never a
        // support alone. Raw evidence is preserved even for unavailable groups.
        public void ObserveClockGroup(Guid run, RecordingFrameClock[] group, bool usable, ulong droppedTotal)
        {
            if (run == Guid.Empty || group == null || group.Length < 1 || group.Length > 3) throw new RecordingFailure("invalid_clock_group");
            Add(new JObject { ["kind"] = "clock-group", ["clockSchemaVersion"] = 2, ["producerRunId"] = run.ToString("D"),
                ["usable"] = usable, ["meaning"] = "generated-scheduling", ["droppedTotal"] = droppedTotal,
                ["records"] = new JArray(group.Select(r => r.Snapshot())) });
            Revision++;
        }
        public void ObserveClockLoss(Guid run, ulong droppedTotal)
        {
            if (run == Guid.Empty) throw new RecordingFailure("invalid_clock_group");
            Add(new JObject { ["kind"] = "clock-loss", ["clockSchemaVersion"] = 2, ["producerRunId"] = run.ToString("D"),
                ["droppedTotal"] = droppedTotal, ["position"] = "unlocalized" }); Revision++;
        }
        internal void LogRange(int sequence, long byteStart, int bytes, int newlines, bool startsMidLine, bool endsMidLine, long startNs, long endNs)
        {
            Add(new JObject { ["kind"] = "log-range", ["logSequence"] = sequence, ["byteStart"] = byteStart, ["byteLength"] = bytes,
                ["newlineCount"] = newlines, ["startsMidLine"] = startsMidLine, ["endsMidLine"] = endsMidLine,
                ["elapsedStartNs"] = startNs, ["elapsedEndNs"] = endNs, ["timestampMeaning"] = "supplied-log-receipt" });
        }
        private void Add(JObject record)
        { if (records.Count == MaximumRecords) { records.RemoveAt(0); DroppedRecords++; Revision++; } records.Add(record); }
        internal void Drop(string reason, double now, int logBytes = 0, bool droppedObject = true, long? acceptedLogByteOffset = null,
            string objectKind = null, int? sequence = null)
        {
            if (droppedObject) DroppedObjects++;
            DroppedLogBytes += logBytes; Revision++;
            Add(new JObject { ["kind"] = "gap", ["reason"] = reason, ["elapsedNs"] = ElapsedNs(now), ["logBytes"] = logBytes,
                ["objectKind"] = objectKind, ["objectSequence"] = sequence.HasValue ? new JValue(sequence.Value) : JValue.CreateNull(),
                ["acceptedLogByteOffset"] = acceptedLogByteOffset.HasValue ? new JValue(acceptedLogByteOffset.Value) : JValue.CreateNull() });
        }
        internal void Uploaded(RecordingObject item)
        {
            if (item.Kind == "log") LatestUploadedLog = Math.Max(LatestUploadedLog, item.Sequence);
            if (item.Kind == "time-index") LatestUploadedIndex = Math.Max(LatestUploadedIndex, item.Sequence);
            if (item.Kind == "manifest") LatestUploadedManifest = Math.Max(LatestUploadedManifest, item.Sequence);
        }
        internal byte[] Manifest(int sequence, string phase)
        {
            var json = Root("manifest", sequence); json["phase"] = phase;
            json["build"] = new JObject { ["gameVersion"] = build.GameVersion, ["steamBuildId"] = build.SteamBuildId };
            json["recordingStart"] = recordingStart?.DeepClone();
            json["applicationAcquisitionStart"] = ApplicationAcquisitionStart(runs.Count == 1 && DroppedRuns == 0 ? runs[0] : null);
            json["producerRuns"] = new JArray(runs.Select(r => new JObject {
                ["producerRunId"] = r["producerRunId"].DeepClone(), ["epoch"] = r["epoch"].DeepClone(),
                ["recordingStart"] = r["recordingStart"]?.DeepClone(), ["applicationAcquisitionStart"] = ApplicationAcquisitionStart(r) }));
            json["latestPostConfirmed"] = new JObject { ["log"] = LatestUploadedLog, ["timeIndex"] = LatestUploadedIndex, ["manifest"] = LatestUploadedManifest };
            json["coverage"] = "unverified"; return Serialize(json, 16384);
        }
        internal byte[] TakeIndex(int sequence)
        {
            var json = Root("time-index", sequence); json["producerRuns"] = new JArray(runs.Select(r => {
                var copy = (JObject)r.DeepClone(); copy["applicationAcquisitionStart"] = ApplicationAcquisitionStart(r); return copy;
            }));
            var entries = new JArray(records.Select(r => r.DeepClone())); json["records"] = entries;
            byte[] bytes;
            while ((bytes = Encoding.UTF8.GetBytes(json.ToString(Formatting.None))).Length > 65536 && entries.Count > 0)
            { entries.RemoveAt(0); DroppedRecords++; json["droppedRecords"] = DroppedRecords; }
            if (bytes.Length > 65536) throw new RecordingFailure("timing_too_large");
            // A capacity wait or serialization failure must not acknowledge these
            // ranges. Only a successfully sealed immutable index consumes them;
            // a later compression/POST failure is reported as an object gap.
            records.Clear(); rangesDirty = false; return bytes;
        }
        // Qualified application acquisition/start metadata is deliberately separate from
        // synthetic recordingStart and strict playback navigation. The request QPC precedes
        // RenderGameView; the submission observation follows readback. Neither is a sensor
        // timestamp, and appsrc acceptance does not prove SDK/encoded/persisted coverage.
        private JObject ApplicationAcquisitionStart(JObject run)
        {
            var result = new JObject { ["status"] = "unavailable", ["reason"] = "no-accepted-video-input",
                ["sessionElapsedEstimateNs"] = null, ["utcEstimate"] = null, ["physicalCaptureErrorNs"] = null,
                ["playbackOffsetVerified"] = false, ["firstEncodedOrPersistedFrameVerified"] = false };
            if (run == null) { result["reason"] = runs.Count == 0 ? "no-producer-run" : "ambiguous-producer-runs"; return result; }
            result["producerRunId"] = run["producerRunId"].DeepClone(); result["epoch"] = run["epoch"].DeepClone();
            result["observedRejectedVideoInputs"] = run.Value<ulong?>("observedRejectedVideoInputs") ?? 0;
            if (run.Value<bool?>("applicationAcquisitionInvalidated") == true) { result["reason"] = "reset-or-input-clock-identity-changed"; return result; }
            var input = run["firstAcceptedVideoInput"] as JObject;
            if (input == null) return result;
            var bridge = run["clockCorrespondence"] as JObject;
            result["reason"] = "missing-native-managed-clock-bridge";
            if (bridge == null || bridge["managedBeforeElapsedNs"].Type == JTokenType.Null || bridge["managedAfterElapsedNs"].Type == JTokenType.Null) return result;
            var frequency = ReadUnsigned(input["observationClock"]["frequencyHz"], true);
            var origin = ReadUnsigned(input["inputOriginQpc"], true); var request = ReadUnsigned(input["captureQpc"], true);
            var submission = ReadUnsigned(input["submissionQpc"], true); var pts = ReadUnsigned(input["ptsNs"], true);
            var bridgeQpc = ReadUnsigned(bridge["correspondenceQpc"], true);
            var uncertainty100ns = ReadUnsigned(bridge["correspondenceUncertainty100ns"], true);
            result["reason"] = "incomplete-or-inconsistent-input-clock-correspondence";
            if (input.Value<uint>("provenance") != 1 || input.Value<ulong>("sequence") == 0
                || !frequency.HasValue || frequency == 0 || !origin.HasValue || origin == 0 || !request.HasValue || request < origin
                || !submission.HasValue || submission < request || !pts.HasValue || !bridgeQpc.HasValue
                || !uncertainty100ns.HasValue || uncertainty100ns > (ulong)long.MaxValue / 100
                || frequency != ReadUnsigned(bridge["observationClock"]["frequencyHz"], true)
                || ReadUnsigned(bridge["managedFrequencyHz"], true) != ReadUnsigned(clockAnchor["frequencyHz"])
                || !JToken.DeepEquals(input["epoch"], bridge["epoch"])) return result;
            // Use the same integer floor/ceiling conversion as existing scheduling evidence.
            // The input PTS must agree with its actual accepted-input origin and QPC.
            if (!RecordingSchedulingMath.TryInterval(0, 0, origin.Value, frequency.Value, request.Value, 0, out var ptsLow, out var ptsHigh)
                || pts > (ulong)ptsHigh || pts < (ulong)ptsLow) { result["reason"] = "input-pts-origin-mismatch"; return result; }
            if (!RecordingSchedulingMath.TryInterval(bridge.Value<long>("managedBeforeElapsedNs"), bridge.Value<long>("managedAfterElapsedNs"),
                bridgeQpc.Value, frequency.Value, request.Value, uncertainty100ns.Value * 100, out var lower, out var upper)) {
                result["reason"] = "session-time-conversion-out-of-range"; return result;
            }
            long estimate = lower + (upper - lower) / 2;
            var anchorUncertainty = ReadUnsigned(clockAnchor["uncertaintyNs"]);
            try {
                // UTC is an estimate from the existing session anchor, never a re-anchor.
                // Round bounds outward at DateTime's 100 ns resolution. These are conversion
                // bounds only: oscillator drift and UTC/physical accuracy remain unmeasured.
                var utc = DateTimeOffset.Parse((string)clockAnchor["utc"], CultureInfo.InvariantCulture);
                result["utcLower"] = utc.AddTicks(checked((long)decimal.Floor(((decimal)lower - anchorUncertainty.Value) / 100))).ToString("O", CultureInfo.InvariantCulture);
                result["utcUpper"] = utc.AddTicks(checked((long)decimal.Ceiling(((decimal)upper + anchorUncertainty.Value) / 100))).ToString("O", CultureInfo.InvariantCulture);
                result["utcEstimate"] = utc.AddTicks(estimate / 100).ToString("O", CultureInfo.InvariantCulture);
            } catch (ArgumentOutOfRangeException) { result["reason"] = "utc-conversion-out-of-range"; return result; }
              catch (OverflowException) { result["reason"] = "utc-conversion-out-of-range"; return result; }
            result["status"] = "estimated"; result["reason"] = null;
            result["meaning"] = "first-observed-accepted-video-acquisition-request";
            result["provenance"] = "game-view-render-request-QPC-before-readback";
            result["inputSequence"] = input["sequence"].DeepClone(); result["inputOriginQpc"] = origin.Value;
            result["requestQpc"] = request.Value; result["submissionObservationQpc"] = submission.Value;
            result["readbackQpc"] = null; result["nativeClockId"] = "windows-QPC"; result["nativeFrequencyHz"] = frequency.Value;
            result["inputPtsNs"] = pts.Value; result["mediaPtsStage"] = "accepted-appsrc-input";
            result["mediaTimebase"] = "1/1000000000-second"; result["sessionClockId"] = clockAnchor["clockId"].DeepClone();
            result["bridgeQpc"] = bridgeQpc.Value; result["bridgeUncertainty100ns"] = uncertainty100ns.Value;
            result["sessionElapsedLowerNs"] = lower; result["sessionElapsedUpperNs"] = upper; result["sessionElapsedEstimateNs"] = estimate;
            result["conversionBoundsMeaning"] = "integer-rounding-plus-measured-bridge-bracket-and-uncertainty";
            result["sessionUtcAnchorUncertaintyNs"] = anchorUncertainty.Value;
            result["absoluteUtcAccuracyNs"] = null; result["clockDriftErrorNs"] = null;
            result["earlierUnobservedInputPossible"] = true;
            return result;
        }
        private JObject Root(string kind, int sequence) => new JObject { ["schemaVersion"] = 1, ["sessionId"] = SessionId.ToString("D"),
            ["admissionGeneration"] = AdmissionGeneration, ["objectKind"] = kind, ["sequence"] = sequence,
            ["sessionClock"] = clockAnchor.DeepClone(), ["droppedRecords"] = DroppedRecords, ["droppedRuns"] = DroppedRuns,
            ["droppedObjects"] = DroppedObjects, ["droppedLogBytes"] = DroppedLogBytes };
        private static byte[] Serialize(JObject value, int limit)
        { var bytes = Encoding.UTF8.GetBytes(value.ToString(Formatting.None)); if (bytes.Length > limit) throw new RecordingFailure("timing_too_large"); return bytes; }
        public static ulong? ReadUnsigned(JToken token, bool timestamp = false)
        {
            if (token != null && token.Type == JTokenType.Null) return null;
            // JObject stores values above Int64.MaxValue as BigInteger. Value<ulong>
            // uses IConvertible and fails for those; decimal JSON parsing is lossless.
            if (token == null || token.Type != JTokenType.Integer || !ulong.TryParse(token.ToString(Formatting.None), NumberStyles.None,
                CultureInfo.InvariantCulture, out var value)) throw new RecordingFailure("invalid_timing");
            return timestamp && value == ulong.MaxValue ? (ulong?)null : value;
        }
        public override string ToString() => "Recording timing (content omitted)";
    }
}
