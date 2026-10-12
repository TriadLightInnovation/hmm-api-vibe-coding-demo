using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HmmExample.Acquisition;
using HmmExample.Display;
using HmmExample.Interop;

namespace HmmExample;

internal static class OfflineFrameTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static CallbackBlock Block(int c, int startLine, int n, int rows, int width, long generation = 7)
    {
        int n0 = Math.Min(n, rows - startLine % rows), n1 = n - n0;
        return new(c, width, rows, n0, n1, Enumerable.Range(startLine * width, n * width).Select(x => (float)(c * 100000 + x)).ToArray(), startLine + n, generation);
    }
    internal static object Run(string pngPath)
    {
        var passed = new List<string>();
        VerifyProgressiveSnapshots();
        passed.Add("Progressive full-size snapshot; black untouched rows; split-frame clearing; immutable completed frame; snapshot isolation");
        foreach (int n in new[] { 16, 32 })
        {
            int rows = n + 4, width = 3;
            var pipeline = new FramePipeline(new(width, rows - 2, 2, n, DummyPolicy.ConfirmedTrailingRows), 7, InitialAlignment.ConfirmedFrameStart);
            var retained = new List<(CompletedFrame Frame, float[] Copy)>();
            for (int step = 0; step < 9; step++) foreach (int c in new[] { 3, 0, 2, 1 })
            {
                pipeline.Accept(Block(c, step * n, n, rows, width));
                bool complete = (step + 1) * n / rows != step * n / rows;
                bool read = pipeline.TryRead(c, out var frame);
                Check(read == complete, "A partial frame was published or a complete frame was lost.");
                if (frame is not null)
                {
                    int start = ((step + 1) * n / rows - 1) * rows * width;
                    Check(frame.Data.SequenceEqual(Enumerable.Range(start, rows * width).Select(x => (float)(c * 100000 + x))), "Cross-frame pixels are misplaced.");
                    Check(frame.FrameSequence == (step + 1) * n / rows && frame.SessionGeneration == 7, "Frame sequence/session incorrect.");
                    retained.Add((frame, (float[])frame.Data.Clone()));
                }
                Check(pipeline.Assemblers[c].Snapshot().AccumulatedLines == (step + 1) * n % rows, "Next-frame partial data lost.");
            }
            Check(retained.All(r => r.Frame.Data.SequenceEqual(r.Copy)), "Published array mutated after handoff.");
            passed.Add($"{n}-line blocks: crossing + independent interleaved channels + complete-only publishing + owned buffers");
        }
        var g = new FrameGeometry(2, 18, 2, 16, DummyPolicy.ConfirmedTrailingRows);
        var p = new FramePipeline(g, 7, InitialAlignment.ConfirmedFrameStart);
        p.Accept(Block(0, 0, 16, 20, 2));
        p.Accept(Block(0, 16, 16, 20, 2, 6));
        Check(p.Assemblers[0].Snapshot().AccumulatedLines == 16 && !p.TryRead(0, out _), "Stale session altered current partial.");
        p.Accept(Block(0, 16, 16, 20, 2));
        Check(p.TryRead(0, out var first) && first!.Data.Length == 40, "Completion after stale callback failed.");
        p.Accept(new(0, 2, 20, 1, 1, new float[4], 1, 7)); // Split before actual end: reject.
        Check(!p.Assemblers[0].Snapshot().Synchronized && !p.TryRead(0, out _), "Malformed split should invalidate partial.");
        p.Accept(Block(0, 16, 16, 20, 2)); // Unknown prefix dropped; 12 next-frame lines retained.
        p.Accept(Block(0, 32, 16, 20, 2));
        Check(p.TryRead(0, out var resynced) && resynced!.Data.SequenceEqual(Enumerable.Range(40, 40).Select(x => (float)x)), "Boundary resynchronization failed.");
        passed.Add("Stale generation rejection; malformed split invalidation; explicit-boundary resynchronization");
        foreach (var bad in new[] {
            Block(0,0,16,20,2) with { Width = 3 },
            Block(0,0,16,20,2) with { Height = 19 },
            Block(0,0,16,20,2) with { Index0Lines = -1 },
            Block(0,0,16,20,2) with { Data = new float[31] } })
        {
            var invalid = new FramePipeline(g, 7, InitialAlignment.ConfirmedFrameStart);
            invalid.Accept(bad);
            Check(invalid.Assemblers[0].Snapshot().RejectedBlocks == 1 && !invalid.TryRead(0, out _), "Invalid metadata was accepted.");
        }
        passed.Add("Width/height/count/negative-line metadata rejected before assembly");

        var aligned = new FramePipeline(new(2, 32, 0, 16, DummyPolicy.Unconfirmed), 7, InitialAlignment.ConfirmedFrameStart);
        for (int i = 0; i < 6; i++) aligned.Accept(Block(0, i * 16, 16, 32, 2));
        Check(aligned.TryRead(0, out var latest) && latest!.FrameSequence == 3 && !aligned.TryRead(0, out _), "Bounded queue must keep latest complete frame.");
        var unknown = new FramePipeline(g, 7, InitialAlignment.WaitForSplitBoundary);
        unknown.Accept(Block(0, 0, 16, 20, 2)); Check(!unknown.TryRead(0, out _), "Unknown session start published partial.");
        unknown.Accept(Block(0, 16, 16, 20, 2)); unknown.Accept(Block(0, 32, 16, 20, 2));
        Check(unknown.TryRead(0, out var anchored) && anchored!.Data[0] == 40, "Unknown-start anchor failed.");
        passed.Add("Exact boundary without index1; bounded latest-frame queue; unknown-start synchronization");

        var concurrent = new FramePipeline(g, 7, InitialAlignment.ConfirmedFrameStart);
        Parallel.For(0, 4, c => { for (int i = 0; i < 100; i++) concurrent.Accept(Block(c, i * 16, 16, 20, 2)); });
        for (int c = 0; c < 4; c++) Check(concurrent.Assemblers[c].Snapshot().CompletedFrames == 80 && concurrent.TryRead(c, out _), "Concurrent channels lost frames.");
        passed.Add("Four parallel channel producers: 80 completed frames each");

        var native = new FramePipeline(new(2, 16, 0, 16, DummyPolicy.Unconfirmed), 7, InitialAlignment.ConfirmedFrameStart);
        IntPtr pixels = Marshal.AllocHGlobal(128), metadata = Marshal.AllocHGlobal(Marshal.SizeOf<CResampledImage>());
        try
        {
            Marshal.Copy(Enumerable.Range(0, 32).Select(x => (float)x).ToArray(), 0, pixels, 32);
            Marshal.StructureToPtr(new CResampledImage { channel = 2, width = 2, height = 16, index0_line_number = 16, image_data = pixels, image_size = 32 }, metadata, false);
            native.AcceptNative(metadata, 7);
            Marshal.Copy(new float[32], 0, pixels, 32);
        }
        finally { Marshal.FreeHGlobal(metadata); Marshal.FreeHGlobal(pixels); }
        Check(native.TryRead(2, out var copied) && copied!.Data[31] == 31, "Native pointer was retained instead of copied.");
        passed.Add("Synthetic unmanaged memory copied before callback return; no native library invoked");

        var cropFrame = new CompletedFrame(0, 2, 3, [0, 1, 2, 3, 1000, 2000], 1, 1, 7);
        var cropGeometry = new FrameGeometry(2, 2, 1, 16, DummyPolicy.ConfirmedTrailingRows);
        Check(FrameRenderer.Normalize(cropFrame, cropGeometry).SequenceEqual(new byte[] { 0, 85, 170, 255 }), "Dummy row leaked into auto contrast.");
        bool blocked = false;
        try { FrameRenderer.Normalize(cropFrame, cropGeometry with { DummyPolicy = DummyPolicy.Unconfirmed }); }
        catch (InvalidOperationException) { blocked = true; }
        Check(blocked, "Unconfirmed Dummy crop should be blocked.");
        Check(FrameRenderer.Normalize(new(0, 2, 2, [float.NaN, float.PositiveInfinity, 2, 2], 1, 1, 7), new(2, 2, 0, 16, DummyPolicy.Unconfirmed)).All(x => x == 0), "Flat/non-finite pixels not handled.");
        passed.Add("Crop only after acquisition completion; Dummy excluded from contrast; unconfirmed crop blocked; finite/flat normalization");

        var app = new Application();
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        var sizes = new List<object>();
        foreach (var size in new[] { new Size(1200,780), new Size(1600,780), new Size(1060,900) })
        {
            root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
            double side = window.Channel1.ActualWidth;
            Check(side > 0 && window.Viewports.All(v => Math.Abs(v.ActualWidth - side) < .01 && Math.Abs(v.ActualHeight - side) < .01), "Square layout failed after resize.");
            sizes.Add(new { Width = size.Width, Height = size.Height, ViewportSide = side });
        }
        var ui = new FramePipeline(new(40, 20, 0, 16, DummyPolicy.Unconfirmed), 7, InitialAlignment.ConfirmedFrameStart);
        window.AttachDisplayPipeline(ui);
        ui.Accept(Block(0, 0, 16, 20, 40)); window.RefreshFrames();
        Check(window.Channel1.Image.Source is null, "Partial frame replaced placeholder.");
        ui.Accept(Block(0, 16, 16, 20, 40)); window.RefreshFrames();
        var lastImage = window.Channel1.Image.Source;
        ui.Accept(Block(0, 32, 4, 20, 40)); window.RefreshFrames();
        Check(ReferenceEquals(lastImage, window.Channel1.Image.Source), "Partial frame replaced previous complete image.");
        var next = new FramePipeline(ui.Geometry, 8, InitialAlignment.ConfirmedFrameStart);
        window.AttachDisplayPipeline(next);
        ui.Accept(Block(0, 36, 16, 20, 40)); window.RefreshFrames();
        Check(ReferenceEquals(lastImage, window.Channel1.Image.Source), "Old session queue reached UI.");
        // Rectangular synthetic images for visual Uniform-fit evidence, never SDK acquisition.
        var landscape = new CompletedFrame(0, 80, 40, Enumerable.Range(0, 3200).Select(x => (float)(x % 80)).ToArray(), 1, 1, 8);
        var portrait = new CompletedFrame(1, 40, 80, Enumerable.Range(0, 3200).Select(x => (float)(x / 40)).ToArray(), 1, 1, 8);
        window.Channel1.Present(FrameRenderer.Render(landscape, new(80,40,0,16,DummyPolicy.Unconfirmed)));
        window.Channel2.Present(FrameRenderer.Render(portrait, new(40,80,0,16,DummyPolicy.Unconfirmed)));
        window.SdkStatus.Text = "SDK Status: OFFLINE SYNTHETIC DISPLAY TEST · SDK not loaded";
        root.Measure(new Size(1200,780)); root.Arrange(new Rect(0,0,1200,780)); root.UpdateLayout();
        Check(window.Viewports.All(v => v.Image.Stretch == Stretch.Uniform), "Image must use Uniform.");
        Check(Math.Abs(window.Channel1.Image.RenderSize.Width / window.Channel1.Image.RenderSize.Height - 2) < .01
            && Math.Abs(window.Channel2.Image.RenderSize.Width / window.Channel2.Image.RenderSize.Height - .5) < .01, "Rendered image aspect ratio was distorted.");
        var bitmap = new RenderTargetBitmap(1200,780,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(pngPath)) encoder.Save(file);
        Check(!window.StartButton.IsEnabled && !window.ParametersButton.IsEnabled && !window.StopButton.IsEnabled, "Phase gate changed.");
        // Offscreen fixtures have no running Dispatcher loop / synchronization context.
        // Drain the managed log before synchronous teardown instead of async-void OnClosing.
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
        var fixtureLog=(RollingDiagnosticLog)typeof(MainWindow).GetField("log",flags)!.GetValue(window)!;
        fixtureLog.FinishAsync().GetAwaiter().GetResult();
        typeof(MainWindow).GetField("allowClose",flags)!.SetValue(window,true);
        window.Close(); app.Shutdown();
        Check(!Diagnostics.SdkLoaded(), "SDK loaded during offline tests.");
        passed.Add("WPF three-size square layout; landscape/portrait Uniform ratios; complete-only UI; old-session UI isolation; scan controls disabled");
        return new { Passed = true, Tests = passed, Layouts = sizes, SdkLoaded = false };
    }
    // Explicit offline CLI only. Merely building the application never runs this fixture.
    private static void VerifyProgressiveSnapshots()
    {
        foreach(int blockSize in new[] { 16,32 })
        {
            int rows=blockSize+4, width=2;
            var pipeline=new FramePipeline(new(width,rows-2,2,blockSize,DummyPolicy.RawIncludesOverscan),7,InitialAlignment.ConfirmedFrameStart);
            var assembler=pipeline.Assemblers[0];
            var buffer=new float[rows*width];
            pipeline.Accept(Block(0,0,blockSize,rows,width));
            Check(!pipeline.TryRead(0,out _),"Partial block must not publish CompletedFrame.");
            Check(assembler.TryCopyLive(0,buffer,out var partial),"Live preview must exist before first complete frame.");
            Check(partial!.Height==rows && partial.AccumulatedLines==blockSize && partial.Data.Skip(blockSize*width).All(v=>v==0),"Partial preview must keep full dimensions with black untouched rows.");
            var firstSnapshot=(float[])buffer.Clone();
            long version=partial.Version;
            pipeline.Accept(Block(0,blockSize,blockSize,rows,width));
            Check(buffer.SequenceEqual(firstSnapshot),"Producer mutated UI snapshot.");
            Check(pipeline.TryRead(0,out var complete) && complete!.Data.SequenceEqual(Enumerable.Range(0,rows*width).Select(x=>(float)x)),"Split completion lost pixels.");
            var immutableCopy=(float[])complete!.Data.Clone();
            Check(assembler.TryCopyLive(version,buffer,out var next) && next!.AccumulatedLines==blockSize-4,"Live must reflect next frame after split.");
            Check(buffer.Take((blockSize-4)*width).SequenceEqual(Enumerable.Range(rows*width,(blockSize-4)*width).Select(x=>(float)x))
                && buffer.Skip((blockSize-4)*width).All(x=>x==0),"New frame must clear old rows before copying its prefix.");
            pipeline.Accept(Block(0,blockSize*2,blockSize,rows,width));
            Check(complete.Data.SequenceEqual(immutableCopy),"Live/assembler mutated published CompletedFrame.");
        }
        var negative=new LiveFrameSnapshot(0,2,3,[-4,-3,0,0,0,0],1,1,0,1,7);
        Check(FrameRenderer.NormalizeLive(negative).SequenceEqual(new byte[] {0,255,0,0,0,0}),"Unacquired zeros must remain black under negative-value contrast.");
    }
}
