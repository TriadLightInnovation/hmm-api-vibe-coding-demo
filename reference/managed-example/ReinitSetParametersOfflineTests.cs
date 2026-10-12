using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HmmExample.Acquisition;
using HmmExample.Interop;
using Fixture=HmmExample.ReinitNoPrepareOfflineTests.Fixture;

namespace HmmExample;

internal static class ReinitSetParametersOfflineTests
{
    private static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    private static void Finish(Task task)=>task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    private static void Until(Func<bool> condition)
    {var clock=Stopwatch.StartNew();while(!condition()) {if(clock.ElapsedMilliseconds>5000) throw new TimeoutException("Offline condition timed out");Thread.Sleep(1);}}
    private static string Read(string path)
    {using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(file);return reader.ReadToEnd();}
    private static string Fields(WaveParams wave)=>JsonSerializer.Serialize(wave,RollingDiagnosticLog.Json);
    private static void BlockNative()=>NativeLibrary.SetDllImportResolver(typeof(HmmNative).Assembly,(_,_,_)=>throw new InvalidOperationException("Native SDK forbidden in SetParameters fixture"));
    private static readonly string[] Sequence=["initialize","setErrorMessageCallback","setResampledImageCallback","setParameters","startScan"];
    internal static int AbruptProbe(string directory,string phase)
    {
        BlockNative();var f=new Fixture(directory);f.Ready();int start=f.Api.Calls.Count;
        void Kill()
        {
            File.WriteAllText(Path.Combine(directory,"probe.json"),JsonSerializer.Serialize(new {f.Log.FilePath,Calls=f.Api.Calls.Skip(start),SdkLoaded=Diagnostics.SdkLoaded()}));
            Process.GetCurrentProcess().Kill(); // Only this offline child; neither fake call enters native SDK.
        }
        if(phase=="setParameters") f.Api.DuringSetParameters=Kill;else if(phase=="startScan") f.Api.DuringStart=Kill;else return 2;
        Finish(f.Session.ReinitSetParametersAsync());return 99;
    }
    private static void Refresh(MainWindow window)=>typeof(MainWindow).GetMethod("RefreshSession",Private)!.Invoke(window,null);
    private static void Render(MainWindow window,string path)
    {
        var root=(FrameworkElement)window.Content;root.Measure(new Size(1600,1180));root.Arrange(new Rect(0,0,1600,1180));root.UpdateLayout();
        var image=new RenderTargetBitmap(1600,1180,96,96,PixelFormats.Pbgra32);image.Render(root);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);encoder.Save(file);
    }
    private static void Close(MainWindow window,RollingDiagnosticLog log)
    {Finish(log.FinishAsync());typeof(MainWindow).GetField("allowClose",Private)!.SetValue(window,true);window.Close();}
    internal static object Run(string directory)
    {
        Directory.CreateDirectory(directory);BlockNative();var checks=new List<string>();
        void Check(bool pass,string name) {if(!pass) throw new InvalidOperationException(name);checks.Add(name);}
        Check(!Diagnostics.SdkLoaded(),"No native SDK loaded at entry");
        using(var f=new Fixture(Path.Combine(directory,"no-source")))
        {
            Check(!f.Session.Snapshot().CanReinitSetParameters,"No saved successful acquisition: new experiment disabled");
            Finish(f.Session.ReinitSetParametersAsync());Check(f.Api.Calls.Count==0,"Ineligible action makes no API call");
            Finish(f.Session.InitializeAsync());Finish(f.Session.PrepareAsync());Finish(f.Session.StartAsync());Finish(f.Session.StopAsync());
            Check(!f.Session.Snapshot().CanReinitSetParameters,"Start true without complete data does not create successful snapshot");
        }
        using(var f=new Fixture(Path.Combine(directory,"exact-snapshot")))
        {
            f.Api.MutateParameterReference=true;f.Ready();var expected=f.Api.SentWaves.Single();int iterations=f.Api.SentIterations.Single();
            var oldRoute=f.Api.Image;var previous=f.Session.Snapshot();
            Check(previous.CanReinitSetParameters,"Complete successful prepared cycle creates saved snapshot");
            f.Session.SetRequestedParameters(f.Request with {XPixel=8,YPixel=48,OversamplePixel=7,OversampleFrame=5,NumberOfLines=32,SpeedPosition=7});
            Check(f.Session.Snapshot().CanReinitSetParameters,"Edited UI values do not replace or disable the saved-parameter experiment");
            int before=f.Api.Calls.Count,loads=f.Loads;bool level1=false,level2=false,beforeFlushed=false;
            f.Api.DuringSetParameters=()=>
            {
                level1=f.Session.Snapshot().ReinitSetParameters?.Level==1;
                string text=Read(f.Log.FilePath);beforeFlushed=text.LastIndexOf("BEFORE setParameters()",StringComparison.Ordinal)>text.LastIndexOf("AFTER setParameters()",StringComparison.Ordinal);
            };
            f.Api.DuringStart=()=>level2=f.Session.Snapshot().ReinitSetParameters is {Level:2,SetParametersReturned:true};
            Finish(f.Session.ReinitSetParametersAsync());var started=f.Session.Snapshot();
            Check(f.Api.Calls.Skip(before).SequenceEqual(Sequence),"Exact five-call sequence: Initialize, error/image callbacks, SetParameters, Start");
            Check(f.Loads==loads,"Experiment does not reload JSON or rebuild from current UI request");
            Check(Fields(f.Api.SentWaves.Last())==Fields(expected)&&f.Api.SentIterations.Last()==iterations,"All WaveParams fields and iterations exactly match original pre-call snapshot, even when native ref output mutated");
            Check(Fields(started.ReinitSetParameters!.Source!.Parameters.Wave)==Fields(expected),"Saved snapshot is immutable and not overwritten by second native ref mutation");
            Check(started.Parameters!.Wave.pixel_per_line==4&&started.Parameters.Wave.line_frequency_hz==400&&started.Requested!.XPixel==8,"Assembler uses saved geometry/frequency; current UI draft remains distinct");
            Check(level1&&level2&&started.ReinitSetParameters is {Level:3,FullPass:false,Status:"Waiting for data"},"Levels: initialize=1, SetParameters returned=2, Start true=3; no premature PASS");
            Check(beforeFlushed,"SetParameters BEFORE log flushed before fake API entry");
            Check(started.Generation>previous.Acquisition!.Generation&&!ReferenceEquals(oldRoute,f.Api.Image),"New generation and re-registered callback route");
            f.Api.Frame(0,oldRoute);
            Check(f.Session.Snapshot().ReinitSetParameters!.Progress is {HasNewCallback:false,HasNewFrame:false},"Old registered callback cannot satisfy new generation");
            f.Api.Emit(0,16);Check(f.Session.Snapshot().ReinitSetParameters is {Level:4,FullPass:false},"First new block reaches Level 4, not FULL PASS");
            f.Api.Emit(0,16);Check(!f.Session.Snapshot().ReinitSetParameters!.FullPass,"One channel's complete frame is insufficient for FULL PASS");
            for(int c=1;c<4;c++) f.Api.Frame(c);Until(()=>f.Session.Snapshot().ReinitSetParameters?.FullPass==true);
            Check(f.Session.Snapshot().ReinitSetParameters is {Status:"PASS",SetAuxCalled:false,PrepareCalled:false},"FULL PASS requires all enabled new frames; no AUX/Prepare");
            Check(f.Api.Calls.Count==before+5&&f.Session.Snapshot().NeedsStop,"PASS leaves acquisition running; no auto-stop or fallback");
            Finish(f.Session.StopAsync());Check(f.Session.Snapshot() is {State:ExternalState.RequiresInitialize,NeedsStop:false}&&!File.Exists(f.Log.SetParamMarkerPath),"Manual Stop returns Requires Initialize and removes matching clean marker");
            string text=Read(f.Log.FilePath);
            Check(new[]{"EXPERIMENT REINIT_SETPARAM_START","SetAuxCalled=false","BEFORE initialize()","AFTER initialize()","AFTER callback registration","BEFORE setParameters()","AFTER setParameters()","BEFORE startScan()","AFTER startScan() result=True","FirstCallback","FirstCompleteFrame"}.All(text.Contains),"Durable log includes all required API boundaries, flags and first data events");
        }
        using(var f=new Fixture(Path.Combine(directory,"after-no-prepare-fail")))
        {
            f.Ready();var original=f.Api.SentWaves.Single();var generation=f.Session.Snapshot().Acquisition!.Generation;
            Finish(f.Session.ReinitNoPrepareAsync());f.Expired.TrySetResult();Until(()=>f.Session.Snapshot().ReinitNoPrepare?.Status=="FAIL");
            Finish(f.Session.StopAsync());
            Check(f.Session.Snapshot().CanReinitSetParameters,"Clean Stop after previous No-Prepare/no-callback failure permits next independent test");
            f.Expired=new(TaskCreationOptions.RunContinuationsAsynchronously);int before=f.Api.Calls.Count;
            Finish(f.Session.ReinitSetParametersAsync());
            Check(f.Api.Calls.Skip(before).SequenceEqual(Sequence)&&Fields(f.Api.SentWaves.Last())==Fields(original),"Next test uses successful normal snapshot, not failed No-Prepare state");
            Check(f.Session.Snapshot().ReinitSetParameters!.Source!.AcquisitionGeneration==generation,"Snapshot provenance survives intervening failed experiment");
        }
        using(var f=new Fixture(Path.Combine(directory,"later-normal-no-data")))
        {
            f.Ready();var original=f.Api.SentWaves.Single();long originalGeneration=f.Session.Snapshot().Acquisition!.Generation;
            f.Session.SetRequestedParameters(f.Request with {XPixel=8,SpeedPosition=7});
            Finish(f.Session.InitializeAsync());Finish(f.Session.PrepareAsync());Finish(f.Session.StartAsync());Finish(f.Session.StopAsync());
            Finish(f.Session.ReinitSetParametersAsync());
            Check(Fields(f.Api.SentWaves.Last())==Fields(original)&&f.Session.Snapshot().ReinitSetParameters!.Source!.AcquisitionGeneration==originalGeneration,"Later prepared Start with no data cannot replace last successful acquisition snapshot");
        }
        using(var f=new Fixture(Path.Combine(directory,"newest-successful")))
        {
            f.Ready();f.Session.SetRequestedParameters(f.Request with {XPixel=8,SpeedPosition=2});
            Finish(f.Session.InitializeAsync());Finish(f.Session.PrepareAsync());Finish(f.Session.StartAsync());
            for(int c=0;c<4;c++) f.Api.Frame(c);Finish(f.Session.StopAsync());
            var newest=f.Api.SentWaves.Last();long generation=f.Session.Snapshot().Acquisition!.Generation;
            Finish(f.Session.ReinitSetParametersAsync());
            Check(Fields(f.Api.SentWaves.Last())==Fields(newest)&&f.Session.Snapshot().ReinitSetParameters!.Source!.AcquisitionGeneration==generation,"Newest complete successful normal acquisition replaces the older snapshot");
        }
        foreach(string mode in new[]{"initialize-false","setParameters-throw","start-false","no-callback","partial"})
        {
            using var f=new Fixture(Path.Combine(directory,mode));f.Ready();int before=f.Api.Calls.Count;
            f.Api.InitializeResult=mode!="initialize-false";f.Api.StartResult=mode!="start-false";f.Api.ThrowSetParameters=mode=="setParameters-throw";
            Finish(f.Session.ReinitSetParametersAsync());
            if(mode=="partial") f.Api.Emit(0,16);
            if(mode is "no-callback" or "partial") {f.Expired.TrySetResult();Until(()=>f.Session.Snapshot().ReinitSetParameters?.Status!="Waiting for data");}
            var state=f.Session.Snapshot();
            Check(state.ReinitSetParameters!.Status==(mode=="partial"?"PARTIAL":"FAIL")&&!state.ReinitSetParameters.FullPass,"Result classification: "+mode);
            if(mode=="initialize-false") Check(f.Api.Calls.Skip(before).SequenceEqual(new[]{"initialize"}),"False Initialize never sends parameters or starts");
            if(mode=="setParameters-throw") Check(!f.Api.Calls.Skip(before).Contains("startScan")&&!state.ReinitSetParameters.SetParametersReturned,"SetParameters exception prevents Start and does not manufacture return");
            if(mode=="no-callback")
            {
                Check(state is {State:ExternalState.Scanning,NeedsStop:true,LastError:null,ReinitSetParameters.Level:3},"Zero callback timeout remains Scanning / Level 3 with manual Stop available");
                int count=f.Api.Calls.Count;for(int c=0;c<4;c++) f.Api.Frame(c);
                Check(!f.Session.Snapshot().ReinitSetParameters!.FullPass&&f.Api.Calls.Count==count,"Late data cannot change timeout result or trigger fallback");
            }
            Check(!f.Api.Calls.Skip(before).Any(x=>x is "setAux" or "setAo2Value" or "getFrequency" or "EnsureAvailable(fake)" or "stopScan"),"No forbidden API or automatic Stop: "+mode);
        }
        using(var f=new Fixture(Path.Combine(directory,"subset"),subset:true))
        {
            f.Ready();f.Api.DuringStart=()=> {f.Api.Frame(0);f.Api.Frame(2);};Finish(f.Session.ReinitSetParametersAsync());Until(()=>f.Session.Snapshot().ReinitSetParameters?.FullPass==true);
            Check(f.Session.Snapshot().ReinitSetParameters!.Progress!.EnabledChannels.SequenceEqual(new[]{0,2}),"Enabled mask comes from saved snapshot, not fixed four channels");
            Until(()=>Read(f.Log.SetParamMarkerPath).Contains("FirstCompleteFrame"));
            var marker=JsonSerializer.Deserialize<RescanMarker>(Read(f.Log.SetParamMarkerPath),RollingDiagnosticLog.Json)!;
            Check(marker.Phase=="FirstCompleteFrame"&&marker.Reached.ContainsKey("AfterStartScan")&&marker.SetParametersReturned==true,"Callback during Start preserves advanced marker phase and SetParameters return evidence");
        }
        using(var f=new Fixture(Path.Combine(directory,"external-rejected"),internalMode:false))
        {f.Ready();Check(!f.Session.Snapshot().CanReinitSetParameters,"This parameter-vs-AUX isolation test is limited to successful Internal sessions");}
        foreach(string phase in new[]{"setParameters","startScan"})
        {
            string path=Path.Combine(directory,"abrupt-"+phase);Directory.CreateDirectory(path);
            var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"HMM_FPGA_UI_Example.Managed.exe")){UseShellExecute=false,CreateNoWindow=true};
            start.ArgumentList.Add("--setparam-abrupt-probe");start.ArgumentList.Add(path);start.ArgumentList.Add(phase);
            using(var child=Process.Start(start)??throw new InvalidOperationException("Probe did not start"))
            {if(!child.WaitForExit(10000)) {child.Kill();throw new TimeoutException("Probe did not terminate");}Check(child.ExitCode!=0,"Independent fake child ended abruptly inside "+phase);}
            using var probe=JsonDocument.Parse(Read(Path.Combine(path,"probe.json")));
            string text=Read(probe.RootElement.GetProperty("FilePath").GetString()!);text=text[text.LastIndexOf("EXPERIMENT REINIT_SETPARAM_START CLICKED",StringComparison.Ordinal)..];
            Check(text.Contains("BEFORE "+phase+"()")&&!text.Contains("AFTER "+phase+"()"),"BEFORE survives abrupt termination with no fabricated AFTER: "+phase);
            var log=new RollingDiagnosticLog(path);
            Check(log.PreviousSetParamCrashNotice?.Contains("CRASH")==true&&File.Exists(log.SetParamMarkerPath),"Separate marker survives and startup flags crash/unclean return: "+phase);
            Check(!probe.RootElement.GetProperty("SdkLoaded").GetBoolean(),"Crash surrogate did not load native SDK: "+phase);
            Finish(log.FinishAsync());
        }
        var application=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        using(var f=new Fixture(Path.Combine(directory,"wpf")))
        {
            f.Ready();var window=new MainWindow(f.Session,f.Log);
            window.XPixel.Text="128";window.YPixel.Text="512";window.SpeedSlider.Value=7;Refresh(window);
            Check(window.FindName("SetParamButton") is null&&window.RescanButton.IsEnabled,"Legacy SetParam UI removed and Direct Start retains last successful acquisition snapshot");
            Finish(f.Session.ReinitSetParametersAsync());Refresh(window);
            Check(window.RequestedFrequency.Text.Contains("400")&&window.ActiveParameters.Text.Contains("Current UI draft is NOT used"),"UI identifies saved-source timing/parameters despite edited UI drafts");
            f.Expired.TrySetResult();Until(()=>f.Session.Snapshot().ReinitSetParameters?.Status=="FAIL");Refresh(window);
            Check(window.StopButton.IsEnabled&&window.ExportButton.IsEnabled&&!window.InitializeButton.IsEnabled&&!window.ParametersButton.IsEnabled&&!window.StartButton.IsEnabled,"Zero callback timeout permits Export then manual Stop, with no automatic reinitialize");
            File.WriteAllText(Path.Combine(directory,"offline-diagnostics-export.json"),JsonSerializer.Serialize(new {Session=f.Session.Snapshot(),ReportedHardwareResults=ExperimentalHardwareEvidence.Results},Diagnostics.JsonOptions));
            Check(Read(Path.Combine(directory,"offline-diagnostics-export.json")).Contains("no callbacks arrived"),"Diagnostics preserves Paz's hardware result separately from this fake test");
            window.SdkStatus.Text="OFFLINE FAKE API — zero-callback timeout / saved parameters; no hardware";
            Render(window,Path.Combine(directory,"setparam-timeout.png"));
            Finish(f.Session.StopAsync());Refresh(window);
            Check(f.Session.Snapshot().State==ExternalState.RequiresInitialize&&window.InitializeButton.IsEnabled,"Returned manual Stop restores Requires Initialize");
            Close(window,f.Log);
        }
        application.Shutdown();Check(!Diagnostics.SdkLoaded(),"No hardware or native SDK execution; Direct Rescan not invoked");
        return new {Passed=true,Checks=checks,Directory=directory,Hardware="NOT TESTED",DirectRescan="DISABLED / NOT EXECUTED",Scope="Injected fake API and offscreen WPF only"};
    }
}
