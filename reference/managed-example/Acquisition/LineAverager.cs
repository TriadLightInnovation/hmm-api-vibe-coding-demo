namespace HmmExample.Acquisition;

internal static class LineAverager
{
    internal static CallbackBlock Process(CallbackBlock raw, ExternalParameters parameters)
    {
        var w=parameters.Wave; int n=parameters.LineFactor;
        if(n==1) return raw;
        int rows=checked(w.line_per_frame+w.oversample_frame);
        if(raw.Width!=w.pixel_per_line || raw.Height!=rows || raw.Index0Lines<=0 || raw.Index1Lines<0
            || (long)raw.Index0Lines+raw.Index1Lines!=w.number_of_lines
            || raw.Index0Lines>rows || raw.Index1Lines>=rows
            || raw.Index0Lines%n!=0 || raw.Index1Lines%n!=0 || rows%n!=0
            || ((long)raw.Index0Lines+raw.Index1Lines)*raw.Width!=raw.Data.Length)
            throw new InvalidOperationException($"Line Average metadata error: expected {w.number_of_lines} raw lines, group={n}, geometry={w.pixel_per_line}x{rows}; received n0={raw.Index0Lines}, n1={raw.Index1Lines}, {raw.Width}x{raw.Height}, size={raw.Data.Length}. Block rejected; no remainder inferred.");
        var data=Average(raw.Data,raw.Width,n);
        return raw with {Height=rows/n,Index0Lines=raw.Index0Lines/n,Index1Lines=raw.Index1Lines/n,Data=data};
    }
    internal static float[] Average(float[] raw,int width,int n)
    {
        if(width<=0 || n<=0 || raw.Length%checked(width*n)!=0) throw new ArgumentException("Raw rows must form exact complete line-average groups.");
        if(n==1) return raw;
        int rows=raw.Length/width/n; var output=HostMemoryAdmissionPolicy.Floats(checked((long)rows*width));
        for(int y=0;y<rows;y++) for(int x=0;x<width;x++)
        {
            double sum=0; for(int k=0;k<n;k++) sum+=raw[((y*n+k)*width)+x];
            output[y*width+x]=(float)(sum/n);
        }
        return output;
    }
}
