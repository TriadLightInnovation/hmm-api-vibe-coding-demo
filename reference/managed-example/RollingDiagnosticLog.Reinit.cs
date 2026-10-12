using System.Text.Json;

namespace HmmExample;

internal sealed partial class RollingDiagnosticLog
{
    private RescanMarker? activeReinitMarker;
    internal string ReinitMarkerPath=>Path.Combine(DirectoryPath,"reinit-no-prepare-in-progress.json");
    internal string? PreviousReinitCrashNotice {get;private set;}
    internal string StartupNotice=>string.Join(Environment.NewLine,new[]{PreviousCrashNotice,PreviousReinitCrashNotice,PreviousSetParamCrashNotice}.Where(x=>x is not null));
    private void RecoverReinitMarker()
    {
        if(!File.Exists(ReinitMarkerPath)) return;
        string content=File.ReadAllText(ReinitMarkerPath),phase="Unknown",detail="";
        try
        {
            var marker=JsonSerializer.Deserialize<RescanMarker>(content,Json);
            phase=marker?.Phase??"Unknown";
            if(marker is {InitializeResult:true,StartResult:null} && phase=="BeforeStartScan")
                detail=" CRASH / unclean termination: startScan did not return after successful Initialize. Exact native fault location requires dump evidence.";
        }
        catch(JsonException) {phase="Unreadable marker";}
        PreviousReinitCrashNotice="Previous Reinit-No-Prepare test ended abnormally. Last phase: "+phase+detail;
        Critical("Startup",PreviousReinitCrashNotice+" Marker="+content);
    }
    internal void BeginReinit(object previous)
    {
        lock(io)
        {
            if(File.Exists(ReinitMarkerPath)) File.Copy(ReinitMarkerPath,Path.Combine(DirectoryPath,$"reinit-no-prepare-prior-{Guid.NewGuid():N}.json"));
            var c=Volatile.Read(ref context);var now=DateTimeOffset.UtcNow;
            activeReinitMarker=new(now,launch,Guid.NewGuid(),c.Session,c.Generation,"ButtonClicked",new(){{"ButtonClicked",now}});
            SaveReinitMarker();
        }
        Critical("ReinitNoPrepare","Previous session diagnostic snapshot="+JsonSerializer.Serialize(previous,Json));
    }
    internal void BindReinitGeneration(long generation)
    {
        lock(io) if(activeReinitMarker is not null)
        {
            activeReinitMarker=activeReinitMarker with {Session=Volatile.Read(ref context).Session,Generation=generation};SaveReinitMarker();
        }
    }
    internal void ReinitPhase(string phase,bool? result=null)
    {
        lock(io)
        {
            if(activeReinitMarker is null) return;
            if(phase=="AfterInitialize") activeReinitMarker=activeReinitMarker with {InitializeResult=result};
            if(phase=="AfterStartScan") activeReinitMarker=activeReinitMarker with {StartResult=result};
            AdvanceReinitMarker(phase);
        }
    }
    private static int ReinitRank(string phase)=>phase switch
    {"ButtonClicked"=>0,"BeforeInitialize"=>1,"AfterInitialize"=>2,"CallbacksRegistered"=>3,"BeforeStartScan"=>4,
        "AfterStartScan"=>5,"FirstCallback"=>6,"FirstCompleteFrame"=>7,"StoppedCleanly"=>8,_=>-1};
    private void AdvanceReinitMarker(string phase)
    {
        if(activeReinitMarker is null || activeReinitMarker.Reached.ContainsKey(phase)) return;
        var now=DateTimeOffset.UtcNow;
        activeReinitMarker=activeReinitMarker with {Timestamp=now,
            Phase=ReinitRank(phase)>ReinitRank(activeReinitMarker.Phase)?phase:activeReinitMarker.Phase,
            Reached=new Dictionary<string,DateTimeOffset>(activeReinitMarker.Reached){{phase,now}}};
        SaveReinitMarker();
    }
    private void SaveReinitMarker()
    {
        string temp=ReinitMarkerPath+"."+Guid.NewGuid().ToString("N")+".tmp";
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(activeReinitMarker,Json);
        using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
        {file.Write(bytes);file.Flush(flushToDisk:true);}
        if(File.Exists(ReinitMarkerPath)) File.Replace(temp,ReinitMarkerPath,null);else File.Move(temp,ReinitMarkerPath);
    }
    internal void EndReinit(long generation)
    {
        lock(io)
        {
            if(activeReinitMarker?.Generation!=generation) return;
            var disk=JsonSerializer.Deserialize<RescanMarker>(File.ReadAllText(ReinitMarkerPath),Json);
            if(disk?.Attempt!=activeReinitMarker.Attempt) throw new IOException("Reinit marker ownership changed; retained evidence.");
            AdvanceReinitMarker("StoppedCleanly");
            Write(Format("ReinitNoPrepare","StoppedCleanly; manual stopScan returned successfully; removing marker",generation));
            File.Delete(ReinitMarkerPath);activeReinitMarker=null;
        }
    }
    internal void AbandonActiveReinit() {lock(io) activeReinitMarker=null;}
}
