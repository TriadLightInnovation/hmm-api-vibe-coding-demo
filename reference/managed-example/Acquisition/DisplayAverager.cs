using System.Threading.Channels;

namespace HmmExample.Acquisition;

internal sealed record FrameAverageOptions(bool Enabled=false,int Count=3)
{
    internal void Validate(int pixels)
    {
        // Total cost is admitted by HostMemoryAdmissionPolicy; validate individual sum arrays here.
        if(Enabled) HostMemoryAdmissionPolicy.ArrayLength(pixels,sizeof(double));
        if(Count is <1 or >1024)
            throw new ArgumentException("Frame Average N must be 1..1024.");
    }
}
// Data belongs to an immutable native-copy/line-average block or completed External frame.
// A split callback produces two slices with independent destination row positions/frame sequences.
internal sealed record ProcessedLineSegment(int Channel,int Width,int Height,float[] Data,int SourceOffset,
    int StartRow,int LineCount,long FrameSequence,long Timestamp,long SessionGeneration);
internal sealed record AveragedLineUpdate(ProcessedLineSegment Source,float[] Data,bool NewDisplay,long BatchSequence);

// Worker-owned state: no ring, no old-frame subtraction, no overlapping output batches.
internal sealed class FrameBatchAccumulator
{
    private readonly int batchSize;
    private double[]? sum;
    private long? frameSequence,generation;
    private int width,height,nextRow;
    internal int BatchFrameIndex {get;private set;}
    internal long CompletedBatches {get;private set;}
    internal double[]? SumBuffer => sum;
    internal FrameBatchAccumulator(int count) {if(count<1) throw new ArgumentOutOfRangeException(nameof(count));batchSize=count;}
    internal AveragedLineUpdate? Add(ProcessedLineSegment segment)
    {
        if(segment.Width<=0 || segment.Height<=0 || segment.StartRow<0 || segment.LineCount<=0
            || (long)segment.StartRow+segment.LineCount>segment.Height || segment.SourceOffset<0
            || (long)segment.SourceOffset+(long)segment.Width*segment.LineCount>segment.Data.Length)
            throw new InvalidOperationException("Invalid processed line segment; average batch halted.");
        // A setting change during a frame starts collection only at the next confirmed row zero.
        if(frameSequence is null && segment.StartRow!=0) return null;
        if(frameSequence is null)
        {
            width=segment.Width;height=segment.Height;generation=segment.SessionGeneration;
            sum=HostMemoryAdmissionPolicy.Doubles(checked((long)width*height));frameSequence=segment.FrameSequence;
        }
        if(segment.Width!=width || segment.Height!=height || segment.SessionGeneration!=generation
            || segment.FrameSequence!=frameSequence || segment.StartRow!=nextRow)
            throw new InvalidOperationException("Processed frame sequence/row gap or geometry change; average batch halted instead of guessing.");
        bool nth=BatchFrameIndex==batchSize-1;
        float[]? output=nth?HostMemoryAdmissionPolicy.Floats(checked((long)width*segment.LineCount)):null;
        int destination=segment.StartRow*width;
        for(int i=0;i<width*segment.LineCount;i++)
        {
            double value=segment.Data[segment.SourceOffset+i];
            if(nth) output![i]=(float)((sum![destination+i]+value)/batchSize);
            else sum![destination+i]+=value;
        }
        var update=nth?new AveragedLineUpdate(segment,output!,segment.StartRow==0,CompletedBatches+1):null;
        nextRow+=segment.LineCount;
        if(nextRow==height)
        {
            nextRow=0;frameSequence++;BatchFrameIndex++;
            if(BatchFrameIndex==batchSize) {Array.Clear(sum!);BatchFrameIndex=0;CompletedBatches++;}
        }
        return update;
    }
}
internal sealed record AverageDisplayState(FrameAverageOptions Options,long Revision,int[] Collected,string? Error,
    long[] CompletedBatches,int[] DisplayLines);

