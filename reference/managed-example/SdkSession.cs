using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HmmExample.Acquisition;
using HmmExample.Interop;

namespace HmmExample;

internal enum ExternalState { RequiresInitialize, Initializing, Initialized, InitializeFailed, Preparing, Prepared, PrepareFailed, Starting, Scanning, StartFailed, Stopping, StopFailed, PrepareRequired }
internal sealed record ScanOnceSnapshot(string Status, ScanRequest? Request, Dictionary<string,double> Timings, ProgressSnapshot? Progress);
internal sealed record DirectRescanSnapshot(string Status, string? Reason, double TimeoutMs, double? StartApiMs, ProgressSnapshot? Progress);
internal sealed record SessionSnapshot(ExternalState State, bool Busy, bool NeedsStop, long Generation, string? LastError,
    Dictionary<string,double> Timings, bool? InitializeResult, bool? Ao2Result, bool? StartResult, FrequencyParams? Frequency,
    ExternalParameters? Parameters, bool ParametersSent, ExternalCaptureSnapshot? Acquisition, ScanRequest? Requested,
    bool ScanOnceActive=false, bool ScanOnceWaiting=false, bool CanDirectRescan=false, ScanOnceSnapshot? ScanOnce=null, DirectRescanSnapshot? DirectRescan=null, bool CanReinitNoPrepare=false, ReinitNoPrepareSnapshot? ReinitNoPrepare=null, bool CanReinitSetParameters=false, ReinitSetParamSnapshot? ReinitSetParameters=null);

internal sealed partial class SdkSession
{
    private sealed record CaptureLease(long Generation, IAcquisitionCapture Capture);
    // A registration has a stable delegate. Direct Rescan replaces ONLY its managed destination.
    // Each callback snapshots its lease at entry: an in-flight old lease never becomes new data.
    private sealed class CallbackRoute { internal CaptureLease? Lease; internal long ErrorGeneration; }
    private sealed record CallbackRoots(HmmNative.ImageCallback Image, HmmNative.ErrorCallback Error);
    private static readonly List<CallbackRoots> roots=[];
    private readonly object gate=new();
    private readonly ConcurrentQueue<(long Generation,string Message)> errors=new();
    private readonly RollingDiagnosticLog log;
    private readonly IHmmApi api;
    internal SdkRuntimeProfile RuntimeProfile {get;}
    private FrameAverageOptions frameAverage=new();
    internal FrameAverageOptions FrameAverage {get {lock(gate) return frameAverage;}}
    internal void ConfigureFrameAverage(FrameAverageOptions value)
    {
        lock(gate)
        {
            int pixels=parameters is { } p?GeometryMath.Pixels(p.Wave.pixel_per_line,checked(p.OutputHeight+p.OutputDummy)):1;
            value.Validate(pixels);
            if(value.Enabled && (parameters?.Requested??requested) is { } memoryRequest) InspectMemory(memoryRequest,value.Enabled).RequirePass();
            capture?.Averaging.Configure(onceCapture?new():value);frameAverage=value;
        }
    }
    private readonly Func<ScanRequest,ExternalParameters> loadParameters;
    private readonly Func<TimeSpan,CancellationToken,Task> diagnosticDelay;
    private readonly Dictionary<string,double> timings=[];
    private ExternalState state=ExternalState.RequiresInitialize;
    private int busy;
    private bool needsStop, parametersSent, successfulStart, rescanEligible, onceActive, onceWaiting, onceCapture;
    private long generation,errorGeneration,onceBegin;
    private long sessionId, previousStopGeneration, directActiveGeneration;
    private string previousStopResult="Not attempted";
    private bool imageCallbackRegistered,errorCallbackRegistered;
    private void UpdateLogContext() {lock(gate) log.SetContext(sessionId,generation,state);}
    private void Critical(string operation,string message) {UpdateLogContext();log.Critical(operation,message);}
    private string? lastError;
    private bool? initializeResult,ao2Result,startResult;
    private FrequencyParams? frequency,preparedFrequency;
    private ExternalParameters? parameters;
    private ScanRequest? requested=ScanRequest.Default,preparedRequest,onceRequest;
    private IAcquisitionCapture? capture;
    private CallbackRoute? route;
    private ExternalCaptureSnapshot? archived;
    private AcquisitionProgress? progress,onceProgress,directProgress;
    private CancellationTokenSource? onceCancel,directCancel;
    private string? onceStatus,directStatus,directReason;
    private Dictionary<string,double> onceTimings=[];
    private double directTimeout;
    private double? directStartMs;
    private CompletedFrame?[]? retainedOnce;

