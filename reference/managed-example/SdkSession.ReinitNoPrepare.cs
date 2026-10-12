using System.Diagnostics;
using HmmExample.Acquisition;
using HmmExample.Interop;

namespace HmmExample;

internal sealed record ReinitNoPrepareSnapshot(string Status,string? Reason,int Level,bool FullPass,double TimeoutMs,
    bool? InitializeResult,bool? StartResult,ProgressSnapshot? Progress,Dictionary<string,double> Timings,
    object? PreviousSession,bool SetParametersCalled=false,bool SetAuxCalled=false,bool PrepareCalled=false);

internal sealed partial class SdkSession
{
    private sealed record PreparedCycle(long Session,long AcquisitionGeneration,ExternalParameters Parameters,ScanRequest Request,
        FrequencyParams? Frequency,double CalculatedFrameTimeMs,ExternalChannelSnapshot[] Channels,bool StopCompleted=true);
    private PreparedCycle? reinitEligibleCycle,reinitSource;
    private bool normalPreparedStart;
    private long reinitActiveGeneration,reinitBegin;
    private AcquisitionProgress? reinitProgress;
    private CancellationTokenSource? reinitCancel;
    private string? reinitStatus,reinitReason;
    private bool? reinitInitializeResult,reinitStartResult;
    private double reinitTimeout;
    private double? reinitStartOffset;
    private readonly Dictionary<string,double> reinitTimings=[];
    private ExternalCaptureSnapshot? reinitArchived;

