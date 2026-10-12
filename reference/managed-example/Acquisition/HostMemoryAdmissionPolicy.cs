using System.Runtime.InteropServices;
using HmmExample.Interop;

namespace HmmExample.Acquisition;

internal sealed record HostMemoryOptions(long ManagedBudgetBytes = 4L*1024*1024*1024,
    long MinimumHostReserve = 16L*1024*1024*1024);
internal sealed record SystemMemorySnapshot(long AvailablePhysicalBytes,long AvailableCommitBytes);
internal sealed record ManagedMemoryEstimate(long RawCallbackSamples,long ProcessedBlockSamples,long ProcessedFrameSamples,
    Dictionary<string,long> Components,long PayloadBytes,long HeadroomBytes,long EstimatedBytes,long KnownRetainedBytes);
internal sealed record MemoryAdmission(ManagedMemoryEstimate Estimate,HostMemoryOptions Policy,SystemMemorySnapshot System,
    bool Passed,string Result,string NativeGeometry)
{
    internal string Describe() => $"Estimated Managed Memory: {Estimate.EstimatedBytes/(double)(1L<<30):F3} GiB (includes allocation headroom)\n"+
        $"Host Memory Budget: {Policy.ManagedBudgetBytes/(double)(1L<<30):F2} GiB\nAvailable Physical Memory: {System.AvailablePhysicalBytes/(double)(1L<<30):F2} GiB\n"+
        $"Available Commit: {System.AvailableCommitBytes/(double)(1L<<30):F2} GiB\nMinimum Host Reserve: {Policy.MinimumHostReserve/(double)(1L<<30):F2} GiB\n"+
        $"Admission Result: {Result}\nNative Geometry: {NativeGeometry}\nPreview reservation: 2048 max edge and Composite enabled; permits display-only setting changes.\n";
    internal void RequirePass() {if(!Passed) throw new InvalidOperationException(Result);}
}

