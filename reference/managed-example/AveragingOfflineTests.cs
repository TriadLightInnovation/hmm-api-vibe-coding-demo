using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HmmExample.Acquisition;
using HmmExample.Display;
using HmmExample.Interop;

namespace HmmExample;

internal static class AveragingOfflineTests
{
    private static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    private static void Finish(Task task)=>task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    private static void Until(Func<bool> condition)
    {var time=Stopwatch.StartNew();while(!condition()) {if(time.ElapsedMilliseconds>5000) throw new TimeoutException("Averaging offline condition");Thread.Sleep(1);}}
    private static void Refresh(MainWindow window)
    {typeof(MainWindow).GetMethod("RefreshSession",Private)!.Invoke(window,null);typeof(MainWindow).GetMethod("RefreshPreview",Private)!.Invoke(window,null);}
    private static void Emit(IAcquisitionCapture capture,long generation,int channel,int width,int height,int n0,int n1,float[] data)
    {
        IntPtr pixels=Marshal.AllocHGlobal(data.Length*4),meta=Marshal.AllocHGlobal(Marshal.SizeOf<CResampledImage>());
        try {Marshal.Copy(data,0,pixels,data.Length);Marshal.StructureToPtr(new CResampledImage {channel=channel,width=width,height=height,index0_line_number=n0,index1_line_number=n1,image_size=data.Length,image_data=pixels},meta,false);capture.Receive(meta,generation,generation);}
        finally {Marshal.FreeHGlobal(meta);Marshal.FreeHGlobal(pixels);}
    }
    private static void Render(MainWindow window,string path)
    {
        var root=(FrameworkElement)window.Content;root.Measure(new Size(1600,1150));root.Arrange(new Rect(0,0,1600,1150));root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(1600,1150,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path)) encoder.Save(file);
    }
    internal static object Run(string directory)
    {
        Directory.CreateDirectory(directory);
        NativeLibrary.SetDllImportResolver(typeof(HmmNative).Assembly,(_,_,_)=>throw new InvalidOperationException("Native SDK forbidden in averaging tests"));
        var checks=new List<string>();
        void Check(bool condition,string text) {if(!condition) throw new InvalidOperationException(text);checks.Add(text);}
        void Near(double actual,double expected,string text)=>Check(Math.Abs(actual-expected)<1e-9,text);
        Check(!Diagnostics.SdkLoaded(),"No native SDK loaded at entry");
        Check(ExperimentalHardwareEvidence.InternalBaseline.Features.Length==6&&ExperimentalHardwareEvidence.InternalBaseline.Features.All(f=>f.Result=="PASS")
            &&ExperimentalHardwareEvidence.InternalBaseline.EvidenceSource.Contains("Paz-reported")&&ExperimentalHardwareEvidence.InternalBaseline.DirectStart.Contains("crash confirmed"),
            "Paz Internal hardware PASS and 3.5.0 crash are explicit reported evidence, separate from offline test results");
        var baseline=ExternalParameters.LoadBaseline();
        var request=ScanRequest.Default with {LineAverage=true,AverageLines=3};
        var settings=baseline.Apply(request);var w=settings.Wave;
        Check(w.line_per_frame==3072&&w.oversample_frame==30&&w.number_of_lines==96,"N=3: requested Y=1024, OS=10, block=32 -> SDK 3072,30,96");
        Check(w.line_average==1&&w.frame_average==1,"Both SDK averaging fields fixed to 1");
        Check(settings.OutputHeight==1024&&settings.OutputDummy==10,"Output geometry returns to requested rows");
        Check(settings.Describe(false).Contains("Requested Number Of Lines: 32")&&settings.Describe(false).Contains("Effective SDK number_of_lines: 96"),"Parameters tab text distinguishes requested and effective block size");
        var off=baseline.Apply(request with {LineAverage=false});
        Check(off.Wave.number_of_lines==32&&off.Wave.line_per_frame==1024&&off.Wave.oversample_frame==10,"Line Average OFF restores requested SDK geometry and block size");
        var timing=ScanTimingCalculator.Internal(w);var baseTiming=ScanTimingCalculator.Internal(off.Wave);
        Near(timing.DwellUs,baseTiming.DwellUs,"Line Average does not change pixel dwell");
        Near(timing.FrameTimeMs,7755,"N=3 frame time = 7755 ms");
        Near(timing.FrameRateHz,baseTiming.FrameRateHz/3,"N=3 frame rate divided by 3");
        Near(ScanTimingCalculator.Internal(baseline.Apply(request with {NumberOfLines=16}).Wave).FrameTimeMs,timing.FrameTimeMs,"Block granularity never changes frame time");
        var ext=baseline.Apply(request with {InternalMode=false});
        var extOff=baseline.Apply(request with {InternalMode=false,LineAverage=false});
        Near(ScanTimingCalculator.External(ext.Wave,400)!.DwellUs,ScanTimingCalculator.External(extOff.Wave,400)!.DwellUs,"External dwell unchanged by averaging");
        Near(ScanTimingCalculator.External(ext.Wave,400)!.FrameTimeMs,2585,"External frame time ignores stored Line Average setting");
        Check(ext.Wave.line_per_frame==1024&&ext.Wave.oversample_frame==10&&ext.Wave.number_of_lines==baseline.Wave.number_of_lines&&ext.Wave.line_average==1,"External Line Average ON leaves Y/OS/block geometry unscaled and SDK averaging=1");
        Check(Equals(ext.Wave,extOff.Wave)&&ext.LineFactor==1,"External ON/OFF Line Average has identical runtime WaveParams");
        Check(baseline.Apply(request with {InternalMode=false,AverageLines=int.MaxValue}).Wave.line_per_frame==1024,"External ignores even an out-of-range inactive Line Average count");
        bool overflow=false;try {baseline.Apply(request with {AverageLines=int.MaxValue});}catch(ArgumentException){overflow=true;}
        Check(overflow,"Oversized averaging rejected before overflow/allocation");

        var small=baseline.Apply(request with {XPixel=2,YPixel=34,OversamplePixel=0,OversampleFrame=0});
        CallbackBlock Block(int channel,int n0,int n1,int first=0)=>new(channel,2,102,n0,n1,
            Enumerable.Range(first,(n0+n1)*2).Select(i=>(float)i).ToArray(),Stopwatch.GetTimestamp(),1);
        var block=LineAverager.Process(Block(0,96,0),small);
        Check(block.Index0Lines==32&&block.Index1Lines==0&&block.Data.Length==64&&block.Height==34,"Regression: 96 raw lines -> exactly 32 processed lines in normal callback");
        Check(block.Data[0]==2&&block.Data[1]==3&&block.Data[62]==188,"True per-pixel mean of each 3 consecutive lines, not decimation");
        var split=LineAverager.Process(Block(0,6,90,192),small);
        Check(split.Index0Lines==2&&split.Index1Lines==30,"Frame boundary split converts raw 6+90 into processed 2+30");
        var pipeline=new FramePipeline(new(2,34,0,32,DummyPolicy.RawIncludesOverscan),1,InitialAlignment.ConfirmedFrameStart);
        pipeline.Accept(block);pipeline.Accept(split);
        Check(pipeline.TryRead(0,out var complete)&&complete is {Height:34}&&complete.Data[64]==194,"First processed frame completed without cross-frame mixing");
        var destination=new float[68];pipeline.Assemblers[0].TryCopyLive(0,destination,out var live);
        Check(live is {AccumulatedLines:30}&&live.Data[0]==206&&live.Data[60]==0,"Split callback live state is next frame prefix with unacquired rows clear");
        Check(complete!.Data[0]==2,"Published frame remains immutable after next-frame writes");
        Check(pipeline.Assemblers[1].Snapshot().AccumulatedLines==0,"Channels maintain independent line assembly");
        foreach(var invalid in new[]{Block(0,32,0),Block(0,5,91),Block(0,96,0) with {Height=101}})
        {
            bool rejected=false;try {LineAverager.Process(invalid,small);}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"Reject inconsistent raw count/group boundary/geometry without guessing remainder");
        }
        pipeline.Clear();

        CompletedFrame Frame(int channel,int seq,float value)=>new(channel,2,1,[value,value*2],seq,Stopwatch.GetTimestamp(),7);
        ProcessedLineSegment Whole(CompletedFrame frame)=>new(frame.Channel,frame.Width,frame.Height,frame.Data,0,0,frame.Height,frame.FrameSequence,frame.Timestamp,frame.SessionGeneration);
        var batch=new FrameBatchAccumulator(3);
        Check(batch.Add(Whole(Frame(0,1,1))) is null&&batch.Add(Whole(Frame(0,2,2))) is null,"Batch N=3 emits nothing for frames 1 and 2");
        var a123=batch.Add(Whole(Frame(0,3,3)));
        Check(a123!.Data.SequenceEqual(new float[]{2,4})&&batch.BatchFrameIndex==0&&batch.CompletedBatches==1,"Batch average123 published then sum/count reset");
        Check(batch.Add(Whole(Frame(0,4,4))) is null&&batch.Add(Whole(Frame(0,5,5))) is null,"No rolling output average234 or average345 from frames 4/5");
        var a456=batch.Add(Whole(Frame(0,6,6)));
        Check(a456!.Data.SequenceEqual(new float[]{5,10})&&batch.CompletedBatches==2,"Second output is non-overlapping average456");
        Check(a123.Data[0]==2,"Published prior batch remains immutable after reset");
        Check(batch.Add(Whole(Frame(0,7,7))) is null&&batch.Add(Whole(Frame(0,8,8))) is null&&batch.Add(Whole(Frame(0,9,9)))!.Data[0]==8,"Third batch averages frames789 only");
        var failures=new List<string>();var average=new DisplayAverager(2,m=>{lock(failures) failures.Add(m);});average.Configure(new(true,3));
        foreach(int seq in new[]{1,2,3}) {average.Publish(Frame(0,seq,seq));average.Publish(Frame(2,seq,seq+10));}
        Until(()=>average.Snapshot().CompletedBatches[0]==1&&average.Snapshot().CompletedBatches[2]==1);
        average.TryTakeLive(0,out var ch0);average.TryTakeLive(2,out var ch2);
        Check(ch0!.Data[0]==2&&ch2!.Data[0]==12&&average.Snapshot().CompletedBatches[1]==0,"Worker batch sums isolated per channel with interleaved arrival");
        average.Publish(Frame(0,4,4));average.Publish(Frame(0,5,5));Until(()=>average.Snapshot().Collected[0]==2);
        Check(!average.TryTakeLive(0,out _),"Frames 4/5 never publish a raw bypass or rolling display update");
        average.Configure(new(true,2));Check(average.Snapshot().Collected.All(x=>x==0)&&!average.TryTakeLive(0,out _),"Changing N resets batch state and pending output");
        average.Publish(Frame(0,6,6));Until(()=>average.Snapshot().Collected[0]==1);
        Check(!average.TryTakeLive(0,out _),"New N batch waits for its own frames");
        average.Publish(Frame(0,7,7));Until(()=>average.Snapshot().CompletedBatches[0]==1);average.TryTakeLive(0,out var reset);
        Near(reset!.Data[0],6.5,"After reset batch uses only new frames");
        average.Configure(new(false,2));average.Publish(Frame(0,8,8));Check(!average.TryTakeLive(0,out _),"OFF clears batch state and does not emit stale average");average.Retire();

        // Full-size progressive average, including clear-to-black at the next batch's Nth frame.
        var progressive=new DisplayAverager(4,_=>{});progressive.Configure(new(true,3));
        ProcessedLineSegment Segment(int seq,int row,int count,float value,long gen=11)=>new(0,1,4,Enumerable.Repeat(value,count).ToArray(),0,row,count,seq,Stopwatch.GetTimestamp(),gen);
        for(int seq=1;seq<=2;seq++) progressive.Publish(Segment(seq,0,4,seq));
        Until(()=>progressive.Snapshot().Collected[0]==2);Check(!progressive.TryTakeLive(0,out _),"First N-1 processed frames only accumulate sums");
        progressive.Publish(Segment(3,0,2,3));Until(()=>progressive.Snapshot().DisplayLines[0]==2);
        progressive.TryTakeLive(0,out var prefix);
        Check(prefix is {Height:4,AccumulatedLines:2}&&prefix.Data.SequenceEqual(new float[]{2,2,0,0})&&progressive.Snapshot().CompletedBatches[0]==0,"Nth first block displays averaged rows before frame completion; other rows black");
        progressive.Publish(Segment(3,2,2,3));Until(()=>progressive.Snapshot().CompletedBatches[0]==1);progressive.TryTakeLive(0,out var finished);
        Check(finished!.Data.All(x=>x==2),"Nth final block completes first average");
        progressive.Publish(Segment(4,0,4,4));progressive.Publish(Segment(5,0,4,5));Until(()=>progressive.Snapshot().Collected[0]==2);
        Check(!progressive.TryTakeLive(0,out _),"Next batch frames 4/5 do not mix partial/raw pixels into prior average");
        progressive.Publish(Segment(6,0,2,6));Until(()=>progressive.Snapshot().DisplayLines[0]==2);progressive.TryTakeLive(0,out var secondPrefix);
        Check(secondPrefix!.Data.SequenceEqual(new float[]{5,5,0,0})&&finished.Data.All(x=>x==2),"Frame6 starts a new black display, preserving no ghost rows from average123");
        progressive.Publish(Segment(6,2,2,6));Until(()=>progressive.Snapshot().CompletedBatches[0]==2);
        progressive.Configure(new(true,1));progressive.Publish(Segment(7,2,2,7));Thread.Sleep(20);
        Check(!progressive.TryTakeLive(0,out _),"Mid-frame option reset discards unanchored remainder instead of mixing incomplete batch");
        progressive.Publish(Segment(8,0,2,8));Until(()=>progressive.Snapshot().DisplayLines[0]==2);progressive.TryTakeLive(0,out var n1);
        Check(n1!.Data.SequenceEqual(new float[]{8,8,0,0}),"N=1 progressive output begins at next known row zero");
        progressive.Publish(Segment(8,3,1,8));Until(()=>progressive.Snapshot().Error is not null);
        Check(!progressive.TryTakeLive(0,out _),"Row gap invalidates batch; no silently fabricated display");progressive.Retire();

        var log=new RollingDiagnosticLog(Path.Combine(directory,"capture"));
        var capture=new InternalCapture(9,small,log);capture.ArmStart();
        Emit(capture,9,0,2,102,96,0,Block(0,96,0).Data);
        Check(capture.TryTakeLive(0,out var processed)&&processed is {Height:34,AccumulatedLines:32}&&processed.Data[0]==2,"Actual native-copy adapter publishes full-size progressive processed image");
        capture.LiveDisplayed(0,processed!.Version);
        Emit(capture,9,0,2,102,32,0,Block(0,32,0).Data);
        Check(capture.Snapshot().Channels[0].RejectedCallbacks==1&&capture.Snapshot().Channels[0].LastMetadata!.Classification.Contains("expected 96 raw lines"),"Malformed callback produces explicit diagnostic error and invalidation");
        capture.FreezeAndSnapshot();Finish(log.FinishAsync());

        var splitLog=new RollingDiagnosticLog(Path.Combine(directory,"split-batch"));
        var splitCapture=new InternalCapture(77,small,splitLog);splitCapture.Averaging.Configure(new(true,3));splitCapture.ArmStart();
        int[] counts={0,1,2,0,1,2,0}, visible={0,0,28,34,34,22,34};long[] batches={0,0,0,1,1,1,2};
        LiveFrameSnapshot? firstBatch=null;
        for(int b=0;b<7;b++)
        {
            int rawStart=b*96,n0=Math.Min(102-rawStart%102,96),n1raw=96-n0;
            var data=Enumerable.Range(0,192).Select(i=>(float)((rawStart+i/2)/102+1+100*(i%2))).ToArray();
            Emit(splitCapture,77,0,2,102,n0,n1raw,data);
            int index=b;Until(()=> {var a=splitCapture.Averaging.Snapshot();return a.Collected[0]==counts[index]&&a.DisplayLines[0]==visible[index]&&a.CompletedBatches[0]==batches[index];});
            bool updated=splitCapture.Averaging.TryTakeLive(0,out var part);
            Check(updated==(b is 2 or 3 or 5 or 6),$"Split callback {b+1}: updates only Nth-frame averaged segments");
            if(b==2 || b==5)
            {
                float expected=b==2?2:5;
                Check(part!.Height==34&&part.AccumulatedLines==visible[b]&&Enumerable.Range(0,visible[b]*2).All(i=>part.Data[i]==expected+100*(i%2))&&part.Data.Skip(visible[b]*2).All(x=>x==0),"Native-copy -> Line Average -> split assembler -> batch average preserves exact means and black tail");
                if(b==2) Check(splitCapture.Snapshot().Channels[0].CompletedFrames==2,"Split callback publishes first average prefix while third frame is incomplete");
            }
            if(b==3) firstBatch=part;
            if(b==6) Check(firstBatch!.Data[0]==2&&part!.Data[0]==5,"Second batch does not mutate first snapshot or reuse prior sum");
        }
        splitCapture.FreezeAndSnapshot();Finish(splitLog.FinishAsync());

        var externalLog=new RollingDiagnosticLog(Path.Combine(directory,"external"));
        var externalSettings=baseline.Apply(request with {InternalMode=false,XPixel=2,YPixel=32,OversamplePixel=0,OversampleFrame=0});
        var externalCapture=new ExternalCapture(10,externalSettings,externalLog);externalCapture.ArmStart();
        Emit(externalCapture,10,1,2,32,32,0,Enumerable.Range(0,64).Select(x=>(float)x).ToArray());
        Check(externalCapture.TryTake(1,out var externalFrame)&&externalFrame is {Height:32}&&externalFrame.Data[0]==0&&externalFrame.Data[2]==2,"External path never applies Managed Line Average");
        externalCapture.Averaging.Configure(new(true,3));
        for(int seq=1;seq<=6;seq++)
        {
            Emit(externalCapture,10,1,2,32,32,0,Enumerable.Repeat((float)seq,64).ToArray());
            int expected=seq/3;
            Until(()=>externalCapture.Averaging.Snapshot().CompletedBatches[1]==expected&&externalCapture.Averaging.Snapshot().Collected[1]==seq%3);
            bool output=externalCapture.Averaging.TryTakeLive(1,out var externalAverage);
            Check(output==(seq%3==0),$"External frame {seq}: only complete non-overlapping batch publishes");
            if(output) Check(externalAverage!.AccumulatedLines==32&&externalAverage.Data[0]==seq-1,"External average is a whole frame with exact batch mean");
        }
        externalCapture.FreezeAndSnapshot();Finish(externalLog.FinishAsync());

        Check(SdkRuntimeProfile.Recognize(SdkRuntimeProfile.BaselineHash).Kind==SdkProfileKind.Baseline350,"Known 3.5.0 hash is informational");
        Check(SdkRuntimeProfile.Recognize(new string('0',64)).Kind==SdkProfileKind.Unknown,"Unknown hash is informational");
        var overrideLog=new RollingDiagnosticLog(Path.Combine(directory,"override"));
        bool nativeOverride=false;try {new SdkSession(overrideLog,offlineProfile:new("fake",SdkProfileKind.Test352));}catch(InvalidOperationException){nativeOverride=true;}
        Check(nativeOverride,"Production NativeHmmApi cannot accept test profile injection");Finish(overrideLog.FinishAsync());
        var directLog=new RollingDiagnosticLog(Path.Combine(directory,"direct"));var api=new ReinitNoPrepareOfflineTests.FakeApi();
        var expiry=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int loads=0;
        var direct=new SdkSession(directLog,api,r=>{loads++;return baseline.Apply(r);},(_,token)=>expiry.Task.WaitAsync(token),new("OFFLINE UNKNOWN HASH",SdkProfileKind.Unknown));
        direct.SetRequestedParameters(ScanRequest.Default with {XPixel=2,YPixel=64,OversampleFrame=0,OversamplePixel=0,NumberOfLines=32,LineAverage=true,AverageLines=3});
        Finish(direct.InitializeAsync());Finish(direct.PrepareAsync());Finish(direct.StartAsync());Finish(direct.StopAsync());
        Check(!direct.Snapshot().CanDirectRescan,"Lifecycle gate rejects prior Start/Stop without acquisition data");
        api.Calls.Clear();Finish(direct.DirectRescanAsync());Check(api.Calls.Count==0,"Ineligible no-data session cannot issue Direct Start");
        Finish(direct.InitializeAsync());Finish(direct.PrepareAsync());Finish(direct.StartAsync());api.Emit(0,96);Finish(direct.StopAsync());
        Check(!direct.Snapshot().CanDirectRescan,"Prior callback-only session without a complete frame is not acquisition confirmed");
        Finish(direct.InitializeAsync());Finish(direct.PrepareAsync());Finish(direct.StartAsync());for(int c=0;c<4;c++) api.Frame(c);Finish(direct.StopAsync());
        Check(direct.Snapshot().CanDirectRescan,"Unknown profile permits Direct Start after clean normal session (fake API only)");
        int previousLoads=loads;api.Calls.Clear();Finish(direct.DirectRescanAsync());
        Check(api.Calls.SequenceEqual(new[]{"startScan"})&&loads==previousLoads,"Direct Start calls startScan only; no initialize/config/registration/JSON reload");
        Check(direct.Snapshot().DirectRescan!.Status=="Waiting for data","Start true alone is not PASS");
        var liveCapture=direct.CurrentCapture!;long gen=direct.Snapshot().Acquisition!.Generation;
        Emit(liveCapture,gen-1,0,2,96,96,0,new float[192]);
        Check(!direct.Snapshot().DirectRescan!.Progress!.HasNewCallback,"Old-generation callback cannot satisfy Direct Start PASS");
        api.Frame(0);Until(()=>direct.Snapshot().DirectRescan!.Status=="PASS");
        Check(direct.Snapshot().DirectRescan!.Progress!.HasNewFrame,"Direct Start PASS requires new processed complete frame");Finish(direct.StopAsync());
        expiry=new(TaskCreationOptions.RunContinuationsAsynchronously);api.Calls.Clear();Finish(direct.DirectRescanAsync());expiry.TrySetResult();Until(()=>direct.Snapshot().DirectRescan!.Status=="FAIL");
        Check(direct.Snapshot() is {State:ExternalState.Scanning,NeedsStop:true}&&api.Calls.SequenceEqual(new[]{"startScan"}),"Zero callback timeout FAIL; no automatic fallback or stop");Finish(direct.StopAsync());
        Check(!direct.Snapshot().CanDirectRescan,"No-data experimental Stop does not re-enable Direct Start");
        Finish(direct.InitializeAsync());Finish(direct.PrepareAsync());Finish(direct.StartAsync());api.Frame(0);Finish(direct.StopAsync());
        api.Calls.Clear();api.StartResult=false;Finish(direct.DirectRescanAsync());
        Check(direct.Snapshot().DirectRescan!.Status=="FAIL"&&api.Calls.SequenceEqual(new[]{"startScan"}),"Direct Start false is FAIL with no fallback");Finish(direct.StopAsync());Finish(directLog.FinishAsync());

        var onceLog=new RollingDiagnosticLog(Path.Combine(directory,"once"));var onceApi=new ReinitNoPrepareOfflineTests.FakeApi();var once=new SdkSession(onceLog,onceApi,baseline.Apply);
        once.SetRequestedParameters(request with {XPixel=2,YPixel=32,OversamplePixel=0,OversampleFrame=0});once.ConfigureFrameAverage(new(true,3));
        onceApi.DuringStart=()=>{for(int c=0;c<4;c++) onceApi.Frame(c);};Finish(once.ScanOnceAsync());
        Check(once.Snapshot() is {State:ExternalState.RequiresInitialize,ScanOnce.Status:"Complete"}&&once.Snapshot().Acquisition!.Channels.All(c=>c.CompletedFrames==1),"Scan Once stops after one processed frame per enabled channel, not N batch frames");
        Check(once.TakeRetainedOnce()!.All(f=>f is {Height:32}),"Scan Once retains processed geometry");Finish(onceLog.FinishAsync());

        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var uiLog=new RollingDiagnosticLog(Path.Combine(directory,"ui"));var uiApi=new ReinitNoPrepareOfflineTests.FakeApi();var uiSession=new SdkSession(uiLog,uiApi,baseline.Apply,offlineProfile:new("OFFLINE UNKNOWN HASH",SdkProfileKind.Unknown));var window=new MainWindow(uiSession,uiLog);
        window.YPixel.Text="64";window.XPixel.Text="2";window.OversamplePixel.Text="0";window.OversampleFrame.Text="0";window.LineAverageEnabled.IsChecked=true;
        Check(window.FindName("ReinitButton") is null&&window.FindName("SetParamButton") is null&&window.RescanButton.Content.ToString()=="Direct Start Test"&&!window.RescanButton.IsEnabled,"Old Experimental UI removed; Direct Start waits for a successful acquisition cycle");
        window.TriggerMode.SelectedIndex=0;Refresh(window);
        Check(!window.LineAverageControls.IsEnabled&&window.FrameAverageEnabled.IsEnabled&&uiSession.Snapshot().Requested!.LineFactor==1,"External UI disables Line Average, keeps Frame Average usable and clears the checked state");
        window.TriggerMode.SelectedIndex=1;Refresh(window);
        Check(window.LineAverageControls.IsEnabled&&window.LineAverageEnabled.IsChecked==false&&uiSession.Snapshot().Requested!.LineFactor==1,"Returning to Internal keeps Line Average OFF until manually selected");
        window.LineAverageEnabled.IsChecked=true;Refresh(window);
        Check(window.ActiveParameters.Text.Contains("Requested Number Of Lines: 32")&&window.ActiveParameters.Text.Contains("Effective SDK number_of_lines: 96"),"WPF Parameters tab exposes requested 32 / SDK 96");
        Finish(uiSession.InitializeAsync());Finish(uiSession.PrepareAsync());Refresh(window);
        window.AverageLines.Text="4";Check(uiSession.Snapshot().State==ExternalState.PrepareRequired&&!window.StartButton.IsEnabled,"Line Average edit after Prepare requires Prepare again");window.AverageLines.Text="3";
        Finish(uiSession.PrepareAsync());Refresh(window);uiApi.Calls.Clear();window.FrameAverageEnabled.IsChecked=true;
        Check(uiSession.Snapshot().State==ExternalState.Prepared&&uiApi.Calls.Count==0,"Frame Average edit does not invalidate Prepare or call FPGA");
        Finish(uiSession.StartAsync());Refresh(window);Check(!window.LineAverageControls.IsEnabled&&window.FrameAverageEnabled.IsEnabled,"Scanning locks Line Average but permits Frame Average edits");
        for(int seq=1;seq<=2;seq++){uiApi.Frame(0);Until(()=>uiSession.CurrentCapture!.Averaging.Snapshot().Collected[0]==seq);Refresh(window);}
        Check(window.Channel1.Image.Source is null,"WPF collects first two complete frames without pretending to have N-frame average");
        uiApi.Emit(0,96);Until(()=>uiSession.CurrentCapture!.Averaging.Snapshot().DisplayLines[0]==32);Refresh(window);
        Check(uiSession.Snapshot().Acquisition!.Channels[0].CompletedFrames==2,"WPF averaged preview updates during third processed frame before completion");
        Check(window.Channel1.Image.Source is WriteableBitmap {PixelHeight:64}&&window.CompositeViewport.Image.Source is not null,"CH and Composite display processed batch-average source");
        var shown=(BitmapSource)window.Channel1.Image.Source!;var gray=new byte[128];shown.CopyPixels(gray,2,0);
        var rgb=new byte[512];((BitmapSource)window.CompositeViewport.Image.Source!).CopyPixels(rgb,8,0);
        Check(Enumerable.Range(0,128).All(i=>rgb[i*4+1]==gray[i]&&rgb[i*4]==0&&rgb[i*4+2]==0),"Composite CH1 green equals the exact normalized averaged CH1 pixels");
        Check(gray.Skip(64).All(x=>x==0),"WPF and Composite unaveraged rows stay black on Nth progressive frame");
        window.SdkStatus.Text="OFFLINE FAKE API — Nth-frame batch average prefix; black unacquired rows; no hardware";
        Render(window,Path.Combine(directory,"batch-progressive-prefix.png"));
        uiApi.Emit(0,96);Until(()=>uiSession.CurrentCapture!.Averaging.Snapshot().CompletedBatches[0]==1);Refresh(window);
        uiApi.Calls.Clear();string beforeTiming=window.InternalFrameTime.Text;window.AverageFrames.Text="2";Refresh(window);
        Check(uiApi.Calls.Count==0&&uiSession.Snapshot().State==ExternalState.Scanning&&window.Channel1.Image.Source is null,"Changing batch N while Scanning resets sum/display without FPGA call");
        Check(window.InternalFrameTime.Text==beforeTiming,"Frame Average N does not alter acquisition timing");
        Check(window.ActiveParameters.Text.Contains("Acquisition Frame Rate:")&&window.ActiveParameters.Text.Contains("Averaged Display Rate:"),"Parameters separates acquisition and averaged display rates");
        Check(window.ActiveParameters.Text.Contains("Acquisition Frame Rate: "+(400.0/192).ToString("F6"))&&window.ActiveParameters.Text.Contains("Averaged Display Rate: "+(400.0/192/2).ToString("F6")),"Averaged display rate is acquisition rate / batch N; main acquisition rate unchanged");
        window.FrameAverageEnabled.IsChecked=false;uiApi.Frame(0);Refresh(window);Check(window.Channel1.Image.Source is not null,"Frame Average OFF resumes progressive processed preview");
        Finish(uiSession.StopAsync());Refresh(window);
        Check(window.RescanButton.IsEnabled&&window.SdkProfileStatus.Text.Contains("Unknown / manually supplied DLL"),"Unknown profile WPF Direct Start button enabled after complete acquisition and clean Stop");
        Check(window.DirectStartWarning.Visibility==Visibility.Collapsed&&!window.DirectRescanStatus.Text.Contains("disabled"),"Unknown profile has no hash-based disabled status");
        window.SdkStatus.Text="OFFLINE FAKE API — averaging UI; no hardware executed";
        Render(window,Path.Combine(directory,"averaging-ui.png"));
        typeof(MainWindow).GetField("allowClose",Private)!.SetValue(window,true);window.Close();app.Shutdown();Finish(uiLog.FinishAsync());
        Check(!Diagnostics.SdkLoaded(),"Native SDK remained unloaded; no physical hardware actions");
        return new {Passed=true,Checks=checks,Hardware="NOT TESTED",DirectStart="Unknown profile uses injected fake API only; hash is informational",Directory=directory};
    }
}
