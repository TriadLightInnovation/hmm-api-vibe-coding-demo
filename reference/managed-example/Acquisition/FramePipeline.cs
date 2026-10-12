using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Diagnostics;
using HmmExample.Interop;

namespace HmmExample.Acquisition;

internal enum InitialAlignment { ConfirmedFrameStart, WaitForSplitBoundary }
internal enum DummyPolicy { Unconfirmed, ConfirmedTrailingRows, RawIncludesOverscan }
internal sealed record FrameGeometry(int Width, int Height, int Dummy, int LinesPerCallback, DummyPolicy DummyPolicy)
{
    internal int AcquisitionRows => checked(Height + Dummy);
    internal int PixelCount => GeometryMath.Pixels(Width,AcquisitionRows);
    internal void Validate()
    {
        if (Width <= 0 || Height <= 0 || Dummy < 0 || LinesPerCallback is not (16 or 32) || PixelCount <= 0)
            throw new ArgumentException("Invalid frame geometry or callback block size.");
    }
}

// Immutable metadata, exclusively owned array: producer never writes to Data after publication.
// Height includes Dummy; cropping/normalization belong to the UI consumer, after completion.
internal sealed record CompletedFrame(int Channel, int Width, int Height, float[] Data,
    long FrameSequence, long Timestamp, long SessionGeneration);
internal sealed record CallbackBlock(int Channel, int Width, int Height, int Index0Lines, int Index1Lines,
    float[] Data, long Timestamp, long SessionGeneration);
// Data is a UI-owned snapshot, never the mutable producer array; not a CompletedFrame.
internal sealed record LiveFrameSnapshot(int Channel, int Width, int Height, float[] Data, long Version,
    int AccumulatedLines, long FrameSequence, long Timestamp, long SessionGeneration);
internal sealed record ChannelDiagnostics(long Callbacks, long Blocks, long CompletedFrames,
    int AccumulatedLines, int LastIndex0Lines, int LastIndex1Lines, long RejectedBlocks, bool Synchronized, long SessionGeneration,
    long LiveVersion = 0, int LiveAccumulatedLines = 0, long FirstCompleteTimestamp = 0);

internal sealed class ChannelFrameAssembler
{
    private readonly object gate = new();
    private readonly int channel;
    private readonly FrameGeometry geometry;
    private readonly long generation;
    private readonly ChannelWriter<CompletedFrame> writer;
    private float[] current;
    private float[] live;
    // Exact-size spare arrays are acquired outside the live-buffer lock. Published arrays never return here.
    private readonly ConcurrentBag<float[]> spares = new();
    private long liveVersion, liveTimestamp, firstCompleteTimestamp;
    private int liveLines;
    private bool retired;
    private int lines;
    private bool synchronized;
    private long callbacks, blocks, completed, rejected;
    private int last0, last1;
    private readonly bool firstFrameOnly;
    internal event Action<CompletedFrame,long>? FrameCompleted;
    // Enqueue immutable processed block slices while the assembler lock preserves per-channel row order.
    // Subscribers must not process pixels, render, dispatch, or perform file I/O here.
    internal event Action<ProcessedLineSegment>? ProcessedLinesReady;
    internal event Action<string>? AssemblyInvalidated;
    internal ChannelFrameAssembler(int channel, FrameGeometry geometry, long generation, InitialAlignment alignment, ChannelWriter<CompletedFrame> writer, bool firstFrameOnly = false)
    {
        this.firstFrameOnly=firstFrameOnly;
        this.channel = channel; this.geometry = geometry; this.generation = generation; this.writer = writer;
        current = HostMemoryAdmissionPolicy.Floats(geometry.PixelCount); live = HostMemoryAdmissionPolicy.Floats(geometry.PixelCount);
        spares.Add(HostMemoryAdmissionPolicy.Floats(geometry.PixelCount));
        synchronized = alignment == InitialAlignment.ConfirmedFrameStart;
    }
    internal void CollectBuffers(HashSet<Array> buffers)
    {lock(gate) {buffers.Add(current);buffers.Add(live);foreach(var b in spares)buffers.Add(b);}}
    internal ChannelDiagnostics Snapshot()
    {
        lock (gate) return new(callbacks, blocks, completed, lines, last0, last1, rejected, synchronized, generation,
            liveVersion, liveLines, firstCompleteTimestamp);
    }
    internal void Clear()
    {
        lock (gate) { retired = true; current = []; live = []; lines = 0; liveLines = 0; liveVersion = 0; firstCompleteTimestamp = 0; synchronized = false; }
        while (spares.TryTake(out _)) { }
    }
    internal void Invalidate() { lock (gate) { if (!retired) Reject(); } }
    internal bool TryCopyLive(long afterVersion, float[] destination, out LiveFrameSnapshot? snapshot, int previewMaxDimension = 0)
    {
        snapshot = null;
        long version, sequence, timestamp; int accumulated;
        var sampling=PreviewSampling.Create(geometry.Width,geometry.AcquisitionRows,previewMaxDimension);
        // UI skips a busy producer; only bounded snapshot sampling is done under this lock.
        if(!Monitor.TryEnter(gate)) return false;
        try
        {
            if (retired || liveVersion <= afterVersion || liveVersion == 0) return false;
            sampling.Copy(live,destination);
            version = liveVersion; sequence = completed; timestamp = liveTimestamp; accumulated = sampling.AcquiredRows(liveLines);
        }
        finally {Monitor.Exit(gate);}
        snapshot = new(channel, sampling.Width, sampling.Height, destination, version, accumulated, sequence, timestamp, generation);
        return true;
    }
    private bool Valid(int width, int height, int n0, int n1, int length, long session) =>
        session == generation && width == geometry.Width && height == geometry.AcquisitionRows && n0 > 0 && n1 >= 0
        && (long)n0 + n1 <= geometry.LinesPerCallback && n0 <= geometry.AcquisitionRows && n1 < geometry.AcquisitionRows
        && ((long)n0 + n1) * width == length;

