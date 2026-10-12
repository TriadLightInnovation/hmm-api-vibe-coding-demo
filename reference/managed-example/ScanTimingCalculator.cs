using HmmExample.Interop;

namespace HmmExample;

internal sealed record ScanTiming(int TotalX, int TotalY, double FrequencyHz, double EffectiveLineRateHz,
    double DwellUs, double FrameTimeMs, double FrameRateHz);

// Pure arithmetic only. Frequency is full-cycle Hz, as confirmed by Paz on 2026-09-19.
internal static class ScanTimingCalculator
{
    private static (int X,int Y) Geometry(WaveParams wave)
    {
        if(wave.pixel_per_line<=0 || wave.line_per_frame<=0 || wave.oversample_head<0 || wave.oversample_tail<0 || wave.oversample_frame<0)
            throw new ArgumentException("Invalid timing geometry.");
        return (checked(wave.pixel_per_line+wave.oversample_head+wave.oversample_tail),checked(wave.line_per_frame+wave.oversample_frame));
    }
    private static ScanTiming Result(WaveParams wave,double frequencyHz,double dwellUs)
    {
        var (x,y)=Geometry(wave);
        double rate=frequencyHz; // Uni-directional: one effective image line per full cycle.
        return new(x,y,frequencyHz,rate,dwellUs,1000.0*y/rate,rate/y);
    }
    internal static ScanTiming Internal(WaveParams wave)
    {
        var (x,_)=Geometry(wave);
        if(wave.line_frequency_hz<=0) throw new ArgumentException("Frequency must be positive.");
        return Result(wave,wave.line_frequency_hz,1_000_000.0/(2.0*wave.line_frequency_hz*x));
    }
    internal static double SineBoundarySeconds(int i,int totalX,double measuredFrequencyHz)
    {
        if(totalX<=0 || i<0 || i>totalX || !double.IsFinite(measuredFrequencyHz) || measuredFrequencyHz<=0)
            throw new ArgumentException("Invalid sine boundary/frequency.");
        return Math.Acos(Math.Clamp(1.0-2.0*i/totalX,-1.0,1.0))/(2.0*Math.PI*measuredFrequencyHz);
    }
    internal static ScanTiming? External(WaveParams wave,double measuredFrequencyHz)
    {
        if(!double.IsFinite(measuredFrequencyHz) || measuredFrequencyHz<=0) return null;
        var (x,_)=Geometry(wave);
        double previous=SineBoundarySeconds(0,x,measuredFrequencyHz), shortest=double.PositiveInfinity;
        // Full spatial-boundary calculation; no central approximation in the production path.
        for(int i=1;i<=x;i++)
        {
            double next=SineBoundarySeconds(i,x,measuredFrequencyHz);
            shortest=Math.Min(shortest,next-previous); previous=next;
        }
        return Result(wave,measuredFrequencyHz,shortest*1_000_000.0);
    }
}