    internal SdkSession(RollingDiagnosticLog log,IHmmApi? api=null,Func<ScanRequest,ExternalParameters>? loader=null,
        Func<TimeSpan,CancellationToken,Task>? diagnosticDelay=null, SdkRuntimeProfile? offlineProfile=null)
    { this.log=log; this.api=api??new NativeHmmApi(); loadParameters=loader??ExternalParameters.LoadRequested; this.diagnosticDelay=diagnosticDelay??DelayDiagnostic;
        if(offlineProfile is not null && this.api is NativeHmmApi) throw new InvalidOperationException("Native profile overrides are forbidden.");
        RuntimeProfile=offlineProfile??SdkRuntimeProfile.Inspect(Path.Combine(AppContext.BaseDirectory,"HMMSysCoreAPILib.dll")); }
    private static async Task DelayDiagnostic(TimeSpan duration,CancellationToken token)
    {
        // Task.Delay has a per-call maximum; preserve very slow valid scan timeouts without truncation.
        long began=Stopwatch.GetTimestamp();
        while(duration-Stopwatch.GetElapsedTime(began) is var remaining && remaining>TimeSpan.Zero)
            await Task.Delay(remaining>TimeSpan.FromDays(1)?TimeSpan.FromDays(1):remaining,token).ConfigureAwait(false);
    }
    internal IAcquisitionCapture? CurrentCapture=>Volatile.Read(ref capture);
    internal (long Generation,IAcquisitionCapture? Capture) DisplayTarget()
    { lock(gate) return (generation,state==ExternalState.Scanning && (Volatile.Read(ref busy)==0 || onceWaiting)?capture:null); }
    internal CompletedFrame?[]? TakeRetainedOnce() { lock(gate) { var frames=retainedOnce; retainedOnce=null; return frames; } }
    internal void RetainedOnceDisplayed(CompletedFrame frame)
    {
        lock(gate)
        {
            if(archived is null || archived.Generation!=frame.SessionGeneration || onceProgress?.Generation!=frame.SessionGeneration) return;
            var channels=(ExternalChannelSnapshot[])archived.Channels.Clone();
            var c=channels[frame.Channel];
            channels[frame.Channel]=c with {DisplayedUpdates=c.DisplayedUpdates+1,FirstDisplayUpdateMs=c.FirstDisplayUpdateMs??onceProgress.ElapsedMs(Stopwatch.GetTimestamp()),
                DisplayStatus="PROCESSED FRAME - includes Dummy/Overscan · Scan Once complete frame retained"};
            archived=archived with {Channels=channels,FirstDisplayUpdateMs=channels.Min(x=>x.FirstDisplayUpdateMs)};
        }
    }
    private bool CanRescan()=>rescanEligible && !needsStop && state==ExternalState.RequiresInitialize && parameters is not null && route is not null;
    internal SessionSnapshot Snapshot()
    {
        DrainErrors();
        lock(gate)
        {
        var acquisition=capture?.Snapshot()??archived;
        return new(state,Volatile.Read(ref busy)!=0,needsStop,generation,lastError,new(timings),initializeResult,ao2Result,startResult,
            frequency,parameters,parametersSent,acquisition,requested,onceActive,onceWaiting,CanRescan()&&busy==0,
            onceStatus is null?null:new(onceStatus,onceRequest,new(onceActive?timings:onceTimings),onceProgress?.Snapshot()),
            directStatus is null?null:new(directStatus,directReason,directTimeout,directStartMs,directProgress?.Snapshot()),
            CanReinit()&&busy==0,ReinitSnapshot(acquisition),CanSetParamExperiment()&&busy==0,SetParamSnapshot(acquisition));
        }
    }
    internal static bool CanEdit(ExternalState value,bool isBusy)=>!isBusy && value is
        ExternalState.RequiresInitialize or ExternalState.InitializeFailed or ExternalState.Initialized or ExternalState.Prepared or ExternalState.PrepareRequired;
    internal bool SetRequestedParameters(ScanRequest? value)
    {
        value?.Validate();
        lock(gate)
        {
            if(!CanEdit(state,busy!=0)) return false;
            if(requested==value) return true;
            requested=value;
            if(state==ExternalState.Prepared) { state=ExternalState.PrepareRequired; parametersSent=false; }
            frequency=null;
            return true;
        }
    }
    private void InstallReceiver(ExternalParameters settings,CallbackRoute target)
    {
        AdmitReceiver(settings); // Before any managed image buffers or hardware API. Also covers Initialize / Direct Start.
        long session=Interlocked.Increment(ref generation); UpdateLogContext();
        var tracking=new AcquisitionProgress(session,settings.Wave);
        if(!onceCapture) FrameAverage.Validate(GeometryMath.Pixels(settings.Wave.pixel_per_line,checked(settings.OutputHeight+settings.OutputDummy)));
        IAcquisitionCapture current=settings.Wave.line_mode?new InternalCapture(session,settings,log,tracking,onceCapture):new ExternalCapture(session,settings,log,tracking);
        current.Averaging.Configure(onceCapture?new():FrameAverage);
        lock(gate) { parameters=settings; capture=current; archived=null; progress=tracking; }
        Interlocked.Exchange(ref errorGeneration,session);
        Interlocked.Exchange(ref target.ErrorGeneration,session);
        Volatile.Write(ref target.Lease,new(session,current));
    }
    // Normal Initialize / Prepare and Reinit-No-Prepare plumbing. Direct Rescan must not call this factory.
    private CallbackRoots CreateCapture(ExternalParameters settings)
    {
        if(route is not null) { Volatile.Write(ref route.Lease,null); Interlocked.Exchange(ref route.ErrorGeneration,0); }
        var target=new CallbackRoute(); route=target; imageCallbackRegistered=false; errorCallbackRegistered=false;
        InstallReceiver(settings,target);
        var callbacks=new CallbackRoots(p=>
        {
            var lease=Volatile.Read(ref target.Lease);
            if(lease is null) return;
            try { lease.Capture.Receive(p,lease.Generation,Interlocked.Read(ref generation)); }
            catch(Exception e) { Error(lease.Generation,"Image callback: "+e.Message); }
        },p=> { long errorSession=Interlocked.Read(ref target.ErrorGeneration); if(errorSession!=0) CopyError(p,errorSession); });
        lock(roots) roots.Add(callbacks);
        return callbacks;
    }
    private void RegisterCallbacks(CallbackRoots callbacks)
    {
        Timed("setErrorMessageCallback",()=>api.SetErrorCallback(callbacks.Error)); errorCallbackRegistered=true;
        Timed("setResampledImageCallback",()=>api.SetImageCallback(callbacks.Image)); imageCallbackRegistered=true;
        log.Event(generation,"Callback registration: stable route with generation-specific managed receiver");
    }
    private void DrainErrors()
    {
        while(errors.TryDequeue(out var error))
            if(error.Generation==Interlocked.Read(ref errorGeneration)) lock(gate) lastError=error.Message;
    }
    private void State(ExternalState value) { lock(gate) state=value; UpdateLogContext(); }
    private void RequireNoSdkError()
    { DrainErrors(); lock(gate) if(lastError is not null) throw new InvalidOperationException("SDK error; Start blocked: "+lastError); }
    private void Time(string name,double value) { lock(gate) timings[name]=value; }
    private static string ApiName(string name)=>name switch
    {
        "Initialize API Time" or "Reinitialize API Time"=>"initialize", "Set AUX API Time"=>"setAux", "Set AO2 API Time"=>"setAo2Value",
        "ADC Calibration API Time"=>"adcCalibration", "Get Frequency API Time"=>"getFrequency", "Set Parameters API Time" or "Experiment SetParameters API Time"=>"setParameters", "Stop Scan API Time"=>"stopScan", _=>name
    };
    private T Timed<T>(string name,Func<T> call,Func<string>? returnedDetail=null)
    {
        string operation=ApiName(name);
        Critical(operation,"BEGIN; BEFORE "+operation+"()");
        long before=Stopwatch.GetTimestamp();
        if(name=="Initialize API Time" && onceActive) Interlocked.Exchange(ref onceBegin,before);
        T result;
        try {result=call();}
        finally {Time(name,Stopwatch.GetElapsedTime(before).TotalMilliseconds);}
        // Reached only after an actual native return, never from a finally block.
        Critical(operation,"AFTER "+operation+"() result="+(returnedDetail?.Invoke()??result?.ToString()??"null")+"; END");
        return result;
    }
    private void Timed(string name,Action call,Func<string>? returnedDetail=null)=>Timed(name,()=> {call(); return "void (returned)";},returnedDetail);
    private Task Manual(Func<Task> operation,params ExternalState[] allowed)
    {
        if(Interlocked.CompareExchange(ref busy,1,0)!=0) return Task.CompletedTask;
        lock(gate) if(!allowed.Contains(state)) { Volatile.Write(ref busy,0); return Task.CompletedTask; }
        return Task.Run(async()=> {try {await operation().ConfigureAwait(false);} finally {Volatile.Write(ref busy,0);} });
    }
    private void Fail(ExternalState failed,Exception error)
    { lock(gate) {state=failed; lastError=error.Message;} UpdateLogContext(); log.Exception(failed.ToString(),error); }
    private void ResetDirectMonitor()
    { directCancel?.Cancel(); directCancel?.Dispose(); directCancel=null; }
    private void InitializeCore(ScanRequest selected)
    {
        lock(gate)
        {
            if(needsStop) throw new InvalidOperationException("Press Stop before another Initialize.");
            sessionId++; state=ExternalState.Initializing; lastError=null; timings.Clear(); initializeResult=null; ao2Result=null; startResult=null;
            frequency=null; preparedFrequency=null; parametersSent=false; preparedRequest=null; archived=null; retainedOnce=null;
            rescanEligible=false; successfulStart=false; directStatus=null; directProgress=null;
        }
        ResetReinitForNormalInitialize();ResetSetParamForNormalInitialize();
        directActiveGeneration=0; log.AbandonActiveRescan();
        Critical("Initialize","BEGIN");
        ResetDirectMonitor();
        if(!Diagnostics.Abi().All(x=>x.Passed) || !Diagnostics.MarshalingCheck()) throw new InvalidOperationException("ABI check failed.");
        var settings=loadParameters(selected);
        if(onceActive && !new AcquisitionProgress(0,settings.Wave).HasEnabledChannels) throw new InvalidOperationException("Scan Once requires at least one enabled image channel.");
        var callbacks=CreateCapture(settings);
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        Critical("NativeLoad","BEFORE ensure native module and exports available"); api.EnsureAvailable();
        Critical("NativeLoad","AFTER native module/export checks");
        lock(gate) needsStop=true;
        bool result=Timed("Initialize API Time",api.Initialize);
        lock(gate) initializeResult=result;
        if(!result) throw new InvalidOperationException("initialize() returned false. Stop is required for cleanup.");
        RegisterCallbacks(callbacks); State(ExternalState.Initialized); Critical("Initialize","END result="+result);
    }
    private async Task PrepareCore(ScanRequest selected)
    {
        State(ExternalState.Preparing); lock(gate) preparedParametersSent=null; Critical("Prepare","BEGIN"); long before=Stopwatch.GetTimestamp();
        try
        {
            var settings=loadParameters(selected); RequireNoSdkError(); ArchiveAndClear();
            // Release the idle receiver before admission; surviving worker/progress buffers remain accounted.
            var callbacks=CreateCapture(settings); RegisterCallbacks(callbacks);
            lock(gate) {parametersSent=false; preparedRequest=null; frequency=null; ao2Result=null;}
            if(!settings.Wave.line_mode)
            {
                bool ao2=Timed("Set AO2 API Time",()=>api.SetAo2Value(settings.Ao2Amplitude,settings.Ao2Offset));
                lock(gate) ao2Result=ao2;
                if(!ao2) throw new InvalidOperationException("setAo2Value() returned false; Start blocked.");
                RequireNoSdkError();
                Time("Resonant Wait Requested",1000); long wait=Stopwatch.GetTimestamp();
                await Task.Delay(1000).ConfigureAwait(false); Time("Resonant Wait Actual",Stopwatch.GetElapsedTime(wait).TotalMilliseconds);
                FrequencyParams measured=default; Timed("Get Frequency API Time",()=>api.GetFrequency(out measured),()=>FormattableString.Invariant($"void (returned); measured line_frequency_hz={measured.line_frequency_hz}, pos_time={measured.pos_time}, neg_time={measured.neg_time}"));
                lock(gate) frequency=measured;
                if(measured.pos_time<0 || measured.neg_time<0 || !float.IsFinite(measured.line_frequency_hz) || measured.line_frequency_hz<=0)
                    throw new InvalidOperationException("Invalid resonant timing (frequency / pos_time / neg_time); Start blocked.");
            }
            RequireNoSdkError();
            Timed("Set AUX API Time",()=>api.SetAux(settings.Aux)); RequireNoSdkError();
            var wave=settings.Wave;
            var exactSent=wave; // Retain the value at API entry, before a native ref parameter could be mutated.
            Critical("setParameters",$"Requested WaveParams before native call: external_line_trigger={wave.external_line_trigger}, line_mode={wave.line_mode}, number_of_lines={wave.number_of_lines}, line_frequency_hz={wave.line_frequency_hz}");
            Timed("Set Parameters API Time",()=>api.SetParameters(ref wave,settings.Iterations)); RequireNoSdkError();
            lock(gate) {preparedParametersSent=settings with {Wave=exactSent};parametersSent=true; preparedRequest=selected; preparedFrequency=frequency; state=ExternalState.Prepared;}
            Critical("Prepare","END");
        }
        finally {Time("Prepare Total Time",Stopwatch.GetElapsedTime(before).TotalMilliseconds);}
    }
    private void StartCore(bool direct=false)
    {
        State(ExternalState.Starting); Critical(direct?"DirectRescan":"Start","BEGIN");
        if(!direct)
        {
            RequireNoSdkError();
            lock(gate) if(!parametersSent || parameters is null || preparedRequest!=requested) throw new InvalidOperationException("Prepare is required before Start.");
        }
        var current=CurrentCapture??throw new InvalidOperationException("No receiver.");
        if(direct) log.RescanPhase("BeforeStartScan");
        Critical(direct?"DirectRescan":"Start","BEFORE startScan()");
        long began=current.ArmStart(); bool result;
        try {result=api.StartScan();}
        finally
        {
            double elapsed=Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            Time(direct?"Direct Rescan Start API Time":"Start Scan API Time",elapsed);
            if(direct) lock(gate) directStartMs=elapsed;
        }
        Critical(direct?"DirectRescan":"Start","AFTER startScan() result="+result);
        if(direct) log.RescanPhase("AfterStartScan");
        lock(gate) {startResult=result; successfulStart=result;normalPreparedStart=!direct&&result;}
        if(!result) throw new InvalidOperationException("startScan returned false");
        State(ExternalState.Scanning); Critical(direct?"DirectRescan":"Start","END result="+result);
    }
    private void ArchiveAndClear()
    {
        if(route is not null) Volatile.Write(ref route.Lease,null);
        Interlocked.Increment(ref generation); UpdateLogContext();
        var old=Interlocked.Exchange(ref capture,null);
        if(old is not null) {var report=old.FreezeAndSnapshot(); lock(gate) {archived=report;retiredMemoryOwners.Add(new(old));}}
        progress?.Retire();
    }
    private void StopCore()
    {
        State(ExternalState.Stopping); Critical("Stop","BEGIN"); ResetDirectMonitor(); CancelReinitMonitor(); CancelSetParamMonitor();
        previousStopGeneration=generation; previousStopResult="Not returned";
        lock(gate)
        {
            if(directStatus=="Waiting for data")
            {
                var p=directProgress?.Snapshot();
                directStatus=startResult==true && lastError is null && p is {HasNewCallback:true,HasNewFrame:true} && p.FirstCompleteMs.Any(x=>x is { } ms && ms<=directTimeout)?"PASS":"FAIL";
                if(directStatus=="FAIL") directReason="stopped before a new complete frame";
            }
        }
        ArchiveAndClear();
        FinishReinitOutcome(stopped:true);FinishSetParamOutcome(stopped:true);
        long returned=0;
        Timed("Stop Scan API Time",()=> {api.StopScan(); returned=Stopwatch.GetTimestamp();});
        previousStopResult="void (returned successfully)";
        if(onceActive && onceBegin!=0) Time("Total Scan Once Time",Stopwatch.GetElapsedTime(onceBegin,returned).TotalMilliseconds);
        DrainErrors(); Interlocked.Exchange(ref errorGeneration,0);
        lock(gate) {needsStop=false; state=ExternalState.RequiresInitialize; parametersSent=false; rescanEligible=successfulStart && lastError is null && progress?.Snapshot() is {HasNewCallback:true,HasNewFrame:true};}
        Critical("Stop","END result="+previousStopResult+"; SDK error="+(lastError??"None"));
        FlushWarnings(true);
        RememberSuccessfulAcquisitionAfterStop();RememberPreparedCycleAfterStop();
        if(lastError is null && setParamActiveGeneration!=0) {log.EndSetParam(setParamActiveGeneration);setParamActiveGeneration=0;}
        if(lastError is null && reinitActiveGeneration!=0) {log.EndReinit(reinitActiveGeneration);reinitActiveGeneration=0;}
        if(lastError is null && directActiveGeneration!=0) {log.EndRescan(directActiveGeneration);directActiveGeneration=0;}
    }
    internal Task InitializeAsync()=>Manual(()=>
    {try {InitializeCore(requested??throw new InvalidOperationException("Correct requested parameters."));} catch(Exception e) {Fail(ExternalState.InitializeFailed,e);} return Task.CompletedTask;},ExternalState.RequiresInitialize,ExternalState.InitializeFailed);
    internal Task PrepareAsync()=>Manual(async()=>
    {try {await PrepareCore(requested??throw new InvalidOperationException("Correct requested parameters."));} catch(Exception e) {Fail(ExternalState.PrepareFailed,e);}},ExternalState.Initialized,ExternalState.PrepareRequired);
    internal Task StartAsync()=>Manual(()=>
    {try {StartCore();} catch(Exception e) {ArchiveAndClear(); Fail(ExternalState.StartFailed,e);} return Task.CompletedTask;},ExternalState.Prepared);
    internal Task StopAsync()
    {
        lock(gate) if(onceActive && onceWaiting) {onceCancel?.Cancel(); return Task.CompletedTask;}
        return Manual(()=> {try {StopCore();} catch(Exception e) {Fail(ExternalState.StopFailed,e);} return Task.CompletedTask;},
            ExternalState.Initialized,ExternalState.Prepared,ExternalState.PrepareRequired,ExternalState.Scanning,ExternalState.InitializeFailed,ExternalState.PrepareFailed,ExternalState.StartFailed,ExternalState.StopFailed);
    }
    internal Task ScanOnceAsync()=>Manual(async()=>
    {
        ScanRequest selected;
        lock(gate)
        {
            if(needsStop || requested is not {InternalMode:true}) return;
            selected=requested; onceRequest=selected; onceActive=true; onceCapture=true; onceStatus="Running";
            onceProgress=null; onceTimings=[]; onceBegin=0; onceCancel=new();
        }
        bool complete=false;
        try
        {
            InitializeCore(selected); await PrepareCore(selected).ConfigureAwait(false);
            lock(gate) onceProgress=progress;
            StartCore(); lock(gate) onceWaiting=true;
            await onceProgress!.AllComplete.Task.WaitAsync(onceCancel.Token).ConfigureAwait(false);
            complete=true;
        }
        catch(OperationCanceledException) {lock(gate) onceStatus="Cancelled by Stop";}
        catch(Exception e) {lock(gate) {lastError=e.Message; onceStatus="FAIL: "+e.Message;} UpdateLogContext();log.Exception("ScanOnce",e);}
        finally
        {
            lock(gate) {onceWaiting=false; retainedOnce=onceProgress?.Frames();}
            try
            {
                if(needsStop) StopCore(); else State(ExternalState.RequiresInitialize);
                lock(gate) if(complete) onceStatus=lastError is null?"Complete":"FAIL: "+lastError;
            }
            catch(Exception e) {Fail(ExternalState.StopFailed,e); lock(gate) onceStatus="FAIL: Stop did not return successfully";}
            lock(gate) {onceTimings=new(timings); onceCapture=false; onceActive=false; onceCancel?.Dispose(); onceCancel=null;}
        }
    },ExternalState.RequiresInitialize,ExternalState.InitializeFailed);
    internal static double RescanTimeoutMs(double calculatedFrameMs)=>Math.Max(2000,2*calculatedFrameMs);
    internal Task DirectRescanAsync()
    {
        lock(gate) if(busy!=0 || !CanRescan()) return Task.CompletedTask; // Hard gate as well as disabled UI; no native call.
        Critical("DirectRescan","DIRECT RESCAN CLICKED");
        var previous=Snapshot();
        log.BeginRescan(new
        {
            PreviousSession=sessionId, PreviousGeneration=previous.Generation, PreviousAcquisitionGeneration=previous.Acquisition?.Generation,
            previous.State, PreviousStopGeneration=previousStopGeneration, PreviousStopResult=previousStopResult,
            Counts=previous.Acquisition?.Channels.Select(c=>new {Channel=c.Channel+1,c.Callbacks,c.CompletedFrames}),
            WaveParams=parameters?.Wave, Geometry=preparedRequest, TriggerMode=parameters?.Wave.external_line_trigger==true?"External":"Internal",
            LineMode=parameters?.Wave.line_mode, NumberOfLines=parameters?.Wave.number_of_lines, Frequency=parameters?.Wave.line_frequency_hz,
            CallbackDelegates=new {ImageRegistered=imageCallbackRegistered,ErrorRegistered=errorCallbackRegistered,RouteRetained=route is not null},
            Pipeline=new {ReceiverPresent=capture is not null,ActiveLease=route?.Lease is not null,ArchivedGeneration=archived?.Generation,
                previous.NeedsStop,previous.ParametersSent,previous.CanDirectRescan}
        });
        return Manual(()=>
    {
        try
        {
            ExternalParameters saved; CallbackRoute savedRoute;
            lock(gate)
            {
                if(!CanRescan()) return Task.CompletedTask;
                saved=parameters!; savedRoute=route!; rescanEligible=false; lastError=null; startResult=null; successfulStart=false;
                frequency=preparedFrequency; directStatus="Waiting for data"; directReason=null; directStartMs=null;
            }
            ResetDirectMonitor();
            var calculated=saved.Wave.line_mode?ScanTimingCalculator.Internal(saved.Wave):ScanTimingCalculator.External(saved.Wave,preparedFrequency?.line_frequency_hz??double.NaN);
            if(calculated is null) throw new InvalidOperationException("No prepared timing for Direct Rescan.");
            lock(gate) directTimeout=RescanTimeoutMs(calculated.FrameTimeMs);
            // No JSON, WaveParams rebuild, Initialize, configuration, or callback registration here.
            // Use the exact retained parameters and the same delegate route; replace managed buffers only.
            Critical("DirectRescan","BEFORE new acquisition generation");
            onceCapture=false; InstallReceiver(saved,savedRoute);
            directActiveGeneration=generation; log.BindRescanGeneration(generation);
            Critical("DirectRescan","generation="+generation);
            lock(gate) {directProgress=progress; needsStop=true;}
            StartCore(direct:true);
            directCancel=new();
            _=MonitorDirectAsync(directProgress!,directTimeout,directCancel.Token);
        }
        catch(Exception e)
        {
            lock(gate) {directStatus="FAIL"; directReason=e.Message;}
            ArchiveAndClear(); Fail(needsStop?ExternalState.StartFailed:ExternalState.RequiresInitialize,e);
        }
        return Task.CompletedTask;
    },ExternalState.RequiresInitialize);
    }
    private async Task MonitorDirectAsync(AcquisitionProgress tracking,double timeout,CancellationToken token)
    {
        try
        {
            // Deadline starts at the startScan epoch, including API latency.
            double remaining=Math.Max(0,timeout-(directStartMs??0));
            var expired=diagnosticDelay(TimeSpan.FromMilliseconds(remaining),token);
            await Task.WhenAny(tracking.AnyComplete.Task,expired).ConfigureAwait(false);
            if(token.IsCancellationRequested) return;
            var p=tracking.Snapshot();
            lock(gate)
            {
                if(directProgress!=tracking || directStatus!="Waiting for data") return;
                bool within=p.FirstCompleteMs.Where(x=>x.HasValue).Any(x=>x!.Value<=timeout);
                if(startResult==true && lastError is null && p.HasNewCallback && p.HasNewFrame && within) {directStatus="PASS"; directReason=null;}
                else {directStatus="FAIL"; directReason=p.HasNewCallback?"no complete frame within diagnostic timeout":"no acquisition callback";}
            }
            // Diagnostic outcome only. Never Stop or fallback Initialize.
        }
        catch(OperationCanceledException) { }
    }
    private void Error(long session,string message)
    {
        if(session!=Interlocked.Read(ref errorGeneration)) return;
        Interlocked.Increment(ref fatalErrorSequence);
        errors.Enqueue((session,message)); while(errors.Count>128) errors.TryDequeue(out _);
        if(directActiveGeneration==session) lock(gate) {directStatus="FAIL";directReason="SDK error: "+message;}
        if(onceActive) progress?.Fault(message);
        if(reinitActiveGeneration==session) reinitProgress?.Fault(message);
        if(setParamActiveGeneration==session) setParamProgress?.Fault(message);
        log.Event(session,"SDK error: "+message);
    }
    private void CopyError(IntPtr pointer,long session)
    {
        try
        {
            if(session!=Interlocked.Read(ref errorGeneration) || pointer==IntPtr.Zero) return;
            var error=Marshal.PtrToStructure<ErrorMessage>(pointer);
            if(error.error_message==IntPtr.Zero || error.message_size<0 || error.message_size>65536) {Error(session,"Invalid error metadata"); return;}
            var bytes=new byte[error.message_size]; Marshal.Copy(error.error_message,bytes,0,bytes.Length);
            string message=Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            if(SdkWarnings.IsAdcOverRange(message))
            {
                if(session==Interlocked.Read(ref errorGeneration)) warnings.Add(message,Interlocked.Read(ref sessionId),session,log);
            }
            else Error(session,message);
        }
        catch(Exception e) {Error(session,"Error callback copy: "+e.Message);}
    }
}
