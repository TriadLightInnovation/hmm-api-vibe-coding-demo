using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HmmExample.Acquisition;
using HmmExample.Interop;

namespace HmmExample;

internal static class LifecycleOfflineTests
{
    private sealed class FakeApi : IHmmApi
    {
        internal readonly ConcurrentQueue<string> Calls=new();
        internal readonly ConcurrentQueue<string> States=new();
        internal HmmNative.ImageCallback? Image;
        internal HmmNative.ErrorCallback? Error;
        internal Func<ExternalState>? State;
        internal Action? DuringStart;
        internal Action? DuringStop;
        internal WaveParams Wave;
        internal bool InitializeResult=true,StartResult=true,ThrowStop,StopInsideCallback;
        internal long InitializeEntered,StopReturned;
        [ThreadStatic] private static bool inCallback;
        private void Call(string name) {Calls.Enqueue(name); States.Enqueue(name+":"+State?.Invoke());}
        public void EnsureAvailable()=>Call("EnsureAvailable(fake)");
        public bool Initialize() {InitializeEntered=Stopwatch.GetTimestamp(); Call("Initialize"); return InitializeResult;}
        public void SetParameters(ref WaveParams wave,int iterations) {Wave=wave; Call("SetParameters");}
        public bool SetAo2Value(float amplitude,float offset) {Call("SetAO2");return true;}
        public void GetFrequency(out FrequencyParams frequency) {Call("GetFrequency");frequency=new(){line_frequency_hz=400,pos_time=1,neg_time=1};}
        public void SetAux(bool value)=>Call("SetAux");
        public bool StartScan() {Call("Start"); DuringStart?.Invoke(); return StartResult;}
        public void StopScan() {StopInsideCallback|=inCallback; Call("Stop"); DuringStop?.Invoke(); if(ThrowStop) throw new InvalidOperationException("fake Stop failure"); StopReturned=Stopwatch.GetTimestamp();}
        public void SetImageCallback(HmmNative.ImageCallback callback) {Image=callback; Call("RegisterImage");}
        public void SetErrorCallback(HmmNative.ErrorCallback callback) {Error=callback; Call("RegisterError");}
        internal void EmitError(string message)
        {
            byte[] bytes=System.Text.Encoding.UTF8.GetBytes(message);
            IntPtr data=Marshal.AllocHGlobal(bytes.Length),meta=Marshal.AllocHGlobal(Marshal.SizeOf<ErrorMessage>());
            try {Marshal.Copy(bytes,0,data,bytes.Length);Marshal.StructureToPtr(new ErrorMessage {error_message=data,message_size=bytes.Length},meta,false);Error!(meta);}
            finally {Marshal.FreeHGlobal(meta);Marshal.FreeHGlobal(data);}
        }
        internal void Emit(int c,int line,int n0,int n1,Action<IntPtr>? receiver=null)
        {
            int count=checked((n0+n1)*Wave.pixel_per_line);
            var values=Enumerable.Range(line*Wave.pixel_per_line,count).Select(x=>(float)(c*10000+x)).ToArray();
            IntPtr data=Marshal.AllocHGlobal(count*sizeof(float)),meta=Marshal.AllocHGlobal(Marshal.SizeOf<CResampledImage>());
            try
            {
                Marshal.Copy(values,0,data,count);
                Marshal.StructureToPtr(new CResampledImage {channel=c,width=Wave.pixel_per_line,height=Wave.line_per_frame+Wave.oversample_frame,
                    index0_line_number=n0,index1_line_number=n1,image_data=data,image_size=count},meta,false);
                inCallback=true;
                if(receiver is not null) receiver(meta); else Image!(meta);
            }
            finally {inCallback=false;Marshal.FreeHGlobal(meta);Marshal.FreeHGlobal(data);}
        }
        internal void Frame(int c)
        {
            int rows=Wave.line_per_frame+Wave.oversample_frame;
            if(!Wave.line_mode) {Emit(c,0,rows,0);return;}
            for(int line=0;line<rows;line+=Wave.number_of_lines)
            {
                int n0=Math.Min(rows-line,Wave.number_of_lines),n1=Wave.number_of_lines-n0;
                Emit(c,line,n0,n1);
            }
        }
    }
    private sealed class Deadline
    {
        internal TaskCompletionSource Expired=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal double RequestedMs;
        internal Task Delay(TimeSpan duration,CancellationToken token) {RequestedMs=duration.TotalMilliseconds;return Expired.Task.WaitAsync(token);}
        internal void Reset() {Expired=new(TaskCreationOptions.RunContinuationsAsynchronously);RequestedMs=0;}
    }
    private static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    private static void Finish(Task task)=>task.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
    private static void Until(Func<bool> condition)
    {
        Finish(Task.Run(async()=> {long start=Stopwatch.GetTimestamp();while(!condition()) {if(Stopwatch.GetElapsedTime(start)>TimeSpan.FromSeconds(5)) throw new TimeoutException("Offline condition not reached");await Task.Delay(1);}}));
    }
    internal static object Run(string pngPath)
    {
        NativeLibrary.SetDllImportResolver(typeof(HmmNative).Assembly,(_,_,_)=>throw new InvalidOperationException("Native SDK forbidden in lifecycle fixture"));
        var checks=new List<string>();
        void Check(bool pass,string name) {if(!pass) throw new InvalidOperationException(name);checks.Add(name);}
        Check(!Diagnostics.SdkLoaded(),"No native SDK loaded before fixture");
        var baseline=ExternalParameters.LoadBaseline();
        var selected=ScanRequest.Default with {XPixel=4,YPixel=30,OversamplePixel=3,OversampleFrame=4,NumberOfLines=16,SpeedPosition=3};
        var log=new RollingDiagnosticLog();var fake=new FakeApi();var deadline=new Deadline();int loads=0;
        var session=new SdkSession(log,fake,r=> {Interlocked.Increment(ref loads);return baseline.Apply(r);},deadline.Delay);
        fake.State=()=>session.Snapshot().State;
        Check(session.SetRequestedParameters(selected),"Initial UI request accepted");
        var once=session.ScanOnceAsync();
        Until(()=>session.Snapshot().ScanOnceWaiting);
        Check(session.Snapshot() is {State:ExternalState.Scanning,Busy:true,ScanOnceActive:true},"Scan Once enters Scanning with whole workflow edit lock");
        Check(!session.SetRequestedParameters(selected with {XPixel=8}),"Scan Once rejects mid-run parameter edits");
        Check(fake.Wave.pixel_per_line==4 && fake.Wave.line_per_frame==30 && fake.Wave.oversample_head==3 && fake.Wave.oversample_tail==3 && fake.Wave.oversample_frame==4
            && fake.Wave.number_of_lines==16 && fake.Wave.line_frequency_hz==100 && fake.Wave.line_mode && !fake.Wave.external_line_trigger,"Scan Once snapshots all current UI parameters into runtime WaveParams");
        fake.Frame(3); fake.Frame(3); fake.Frame(0); fake.Frame(2);
        Check(!once.IsCompleted && !fake.Calls.Contains("Stop"),"Three channels and a duplicate cannot release four-channel barrier");
        Check(session.Snapshot().Acquisition!.Channels[3].CompletedFrames==1,"Scan Once accepts only first frame per channel even when next-frame blocks arrive");
        fake.Frame(1); Finish(once);
        var completed=session.Snapshot();
        Check(completed is {State:ExternalState.RequiresInitialize,NeedsStop:false,Busy:false} && completed.ScanOnce?.Status=="Complete","All enabled first frames trigger Stop and return Requires Initialize");
        Check(!fake.StopInsideCallback,"Scan Once never invokes Stop on callback stack");
        Check(fake.Calls.ToArray().SequenceEqual(new[]{"EnsureAvailable(fake)","Initialize","RegisterError","RegisterImage","RegisterError","RegisterImage","SetAux","SetParameters","Start","Stop"}),"Scan Once exact API sequence; no AO2 / frequency / fallback Initialize");
        Check(fake.States.Contains("Initialize:Initializing") && fake.States.Contains("SetAux:Preparing") && fake.States.Contains("Start:Starting") && fake.States.Contains("Stop:Stopping"),"Scan Once state sequence matches each lifecycle stage");
        var timings=completed.ScanOnce!.Timings;
        foreach(string key in new[]{"Initialize API Time","Prepare Total Time","Set AUX API Time","Set Parameters API Time","Start Scan API Time","Stop Scan API Time","Total Scan Once Time"})
            Check(timings.TryGetValue(key,out var value)&&value>=0,"Monotonic timing recorded: "+key);
        var timingProgress=completed.ScanOnce.Progress!;
        Check(timingProgress.FirstCallbackMs is >=0 && timingProgress.FirstCompleteMs.All(x=>x is >=0) && timingProgress.AllEnabledCompleteMs==timingProgress.FirstCompleteMs.Max(),"First callback / per-channel / all-enabled timing are separate");
        double nativeSpan=Stopwatch.GetElapsedTime(fake.InitializeEntered,fake.StopReturned).TotalMilliseconds;
        Check(timings["Total Scan Once Time"]>=nativeSpan && timings["Total Scan Once Time"]-nativeSpan<30,"Total Scan Once spans Initialize call through Stop return");
        var retained=session.TakeRetainedOnce()!;
        Check(retained.All(f=>f is {Width:4,Height:34,FrameSequence:1}),"Retained frames include all 30 + 4 raw rows and exactly first sequence");
        Check(retained.Select((f,c)=>f!.Data.SequenceEqual(Enumerable.Range(0,136).Select(x=>(float)(c*10000+x)))).All(x=>x),"Cross-boundary callback retains correct immutable first frame pixels");
        Check(completed.CanDirectRescan && completed.CanReinitNoPrepare,"Successful prepared cycle enables Direct Start and enables Reinit-No-Prepare");
        // Direct Start call-order and PASS tests live in AveragingOfflineTests using an unknown profile.
        // This fixture no longer invokes DirectRescanAsync; new experiment coverage lives separately.
        Check(SdkSession.RescanTimeoutMs(2585)==5170 && SdkSession.RescanTimeoutMs(100)==2000,"Dynamic diagnostic timeout = max(2000, 2 x frame time)");
        // Barrier membership comes from actual enabled channels, not fixed CH1 or all four.
        var subsetWave=baseline.Wave;subsetWave.channel0_enable=false;subsetWave.channel1_enable=true;subsetWave.channel2_enable=false;subsetWave.channel3_enable=true;
        var subsetParameters=baseline with {Wave=subsetWave};
        var subsetFake=new FakeApi();var subsetSession=new SdkSession(log,subsetFake,r=>subsetParameters.Apply(r));
        subsetSession.SetRequestedParameters(selected);var subsetOnce=subsetSession.ScanOnceAsync();Until(()=>subsetSession.Snapshot().ScanOnceWaiting);
        subsetFake.Frame(1);Check(!subsetOnce.IsCompleted,"One of two enabled channels does not complete Scan Once");
        subsetFake.Frame(3);Finish(subsetOnce);
        Check(subsetSession.Snapshot().ScanOnce!.Progress!.EnabledChannels.SequenceEqual(new[]{1,3}),"Only enabled CH2 / CH4 are required; disabled channels do not block");
        // Data before a new start epoch is not acquisition data for this attempt.
        var epochProgress=new AcquisitionProgress(99,baseline.Wave);long now=Stopwatch.GetTimestamp();
        epochProgress.Callback(0,99,now);epochProgress.Arm(now+1);
        epochProgress.Complete(new(0,1,1,new[]{1f},1,now,99),now);
        Check(!epochProgress.Snapshot().HasNewFrame && !epochProgress.Snapshot().HasNewCallback,"Pre-Start callback and frame cannot satisfy new attempt");
        epochProgress.Callback(0,98,now+2);epochProgress.Complete(new(0,1,1,new[]{1f},1,now+2,98),now+2);
        Check(!epochProgress.AnyComplete.Task.IsCompleted,"Wrong-generation completion cannot release signal");
        // Stop remains available while a one-frame acquisition waits for missing data.
        var cancelled=subsetSession.ScanOnceAsync();Until(()=>subsetSession.Snapshot().ScanOnceWaiting);
        Finish(subsetSession.StopAsync());Finish(cancelled);
        Check(subsetSession.Snapshot() is {State:ExternalState.RequiresInitialize,NeedsStop:false} && subsetSession.Snapshot().ScanOnce!.Status=="Cancelled by Stop","Manual Stop cancels Scan Once wait and performs exactly one cleanup Stop");
        // Callbacks can arrive synchronously inside Start without inline Stop / SDK worker join.
        subsetFake.DuringStart=()=> {subsetFake.Frame(1);subsetFake.Frame(3);};
        Finish(subsetSession.ScanOnceAsync());
        Check(subsetSession.Snapshot().ScanOnce!.Status=="Complete" && !subsetFake.StopInsideCallback,"Synchronous Start callbacks complete safely; continuation never calls Stop inline");
        subsetFake.ThrowStop=true;Finish(subsetSession.ScanOnceAsync());
        Check(subsetSession.Snapshot() is {State:ExternalState.StopFailed,NeedsStop:true,CanDirectRescan:false},"Stop failure retains cleanup requirement and blocks Direct Rescan");
        subsetFake.ThrowStop=false;Finish(subsetSession.StopAsync());
        subsetFake.DuringStart=null; subsetFake.StartResult=false;
        int stopsBefore=subsetFake.Calls.Count(x=>x=="Stop");
        Finish(subsetSession.ScanOnceAsync());
        Check(subsetSession.Snapshot().ScanOnce!.Status=="FAIL: startScan returned false" && subsetSession.Snapshot().State==ExternalState.RequiresInitialize
            && subsetFake.Calls.Count(x=>x=="Stop")==stopsBefore+1,"Scan Once failed Start performs one cleanup Stop without retry");
        subsetFake.StartResult=true;subsetFake.DuringStart=()=> {subsetFake.Frame(1);subsetFake.Frame(3);};
        subsetFake.DuringStop=()=>subsetFake.EmitError("fake stop callback error");
        Finish(subsetSession.ScanOnceAsync());
        Check(subsetSession.Snapshot() is {LastError:"fake stop callback error",CanDirectRescan:false} && subsetSession.Snapshot().ScanOnce!.Status=="FAIL: fake stop callback error",
            "Error callback remains active during Stop after image capture retirement; failure cannot enable Direct Rescan");
        var disabledWave=baseline.Wave;
        disabledWave.channel0_enable=disabledWave.channel1_enable=disabledWave.channel2_enable=disabledWave.channel3_enable=false;
        var disabledFake=new FakeApi();var disabledSession=new SdkSession(log,disabledFake,r=>(baseline with {Wave=disabledWave}).Apply(r));
        Finish(disabledSession.ScanOnceAsync());
        Check(disabledFake.Calls.IsEmpty && disabledSession.Snapshot().ScanOnce!.Status!.Contains("at least one enabled"),"No enabled channel is rejected before any SDK call");
        // WPF fixture verifies controls and retained full-size image even if no preview tick ran during scan.
        var app=new Application();var uiFake=new FakeApi();var uiSession=new SdkSession(log,uiFake,r=>baseline.Apply(r));
        var window=new MainWindow(uiSession,log);
        void Refresh()=>typeof(MainWindow).GetMethod("RefreshSession",Private)!.Invoke(window,null);
        void Preview()=>typeof(MainWindow).GetMethod("RefreshPreview",Private)!.Invoke(window,null);
        window.XPixel.Text="4";window.YPixel.Text="400";window.OversampleFrame.Text="10";window.LinesPerCallback.SelectedIndex=1;
        var uiOnce=uiSession.ScanOnceAsync();Until(()=>uiSession.Snapshot().ScanOnceWaiting);Refresh();
        Check(!window.GeometryControls.IsEnabled && !window.SpeedSlider.IsEnabled && !window.LinesPerCallback.IsEnabled && !window.PreviewRate.IsEnabled && window.StopButton.IsEnabled,"Scan Once locks controls; Stop can cancel the wait");
        for(int c=0;c<4;c++) uiFake.Frame(c);Finish(uiOnce);Refresh();Preview();
        Check(window.Viewports.All(v=>v.Image.Source is WriteableBitmap {PixelWidth:4,PixelHeight:410}),"WPF displays retained complete 400 + 10 raw rows after Scan Once Stop");
        var sources=window.Viewports.Select(v=>v.Image.Source).ToArray();Preview();Refresh();
        Check(window.Viewports.Select((v,i)=>ReferenceEquals(v.Image.Source,sources[i])).All(x=>x),"Stopped preview ticks preserve completed frame on all four channels");
        Check(uiSession.Snapshot().Acquisition!.Channels.All(c=>c.DisplayedUpdates==1 && c.FirstDisplayUpdateMs is >=0),"Retained final image updates archived display counters and first-display timing");
        Check(window.TimingDetails.Text.Contains("SCAN ONCE") && window.TimingDetails.Text.Contains("All Enabled Channels First Complete"),"Existing API Timing tab contains Scan Once and per-channel timing");
        Check(window.InitializeButton.IsEnabled && window.ScanOnceButton.IsEnabled && window.RescanButton.IsEnabled && window.FindName("ReinitButton") is null && !window.StartButton.IsEnabled,"Stopped UI requires new Initialize; Direct Start lifecycle eligible and old experimental UI removed");
        window.SdkStatus.Text="OFFLINE LIFECYCLE FIXTURE — managed fake API; no hardware";
        var root=(FrameworkElement)window.Content;root.Measure(new Size(1520,1060));root.Arrange(new Rect(0,0,1520,1060));root.UpdateLayout();
        var image=new RenderTargetBitmap(1520,1060,96,96,PixelFormats.Pbgra32);image.Render(root);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(pngPath)) png.Save(file);
        window.TriggerMode.SelectedIndex=0;
        Check(window.ScanOnceButton.Visibility==Visibility.Collapsed && window.ExternalValidation.Text=="External: Implemented / Awaiting Resonant Hardware Validation","External status is awaiting resonant hardware validation; Scan Once hidden");
        int externalBegin=uiFake.Calls.Count;
        Finish(uiSession.InitializeAsync());Finish(uiSession.PrepareAsync());Finish(uiSession.StartAsync());
        Check(uiFake.Calls.ToArray().Skip(externalBegin).SequenceEqual(new[]{"EnsureAvailable(fake)","Initialize","RegisterError","RegisterImage","RegisterError","RegisterImage","SetAO2","GetFrequency","SetAux","SetParameters","Start"})
            && uiSession.Snapshot().Timings["Resonant Wait Actual"]>=990 && !uiFake.Wave.line_mode && uiFake.Wave.external_line_trigger,"External lifecycle uses AO2 / 1000 ms wait / measured frequency / AUX / parameters order (fake API only)");
        uiFake.Frame(0);Preview();
        Check(window.Viewports[0].Image.Source is WriteableBitmap {PixelWidth:4,PixelHeight:410},"External complete frame also displays full raw height without guessed crop");
        Finish(uiSession.StopAsync());
        Check(!Diagnostics.SdkLoaded(),"No HMM native modules loaded after lifecycle and WPF tests");
        Finish(log.FinishAsync());typeof(MainWindow).GetField("allowClose",Private)!.SetValue(window,true);window.Close();app.Shutdown();
        return new {Passed=true,Mode="Managed fake API only; no native SDK or hardware",Checks=checks,ScanOnce=completed.ScanOnce,ApiSequence=fake.States.ToArray(),Screenshot=pngPath};
    }
}
