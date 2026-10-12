using System.Diagnostics;
using System.Threading.Channels;

namespace HmmExample.Acquisition;

internal sealed record FrameStatistics(int Channel, long Generation, long FrameSequence, long FrameTimestamp,
    DateTimeOffset CalculatedAt, int Width, int Height, float? FiniteMin, float? FiniteMax,
    double? FiniteMean, long FiniteCount, long NonFiniteCount);
internal sealed record FrameStatisticsSnapshot(string Sampling, FrameStatistics?[] Latest, long SkippedSamples);

// Independent diagnostic worker. Never mutates image data, rendering, averaging or acquisition queues.
internal sealed class CompletedFrameStatistics
{
    private readonly object gate = new();
    private readonly Channel<CompletedFrame> queue = Channel.CreateBounded<CompletedFrame>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly long[] sampledAt = new long[4];
    private readonly FrameStatistics?[] latest = new FrameStatistics?[4];
    private readonly Action<string> diagnostic;
    private bool retired;
    private readonly HashSet<CompletedFrame> retained=new(ReferenceEqualityComparer.Instance);
    private long skipped;
    internal CompletedFrameStatistics(Action<string> diagnostic) { this.diagnostic = diagnostic; _ = Task.Run(Run); }
    internal void Publish(CompletedFrame frame, long completedAt = 0)
    {
        long now = Stopwatch.GetTimestamp();
        lock (gate)
        {
            if (retired || (uint)frame.Channel >= 4) return;
            if ((sampledAt[frame.Channel] != 0 && Stopwatch.GetElapsedTime(sampledAt[frame.Channel], now).TotalSeconds < 1)
                || !queue.Writer.TryWrite(frame)) { skipped++; return; }
            retained.Add(frame);sampledAt[frame.Channel] = now;
        }
    }
    private async Task Run()
    {
        await foreach (var frame in queue.Reader.ReadAllAsync())
        {
            try
            {
            lock (gate) if (retired) continue;
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            double sum = 0; long finite = 0, nonfinite = 0;
            foreach (float value in frame.Data)
            {
                if (!float.IsFinite(value)) { nonfinite++; continue; }
                min = Math.Min(min, value); max = Math.Max(max, value); sum += value; finite++;
            }
            var result = new FrameStatistics(frame.Channel, frame.SessionGeneration, frame.FrameSequence, frame.Timestamp,
                DateTimeOffset.Now, frame.Width, frame.Height, finite == 0 ? null : min, finite == 0 ? null : max,
                finite == 0 ? null : sum / finite, finite, nonfinite);
            string message="FRAME_STATS " + System.Text.Json.JsonSerializer.Serialize(result, RollingDiagnosticLog.Json);
            // Enqueue before Retire returns, so closing the persistent log cannot race a late sample.
            lock (gate) { if (retired) continue; latest[frame.Channel] = result; diagnostic(message); }
            } finally {lock(gate) retained.Remove(frame);}
        }
    }
    internal FrameStatisticsSnapshot Snapshot()
    { lock (gate) return new("First complete frame, then at most 1 sample/channel/second; bounded diagnostic queue; skipped samples are not acquisition loss", (FrameStatistics?[])latest.Clone(), skipped); }
    internal void CollectBuffers(HashSet<Array> buffers) {lock(gate)foreach(var f in retained)buffers.Add(f.Data);}
    internal void Retire() { lock (gate) retired = true; queue.Writer.TryComplete(); }
}
