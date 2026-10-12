using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HmmExample.Acquisition;
using HmmExample.Interop;

namespace HmmExample;

internal static class ReinitNoPrepareOfflineTests
{
    private static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    private static void Finish(Task task)=>task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    private static void Until(Func<bool> condition)
    {
        var clock=Stopwatch.StartNew();
        while(!condition()) {if(clock.ElapsedMilliseconds>5000) throw new TimeoutException("Offline condition not reached");Thread.Sleep(1);}
    }
    private static string Read(string path)
    {using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(file);return reader.ReadToEnd();}
    internal sealed class FakeApi : IHmmApi
    {
        internal readonly List<string> Calls=[];
        internal HmmNative.ImageCallback? Image;
        internal HmmNative.ErrorCallback? Error;
        internal Action? DuringStart,DuringRegistration,DuringSetParameters;
        internal readonly List<WaveParams> SentWaves=[];
        internal readonly List<int> SentIterations=[];
        internal bool MutateParameterReference,ThrowSetParameters;
        internal bool InitializeResult=true,StartResult=true,ThrowRegistration,ThrowStop;
        internal WaveParams Wave;
        public void EnsureAvailable()=>Calls.Add("EnsureAvailable(fake)");
        public bool Initialize() {Calls.Add("initialize");return InitializeResult;}
        public void SetAux(bool value)=>Calls.Add("setAux");
        public void SetParameters(ref WaveParams wave,int iterations)
        {
            Calls.Add("setParameters");SentWaves.Add(wave);SentIterations.Add(iterations);DuringSetParameters?.Invoke();
            if(ThrowSetParameters) throw new InvalidOperationException("Fake SetParameters exception");
            Wave=wave;
            if(MutateParameterReference) {wave.pixel_per_line=999;wave.line_frequency_hz=12345;wave.signal_invert=!wave.signal_invert;}
        }
        public bool SetAo2Value(float amplitude,float offset) {Calls.Add("setAo2Value");return true;}
        public void GetFrequency(out FrequencyParams frequency) {Calls.Add("getFrequency");frequency=new(){line_frequency_hz=10,pos_time=1,neg_time=1};}
        public void SetErrorCallback(HmmNative.ErrorCallback callback) {Calls.Add("setErrorMessageCallback");if(ThrowRegistration) throw new InvalidOperationException("Fake registration exception");Error=callback;DuringRegistration?.Invoke();}
        public void SetImageCallback(HmmNative.ImageCallback callback) {Calls.Add("setResampledImageCallback");Image=callback;}
        public bool StartScan() {Calls.Add("startScan");DuringStart?.Invoke();return StartResult;}
        public void StopScan() {Calls.Add("stopScan");if(ThrowStop) throw new InvalidOperationException("Fake stop failure");}
        internal void Emit(int channel,int rows,HmmNative.ImageCallback? receiver=null)
        {
            int count=rows*Wave.pixel_per_line;
            var data=Enumerable.Range(0,count).Select(x=>(float)(x+channel)).ToArray();
            IntPtr values=Marshal.AllocHGlobal(count*sizeof(float)),meta=Marshal.AllocHGlobal(Marshal.SizeOf<CResampledImage>());
            try
            {
                Marshal.Copy(data,0,values,count);
                Marshal.StructureToPtr(new CResampledImage {channel=channel,width=Wave.pixel_per_line,height=Wave.line_per_frame+Wave.oversample_frame,
                    image_data=values,image_size=count,index0_line_number=rows},meta,false);
                (receiver??Image)!(meta);
            }
            finally {Marshal.FreeHGlobal(meta);Marshal.FreeHGlobal(values);}
        }
        internal void Frame(int channel,HmmNative.ImageCallback? receiver=null)
        {
            int rows=Wave.line_per_frame+Wave.oversample_frame;
            if(Wave.line_mode) for(int i=0;i<rows;i+=Wave.number_of_lines) Emit(channel,Wave.number_of_lines,receiver);
            else Emit(channel,rows,receiver);
        }
    }
    internal sealed class Fixture : IDisposable
    {
        internal readonly RollingDiagnosticLog Log;
        internal readonly FakeApi Api=new();
        internal readonly SdkSession Session;
        internal readonly ScanRequest Request;
        internal TaskCompletionSource Expired=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Loads;
        internal double TimeoutRequested;
        internal Fixture(string directory,bool internalMode=true,bool subset=false)
        {
            Log=new(directory);
            var baseline=ExternalParameters.LoadBaseline();
            Request=ScanRequest.Default with {InternalMode=internalMode,XPixel=4,YPixel=32,OversamplePixel=0,OversampleFrame=0,NumberOfLines=16};
            Session=new(Log,Api,r=>
            {
                Loads++;var parameters=baseline.Apply(r);var wave=parameters.Wave;
                wave.channel0_enable=true;wave.channel1_enable=!subset;wave.channel2_enable=true;wave.channel3_enable=!subset;
                return parameters with {Wave=wave};
            },(duration,token)=> {TimeoutRequested=duration.TotalMilliseconds;return Expired.Task.WaitAsync(token);});
            Session.SetRequestedParameters(Request);
        }
        internal void Ready()
        {
            Finish(Session.InitializeAsync());Finish(Session.PrepareAsync());Finish(Session.StartAsync());
            for(int c=0;c<4;c++) Api.Frame(c);
            Finish(Session.StopAsync());
        }
        public void Dispose()
        {
            if(Session.Snapshot().NeedsStop) {Api.ThrowStop=false;Finish(Session.StopAsync());}
            Finish(Log.FinishAsync());
        }
    }
    internal static int AbruptProbe(string directory)
    {
        NativeLibrary.SetDllImportResolver(typeof(HmmNative).Assembly,(_,_,_)=>throw new InvalidOperationException("Native SDK forbidden"));
        var f=new Fixture(directory);f.Ready();int count=f.Api.Calls.Count;
        f.Api.DuringStart=()=>
        {
            File.WriteAllText(Path.Combine(directory,"probe.json"),JsonSerializer.Serialize(new {f.Log.FilePath,Calls=f.Api.Calls.Skip(count).ToArray(),SdkLoaded=Diagnostics.SdkLoaded()}));
            Process.GetCurrentProcess().Kill(); // Only this diagnostic child, inside a fake StartScan.
        };
        Finish(f.Session.ReinitNoPrepareAsync());return 99;
    }
    private static void Refresh(MainWindow window)=>typeof(MainWindow).GetMethod("RefreshSession",Private)!.Invoke(window,null);
    private static void Preview(MainWindow window)=>typeof(MainWindow).GetMethod("RefreshPreview",Private)!.Invoke(window,null);
    private static void Align(MainWindow window,ScanRequest request)
    {
        typeof(MainWindow).GetMethod("RestoreRequestedControls",Private)!.Invoke(window,[request]);
        typeof(MainWindow).GetMethod("RequestedControlsChanged",Private)!.Invoke(window,null);Refresh(window);
    }
    private static void Render(MainWindow window,string path)
    {
        var root=(FrameworkElement)window.Content;root.Measure(new Size(1600,1100));root.Arrange(new Rect(0,0,1600,1100));root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(1600,1100,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
    private static void Close(MainWindow window,RollingDiagnosticLog log)
    {Finish(log.FinishAsync());typeof(MainWindow).GetField("allowClose",Private)!.SetValue(window,true);window.Close();}
    internal static object Run(string directory)
    {
        Directory.CreateDirectory(directory);
        NativeLibrary.SetDllImportResolver(typeof(HmmNative).Assembly,(_,_,_)=>throw new InvalidOperationException("Native SDK forbidden in Reinit fixture"));
        var checks=new List<string>();
        void Check(bool pass,string name) {if(!pass) throw new InvalidOperationException(name);checks.Add(name);}
        Check(!Diagnostics.SdkLoaded(),"Native SDK absent before fixture");
        string[] sequence=["initialize","setErrorMessageCallback","setResampledImageCallback","startScan"];
        using(var f=new Fixture(Path.Combine(directory,"all-channels")))
        {
            Check(!f.Session.Snapshot().CanReinitNoPrepare,"Experiment unavailable without a successful prepared Start/Stop");
            Finish(f.Session.ReinitNoPrepareAsync());Check(f.Api.Calls.Count==0,"Ineligible request invokes nothing");
            f.Ready();var previous=f.Session.Snapshot();var oldImage=f.Api.Image!;
            Check(previous.State==ExternalState.RequiresInitialize&&previous.CanReinitNoPrepare,"Successful prepared cycle enables new experiment");
            Check(previous.CanDirectRescan,"Baseline crash warning does not block Direct Start after successful cycle");
            f.Session.SetRequestedParameters(f.Request with {SpeedPosition=0});
            Check(!f.Session.Snapshot().CanReinitNoPrepare,"Changed UI request cannot silently use previous diagnostic geometry/frequency");
            f.Session.SetRequestedParameters(f.Request);
            int before=f.Api.Calls.Count,loads=f.Loads;long oldGeneration=previous.Acquisition!.Generation;
            bool level1=false;f.Api.DuringRegistration=()=>level1=f.Session.Snapshot().ReinitNoPrepare?.Level==1;
            Finish(f.Session.ReinitNoPrepareAsync());var fresh=f.Session.Snapshot();
            Check(f.Api.Calls.Skip(before).SequenceEqual(sequence),"Exact experiment sequence: Initialize, ErrorCallback, ImageCallback, Start only");
            Check(f.Loads==loads,"Experiment performs no JSON reload / parameter construction");
            Check(!ReferenceEquals(oldImage,f.Api.Image),"Both delegates re-registered with fresh route");
            Check(fresh.Generation>oldGeneration&&fresh.ReinitNoPrepare is {Level:2,FullPass:false,Status:"Waiting for data"},"Start true is only LEVEL 2; no prior counts reused");
            Check(level1,"Initialize true is LEVEL 1 before callback registration");
            Check(fresh.ReinitNoPrepare!.TimeoutMs==2000,"Fast prior session uses 2000 ms diagnostic floor");
            Check(fresh.Acquisition!.Channels.All(c=>c.Callbacks==0&&c.CompletedFrames==0),"Fresh acquisition starts with zero callback and frame counters");
            f.Api.Frame(0,oldImage);
            Check(f.Session.Snapshot().ReinitNoPrepare!.Progress is {HasNewCallback:false,HasNewFrame:false},"Old registered generation cannot contribute callback/frame or PASS");
            f.Api.Emit(0,16);
            Check(f.Session.Snapshot().ReinitNoPrepare is {Level:3,FullPass:false},"Partial new block reaches LEVEL 3 only");
            f.Api.Emit(0,16);
            Check(f.Session.Snapshot().ReinitNoPrepare is {Level:4,FullPass:false},"One completed channel reaches LEVEL 4, not FULL PASS");
            for(int c=1;c<4;c++) f.Api.Frame(c);
            Until(()=>f.Session.Snapshot().ReinitNoPrepare?.FullPass==true);
            Check(f.Session.Snapshot().ReinitNoPrepare is {Status:"PASS",Level:4,Progress.AllEnabledCompleteMs:not null},"FULL PASS requires every enabled channel to complete a new frame");
            Check(f.Api.Calls.Count==before+4&&f.Session.Snapshot().NeedsStop,"No automatic Stop or fallback after PASS");
            Check(!f.Session.Snapshot().ParametersSent,"Experiment does not mark parameters as sent");
            Finish(f.Session.StopAsync());
            Check(f.Session.Snapshot() is {State:ExternalState.RequiresInitialize,NeedsStop:false,CanReinitNoPrepare:false},"Manual Stop restores Requires Initialize; another experiment requires a fresh prepared cycle");
            Check(!File.Exists(f.Log.ReinitMarkerPath),"Clean manual Stop deletes new experiment marker");
            string text=Read(f.Log.FilePath);
            Check(new[]{"EXPERIMENT REINIT_NO_PREPARE CLICKED","Previous session diagnostic snapshot","SetParametersCalled=false","SetAuxCalled=false","PrepareCalled=false","BEFORE callback registration","AFTER callback registration"}.All(text.Contains),"Persistent log includes previous snapshot and explicit no-configuration flags");
            Check(text.Contains("line_frequency_hz")&&text.Contains("channel0_enable"),"Previous WaveParams fields and enabled mask serialized");
        }
        foreach(string outcome in new[]{"initialize-false","registration-throw","start-false","no-callback","partial","subset-incomplete"})
        {
            using var f=new Fixture(Path.Combine(directory,outcome));f.Ready();int before=f.Api.Calls.Count;
            f.Api.InitializeResult=outcome!="initialize-false";f.Api.StartResult=outcome!="start-false";f.Api.ThrowRegistration=outcome=="registration-throw";
            Finish(f.Session.ReinitNoPrepareAsync());
            if(outcome=="partial") f.Api.Emit(0,16);
            if(outcome=="subset-incomplete") f.Api.Frame(0);
            if(outcome is "no-callback" or "partial" or "subset-incomplete") {f.Expired.TrySetResult();Until(()=>f.Session.Snapshot().ReinitNoPrepare?.Status!="Waiting for data");}
            var result=f.Session.Snapshot().ReinitNoPrepare!;
            string expectedStatus=outcome is "partial" or "subset-incomplete"?"PARTIAL":"FAIL";
            Check(result.Status==expectedStatus&&!result.FullPass,"Result classification: "+outcome);
            if(outcome=="initialize-false") Check(result.Reason=="Initialize failed"&&f.Api.Calls.Skip(before).SequenceEqual(new[]{"initialize"}),"False Initialize does not register callbacks or Start");
            if(outcome=="start-false") Check(result.Reason=="Start returned false without SetParameters","False Start has exact reason, not a crash classification");
            if(outcome=="no-callback") Check(result.Reason=="Start accepted but no acquisition callback","No callback timeout reason");
            if(outcome=="partial") Check(result.Reason=="Callbacks resumed but frame assembly incomplete","Callback-only timeout is PARTIAL");
            int after=f.Api.Calls.Count;
            if(outcome is "no-callback" or "partial" or "subset-incomplete")
            {for(int c=0;c<4;c++) f.Api.Frame(c);Check(!f.Session.Snapshot().ReinitNoPrepare!.FullPass,"Late frames cannot overwrite terminal timeout result: "+outcome);}
            Check(f.Api.Calls.Count==after&&!f.Api.Calls.Skip(before).Any(x=>x is "setParameters" or "setAux" or "setAo2Value" or "getFrequency" or "stopScan"),"No forbidden configuration, automatic stop or retry: "+outcome);
            Check(f.Session.Snapshot().NeedsStop,"Failure retains manual cleanup Stop: "+outcome);
        }
        using(var f=new Fixture(Path.Combine(directory,"enabled-subset"),subset:true))
        {
            f.Ready();f.Api.DuringStart=()=> {f.Api.Frame(0);f.Api.Frame(2);};
            Finish(f.Session.ReinitNoPrepareAsync());Until(()=>f.Session.Snapshot().ReinitNoPrepare?.FullPass==true);
            Check(f.Session.Snapshot().ReinitNoPrepare!.Progress!.EnabledChannels.SequenceEqual(new[]{0,2}),"Enabled-channel mask from previous session controls FULL PASS");
            Until(()=>Read(f.Log.ReinitMarkerPath).Contains("FirstCompleteFrame"));
            var marker=JsonSerializer.Deserialize<RescanMarker>(Read(f.Log.ReinitMarkerPath),RollingDiagnosticLog.Json)!;
            Check(marker.Phase=="FirstCompleteFrame"&&marker.Reached.ContainsKey("AfterStartScan")&&marker.InitializeResult==true,"Synchronous Start callbacks cannot be overwritten by AfterStartScan marker");
            f.Api.ThrowStop=true;Finish(f.Session.StopAsync());
            Check(f.Session.Snapshot() is {State:ExternalState.StopFailed,NeedsStop:true}&&File.Exists(f.Log.ReinitMarkerPath),"Failed Stop preserves marker and cleanup requirement");
            f.Api.ThrowStop=false;Finish(f.Session.StopAsync());Check(f.Session.Snapshot() is {State:ExternalState.RequiresInitialize,NeedsStop:false}&&File.Exists(f.Log.ReinitMarkerPath),"Explicit cleanup retry returns to idle but preserves prior failed-Stop evidence");
        }
        using(var f=new Fixture(Path.Combine(directory,"external"),internalMode:false))
        {
            f.Ready();int before=f.Api.Calls.Count;Finish(f.Session.ReinitNoPrepareAsync());
            Check(f.Api.Calls.Skip(before).SequenceEqual(sequence),"External experiment does not resend AO2 or remeasure frequency");
            Check(f.Session.Snapshot().ReinitNoPrepare!.TimeoutMs==6400,"External timeout uses last measured frequency (10 Hz, 32 rows => 6400 ms)");
            for(int c=0;c<4;c++) f.Api.Frame(c);Until(()=>f.Session.Snapshot().ReinitNoPrepare?.FullPass==true);
        }
        var regression=ExternalParameters.LoadBaseline().Apply(ScanRequest.Default with {XPixel=1024,YPixel=1024,OversamplePixel=16,OversampleFrame=10,SpeedPosition=1});
        Check(SdkSession.RescanTimeoutMs(ScanTimingCalculator.Internal(regression.Wave).FrameTimeMs)==5170,"400 Hz / 1034 rows gives 5170 ms; fast sessions retain 2000 ms floor");

        string abrupt=Path.Combine(directory,"abrupt");Directory.CreateDirectory(abrupt);
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"HMM_FPGA_UI_Example.Managed.exe")) {UseShellExecute=false,CreateNoWindow=true};
        start.ArgumentList.Add("--reinit-abrupt-probe");start.ArgumentList.Add(abrupt);
        using(var child=Process.Start(start)??throw new InvalidOperationException("Could not start probe"))
        {if(!child.WaitForExit(10000)) {child.Kill();throw new TimeoutException("Probe did not exit");}Check(child.ExitCode!=0,"Independent fake experiment child terminates abruptly inside fake Start");}
        using(var probe=JsonDocument.Parse(Read(Path.Combine(abrupt,"probe.json"))))
        {
            Check(probe.RootElement.GetProperty("Calls").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(sequence),"Crash probe also entered exactly four allowed calls");
            string text=Read(probe.RootElement.GetProperty("FilePath").GetString()!);
            int click=text.LastIndexOf("EXPERIMENT REINIT_NO_PREPARE CLICKED",StringComparison.Ordinal);
            text=text[click..];
            Check(text.Contains("BEFORE startScan()")&&!text.Contains("AFTER startScan()"),"Crash-surviving log has BEFORE and no fabricated AFTER for experiment Start");
            Check(!probe.RootElement.GetProperty("SdkLoaded").GetBoolean(),"Abrupt probe never loaded native SDK");
        }
        string original=Read(Path.Combine(abrupt,"reinit-no-prepare-in-progress.json"));
        var recovered=new RollingDiagnosticLog(abrupt);
        Check(recovered.PreviousReinitCrashNotice?.Contains("Last phase: BeforeStartScan")==true&&Read(recovered.ReinitMarkerPath)==original,"Restart retains marker and reports last phase after successful Initialize");
        Check(Read(recovered.FilePath).Contains("Previous Reinit-No-Prepare test ended abnormally"),"Startup copies prior abnormal experiment into new durable log");
        var application=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var recoveryWindow=new MainWindow(new SdkSession(recovered),recovered);
        Check(recoveryWindow.InitializeButton.IsEnabled&&!recoveryWindow.RescanButton.IsEnabled&&recoveryWindow.FindName("ReinitButton") is null,"Recovery notice does not block normal Initialize; experimental preconditions still apply");
        recoveryWindow.SdkStatus.Text="OFFLINE RECOVERY FIXTURE — simulated marker; no hardware";
        Render(recoveryWindow,Path.Combine(directory,"startup-notice.png"));Close(recoveryWindow,recovered);
        Check(File.Exists(recovered.ReinitMarkerPath),"Ordinary close does not delete previous abnormal experiment marker");
        using(var f=new Fixture(Path.Combine(directory,"wpf")))
        {
            f.Ready();var window=new MainWindow(f.Session,f.Log);Align(window,f.Request);
            Check(window.FindName("ReinitButton") is null&&window.RescanButton.IsEnabled,"Legacy Reinit control is removed and Direct Start uses successful lifecycle");
            Finish(f.Session.ReinitNoPrepareAsync());Refresh(window);
            Check(window.FindName("ReinitButton") is null&&!window.ParametersButton.IsEnabled&&!window.StartButton.IsEnabled&&window.StopButton.IsEnabled,"Scanning experiment enables only manual Stop; no normal Prepare bypass");
            f.Api.Emit(0,16);Preview(window);Refresh(window);
            Check(f.Session.Snapshot().ReinitNoPrepare!.Timings.ContainsKey("Reinit Test → First Display"),"Reinit first progressive UI assignment measured independently of complete frame");
            f.Api.Emit(0,16);for(int c=1;c<4;c++) f.Api.Frame(c);
            Until(()=>f.Session.Snapshot().ReinitNoPrepare?.FullPass==true);Preview(window);Refresh(window);
            Check(f.Session.Snapshot().ReinitNoPrepare is {FullPass:true,SetParametersCalled:false}&&!window.TimingDetails.Text.Contains("REINIT + START"),"Historical result retained in diagnostics snapshot; removed experiment no longer displayed in API tab");
            window.SdkStatus.Text="OFFLINE REINIT FIXTURE — managed fake API / synthetic pixels; no hardware";Render(window,Path.Combine(directory,"reinit-preview.png"));
            Finish(f.Session.StopAsync());Refresh(window);
            Check(window.InitializeButton.IsEnabled&&!window.RescanButton.IsEnabled&&window.FindName("ReinitButton") is null,"Stopped experiment returns to normal Initialize gate and cannot chain the retired Reinit experiment");
            Close(window,f.Log);
        }
        application.Shutdown();
        Check(!Diagnostics.SdkLoaded(),"Native SDK absent after fixture; no hardware or Direct Rescan execution");
        return new {Passed=true,Checks=checks,Directory=directory,Hardware="NOT EXECUTED",DirectRescan="DISABLED; NOT EXECUTED",Experiment="Offline injected IHmmApi only",Crash="Abrupt diagnostics child in fake Start; not native-crash reproduction"};
    }
}
