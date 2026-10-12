using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HmmExample.Acquisition;
using HmmExample.Display;

namespace HmmExample;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer timer, previewTimer;
    private readonly RollingDiagnosticLog log;
    private readonly SdkSession session;
    private readonly bool deploymentReady;
    private ExternalParameters? baseline, preview, lastExternalParameters;
    private ScanTiming? externalTiming;
    private float lastExternalFrequency;
    private int previewHz=30, previewMaxDimension=2048, effectivePreviewHz=30;
    private bool closing, allowClose, updatingControls;
    private string? uiError, requestError;
    private readonly CompositeDisplay composite;
    private FramePipeline? displayPipeline; // Explicit offline fixture only.
    internal ChannelViewport[] Viewports => [Channel1,Channel2,Channel3,Channel4];
    private bool InternalSelected => TriggerMode.SelectedIndex==1;
    public MainWindow() : this(null,null) { }
    internal MainWindow(SdkSession? fixtureSession,RollingDiagnosticLog? fixtureLog)
    {
        InitializeComponent();
        composite=new CompositeDisplay(Dispatcher);
        InitializeCompositeControls();
        foreach(var view in Viewports) view.RawCaption="PROCESSED FRAME - includes Dummy/Overscan";
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        log=fixtureLog??new RollingDiagnosticLog(); session=fixtureSession??new SdkSession(log);

        try
        {
            var deployment=Diagnostics.Deployment(AppContext.BaseDirectory);
            deploymentReady=deployment.Passed && Diagnostics.Abi().All(x=>x.Passed) && Diagnostics.MarshalingCheck();
            if(deploymentReady)
            {
                baseline=ExternalParameters.LoadBaseline();
                XPixel.Text=baseline.Wave.pixel_per_line.ToString(); YPixel.Text=baseline.Wave.line_per_frame.ToString();
                OversamplePixel.Text=baseline.Wave.oversample_head.ToString(); OversampleFrame.Text=baseline.Wave.oversample_frame.ToString();
            }
            else uiError="Deployment / ABI verification failed.";
        }
        catch(Exception error) { uiError=error.Message; }
        timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(200) };
        timer.Tick+=(_,_)=>RefreshSession();
        previewTimer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(1.0/previewHz) };
        previewTimer.Tick+=(_,_)=>RefreshPreview();
        PreviewDimension.SelectionChanged+=(_,_)=> {previewMaxDimension=PreviewDimension.SelectedIndex==0?1024:2048;composite.ResetSources();RefreshSession();};
        TriggerMode.SelectionChanged+=(_,_)=>RequestedControlsChanged();
        LinesPerCallback.SelectionChanged+=(_,_)=>RequestedControlsChanged();
        foreach(var field in new[] { XPixel,YPixel,OversamplePixel,OversampleFrame,AverageLines }) field.TextChanged+=(_,_)=>RequestedControlsChanged();
        SpeedSlider.ValueChanged+=(_,_)=>RequestedControlsChanged();
        PreviewRate.SelectionChanged+=(_,_)=> { previewHz=new[] {5,15,30,60}[PreviewRate.SelectedIndex]; RefreshSession(); };
        LineAverageEnabled.Checked+=(_,_)=>RequestedControlsChanged();LineAverageEnabled.Unchecked+=(_,_)=>RequestedControlsChanged();
        FrameAverageEnabled.Checked+=(_,_)=>FrameAverageChanged();FrameAverageEnabled.Unchecked+=(_,_)=>FrameAverageChanged();
        AverageFrames.TextChanged+=(_,_)=>FrameAverageChanged();
        RequestedControlsChanged();
        timer.Start(); previewTimer.Start();
        Closing+=OnClosing; Closed+=(_,_)=> { timer.Stop(); previewTimer.Stop(); };
        log.Event(0,"Integrated UI launch; managed checks only; manual hardware actions required.");
    }
    private void InitializeCompositeControls()
    {
        var include=new[]{CompositeInclude1,CompositeInclude2,CompositeInclude3,CompositeInclude4};
        var colors=new[]{CompositeColor1,CompositeColor2,CompositeColor3,CompositeColor4};
        CompositeEnabled.Checked+=(_,_)=>composite.SetEnabled(true);
        CompositeEnabled.Unchecked+=(_,_)=>composite.SetEnabled(false);
        for(int i=0;i<4;i++)
        {
            int channel=i;
            colors[i].ItemsSource=RgbColor.Presets;colors[i].SelectedIndex=i;
            void Changed() {if(colors[channel].SelectedItem is RgbColor color) composite.SetChannel(channel,include[channel].IsChecked==true,color);}
            include[i].Checked+=(_,_)=>Changed();include[i].Unchecked+=(_,_)=>Changed();
            colors[i].SelectionChanged+=(_,_)=>Changed();
        }
    }
    private void RefreshComposite()
    {
        if(!composite.Refresh()) return;
        if(composite.Image is { } image) CompositeViewport.Present(image,true);
        else CompositeViewport.ShowStatus(composite.Status);
    }
    private void PresentComplete(CompletedFrame frame,FrameGeometry geometry)
    {
        var image=PreviewRenderer.Complete(frame,geometry,previewMaxDimension);
        Viewports[frame.Channel].Present(FrameRenderer.RenderGray(image.Width,image.Height,image.Pixels),geometry.DummyPolicy==DummyPolicy.RawIncludesOverscan);
        composite.Submit(frame.Channel,new(image.Width,image.Height,image.Pixels,frame.SessionGeneration,frame.FrameSequence,DisplaySource.Complete));
    }
    private long displayAverageGeneration=-1,displayAverageRevision=-1;
    private void FrameAverageChanged()
    {
        try
        {
            if(!int.TryParse(AverageFrames.Text,out int count)) throw new ArgumentException("Average Frames must be a positive integer.");
            var value=new FrameAverageOptions(FrameAverageEnabled.IsChecked==true,count);
            if(preview is { } p) value.Validate(GeometryMath.Pixels(p.Wave.pixel_per_line,checked(p.OutputHeight+p.OutputDummy)));
            session.ConfigureFrameAverage(value);uiError=null;
        }
        catch(Exception error) {uiError=error.Message;}
        RefreshSession();
    }
    private ScanRequest ReadRequest()
    {
        int Read(TextBox box,string label)
        {
            if(!int.TryParse(box.Text.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out int value))
                throw new ArgumentException(label+" must be an integer.");
            return value;
        }
        var value=new ScanRequest(InternalSelected,Read(XPixel,"X Pixel"),Read(YPixel,"Y Pixel"),Read(OversamplePixel,"Oversample Pixel"),
            Read(OversampleFrame,"Oversample Frame"),LinesPerCallback.SelectedIndex==0?16:32,(int)SpeedSlider.Value,LineAverageEnabled.IsChecked==true,InternalSelected?Read(AverageLines,"Average Lines"):session.Snapshot().Requested?.AverageLines??3);
        value.Validate(); return value;
    }
    private void RequestedControlsChanged()
    {
        if(updatingControls) return;
        var s=session.Snapshot();
        if(!SdkSession.CanEdit(s.State,s.Busy)) { RestoreRequestedControls(s.Requested); return; }
        updatingControls=true;
        try
        {
            SpeedSlider.Value=Math.Clamp(Math.Round(SpeedSlider.Value),0,7);
            if(!InternalSelected) LineAverageEnabled.IsChecked=false;
        }
        finally { updatingControls=false; }
        ScanRequest? value=null; requestError=null;
        try { value=ReadRequest(); }
        catch(Exception e) { requestError=e.Message; }
        if(session.SetRequestedParameters(value)) preview=value is null ? null : baseline?.Apply(value);
        RefreshSession(); // Pure calculations immediately; no logging, SDK calls or parameter writes.
    }
    private void RestoreRequestedControls(ScanRequest? request)
    {
        if(request is null) return;
        updatingControls=true;
        try
        {
            TriggerMode.SelectedIndex=request.InternalMode?1:0; XPixel.Text=request.XPixel.ToString(); YPixel.Text=request.YPixel.ToString();
            OversamplePixel.Text=request.OversamplePixel.ToString(); OversampleFrame.Text=request.OversampleFrame.ToString();
            LinesPerCallback.SelectedIndex=request.NumberOfLines==16?0:1; SpeedSlider.Value=request.SpeedPosition;
            LineAverageEnabled.IsChecked=request.LineAverage;AverageLines.Text=request.AverageLines.ToString();
        }
        finally { updatingControls=false; }
    }
    private static string Ms(double? value)=>value is double number?$"{number:F3} ms":"—";
    private static bool ShowingSavedSession(SessionSnapshot s)=>s.NeedsStop && s.ReinitSetParameters?.Source is not null
        && s.ReinitSetParameters.Progress?.Generation==s.Acquisition?.Generation;
    private void RefreshCalculatedTiming(SessionSnapshot s)
    {
        if(ShowingSavedSession(s))
        {
            var source=s.ReinitSetParameters!.Source!;var calculated=ScanTimingCalculator.Internal(source.Parameters.Wave);
            InternalTimingPanel.Visibility=Visibility.Visible;ExternalTimingPanel.Visibility=Visibility.Collapsed;
            RequestedFrequency.Text=$"{source.Parameters.Wave.line_frequency_hz} Hz (saved)";
            InternalDwell.Text=$"{calculated.DwellUs:F3} us";InternalFrameTime.Text=$"{calculated.FrameTimeMs:F3} ms";InternalFrameRate.Text=$"{calculated.FrameRateHz:F3} Hz";
            return;
        }
        InternalTimingPanel.Visibility=InternalSelected?Visibility.Visible:Visibility.Collapsed;
        ExternalTimingPanel.Visibility=InternalSelected?Visibility.Collapsed:Visibility.Visible;
        RequestedFrequency.Text=$"{ScanRequest.FrequencyAt((int)SpeedSlider.Value)} Hz";
        var timing=preview is not null && InternalSelected ? ScanTimingCalculator.Internal(preview.Wave) : null;
        InternalDwell.Text=timing is null?"—":$"{timing.DwellUs:F3} us";
        InternalFrameTime.Text=timing is null?"—":$"{timing.FrameTimeMs:F3} ms";
        InternalFrameRate.Text=timing is null?"—":$"{timing.FrameRateHz:F3} Hz";
        bool measuredValid=!InternalSelected && s.ParametersSent && s.Parameters is { Wave.line_mode:false }
            && s.Frequency is {pos_time:>=0,neg_time:>=0} f && float.IsFinite(f.line_frequency_hz) && f.line_frequency_hz>0;
        if(measuredValid && s.Parameters is { } parameters && s.Frequency is { } frequency)
        {
            if(lastExternalParameters!=parameters || lastExternalFrequency!=frequency.line_frequency_hz)
            {
                externalTiming=ScanTimingCalculator.External(parameters.Wave,frequency.line_frequency_hz);
                lastExternalParameters=parameters; lastExternalFrequency=frequency.line_frequency_hz;
            }
            MeasuredFrequency.Text=$"{frequency.line_frequency_hz:F3} Hz";
            ResonantPhaseTimes.Text=$"Pos Time: {frequency.pos_time}     Neg Time: {frequency.neg_time}";
        }
        else
        {
            externalTiming=null; lastExternalParameters=null;
            MeasuredFrequency.Text="—"; ResonantPhaseTimes.Text="Pos Time: —     Neg Time: —";
        }
        ExternalDwell.Text=externalTiming is null?"—":$"{externalTiming.DwellUs:F3} us";
        ExternalFrameTime.Text=externalTiming is null?"—":$"{externalTiming.FrameTimeMs:F3} ms";
        ExternalFrameRate.Text=externalTiming is null?"—":$"{externalTiming.FrameRateHz:F3} Hz";
    }
    private void RefreshSession()
    {
        session.FlushWarnings();
        var s=session.Snapshot();
        AdcCalibrationMenuItem.IsEnabled=!closing&&session.CanAdcCalibration();
        RefreshAdcDiagnostics(s);
        var previewRequest=s.NeedsStop?s.Parameters?.Requested:s.Requested;
        bool largePreview=previewRequest is not null && Math.Max(previewRequest.XPixel,(long)previewRequest.YPixel+previewRequest.OversampleFrame)>=4096;
        effectivePreviewHz=largePreview?Math.Min(previewHz,previewMaxDimension==1024?5:2):previewHz;
        previewTimer.Interval=TimeSpan.FromSeconds(1.0/effectivePreviewHz);
        PreviewRate.ToolTip=$"Requested {previewHz} Hz; effective timer ceiling {effectivePreviewHz} Hz. 4096 preview workload is capped; acquisition rate is unchanged.";
        session.RetainedDisplayBytes=checked(composite.RetainedBytes()+Viewports.Select(v=>v.Image.Source).OfType<System.Windows.Media.Imaging.BitmapSource>().Distinct().Sum(b=>checked((long)b.PixelWidth*b.PixelHeight*((b.Format.BitsPerPixel+7)/8))));
        string memoryText;bool memoryPass=false;
        try {var admission=s.Requested is null?null:session.InspectMemory(s.Requested);memoryPass=admission?.Passed==true;memoryText=admission?.Describe()??"Host Memory Budget: correct requested geometry first.";}
        catch(Exception e) {memoryText="Host Memory Admission BLOCK: "+e.Message;}
        bool valid=s.Requested is not null && memoryPass, edit=SdkSession.CanEdit(s.State,s.Busy)&&!closing;
        GeometryControls.IsEnabled=edit; SpeedSlider.IsEnabled=edit&&InternalSelected; LinesPerCallback.IsEnabled=edit&&InternalSelected;
        InitializeButton.IsEnabled=valid&&deploymentReady&&!closing&&!s.Busy&&!s.NeedsStop&&s.State is ExternalState.RequiresInitialize or ExternalState.InitializeFailed;
        ParametersButton.IsEnabled=valid&&!closing&&!s.Busy&&s.State is ExternalState.Initialized or ExternalState.PrepareRequired;
        StartButton.IsEnabled=valid&&!closing&&!s.Busy&&s.State==ExternalState.Prepared&&s.ParametersSent&&s.LastError is null;
        StopButton.IsEnabled=!closing&&s.NeedsStop&&(!s.Busy || s.ScanOnceWaiting);
        ScanOnceButton.Visibility=InternalSelected?Visibility.Visible:Visibility.Collapsed;
        ScanOnceButton.IsEnabled=valid&&InternalSelected&&deploymentReady&&!closing&&!s.Busy&&!s.NeedsStop&&s.State is ExternalState.RequiresInitialize or ExternalState.InitializeFailed;
        LineAverageControls.IsEnabled=edit&&InternalSelected;
        RescanButton.IsEnabled=!closing&&s.CanDirectRescan;
        DirectRescanStatus.Text="Tests Stop → Start without Initialize/Prepare"+
            (s.DirectRescan is null?"":$" · Direct Start Test: {s.DirectRescan.Status} {s.DirectRescan.Reason}");
        DirectStartWarning.Text=session.RuntimeProfile.Kind==SdkProfileKind.Baseline350
            ?"Direct Start: Known to crash on tested 3.5.0 DLL. Use for controlled SDK comparison only.":"";
        DirectStartWarning.Visibility=DirectStartWarning.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
        SdkProfileStatus.Text=$"Recognized Profile: {session.RuntimeProfile.Name} · SDK DLL SHA-256: {session.RuntimeProfile.Sha256}";
        PreviewRate.IsEnabled=!s.ScanOnceActive;
        ExternalValidation.Visibility=InternalSelected||ShowingSavedSession(s)?Visibility.Collapsed:Visibility.Visible;
        RequestValidation.Text=requestError??(s.State==ExternalState.PrepareRequired?"Requested parameters changed — Needs Prepare before Start":"");
        if(s.Requested?.CapabilityNotice is {Length:>0} notice) RequestValidation.Text+=(RequestValidation.Text.Length==0?"":"\n")+notice;
        RequestValidation.Visibility=RequestValidation.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
        string state=s.State switch {ExternalState.RequiresInitialize=>"Requires Initialize",ExternalState.InitializeFailed=>"Initialize Failed",ExternalState.PrepareRequired=>"Needs Prepare",_=>s.State.ToString()};
        ScanState.Text="State: "+state;
        SdkStatus.Text=$"SDK: {(deploymentReady?"Runtime compatibility checked":"Deployment / ABI error")} · Generation {s.Generation} · Initialize: {s.InitializeResult?.ToString()??"not called"}";
        LastError.Text="Last Error: "+(uiError??s.LastError??log.Failure??"None");
        var shown=s.NeedsStop?s.Parameters:preview;
        string requested="Requested geometry (not hardware readback):\n"+JsonSerializer.Serialize(s.Requested,Diagnostics.JsonOptions)+"\n\n";
        ActiveParameters.Text=requested+(shown?.Describe(s.NeedsStop&&s.ParametersSent,s.Requested?.FrequencyHz,s.State==ExternalState.PrepareRequired)??"No valid runtime parameters");
        if(s.ReinitNoPrepare is { } activeExperiment && s.NeedsStop && activeExperiment.Progress?.Generation==s.Acquisition?.Generation)
            ActiveParameters.Text="Reinit-No-Prepare: previous WaveParams/geometry are diagnostic references only; NOT sent in this experiment.\nSetParametersCalled=false; SetAuxCalled=false; PrepareCalled=false\n\n"+ActiveParameters.Text;
        if(ShowingSavedSession(s))
        {
            var saved=s.ReinitSetParameters!.Source!;
            ActiveParameters.Text="Reinit + SetParameters + Start: exact last successful acquisition snapshot. Current UI draft is NOT used.\nSetAuxCalled=false; PrepareCalled=false\n\n"+
                saved.Parameters.Describe(s.ParametersSent,saved.Parameters.Wave.line_frequency_hz);
        }
        ActiveParameters.Text+="\n\n"+memoryText;
        RefreshCalculatedTiming(s);
        var timingParameters=s.NeedsStop?s.Parameters:preview;
        ScanTiming? acquisitionTiming=timingParameters is null?null:timingParameters.Wave.line_mode?
            ScanTimingCalculator.Internal(timingParameters.Wave):ScanTimingCalculator.External(timingParameters.Wave,s.Frequency?.line_frequency_hz??double.NaN);
        double? acquisitionRate=acquisitionTiming?.FrameRateHz;
        ActiveParameters.Text+=$"\n\nFrame Average: {(session.FrameAverage.Enabled?"Non-overlapping batch":"OFF")} N={session.FrameAverage.Count}\nAcquisition Frame Rate: {acquisitionRate?.ToString("F6")??"—"} Hz\nAveraged Display Rate: {(acquisitionRate/(session.FrameAverage.Enabled?session.FrameAverage.Count:1))?.ToString("F6")??"—"} Hz (continuous batch output; theoretical, before UI refresh limiting)";
        var api=new StringBuilder("API call-to-return latency; separate from requested/calculated scan timing.\n\n");
        bool internalSession=s.Parameters?.Wave.line_mode??InternalSelected;
        foreach(string key in new[] {"Initialize API Time","Set AUX API Time","Set AO2 API Time","Resonant Wait Requested","Resonant Wait Actual","Get Frequency API Time","Set Parameters API Time","Start Scan API Time","Stop Scan API Time","ADC Calibration API Time"})
            api.AppendLine(key+": "+(internalSession&&key is "Set AO2 API Time" or "Resonant Wait Requested" or "Resonant Wait Actual" or "Get Frequency API Time"?"N/A":Ms(s.Timings.TryGetValue(key,out var t)?t:null)));
        api.AppendLine($"\nAO2 result: {(internalSession?"N/A":s.Ao2Result?.ToString()??"—")}\nStart result: {s.StartResult?.ToString()??"—"}\nSetParameters: {(s.ParametersSent?"void API returned; inspect Last Error":"not prepared / session stopped")}");
        if(!internalSession && s.Frequency is { } measured) api.AppendLine($"Measured Resonant Frequency: {measured.line_frequency_hz:F3} Hz\nPos Time: {measured.pos_time}\nNeg Time: {measured.neg_time}");
        api.AppendLine($"\nStart → First Callback: {Ms(s.Acquisition?.FirstAnyCallbackMs)}\nStart → First Complete Frame: {Ms(s.Acquisition?.FirstCompleteFrameMs)}\nStart → First Display Update: {Ms(s.Acquisition?.FirstDisplayUpdateMs)}");
        if(s.Acquisition is { } a) foreach(var c in a.Channels) api.AppendLine($"\nCH{c.Channel+1}: callback {Ms(c.FirstCallbackMs)}; complete {Ms(c.FirstCompleteFrameMs)}; display {Ms(c.FirstDisplayUpdateMs)}");
        api.AppendLine("\nEpoch precedes startScan call. First Display is UI bitmap assignment, not monitor presentation. Stopped metrics are archived.");
        if(s.ScanOnce is { } once)
        {
            api.AppendLine("\nSCAN ONCE — "+once.Status);
            foreach(string key in new[]{"Initialize API Time","Prepare Total Time","Set AUX API Time","Set Parameters API Time","Start Scan API Time","Stop Scan API Time","ADC Calibration API Time","Total Scan Once Time"})
                api.AppendLine(key+": "+Ms(once.Timings.TryGetValue(key,out var elapsed)?elapsed:null));
            AppendProgress(api,"Start",once.Progress);
        }
        if(s.DirectRescan is { } direct) {api.AppendLine($"\nDIRECT START TEST: {direct.Status} / {direct.Reason}");AppendProgress(api,"Direct Start",direct.Progress);}
        TimingDetails.Text=api.ToString();
        if(displayPipeline is not null) { RefreshFrames(); return; }
        var average=session.CurrentCapture?.Averaging.Snapshot();
        AverageStatus.Text=average?.Error??(session.FrameAverage.Enabled?$"Frame Average Batch: {session.FrameAverage.Count} frames":"Frame Average OFF");
        var counters=new StringBuilder($"Preview max edge {previewMaxDimension}; requested {previewHz} Hz / timer ceiling {effectivePreviewHz} Hz (actual rendering may be slower).\nDropped UI Updates/Frames are display backlog, not FPGA acquisition loss.\n\n");
        for(int i=0;i<4;i++)
        {
            var c=s.Acquisition?.Channels[i]; var m=c?.LastMetadata;
            if(average is not null) counters.AppendLine($"Frame Average CH{i+1}: batch accumulated frames {average.Collected[i]}/{average.Options.Count}; completed batches {average.CompletedBatches[i]}; averaged display lines {average.DisplayLines[i]}; {average.Error}");
            counters.AppendLine($"CH{i+1}\nCallbacks: {c?.Callbacks??0}\nReceived Line Blocks: {c?.Blocks??0}\nCompleted Frames: {c?.CompletedFrames??0}");
            if(internalSession) counters.AppendLine($"Current Frame: {c?.LiveAccumulatedLines??0} / {c?.AcquisitionRows??(preview?.Wave.line_per_frame??0)+(preview?.Wave.oversample_frame??0)} lines\nDisplayed Updates: {c?.DisplayedUpdates??0}\nDropped UI Updates: {c?.DroppedUiUpdates??0}");
            else counters.AppendLine($"Displayed Frames: {c?.DisplayedFrames??0}\nDropped UI Frames: {c?.DroppedUiFrames??0}");
            counters.AppendLine($"Assembler lines / write position: {c?.AccumulatedLines??0} / {c?.WritePosition??0}\nFrame Sequence: {c?.FrameSequence??0}\nSynchronized: {c?.Synchronized?.ToString()??"—"}\nRejected callbacks: {c?.RejectedCallbacks??0}\nDisplay rejected: {c?.DisplayBlockedFrames??0}");
            counters.AppendLine($"Last Width / Height / Size: {m?.Width} / {m?.Height} / {m?.ImageSize}\nindex0 lines / index0: {m?.Index0LineNumber} / {m?.Index0}\nindex1 lines / index1: {m?.Index1LineNumber} / {m?.Index1}\n{m?.Classification}\n{c?.DisplayStatus}\n");
        }
        var cd=composite.Snapshot();
        counters.AppendLine($"COMPOSITE\n{cd.Status}\nDisplayed Updates: {cd.DisplayedUpdates}\nDropped UI Updates: {cd.DroppedUiUpdates}\nRGB calculations: {cd.RgbCalculations}\nDropped = skipped included source versions; not acquisition loss.");
        ChannelDiagnostics.Text=counters.ToString();
        MetadataDetails.Text=log.StartupNotice+"\n\nLatest 100 metadata records\n\n"+JsonSerializer.Serialize(s.Acquisition?.RecentMetadata??[],Diagnostics.JsonOptions)+$"\n\nPersistent session log: {log.FilePath}\nLog events dropped: {log.DroppedEvents}";
    }
    private static void AppendProgress(StringBuilder text,string prefix,ProgressSnapshot? progress)
    {
        text.AppendLine(prefix+" → First Callback: "+Ms(progress?.FirstCallbackMs));
        for(int i=0;i<4;i++) text.AppendLine($"{prefix} → CH{i+1} First Complete: "+(progress is not null && !progress.EnabledChannels.Contains(i)?"Disabled":Ms(progress?.FirstCompleteMs[i])));
        text.AppendLine(prefix+" → All Enabled Channels First Complete: "+Ms(progress?.AllEnabledCompleteMs));
    }
    private void RefreshPreview()
    {
        if(closing) return;
        if(session.TakeRetainedOnce() is { } retained)
            foreach(var frame in retained)
                if(frame is not null)
                {
                    PresentComplete(frame,new(frame.Width,frame.Height,0,16,DummyPolicy.RawIncludesOverscan));
                    session.RetainedOnceDisplayed(frame);
                }
        var target=session.DisplayTarget();
        if(target.Capture is not { } current) { RefreshComposite(); return; }
        composite.BeginGeneration(target.Generation);
        var avg=current.Averaging.Snapshot();
        if(displayAverageGeneration!=target.Generation || displayAverageRevision!=avg.Revision)
        {
            displayAverageGeneration=target.Generation;displayAverageRevision=avg.Revision;
            composite.ResetSources();foreach(var viewport in Viewports) viewport.ShowStatus(avg.Options.Enabled?"Collecting frames 0/"+avg.Options.Count:"Awaiting processed image data");
        }
        for(int channel=0;channel<4;channel++) try
        {
            if(avg.Options.Enabled)
            {
                if(current.Averaging.TryTakeLive(channel,out var averaged,previewMaxDimension) && averaged is not null && averaged.SessionGeneration==target.Generation)
                {
                    byte[] pixels=FrameRenderer.NormalizeLive(averaged);
                    Viewports[channel].Present(FrameRenderer.RenderGray(averaged.Width,averaged.Height,pixels),true);
                    composite.Submit(channel,new(averaged.Width,averaged.Height,pixels,averaged.SessionGeneration,averaged.Version,DisplaySource.BatchAverage));
                    current.Displayed(channel);
                }
                else if(Viewports[channel].Image.Source is null) Viewports[channel].ShowStatus(avg.Error??$"Collecting frames {avg.Collected[channel]}/{avg.Options.Count}");
            }
            else if(current is InternalCapture live)
            {
                if(!live.TryTakeLive(channel,out var snapshot,previewMaxDimension)||snapshot is null||snapshot.SessionGeneration!=target.Generation) continue;
                byte[] pixels=FrameRenderer.NormalizeLive(snapshot);
                Viewports[channel].Present(FrameRenderer.RenderGray(snapshot.Width,snapshot.Height,pixels),true);
                composite.Submit(channel,new(snapshot.Width,snapshot.Height,pixels,snapshot.SessionGeneration,snapshot.Version,DisplaySource.Live));
                live.LiveDisplayed(channel,snapshot.Version);
            }
            else
            {
                if(!current.TryTake(channel,out var frame)||frame is null||frame.SessionGeneration!=target.Generation) continue;
                var wave=current.Parameters.Wave;
                PresentComplete(frame,new(wave.pixel_per_line,current.Parameters.OutputHeight,current.Parameters.OutputDummy,16,DummyPolicy.RawIncludesOverscan));
                current.Displayed(channel);
            }
        }
        catch(Exception e) { current.DisplayBlocked(channel,e.Message); }
        RefreshComposite();
    }
    private async Task Manual(Func<Task> operation)
    {
        if(closing) return; uiError=null;
        try { var task=operation(); RefreshSession(); await task; }
        catch(Exception error) { uiError=error.Message; try {log.Exception("UI.Manual",error);} catch(Exception loggingError) {System.Diagnostics.Debug.WriteLine(loggingError);} }
        RefreshSession(); RefreshPreview();
    }
    private void RefreshAdcDiagnostics(SessionSnapshot snapshot)
    {
        var warning=session.WarningSnapshot(); var calibration=session.CalibrationSnapshot();
        LastWarning.Text="Last Warning: "+(warning.LastWarning??"None")+(warning.Total==0?"":$" (total {warning.Total})");
        LastAdcCalibration.Text=$"Last ADC Calibration: {calibration.Status}"+
            (calibration.Timestamp is null?"":$" · {calibration.Timestamp:yyyy-MM-dd HH:mm:ss zzz} · {calibration.ApiTimeMs:F3} ms")+
            (calibration.Error is null?"":" · "+calibration.Error);
        AdcDiagnostics.Text="ADC over-range warning counters (application lifetime; SDK channel names are not UI channel mappings):\n"+
            JsonSerializer.Serialize(warning,Diagnostics.JsonOptions)+"\n\nADC Calibration:\n"+
            JsonSerializer.Serialize(calibration,Diagnostics.JsonOptions)+"\n\nCompleted-frame statistics (before display normalization; processed acquisition frames):\n"+
            JsonSerializer.Serialize(snapshot.Acquisition?.FrameStatistics,Diagnostics.JsonOptions);
    }
    internal async Task RequestAdcCalibrationAsync(Func<bool> confirm)
    {
        if(closing || !session.CanAdcCalibration() || !confirm()) return;
        await Manual(session.AdcCalibrationAsync);
    }
    private async void AdcCalibration_Click(object sender,RoutedEventArgs e)=>
        await RequestAdcCalibrationAsync(()=>new AdcCalibrationDialog {Owner=this}.ShowDialog()==true);
    private async void Initialize_Click(object sender,RoutedEventArgs e)=>await Manual(session.InitializeAsync);
    private async void Prepare_Click(object sender,RoutedEventArgs e)=>await Manual(session.PrepareAsync);
    private async void Start_Click(object sender,RoutedEventArgs e)=>await Manual(session.StartAsync);
    private async void Stop_Click(object sender,RoutedEventArgs e)=>await Manual(session.StopAsync);
    private async void ScanOnce_Click(object sender,RoutedEventArgs e)=>await Manual(session.ScanOnceAsync);
    private async void DirectStart_Click(object sender,RoutedEventArgs e)=>await Manual(session.DirectRescanAsync);
    private void Export_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var dialog=new Microsoft.Win32.SaveFileDialog {FileName=$"HMM-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",Filter="JSON|*.json"};
            if(dialog.ShowDialog(this)!=true) return;
            var s=session.Snapshot();
            File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(new {ReportedHardwareValidation=ExperimentalHardwareEvidence.InternalBaseline,ReportedHardwareResults=ExperimentalHardwareEvidence.Results,Exported=DateTimeOffset.UtcNow,StopwatchFrequency=System.Diagnostics.Stopwatch.Frequency,
                HostMemoryAdmission=session.LastMemoryAdmission,HostMemoryPreview=s.Requested is null?null:session.InspectMemory(s.Requested),Warnings=session.WarningSnapshot(),AdcCalibration=session.CalibrationSnapshot(),Snapshot=s,RuntimeProfile=session.RuntimeProfile,RuntimeCompatibility=Diagnostics.Deployment(AppContext.BaseDirectory),FrameAverage=session.FrameAverage,AverageDisplay=session.CurrentCapture?.Averaging.Snapshot(),Composite=composite.Snapshot(),RequestedTiming=InternalSelected&&preview is not null?ScanTimingCalculator.Internal(preview.Wave):null,
                ExternalCalculatedTiming=externalTiming,FrequencySemantics="Scanner full-cycle Hz; Uni-directional only",LivePreviewTargetHz=previewHz,
                PreviewMaxDimension=previewMaxDimension,EffectivePreviewTimerHz=effectivePreviewHz,PreviewPolicy="Display-only nearest-neighbour sampling before unchanged Auto Contrast; full acquisition/average source retained",DiagnosticRefreshHz=5,LogDirectory=log.DirectoryPath,LogEventsDropped=log.DroppedEvents},
                new JsonSerializerOptions {WriteIndented=true,IncludeFields=true,Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}}));
        }
        catch(Exception error) { uiError=error.Message; }
    }
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(allowClose) return; e.Cancel=true;
        var s=session.Snapshot();
        if(s.Busy||s.NeedsStop) { uiError=s.Busy?"Wait for the current API to return.":"Press Stop Scan for manual cleanup before closing."; RefreshSession(); return; }
        if(closing) return;
        closing=true; timer.Stop(); previewTimer.Stop(); session.FlushWarnings(true); await log.FinishAsync(); allowClose=true; Close();
    }
    internal void AttachDisplayPipeline(FramePipeline pipeline) { Dispatcher.VerifyAccess(); displayPipeline=pipeline; }
    internal void RefreshFrames()
    {
        Dispatcher.VerifyAccess();
        var pipeline=displayPipeline;
        if(pipeline is null) return;
        var text=new StringBuilder();
        for(int c=0;c<4;c++)
        {
            if(pipeline.TryRead(c,out var frame) && frame is not null && frame.SessionGeneration==pipeline.Generation)
            {
                try { PresentComplete(frame,pipeline.Geometry); }
                catch(Exception error) { LastError.Text="Last Error: "+error.Message; }
            }
            var d=pipeline.Assemblers[c].Snapshot();
            text.AppendLine($"CH{c+1} Offline line-mode fixture\nCallbacks: {d.Callbacks}; Complete: {d.CompletedFrames}\nAccumulated: {d.AccumulatedLines}\n");
        }
        ChannelDiagnostics.Text=text.ToString();
        RefreshComposite();
    }
}

