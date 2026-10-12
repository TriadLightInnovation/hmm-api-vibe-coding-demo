using System.Diagnostics;
using System.Runtime.InteropServices;
using HmmExample.Interop;

namespace HmmExample.Acquisition;

internal sealed record CallbackMetadata(long Generation, long Timestamp, int Channel, int Width, int Height, int ImageSize,
    int Index0LineNumber, int Index0, int Index1LineNumber, int Index1, int ConfiguredWidth, int ConfiguredHeight,
    int OversampleFrame, string Classification);
internal sealed record ExternalChannelSnapshot(int Channel, long Callbacks, long ReceivedFrames, long DisplayedFrames,
    long DroppedUiFrames, long DisplayBlockedFrames, long RejectedCallbacks, double? FirstCallbackMs, CallbackMetadata? LastMetadata, string DisplayStatus,
    double? FirstCompleteFrameMs = null, int AccumulatedLines = 0, int WritePosition = 0, long FrameSequence = 0, bool? Synchronized = null, long Blocks = 0,
    long DisplayedUpdates = 0, long DroppedUiUpdates = 0, double? FirstDisplayUpdateMs = null,
    long LiveVersion = 0, int LiveAccumulatedLines = 0, int AcquisitionRows = 0, long DroppedCompletedQueueFrames = 0)
{
    public long CompletedFrames => ReceivedFrames;
}
internal sealed record ExternalCaptureSnapshot(long Generation, double? FirstAnyCallbackMs, ExternalChannelSnapshot[] Channels, CallbackMetadata[] RecentMetadata,
    double? FirstCompleteFrameMs = null, string Mode = "External Trigger", bool RawIncludesOverscan = false, double? FirstDisplayUpdateMs = null, FrameStatisticsSnapshot? FrameStatistics = null);

internal interface IAcquisitionCapture
{
    ExternalParameters Parameters { get; }
    DisplayAverager Averaging { get; }
    long ArmStart();
    void Receive(IntPtr pointer, long callbackGeneration, long currentGeneration);
    bool TryTake(int index, out CompletedFrame? frame);
    void Displayed(int index);
    void DisplayBlocked(int index, string reason);
    void CollectBuffers(HashSet<Array> buffers);
    ExternalCaptureSnapshot Snapshot();
    ExternalCaptureSnapshot FreezeAndSnapshot();
}

