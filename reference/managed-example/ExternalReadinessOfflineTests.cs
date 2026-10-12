using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using HmmExample.Acquisition;
using HmmExample.Interop;

namespace HmmExample;

internal static class ExternalReadinessOfflineTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Finish(Task task) => task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    private static void Refresh(MainWindow window) => typeof(MainWindow).GetMethod("RefreshSession", Private)!.Invoke(window, null);
    private static void Close(MainWindow window)
    {
        typeof(MainWindow).GetField("allowClose", Private)!.SetValue(window, true);
        window.Close();
    }
    private sealed class FakeApi : IHmmApi
    {
        internal readonly ReinitNoPrepareOfflineTests.FakeApi Inner = new();
        internal FrequencyParams Frequency = new() { line_frequency_hz = 400, pos_time = 1250, neg_time = 1250 };
        internal bool Ao2Result = true;
        internal float Amplitude, Offset;
        internal long Ao2Returned, FrequencyEntered;
        public void EnsureAvailable() => Inner.EnsureAvailable();
        public bool Initialize() => Inner.Initialize();
        public void SetParameters(ref WaveParams wave, int iterations) => Inner.SetParameters(ref wave, iterations);
        public bool SetAo2Value(float amplitude, float offset)
        {
            Amplitude = amplitude; Offset = offset; Inner.Calls.Add("setAo2Value");
            Ao2Returned = Stopwatch.GetTimestamp(); return Ao2Result;
        }
        public void GetFrequency(out FrequencyParams frequency)
        {
            FrequencyEntered = Stopwatch.GetTimestamp(); Inner.Calls.Add("getFrequency"); frequency = Frequency;
        }
        public void SetAux(bool value) => Inner.SetAux(value);
        public bool StartScan() => Inner.StartScan();
        public void StopScan() => Inner.StopScan();
        public void SetImageCallback(HmmNative.ImageCallback callback) => Inner.SetImageCallback(callback);
        public void SetErrorCallback(HmmNative.ErrorCallback callback) => Inner.SetErrorCallback(callback);
    }
    internal static object Run(string directory)
    {
        Directory.CreateDirectory(directory);
        NativeLibrary.SetDllImportResolver(typeof(HmmNative).Assembly, (_, _, _) => throw new InvalidOperationException("Native SDK forbidden in offline tests"));
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        var baseline = ExternalParameters.LoadBaseline();
        var internalRequest = ScanRequest.Default with { XPixel = 4, YPixel = 64, OversamplePixel = 2, OversampleFrame = 3, LineAverage = true, AverageLines = 3 };
        var internalWave = baseline.Apply(internalRequest).Wave;
        Check(internalWave.line_per_frame == 192 && internalWave.oversample_frame == 9 && internalWave.number_of_lines == 96, "Internal N=3 retains scaled raw geometry");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var log = new RollingDiagnosticLog(Path.Combine(directory, "valid"));
        var api = new FakeApi();
        var session = new SdkSession(log, api, baseline.Apply);
        var window = new MainWindow(session, log);
        window.XPixel.Text = "4"; window.YPixel.Text = "64"; window.OversamplePixel.Text = "2"; window.OversampleFrame.Text = "3";
        window.LineAverageEnabled.IsChecked = true; window.AverageLines.Text = "3";
        window.FrameAverageEnabled.IsChecked = true;
        window.TriggerMode.SelectedIndex = 0; Refresh(window);
        Check(window.LineAverageEnabled.IsChecked == false && !window.LineAverageEnabled.IsEnabled && !window.AverageLines.IsEnabled,
            "Switch External clears checkbox and disables both line-average controls");
        Check(window.FrameAverageEnabled.IsChecked == true && window.FrameAverageEnabled.IsEnabled, "Frame Average remains available and selected");
        Check(session.Snapshot().Requested is { LineAverage: false, LineFactor: 1 }, "External request has no residual line average");
        Check(window.MeasuredFrequency.Text == "—" && window.ExternalDwell.Text == "—" && window.ExternalFrameTime.Text == "—" && window.ExternalFrameRate.Text == "—", "External timing blank before Prepare");
        window.TriggerMode.SelectedIndex = 1; Refresh(window);
        Check(window.LineAverageEnabled.IsChecked == false && window.LineAverageEnabled.IsEnabled && window.AverageLines.IsEnabled, "Return Internal enables controls without restoring ON");
        window.TriggerMode.SelectedIndex = 0;
        Finish(session.InitializeAsync()); Refresh(window);
        Check(api.Inner.Calls.SequenceEqual(new[] { "EnsureAvailable(fake)", "initialize", "setErrorMessageCallback", "setResampledImageCallback" }), "Initialize then callbacks; no configuration before Prepare");
        api.Inner.Calls.Clear();
        Finish(session.PrepareAsync()); Refresh(window);
        Check(api.Inner.Calls.SequenceEqual(new[] { "setErrorMessageCallback", "setResampledImageCallback", "setAo2Value", "getFrequency", "setAux", "setParameters" }), "Prepare refreshes callbacks then AO2, frequency, AUX, parameters in exact order");
        var prepared = session.Snapshot(); var wave = api.Inner.Wave;
        Check(api.Amplitude == 2 && api.Offset == 0 && api.Amplitude == baseline.Ao2Amplitude && api.Offset == baseline.Ao2Offset, "AO2 amplitude 2.0 / offset 0 from runtime baseline JSON");
        Check(prepared.Timings["Resonant Wait Requested"] == 1000 && prepared.Timings["Resonant Wait Actual"] >= 990 && Stopwatch.GetElapsedTime(api.Ao2Returned, api.FrequencyEntered).TotalMilliseconds >= 990, "Real managed stabilization wait precedes fake getFrequency: requested 1000 ms");
        Check(new[] { "Initialize API Time", "Set AO2 API Time", "Resonant Wait Requested", "Resonant Wait Actual", "Get Frequency API Time", "Set AUX API Time", "Set Parameters API Time" }.All(prepared.Timings.ContainsKey), "All seven requested API/wait timings retained");
        Check(wave.external_line_trigger && !wave.line_mode && wave.line_average == 1 && wave.frame_average == 1, "External flags and native averages fixed correctly");
        Check(wave.line_per_frame == 64 && wave.oversample_frame == 3 && wave.number_of_lines == baseline.Wave.number_of_lines && wave.number_of_lines == 200, "External geometry unscaled and callback block restored to baseline 200");
        Check(wave.line_frequency_hz == 8000 && prepared.Frequency?.line_frequency_hz == 400, "Measured frequency does not overwrite configured WaveParams frequency");
        Check(window.StartButton.IsEnabled && window.MeasuredFrequency.Text == "400.000 Hz" && window.ResonantPhaseTimes.Text.Contains("1250") && window.ExternalFrameTime.Text == "167.500 ms", "Only successful Prepare exposes measured timing and enables Start");
        var calculated = ScanTimingCalculator.External(wave, 400)!;
        double minimum = Enumerable.Range(0, 8).Min(i => (Math.Acos(1 - 2.0 * (i + 1) / 8) - Math.Acos(1 - 2.0 * i / 8)) / (2 * Math.PI * 400));
        Check(Math.Abs(calculated.DwellUs - minimum * 1e6) < 1e-9 && Math.Abs(calculated.FrameRateHz - 400.0 / 67) < 1e-12, "Full sine boundaries and one-line-per-cycle frame formulas");
        Check(session.CurrentCapture is ExternalCapture && session.CurrentCapture.Parameters.LineFactor == 1, "External uses frame capture, never Internal line assembler/average");
        Finish(session.StartAsync()); api.Inner.Frame(0);
        Check(session.CurrentCapture!.TryTake(0, out var frame) && frame is { Width: 4, Height: 67 } && frame.Data[1] == 1, "Complete native-shaped buffer copied into owned managed frame before synthetic memory freed");
        Finish(session.StopAsync()); Close(window); Finish(log.FinishAsync());

        var invalid = new[] {
            new FrequencyParams {line_frequency_hz=0,pos_time=1,neg_time=1},
            new FrequencyParams {line_frequency_hz=-1,pos_time=1,neg_time=1},
            new FrequencyParams {line_frequency_hz=float.NaN,pos_time=1,neg_time=1},
            new FrequencyParams {line_frequency_hz=float.PositiveInfinity,pos_time=1,neg_time=1},
            new FrequencyParams {line_frequency_hz=400,pos_time=-1,neg_time=1},
            new FrequencyParams {line_frequency_hz=400,pos_time=1,neg_time=-1}
        };
        for (int i = 0; i < invalid.Length; i++)
        {
            var badLog = new RollingDiagnosticLog(Path.Combine(directory, "invalid-" + i));
            var badApi = new FakeApi { Frequency = invalid[i] };
            var badSession = new SdkSession(badLog, badApi, baseline.Apply);
            badSession.SetRequestedParameters(internalRequest with { InternalMode = false, LineAverage = false });
            Finish(badSession.InitializeAsync()); Finish(badSession.PrepareAsync()); Finish(badSession.StartAsync());
            var badWindow = new MainWindow(badSession, badLog); Refresh(badWindow);
            Check(badSession.Snapshot() is { State: ExternalState.PrepareFailed, ParametersSent: false } && !badWindow.StartButton.IsEnabled, "Invalid resonant timing blocks Start: " + i);
            Check(!badApi.Inner.Calls.Any(x => x is "setAux" or "setParameters" or "startScan"), "Invalid measurement aborts before AUX/parameters/Start: " + i);
            Check(badWindow.ExternalDwell.Text == "—" && badWindow.ExternalFrameTime.Text == "—" && badWindow.LastError.Text.Contains("Invalid resonant timing"), "Prepare failure has no Internal timing fallback: " + i);
            Finish(badSession.StopAsync()); Close(badWindow); Finish(badLog.FinishAsync());
        }
        var aoLog = new RollingDiagnosticLog(Path.Combine(directory, "ao2-false"));
        var aoApi = new FakeApi { Ao2Result = false }; var aoSession = new SdkSession(aoLog, aoApi, baseline.Apply);
        aoSession.SetRequestedParameters(internalRequest with { InternalMode = false });
        Finish(aoSession.InitializeAsync()); Finish(aoSession.PrepareAsync());
        Check(aoSession.Snapshot().State == ExternalState.PrepareFailed && !aoApi.Inner.Calls.Any(x => x is "getFrequency" or "setAux" or "setParameters"), "AO2 false stops pathway without fallback");
        Finish(aoSession.StopAsync()); Finish(aoLog.FinishAsync());
        application.Shutdown(); Check(!Diagnostics.SdkLoaded(), "Native SDK stayed unloaded; no hardware operations");
        return new { Passed = true, Checks = checks, Hardware = "NOT OPERATED", WaitActualMs = prepared.Timings["Resonant Wait Actual"] };
    }
}