    private bool CanReinit()=>!needsStop && state==ExternalState.RequiresInitialize && reinitEligibleCycle is { } previous
        && requested==previous.Request && new[]{previous.Parameters.Wave.channel0_enable,previous.Parameters.Wave.channel1_enable,
            previous.Parameters.Wave.channel2_enable,previous.Parameters.Wave.channel3_enable}.Any(x=>x);
    private void CancelReinitMonitor() {reinitCancel?.Cancel();reinitCancel?.Dispose();reinitCancel=null;}
    private void ResetReinitForNormalInitialize()
    {
        CancelReinitMonitor();log.AbandonActiveReinit();
        lock(gate) {reinitEligibleCycle=null;normalPreparedStart=false;reinitActiveGeneration=0;}
    }
    private void RememberPreparedCycleAfterStop()
    {
        lock(gate)
        {
            reinitEligibleCycle=null;
            if(normalPreparedStart && successfulStart && lastError is null && parameters is { } saved && preparedRequest is { } request && archived is { } stopped)
            {
                var calculated=saved.Wave.line_mode?ScanTimingCalculator.Internal(saved.Wave):ScanTimingCalculator.External(saved.Wave,preparedFrequency?.line_frequency_hz??double.NaN);
                if(calculated is not null) reinitEligibleCycle=new(sessionId,stopped.Generation,saved,request,preparedFrequency,calculated.FrameTimeMs,(ExternalChannelSnapshot[])stopped.Channels.Clone());
            }
            normalPreparedStart=false;
            if(archived?.Generation==reinitProgress?.Generation) reinitArchived=archived;
        }
    }
    private ReinitNoPrepareSnapshot? ReinitSnapshot(ExternalCaptureSnapshot? current)
    {
        if(reinitStatus is null) return null;
        var p=reinitProgress?.Snapshot();
        var times=new Dictionary<string,double>(reinitTimings);
        var a=current?.Generation==p?.Generation?current:reinitArchived;
        void Add(string name,double? sinceStart) {if(reinitStartOffset is { } offset && sinceStart is { } ms) times[name]=offset+ms;}
        Add("Reinit Test → First Callback",p?.FirstCallbackMs);
        Add("Reinit Test → First Complete Frame",p?.FirstCompleteMs.Where(x=>x.HasValue).DefaultIfEmpty().Min());
        Add("Reinit Test → First Display",a?.FirstDisplayUpdateMs);
        Add("Reinit Test → All Enabled Complete",p?.AllEnabledCompleteMs);
        if(a is not null) foreach(var c in a.Channels)
        {
            Add($"Reinit Test → CH{c.Channel+1} First Callback",c.FirstCallbackMs);
            Add($"Reinit Test → CH{c.Channel+1} First Complete",c.FirstCompleteFrameMs);
        }
        int level=reinitInitializeResult!=true?0:reinitStartResult!=true?1:p?.HasNewCallback!=true?2:p.HasNewFrame?4:3;
        return new(reinitStatus,reinitReason,level,reinitStatus=="PASS",reinitTimeout,reinitInitializeResult,reinitStartResult,p,times,reinitSource);
    }
    private void ReinitTime(string name,double elapsed) {lock(gate) {reinitTimings[name]=elapsed;timings[name]=elapsed;}}
    internal Task ReinitNoPrepareAsync()
    {
        PreparedCycle previous;
        lock(gate) {if(busy!=0 || !CanReinit()) return Task.CompletedTask;previous=reinitEligibleCycle!;}
        long clicked=Stopwatch.GetTimestamp();
        Critical("ReinitNoPrepare","EXPERIMENT REINIT_NO_PREPARE CLICKED");
        log.BeginReinit(previous); // Only the captured managed snapshot; no JSON reload or parameter send.
        return Manual(()=>
        {
            try {ReinitNoPrepareCore(previous,clicked);}
            catch(Exception error)
            {
                lock(gate) {reinitStatus="FAIL";reinitReason??=error.Message;}
                ArchiveAndClear();Fail(reinitInitializeResult==true?ExternalState.StartFailed:ExternalState.InitializeFailed,error);
                Critical("ReinitNoPrepare","FAIL Reason="+reinitReason+"; no fallback/retry/automatic Stop");
            }
            return Task.CompletedTask;
        },ExternalState.RequiresInitialize);
    }
    private void ReinitNoPrepareCore(PreparedCycle previous,long clicked)
    {
        CancelReinitMonitor();
        lock(gate)
        {
            reinitSource=previous;reinitEligibleCycle=null;normalPreparedStart=false;rescanEligible=false;
            reinitBegin=clicked;reinitTimings.Clear();reinitArchived=null;reinitProgress=null;reinitStartOffset=null;
            reinitInitializeResult=null;reinitStartResult=null;reinitStatus="Running";reinitReason=null;
            reinitTimeout=RescanTimeoutMs(previous.CalculatedFrameTimeMs);
            sessionId++;state=ExternalState.Initializing;lastError=null;initializeResult=null;startResult=null;successfulStart=false;
            ao2Result=null;frequency=null;parametersSent=false;timings.Clear();retainedOnce=null;onceCapture=false;
        }
        // New buffers/route are managed host plumbing, based on the prior geometry for comparison.
        // Do not call InitializeCore, PrepareCore, loadParameters or EnsureAvailable in this experiment.
        Critical("ReinitNoPrepare","BEFORE new acquisition generation");
        var callbacks=CreateCapture(previous.Parameters);
        lock(gate) {reinitActiveGeneration=generation;reinitProgress=progress;}
        log.BindReinitGeneration(generation);
        Critical("ReinitNoPrepare","generation="+generation+"; SetParametersCalled=false; SetAuxCalled=false; PrepareCalled=false; SetAo2ValueCalled=false; GetFrequencyCalled=false; AdcCalibrationCalled=false");
        log.ReinitPhase("BeforeInitialize");
        lock(gate) needsStop=true; // Once entering native initialize, cleanup remains an explicit Stop action.
        bool initialized;
        try {initialized=Timed("Reinitialize API Time",api.Initialize);}
        finally {lock(gate) if(timings.TryGetValue("Reinitialize API Time",out double elapsed)) reinitTimings["Reinitialize API Time"]=elapsed;}
        lock(gate) {initializeResult=initialized;reinitInitializeResult=initialized;}
        log.ReinitPhase("AfterInitialize",initialized);
        if(!initialized) {reinitReason="Initialize failed";throw new InvalidOperationException(reinitReason);}

        Critical("ReinitNoPrepare","BEFORE callback registration");
        long registration=Stopwatch.GetTimestamp();
        try {RegisterCallbacks(callbacks);}
        finally {ReinitTime("Callback Registration Time",Stopwatch.GetElapsedTime(registration).TotalMilliseconds);}
        Critical("ReinitNoPrepare","AFTER callback registration; error and image callbacks returned; SetParametersCalled=false; SetAuxCalled=false; PrepareCalled=false");
        log.ReinitPhase("CallbacksRegistered");RequireNoSdkError();

        State(ExternalState.Starting);log.ReinitPhase("BeforeStartScan");
        Critical("ReinitNoPrepare","BEFORE startScan()");
        long began=CurrentCapture!.ArmStart();
        lock(gate) reinitStartOffset=Stopwatch.GetElapsedTime(reinitBegin,began).TotalMilliseconds;
        bool started;
        try {started=api.StartScan();}
        finally {ReinitTime("Reinit Start API Time",Stopwatch.GetElapsedTime(began).TotalMilliseconds);}
        Critical("ReinitNoPrepare","AFTER startScan() result="+started);
        log.ReinitPhase("AfterStartScan",started);
        lock(gate) {startResult=started;reinitStartResult=started;}
        if(!started) {reinitReason="Start returned false without SetParameters";throw new InvalidOperationException(reinitReason);}
        RequireNoSdkError();State(ExternalState.Scanning);
        lock(gate) reinitStatus="Waiting for data";
        reinitCancel=new();
        _=MonitorReinitAsync(reinitProgress!,reinitCancel.Token);
    }
    private async Task MonitorReinitAsync(AcquisitionProgress tracking,CancellationToken token)
    {
        try
        {
            double remaining=Math.Max(0,reinitTimeout-(tracking.ElapsedMs(Stopwatch.GetTimestamp())??0));
            var expired=diagnosticDelay(TimeSpan.FromMilliseconds(remaining),token);
            await Task.WhenAny(tracking.AllComplete.Task,expired).ConfigureAwait(false);
            if(token.IsCancellationRequested) return;
            if(tracking.AllComplete.Task.IsFaulted) _=tracking.AllComplete.Task.Exception;
            FinishReinitOutcome(stopped:false,tracking);
        }
        catch(OperationCanceledException) { }
        catch(Exception error)
        {
            lock(gate) if(reinitProgress==tracking) {reinitStatus="FAIL";reinitReason="Diagnostic monitor: "+error.Message;}
            log.Exception("ReinitNoPrepare.Monitor",error);
        }
        // Never stop or reconfigure/retry. Display may continue after the diagnostic deadline.
    }
    private void FinishReinitOutcome(bool stopped,AcquisitionProgress? expected=null)
    {
        DrainErrors();string? result=null;
        lock(gate)
        {
            if(reinitStatus!="Waiting for data" || reinitProgress is null || (expected is not null && expected!=reinitProgress)) return;
            var p=reinitProgress.Snapshot();
            if(lastError is not null) {reinitStatus="FAIL";reinitReason="SDK error: "+lastError;}
            else if(reinitInitializeResult==true && reinitStartResult==true && p.HasNewCallback && p.AllEnabledCompleteMs is { } all && all<=reinitTimeout)
            {reinitStatus="PASS";reinitReason="Stop → Initialize → callback registration → Start works without re-sending parameters.";}
            else if(!p.HasNewCallback) {reinitStatus="FAIL";reinitReason="Start accepted but no acquisition callback";}
            else {reinitStatus="PARTIAL";reinitReason=p.HasNewFrame?"New frame received, but not all enabled channels completed within timeout":"Callbacks resumed but frame assembly incomplete";}
            if(stopped && reinitStatus!="PASS") reinitReason+="; manually stopped";
            result=$"Result={reinitStatus}; Reason={reinitReason}; generation={p.Generation}; SetParametersCalled=false; SetAuxCalled=false; PrepareCalled=false";
        }
        Critical("ReinitNoPrepare",result);
    }
}
