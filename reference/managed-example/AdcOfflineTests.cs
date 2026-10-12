using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HmmExample.Acquisition;
using HmmExample.Interop;
namespace HmmExample;
internal static class AdcOfflineTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static void Finish(Task task)=>task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    private static void Until(Func<bool> test)
    {var sw=Stopwatch.StartNew();while(!test()) {if(sw.Elapsed.TotalSeconds>10) throw new TimeoutException();Thread.Sleep(2);}}
    private static void Refresh(MainWindow w) => typeof(MainWindow).GetMethod("RefreshSession",Private)!.Invoke(w,null);
    private static void Preview(MainWindow w) => typeof(MainWindow).GetMethod("RefreshPreview",Private)!.Invoke(w,null);
    private static void Pump(Task task)
    {
        var frame=new DispatcherFrame();
        _=task.ContinueWith(_=>Application.Current.Dispatcher.BeginInvoke(()=>frame.Continue=false));
        Dispatcher.PushFrame(frame);Finish(task);
    }
    private static void Error(HmmNative.ErrorCallback callback,string message)
    {
        var bytes=Encoding.UTF8.GetBytes(message);var data=Marshal.AllocHGlobal(bytes.Length);var meta=Marshal.AllocHGlobal(Marshal.SizeOf<ErrorMessage>());
        try {Marshal.Copy(bytes,0,data,bytes.Length);Marshal.StructureToPtr(new ErrorMessage {error_message=data,message_size=bytes.Length},meta,false);callback(meta);}
        finally {Marshal.FreeHGlobal(meta);Marshal.FreeHGlobal(data);}
    }
    private sealed class FakeApi:IHmmApi
    {
        internal readonly ReinitNoPrepareOfflineTests.FakeApi Inner=new();
        internal bool Result=true;internal Action? During;internal int CalibrationThread;
        public void EnsureAvailable()=>Inner.EnsureAvailable();
        public bool Initialize()=>Inner.Initialize();
        public bool AdcCalibration() {Inner.Calls.Add("adcCalibration");CalibrationThread=Environment.CurrentManagedThreadId;During?.Invoke();return Result;}
        public void SetParameters(ref WaveParams wave,int n)=>Inner.SetParameters(ref wave,n);
        public bool SetAo2Value(float a,float o)=>Inner.SetAo2Value(a,o);
        public void GetFrequency(out FrequencyParams f)=>Inner.GetFrequency(out f);
        public void SetAux(bool v)=>Inner.SetAux(v);
        public bool StartScan()=>Inner.StartScan();public void StopScan()=>Inner.StopScan();
        public void SetImageCallback(HmmNative.ImageCallback c)=>Inner.SetImageCallback(c);
        public void SetErrorCallback(HmmNative.ErrorCallback c)=>Inner.SetErrorCallback(c);
    }
    private static void Render(FrameworkElement root,string path,int width,int height)
    {
        root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var f=File.Create(path);png.Save(f);
    }
    internal static object Run(string directory)
    {
        Directory.CreateDirectory(directory);
        NativeLibrary.SetDllImportResolver(typeof(HmmNative).Assembly,(_,_,_)=>throw new InvalidOperationException("Native SDK prohibited in offline ADC tests"));
        var checks=new List<string>();
        void Check(bool yes,string name) {if(!yes) throw new InvalidOperationException(name);checks.Add(name);}
        Check(SdkWarnings.IsAdcOverRange("Channel2 ADC over range") && SdkWarnings.IsAdcOverRange("Channel3 ADC overrange"),"Classifies ADC over-range variants");
        Check(!SdkWarnings.IsAdcOverRange("image float outside display range")&&!SdkWarnings.IsAdcOverRange("ADC link failure"),"Unrelated errors remain fatal");
        var method=typeof(HmmNative).GetMethod("AdcCalibration",BindingFlags.Static|BindingFlags.NonPublic)!;
        Check(method.ReturnType==typeof(bool)&&method.GetParameters().Length==0&&method.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>()?.Value==UnmanagedType.I1&&
            method.GetCustomAttribute<DllImportAttribute>() is {EntryPoint:"adcCalibration",CallingConvention:CallingConvention.Cdecl,ExactSpelling:true},"Native declaration: exact adcCalibration, zero args, Cdecl x64, I1 bool");
        Check(RuntimeCompatibility.Inspect(Path.Combine(AppContext.BaseDirectory,"HMMSysCoreAPILib.dll")).Exports.Contains("adcCalibration"),"Static PE inspection finds calibration export; DLL not loaded");
        Check(Diagnostics.MarshalingCheck(),"All native signatures retain expected marshaling");
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var baseline=ExternalParameters.LoadBaseline();
        var log=new RollingDiagnosticLog(Path.Combine(directory,"calibration"));var api=new FakeApi();
        var session=new SdkSession(log,api,baseline.Apply);var window=new MainWindow(session,log);
        window.XPixel.Text="4";window.YPixel.Text="32";window.OversamplePixel.Text="0";window.OversampleFrame.Text="0";
        Refresh(window);
        Check(!window.AdcCalibrationMenuItem.IsEnabled&&session.CalibrationSnapshot().Status=="Not Run"&&api.Inner.Calls.Count==0,"Startup: Not Run, menu disabled, zero API calls");
        Finish(session.AdcCalibrationAsync());Check(api.Inner.Calls.Count==0,"Pre-initialize calibration hard gate");
        Finish(session.InitializeAsync());Refresh(window);
        Check(window.AdcCalibrationMenuItem.IsEnabled,"Initialized calibration enabled");
        Check(!api.Inner.Calls.Contains("adcCalibration"),"Initialize does not auto-calibrate");
        api.Inner.Calls.Clear();Finish(window.RequestAdcCalibrationAsync(()=>false));Check(api.Inner.Calls.Count==0,"Cancel confirmation has zero API calls");
        int uiThread=Environment.CurrentManagedThreadId;
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        api.During=()=> {entered.Set();if(!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();};
        var task=window.RequestAdcCalibrationAsync(()=>true);Check(entered.Wait(TimeSpan.FromSeconds(5)),"Confirmed calibration enters background API");Refresh(window);
        Check(api.CalibrationThread!=uiThread&&session.Snapshot().Busy,"Calibration runs off UI thread with busy gate");
        Check(!window.AdcCalibrationMenuItem.IsEnabled&&!window.InitializeButton.IsEnabled&&!window.ParametersButton.IsEnabled&&!window.StartButton.IsEnabled&&!window.StopButton.IsEnabled&&!window.ScanOnceButton.IsEnabled&&!window.RescanButton.IsEnabled,"All hardware controls disabled during calibration");
        release.Set();Pump(task);api.During=null;
        Check(api.Inner.Calls.SequenceEqual(new[]{"adcCalibration"}),"Confirmed calibration calls exactly one API");
        Check(session.CalibrationSnapshot() is {Status:"PASS",Result:true,ApiTimeMs:not null,Timestamp:not null}&&session.Snapshot().State==ExternalState.Initialized,"True calibration result PASS, timing and Initialized state retained");
        Finish(session.PrepareAsync());Refresh(window);Check(window.AdcCalibrationMenuItem.IsEnabled&&window.StartButton.IsEnabled,"Prepared calibration allowed");
        var before=session.Snapshot();var wave=JsonSerializer.Serialize(before.Parameters!.Wave,RollingDiagnosticLog.Json);
        var average=session.FrameAverage;bool? composite=window.CompositeEnabled.IsChecked;
        api.Inner.Calls.Clear();Finish(session.AdcCalibrationAsync());Refresh(window);var after=session.Snapshot();
        Check(after.State==ExternalState.PrepareRequired&&!after.ParametersSent&&!window.StartButton.IsEnabled,"Prepared calibration invalidates Prepare and disables Start");
        Check(before.Requested==after.Requested&&wave==JsonSerializer.Serialize(after.Parameters!.Wave,RollingDiagnosticLog.Json)&&average==session.FrameAverage&&composite==window.CompositeEnabled.IsChecked,"Calibration preserves request, WaveParams, averages and Composite");
        Check(api.Inner.Calls.SequenceEqual(new[]{"adcCalibration"}),"No implicit Prepare, Initialize or hardware changes after calibration");
        api.Result=false;Finish(session.AdcCalibrationAsync());Refresh(window);
        Check(session.CalibrationSnapshot() is {Status:"FAIL",Result:false}&&session.Snapshot().LastError is null&&window.LastAdcCalibration.Text.Contains("FAIL"),"False result displayed as calibration FAIL, separate from Last Error");
        api.Result=true;api.During=()=>Error(api.Inner.Error!,"Channel2 ADC over range");Finish(session.AdcCalibrationAsync());
        Check(session.CalibrationSnapshot() is {Status:"PASS",WarningsDuringCall:1}&&session.Snapshot().LastError is null,"SDK warning during calibration counted but not fatal");
        api.During=()=>Error(api.Inner.Error!,"ADC link failure");Finish(session.AdcCalibrationAsync());Refresh(window);
        Check(session.CalibrationSnapshot().Status=="FAIL"&&session.Snapshot().LastError=="ADC link failure"&&window.LastError.Text.Contains("ADC link failure"),"Actual SDK calibration error fails result and appears in Last Error");
        Error(api.Inner.Error!,"Channel0 ADC over range");Refresh(window);
        Check(session.Snapshot().LastError=="ADC link failure"&&window.LastWarning.Text.Contains("Channel0 ADC over range"),"Warning retains original SDK wording and cannot overwrite fatal Last Error");
        api.During=()=>throw new EntryPointNotFoundException("test missing adcCalibration");Finish(session.AdcCalibrationAsync());
        Check(session.CalibrationSnapshot() is {Status:"FAIL",Result:null}&&session.CalibrationSnapshot().Error!.Contains("missing"),"Missing export / API exception reports failure without invented bool return");
        api.During=null;Finish(session.StopAsync());Refresh(window);
        Check(!session.CanAdcCalibration()&&!window.AdcCalibrationMenuItem.IsEnabled,"Stop RequiresInitialize disables calibration");
        api.Inner.Calls.Clear();Finish(session.AdcCalibrationAsync());Check(api.Inner.Calls.Count==0,"Stopped calibration hard gate makes no API call");
        // Fresh clean generation: only ADC warnings, no prior fatal error.
        Finish(session.InitializeAsync());Finish(session.PrepareAsync());Finish(session.StartAsync());Refresh(window);
        Check(!window.AdcCalibrationMenuItem.IsEnabled,"Scanning calibration disabled");api.Inner.Calls.Clear();Finish(session.AdcCalibrationAsync());Check(api.Inner.Calls.Count==0,"Scanning calibration hard gate");
        long prior=session.WarningSnapshot().Total;
        for(int i=0;i<1000;i++) Error(api.Inner.Error!,"Channel2 ADC over range");
        Error(api.Inner.Error!,"Channel3 ADC over range");Error(api.Inner.Error!,"Channel17 ADC over range");
        var warnings=session.WarningSnapshot();
        Check(warnings.Total==prior+1002&&warnings.Counters.Single(c=>c.SdkChannel=="Channel2").Count==1001,"Repeated warnings increment exact SDK channel counters");
        Check(warnings.Counters.Single(c=>c.SdkChannel=="Channel17") is {Count:1,First:not null,Last:not null},"Future SDK channels retain first/last/count");
        Refresh(window);
        Check(session.Snapshot().State==ExternalState.Scanning&&session.Snapshot().LastError is null&&window.StopButton.IsEnabled&&window.CompositeEnabled.IsEnabled,"Warnings preserve Scanning, Stop and Composite controls");
        Check(app.Windows.Count==1,"1002 warnings do not create popup windows");
        for(int c=0;c<4;c++) api.Inner.Frame(c);Preview(window);Refresh(window);
        Check(session.Snapshot().Acquisition!.Channels.All(c=>c.Callbacks>0&&c.CompletedFrames==1&&c.DisplayedUpdates>0&&c.RejectedCallbacks==0&&c.DisplayBlockedFrames==0),"Internal warning: all channels continue callback, complete-frame publication and bitmap display");
        Until(()=>session.Snapshot().Acquisition!.FrameStatistics!.Latest.All(x=>x is not null));
        Check(session.Snapshot().Acquisition!.FrameStatistics!.Latest.All(x=>x!.FiniteCount==128&&x.NonFiniteCount==0),"Internal completed frame statistics collected on background worker");
        Render((FrameworkElement)window.Content,Path.Combine(directory,"adc-ui.png"),1444,1000);
        var dialog=new AdcCalibrationDialog();Render((FrameworkElement)dialog.Content,Path.Combine(directory,"calibration-confirmation.png"),500,180);dialog.Close();
        Finish(session.StopAsync());Check(session.Snapshot().CanDirectRescan,"Warnings do not block Direct Start lifecycle eligibility");
        api.Inner.DuringStart=()=> {Error(api.Inner.Error!,"Channel2 ADC over range");for(int c=0;c<4;c++)api.Inner.Frame(c);};
        api.Inner.Calls.Clear();Finish(session.DirectRescanAsync());Until(()=>session.Snapshot().DirectRescan?.Status=="PASS");
        Check(api.Inner.Calls.SequenceEqual(new[]{"startScan"}),"Direct Start with warning still PASS and only calls startScan");Finish(session.StopAsync());
        // Scan Once must not treat a warning as its cancellation/fault signal.
        Finish(session.ScanOnceAsync());Check(session.Snapshot().ScanOnce?.Status=="Complete","Scan Once completes all channels despite ADC warning");
        session.FlushWarnings(true);Finish(log.FinishAsync());var text=File.ReadAllText(log.FilePath);
        Check(text.Contains("ADC_CALIBRATION REQUESTED")&&text.Contains("BEFORE adcCalibration()")&&text.Contains("AFTER adcCalibration() result=True")&&text.Contains("API elapsed ms="),"Persistent calibration request/boundary/result/timing records");
        Check(text.Split("BEFORE adcCalibration()").Length==text.Split("AFTER adcCalibration()").Length+1,"Throwing API has BEFORE but no fabricated AFTER return");
        Check(text.Contains("ADC_OVERRANGE FIRST")&&text.Contains("ADC_OVERRANGE SUMMARY")&&text.Split("ADC_OVERRANGE").Length<25,"High-frequency warning log is first plus bounded summaries");
        Check(text.Contains("FRAME_STATS"),"Finite statistics persisted without image payload");
        typeof(MainWindow).GetField("allowClose",Private)!.SetValue(window,true);window.Close();
        using(var fixture=new ReinitNoPrepareOfflineTests.Fixture(Path.Combine(directory,"external"),false))
        {
            Finish(fixture.Session.InitializeAsync());Finish(fixture.Session.PrepareAsync());Finish(fixture.Session.StartAsync());
            Error(fixture.Api.Error!,"Channel1 ADC over range");
            for(int c=0;c<4;c++) {fixture.Api.Frame(c);Check(fixture.Session.CurrentCapture!.TryTake(c,out var frame)&&frame is not null,"External full frame remains available channel "+c);fixture.Session.CurrentCapture.Displayed(c);}
            Check(fixture.Session.Snapshot().Acquisition!.Channels.All(c=>c.CompletedFrames==1&&c.DisplayedFrames==1&&c.RejectedCallbacks==0),"External frame-oriented display unaffected by warning");
            Until(()=>fixture.Session.Snapshot().Acquisition!.FrameStatistics!.Latest.All(x=>x is not null));
            Check(fixture.Session.Snapshot().State==ExternalState.Scanning&&fixture.Session.Snapshot().LastError is null,"External warning does not change acquisition state");
        }
        var stats=new CompletedFrameStatistics(_=>{});float[] values=[-2,0,4,float.NaN,float.PositiveInfinity,float.NegativeInfinity];
        var original=(float[])values.Clone();stats.Publish(new(0,3,2,values,1,Stopwatch.GetTimestamp(),99));
        stats.Publish(new(1,2,1,[float.NaN,float.PositiveInfinity],1,Stopwatch.GetTimestamp(),99));
        Until(()=>stats.Snapshot().Latest[0] is not null&&stats.Snapshot().Latest[1] is not null);
        var st=stats.Snapshot().Latest[0]!;
        Check(st.FiniteMin==-2&&st.FiniteMax==4&&Math.Abs(st.FiniteMean!.Value-2d/3)<1e-12&&st.NonFiniteCount==3,"Finite-only min/max/mean excludes NaN and infinities");
        Check(stats.Snapshot().Latest[1] is {FiniteMin:null,FiniteMax:null,FiniteMean:null,NonFiniteCount:2},"All-nonfinite frame reports null statistics, never fabricated zero");
        Check(values.SequenceEqual(original),"Statistics never mutate source floats or normalization");
        stats.Publish(new(0,3,2,values,2,Stopwatch.GetTimestamp(),99));Check(stats.Snapshot().SkippedSamples==1,"Diagnostic sample throttling independent of acquisition counters");stats.Retire();
        Check(!Diagnostics.SdkLoaded(),"No native SDK loaded; all API calls managed fakes");app.Shutdown();
        return new {Passed=true,Count=checks.Count,Checks=checks,Hardware="NOT TESTED",NativeLoaded=false};
    }
}