// SDK callbacks enqueue processed segments only. The worker accumulates every segment before
// display coalescing. UI receives a synchronized full-size snapshot, never an actively written array.
internal sealed class DisplayAverager
{
    private sealed record Work(long Revision,FrameAverageOptions Options,ProcessedLineSegment Segment);
    private sealed class DisplayState
    {
        internal float[] Data=[];
        internal int Width,Height,Lines;
        internal long Version,TakenVersion,BatchSequence,Timestamp,Generation;
    }
    private readonly object gate=new();
    private readonly Channel<Work> queue=Channel.CreateBounded<Work>(new BoundedChannelOptions(1024){FullMode=BoundedChannelFullMode.Wait,SingleReader=true});
    private readonly DisplayState[] displays=Enumerable.Range(0,4).Select(_=>new DisplayState()).ToArray();
    private readonly int[] collected=new int[4];
    private readonly long[] completedBatches=new long[4];
    private readonly int pixels;
    private readonly Action<string> diagnostic;
    private FrameAverageOptions options=new();
    private long revision,queuedBytes;
    private readonly long queueBudget;
    private readonly Dictionary<float[],int> queuedArrays=new(ReferenceEqualityComparer.Instance);
    private FrameBatchAccumulator[] workerStates=[];
    private float[]? workerSource;
    private bool retired;
    private string? error;
    internal DisplayAverager(int pixels,Action<string> diagnostic,int callbackPixels=0,bool internalMode=false) {this.pixels=pixels;this.diagnostic=diagnostic;queueBudget=HostMemoryAdmissionPolicy.AverageQueueBytes(pixels,callbackPixels==0?pixels:callbackPixels,internalMode);_=Run();}
    private void ClearDisplays()
    {
        foreach(var d in displays) {d.Data=[];d.Lines=0;d.TakenVersion=d.Version;}
        Array.Clear(collected);Array.Clear(completedBatches);
    }
    internal void Configure(FrameAverageOptions value)
    {
        value.Validate(pixels);
        lock(gate) {if(options==value) return;options=value;revision++;ClearDisplays();error=null;if(!value.Enabled && workerSource is null) workerStates=[];}
    }
    internal AverageDisplayState Snapshot()
    {lock(gate) return new(options,revision,(int[])collected.Clone(),error,(long[])completedBatches.Clone(),displays.Select(d=>d.Lines).ToArray());}
    internal void Invalidate(string reason)
    {
        lock(gate) {if(retired || !options.Enabled || error is not null) return;error=reason;revision++;ClearDisplays();}
        diagnostic("Frame Batch Average: "+reason);
    }
    internal void Publish(CompletedFrame frame)=>Publish(new ProcessedLineSegment(frame.Channel,frame.Width,frame.Height,frame.Data,0,0,frame.Height,frame.FrameSequence,frame.Timestamp,frame.SessionGeneration));
    internal void Publish(ProcessedLineSegment segment)
    {
        string? failure=null;
        lock(gate)
        {
            if(retired || !options.Enabled || error is not null) return;
            long bytes=queuedArrays.ContainsKey(segment.Data)?0:checked(segment.Data.LongLength*4); // Shared split-block ownership counted once.
            if((uint)segment.Channel>=4 || queuedBytes+bytes>queueBudget || !queue.Writer.TryWrite(new(revision,options,segment)))
            {error=failure="Frame Average processing backlog/invalid channel; batch halted. Reset Frame Average to resume; no silent skipped data.";revision++;ClearDisplays();}
            else {queuedBytes+=bytes;queuedArrays.TryGetValue(segment.Data,out int refs);queuedArrays[segment.Data]=refs+1;}
        }
        if(failure is not null) diagnostic(failure);
    }
    private async Task Run()
    {
        long workerRevision=-1;
        await foreach(var item in queue.Reader.ReadAllAsync())
        {
            lock(gate) {
                if(--queuedArrays[item.Segment.Data]==0) {queuedArrays.Remove(item.Segment.Data);queuedBytes-=item.Segment.Data.LongLength*4;}
                if(retired || revision!=item.Revision || error is not null) continue;
                workerSource=item.Segment.Data;
                if(workerRevision!=item.Revision) {workerStates=Enumerable.Range(0,4).Select(_=>new FrameBatchAccumulator(item.Options.Count)).ToArray();workerRevision=item.Revision;}
            }
            try
            {
                int channel=item.Segment.Channel;var state=workerStates[channel];
                var update=state.Add(item.Segment);
                // New Nth frame gets a fresh black full-size display buffer; allocation stays outside locks.
                float[]? cleared=update?.NewDisplay==true?HostMemoryAdmissionPolicy.Floats(checked((long)item.Segment.Width*item.Segment.Height)):null;
                lock(gate) if(!retired && revision==item.Revision && error is null)
                {
                    collected[channel]=state.BatchFrameIndex;completedBatches[channel]=state.CompletedBatches;
                    if(update is not null)
                    {
                        var d=displays[channel];var s=update.Source;
                        if(cleared is not null) {d.Data=cleared;d.Lines=0;}
                        Array.Copy(update.Data,0,d.Data,s.StartRow*s.Width,update.Data.Length);
                        d.Width=s.Width;d.Height=s.Height;d.Lines=s.StartRow+s.LineCount;d.BatchSequence=update.BatchSequence;
                        d.Timestamp=s.Timestamp;d.Generation=s.SessionGeneration;d.Version++;
                    }
                }
            }
            catch(Exception e)
            {
                bool report=false;
                lock(gate) if(!retired && revision==item.Revision) {error=e.Message;revision++;ClearDisplays();report=true;}
                if(report) diagnostic("Frame Batch Average: "+e.Message);
            }
            finally {lock(gate) {workerSource=null;if(retired || !options.Enabled) workerStates=[];}}
        }
    }
    internal bool TryTakeLive(int channel,out LiveFrameSnapshot? snapshot,int previewMaxDimension=0)
    {
        snapshot=null;int length;long observedRevision;PreviewSampling sampling;
        lock(gate)
        {
            var d=displays[channel];if(retired || error is not null || d.Data.Length==0 || d.Version<=d.TakenVersion) return false;
            length=d.Data.Length;observedRevision=revision;sampling=PreviewSampling.Create(d.Width,d.Height,previewMaxDimension);
        }
        var copy=HostMemoryAdmissionPolicy.Floats(sampling.PixelCount);
        lock(gate)
        {
            var d=displays[channel];if(retired || error is not null || revision!=observedRevision || d.Data.Length!=length || d.Version<=d.TakenVersion) return false;
            sampling.Copy(d.Data,copy);d.TakenVersion=d.Version;
            snapshot=new(channel,sampling.Width,sampling.Height,copy,d.Version,sampling.AcquiredRows(d.Lines),d.BatchSequence,d.Timestamp,d.Generation);return true;
        }
    }
    internal void CollectBuffers(HashSet<Array> buffers)
    {lock(gate) {foreach(var d in displays) buffers.Add(d.Data);foreach(var a in queuedArrays.Keys)buffers.Add(a);if(workerSource is not null)buffers.Add(workerSource);foreach(var state in workerStates)if(state.SumBuffer is { } sum)buffers.Add(sum);}}
    internal void Retire() {lock(gate) {retired=true;revision++;ClearDisplays();if(workerSource is null) workerStates=[];}queue.Writer.TryComplete();}
}