    // Standalone native adapter retained for offline fixtures; actual InternalCapture copies before Accept.
    internal void AcceptNative(CResampledImage image, long session, long timestamp)
    {
        if (image.image_data == IntPtr.Zero || !Valid(image.width, image.height, image.index0_line_number,
            image.index1_line_number, image.image_size, session))
        {
            lock (gate) { if (retired) return; Observe(image.index0_line_number, image.index1_line_number); if (session != generation) rejected++; else Reject(); }
            return;
        }
        var copy = HostMemoryAdmissionPolicy.Floats(image.image_size);
        Marshal.Copy(image.image_data, copy, 0, copy.Length);
        Accept(new(image.channel, image.width, image.height, image.index0_line_number, image.index1_line_number, copy, timestamp, session));
    }
    internal void Accept(CallbackBlock block)
    {
        lock(gate) if(retired || (firstFrameOnly && completed!=0)) return;
        if (block.SessionGeneration != generation || block.Channel != channel || !Valid(block.Width, block.Height,
            block.Index0Lines, block.Index1Lines, block.Data.Length, block.SessionGeneration))
        {
            lock (gate) { if (retired) return; Observe(block.Index0Lines, block.Index1Lines); if (block.SessionGeneration != generation) rejected++; else Reject(); }
            return;
        }
        // No allocation-heavy work under the producer/UI buffer lock.
        var next = spares.TryTake(out var spare) ? spare : HostMemoryAdmissionPolicy.Floats(geometry.PixelCount);
        bool used = false;
        CompletedFrame? published=null;
        try
        {
            lock (gate)
            {
                if (retired || (firstFrameOnly && completed!=0)) return;
                Observe(block.Index0Lines, block.Index1Lines);
                Assemble(block.Index0Lines, block.Index1Lines, block.Data, block.Timestamp, next, ref used, ref published);
            }
            if(published is not null) FrameCompleted?.Invoke(published,Stopwatch.GetTimestamp());
        }
        finally { if (!used && !Volatile.Read(ref retired)) spares.Add(next); }
    }
    private void Observe(int n0, int n1) { callbacks++; last0 = n0; last1 = n1; }
    private void Reject()
    {
        AssemblyInvalidated?.Invoke($"CH{channel+1} processed frame boundary invalid; batch average halted");
        rejected++; lines = 0; liveLines = 0; synchronized = false;
        Array.Clear(current); Array.Clear(live);
        if (liveVersion != 0) liveVersion++; // Invalidation clears stale imagery on the next timer tick.
    }
    private void Assemble(int n0, int n1, float[] source, long timestamp, float[] next, ref bool used, ref CompletedFrame? published)
    {
        if (synchronized && (lines + n0 > geometry.AcquisitionRows || (n1 != 0 && lines + n0 != geometry.AcquisitionRows)))
        { Reject(); return; }
        blocks++;
        if (!synchronized)
        {
            // Unknown start / corrupted partial frame: discard until an explicit split anchors row 0.
            if (n1 == 0) return;
            lines = 0; synchronized = true;
        }
        else
        {
            if (lines == 0) Array.Clear(live); // New frame: no stale rows from the previous frame.
            Array.Copy(source, 0, current, lines * geometry.Width, n0 * geometry.Width);
            Array.Copy(source, 0, live, lines * geometry.Width, n0 * geometry.Width);
            ProcessedLinesReady?.Invoke(new(channel,geometry.Width,geometry.AcquisitionRows,source,0,lines,n0,completed+1,timestamp,generation));
            lines += n0;
            liveLines = lines;
            if (lines == geometry.AcquisitionRows)
            {
                if (firstCompleteTimestamp == 0) firstCompleteTimestamp = Stopwatch.GetTimestamp();
                published=new(channel, geometry.Width, geometry.AcquisitionRows, current, ++completed, timestamp, generation);
                writer.TryWrite(published);
                current = next; used = true; lines = 0;
            }
        }
        if (n1 != 0 && !(firstFrameOnly && completed!=0))
        {
            // The published old frame remains immutable; the live view now shows the new frame's prefix.
            Array.Clear(live);
            Array.Copy(source, n0 * geometry.Width, current, 0, n1 * geometry.Width);
            Array.Copy(source, n0 * geometry.Width, live, 0, n1 * geometry.Width);
            ProcessedLinesReady?.Invoke(new(channel,geometry.Width,geometry.AcquisitionRows,source,n0*geometry.Width,0,n1,completed+1,timestamp,generation));
            lines = n1; liveLines = n1;
        }
        liveTimestamp = timestamp; liveVersion++;
    }
}

