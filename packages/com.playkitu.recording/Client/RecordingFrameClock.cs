using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Playkitu.Client
{
    // Exact additive native clock-v2 field names/units. UINT64_MAX is unknown;
    // sequence, ordinals and epochs are identities (zero is valid where allowed).
    public sealed class RecordingFrameClock
    {
        public uint Size, Version, Kind, Flags, Track, Reason, SourceSegmentSeq, EncodedSegmentSeq;
        public ulong Sequence, FrameOrdinal, ClockEpoch, ProducerEpoch;
        public ulong QpcBefore, QpcAfter, QpcFrequency, GstClockTimeNs, PipelineBaseTimeNs, ClockResolutionNs;
        public ulong CalibrationInternalNs, CalibrationExternalNs, CalibrationRateNumerator, CalibrationRateDenominator;
        public ulong PadArrivalQpc, EstimatedScheduleQpc, ScheduleUncertaintyNs;
        public ulong SourcePtsNs, SourceDurationNs, SourceRunningNs, SourceSegmentStartNs, SourceSegmentBaseNs;
        public ulong EncodedPtsNs, EncodedRunningNs, EncodedSegmentStartNs, EncodedSegmentBaseNs, SdkInputRunningNs, SdkPts100ns;
        internal JObject Snapshot()
        {
            var result = new JObject { ["size"] = Size, ["version"] = Version, ["kind"] = Kind, ["flags"] = Flags,
                ["track"] = Track, ["reason"] = Reason, ["sourceSegmentSeq"] = SourceSegmentSeq, ["encodedSegmentSeq"] = EncodedSegmentSeq,
                ["sequence"] = Sequence, ["frameOrdinal"] = FrameOrdinal, ["clockEpoch"] = ClockEpoch, ["producerEpoch"] = ProducerEpoch };
            string[] names = { "qpcBefore", "qpcAfter", "qpcFrequency", "gstClockTimeNs", "pipelineBaseTimeNs", "clockResolutionNs",
                "calibrationInternalNs", "calibrationExternalNs", "calibrationRateNumerator", "calibrationRateDenominator", "padArrivalQpc",
                "estimatedScheduleQpc", "scheduleUncertaintyNs", "sourcePtsNs", "sourceDurationNs", "sourceRunningNs", "sourceSegmentStartNs",
                "sourceSegmentBaseNs", "encodedPtsNs", "encodedRunningNs", "encodedSegmentStartNs", "encodedSegmentBaseNs", "sdkInputRunningNs", "sdkPts100ns" };
            ulong[] values = { QpcBefore, QpcAfter, QpcFrequency, GstClockTimeNs, PipelineBaseTimeNs, ClockResolutionNs,
                CalibrationInternalNs, CalibrationExternalNs, CalibrationRateNumerator, CalibrationRateDenominator, PadArrivalQpc,
                EstimatedScheduleQpc, ScheduleUncertaintyNs, SourcePtsNs, SourceDurationNs, SourceRunningNs, SourceSegmentStartNs,
                SourceSegmentBaseNs, EncodedPtsNs, EncodedRunningNs, EncodedSegmentStartNs, EncodedSegmentBaseNs, SdkInputRunningNs, SdkPts100ns };
            for (int i = 0; i < names.Length; i++) result[names[i]] = values[i] == ulong.MaxValue ? JValue.CreateNull() : new JValue(values[i]);
            return result;
        }
    }
    // Exactly two pending supports. A loss, boundary, identity or sequence change
    // prevents a usable group; no nearest frame, cross-epoch or partial matching.
    public sealed class RecordingClockGroups
    {
        private readonly List<RecordingFrameClock> pending = new List<RecordingFrameClock>(2);
        private ulong lastSequence, lastDropped;
        public int Pending => pending.Count;
        public void ObserveLoss(RecordingTiming timing, Guid run, ulong droppedTotal)
        {
            if (droppedTotal == lastDropped) return;
            Flush(timing, run, droppedTotal); timing.ObserveClockLoss(run, droppedTotal); lastDropped = droppedTotal;
        }
        public void Observe(RecordingTiming timing, Guid run, RecordingFrameClock row, ulong droppedTotal)
        {
            bool loss = droppedTotal != lastDropped || (lastSequence != 0 && row.Sequence != lastSequence + 1);
            ObserveLoss(timing, run, droppedTotal); lastSequence = row.Sequence;
            if (loss || row.Kind == 2) Flush(timing, run, droppedTotal);
            if (row.Size != 256 || row.Version != 2) { pending.Clear(); return; }
            if (row.Kind == 3) {
                if (pending.Count == 2 || (pending.Count > 0 && !Same(pending[0], row))) Flush(timing, run, droppedTotal);
                pending.Add(row); return;
            }
            if (row.Kind != 1 && row.Kind != 2) { pending.Clear(); return; }
            bool valid = !loss && row.Kind == 1 && row.Reason == 0 && (row.Flags & 15) == 15 && row.Track == 1
                && pending.Count == 2 && Same(pending[0], row) && Same(pending[1], row)
                && pending[0].Sequence + 1 == pending[1].Sequence && pending[1].Sequence + 1 == row.Sequence
                && pending[0].Reason == 0 && pending[1].Reason == 0 && row.ClockEpoch != 0
                && row.SourceSegmentSeq != 0 && row.EncodedSegmentSeq != 0
                && row.EstimatedScheduleQpc != ulong.MaxValue && row.ScheduleUncertaintyNs != ulong.MaxValue
                && pending[0].QpcFrequency != 0 && pending[0].QpcFrequency != ulong.MaxValue
                && pending[0].QpcFrequency == pending[1].QpcFrequency && row.QpcFrequency == pending[0].QpcFrequency
                && pending[0].QpcBefore <= row.EstimatedScheduleQpc && row.EstimatedScheduleQpc <= pending[1].QpcAfter
                && Encloses(pending[0], pending[1], row);
            var records = new List<RecordingFrameClock>(pending); records.Add(row);
            timing.ObserveClockGroup(run, records.ToArray(), valid, droppedTotal); pending.Clear();
        }
        public void Flush(RecordingTiming timing, Guid run, ulong droppedTotal)
        {
            if (pending.Count > 0) timing.ObserveClockGroup(run, pending.ToArray(), false, droppedTotal);
            pending.Clear();
        }
        private static bool Same(RecordingFrameClock a, RecordingFrameClock b) => a.FrameOrdinal == b.FrameOrdinal
            && a.ClockEpoch == b.ClockEpoch && a.ProducerEpoch == b.ProducerEpoch;
        private static bool Encloses(RecordingFrameClock a, RecordingFrameClock b, RecordingFrameClock frame)
        {
            if (a.QpcBefore > a.QpcAfter || a.QpcAfter >= b.QpcBefore || b.QpcBefore > b.QpcAfter
                || b.QpcAfter == ulong.MaxValue || a.GstClockTimeNs == ulong.MaxValue || b.GstClockTimeNs == ulong.MaxValue
                || frame.PipelineBaseTimeNs == ulong.MaxValue || frame.SourceRunningNs == ulong.MaxValue
                || frame.EncodedRunningNs != frame.SourceRunningNs || frame.SdkInputRunningNs != frame.SourceRunningNs
                || frame.SdkPts100ns == ulong.MaxValue || frame.SourcePtsNs == ulong.MaxValue || frame.EncodedPtsNs == ulong.MaxValue
                || a.ClockResolutionNs == ulong.MaxValue || b.ClockResolutionNs == ulong.MaxValue
                || a.PipelineBaseTimeNs != frame.PipelineBaseTimeNs || b.PipelineBaseTimeNs != frame.PipelineBaseTimeNs
                || a.CalibrationInternalNs != b.CalibrationInternalNs || a.CalibrationExternalNs != b.CalibrationExternalNs
                || a.CalibrationRateNumerator == 0 || a.CalibrationRateNumerator == ulong.MaxValue
                || a.CalibrationRateDenominator == 0 || a.CalibrationRateDenominator == ulong.MaxValue
                || a.CalibrationRateNumerator != b.CalibrationRateNumerator || a.CalibrationRateDenominator != b.CalibrationRateDenominator) return false;
            decimal target = (decimal)frame.PipelineBaseTimeNs + frame.SourceRunningNs;
            return target <= ulong.MaxValue && a.GstClockTimeNs < target && target < b.GstClockTimeNs;
        }
    }
}
