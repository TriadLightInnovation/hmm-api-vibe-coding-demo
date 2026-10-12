using HmmExample.Acquisition;

namespace HmmExample;

internal sealed partial class SdkSession
{
    internal HostMemoryOptions MemoryPolicy {get;set;} = new(MinimumHostReserve: ReadHostReserve());
    internal Func<SystemMemorySnapshot> MemoryReader {get;set;} = WindowsHostMemory.Read;
    internal MemoryAdmission? LastMemoryAdmission {get;private set;}
    private readonly List<WeakReference<IAcquisitionCapture>> retiredMemoryOwners=[];
    internal long RetainedDisplayBytes {get;set;}
    private static long ReadHostReserve()
    {
        string? configured=Environment.GetEnvironmentVariable("HMM_MINIMUM_HOST_RESERVE_GIB");
        if(string.IsNullOrWhiteSpace(configured)) return 16L<<30;
        if(!int.TryParse(configured,out int gib) || gib<0 || gib>1024)
            throw new ArgumentException("HMM_MINIMUM_HOST_RESERVE_GIB must be an integer from 0 to 1024.");
        return checked((long)gib<<30);
    }
    private long KnownRetainedBytes(bool replacing,bool averagingEnabled)
    {
        var buffers=new HashSet<Array>(ReferenceEqualityComparer.Instance);
        if(replacing) capture?.CollectBuffers(buffers);
        else if(!averagingEnabled) capture?.Averaging.CollectBuffers(buffers);
        foreach(var weak in retiredMemoryOwners.ToArray())
            if(weak.TryGetTarget(out var owner)) owner.CollectBuffers(buffers);else retiredMemoryOwners.Remove(weak);
        foreach(var tracker in new[]{progress,onceProgress,directProgress,reinitProgress,setParamProgress}.OfType<AcquisitionProgress>())
        {
            // Current first frames are already reserved by the model, except during receiver replacement.
            if(!replacing && capture is not null && tracker==progress) continue;
            foreach(var frame in tracker.Frames()) if(frame is not null) buffers.Add(frame.Data);
        }
        if(retainedOnce is not null) foreach(var frame in retainedOnce) if(frame is not null) buffers.Add(frame.Data);
        return HostMemoryAdmissionPolicy.BufferBytes(buffers);
    }
    internal MemoryAdmission InspectMemory(ScanRequest request,bool? average=null,bool replacing=false)
    {
        lock(gate)
        {
            bool enabled=average??(!onceCapture&&frameAverage.Enabled);
            var size=PreviewSampling.Create(request.XPixel,checked(request.YPixel+request.OversampleFrame),2048);
            // Gray source + bitmap x4 and Composite buffer + bitmap. Excess old-size buffers remain resident.
            long displayExcess=Math.Max(0,RetainedDisplayBytes-checked(16L*size.PixelCount));
            return HostMemoryAdmissionPolicy.Evaluate(request,enabled,MemoryReader(),checked(KnownRetainedBytes(replacing,enabled)+displayExcess),MemoryPolicy);
        }
    }
    private void AdmitReceiver(ExternalParameters settings)
    {
        var w=settings.Wave;
        var request=settings.Requested??new ScanRequest(w.line_mode,w.pixel_per_line,settings.OutputHeight,w.oversample_head,
            settings.OutputDummy,w.number_of_lines==16?16:32,1);
        var result=InspectMemory(request,replacing:true);
        LastMemoryAdmission=result;
        log.Event(generation,"HOST MEMORY ADMISSION: "+result.Describe());
        result.RequirePass();
    }
}
