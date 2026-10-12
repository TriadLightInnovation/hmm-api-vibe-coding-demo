using System.Diagnostics;
using HmmExample.Interop;

namespace HmmExample.Acquisition;

internal sealed record ProgressSnapshot(long Generation, int[] EnabledChannels, double? FirstCallbackMs,
    double?[] FirstCompleteMs, double? AllEnabledCompleteMs, bool HasNewCallback, bool HasNewFrame);

// Callback bookkeeping only. Continuations always run away from the SDK callback stack.
internal sealed class AcquisitionProgress
{
    private readonly object gate = new();
    private readonly bool[] enabled;
    private readonly CompletedFrame?[] first = new CompletedFrame?[4];
    private readonly long[] callbackTicks = new long[4], completeTicks = new long[4];
    private long epoch, allTicks;
    private bool retired;
    internal long Generation { get; }
    internal TaskCompletionSource AllComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource AnyComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal AcquisitionProgress(long generation, WaveParams wave)
    { Generation=generation; enabled=[wave.channel0_enable,wave.channel1_enable,wave.channel2_enable,wave.channel3_enable]; }
    internal bool HasEnabledChannels => enabled.Any(x=>x);
    internal void Arm(long timestamp) { lock(gate) epoch=timestamp; }
    internal void Callback(int channel,long generation,long entered)
    {
        lock(gate)
            if(!retired && generation==Generation && (uint)channel<4 && enabled[channel] && epoch!=0 && entered>=epoch && callbackTicks[channel]==0)
                callbackTicks[channel]=entered;
    }
    internal void Complete(CompletedFrame frame,long timestamp)
    {
        bool all=false;
        lock(gate)
        {
            int c=frame.Channel;
            if(retired || frame.SessionGeneration!=Generation || (uint)c>=4 || !enabled[c] || epoch==0 || frame.Timestamp<epoch
                || timestamp<epoch || callbackTicks[c]==0 || first[c] is not null) return;
            first[c]=frame; completeTicks[c]=timestamp;
            all=Enumerable.Range(0,4).All(i=>!enabled[i] || first[i] is not null);
            if(all) allTicks=completeTicks.Max();
        }
        AnyComplete.TrySetResult();
        if(all) AllComplete.TrySetResult();
    }
    internal CompletedFrame?[] Frames() { lock(gate) return (CompletedFrame?[])first.Clone(); }
    internal double? ElapsedMs(long timestamp) { lock(gate) return epoch==0?null:Stopwatch.GetElapsedTime(epoch,timestamp).TotalMilliseconds; }
    internal void Retire() { lock(gate) retired=true; }
    internal void Fault(string message) { AllComplete.TrySetException(new InvalidOperationException(message)); }
    internal ProgressSnapshot Snapshot()
    {
        lock(gate)
        {
            double? Ms(long t)=>epoch==0 || t==0?null:Stopwatch.GetElapsedTime(epoch,t).TotalMilliseconds;
            long firstCallback=callbackTicks.Where(t=>t!=0).DefaultIfEmpty().Min();
            return new(Generation,Enumerable.Range(0,4).Where(i=>enabled[i]).ToArray(),Ms(firstCallback),completeTicks.Select(Ms).ToArray(),Ms(allTicks),
                firstCallback!=0,first.Any(f=>f is not null));
        }
    }
}
