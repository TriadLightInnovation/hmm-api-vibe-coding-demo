using System.Text.Json;

namespace HmmExample;

internal sealed partial class RollingDiagnosticLog
{
    private RescanMarker? activeSetParamMarker;
    internal string SetParamMarkerPath=>Path.Combine(DirectoryPath,"reinit-setparam-start-in-progress.json");
    internal string? PreviousSetParamCrashNotice {get;private set;}
    private void RecoverSetParamMarker()
    {
        if(!File.Exists(SetParamMarkerPath)) return;
        string content=File.ReadAllText(SetParamMarkerPath),phase="Unknown",detail="";
        try
        {
            var marker=JsonSerializer.Deserialize<RescanMarker>(content,Json);
            phase=marker?.Phase??"Unknown";
            if(marker is {InitializeResult:true,SetParametersReturned:null} && phase=="BeforeSetParameters")
                detail=" CRASH / unclean termination: setParameters did not return. Exact native fault location requires dump evidence.";
            if(marker is {InitializeResult:true,StartResult:null} && phase=="BeforeStartScan")
                detail=" CRASH / unclean termination: startScan did not return after successful Initialize. Exact native fault location requires dump evidence.";
        }
        catch(JsonException) {phase="Unreadable marker";}
        PreviousSetParamCrashNotice="Previous Reinit-SetParameters-Start test ended abnormally. Last phase: "+phase+detail;
        Critical("Startup",PreviousSetParamCrashNotice+" Marker="+content);
    }
    internal void BeginSetParam(object previous)
    {
        lock(io)
        {
            if(File.Exists(SetParamMarkerPath)) File.Copy(SetParamMarkerPath,Path.Combine(DirectoryPath,$"reinit-setparam-start-prior-{Guid.NewGuid():N}.json"));
            var c=Volatile.Read(ref context);var now=DateTimeOffset.UtcNow;
            activeSetParamMarker=new(now,launch,Guid.NewGuid(),c.Session,c.Generation,"ButtonClicked",new(){{"ButtonClicked",now}});
            SaveSetParamMarker();
        }
        Critical("ReinitSetParamStart","Previous session diagnostic snapshot="+JsonSerializer.Serialize(previous,Json));
    }
    internal void BindSetParamGeneration(long generation)
    {
        lock(io) if(activeSetParamMarker is not null)
        {
            activeSetParamMarker=activeSetParamMarker with {Session=Volatile.Read(ref context).Session,Generation=generation};SaveSetParamMarker();
        }
    }
    internal void SetParamPhase(string phase,bool? result=null)
    {
        lock(io)
        {
            if(activeSetParamMarker is null) return;
            if(phase=="AfterInitialize") activeSetParamMarker=activeSetParamMarker with {InitializeResult=result};
            if(phase=="AfterSetParameters") activeSetParamMarker=activeSetParamMarker with {SetParametersReturned=result};
            if(phase=="AfterStartScan") activeSetParamMarker=activeSetParamMarker with {StartResult=result};
            AdvanceSetParamMarker(phase);
        }
    }
    private static int SetParamRank(string phase)=>phase switch
    {"ButtonClicked"=>0,"BeforeInitialize"=>1,"AfterInitialize"=>2,"CallbacksRegistered"=>3,"BeforeSetParameters"=>4,"AfterSetParameters"=>5,"BeforeStartScan"=>6,
        "AfterStartScan"=>7,"FirstCallback"=>8,"FirstCompleteFrame"=>9,"StoppedCleanly"=>10,_=>-1};
    private void AdvanceSetParamMarker(string phase)
    {
        if(activeSetParamMarker is null || activeSetParamMarker.Reached.ContainsKey(phase)) return;
        var now=DateTimeOffset.UtcNow;
        activeSetParamMarker=activeSetParamMarker with {Timestamp=now,
            Phase=SetParamRank(phase)>SetParamRank(activeSetParamMarker.Phase)?phase:activeSetParamMarker.Phase,
            Reached=new Dictionary<string,DateTimeOffset>(activeSetParamMarker.Reached){{phase,now}}};
        SaveSetParamMarker();
    }
    private void SaveSetParamMarker()
    {
        string temp=SetParamMarkerPath+"."+Guid.NewGuid().ToString("N")+".tmp";
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(activeSetParamMarker,Json);
        using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
        {file.Write(bytes);file.Flush(flushToDisk:true);}
        if(File.Exists(SetParamMarkerPath)) File.Replace(temp,SetParamMarkerPath,null);else File.Move(temp,SetParamMarkerPath);
    }
    internal void EndSetParam(long generation)
    {
        lock(io)
        {
            if(activeSetParamMarker?.Generation!=generation) return;
            var disk=JsonSerializer.Deserialize<RescanMarker>(File.ReadAllText(SetParamMarkerPath),Json);
            if(disk?.Attempt!=activeSetParamMarker.Attempt) throw new IOException("SetParam marker ownership changed; retained evidence.");
            AdvanceSetParamMarker("StoppedCleanly");
            Write(Format("ReinitSetParamStart","StoppedCleanly; manual stopScan returned successfully; removing marker",generation));
            File.Delete(SetParamMarkerPath);activeSetParamMarker=null;
        }
    }
    internal void AbandonActiveSetParam() {lock(io) activeSetParamMarker=null;}
}
