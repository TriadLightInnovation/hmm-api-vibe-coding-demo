using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HmmExample.Acquisition;
using HmmExample.Display;
using HmmExample.Interop;
namespace HmmExample;
internal static class PreviewOfflineTests
{
    internal static object Run(string directory)
    {
        Directory.CreateDirectory(directory);
        NativeLibrary.SetDllImportResolver(typeof(HmmNative).Assembly,(_,_,_)=>throw new InvalidOperationException("Native SDK prohibited in preview tests"));
        var checks=new List<string>();void Check(bool value,string name) {if(!value)throw new InvalidOperationException(name);checks.Add(name);}
        void Reject(Action action,string name) {bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}catch(OverflowException){rejected=true;}Check(rejected,name);}
        var request=ScanRequest.Default with {XPixel=4096,YPixel=4096,OversamplePixel=16,OversampleFrame=0};
        request.Validate();Check(request.CapabilityNotice.Contains("Hardware Validation Required"),"4096 zero-Dummy fits per-allocation checks but remains hardware-unvalidated");
        (request with {OversampleFrame=10}).Validate();Check(GeometryMath.Pixels(4096,4106)==16818176,"4096 plus Dummy passes allocation safety");
        (request with {LineAverage=true,AverageLines=3}).Validate();Check(HostMemoryAdmissionPolicy.Estimate(request with {LineAverage=true,AverageLines=3}).RawCallbackSamples==4096*96,"4096 N=3 admitted using streaming raw block");
        var baseline=ExternalParameters.LoadBaseline();var small=baseline.Apply(ScanRequest.Default with {LineAverage=true,AverageLines=3});
        Check(small.Wave.line_per_frame==3072&&small.Wave.oversample_frame==30&&small.Wave.number_of_lines==96,"Internal effective SDK scaling still Y*N, Dummy*N, Block*N");
        Check(GeometryMath.Pixels(4096,4096)==16777216&&checked((long)GeometryMath.Pixels(4096,4096)*4)==67108864,"4096 frame = 16777216 float32 samples / 64 MiB");
        Reject(()=>GeometryMath.Pixels(int.MaxValue,int.MaxValue),"Huge product rejected using long before allocation");
        Reject(()=>(request with {YPixel=int.MaxValue,OversampleFrame=int.MaxValue,LineAverage=true,AverageLines=1024}).Validate(),"Effective rows overflow guarded before narrowing/multiplication");
        Reject(()=>new FrameGeometry(4096,int.MaxValue,1,32,DummyPolicy.RawIncludesOverscan).Validate(),"Frame rows checked before buffer allocation");
        new FrameAverageOptions(true,3).Validate(16777216);Check(true,"Frame Average sums pass per-allocation safety; admission checks total budget");
        Reject(()=>PreviewSampling.Create(4096,4096,512),"Preview max dimension limited to explicit 1024 / 2048 choices");
        foreach(int cap in new[]{1024,2048})
        {
            var square=PreviewSampling.Create(4096,4096,cap);Check(square.Width==cap&&square.Height==cap,"Square preview at "+cap);
            var wide=PreviewSampling.Create(4096,2048,cap);Check(wide.Width==cap&&wide.Height==cap/2,"Aspect ratio preserved at "+cap);
            var tall=PreviewSampling.Create(2048,4096,cap);Check(tall.Width==cap/2&&tall.Height==cap,"Portrait aspect ratio preserved at "+cap);
            Check(square.AcquiredRows(32)==cap/128&&square.AcquiredRows(1)==1&&square.AcquiredRows(4096)==cap,"Progressive acquired-row boundary at "+cap);
        }
        var original=Enumerable.Range(0,1024*32).Select(i=>(float)i).ToArray();var nativeSmall=new CompletedFrame(0,1024,32,original,1,1,1);
        var smallGeometry=new FrameGeometry(1024,32,0,32,DummyPolicy.RawIncludesOverscan);
        Check(PreviewRenderer.Complete(nativeSmall,smallGeometry,2048).Pixels.SequenceEqual(FrameRenderer.Normalize(nativeSmall,smallGeometry)),"Below preview cap: exact original grayscale values / normalization");
        var geometry=new FrameGeometry(4096,4096,0,32,DummyPolicy.RawIncludesOverscan);
        var queue=Channel.CreateBounded<CompletedFrame>(1);
        var assembler=new ChannelFrameAssembler(0,geometry,77,InitialAlignment.ConfirmedFrameStart,queue.Writer);
        var block=new float[4096*32];var sampled=new float[1024*1024];long version=0;
        for(int row=0;row<4096;row+=32)
        {
            for(int y=0;y<32;y++) Array.Fill(block,(float)(row+y-100),y*4096,4096);
            assembler.Accept(new(0,4096,4096,32,0,block,Stopwatch.GetTimestamp(),77));
            if(row==0)
            {
                Check(assembler.TryCopyLive(0,sampled,out var snapshot,1024)&&snapshot is {Width:1024,Height:1024,AccumulatedLines:8},"Full 4096 assembler publishes a bounded progressive preview snapshot");
                version=snapshot!.Version;Check(sampled[0]==-100&&sampled[7*1024]==-72,"Preview samples correct original rows");
                var gray=FrameRenderer.NormalizeLive(snapshot);Check(gray.Skip(8*1024).All(x=>x==0),"Unacquired preview rows black with negative values");
                Check(!queue.Reader.TryRead(out _),"Partial preview never publishes a partial CompletedFrame");
            }
        }
        Check(queue.Reader.TryRead(out var frame)&&frame is {Width:4096,Height:4096}&&frame.Data.Length==16777216,"Full-resolution immutable frame retains 4096 geometry and all samples");
        Check(frame!.Data[0]==-100&&frame.Data[^1]==3995&&assembler.Snapshot().CompletedFrames==1,"Assembly data and complete-frame count unchanged by preview sampling");
        Check(assembler.TryCopyLive(version,sampled,out var full,1024)&&full!.AccumulatedLines==1024,"Latest version updates preview after completion");
        var elapsed=new Dictionary<int,double>();
        foreach(int cap in new[]{1024,2048})
        {
            var sw=Stopwatch.StartNew();var preview=PreviewRenderer.Complete(frame,geometry,cap);elapsed[cap]=sw.Elapsed.TotalMilliseconds;
            Check(preview.Pixels.Length==cap*cap&&preview.Width==cap&&preview.Height==cap,"Complete preview bounded at "+cap);
            Check(frame.Data[0]==-100&&frame.Data[^1]==3995&&frame.Data.Length==16777216,"Preview preserves full data at "+cap);
        }
        var avg=new DisplayAverager(4096*32,_=>{});avg.Configure(new(true,3));
        for(int n=1;n<=3;n++) avg.Publish(new CompletedFrame(0,4096,32,Enumerable.Repeat((float)n,4096*32).ToArray(),n,n,88));
        var wait=Stopwatch.StartNew();LiveFrameSnapshot? averaged=null;
        while(!avg.TryTakeLive(0,out averaged,2048)) {if(wait.Elapsed.TotalSeconds>5)throw new TimeoutException();Thread.Sleep(2);}
        Check(averaged is {Width:2048,Height:16,AccumulatedLines:16}&&averaged.Data.All(x=>x==2),"Frame Average uses full data; only its display snapshot is sampled");avg.Retire();
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var log=new RollingDiagnosticLog(Path.Combine(directory,"ui"));var fake=new ReinitNoPrepareOfflineTests.FakeApi();var session=new SdkSession(log,fake,baseline.Apply);var window=new MainWindow(session,log);
        Check(window.ApplicationMenu is Menu {IsMainMenu:true}&&DockPanel.GetDock(window.ApplicationMenu)==Dock.Top,"Standard WPF application Menu docked at client top");
        Check(window.AdcCalibrationMenuItem.Header.ToString()=="ADC Calibration"&&!window.AdcCalibrationMenuItem.IsEnabled,"Calibration wording and pre-initialize gate retained");
        Check(window.PreviewDimension.SelectedIndex==1,"Preview long edge defaults to 2048");
        Check(window.PreviewRate.ToolTip.ToString()!.Contains("ceiling 30 Hz"),"Normal geometry retains requested 30 Hz preview timer");
        window.XPixel.Text="4096";window.YPixel.Text="4096";window.OversampleFrame.Text="0";
        Check(window.ActiveParameters.Text.Contains("Host Memory Budget")&&window.ActiveParameters.Text.Contains("Available Commit")&&window.ActiveParameters.Text.Contains("Admission Result: PASS"),"Parameters show budget, dynamic memory and admission separately from native warning");
        Check(window.RequestValidation.Text.Contains("Hardware Validation Required")&&!window.StartButton.IsEnabled,"4096 UI marks unvalidated capability without starting hardware");
        typeof(MainWindow).GetMethod("PresentComplete",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[frame,geometry]);
        Check(window.Channel1.Image.Source is BitmapSource {PixelWidth:2048,PixelHeight:2048},"CH viewport renders bounded bitmap from full 4096 frame");
        Check(window.PreviewRate.ToolTip.ToString()!.Contains("ceiling 2 Hz"),"4096 / 2048 preview automatically caps UI timer at 2 Hz");
        var unchangedRequest=session.Snapshot().Requested;
        window.PreviewDimension.SelectedIndex=0;
        Check(window.PreviewRate.ToolTip.ToString()!.Contains("ceiling 5 Hz")&&unchangedRequest==session.Snapshot().Requested,"1024 preview caps UI timer at 5 Hz without changing acquisition parameters");
        typeof(MainWindow).GetMethod("PresentComplete",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[frame,geometry]);
        typeof(MainWindow).GetMethod("RefreshComposite",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
        Check(window.Channel1.Image.Source is BitmapSource {PixelWidth:1024,PixelHeight:1024}&&window.CompositeViewport.Image.Source is BitmapSource {PixelWidth:1024,PixelHeight:1024},"CH and Composite use selected 1024 preview size");
        Check(window.Channel1.Image.Stretch==Stretch.Uniform,"Square viewport preserves Uniform fit");
        var root=(FrameworkElement)window.Content;root.Measure(new Size(1444,1000));root.Arrange(new Rect(0,0,1444,1000));root.UpdateLayout();
        Check(window.ApplicationMenu.TranslatePoint(new Point(0,0),root).Y==0,"Menu is flush to client top, above header/content margin");
        var bitmap=new RenderTargetBitmap(1444,1000,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(directory,"preview-menu.png")))png.Save(file);
        Check(fake.Calls.Count==0&&!Diagnostics.SdkLoaded(),"No Initialize/Prepare/Calibration/Start or native SDK load");
        assembler.Clear();typeof(MainWindow).GetField("allowClose",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true);window.Close();log.FinishAsync().GetAwaiter().GetResult();app.Shutdown();
        return new {Passed=true,Checks=checks,PreviewSnapshotAndNormalizeMs=elapsed,TimingEvidence="Single synthetic offline sample; not a 30 Hz or hardware performance claim",Hardware="NOT TESTED"};
    }
}
