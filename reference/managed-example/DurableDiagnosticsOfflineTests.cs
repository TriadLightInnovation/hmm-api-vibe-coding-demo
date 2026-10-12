using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using HmmExample.Acquisition;
using HmmExample.Interop;

namespace HmmExample;

// No DirectRescanAsync call exists in this fixture. Marker transitions are diagnostic simulations only.
internal static class DurableDiagnosticsOfflineTests
{
    internal static int AbruptProbe(string directory)
    {
        NativeLibrary.SetDllImportResolver(typeof(HmmNative).Assembly,(_,_,_)=>throw new InvalidOperationException("SDK prohibited in abrupt probe"));
        var log=new RollingDiagnosticLog(directory);
        log.SetContext(8,9,ExternalState.RequiresInitialize);
        log.Critical("Simulation","DIRECT RESCAN CLICKED (diagnostics only; no SDK)");
        log.BeginRescan(new {Simulation=true,NativeCalls=0});
        log.BindRescanGeneration(10);log.SetContext(8,10,ExternalState.Starting);
        log.RescanPhase("BeforeStartScan");
        log.Critical("Simulation","BEFORE startScan() surrogate; native API will NOT be invoked");
        File.WriteAllText(Path.Combine(directory,"probe.json"),JsonSerializer.Serialize(new {log.FilePath,SdkLoaded=Diagnostics.SdkLoaded()}));
        // Abrupt termination of this explicitly spawned test process: no finally/FinishAsync.
        Process.GetCurrentProcess().Kill();
        return 99;
    }
    private static string ReadText(string path)
    {using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(file);return reader.ReadToEnd();}
    private static void Finish(Task task)=>task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    private static RescanMarker ReadMarker(RollingDiagnosticLog log)=>JsonSerializer.Deserialize<RescanMarker>(ReadText(log.MarkerPath),RollingDiagnosticLog.Json)!;
    private sealed class BoundaryApi(RollingDiagnosticLog log) : IHmmApi
    {
        internal readonly List<string> Calls=[];
        internal bool ThrowStart;
        private void Call(string name)
        {
            string text=ReadText(log.FilePath);
            if(!text.Contains("BEFORE "+name+"()")) throw new InvalidOperationException("BEFORE not flushed when fake entered: "+name);
            Calls.Add(name);
        }
        public void EnsureAvailable() {Calls.Add("EnsureAvailable(fake)");}
        public bool Initialize() {Call("initialize");return true;}
        public void SetAux(bool value)=>Call("setAux");
        public bool SetAo2Value(float amplitude,float offset) {Call("setAo2Value");return true;}
        public void GetFrequency(out FrequencyParams frequency) {Call("getFrequency");frequency=new(){line_frequency_hz=400,pos_time=1,neg_time=1};}
        public void SetParameters(ref WaveParams wave,int iterations)=>Call("setParameters");
        public bool StartScan() {Call("startScan");if(ThrowStart) throw new InvalidOperationException("SIMULATED managed start failure",new ArgumentException("inner detail"));return true;}
        public void StopScan()=>Call("stopScan");
        public void SetImageCallback(HmmNative.ImageCallback callback)=>Call("setResampledImageCallback");
        public void SetErrorCallback(HmmNative.ErrorCallback callback)=>Call("setErrorMessageCallback");
    }
    private static void Emit(IAcquisitionCapture capture,long generation,int channel,int rows,int width)
    {
        var values=new float[rows*width];
        IntPtr data=Marshal.AllocHGlobal(values.Length*sizeof(float)),meta=Marshal.AllocHGlobal(Marshal.SizeOf<CResampledImage>());
        try
        {
            Marshal.Copy(values,0,data,values.Length);
            Marshal.StructureToPtr(new CResampledImage {channel=channel,width=width,height=32,image_size=values.Length,image_data=data,index0_line_number=rows},meta,false);
            capture.Receive(meta,generation,generation);
        }
        finally {Marshal.FreeHGlobal(meta);Marshal.FreeHGlobal(data);}
    }
    internal static object Run(string directory)
    {
        NativeLibrary.SetDllImportResolver(typeof(HmmNative).Assembly,(_,_,_)=>throw new InvalidOperationException("Native SDK prohibited in durable diagnostics fixture"));
        Directory.CreateDirectory(directory);
        var checks=new List<string>();
        void Check(bool passed,string name) {if(!passed) throw new InvalidOperationException(name);checks.Add(name);}
        Check(!Diagnostics.SdkLoaded(),"SDK absent before offline fixture");
        string abrupt=Path.Combine(directory,"abrupt");Directory.CreateDirectory(abrupt);
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"HMM_FPGA_UI_Example.Managed.exe")) {UseShellExecute=false,CreateNoWindow=true};
        start.ArgumentList.Add("--durable-abrupt-probe");start.ArgumentList.Add(abrupt);
        using(var child=Process.Start(start)??throw new InvalidOperationException("Probe failed to start"))
        {
            if(!child.WaitForExit(10000)) {child.Kill();throw new TimeoutException("Probe did not terminate");}
            Check(child.ExitCode!=0,"Independent diagnostics-only child terminated abruptly");
        }
        using(var probe=JsonDocument.Parse(ReadText(Path.Combine(abrupt,"probe.json"))))
        {
            string text=ReadText(probe.RootElement.GetProperty("FilePath").GetString()!);
            Check(text.Contains("BEFORE startScan() surrogate")&&!text.Contains("AFTER startScan()"),"BEFORE survives abrupt exit without fabricated AFTER");
            Check(!text.Contains("Logging shutdown"),"Probe log persisted without FinishAsync or clean shutdown");
            Check(!probe.RootElement.GetProperty("SdkLoaded").GetBoolean(),"Abrupt probe did not load native SDK");
        }
        string original=ReadText(Path.Combine(abrupt,"rescan-in-progress.json"));
        var recovered=new RollingDiagnosticLog(abrupt);
        Check(ReadMarker(recovered).Phase=="BeforeStartScan","Marker survives abnormal termination at last durable phase");
        Check(recovered.PreviousCrashNotice?.Contains("did not exit cleanly")==true,"Startup abnormal indication includes phase");
        Check(ReadText(recovered.FilePath).Contains("Previous Direct Rescan ended abnormally")&&ReadText(recovered.FilePath).Contains("BeforeStartScan"),"Previous marker copied into new launch log immediately");
        Check(ReadText(recovered.MarkerPath)==original,"Startup preserves previous marker bytes");

        var application=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var window=new MainWindow(new SdkSession(recovered),recovered);
        Check(window.MetadataDetails.Text.Contains("BeforeStartScan"),"WPF Diagnostics preserves previous crash phase");
        Check(window.InitializeButton.IsEnabled&&!window.StartButton.IsEnabled,"Previous marker does not block normal Initialize; Prepare/Start still follow lifecycle");
        // Test actual WPF event delivery. Only this test observer sets Handled after the production hook.
        using(var hooks=new ManagedExceptionDiagnostics(application,recovered))
        {
            var frame=new DispatcherFrame();bool productionLeftUnhandled=false;
            void Observed(object sender,DispatcherUnhandledExceptionEventArgs e) {productionLeftUnhandled=!e.Handled;e.Handled=true;frame.Continue=false;}
            application.DispatcherUnhandledException+=Observed;
            application.Dispatcher.BeginInvoke(new Action(()=>throw new InvalidOperationException("WPF fixture exception",new ArgumentException("WPF inner"))));
            Dispatcher.PushFrame(frame);
            application.DispatcherUnhandledException-=Observed;
            Check(productionLeftUnhandled,"Production WPF hook logs but does not mark handled");
            Exception sample;
            try {throw new InvalidOperationException("Managed fixture exception",new ArgumentException("nested fixture"));}
            catch(Exception error) {sample=error;}
            hooks.DomainException(typeof(DurableDiagnosticsOfflineTests),new UnhandledExceptionEventArgs(sample,true));
            var taskArgs=new UnobservedTaskExceptionEventArgs(new AggregateException(sample));
            hooks.TaskException(null,taskArgs);
            Check(!taskArgs.Observed,"Task hook does not alter exception observation policy");
            string text=ReadText(recovered.FilePath);
            Check(new[]{"DispatcherUnhandledException","AppDomain.UnhandledException","TaskScheduler.UnobservedTaskException","InvalidOperationException","nested fixture","WPF inner","DurableDiagnosticsOfflineTests"}.All(text.Contains),"All three handler paths synchronously retain type/message/stack/inner; WPF via real event, Domain/Task via direct handler unit calls");
        }
        Finish(recovered.FinishAsync());
        typeof(MainWindow).GetField("allowClose",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,true);window.Close();
        Check(File.Exists(recovered.MarkerPath),"Ordinary app/log shutdown does not delete previous abnormal marker");
        application.Shutdown();

        var clean=new RollingDiagnosticLog(Path.Combine(directory,"clean"));
        clean.SetContext(3,4,ExternalState.Starting);clean.BeginRescan(new {Simulation=true});
        Check(ReadMarker(clean).Phase=="ButtonClicked","ButtonClicked persisted immediately");
        clean.BindRescanGeneration(5);clean.RescanPhase("BeforeStartScan");
        Check(ReadMarker(clean).Generation==5,"Marker binds new acquisition generation before API");
        // Simulate synchronous callbacks inside startScan and delayed AFTER observation.
        clean.RescanPhase("FirstCallback");clean.RescanPhase("FirstCompleteFrame");clean.RescanPhase("AfterStartScan");
        var advanced=ReadMarker(clean);
        Check(advanced.Phase=="FirstCompleteFrame"&&advanced.Reached.ContainsKey("AfterStartScan"),"AFTER cannot regress earlier callback/frame evidence");
        clean.EndRescan(4);
        Check(File.Exists(clean.MarkerPath),"Wrong acquisition cannot delete marker");
        clean.First(5,0,"FirstCallback","queued milestone");clean.EndRescan(5);
        Check(!File.Exists(clean.MarkerPath),"Clean diagnostic completion removes matching marker (no DirectRescan execution)");
        Finish(clean.FinishAsync());
        Check(!File.Exists(clean.MarkerPath),"Delayed queued event cannot recreate cleanly removed marker");
        Check(ReadText(clean.FilePath).Contains("StoppedCleanly"),"Clean completion durably recorded before marker removal");

        var queued=new RollingDiagnosticLog(Path.Combine(directory,"queued-marker"));
        queued.SetContext(6,7,ExternalState.Starting);queued.BeginRescan(new {Simulation=true});
        queued.RescanPhase("BeforeStartScan");
        queued.First(7,2,"FirstCallback","simulated callback thread observation");
        queued.First(7,2,"FirstCompleteFrame","simulated frame completion");
        Finish(queued.FinishAsync());
        Check(ReadMarker(queued).Phase=="FirstCompleteFrame"&&ReadMarker(queued).Reached.ContainsKey("FirstCallback"),"Queued acquisition milestones atomically advance persisted marker");
        Check(File.Exists(queued.MarkerPath),"Complete frame alone does not delete in-progress marker");
        var restart=new RollingDiagnosticLog(queued.DirectoryPath);
        restart.AbandonActiveRescan();restart.EndRescan(7);
        Check(File.Exists(restart.MarkerPath),"New lifecycle cannot clear previous process marker");
        restart.SetContext(1,8,ExternalState.Starting);restart.BeginRescan(new {Simulation=true});
        Check(Directory.GetFiles(restart.DirectoryPath,"rescan-prior-*.json").Length==1,"New explicit diagnostic attempt archives previous marker");
        restart.EndRescan(8);Finish(restart.FinishAsync());

        var ownership=new RollingDiagnosticLog(Path.Combine(directory,"ownership"));
        ownership.SetContext(1,99,ExternalState.Starting);ownership.BeginRescan(new {Simulation=true});
        var foreign=ReadMarker(ownership) with {Attempt=Guid.NewGuid()};
        File.WriteAllText(ownership.MarkerPath,JsonSerializer.Serialize(foreign,RollingDiagnosticLog.Json));
        bool refused=false;try {ownership.EndRescan(99);} catch(IOException) {refused=true;}
        Check(refused&&ReadMarker(ownership).Attempt==foreign.Attempt,"Cleanup preserves a marker replaced by another attempt");
        Finish(ownership.FinishAsync());

        var baseline=ExternalParameters.LoadBaseline();
        var selected=ScanRequest.Default with {XPixel=4,YPixel=32,OversamplePixel=0,OversampleFrame=0,NumberOfLines=16};
        foreach(bool internalMode in new[]{true,false})
        {
            var log=new RollingDiagnosticLog(Path.Combine(directory,internalMode?"callbacks-internal":"callbacks-external"));
            log.SetContext(1,42,ExternalState.Scanning);
            var settings=baseline.Apply(selected with {InternalMode=internalMode});
            IAcquisitionCapture capture=internalMode?new InternalCapture(42,settings,log):new ExternalCapture(42,settings,log);
            capture.ArmStart();
            var samplingWindow=Stopwatch.StartNew();
            Parallel.For(0,4,c=> {for(int i=0;i<1000;i++) Emit(capture,42,c,internalMode?16:32,4);});
            var snapshot=capture.Snapshot();
            Check(snapshot.Channels.All(c=>c.Callbacks==1000&&c.CompletedFrames>0),$"{(internalMode?"Internal":"External")}: 4000 synthetic callbacks processed");
            capture.FreezeAndSnapshot();Finish(log.FinishAsync());
            var lines=File.ReadAllLines(log.FilePath);
            Check(lines.Count(x=>x.Contains("FirstCallback:"))==4&&lines.Count(x=>x.Contains("FirstCompleteFrame:"))==4&&lines.Count(x=>!x.Contains("FRAME_STATS"))==10,"Only first callback/frame per channel persisted; no per-block log spam: "+internalMode);
            Check(lines.Count(x=>x.Contains("FRAME_STATS"))<=4*(1+(int)samplingWindow.Elapsed.TotalSeconds),"Frame statistics are rate-limited independently of per-block logging: "+internalMode);
            Check(log.Failure is null&&log.DroppedEvents==0,"No lost logging events (Internal="+internalMode+")");
            Check(lines.All(x=>new[]{" ms TID=","Session=","Generation=","State=","Operation=","Message="}.All(x.Contains)),"Every persisted line has diagnostic context: "+internalMode);
        }
        // Normal lifecycle only, with fake IHmmApi implementations. NEVER DirectRescanAsync.
        foreach(bool throwStart in new[]{false,true})
        {
            var log=new RollingDiagnosticLog(Path.Combine(directory,throwStart?"fake-exception":"fake-lifecycle"));
            var fake=new BoundaryApi(log) {ThrowStart=throwStart};
            var session=new SdkSession(log,fake,r=>baseline.Apply(r));
            session.SetRequestedParameters(selected with {InternalMode=false});
            Finish(session.InitializeAsync());Finish(session.PrepareAsync());Finish(session.StartAsync());Finish(session.StopAsync());
            Finish(log.FinishAsync());string text=ReadText(log.FilePath);
            Check(new[]{"initialize","setAux","setAo2Value","getFrequency","setParameters","startScan","stopScan"}.All(fake.Calls.Contains),"All seven boundary paths exercised with fake API only: "+throwStart);
            Check(fake.Calls.Where(x=>!x.StartsWith("EnsureAvailable")&&(!throwStart||x!="startScan")).All(x=>text.Contains("AFTER "+x+"() result=")),"Returning APIs have flushed AFTER result: "+throwStart);
            Check(text.Contains("Prepare BEGIN")||text.Contains("Operation=Prepare Message=BEGIN"),"Normal lifecycle BEGIN recorded (throwStart="+throwStart+")");
            if(throwStart) Check(!text.Contains("AFTER startScan()")&&text.Contains("SIMULATED managed start failure")&&text.Contains("inner detail"),"Throwing surrogate produces exception detail and no false AFTER");
            else Check(text.Contains("AFTER startScan() result=True")&&session.Snapshot().State==ExternalState.RequiresInitialize,"Normal fake Start/Stop preserves state machine");
        }
        Check(!Diagnostics.SdkLoaded(),"SDK still absent after fixture; no native hardware calls or DirectRescan invocation");
        return new {Passed=true,Checks=checks,Directory=directory,Hardware="NOT EXECUTED",DirectRescan="NOT EXECUTED; marker methods simulated independently",Registry="NOT MODIFIED"};
    }
}
