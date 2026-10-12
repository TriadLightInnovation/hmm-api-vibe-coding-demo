using System.Diagnostics;
using HmmExample.Acquisition;
using HmmExample.Interop;

namespace HmmExample;

internal sealed record SuccessfulAcquisitionSnapshot(long Session,long AcquisitionGeneration,ExternalParameters Parameters,
    ScanRequest Request,FrequencyParams? Frequency,double CalculatedFrameTimeMs,ExternalChannelSnapshot[] Channels);
internal sealed record ReinitSetParamSnapshot(string Status,string? Reason,int Level,bool FullPass,double TimeoutMs,
    bool? InitializeResult,bool SetParametersReturned,bool? StartResult,ProgressSnapshot? Progress,Dictionary<string,double> Timings,
    SuccessfulAcquisitionSnapshot? Source,bool SetParametersCalled,bool SetAuxCalled=false,bool PrepareCalled=false);

internal sealed partial class SdkSession
{
    // Value copy taken BEFORE the successful normal setParameters call, never its possibly mutated ref output.
    private ExternalParameters? preparedParametersSent;
    private SuccessfulAcquisitionSnapshot? lastSuccessfulAcquisition,setParamSource;
    private long setParamActiveGeneration,setParamBegin;
    private AcquisitionProgress? setParamProgress;
    private CancellationTokenSource? setParamCancel;
    private string? setParamStatus,setParamReason;
    private bool? setParamInitializeResult,setParamStartResult;
    private bool setParamCalled,setParamReturned;
    private double setParamTimeout;
    private double? setParamStartOffset;
    private readonly Dictionary<string,double> setParamTimings=[];
    private ExternalCaptureSnapshot? setParamArchived;
    private bool CanSetParamExperiment()=>!needsStop && lastError is null && state==ExternalState.RequiresInitialize
        && lastSuccessfulAcquisition is {Parameters.Wave.line_mode:true,Parameters.Wave.external_line_trigger:false};
    private void CancelSetParamMonitor() {setParamCancel?.Cancel();setParamCancel?.Dispose();setParamCancel=null;}
    private void ResetSetParamForNormalInitialize()
    {
        CancelSetParamMonitor();log.AbandonActiveSetParam();
        lock(gate) {preparedParametersSent=null;setParamActiveGeneration=0;}
        // Last successful acquisition remains immutable across failed attempts and UI edits.
    }
    private void RememberSuccessfulAcquisitionAfterStop()
    {
        lock(gate)
        {
            if(normalPreparedStart && successfulStart && lastError is null && preparedParametersSent is { } sent
                && preparedRequest is { } request && archived is { } stopped && progress?.Snapshot().AllEnabledCompleteMs is not null)
            {
                var calculated=sent.Wave.line_mode?ScanTimingCalculator.Internal(sent.Wave):ScanTimingCalculator.External(sent.Wave,preparedFrequency?.line_frequency_hz??double.NaN);
                if(calculated is not null) lastSuccessfulAcquisition=new(sessionId,stopped.Generation,sent,request,preparedFrequency,
                    calculated.FrameTimeMs,(ExternalChannelSnapshot[])stopped.Channels.Clone());
            }
            if(archived?.Generation==setParamProgress?.Generation) setParamArchived=archived;
        }
    }
    private ReinitSetParamSnapshot? SetParamSnapshot(ExternalCaptureSnapshot? current)
    {
        if(setParamStatus is null) return null;
        var p=setParamProgress?.Snapshot();
        var times=new Dictionary<string,double>(setParamTimings);
        var a=current?.Generation==p?.Generation?current:setParamArchived;
        void Add(string name,double? sinceStart) {if(setParamStartOffset is { } offset && sinceStart is { } ms) times[name]=offset+ms;}
        Add("SetParam Test → First Callback",p?.FirstCallbackMs);
        Add("SetParam Test → First Complete Frame",p?.FirstCompleteMs.Where(x=>x.HasValue).DefaultIfEmpty().Min());
        Add("SetParam Test → First Display",a?.FirstDisplayUpdateMs);
        Add("SetParam Test → All Enabled Complete",p?.AllEnabledCompleteMs);
        if(a is not null) foreach(var c in a.Channels)
        {
            Add($"SetParam Test → CH{c.Channel+1} First Callback",c.FirstCallbackMs);
            Add($"SetParam Test → CH{c.Channel+1} First Complete",c.FirstCompleteFrameMs);
        }
        int level=setParamInitializeResult!=true?0:!setParamReturned?1:setParamStartResult!=true?2:p?.HasNewCallback!=true?3:4;
        return new(setParamStatus,setParamReason,level,setParamStatus=="PASS",setParamTimeout,setParamInitializeResult,setParamReturned,setParamStartResult,p,times,setParamSource,setParamCalled);
    }
    private void SetParamTime(string name,double elapsed) {lock(gate) {setParamTimings[name]=elapsed;timings[name]=elapsed;}}
    internal Task ReinitSetParametersAsync()
    {
        SuccessfulAcquisitionSnapshot previous;
        lock(gate) {if(busy!=0 || !CanSetParamExperiment()) return Task.CompletedTask;previous=lastSuccessfulAcquisition!;}
        long clicked=Stopwatch.GetTimestamp();
        Critical("ReinitSetParamStart","EXPERIMENT REINIT_SETPARAM_START CLICKED");
        log.BeginSetParam(previous); // Snapshot captured from a successful normal acquisition, not the UI draft.
        return Manual(()=>
        {
            try {ReinitSetParametersCore(previous,clicked);}
            catch(Exception error)
            {
                lock(gate) {setParamStatus="FAIL";setParamReason??=error.Message;}
                ArchiveAndClear();Fail(setParamInitializeResult==true?ExternalState.StartFailed:ExternalState.InitializeFailed,error);
                Critical("ReinitSetParamStart","FAIL Reason="+setParamReason+"; no fallback/retry/automatic Stop");
            }
            return Task.CompletedTask;
        },ExternalState.RequiresInitialize);
    }
    private void ReinitSetParametersCore(SuccessfulAcquisitionSnapshot previous,long clicked)
    {
        CancelSetParamMonitor();CancelReinitMonitor();
        lock(gate)
        {
            setParamSource=previous;reinitEligibleCycle=null;normalPreparedStart=false;rescanEligible=false;
            setParamBegin=clicked;setParamTimings.Clear();setParamArchived=null;setParamProgress=null;setParamStartOffset=null;
            setParamInitializeResult=null;setParamStartResult=null;setParamCalled=false;setParamReturned=false;setParamStatus="Running";setParamReason=null;
            setParamTimeout=RescanTimeoutMs(previous.CalculatedFrameTimeMs);
            sessionId++;state=ExternalState.Initializing;lastError=null;initializeResult=null;startResult=null;successfulStart=false;
            ao2Result=null;frequency=null;parametersSent=false;timings.Clear();retainedOnce=null;onceCapture=false;
        }
        // New buffers/route are managed host plumbing, based on the prior geometry for comparison.
        // Do not call InitializeCore, PrepareCore, loadParameters or EnsureAvailable in this experiment.
        Critical("ReinitSetParamStart","BEFORE new acquisition generation");
        var callbacks=CreateCapture(previous.Parameters);
        lock(gate) {setParamActiveGeneration=generation;setParamProgress=progress;}
        log.BindSetParamGeneration(generation);
        Critical("ReinitSetParamStart","generation="+generation+"; SetParametersCalled=false; SetAuxCalled=false; PrepareCalled=false; SetAo2ValueCalled=false; GetFrequencyCalled=false; AdcCalibrationCalled=false");
        log.SetParamPhase("BeforeInitialize");
        lock(gate) needsStop=true; // Once entering native initialize, cleanup remains an explicit Stop action.
        bool initialized;
        try {initialized=Timed("Reinitialize API Time",api.Initialize);}
        finally {lock(gate) if(timings.TryGetValue("Reinitialize API Time",out double elapsed)) setParamTimings["Reinitialize API Time"]=elapsed;}
        lock(gate) {initializeResult=initialized;setParamInitializeResult=initialized;}
        log.SetParamPhase("AfterInitialize",initialized);
        if(!initialized) {setParamReason="Initialize failed";throw new InvalidOperationException(setParamReason);}

        Critical("ReinitSetParamStart","BEFORE callback registration");
        long registration=Stopwatch.GetTimestamp();
        try {RegisterCallbacks(callbacks);}
        finally {SetParamTime("Callback Registration Time",Stopwatch.GetElapsedTime(registration).TotalMilliseconds);}
        Critical("ReinitSetParamStart","AFTER callback registration; error and image callbacks returned; SetParametersCalled=false; SetAuxCalled=false; PrepareCalled=false");
        log.SetParamPhase("CallbacksRegistered");RequireNoSdkError();

        // WaveParams is a struct: copy the exact pre-call value from the successful normal session.
        // Current UI drafts, JSON and native ref outputs are not parameter sources.
        var wave=previous.Parameters.Wave;
        Critical("ReinitSetParamStart","EXPERIMENT REINIT_SETPARAM_START; SetAuxCalled=false; PrepareCalled=false; source="+
            System.Text.Json.JsonSerializer.Serialize(previous,RollingDiagnosticLog.Json));
        log.SetParamPhase("BeforeSetParameters");
        try {Timed("Experiment SetParameters API Time",()=> {lock(gate) setParamCalled=true;api.SetParameters(ref wave,previous.Parameters.Iterations);});}
        finally {lock(gate) if(timings.TryGetValue("Experiment SetParameters API Time",out double elapsed)) setParamTimings["Experiment SetParameters API Time"]=elapsed;}
        lock(gate) setParamReturned=true;
        log.SetParamPhase("AfterSetParameters",true);RequireNoSdkError();
        lock(gate) parametersSent=true;
        Critical("ReinitSetParamStart","SetParametersCalled=true; SetParametersReturned=true; SetAuxCalled=false; PrepareCalled=false");

        State(ExternalState.Starting);log.SetParamPhase("BeforeStartScan");
        Critical("ReinitSetParamStart","BEFORE startScan()");
        long began=CurrentCapture!.ArmStart();
        lock(gate) setParamStartOffset=Stopwatch.GetElapsedTime(setParamBegin,began).TotalMilliseconds;
        bool started;
        try {started=api.StartScan();}
        finally {SetParamTime("SetParam Start API Time",Stopwatch.GetElapsedTime(began).TotalMilliseconds);}
        Critical("ReinitSetParamStart","AFTER startScan() result="+started);
        log.SetParamPhase("AfterStartScan",started);
        lock(gate) {startResult=started;setParamStartResult=started;}
        if(!started) {setParamReason="Start returned false after SetParameters";throw new InvalidOperationException(setParamReason);}
        RequireNoSdkError();State(ExternalState.Scanning);
        lock(gate) setParamStatus="Waiting for data";
        setParamCancel=new();
        _=MonitorSetParamAsync(setParamProgress!,setParamCancel.Token);
    }
    private async Task MonitorSetParamAsync(AcquisitionProgress tracking,CancellationToken token)
    {
        try
        {
            double remaining=Math.Max(0,setParamTimeout-(tracking.ElapsedMs(Stopwatch.GetTimestamp())??0));
            var expired=diagnosticDelay(TimeSpan.FromMilliseconds(remaining),token);
            await Task.WhenAny(tracking.AllComplete.Task,expired).ConfigureAwait(false);
            if(token.IsCancellationRequested) return;
            if(tracking.AllComplete.Task.IsFaulted) _=tracking.AllComplete.Task.Exception;
            FinishSetParamOutcome(stopped:false,tracking);
        }
        catch(OperationCanceledException) { }
        catch(Exception error)
        {
            lock(gate) if(setParamProgress==tracking) {setParamStatus="FAIL";setParamReason="Diagnostic monitor: "+error.Message;}
            log.Exception("ReinitSetParamStart.Monitor",error);
        }
        // Never stop or reconfigure/retry. Display may continue after the diagnostic deadline.
    }
    private void FinishSetParamOutcome(bool stopped,AcquisitionProgress? expected=null)
    {
        DrainErrors();string? result=null;
        lock(gate)
        {
            if(setParamStatus!="Waiting for data" || setParamProgress is null || (expected is not null && expected!=setParamProgress)) return;
            var p=setParamProgress.Snapshot();
            if(lastError is not null) {setParamStatus="FAIL";setParamReason="SDK error: "+lastError;}
            else if(setParamInitializeResult==true && setParamReturned && setParamStartResult==true && p.HasNewCallback && p.AllEnabledCompleteMs is { } all && all<=setParamTimeout)
            {setParamStatus="PASS";setParamReason="Stop → Initialize → callback registration → SetParameters → Start resumes acquisition without SetAux.";}
            else if(!p.HasNewCallback) {setParamStatus="FAIL";setParamReason="Start accepted but no acquisition callback";}
            else {setParamStatus="PARTIAL";setParamReason=p.HasNewFrame?"New frame received, but not all enabled channels completed within timeout":"Callbacks resumed but frame assembly incomplete";}
            if(stopped && setParamStatus!="PASS") setParamReason+="; manually stopped";
            result=$"Result={setParamStatus}; Reason={setParamReason}; generation={p.Generation}; SetParametersCalled={setParamCalled}; SetParametersReturned={setParamReturned}; SetAuxCalled=false; PrepareCalled=false";
        }
        Critical("ReinitSetParamStart",result);
    }
}
