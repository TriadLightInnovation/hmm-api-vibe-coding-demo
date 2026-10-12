using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HmmExample.Acquisition;
using HmmExample.Interop;

namespace HmmExample;

// Only managed arithmetic, control edits and synthetic state. Never press hardware buttons.
internal static class IntegratedOfflineTests
{
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    internal static object Run(string imagePrefix)
    {
        // Fail closed if a fixture accidentally reaches interop. EnsureLibrary cannot replace this resolver.
        NativeLibrary.SetDllImportResolver(typeof(HmmNative).Assembly, (_,_,_) => throw new InvalidOperationException("Native SDK forbidden in offline tests."));
        CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
        var checks=new List<string>();
        void Check(bool pass,string name) { if(!pass) throw new InvalidOperationException(name); checks.Add(name); }
        void Near(double actual,double expected,double tolerance,string name)=>Check(Math.Abs(actual-expected)<=tolerance,name);
        Check(!Diagnostics.SdkLoaded(),"SDK unloaded before tests");
        var baseline=ExternalParameters.LoadBaseline();
        var request=ScanRequest.Default;
        var settings=baseline.Apply(request);
        int[] expected={800,400,200,100,50,20,10,5};
        for(int i=0;i<expected.Length;i++)
            Check(ScanRequest.FrequencyAt(i)==expected[i] && baseline.Apply(request with {SpeedPosition=i}).Wave.line_frequency_hz==expected[i],$"Speed {i} maps exactly to {expected[i]} Hz");
        foreach(int position in new[]{-1,8})
        {
            bool rejected=false;
            try { ScanRequest.FrequencyAt(position); } catch(ArgumentOutOfRangeException) { rejected=true; }
            Check(rejected,$"Reject invalid Speed position {position}");
        }
        var regression=ScanTimingCalculator.Internal(settings.Wave);
        Check(regression.TotalX==1056 && regression.TotalY==1034,"400 Hz regression geometry 1056 x 1034");
        Near(regression.DwellUs,1.183712121212121,1e-12,"400 Hz triangle dwell 1.183712 us");
        Near(regression.FrameTimeMs,2585,1e-10,"400 Hz frame time 2585 ms");
        Near(regression.FrameRateHz,0.3868471953578337,1e-12,"400 Hz frame rate 0.386847 Hz");
        Near(regression.EffectiveLineRateHz,400,0,"One effective image line per full cycle");
        var changed=baseline.Apply(request with { XPixel=512,YPixel=256,OversamplePixel=8,OversampleFrame=20,NumberOfLines=16,SpeedPosition=2 });
        Check(changed.Wave.pixel_per_line==512 && changed.Wave.line_per_frame==256 && changed.Wave.oversample_head==8 && changed.Wave.oversample_tail==8 && changed.Wave.oversample_frame==20,"Geometry overrides all five runtime fields");
        Check(changed.Wave.line_frequency_hz==200 && changed.Wave.number_of_lines==16 && changed.Wave.line_mode && !changed.Wave.external_line_trigger,"Internal runtime overrides");
        string[] overrides={"pixel_per_line","line_per_frame","oversample_head","oversample_tail","oversample_frame","line_frequency_hz","number_of_lines","line_mode","external_line_trigger"};
        foreach(var field in typeof(WaveParams).GetFields())
            if(!overrides.Contains(field.Name)) Check(Equals(field.GetValue(changed.Wave),field.GetValue(baseline.Wave)),"Baseline field preserved: "+field.Name);
        Check(!changed.Wave.scan_dir,"Verified baseline scan direction remains false");
        var changedTiming=ScanTimingCalculator.Internal(changed.Wave);
        Check(changedTiming.TotalX==528 && changedTiming.TotalY==276,"Timing uses edited geometry");
        Near(changedTiming.FrameTimeMs,1380,1e-10,"Edited geometry frame time");
        Near(changedTiming.DwellUs,4.734848484848485,1e-12,"Edited geometry half-cycle dwell");
        var external=baseline.Apply(request with {InternalMode=false});
        Check(external.Wave.external_line_trigger && !external.Wave.line_mode && external.Wave.line_frequency_hz==baseline.Wave.line_frequency_hz && external.Wave.number_of_lines==baseline.Wave.number_of_lines,"External retains baseline frequency and baseline block count");
        foreach(double invalid in new[]{0d,-1,double.NaN,double.PositiveInfinity,double.NegativeInfinity})
            Check(ScanTimingCalculator.External(external.Wave,invalid) is null,"Invalid measured frequency produces no timing: "+invalid);
        foreach(int n in new[]{1,2,3,1055,1056})
        {
            const double f=400;
            var boundaries=Enumerable.Range(0,n+1).Select(i=>ScanTimingCalculator.SineBoundarySeconds(i,n,f)).ToArray();
            var dwells=Enumerable.Range(0,n).Select(i=>boundaries[i+1]-boundaries[i]).ToArray();
            Check(dwells.All(d=>double.IsFinite(d)&&d>0),$"Sine N={n}: all dwells finite / positive");
            Near(boundaries[0],0,0,$"Sine N={n}: begins at zero");
            Near(boundaries[^1],1.0/(2*f),1e-15,$"Sine N={n}: full spatial range spans half-cycle");
            Check(dwells.Select((d,i)=>Math.Abs(d-dwells[n-i-1])<1e-14).All(x=>x),$"Sine N={n}: symmetric dwell");
            Near(dwells[n/2],dwells.Min(),1e-14,$"Sine N={n}: fastest dwell at center");
            var wave=external.Wave; wave.pixel_per_line=n; wave.oversample_head=wave.oversample_tail=0;
            var calculated=ScanTimingCalculator.External(wave,f)!;
            Near(calculated.DwellUs,dwells.Min()*1e6,1e-12,$"Sine N={n}: calculator uses all spatial boundaries");
            Near(calculated.FrameTimeMs,2585,1e-10,$"Sine N={n}: measured full-cycle frequency controls frame time");
            Near(calculated.FrameRateHz,regression.FrameRateHz,1e-12,$"Sine N={n}: measured full-cycle frequency controls frame rate");
            if(n>1000) Near(calculated.DwellUs,1e6/(Math.PI*f*n),1e-6,$"Sine N={n}: center approximation cross-check (test only)");
        }
        var app=new Application();
        var window=new MainWindow();
        var session=(SdkSession)typeof(MainWindow).GetField("session",Private)!.GetValue(window)!;
        var log=(RollingDiagnosticLog)typeof(MainWindow).GetField("log",Private)!.GetValue(window)!;
        void Set(string field,object? value)=>typeof(SdkSession).GetField(field,Private)!.SetValue(session,value);
        void Refresh()=>typeof(MainWindow).GetMethod("RefreshSession",Private)!.Invoke(window,null);
        var makeCapture=typeof(SdkSession).GetMethod("CreateCapture",Private)!;
        var retireCapture=typeof(SdkSession).GetMethod("ArchiveAndClear",Private)!;
        var small=request with {XPixel=3,YPixel=30,OversamplePixel=1,OversampleFrame=4,NumberOfLines=16};
        makeCapture.Invoke(session,new object[]{baseline.Apply(small)});
        var oldCapture=session.CurrentCapture!;
        long oldGeneration=session.Snapshot().Generation;
        Check(oldCapture is InternalCapture && oldCapture.Parameters.Wave.pixel_per_line==3 && oldCapture.Snapshot().Channels.All(c=>c.AcquisitionRows==34),"Prepare capture factory uses requested Internal geometry for all four channels");
        retireCapture.Invoke(session,null);
        makeCapture.Invoke(session,new object[]{baseline.Apply(small with {InternalMode=false,XPixel=4,YPixel=6,OversampleFrame=2})});
        var newCapture=session.CurrentCapture!;
        long newGeneration=session.Snapshot().Generation;
        Check(newCapture is ExternalCapture && newCapture.Parameters.Wave.pixel_per_line==4 && newGeneration>oldGeneration && !ReferenceEquals(oldCapture,newCapture),"Changed mode / geometry creates fresh receiver and generation");
        // Feed caller-owned unmanaged memory into the managed receiver; no DLL or hardware call.
        var pixels=Enumerable.Range(0,32).Select(i=>(float)i).ToArray();
        IntPtr data=Marshal.AllocHGlobal(32*sizeof(float)), metadata=Marshal.AllocHGlobal(Marshal.SizeOf<CResampledImage>());
        try
        {
            Marshal.Copy(pixels,0,data,32);
            Marshal.StructureToPtr(new CResampledImage {channel=2,width=4,height=8,index0_line_number=8,index1_line_number=0,image_data=data,image_size=32},metadata,false);
            newCapture.ArmStart(); newCapture.Receive(metadata,newGeneration,newGeneration);
            var snap=newCapture.Snapshot();
            Check(snap.Channels[2].CompletedFrames==1 && snap.Channels[2].FirstCompleteFrameMs is >=0 && snap.FirstCompleteFrameMs is >=0,"Synthetic External complete frame records first-complete timing");
            Check(snap.Channels[0].CompletedFrames==0 && snap.Channels[1].CompletedFrames==0 && snap.Channels[3].CompletedFrames==0,"External completion counters remain per channel");
        }
        finally { Marshal.FreeHGlobal(data); Marshal.FreeHGlobal(metadata); }
        retireCapture.Invoke(session,null);
        Set("archived",null); Set("parameters",null);
        void SeedPrepared(ScanRequest selected)
        {
            Set("state",ExternalState.RequiresInitialize); Set("parametersSent",false);
            session.SetRequestedParameters(selected);
            Set("parameters",baseline.Apply(selected)); Set("preparedRequest",selected);
            Set("parametersSent",true); Set("state",ExternalState.Prepared);
            Set("frequency",selected.InternalMode?null:new FrequencyParams {line_frequency_hz=400,pos_time=1250,neg_time=1250});
            Refresh();
        }
        void Render(int width,int height,string? filename)
        {
            var root=(FrameworkElement)window.Content;
            root.Measure(new Size(width,height)); root.Arrange(new Rect(0,0,width,height)); root.UpdateLayout();
            var first=window.Viewports[0];
            Check(first.ActualWidth>0 && window.Viewports.All(v=>Math.Abs(v.ActualWidth-v.ActualHeight)<1e-6 && Math.Abs(v.ActualWidth-first.ActualWidth)<1e-6),$"Square equal 2x2 viewports at {width}x{height}");
            Check(window.Viewports.All(v=>v.Image.Stretch==Stretch.Uniform),"All images Uniform fit / letterbox");
            if(filename is null) return;
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output=File.Create(filename); encoder.Save(output);
        }
        Check(window.InitializeButton.IsEnabled && !window.ParametersButton.IsEnabled && !window.StartButton.IsEnabled && !window.StopButton.IsEnabled,"Startup gates; no automatic hardware action");
        Check(window.SpeedSlider.IsSnapToTickEnabled && window.SpeedSlider.Minimum==0 && window.SpeedSlider.Maximum==7 && window.SpeedSlider.TickFrequency==1,"Slider has exactly eight snapped positions");
        for(int i=0;i<8;i++)
        {
            window.SpeedSlider.Value=i;
            Check(session.Snapshot().Requested!.FrequencyHz==expected[i] && window.RequestedFrequency.Text==$"{expected[i]} Hz",$"UI Speed {i} updates requested frequency immediately");
        }
        window.SpeedSlider.Value=1;
        Check(window.InternalDwell.Text=="1.184 us" && window.InternalFrameTime.Text=="2585.000 ms" && window.InternalFrameRate.Text=="0.387 Hz","Internal regression main readouts");
        Render(1480,1000,imagePrefix+"-internal.png"); Render(1160,860,null); Render(1800,1000,null);
        var edits=new (string Name,Action Edit,Action Restore)[] {
            ("X Pixel",()=>window.XPixel.Text="512",()=>window.XPixel.Text="1024"),
            ("Y Pixel",()=>window.YPixel.Text="512",()=>window.YPixel.Text="1024"),
            ("Oversample Pixel",()=>window.OversamplePixel.Text="8",()=>window.OversamplePixel.Text="16"),
            ("Oversample Frame",()=>window.OversampleFrame.Text="20",()=>window.OversampleFrame.Text="10"),
            ("Speed",()=>window.SpeedSlider.Value=2,()=>window.SpeedSlider.Value=1),
            ("Number Of Lines",()=>window.LinesPerCallback.SelectedIndex=0,()=>window.LinesPerCallback.SelectedIndex=1),
            ("Trigger Mode",()=>window.TriggerMode.SelectedIndex=0,()=>window.TriggerMode.SelectedIndex=1),
            ("Invalid geometry",()=>window.XPixel.Text="",()=>window.XPixel.Text="1024")
        };
        foreach(var edit in edits)
        {
            SeedPrepared(request); Check(window.StartButton.IsEnabled,"Synthetic Prepared enables Start before "+edit.Name);
            edit.Edit(); var snapshot=session.Snapshot();
            Check(snapshot.State==ExternalState.PrepareRequired && !snapshot.ParametersSent && !window.StartButton.IsEnabled,"Prepared edit requires Prepare: "+edit.Name);
            Check(window.ParametersButton.IsEnabled==(snapshot.Requested is not null),"Prepare respects valid input: "+edit.Name);
            edit.Restore();
        }
        SeedPrepared(request); Set("state",ExternalState.Scanning); Refresh();
        Check(!window.GeometryControls.IsEnabled && !window.SpeedSlider.IsEnabled && !window.LinesPerCallback.IsEnabled,"Scanning disables mode / geometry / Speed / blocks");
        window.SpeedSlider.Value=7; window.XPixel.Text="128"; window.TriggerMode.SelectedIndex=0;
        Check(session.Snapshot().Requested==request && window.SpeedSlider.Value==1 && window.XPixel.Text=="1024" && window.TriggerMode.SelectedIndex==1,"Scanning rejects even programmatic request edits");
        Set("state",ExternalState.RequiresInitialize); Set("parametersSent",false); Refresh();
        window.TriggerMode.SelectedIndex=0;
        Check(window.InternalTimingPanel.Visibility==Visibility.Collapsed && window.ExternalTimingPanel.Visibility==Visibility.Visible && !window.LinesPerCallback.IsEnabled,"External hides Speed and disables line block selector");
        Check(window.MeasuredFrequency.Text=="—" && window.ExternalDwell.Text=="—" && window.ExternalFrameTime.Text=="—" && window.ExternalFrameRate.Text=="—","External before Prepare has no substituted baseline timing");
        SeedPrepared(request with {InternalMode=false});
        Check(window.MeasuredFrequency.Text=="400.000 Hz" && window.ExternalDwell.Text!="—" && window.ExternalFrameTime.Text=="2585.000 ms" && window.ExternalFrameRate.Text=="0.387 Hz","Synthetic successful External Prepare displays measured-derived timing");
        window.SdkStatus.Text="OFFLINE FIXTURE: synthetic 400 Hz measurement; no native SDK loaded";
        Render(1480,1000,imagePrefix+"-external.png");
        window.XPixel.Text="512";
        Check(session.Snapshot().Frequency is null && window.MeasuredFrequency.Text=="—" && window.ExternalDwell.Text=="—" && window.ExternalFrameTime.Text=="—" && window.ExternalFrameRate.Text=="—","External request change clears all stale measured timing");
        Set("state",ExternalState.RequiresInitialize); Set("parametersSent",false); Refresh();
        Check(window.InitializeButton.IsEnabled && !window.ParametersButton.IsEnabled && !window.StartButton.IsEnabled,"Requires Initialize cannot skip directly to Prepare / Start");
        Check(!Diagnostics.SdkLoaded(),"SDK remained unloaded after all offline tests");
        // No native session exists. Stop the managed log explicitly before allowing fixture teardown.
        log.FinishAsync().GetAwaiter().GetResult();
        typeof(MainWindow).GetField("allowClose",Private)!.SetValue(window,true);
        window.Close(); app.Shutdown();
        return new { Passed=true, Mode="Pure offline; synthetic states; no SDK load or hardware API calls", Checks=checks,
            Regression=regression, Reference="Paz reports first complete frame about 2.66 s; not measured by this test", Images=new[]{imagePrefix+"-internal.png",imagePrefix+"-external.png"} };
    }
}