internal sealed class FramePipeline
{
    internal FrameGeometry Geometry { get; }
    internal long Generation { get; }
    internal ChannelFrameAssembler[] Assemblers { get; }
    private readonly Channel<CompletedFrame>[] queues;
    private readonly long[] dropped = new long[4];
    internal long DroppedCompletedQueueFrames(int channel) => Interlocked.Read(ref dropped[channel]);
    internal FramePipeline(FrameGeometry geometry, long generation, InitialAlignment alignment, bool firstFrameOnly = false)
    {
        geometry.Validate(); Geometry = geometry; Generation = generation;
        queues = Enumerable.Range(0, 4).Select(c => Channel.CreateBounded<CompletedFrame>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false },
            _ => Interlocked.Increment(ref dropped[c]))).ToArray();
        Assemblers = Enumerable.Range(0, 4).Select(c => new ChannelFrameAssembler(c, geometry, generation, alignment, queues[c].Writer, firstFrameOnly)).ToArray();
    }
    internal void Accept(CallbackBlock block)
    {
        if ((uint)block.Channel < 4) Assemblers[block.Channel].Accept(block);
    }
    internal void AcceptNative(IntPtr pointer, long session)
    {
        if (pointer == IntPtr.Zero || session != Generation) return;
        var image = Marshal.PtrToStructure<CResampledImage>(pointer);
        if ((uint)image.channel < 4) Assemblers[image.channel].AcceptNative(image, session, System.Diagnostics.Stopwatch.GetTimestamp());
    }
    internal void CollectBuffers(HashSet<Array> buffers)
    {foreach(var a in Assemblers)a.CollectBuffers(buffers);foreach(var q in queues)if(q.Reader.TryPeek(out var f))buffers.Add(f.Data);}
    internal bool TryRead(int channel, out CompletedFrame? frame) => queues[channel].Reader.TryRead(out frame);
    // Caller must first retire the capture and synchronize with its producers/consumer.
    internal void Clear()
    {
        for (int c = 0; c < 4; c++)
        {
            while (queues[c].Reader.TryRead(out _)) { }
            Assemblers[c].Clear();
        }
    }
}
