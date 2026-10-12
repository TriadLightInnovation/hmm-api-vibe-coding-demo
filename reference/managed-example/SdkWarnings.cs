using System.Text.RegularExpressions;

namespace HmmExample;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
internal enum WarningSeverity { AdcOverRange }
internal sealed record AdcWarningCounter(string SdkChannel, WarningSeverity Severity, long Count,
    DateTimeOffset? First, DateTimeOffset? Last, long FirstSession, long LastSession,
    long FirstGeneration, long LastGeneration, string? LastMessage);
internal sealed record SdkWarningSnapshot(string? LastWarning, long Total, AdcWarningCounter[] Counters);

// Original SDK wording is retained. These channel names are never mapped to WPF CH labels.
internal sealed class SdkWarnings
{
    private static readonly Regex OverRange = new(@"\bADC\s+over\s*range\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Channel = new(@"\bChannel\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly object gate = new();
    private readonly Dictionary<string, AdcWarningCounter> counters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Count, DateTimeOffset At)> logged = new(StringComparer.OrdinalIgnoreCase);
    private string? lastWarning;
    internal SdkWarnings()
    {
        for (int i = 0; i < 4; i++) counters["Channel" + i] = new("Channel" + i, WarningSeverity.AdcOverRange, 0, null, null, 0, 0, 0, 0, null);
    }
    internal static bool IsAdcOverRange(string message) => OverRange.IsMatch(message);
    internal void Add(string message, long session, long generation, RollingDiagnosticLog log)
    {
        var match = Channel.Match(message);
        string key = match.Success ? "Channel" + match.Groups[1].Value : "Unspecified SDK channel";
        AdcWarningCounter value; bool first;
        var now = DateTimeOffset.Now;
        lock (gate)
        {
            if (!counters.TryGetValue(key, out var prior)) prior = new(key, WarningSeverity.AdcOverRange, 0, null, null, 0, 0, 0, 0, null);
            first = prior.Count == 0;
            value = prior with { Count = prior.Count + 1, First = prior.First ?? now, Last = now,
                FirstSession = first ? session : prior.FirstSession, LastSession = session,
                FirstGeneration = first ? generation : prior.FirstGeneration, LastGeneration = generation, LastMessage = message };
            counters[key] = value; lastWarning = message;
            if (first) logged[key] = (value.Count, now);
        }
        // Enqueue only: no synchronous disk flush or WPF work on the callback thread.
        if (first) Write(log, value, "FIRST");
    }
    private static void Write(RollingDiagnosticLog log, AdcWarningCounter value, string kind) =>
        log.Event(value.LastGeneration, $"ADC_OVERRANGE {kind} Severity={value.Severity} SDKChannel={value.SdkChannel} Count={value.Count} First={value.First:O} Last={value.Last:O} FirstSession={value.FirstSession} LastSession={value.LastSession} FirstGeneration={value.FirstGeneration} LastGeneration={value.LastGeneration} OriginalMessage={value.LastMessage}");
    internal void Flush(RollingDiagnosticLog log, bool force = false)
    {
        var pending = new List<AdcWarningCounter>(); var now = DateTimeOffset.Now;
        lock (gate)
            foreach (var (key, value) in counters)
            {
                logged.TryGetValue(key, out var previous);
                if (value.Count > previous.Count && (force || now - previous.At >= TimeSpan.FromSeconds(1)))
                { pending.Add(value); logged[key] = (value.Count, now); }
            }
        foreach (var value in pending) Write(log, value, "SUMMARY");
    }
    internal SdkWarningSnapshot Snapshot()
    { lock (gate) return new(lastWarning, counters.Values.Sum(x => x.Count), counters.Values.ToArray()); }
}
