using System.Diagnostics;
using System.Runtime.InteropServices;
using HmmExample.Interop;

namespace HmmExample.Acquisition;

// Generation-specific live receiver. UI snapshots and immutable completed frames are separate products.
internal sealed class InternalCapture : IAcquisitionCapture
{
    private sealed class ChannelState
    {
        internal readonly object Gate = new();
        internal readonly object CopyGate = new();
        internal long Callbacks, Displayed, DroppedUpdates, Blocked, FirstCallbackTicks, FirstDisplayTicks, DisplayedVersion;
        internal CallbackMetadata? Last;
        internal float[]? LastRawBlock;
        internal string Status = "Waiting for line blocks";
    }
    private readonly ChannelState[] channels = Enumerable.Range(0, 4).Select(_ => new ChannelState()).ToArray();
    private readonly FramePipeline pipeline;
    private readonly AcquisitionProgress? progress;
    // UI-owned snapshots: SDK producers never receive these arrays.
    private readonly float[][] uiSnapshots;
    private readonly RollingDiagnosticLog log;
    private readonly object ringGate = new();
    private readonly Queue<CallbackMetadata> ring = new();
    private readonly long generation;
    private long startTicks;
    private int active = 1, inFlight;
    public ExternalParameters Parameters { get; }
    public DisplayAverager Averaging { get; }
    internal CompletedFrameStatistics Statistics { get; }
    internal InternalCapture(long generation, ExternalParameters parameters, RollingDiagnosticLog log, AcquisitionProgress? progress = null, bool firstFrameOnly = false)
    {
        this.progress=progress;
        this.generation = generation; Parameters = parameters; this.log = log;
        var w = parameters.Wave;
        // Fresh manual Start begins at row zero; malformed metadata falls back to split-boundary resync.
        pipeline = new(new(w.pixel_per_line, parameters.OutputHeight, parameters.OutputDummy, w.number_of_lines/parameters.LineFactor,
            DummyPolicy.RawIncludesOverscan), generation, InitialAlignment.ConfirmedFrameStart,firstFrameOnly);
        Averaging=new(pipeline.Geometry.PixelCount,m=>log.Event(generation,m),GeometryMath.Pixels(w.pixel_per_line,w.number_of_lines),true);
        Statistics=new(m=>log.Event(generation,m));
        foreach(var assembler in pipeline.Assemblers) assembler.FrameCompleted+=Statistics.Publish;
        foreach(var assembler in pipeline.Assemblers) {assembler.ProcessedLinesReady+=Averaging.Publish;assembler.AssemblyInvalidated+=Averaging.Invalidate;}
        if(progress is not null) foreach(var assembler in pipeline.Assemblers) assembler.FrameCompleted+=progress.Complete;
        uiSnapshots = Enumerable.Range(0, 4).Select(_ => Array.Empty<float>()).ToArray();
    }
    public long ArmStart()
    {
        long value = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref startTicks, value); progress?.Arm(value);
        return value;
    }
    private static double? Since(long epoch, long value) => epoch == 0 || value == 0 ? null : Stopwatch.GetElapsedTime(epoch, value).TotalMilliseconds;
    private void Record(CallbackMetadata metadata)
    {
        lock (ringGate) { if (ring.Count == 100) ring.Dequeue(); ring.Enqueue(metadata); }
    }
    public void Receive(IntPtr pointer, long callbackGeneration, long currentGeneration)
    {
        long entered = Stopwatch.GetTimestamp();
        if (callbackGeneration != generation || currentGeneration != generation || Volatile.Read(ref active) == 0) return;
        Interlocked.Increment(ref inFlight);
        try
        {
            if (Volatile.Read(ref active) == 0) return;
            ReceiveCore(pointer, entered);
        }
        finally { Interlocked.Decrement(ref inFlight); }
    }
    private void ReceiveCore(IntPtr pointer, long entered)
    {
        var w = Parameters.Wave;
        if (pointer == IntPtr.Zero)
        {
            Record(new(generation, entered, -1, 0, 0, 0, 0, 0, 0, 0, w.pixel_per_line, w.line_per_frame, w.oversample_frame, "Null metadata pointer"));
            return;
        }
        var m = Marshal.PtrToStructure<CResampledImage>(pointer);
        var metadata = new CallbackMetadata(generation, entered, m.channel, m.width, m.height, m.image_size,
            m.index0_line_number, m.index0, m.index1_line_number, m.index1, w.pixel_per_line, w.line_per_frame, w.oversample_frame, "");
        if ((uint)m.channel >= 4) { Record(metadata with { Classification = "Invalid channel" }); return; }
        var c = channels[m.channel];
        if(!Monitor.TryEnter(c.CopyGate)) throw new InvalidOperationException($"CH{m.channel+1} overlapping callback rejected: host permits one copied block/channel at a time.");
        try
        {
        long epoch = Interlocked.Read(ref startTicks);
        bool afterStart = epoch != 0 && entered >= epoch;
        bool validShape = HostMemoryAdmissionPolicy.CallbackFits(m,Parameters);
        bool firstCallback = false;
        lock (c.Gate)
        {
            c.Callbacks++;
            if (afterStart && validShape)
            {
                firstCallback = c.FirstCallbackTicks == 0;
                if (firstCallback || entered < c.FirstCallbackTicks) c.FirstCallbackTicks = entered;
            }
        }
        if(afterStart && validShape) progress?.Callback(m.channel,generation,entered);
        if (firstCallback) log.First(generation,m.channel,"FirstCallback", $" {Since(epoch, entered):F3} ms");
        string classification;
        if (!validShape)
        {
            pipeline.Assemblers[m.channel].Invalidate();
            classification = $"Metadata mismatch: expected {w.number_of_lines} raw lines (at most for N=1), group={Parameters.LineFactor}, geometry={w.pixel_per_line}x{checked(w.line_per_frame+w.oversample_frame)}; received size={m.image_size}, n0={m.index0_line_number}, n1={m.index1_line_number}; live/partial frame invalidated";
            log.Event(generation,$"CH{m.channel+1} {classification}; width={m.width}, height={m.height}, size={m.image_size}, n0={m.index0_line_number}, n1={m.index1_line_number}");
        }
        else
        {
            // Allocate/copy outside shared-buffer and counter locks. No pointer survives the return.
            var copy = HostMemoryAdmissionPolicy.Floats(m.image_size);
            Marshal.Copy(m.image_data, copy, 0, copy.Length);
            lock (c.Gate) c.LastRawBlock = copy;
            if (!afterStart) classification = "Copied before Start; not assembled";
            else
            {
                var before = pipeline.Assemblers[m.channel].Snapshot();
                string? averageError=null;
                try {pipeline.Accept(LineAverager.Process(new(m.channel, m.width, m.height, m.index0_line_number, m.index1_line_number, copy, entered, generation),Parameters));}
                catch(InvalidOperationException e) {averageError=e.Message;pipeline.Assemblers[m.channel].Invalidate();log.Event(generation,e.Message);}

                var after = pipeline.Assemblers[m.channel].Snapshot();
                if(after.RejectedBlocks>before.RejectedBlocks && averageError is null) log.Event(generation,$"Frame boundary diagnostic error: n0={m.index0_line_number}, n1={m.index1_line_number}; raw block={Parameters.Wave.number_of_lines}, raw frame={m.height}; partial frame invalidated.");
                if (before.FirstCompleteTimestamp == 0 && after.FirstCompleteTimestamp != 0)
                    log.First(generation,m.channel,"FirstCompleteFrame", $" {Since(epoch, after.FirstCompleteTimestamp):F3} ms");
                classification = averageError ?? (after.RejectedBlocks > before.RejectedBlocks ? "Rejected block; live cleared, waiting for split boundary"
                    : !after.Synchronized ? "Waiting for split boundary after invalidation"
                    : after.CompletedFrames > before.CompletedFrames ? "Immutable frame published; live reflects current frame state"
                    : "Progressive processed live buffer updated; not a complete frame");
            }
        }
        metadata = metadata with { Classification = classification };
        lock (c.Gate) c.Last = metadata;
        Record(metadata);
        } finally {Monitor.Exit(c.CopyGate);}
    }
    // Completed frames remain available for a future frame consumer, not live rendering.
    public bool TryTake(int index, out CompletedFrame? frame)
    {
        frame = null;
        return Volatile.Read(ref active) != 0 && pipeline.TryRead(index, out frame);
    }
    internal bool TryTakeLive(int index, out LiveFrameSnapshot? snapshot, int previewMaxDimension = 0)
    {
        snapshot = null;
        if (Volatile.Read(ref active) == 0) return false;
        long afterVersion; lock (channels[index].Gate) afterVersion = channels[index].DisplayedVersion;
        var sampling=PreviewSampling.Create(pipeline.Geometry.Width,pipeline.Geometry.AcquisitionRows,previewMaxDimension);
        if(uiSnapshots[index].Length!=sampling.PixelCount) uiSnapshots[index]=HostMemoryAdmissionPolicy.Floats(sampling.PixelCount);
        return pipeline.Assemblers[index].TryCopyLive(afterVersion, uiSnapshots[index], out snapshot,previewMaxDimension)
            && Volatile.Read(ref active) != 0;
    }
    internal void LiveDisplayed(int index, long version)
    {
        long now = Stopwatch.GetTimestamp();
        bool first;
        lock (channels[index].Gate)
        {
            var c = channels[index];
            if (version <= c.DisplayedVersion) return;
            c.DroppedUpdates += Math.Max(0, version - c.DisplayedVersion - 1);
            c.DisplayedVersion = version; c.Displayed++;
            first = c.FirstDisplayTicks == 0;
            if (first) c.FirstDisplayTicks = now;
            c.Status = "PROCESSED FRAME - includes Dummy/Overscan · progressive live preview";
        }
        if (first) log.Event(generation, $"CH{index + 1} first display update: {Since(Interlocked.Read(ref startTicks), now):F3} ms (UI bitmap assigned)");
    }
    public void Displayed(int index) { lock(channels[index].Gate) {channels[index].Displayed++;channels[index].Status="Batch-averaged processed lines displayed";if(channels[index].FirstDisplayTicks==0) channels[index].FirstDisplayTicks=Stopwatch.GetTimestamp();} }
    public void DisplayBlocked(int index, string reason)
    {
        lock (channels[index].Gate) { channels[index].Blocked++; channels[index].Status = reason; }
    }
    public void CollectBuffers(HashSet<Array> buffers)
    {
        foreach(var c in channels) lock(c.Gate) if(c.LastRawBlock is { } b) buffers.Add(b);
        pipeline.CollectBuffers(buffers);foreach(var b in uiSnapshots)buffers.Add(b);
        Averaging.CollectBuffers(buffers);Statistics.CollectBuffers(buffers);
    }
    public ExternalCaptureSnapshot Snapshot()
    {
        long epoch = Interlocked.Read(ref startTicks);
        var states = channels.Select((c, i) =>
        {
            var d = pipeline.Assemblers[i].Snapshot();
            lock (c.Gate)
                return new ExternalChannelSnapshot(i, c.Callbacks, d.CompletedFrames, 0, 0,
                    c.Blocked, d.RejectedBlocks, Since(epoch, c.FirstCallbackTicks), c.Last, c.Status,
                    Since(epoch, d.FirstCompleteTimestamp), d.AccumulatedLines, checked(d.AccumulatedLines * Parameters.Wave.pixel_per_line),
                    d.CompletedFrames, d.Synchronized, d.Blocks, c.Displayed, c.DroppedUpdates, Since(epoch, c.FirstDisplayTicks),
                    d.LiveVersion, d.LiveAccumulatedLines, pipeline.Geometry.AcquisitionRows, pipeline.DroppedCompletedQueueFrames(i));
        }).ToArray();
        CallbackMetadata[] metadata; lock (ringGate) metadata = ring.ToArray();
        return new(generation, states.Min(c => c.FirstCallbackMs), states, metadata,
            states.Min(c => c.FirstCompleteFrameMs), "Internal Trigger", true, states.Min(c => c.FirstDisplayUpdateMs), Statistics.Snapshot());
    }
    public ExternalCaptureSnapshot FreezeAndSnapshot()
    {
        Volatile.Write(ref active, 0);
        // Stop runs on a worker. Drain admitted callbacks before buffer teardown.
        var wait = new SpinWait();
        while (Volatile.Read(ref inFlight) != 0) wait.SpinOnce();
        var report = Snapshot();
        foreach (var c in channels) lock (c.Gate) { c.LastRawBlock = null; c.FirstCallbackTicks = 0; c.FirstDisplayTicks = 0; c.DisplayedVersion = 0; }
        Statistics.Retire();
        Averaging.Retire();
        pipeline.Clear();
        for (int i = 0; i < uiSnapshots.Length; i++) uiSnapshots[i] = [];
        Interlocked.Exchange(ref startTicks, 0);
        return report;
    }
}
