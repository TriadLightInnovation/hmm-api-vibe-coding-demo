using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HmmExample;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        RollingDiagnosticLog? launchLog=null;
        try
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            if (args.Length > 0)
            {
                if(args[0]=="--memory-tests" && args.Length==3)
                {
                    try {File.WriteAllText(args[1],JsonSerializer.Serialize(MemoryOfflineTests.Run(args[2]),Diagnostics.JsonOptions));return 0;}
                    catch(Exception error) {File.WriteAllText(args[1],error.ToString());return 1;}
                }
                if(args[0]=="--preview-tests" && args.Length==3)
                {
                    try {File.WriteAllText(args[1],JsonSerializer.Serialize(PreviewOfflineTests.Run(args[2]),Diagnostics.JsonOptions));return 0;}
                    catch(Exception error) {File.WriteAllText(args[1],error.ToString());return 1;}
                }
                if(args[0]=="--adc-tests" && args.Length==3)
                {
                    try {File.WriteAllText(args[1],JsonSerializer.Serialize(AdcOfflineTests.Run(args[2]),Diagnostics.JsonOptions));return 0;}
                    catch(Exception error) {File.WriteAllText(args[1],error.ToString());return 1;}
                }
                if(args[0]=="--external-readiness-tests" && args.Length==3)
                {
                    try {File.WriteAllText(args[1],JsonSerializer.Serialize(ExternalReadinessOfflineTests.Run(args[2]),Diagnostics.JsonOptions));return 0;}
                    catch(Exception error) {File.WriteAllText(args[1],error.ToString());return 1;}
                }
                if(args[0]=="--averaging-tests" && args.Length==3)
                {
                    try {File.WriteAllText(args[1],JsonSerializer.Serialize(AveragingOfflineTests.Run(args[2]),Diagnostics.JsonOptions));return 0;}
                    catch(Exception error) {File.WriteAllText(args[1],error.ToString());return 1;}
                }
                if(args[0]=="--setparam-abrupt-probe" && args.Length==3) return ReinitSetParametersOfflineTests.AbruptProbe(args[1],args[2]);
                if(args[0]=="--setparam-tests" && args.Length==3)
                {
                    try {File.WriteAllText(args[1],JsonSerializer.Serialize(ReinitSetParametersOfflineTests.Run(args[2]),Diagnostics.JsonOptions));return 0;}
                    catch(Exception error) {File.WriteAllText(args[1],error.ToString());return 1;}
                }
                if(args[0]=="--reinit-abrupt-probe" && args.Length==2) return ReinitNoPrepareOfflineTests.AbruptProbe(args[1]);
                if(args[0]=="--reinit-tests" && args.Length==3)
                {
                    try {File.WriteAllText(args[1],JsonSerializer.Serialize(ReinitNoPrepareOfflineTests.Run(args[2]),Diagnostics.JsonOptions));return 0;}
                    catch(Exception error) {File.WriteAllText(args[1],error.ToString());return 1;}
                }
                if(args[0]=="--durable-abrupt-probe" && args.Length==2) return DurableDiagnosticsOfflineTests.AbruptProbe(args[1]);
                if(args[0]=="--durable-tests" && args.Length==3)
                {
                    try {File.WriteAllText(args[1],JsonSerializer.Serialize(DurableDiagnosticsOfflineTests.Run(args[2]),Diagnostics.JsonOptions));return 0;}
                    catch(Exception error) {File.WriteAllText(args[1],error.ToString());return 1;}
                }
                if (args[0] == "--composite-tests" && args.Length == 3)
                {
                    try { File.WriteAllText(args[1], JsonSerializer.Serialize(CompositeOfflineTests.Run(args[2]), Diagnostics.JsonOptions)); return 0; }
                    catch (Exception error) { File.WriteAllText(args[1], error.ToString()); return 1; }
                }
                if (args[0] == "--lifecycle-tests" && args.Length == 3)
                {
                    try { File.WriteAllText(args[1], JsonSerializer.Serialize(LifecycleOfflineTests.Run(args[2]), Diagnostics.JsonOptions)); return 0; }
                    catch (Exception error) { File.WriteAllText(args[1], error.ToString()); return 1; }
                }
                if (args[0] == "--integrated-tests" && args.Length == 3)
                {
                    try { File.WriteAllText(args[1], JsonSerializer.Serialize(IntegratedOfflineTests.Run(args[2]), Diagnostics.JsonOptions)); return 0; }
                    catch (Exception error) { File.WriteAllText(args[1], error.ToString()); return 1; }
                }
                if (args[0] == "--frame-tests" && args.Length == 3)
                {
                    try { File.WriteAllText(args[1], JsonSerializer.Serialize(OfflineFrameTests.Run(args[2]), Diagnostics.JsonOptions)); return 0; }
                    catch (Exception error) { File.WriteAllText(args[1], error.ToString()); return 1; }
                }
                // All command-line modes are offline. No native library load or SDK invocation.
                if (args[0] == "--diagnostics" && args.Length == 2)
                {
                    var abi = Diagnostics.Abi();
                    var deployment = Diagnostics.Deployment(AppContext.BaseDirectory);
                    bool marshaling = Diagnostics.MarshalingCheck();
                    bool sdkLoaded = Diagnostics.SdkLoaded();
                    var report = new { Mode = "offline; no SDK loaded or invoked", X64 = Environment.Is64BitProcess, ABI = abi, MarshalingPassed = marshaling, SdkLoaded = sdkLoaded, Deployment = deployment };
                    File.WriteAllText(args[1], JsonSerializer.Serialize(report, Diagnostics.JsonOptions));
                    return abi.All(x => x.Passed) && marshaling && !sdkLoaded && deployment.Passed ? 0 : 1;
                }
                if (args[0] is "--ui-smoke" or "--ui-smoke-blocked" && args.Length == 2)
                {
                    var app = new Application();
                    var window = new MainWindow();
                    bool expectedEnabled = args[0] == "--ui-smoke";
                    if (window.ParametersButton.IsEnabled || window.StartButton.IsEnabled || window.StopButton.IsEnabled || window.InitializeButton.IsEnabled != expectedEnabled)
                        throw new InvalidOperationException("Phase 2A control gate failed.");
                    var root = (FrameworkElement)window.Content;
                    root.Measure(new Size(1200, 780)); root.Arrange(new Rect(0, 0, 1200, 780)); root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(1200, 780, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(args[1])) png.Save(file);
                    bool sdkLoaded = Diagnostics.SdkLoaded();
                    File.WriteAllText(args[1] + ".json", JsonSerializer.Serialize(new {
                        InitializeEnabled = window.InitializeButton.IsEnabled, ParametersEnabled = window.ParametersButton.IsEnabled,
                        StartEnabled = window.StartButton.IsEnabled, StopEnabled = window.StopButton.IsEnabled,
                        Status = window.SdkStatus.Text, Error = window.LastError.Text, SdkLoaded = sdkLoaded,
                        CurrentDirectory = Directory.GetCurrentDirectory(), Passed = !sdkLoaded
                    }, Diagnostics.JsonOptions));
                    window.Close(); app.Shutdown();
                    return sdkLoaded ? 1 : 0;
                }
                if (args[0] == "--check-deployment" && args.Length == 3)
                {
                    var report = Diagnostics.Deployment(args[1]);
                    File.WriteAllText(args[2], JsonSerializer.Serialize(report, Diagnostics.JsonOptions));
                    return report.Passed ? 0 : 1;
                }
                return 2; // Never fall through from an unknown diagnostic argument to the UI.
            }
            launchLog=new RollingDiagnosticLog();
            var application = new Application();
            using var exceptionDiagnostics=new ManagedExceptionDiagnostics(application,launchLog);
            return application.Run(new MainWindow(null,launchLog));
        }
        catch (Exception e)
        {
            try {launchLog?.Exception("Program.Main",e);} catch(Exception logError) {System.Diagnostics.Debug.WriteLine(logError);}
            if (args.Length == 0) MessageBox.Show(e.Message, "HMM deployment error", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally {launchLog?.FinishAsync().GetAwaiter().GetResult();}
    }
}
