namespace HmmExample;

internal sealed record AdcCalibrationSnapshot(string Status, DateTimeOffset? Timestamp = null,
    bool? Result = null, double? ApiTimeMs = null, string? Error = null, long WarningsDuringCall = 0);

internal sealed partial class SdkSession
{
    private readonly SdkWarnings warnings = new();
    private long fatalErrorSequence;
    private AdcCalibrationSnapshot adcCalibration = new("Not Run");
    internal SdkWarningSnapshot WarningSnapshot() => warnings.Snapshot();
    internal void FlushWarnings(bool force = false) => warnings.Flush(log, force);
    internal AdcCalibrationSnapshot CalibrationSnapshot() { lock (gate) return adcCalibration; }
    internal bool CanAdcCalibration()
    {
        lock (gate) return busy == 0 && initializeResult == true &&
            state is ExternalState.Initialized or ExternalState.Prepared or ExternalState.PrepareRequired;
    }
    internal Task AdcCalibrationAsync()
    {
        if (!CanAdcCalibration()) return Task.CompletedTask;
        return Manual(() =>
        {
            var requestedAt = DateTimeOffset.Now;
            ExternalState previous; long beforeErrors = Interlocked.Read(ref fatalErrorSequence);
            long beforeWarnings = warnings.Snapshot().Total;
            lock (gate)
            {
                previous = state;
                adcCalibration = new("Running", requestedAt);
                timings.Remove("ADC Calibration API Time");
                // Any attempted calibration can change the setup, even if it fails.
                parametersSent = false; preparedRequest = null; preparedParametersSent = null;
                preparedFrequency = null; frequency = null;
            }
            bool? result = null; string? failure = null;
            try
            {
                Critical("ADC_CALIBRATION", $"ADC_CALIBRATION REQUESTED State={previous} Session={sessionId} Timestamp={requestedAt:O}");
                result = Timed("ADC Calibration API Time", api.AdcCalibration);
                DrainErrors();
                if (Interlocked.Read(ref fatalErrorSequence) != beforeErrors)
                { lock (gate) failure = "SDK error during calibration: " + lastError; }
                else if (result != true) failure = "adcCalibration() returned false";
            }
            catch (Exception error) { failure = error.Message; log.Exception("ADC_CALIBRATION", error); }
            finally
            {
                DrainErrors();
                lock (gate)
                {
                    if (Interlocked.Read(ref fatalErrorSequence) != beforeErrors) failure ??= "SDK error during calibration: " + lastError;
                    adcCalibration = new(failure is null ? "PASS" : "FAIL", requestedAt, result,
                        timings.TryGetValue("ADC Calibration API Time", out var time) ? time : null, failure,
                        warnings.Snapshot().Total - beforeWarnings);
                    state = previous == ExternalState.Initialized ? ExternalState.Initialized : ExternalState.PrepareRequired;
                }
                UpdateLogContext(); FlushWarnings(true);
            }
            var outcome = CalibrationSnapshot();
            Critical("ADC_CALIBRATION", $"ADC_CALIBRATION {outcome.Status} Result={outcome.Result} API elapsed ms={outcome.ApiTimeMs:F3} SDK warnings={outcome.WarningsDuringCall} Error={outcome.Error ?? "None"}");
            return Task.CompletedTask;
        }, ExternalState.Initialized, ExternalState.Prepared, ExternalState.PrepareRequired);
    }
}
