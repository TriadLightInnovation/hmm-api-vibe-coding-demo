using System.Text.Json;
using HmmExample.Interop;

namespace HmmExample.Acquisition;

internal sealed record ExternalParameters(WaveParams Wave, float Ao2Amplitude, float Ao2Offset, bool Aux, int Iterations)
{
    public ScanRequest? Requested { get; init; }
    internal int LineFactor => Wave.line_mode && !Wave.external_line_trigger ? Requested?.LineFactor ?? 1 : 1;
    internal int OutputHeight => Wave.line_per_frame / LineFactor;
    internal int OutputDummy => Wave.oversample_frame / LineFactor;
    internal ExternalParameters Apply(ScanRequest request)
    {
        request.Validate();
        var wave=Wave;
        wave.pixel_per_line=request.XPixel; wave.line_per_frame=checked(request.YPixel*request.LineFactor);
        wave.oversample_head=request.OversamplePixel; wave.oversample_tail=request.OversamplePixel;
        wave.oversample_frame=checked(request.OversampleFrame*request.LineFactor);
        wave.number_of_lines=request.InternalMode ? checked(request.NumberOfLines*request.LineFactor) : Wave.number_of_lines;
        wave.line_average=1; wave.frame_average=1;
        wave.external_line_trigger=!request.InternalMode; wave.line_mode=request.InternalMode;
        if(request.InternalMode) { wave.line_frequency_hz=request.FrequencyHz; }
        return this with { Wave=wave, Requested=request };
    }
    internal static ExternalParameters LoadRequested(ScanRequest request) => LoadBaseline().Apply(request);
    internal static ExternalParameters LoadBaseline()
    {
        if (!Diagnostics.Deployment(AppContext.BaseDirectory).Passed) throw new InvalidOperationException("Runtime compatibility or configuration verification failed; inspect deployment diagnostics.");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "io_settings.json")));
        var json = document.RootElement;
        float F(string key) => json.GetProperty(key).GetSingle();
        int I(string key) => json.GetProperty(key).GetInt32();
        bool B(string key) => json.GetProperty(key).GetBoolean();
        var wave = new WaveParams
        {
            ao0_amplitude=F("ao0 amplitude"), ao1_amplitude=F("ao1 amplitude"), ao3_amplitude=F("ao3 amplitude"),
            ao0_offset=F("ao0 offset"), ao1_offset=F("ao1 offset"), ao3_offset=F("ao3 offset"),
            ao3_static_mode=B("ao3 static mode"), signal_invert=B("signal invert"),
            line_mode=B("line mode"), external_line_trigger=B("external line trigger"),
            number_of_lines=I("number of lines"), channel_start_delay=I("channel start delay"),
            line_frequency_hz=I("line frequency hz"), pixel_per_line=I("pixel per line"), line_per_frame=I("line per frame"),
            steps_per_stack=I("steps per stack"), scan_dir=B("scan dir"), trig0_mode=B("trig0 mode"),
            line_average=I("line average"), frame_average=I("frame average"),
            oversample_head=I("oversample head"), oversample_tail=I("oversample tail"),
            oversample_frame=I("oversample frame"), oversample_stack=I("oversample stack"),
            channel0_enable=B("channel0 enable"), channel1_enable=B("channel1 enable"),
            channel2_enable=B("channel2 enable"), channel3_enable=B("channel3 enable")
        };
        // Apply(ScanRequest) is the only override path. Original JSON is never written.
        if (wave.pixel_per_line <= 0 || wave.line_per_frame <= 0 || wave.oversample_frame < 0
            || (long)wave.line_per_frame + wave.oversample_frame > int.MaxValue)
            throw new InvalidOperationException("Invalid baseline geometry / Int32 field range.");
        GeometryMath.Pixels(wave.pixel_per_line,wave.line_mode?wave.number_of_lines:checked(wave.line_per_frame+wave.oversample_frame));
        return new(wave, F("ao2 amplitude"), F("ao2 offset"), B("aux enable"), I("iteration number"));
    }
    internal string Describe(bool sent, int? requestedFrequency = null, bool prepareRequired = false) =>
        $"{(prepareRequired ? "Parameters changed — Needs Prepare; previous WaveParams below have not been resent" : sent ? "Sent by setParameters (not hardware readback)" : "Runtime preview — not sent")}\n" +
        $"Requested Y Pixel: {Requested?.YPixel}\nRequested Oversample Frame: {Requested?.OversampleFrame}\nRequested Number Of Lines: {Requested?.NumberOfLines}\nManaged Line Average: {(Wave.line_mode && !Wave.external_line_trigger && Requested?.LineAverage==true ? "ON" : "OFF / not applicable")} N={LineFactor}\nEffective SDK line_per_frame: {Wave.line_per_frame}\nEffective SDK oversample_frame: {Wave.oversample_frame}\nEffective SDK number_of_lines: {Wave.number_of_lines}\nWaveParams.line_average: {Wave.line_average}\nWaveParams.frame_average: {Wave.frame_average}\n" +
        $"Scan Frequency Requested: {(Wave.line_mode ? requestedFrequency?.ToString() ?? "invalid / not entered" : "N/A (External Trigger)")} Hz (full cycle)\n" +
        $"WaveParams.line_frequency_hz: {Wave.line_frequency_hz} Hz\n" +
        $"External Trigger: {Wave.external_line_trigger}\nLine Mode: {Wave.line_mode}\n" +
        $"Pixel Per Line: {Wave.pixel_per_line}\nLine Per Frame: {Wave.line_per_frame}\nNumber Of Lines: {Wave.number_of_lines}\n" +
        $"Oversample Frame: {Wave.oversample_frame}\nCH0 Enable (display CH1): {Wave.channel0_enable}\nCH1 Enable (display CH2): {Wave.channel1_enable}\n" +
        $"CH2 Enable (display CH3): {Wave.channel2_enable}\nCH3 Enable (display CH4): {Wave.channel3_enable}\nIteration Number: {Iterations}\n" +
        $"Configured frequency: {Wave.line_frequency_hz} Hz\nAO2 amplitude / offset: {Ao2Amplitude} / {Ao2Offset}\nAUX: {Aux}\n\n" +
        JsonSerializer.Serialize(Wave, new JsonSerializerOptions { IncludeFields=true, WriteIndented=true });
}
