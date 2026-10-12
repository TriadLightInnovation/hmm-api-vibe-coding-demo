using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HmmExample.Acquisition;
using HmmExample.Display;
using HmmExample.Interop;

namespace HmmExample;

internal static class CompositeOfflineTests
{
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static byte[] Pixels(WriteableBitmap bitmap)
    {
        int stride=bitmap.PixelWidth*bitmap.Format.BitsPerPixel/8;
        byte[] bytes=new byte[stride*bitmap.PixelHeight];bitmap.CopyPixels(bytes,stride,0);return bytes;
    }
    internal static object Run(string imagePrefix)
    {
        NativeLibrary.SetDllImportResolver(typeof(HmmNative).Assembly,(_,_,_)=>throw new InvalidOperationException("Native SDK forbidden in Composite fixture"));
        var checks=new List<string>();
        void Check(bool pass,string message) {if(!pass) throw new InvalidOperationException(message);checks.Add(message);}
        Check(!Diagnostics.SdkLoaded(),"Native SDK unloaded before Composite tests");
        var app=new Application();
        var model=new CompositeDisplay(app.Dispatcher);
        void Select(params int[] channels) {for(int c=0;c<4;c++) model.SetChannel(c,channels.Contains(c),RgbColor.Presets[c]);}
        byte[][] gray=[new byte[]{0,128,255},new byte[]{255,128,0},new byte[]{255,255,255},new byte[]{128,128,128}];
        for(int c=0;c<4;c++) model.Submit(c,new(3,1,gray[c],1,1,DisplaySource.Live));
        Select(0);model.Refresh();
        Check(Pixels(model.Image!).SequenceEqual(new byte[]{0,0,0,255,0,128,0,255,0,255,0,255}),"CH1 only = Green using exact display-normalized intensity");
        Select(1);model.Refresh();
        Check(Pixels(model.Image!).SequenceEqual(new byte[]{0,0,255,255,0,0,128,255,0,0,0,255}),"CH2 only = Red");
        Select(0,1);model.Refresh();
        Check(Pixels(model.Image!).SequenceEqual(new byte[]{0,0,255,255,0,128,128,255,0,255,0,255}),"CH1 + CH2 additive Green/Red blend");
        Select(0,1,2,3);model.Refresh();
        Check(Pixels(model.Image!).SequenceEqual(new byte[]{255,128,255,255,255,255,255,255,255,255,128,255}),"All channels blend and clamp at 255 without wrapping");
        long calculations=model.Snapshot().RgbCalculations;
        Select();model.Refresh();
        Check(model.Image is null && model.Status=="No channels selected" && model.Snapshot().RgbCalculations==calculations,"No channel selected: explicit status, no RGB calculation");
        Select(0);model.SetChannel(0,true,new RgbColor("Custom test RGB",127,64,255));model.Refresh();
        var custom=Pixels(model.Image!);
        Check(custom[8]==255 && custom[9]==64 && custom[10]==127 && custom[4]==128 && custom[5]==32 && custom[6]==64,"RGB coefficients use 0-255 scale consistently and round once");
        model.SetEnabled(false);model.Refresh();calculations=model.Snapshot().RgbCalculations;
        model.Submit(0,new(3,1,new byte[]{255,0,128},1,2,DisplaySource.Live));model.Refresh();
        Check(model.Image is null && model.Status=="Disabled" && model.Snapshot().RgbCalculations==calculations,"Master disabled skips RGB processing even while source changes");
        model.SetEnabled(true);model.Refresh();Check(Pixels(model.Image!)[0]==255,"Re-enable uses newest cached source, no acquisition needed");
        model.SetChannel(1,true,RgbColor.Presets[1]);model.Submit(1,new(2,2,new byte[4],1,2,DisplaySource.Live));calculations=model.Snapshot().RgbCalculations;model.Refresh();
        Check(model.Image is null && model.Status=="geometry mismatch" && model.Snapshot().RgbCalculations==calculations,"Mismatched selected geometry blocks RGB instead of resizing");
        model.SetChannel(1,false,RgbColor.Presets[1]);model.Refresh();Check(model.Image is not null,"Unchecked mismatched channel does not block selected channels");
        model.BeginGeneration(2);model.Submit(0,new(3,2,new byte[]{0,128,255,0,0,0},2,1,DisplaySource.Live));model.Refresh();
        Check(model.Image is {PixelWidth:3,PixelHeight:2} && Pixels(model.Image!).Skip(12).Where((_,i)=>i%4!=3).All(x=>x==0),"Progressive partial buffer composites at full raw dimensions with black unacquired rows");
        long updates=model.Snapshot().DisplayedUpdates;
        model.Submit(0,new(3,2,new byte[]{0,128,255,50,100,200},2,2,DisplaySource.Live));model.Refresh();
        Check(model.Snapshot().DisplayedUpdates==updates+1 && Pixels(model.Image!).Skip(12).Where((_,i)=>i%4!=3).Any(x=>x!=0),"Partial-frame update appears before another complete frame");
        model.Submit(0,new(3,2,new byte[6],1,99,DisplaySource.Live));Check(!model.Refresh(),"Old generation display cache cannot overwrite current Composite");
        model.Submit(0,new(3,2,new byte[6],2,3,DisplaySource.Live));model.Submit(0,new(3,2,new byte[]{255,255,255,255,255,255},2,6,DisplaySource.Live));model.Refresh();
        Check(model.Snapshot().DroppedUiUpdates==3 && Pixels(model.Image!)[0]==255,"Latest-only source coalescing counts overwritten version plus missing UI versions");
        calculations=model.Snapshot().RgbCalculations;Check(!model.Refresh() && calculations==model.Snapshot().RgbCalculations,"Unchanged preview tick skips redundant RGB calculation");
        var missing=new CompositeDisplay(app.Dispatcher);missing.Submit(0,new(1,1,new byte[]{255},1,1,DisplaySource.Live));missing.Refresh();
        Check(Pixels(missing.Image!).SequenceEqual(new byte[]{0,255,0,255}) && missing.Status.Contains("missing channel data is black"),"Not-yet-received channels contribute black; progressive first channel need not wait");
        bool wrongThread=Task.Run(()=> {try {model.Refresh();return false;}catch(InvalidOperationException) {return true;}}).GetAwaiter().GetResult();
        Check(wrongThread,"Composite refresh enforces UI-thread ownership");
        // WPF integration with the real managed receive/assembler path; no SDK APIs invoked.
        var window=new MainWindow();
        var session=(SdkSession)typeof(MainWindow).GetField("session",Private)!.GetValue(window)!;
        var composite=(CompositeDisplay)typeof(MainWindow).GetField("composite",Private)!.GetValue(window)!;
        void Set(string field,object? value)=>typeof(SdkSession).GetField(field,Private)!.SetValue(session,value);
        void Refresh()=>typeof(MainWindow).GetMethod("RefreshSession",Private)!.Invoke(window,null);
        void Preview()=>typeof(MainWindow).GetMethod("RefreshPreview",Private)!.Invoke(window,null);
        var baseline=ExternalParameters.LoadBaseline();var original=session.Snapshot().Requested!;var prepared=baseline.Apply(original);
        Set("parameters",prepared);Set("preparedRequest",original);Set("parametersSent",true);Set("state",ExternalState.Prepared);Refresh();
        window.CompositeInclude3.IsChecked=false;window.CompositeColor1.SelectedIndex=5;window.CompositeEnabled.IsChecked=false;
        Check(session.Snapshot() is {State:ExternalState.Prepared,ParametersSent:true} && session.Snapshot().Requested==original && session.Snapshot().Parameters!.Wave.Equals(prepared.Wave)
            && window.StartButton.IsEnabled,"Include / color / master changes never modify acquisition parameters or invalidate Prepare");
        Check(composite.Snapshot().Channels[0].Color==RgbColor.Presets[5] && !composite.Snapshot().Channels[2].Include,"Independent per-channel UI Include and preset mapping");
        Set("state",ExternalState.RequiresInitialize);Set("parametersSent",false);
        var request=ScanRequest.Default with {XPixel=8,YPixel=30,OversampleFrame=4,NumberOfLines=16};
        session.SetRequestedParameters(request);var settings=baseline.Apply(request);
        typeof(SdkSession).GetMethod("CreateCapture",Private)!.Invoke(session,new object[]{settings});
        var capture=session.CurrentCapture!;long generation=session.Snapshot().Generation;capture.ArmStart();Set("state",ExternalState.Scanning);Refresh();
        void Emit(int c,int line)
        {
            float[] floats=Enumerable.Range(line*8,16*8).Select(i=>(float)(c*1000+i)).ToArray();
            IntPtr data=Marshal.AllocHGlobal(floats.Length*sizeof(float)),meta=Marshal.AllocHGlobal(Marshal.SizeOf<CResampledImage>());
            try
            {
                Marshal.Copy(floats,0,data,floats.Length);
                Marshal.StructureToPtr(new CResampledImage {channel=c,width=8,height=34,index0_line_number=16,image_data=data,image_size=floats.Length},meta,false);
                Task.Run(()=>capture.Receive(meta,generation,generation)).GetAwaiter().GetResult();
            }
            finally {Marshal.FreeHGlobal(meta);Marshal.FreeHGlobal(data);}
        }
        calculations=composite.Snapshot().RgbCalculations;
        for(int c=0;c<4;c++) Emit(c,0);
        Check(composite.Snapshot().RgbCalculations==calculations && window.Viewports.All(v=>v.Image.Source is null),"SDK-style callback worker does no grayscale/WPF/Composite processing");
        Preview();
        Check(window.Viewports.All(v=>v.Image.Source is WriteableBitmap {PixelWidth:8,PixelHeight:34}) && composite.Snapshot().RgbCalculations==calculations,"All four gray channels still update while master Composite is disabled");
        window.CompositeEnabled.IsChecked=true;window.CompositeColor1.SelectedIndex=0;window.CompositeInclude4.IsChecked=false;Preview();
        Check(composite.Image is {PixelWidth:8,PixelHeight:34} && capture.Snapshot().Channels.All(c=>c.CompletedFrames==0),"UI timer builds progressive Composite while all complete-frame counts are zero");
        var actual=Pixels(composite.Image!);var gray1=Pixels((WriteableBitmap)window.Channel1.Image.Source);var gray2=Pixels((WriteableBitmap)window.Channel2.Image.Source);
        Check(Enumerable.Range(0,gray1.Length).All(p=>actual[4*p]==0 && actual[4*p+1]==gray1[p] && actual[4*p+2]==gray2[p]),"Composite uses the exact CH1 / CH2 displayed normalization; unchecked CH3 / CH4 add nothing");
        var previousGray=window.Channel3.Image.Source;Emit(2,16);Preview();
        Check(!ReferenceEquals(previousGray,window.Channel3.Image.Source) && capture.Snapshot().Channels[2].DisplayedUpdates==2,"Unchecked CH3 continues grayscale processing and updates");
        window.CompositeInclude1.IsChecked=false;window.CompositeColor2.SelectedIndex=4;Preview();
        Check(session.Snapshot().State==ExternalState.Scanning && session.Snapshot().Requested==request && settings.Wave.channel0_enable && settings.Wave.channel2_enable,"Composite controls stay editable during Scanning without touching acquisition channel enables");
        Check(Pixels(composite.Image!)[4*127]==255 && Pixels(composite.Image!)[4*127+1]==255 && Pixels(composite.Image!)[4*127+2]==0,"Include/color change affects next preview refresh immediately (Cyan CH2)");
        window.CompositeEnabled.IsChecked=false;calculations=composite.Snapshot().RgbCalculations;Preview();
        Check(composite.Snapshot().RgbCalculations==calculations && window.CompositeViewport.Image.Source is null && window.Viewports.All(v=>v.Image.Source is not null),"Disabled status clears Composite only; gray images remain");
        typeof(SdkSession).GetMethod("ArchiveAndClear",Private)!.Invoke(session,null);Set("state",ExternalState.RequiresInitialize);Refresh();
        // Render visibly distinct synthetic channel patterns for layout/letterbox review.
        var includes=new[]{window.CompositeInclude1,window.CompositeInclude2,window.CompositeInclude3,window.CompositeInclude4};
        var colors=new[]{window.CompositeColor1,window.CompositeColor2,window.CompositeColor3,window.CompositeColor4};
        window.CompositeEnabled.IsChecked=true;
        for(int c=0;c<4;c++)
        {
            includes[c].IsChecked=true;colors[c].SelectedIndex=c;
            const int w=240,h=250;var pixels=new byte[w*h];
            double cx=c%2==0?88:152,cy=c<2?92:156;
            for(int y=0;y<h-10;y++)for(int x=0;x<w;x++) pixels[y*w+x]=(byte)Math.Clamp(255*Math.Exp(-((x-cx)*(x-cx)+(y-cy)*(y-cy))/1800.0),0,255);
            window.Viewports[c].Present(FrameRenderer.RenderGray(w,h,pixels),true);
            composite.Submit(c,new(w,h,pixels,100,1,DisplaySource.Complete));
        }
        Preview();window.SdkStatus.Text="OFFLINE COMPOSITE FIXTURE — synthetic normalized images; native SDK not loaded";
        foreach(var size in new[]{new Size(1480,1000),new Size(1160,860),new Size(1800,1000)})
        {
            var root=(FrameworkElement)window.Content;root.Measure(size);root.Arrange(new Rect(new Point(),size));root.UpdateLayout();
            var first=window.Viewports[0];
            Check(window.Viewports.All(v=>v.ActualWidth>0 && Math.Abs(v.ActualWidth-v.ActualHeight)<1e-6 && Math.Abs(v.ActualWidth-first.ActualWidth)<1e-6),$"Four equal square gray viewports retained at {size.Width}x{size.Height}");
            Check(Math.Abs(window.CompositeViewport.ActualWidth-window.CompositeViewport.ActualHeight)<1e-6 && window.CompositeViewport.Image.Stretch==Stretch.Uniform,$"Composite square / Uniform viewport at {size.Width}x{size.Height}");
            var rendered=window.CompositeViewport.Image.RenderSize;
            Check(Math.Abs(rendered.Width/rendered.Height-240.0/250)<0.001,$"Composite image aspect ratio preserved at {size.Width}x{size.Height}");
            var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(imagePrefix+$"-{size.Width}.png");png.Save(file);
        }
        var diagnostics=composite.Snapshot();
        Check(diagnostics.DisplayedUpdates>0 && window.CompositeViewport.RawCaption=="PROCESSED COMPOSITE - includes Dummy/Overscan","Composite diagnostics and processed overscan label retained");
        Check(!Diagnostics.SdkLoaded(),"Native SDK stayed unloaded for all Composite tests");
        var log=(RollingDiagnosticLog)typeof(MainWindow).GetField("log",Private)!.GetValue(window)!;log.FinishAsync().GetAwaiter().GetResult();
        typeof(MainWindow).GetField("allowClose",Private)!.SetValue(window,true);window.Close();app.Shutdown();
        return new {Passed=true,Mode="Offline UI/managed buffers only; no native SDK or hardware",Checks=checks,Composite=diagnostics,ImagePrefix=imagePrefix};
    }
}