// Host policy only. Neither these limits nor an admission PASS establish SDK/FPGA capability.
internal static class HostMemoryAdmissionPolicy
{
    internal const long MiB=1024L*1024;
    internal const long SingleAllocationBytes=1L<<30;
    internal static int ArrayLength(long samples,int elementBytes)
    {
        long bytes=checked(samples*elementBytes);
        if(samples<=0 || elementBytes<=0 || samples>Array.MaxLength || bytes>SingleAllocationBytes)
            throw new ArgumentException("Host single allocation safety limit exceeded (1 GiB / legal CLR array length).");
        return checked((int)samples);
    }
    internal static float[] Floats(long count)=>new float[ArrayLength(count,sizeof(float))];
    internal static double[] Doubles(long count)=>new double[ArrayLength(count,sizeof(double))];
    internal static byte[] Bytes(long count)=>new byte[ArrayLength(count,1)];
    internal static long BufferBytes(IEnumerable<Array> arrays)
    {
        long bytes=0;foreach(var a in arrays.Distinct(ReferenceEqualityComparer.Instance))
            bytes=checked(bytes+Buffer.ByteLength((Array)a!));
        return bytes;
    }
    internal static long AverageQueueBytes(int framePixels,int callbackPixels,bool internalMode)
    {
        long block=checked((long)callbackPixels*4);
        // Keep the existing small-image 128 MiB ceiling; admit at least one full frame/channel.
        // Entry count remains <=1024; at 4096 External the byte ceiling is exactly four frames.
        return internalMode?Math.Max(checked(4*block),Math.Min(128*MiB,checked(1024*block))):Math.Max(128*MiB,checked(4L*framePixels*4));
    }
    internal static ManagedMemoryEstimate Estimate(ScanRequest request,bool frameAverage=false,bool composite=true,int previewMax=2048,long retained=0)
    {
        request.Validate();
        if(retained<0) throw new ArgumentOutOfRangeException(nameof(retained));
        checked
        {
            int rows=request.YPixel+request.OversampleFrame;
            long samples=(long)request.XPixel*rows;
            long processedBlock=(long)request.XPixel*request.NumberOfLines;
            long raw=request.InternalMode?processedBlock*request.LineFactor:samples;
            int pixels=ArrayLength(samples,4),callback=ArrayLength(raw,4);
            long f=samples*4,b=raw*4,p=processedBlock*4;
            var preview=PreviewSampling.Create(request.XPixel,rows,previewMax);
            long g=preview.PixelCount,s=g*4;
            var c=new Dictionary<string,long>();
            if(request.InternalMode)
            {
                c["Callback copied blocks (latest + one concurrent/channel)"]=8*b;
                c["Assembler current/live/spare (four channels)"]=12*f;
                c["Latest complete queue (four channels)"]=4*f;
                c["Line Average temporary processed blocks"]=request.LineFactor>1?4*p:0;
                c["Preview float snapshots"]=4*s;
            }
            else
            {
                c["Latest/raw shared arrays (four channels, counted once)"]=4*f;
                c["Concurrent callbacks (one/channel)"]=4*f;
                c["UI in-flight source"]=f;
                c["Preview float snapshot"]=s;
            }
            c["First complete frame retention"]=4*f;
            c["Statistics queue 4 + worker 1"]=5*f;
            c["Gray8 sources + bitmap backing + transient"]=9*g;
            c["Composite BGRA + bitmap"]=composite?8*g:0;
            if(frameAverage)
            {
                ArrayLength(samples,8);
                c["Frame Average double sums (four channels)"]=8*f;
                c["Frame Average display (four channels)"]=4*f;
                c["Frame Average output + new display transient"]=(request.InternalMode?p:f)+f;
                c["Frame Average worker source"]=request.InternalMode?b:f;
                c["Frame Average queue"]=AverageQueueBytes(pixels,callback,request.InternalMode);
                c["Frame Average preview snapshot"]=s;
            }
            c["Known previous buffers (unique arrays)"]=retained;
            long payload=0;foreach(long v in c.Values)payload+=v;
            long headroom=(payload+3)/4+4*f; // 25% + one four-channel frame turnover.
            return new(raw,processedBlock,samples,c,payload,headroom,payload+headroom,retained);
        }
    }
    internal static MemoryAdmission Evaluate(ScanRequest request,bool average,SystemMemorySnapshot available,
        long retained=0,HostMemoryOptions? options=null,bool composite=true,int previewMax=2048)
    {
        var policy=options??new();
        if(policy.ManagedBudgetBytes<=0 || policy.MinimumHostReserve<0) throw new ArgumentException("Invalid Host Memory Budget policy.");
        var estimate=Estimate(request,average,composite,previewMax,retained);
        // Retained bytes are already resident; still charged to the managed total, not allocated again.
        long additional=checked(estimate.EstimatedBytes-retained);
        string result=estimate.EstimatedBytes>policy.ManagedBudgetBytes?"Host Managed Memory Budget Exceeded":
            available.AvailablePhysicalBytes<policy.MinimumHostReserve || available.AvailableCommitBytes<policy.MinimumHostReserve ||
            additional>available.AvailablePhysicalBytes-policy.MinimumHostReserve || additional>available.AvailableCommitBytes-policy.MinimumHostReserve?
            "Insufficient Available Host Memory":"PASS";
        string native=$"{request.XPixel} x {checked(request.YPixel*request.LineFactor)} + {checked(request.OversampleFrame*request.LineFactor)} Dummy rows";
        if(request.CapabilityNotice.Length>0) native+=" — Native SDK / FPGA Geometry Not Yet Validated; Hardware Validation Required";
        return new(estimate,policy,available,result=="PASS",result,native);
    }
    internal static bool CallbackFits(CResampledImage m,ExternalParameters parameters)
    {
        var w=parameters.Wave;
        long rows=(long)w.line_per_frame+w.oversample_frame,n=(long)m.index0_line_number+m.index1_line_number;
        if(m.image_data==IntPtr.Zero || m.width!=w.pixel_per_line || m.height!=rows || m.image_size<=0) return false;
        long expected=checked((long)w.pixel_per_line*(w.line_mode?w.number_of_lines:rows));
        if(m.image_size>expected || m.index0_line_number<=0 || m.index1_line_number<0 || m.index0_line_number>rows || m.index1_line_number>=rows) return false;
        if(w.line_mode)
        {
            int factor=parameters.LineFactor;
            // N=1 retains the verified partial-block semantics; averaged blocks require exact groups.
            return n<=w.number_of_lines && n*m.width==m.image_size &&
                (factor==1 || (n==w.number_of_lines && m.index0_line_number%factor==0 && m.index1_line_number%factor==0));
        }
        return m.image_size==expected && m.index0_line_number==rows && m.index1_line_number==0;
    }
}

internal static class WindowsHostMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Status
    {
        internal uint Length,Load;
        internal ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtended;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private delegate bool Query(ref Status status);
    // Explicit system module, independent of the offline tests' SDK-blocking DllImport resolver.
    internal static SystemMemorySnapshot Read()
    {
        IntPtr module=NativeLibrary.Load(Path.Combine(Environment.SystemDirectory,"kernel32.dll"));
        try
        {
            var query=Marshal.GetDelegateForFunctionPointer<Query>(NativeLibrary.GetExport(module,"GlobalMemoryStatusEx"));
            var s=new Status {Length=(uint)Marshal.SizeOf<Status>()};
            if(!query(ref s)) throw new InvalidOperationException("Windows memory preflight unavailable; admission blocked.");
            return new(checked((long)s.AvailablePhysical),checked((long)s.AvailablePageFile));
        }
        finally {NativeLibrary.Free(module);}
    }
}
