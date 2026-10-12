using System.Runtime.InteropServices;
using System.Text.Json;
using HmmExample.Acquisition;
using HmmExample.Interop;

namespace HmmExample;

internal static class MemoryOfflineTests
{
    internal static object Run(string directory)
    {
        Directory.CreateDirectory(directory);
        NativeLibrary.SetDllImportResolver(typeof(HmmNative).Assembly,(_,_,_)=>throw new InvalidOperationException("Native SDK prohibited in memory tests"));
        var checks=new List<string>();
        void Check(bool value,string name) {if(!value)throw new InvalidOperationException(name);checks.Add(name);}
        void Reject(Action action,string name) {bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}catch(OverflowException){rejected=true;}catch(InvalidOperationException){rejected=true;}Check(rejected,name);}
        var enough=new SystemMemorySnapshot(50L<<30,52L<<30);
        var external=ScanRequest.Default with {InternalMode=false,XPixel=4096,YPixel=4096,OversampleFrame=10};
        var internalScan=external with {InternalMode=true};
        var lineAverage=internalScan with {LineAverage=true,AverageLines=3};
        var estimates=new Dictionary<string,MemoryAdmission>();
        foreach(var (name,r) in new[]{("External",external),("Internal",internalScan),("Internal LA3",lineAverage)})
        {
            var admission=HostMemoryAdmissionPolicy.Evaluate(r,false,enough);estimates[name]=admission;
            Check(admission.Passed,name+" 4096 Host Admission PASS");
            Check(admission.NativeGeometry.Contains("Hardware Validation Required"),name+" native qualification warning retained");
        }
        Check(estimates["External"].Estimate.ProcessedFrameSamples==16_818_176,"4096 plus Dummy frame exceeds old 16M and is accepted");
        Check(estimates["Internal LA3"].Estimate.RawCallbackSamples==393_216,"LA3 raw callback = 4096 x 96");
        Check(estimates["Internal LA3"].Estimate.ProcessedBlockSamples==131_072,"LA3 processed block = 4096 x 32");
        Check(estimates["Internal LA3"].Estimate.ProcessedFrameSamples==estimates["Internal"].Estimate.ProcessedFrameSamples,"LA3 does not allocate full effective raw frame");
        Check(estimates["Internal LA3"].Estimate.PayloadBytes-estimates["Internal"].Estimate.PayloadBytes==10*1024*1024,"LA3 adds eight raw block deltas and four processed temporaries only");
        var baseline=ExternalParameters.LoadBaseline();var applied=baseline.Apply(lineAverage);
        Check(applied.Wave.line_per_frame==12288&&applied.Wave.oversample_frame==30&&applied.Wave.number_of_lines==96,"Effective SDK fields unchanged");
        var avg=HostMemoryAdmissionPolicy.Estimate(lineAverage,true);
        Check(avg.Components["Frame Average double sums (four channels)"]==538_181_632,"Four double sums included");
        Check(avg.Components["Frame Average display (four channels)"]==269_090_816,"Four averaged display arrays included");
        Check(avg.EstimatedBytes>estimates["Internal LA3"].Estimate.EstimatedBytes,"Frame Average increases total admission");
        Check(HostMemoryAdmissionPolicy.Evaluate(lineAverage,true,enough).Passed,"4096 LA3 + Frame Average within managed budget");
        var withoutComposite=HostMemoryAdmissionPolicy.Estimate(external,composite:false);
        Check(estimates["External"].Estimate.PayloadBytes-withoutComposite.PayloadBytes==8L*2043*2048,"Composite BGRA + bitmap counted; shared Gray8 not duplicated");
        Check(PreviewSampling.Create(4096,4106,2048) is {Width:2043,Height:2048},"4096 source -> 2048 aspect-preserving preview");
        Check(!HostMemoryAdmissionPolicy.Evaluate(external,false,new(16L<<30,52L<<30)).Passed,"Insufficient physical memory blocks");
        Check(!HostMemoryAdmissionPolicy.Evaluate(external,false,new(50L<<30,16L<<30)).Passed,"Insufficient commit blocks independently");
        Check(!HostMemoryAdmissionPolicy.Evaluate(external,false,enough,options:new(1L<<30)).Passed,"Managed budget blocks independently");
        Check(!HostMemoryAdmissionPolicy.Evaluate(external,false,enough,retained:3L<<30).Passed,"Retained state charged to total budget");
        Reject(()=>(lineAverage with {YPixel=int.MaxValue}).Validate(),"Effective SDK Int32 overflow rejected");
        Reject(()=>HostMemoryAdmissionPolicy.ArrayLength(long.MaxValue,8),"Byte overflow rejected");
        Reject(()=>HostMemoryAdmissionPolicy.ArrayLength(0,4),"Zero allocation rejected");
        Reject(()=>GeometryMath.Pixels(int.MaxValue,int.MaxValue),"Huge image rejected before allocation");
        Reject(()=>HostMemoryAdmissionPolicy.ArrayLength((1L<<28)+1,4),"Single allocation >1 GiB rejected");
        var shared=new float[4];Check(HostMemoryAdmissionPolicy.BufferBytes(new Array[]{shared,shared})==16,"Shared array references counted once");
        int pixels=GeometryMath.Pixels(4096,4106);
        Check(HostMemoryAdmissionPolicy.AverageQueueBytes(pixels,pixels,false)==4L*pixels*4,"External average queue bounded to four full frames, not 128 MiB");
        var m=new CResampledImage {channel=0,width=4096,height=12318,image_size=4096*96,index0_line_number=96,image_data=new IntPtr(1)};
        Check(HostMemoryAdmissionPolicy.CallbackFits(m,applied),"Internal raw block metadata accepted");
        m.index0_line_number=6;m.index1_line_number=90;
        Check(HostMemoryAdmissionPolicy.CallbackFits(m,applied),"Internal cross-frame metadata preserves whole average groups");
        m.index0_line_number=7;m.index1_line_number=89;
        Check(!HostMemoryAdmissionPolicy.CallbackFits(m,applied),"Misaligned average groups rejected before copy");
        m.image_size=4096*12318;Check(!HostMemoryAdmissionPolicy.CallbackFits(m,applied),"Unexpected Internal raw full-frame callback rejected before copy");
        var ep=baseline.Apply(external);
        m.height=4106;m.image_size=pixels;m.index0_line_number=4106;m.index1_line_number=0;
        Check(HostMemoryAdmissionPolicy.CallbackFits(m,ep),"External full frame >16M accepted");
        m.width=4095;Check(!HostMemoryAdmissionPolicy.CallbackFits(m,ep),"External width mismatch rejected");
        using var log=new TestLog(directory);
        // Deliberately invalid native data pointer must never be dereferenced after metadata rejection.
        var receiver=new ExternalCapture(1,ep,log.Value);receiver.ArmStart();
        IntPtr metadata=Marshal.AllocHGlobal(Marshal.SizeOf<CResampledImage>());
        try {Marshal.StructureToPtr(m,metadata,false);receiver.Receive(metadata,1,1);Check(receiver.Snapshot().Channels[0].RejectedCallbacks==1,"External receiver rejects mismatch before Marshal.Copy");}
        finally {Marshal.FreeHGlobal(metadata);receiver.FreezeAndSnapshot();}
        // Exercise the real managed Internal adapter at large geometry, using synthetic native memory only.
        var live=new InternalCapture(2,applied,log.Value);live.ArmStart();
        var owned=new HashSet<Array>(ReferenceEqualityComparer.Instance);live.CollectBuffers(owned);
        Check(HostMemoryAdmissionPolicy.BufferBytes(owned)==12L*pixels*4,"Internal allocates requested current/live/spare only, never effective raw frame");
        var raw=HostMemoryAdmissionPolicy.Floats(4096L*96);
        for(int row=0;row<96;row++)Array.Fill(raw,(float)(row%3+1),row*4096,4096);
        IntPtr data=Marshal.AllocHGlobal(checked(raw.Length*4));metadata=Marshal.AllocHGlobal(Marshal.SizeOf<CResampledImage>());
        try
        {
            Marshal.Copy(raw,0,data,raw.Length);
            m=new CResampledImage {channel=0,width=4096,height=12318,image_size=raw.Length,index0_line_number=96,image_data=data};
            Marshal.StructureToPtr(m,metadata,false);live.Receive(metadata,2,2);
            Check(live.Snapshot().Channels[0] is {LiveAccumulatedLines:32,CompletedFrames:0},"4096 LA3 adapter: 96 raw lines -> 32 processed lines");
            Check(live.TryTakeLive(0,out var view,2048)&&view is {Width:2043,Height:2048}&&view.Data[0]==2,"Actual 4096 x 4106 source yields correct bounded progressive preview");
            m.image_data=new IntPtr(1);m.image_size=4096*12318;
            Marshal.StructureToPtr(m,metadata,false);live.Receive(metadata,2,2);
            Check(live.Snapshot().Channels[0].RejectedCallbacks==1,"Internal unexpected full raw frame rejected before pointer dereference");
        }
        finally {Marshal.FreeHGlobal(data);Marshal.FreeHGlobal(metadata);live.FreezeAndSnapshot();}
        var fake=new ReinitNoPrepareOfflineTests.FakeApi();
        var session=new SdkSession(log.Value,fake,baseline.Apply) {MemoryReader=()=>new(1L<<30,1L<<30)};
        session.InitializeAsync().GetAwaiter().GetResult();
        Check(fake.Calls.Count==0&&session.Snapshot().LastError!.Contains("Insufficient Available Host Memory"),"Admission blocks before native Initialize and buffer creation");
        var prepareFake=new ReinitNoPrepareOfflineTests.FakeApi();
        var prepareSession=new SdkSession(log.Value,prepareFake,baseline.Apply) {MemoryReader=()=>enough};
        prepareSession.InitializeAsync().GetAwaiter().GetResult();int before=prepareFake.Calls.Count;
        prepareSession.MemoryReader=()=>new(1L<<30,1L<<30);
        prepareSession.PrepareAsync().GetAwaiter().GetResult();
        Check(prepareFake.Calls.Count==before&&prepareSession.Snapshot().State==ExternalState.PrepareFailed,"Prepare rechecks current memory before callbacks or hardware APIs");
        prepareSession.StopAsync().GetAwaiter().GetResult();
        Check(!Diagnostics.SdkLoaded(),"No native SDK loaded or hardware operated");
        return new {Passed=true,Checks=checks,Estimates=estimates,FrameAverageEstimate=avg,WindowsMemory=WindowsHostMemory.Read(),Hardware="NOT TESTED; 4096 Host Admission only"};
    }
    private sealed class TestLog(string directory):IDisposable
    {
        internal RollingDiagnosticLog Value {get;}=new(Path.Combine(directory,"logs"));
        public void Dispose()=>Value.FinishAsync().GetAwaiter().GetResult();
    }
}
