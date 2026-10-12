namespace HmmExample;

internal sealed record ReportedExperimentResult(string Test,string Flow,string Result,string Reason,string EvidenceSource,
    int? Level=null,bool? InitializeSucceeded=null,bool? StartAccepted=null,long[]? ChannelCallbacks=null,
    long? ReceivedLineBlocks=null,long? CompletedFrames=null,string? LastError=null);
internal sealed record ReportedFeatureValidation(string Feature,string Result);
internal sealed record ReportedBaselineValidation(string EvidenceSource,string Profile,string SdkSha256,
    ReportedFeatureValidation[] Features,string DirectStart,string ExternalTrigger,string EvidenceLimit);
internal static class ExperimentalHardwareEvidence
{
    internal static readonly ReportedBaselineValidation InternalBaseline=new(
        "Paz-reported hardware validation; recorded 2026-09-19", "3.5.0 baseline", SdkRuntimeProfile.BaselineHash,
        [new("Internal Trigger","PASS"),new("Progressive Live Preview","PASS"),new("Line Average","PASS"),
         new("Frame Average non-rolling batch","PASS"),new("Composite","PASS"),new("Stop Scan","PASS")],
        "stopScan() -> startScan(): native crash confirmed; Direct Start Test disabled",
        "Implementation preserved; hardware validation pending Resonant scanner availability",
        "Reported by Paz, not executed by the agent. Exact hardware timestamps, geometry and averaging counts were not supplied in this report.");
    internal const string NoPrepareSummary="Paz hardware: FAIL / Level 2 — Start accepted but no callback";
    internal static readonly ReportedExperimentResult[] Results=
    [
        new("Direct Rescan","Stop → Start","FAIL / native process crash","Native process crash inside startScan()","Paz-reported hardware result"),
        new("Reinit + Start (No Prepare)","Stop → Initialize → Register Callbacks → Start (No Prepare / No SetParameters / No SetAux)",
            "FAIL / Level 2 of 4","startScan accepted, but acquisition did not resume and no callbacks arrived.",
            "Paz-reported hardware result; previous normal Initialize → Prepare → Start → Stop PASS",2,true,true,[0,0,0,0],0,0,"None")
    ];
}
