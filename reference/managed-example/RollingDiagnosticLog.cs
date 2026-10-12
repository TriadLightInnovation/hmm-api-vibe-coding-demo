using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace HmmExample;

internal sealed record DiagnosticContext(long Session, long Generation, string State);
internal sealed record RescanMarker(DateTimeOffset Timestamp, string Launch, Guid Attempt, long Session, long Generation,
    string Phase, Dictionary<string, DateTimeOffset> Reached, bool? InitializeResult=null, bool? StartResult=null, bool? SetParametersReturned=null);

// Critical callers synchronously flush to disk; callback milestones only enqueue small records.
internal sealed partial class RollingDiagnosticLog
{
    private sealed record Entry(string Line, long Generation, string? Phase = null);
    private readonly Channel<Entry> pending = Channel.CreateBounded<Entry>(new BoundedChannelOptions(1024)
        { FullMode=BoundedChannelFullMode.Wait, SingleReader=true, SingleWriter=false });
    private readonly ConcurrentDictionary<(long Generation,int Channel,string Phase),byte> firstEvents = new();
    private readonly object io = new();
    private readonly FileStream stream;
    private readonly StreamWriter writer;
    private readonly Task worker;
    private readonly long epoch = Stopwatch.GetTimestamp();
    private readonly string launch = Guid.NewGuid().ToString("N");
    private DiagnosticContext context = new(0,0,"RequiresInitialize");
    private RescanMarker? activeMarker;
    private bool finished;
    private string? failure;
    private long dropped;
    internal static readonly JsonSerializerOptions Json = new() { IncludeFields=true, PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
    internal string DirectoryPath { get; }
    internal string FilePath { get; }
    internal string MarkerPath => Path.Combine(DirectoryPath,"rescan-in-progress.json");
    internal string? PreviousMarker { get; }
    internal string? PreviousCrashNotice { get; }
    internal string? Failure => Volatile.Read(ref failure);
    internal long DroppedEvents => Interlocked.Read(ref dropped);
    internal void SetContext(long session,long generation,ExternalState state) => Volatile.Write(ref context,new(session,generation,state.ToString()));
    internal RollingDiagnosticLog(string? directory = null)
    {
        DirectoryPath=directory??Path.Combine(AppContext.BaseDirectory,"logs");
        Directory.CreateDirectory(DirectoryPath);
        string stem=Path.Combine(DirectoryPath,$"HMM_{DateTime.Now:yyyyMMdd_HHmmss}");
        FilePath=stem+".log";
        // Preserve independent launches within the same second.
        if(File.Exists(FilePath)) FilePath=stem+$"_{Environment.ProcessId}_{Guid.NewGuid():N}.log";
        stream=new(FilePath,FileMode.Append,FileAccess.Write,FileShare.Read);
        writer=new(stream,new UTF8Encoding(false),4096,leaveOpen:true);
        Critical("Application",$"LAUNCH id={launch} pid={Environment.ProcessId}; timestamps describe observation, not proven native crash cause");
        if(File.Exists(MarkerPath))
        {
            PreviousMarker=File.ReadAllText(MarkerPath);
            string phase;
            try
            {
                using var parsed=JsonDocument.Parse(PreviousMarker);
                phase=parsed.RootElement.ValueKind==JsonValueKind.Object && parsed.RootElement.TryGetProperty("phase",out var value)
                    && value.ValueKind==JsonValueKind.String ? value.GetString()??"Unknown" : "Unknown";
            }
            catch(JsonException) {phase="Unreadable marker";}
            PreviousCrashNotice="Previous Direct Rescan did not exit cleanly. Last recorded phase: "+phase;
            Critical("Startup","Previous Direct Rescan ended abnormally. "+PreviousCrashNotice+" Marker="+PreviousMarker);
        }
        RecoverReinitMarker();RecoverSetParamMarker();
        worker=Task.Run(WriteLoop);
    }
    private string Format(string operation,string message,long? generation=null)
    {
        var c=Volatile.Read(ref context);
        // Escaping retains complete stack/inner exception text on a single physical log line.
        return $"{DateTimeOffset.Now:O} +{Stopwatch.GetElapsedTime(epoch).TotalMilliseconds:F3} ms TID={Environment.CurrentManagedThreadId} Session={c.Session} Generation={generation??c.Generation} State={c.State} Operation={operation} Message={message.Replace("\r","\\r").Replace("\n","\\n")}";
    }
    private void Write(string line)
    {
        if(finished) throw new ObjectDisposedException(nameof(RollingDiagnosticLog));
        writer.WriteLine(line); writer.Flush(); stream.Flush(flushToDisk:true);
    }
    internal void Critical(string operation,string message)
    {
        string line=Format(operation,message);
        lock(io) try {Write(line);} catch(Exception e) {Volatile.Write(ref failure,e.Message);throw;}
    }
    internal void Exception(string operation,Exception error) => Critical(operation,error.ToString());
    internal void Event(long generation,string message) => Enqueue(new(Format("Acquisition",message,generation),generation));
    internal void First(long generation,int channel,string phase,string message)
    {
        if(!firstEvents.TryAdd((generation,channel,phase),0)) return;
        Enqueue(new(Format("Acquisition",$"CH{channel+1} {phase}: {message}",generation),generation,phase));
    }
    private void Enqueue(Entry entry)
    { if(!pending.Writer.TryWrite(entry)) Interlocked.Increment(ref dropped); }
    private async Task WriteLoop()
    {
        await foreach(var entry in pending.Reader.ReadAllAsync())
            lock(io) try
            {
                Write(entry.Line);
                if(entry.Phase is not null && activeMarker?.Generation==entry.Generation) AdvanceMarker(entry.Phase);
                if(entry.Phase is not null && activeReinitMarker?.Generation==entry.Generation) AdvanceReinitMarker(entry.Phase);
                if(entry.Phase is not null && activeSetParamMarker?.Generation==entry.Generation) AdvanceSetParamMarker(entry.Phase);
            }
            catch(Exception error) {Volatile.Write(ref failure,error.Message);}
    }
    internal void BeginRescan(object snapshot)
    {
        lock(io)
        {
            // Preserve prior abnormal evidence when a later explicit click starts another attempt.
            if(File.Exists(MarkerPath)) File.Copy(MarkerPath,Path.Combine(DirectoryPath,$"rescan-prior-{Guid.NewGuid():N}.json"));
            var c=Volatile.Read(ref context); var now=DateTimeOffset.UtcNow;
            activeMarker=new(now,launch,Guid.NewGuid(),c.Session,c.Generation,"ButtonClicked",new(){{"ButtonClicked",now}});
            SaveMarker();
        }
        Critical("DirectRescan","Snapshot="+JsonSerializer.Serialize(snapshot,Json));
    }
    internal void BindRescanGeneration(long generation)
    {
        lock(io) if(activeMarker is not null) {activeMarker=activeMarker with {Generation=generation};SaveMarker();}
    }
    internal void RescanPhase(string phase) {lock(io) AdvanceMarker(phase);}
    private static int PhaseRank(string phase)=>phase switch
    {"ButtonClicked"=>0,"BeforeStartScan"=>1,"AfterStartScan"=>2,"FirstCallback"=>3,"FirstCompleteFrame"=>4,"StoppedCleanly"=>5,_=>-1};
    private void AdvanceMarker(string phase)
    {
        if(activeMarker is null || activeMarker.Reached.ContainsKey(phase)) return;
        var now=DateTimeOffset.UtcNow;
        var reached=new Dictionary<string,DateTimeOffset>(activeMarker.Reached){{phase,now}};
        // A callback may happen inside startScan: AFTER must not erase stronger callback evidence.
        activeMarker=activeMarker with {Timestamp=now,Phase=PhaseRank(phase)>PhaseRank(activeMarker.Phase)?phase:activeMarker.Phase,Reached=reached};
        SaveMarker();
    }
    private void SaveMarker()
    {
        string temp=MarkerPath+"."+Guid.NewGuid().ToString("N")+".tmp";
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(activeMarker,Json);
        using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
        {file.Write(bytes);file.Flush(flushToDisk:true);}
        if(File.Exists(MarkerPath)) File.Replace(temp,MarkerPath,null); else File.Move(temp,MarkerPath);
    }
    internal void EndRescan(long acquisitionGeneration)
    {
        lock(io)
        {
            if(activeMarker?.Generation!=acquisitionGeneration) return;
            // Check before updating: even cleanup must not overwrite another attempt's evidence.
            var disk=JsonSerializer.Deserialize<RescanMarker>(File.ReadAllText(MarkerPath),Json);
            if(disk?.Attempt!=activeMarker.Attempt) throw new IOException("Rescan marker ownership changed; retained evidence.");
            AdvanceMarker("StoppedCleanly");
            Write(Format("DirectRescan","StoppedCleanly; stopScan returned successfully; removing owned marker",acquisitionGeneration));
            File.Delete(MarkerPath); activeMarker=null;
        }
    }
    internal void AbandonActiveRescan() {lock(io) activeMarker=null;} // New Initialize keeps previous disk evidence.
    internal async Task FinishAsync()
    {
        pending.Writer.TryComplete(); await worker.ConfigureAwait(false);
        lock(io) {if(finished) return; Write(Format("Application","Logging shutdown; does not imply a clean acquisition stop"));finished=true;writer.Dispose();stream.Dispose();}
    }
}