internal sealed class ExternalCapture : IAcquisitionCapture
{
    private sealed class ChannelState
    {
        internal readonly object Gate = new();
        internal readonly object CopyGate = new();
        internal long Callbacks, Received, Displayed, Dropped, DisplayBlocked, Rejected, FirstTicks, FirstCompleteTicks, FirstDisplayTicks, LastPublishedTicks;
        internal CallbackMetadata? Last;
        internal CompletedFrame? Latest, RawBuffer;
        internal string DisplayStatus = "No complete frame";
    }
    private readonly ChannelState[] channels = Enumerable.Range(0,4).Select(_ => new ChannelState()).ToArray();
    private readonly object ringGate = new();
    private readonly Queue<CallbackMetadata> ring = new();
    private readonly RollingDiagnosticLog log;
    private readonly AcquisitionProgress? progress;
    private long startTicks, firstAnyTicks;
    private int active=1, armed;
    internal long Generation { get; }
    public ExternalParameters Parameters { get; }
    public DisplayAverager Averaging { get; }
    internal CompletedFrameStatistics Statistics { get; }
    internal ExternalCapture(long generation, ExternalParameters parameters, RollingDiagnosticLog log, AcquisitionProgress? progress = null)
    { Statistics=new(m=>log.Event(generation,m)); Generation=generation; Parameters=parameters; this.log=log; this.progress=progress; Averaging=new(GeometryMath.Pixels(parameters.Wave.pixel_per_line,checked(parameters.OutputHeight+parameters.OutputDummy)),m=>log.Event(generation,m)); }
    // Called immediately before startScan; callbacks during that API are included.
    public long ArmStart()
    {
        long timestamp=Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref startTicks,timestamp); progress?.Arm(timestamp); Volatile.Write(ref armed,1);
        return timestamp;
    }
    private static double? Since(long start, long value) => start==0 || value==0 ? null : Stopwatch.GetElapsedTime(start,value).TotalMilliseconds;
    public void Receive(IntPtr pointer, long callbackGeneration, long currentGeneration)
    {
        long timestamp=Stopwatch.GetTimestamp();
        long epoch=Interlocked.Read(ref startTicks);
        bool afterStart=Volatile.Read(ref armed)!=0 && epoch!=0 && timestamp>=epoch;
        if (callbackGeneration!=Generation || currentGeneration!=Generation || Volatile.Read(ref active)==0) return;
        if (pointer==IntPtr.Zero) { Record(new(Generation,timestamp,-1,0,0,0,0,0,0,0,Parameters.Wave.pixel_per_line,Parameters.Wave.line_per_frame,Parameters.Wave.oversample_frame,"Null metadata pointer")); return; }
        var m=Marshal.PtrToStructure<CResampledImage>(pointer);
        var w=Parameters.Wave;
        bool channelValid=(uint)m.channel<4;
        bool shapeValid=HostMemoryAdmissionPolicy.CallbackFits(m,Parameters);
        string classification = !channelValid ? "Invalid channel" : !shapeValid ? "Metadata mismatch: callback exceeds or differs from applied geometry / pointer" : "Copied metadata; awaiting classification";
        var metadata=new CallbackMetadata(Generation,timestamp,m.channel,m.width,m.height,m.image_size,m.index0_line_number,m.index0,
            m.index1_line_number,m.index1,w.pixel_per_line,w.line_per_frame,w.oversample_frame,classification);
        if (!channelValid) { Record(metadata); return; }
        var channel=channels[m.channel];
        if(!Monitor.TryEnter(channel.CopyGate)) throw new InvalidOperationException($"CH{m.channel+1} overlapping callback rejected: host permits one copied frame/channel at a time.");
        try
        {
        bool first=false;
        lock(channel.Gate)
        {
            if (Volatile.Read(ref active)==0) return;
            channel.Callbacks++;
            // First data means valid metadata + native data pointer, independent of successful display/crop.
            if (shapeValid && afterStart)
            {
                first=channel.FirstTicks==0;
                if(first || timestamp<channel.FirstTicks) channel.FirstTicks=timestamp;
                long prior;
                do { prior=Interlocked.Read(ref firstAnyTicks); if(prior!=0 && prior<=timestamp) break; }
                while(Interlocked.CompareExchange(ref firstAnyTicks,timestamp,prior)!=prior);
            }
        }
        if(shapeValid && afterStart) progress?.Callback(m.channel,Generation,timestamp);
        if (first) log.First(Generation,m.channel,"FirstCallback",$" {Since(Interlocked.Read(ref startTicks),timestamp):F3} ms; {m.width}x{m.height}, size={m.image_size}, index0Lines={m.index0_line_number}, index1Lines={m.index1_line_number}");
        if (!shapeValid)
        {
            Averaging.Invalidate("External callback has invalid dimensions / size / data pointer");
            lock(channel.Gate) { channel.Rejected++; channel.Last=metadata; channel.DisplayStatus=classification; }
            log.Event(Generation,$"CH{m.channel+1} {classification}; width={m.width}, height={m.height}, size={m.image_size}, n0={m.index0_line_number}, n1={m.index1_line_number}");
            Record(metadata); return;
        }
        var copy=HostMemoryAdmissionPolicy.Floats(m.image_size);
        Marshal.Copy(m.image_data,copy,0,copy.Length); // All native floats copied before returning. No WPF or disk I/O.
        int rows=checked(w.line_per_frame+w.oversample_frame);
        bool complete=m.width==w.pixel_per_line && m.height==rows && (long)m.width*m.height==m.image_size
            && m.index0_line_number==rows && m.index1_line_number==0;
        classification=complete ? "Complete acquisition frame; exact configured geometry and row metadata" : "Metadata mismatch: retained raw buffer, no display; needs Paz review";
        metadata=metadata with { Classification=classification };
        if(!complete && afterStart) Averaging.Invalidate("External complete frame metadata mismatch");
        // External is frame-oriented and never performs Managed Line Average.
        CompletedFrame? published=null; bool firstComplete=false;
        lock(channel.Gate)
        {
            if (Volatile.Read(ref active)==0) return;
            bool changed=channel.Last?.Classification!=classification;
            channel.Last=metadata;
            // Retain latest raw acquisition buffer, including any overscan or unexpected shape.
            var owned=new CompletedFrame(m.channel,m.width,m.height,copy,channel.Received+1,timestamp,Generation);
            if(channel.RawBuffer is null || channel.RawBuffer.Timestamp<=timestamp) channel.RawBuffer=owned;
            if (complete && afterStart)
            {
                if(channel.FirstCompleteTicks==0) {channel.FirstCompleteTicks=Stopwatch.GetTimestamp();firstComplete=true;}
                channel.Received++; published=owned with {FrameSequence=channel.Received,Height=rows,Data=copy};
                if(timestamp<channel.LastPublishedTicks) channel.Dropped++;
                else
                {
                    if(channel.Latest is not null) channel.Dropped++;
                    channel.Latest=published;
                    channel.LastPublishedTicks=timestamp;
                }
            }
            else if (!complete) { channel.Rejected++; channel.DisplayStatus=classification; }
            if(published is not null) Averaging.Publish(published); // Preserve assigned per-channel sequence order. Enqueue only.
            if (changed && !complete) log.Event(Generation,$"CH{m.channel+1} metadata mismatch: width={m.width}, height={m.height}, size={m.image_size}, n0={m.index0_line_number}, n1={m.index1_line_number}; configured={w.pixel_per_line}x{w.line_per_frame}+{w.oversample_frame}");
        }
        if(firstComplete) log.First(Generation,m.channel,"FirstCompleteFrame",$"{Since(epoch,Stopwatch.GetTimestamp()):F3} ms");
        if(published is not null) { progress?.Complete(published,Stopwatch.GetTimestamp()); Statistics.Publish(published); }
        Record(metadata);
        } finally {Monitor.Exit(channel.CopyGate);}
    }
    private void Record(CallbackMetadata metadata) { lock(ringGate) { if(ring.Count==100) ring.Dequeue(); ring.Enqueue(metadata); } }
    public bool TryTake(int index, out CompletedFrame? frame)
    {
        var channel=channels[index];
        lock(channel.Gate) { frame=channel.Latest; channel.Latest=null; return Volatile.Read(ref active)!=0 && frame is not null; }
    }
    public void Displayed(int index) { lock(channels[index].Gate) { channels[index].Displayed++; if(channels[index].FirstDisplayTicks==0) channels[index].FirstDisplayTicks=Stopwatch.GetTimestamp(); channels[index].DisplayStatus="Complete frame displayed"; } }
    public void DisplayBlocked(int index, string reason)
    {
        lock(channels[index].Gate)
        {
            if(channels[index].DisplayStatus!=reason) log.Event(Generation,$"CH{index+1} display blocked: {reason}");
            channels[index].DisplayStatus=reason; channels[index].DisplayBlocked++;
        }
    }
    public void CollectBuffers(HashSet<Array> buffers)
    {
        foreach(var c in channels) lock(c.Gate) {if(c.Latest is { } latest)buffers.Add(latest.Data);if(c.RawBuffer is { } raw)buffers.Add(raw.Data);}
        Averaging.CollectBuffers(buffers);Statistics.CollectBuffers(buffers);
    }
    public ExternalCaptureSnapshot Snapshot()
    {
        long start=Interlocked.Read(ref startTicks);
        var states=channels.Select((c,i) => { lock(c.Gate) return new ExternalChannelSnapshot(i,c.Callbacks,c.Received,c.Displayed,c.Dropped,c.DisplayBlocked,c.Rejected,Since(start,c.FirstTicks),c.Last,c.DisplayStatus,Since(start,c.FirstCompleteTicks),FirstDisplayUpdateMs:Since(start,c.FirstDisplayTicks)); }).ToArray();
        CallbackMetadata[] metadata; lock(ringGate) metadata=ring.ToArray();
        return new(Generation,Since(start,Interlocked.Read(ref firstAnyTicks)),states,metadata,states.Min(c=>c.FirstCompleteFrameMs),FirstDisplayUpdateMs:states.Min(c=>c.FirstDisplayUpdateMs),FrameStatistics:Statistics.Snapshot());
    }
    internal void DeactivateAndClear()
    {
        Statistics.Retire();
        Averaging.Retire();
        Volatile.Write(ref active,0); Volatile.Write(ref armed,0);
        foreach(var c in channels) lock(c.Gate) { c.Latest=null; c.RawBuffer=null; c.FirstTicks=0; c.FirstCompleteTicks=0; c.LastPublishedTicks=0; }
        Interlocked.Exchange(ref firstAnyTicks,0); Interlocked.Exchange(ref startTicks,0);
        // Recent raw metadata remains available in the archived stop report; no float arrays retained.
    }
    public ExternalCaptureSnapshot FreezeAndSnapshot()
    {
        Volatile.Write(ref active,0);
        foreach(var c in channels) lock(c.CopyGate) { } // Drain admitted copies before clearing ownership.
        var report=Snapshot();
        DeactivateAndClear();
        return report;
    }
}
