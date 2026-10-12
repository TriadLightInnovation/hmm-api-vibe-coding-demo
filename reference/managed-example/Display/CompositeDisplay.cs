using HmmExample.Acquisition;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HmmExample.Display;

internal sealed record RgbColor(string Name, byte R, byte G, byte B)
{
    public override string ToString()=>Name;
    internal static readonly RgbColor[] Presets=[new("Green",0,255,0),new("Red",255,0,0),new("Blue",0,0,255),
        new("White",255,255,255),new("Cyan",0,255,255),new("Magenta",255,0,255),new("Yellow",255,255,0),new("Orange",255,128,0)];
}
internal enum DisplaySource { Live, Complete, BatchAverage }
// UI-owned normalized bytes, shared by the grayscale bitmap and composite without a second normalization.
internal sealed record NormalizedDisplayFrame(int Width,int Height,byte[] Pixels,long Generation,long Version,DisplaySource Source);
internal sealed record CompositeChannelSetting(bool Include,RgbColor Color);
internal sealed record CompositeDiagnostics(bool Enabled,CompositeChannelSetting[] Channels,long? Generation,string Status,
    long DisplayedUpdates,long DroppedUiUpdates,long RgbCalculations,int Width,int Height);

internal static class CompositeCalculator
{
    // B,G,R,A storage for WPF Bgra32; opaque alpha. Sum before rounding/clamping.
    internal static void Blend(NormalizedDisplayFrame?[] frames,CompositeChannelSetting[] settings,int width,int height,byte[] output)
    {
        if(frames.Length!=4 || settings.Length!=4 || width<=0 || height<=0 || output.Length!=checked(width*height*4))
            throw new ArgumentException("Invalid composite dimensions.");
        var inputs=Enumerable.Range(0,4).Where(i=>settings[i].Include && frames[i] is not null)
            .Select(i=>(Frame:frames[i]!,Color:settings[i].Color)).ToArray();
        if(inputs.Any(x=>x.Frame.Width!=width || x.Frame.Height!=height || x.Frame.Pixels.Length!=checked(width*height)))
            throw new ArgumentException("geometry mismatch");
        for(int p=0,o=0;p<width*height;p++,o+=4)
        {
            int r=0,g=0,b=0;
            foreach(var input in inputs) {int value=input.Frame.Pixels[p];r+=value*input.Color.R;g+=value*input.Color.G;b+=value*input.Color.B;}
            output[o]=(byte)Math.Min(255,(b+127)/255);output[o+1]=(byte)Math.Min(255,(g+127)/255);output[o+2]=(byte)Math.Min(255,(r+127)/255);output[o+3]=255;
        }
    }
}

// UI thread only. One latest normalized buffer per channel, no FIFO and no acquisition locks.
internal sealed class CompositeDisplay
{
    private readonly Dispatcher dispatcher;
    private readonly NormalizedDisplayFrame?[] latest=new NormalizedDisplayFrame?[4];
    private readonly CompositeChannelSetting[] settings=Enumerable.Range(0,4).Select(i=>new CompositeChannelSetting(true,RgbColor.Presets[i])).ToArray();
    private readonly bool[] pending=new bool[4];
    private bool enabled=true,dirty=true;
    private long? generation;
    private long displayed,dropped,calculations;
    private byte[] rgb=[];
    private WriteableBitmap? bitmap;
    private int width,height;
    internal string Status {get;private set;}="Awaiting image data";
    internal WriteableBitmap? Image {get;private set;}
    internal CompositeDisplay(Dispatcher dispatcher) {this.dispatcher=dispatcher;}
    internal void SetEnabled(bool value) {dispatcher.VerifyAccess();if(enabled!=value) {enabled=value;dirty=true;Array.Clear(pending);}}
    internal void SetChannel(int channel,bool include,RgbColor color)
    {
        dispatcher.VerifyAccess();
        var value=new CompositeChannelSetting(include,color);
        if(settings[channel]!=value) {settings[channel]=value;pending[channel]=false;dirty=true;}
    }
    internal void BeginGeneration(long value)
    {
        dispatcher.VerifyAccess();
        if(generation is long current && value<=current) return;
        generation=value;Array.Clear(latest);Array.Clear(pending);dirty=true;width=height=0;
    }
    internal void ResetSources() {dispatcher.VerifyAccess();Array.Clear(latest);Array.Clear(pending);dirty=true;}
    internal void Submit(int channel,NormalizedDisplayFrame frame)
    {
        dispatcher.VerifyAccess();
        if((uint)channel>=4 || frame.Width<=0 || frame.Height<=0 || frame.Pixels.Length!=checked(frame.Width*frame.Height))
            throw new ArgumentException("Invalid normalized display buffer.");
        if(generation is long current && frame.Generation<current) return;
        BeginGeneration(frame.Generation);
        var previous=latest[channel];
        if(previous is not null && previous.Source==frame.Source && frame.Version<=previous.Version) return;
        if(enabled && settings[channel].Include)
        {
            if(pending[channel]) dropped++;
            if(previous is null || previous.Source==frame.Source) dropped+=Math.Max(0,frame.Version-(previous?.Version??0)-1);
            pending[channel]=true;
        }
        latest[channel]=frame;
        // Excluded channels still refresh their cached grayscale source, but need not cause RGB work.
        if(settings[channel].Include) dirty=true;
    }
    internal bool Refresh()
    {
        dispatcher.VerifyAccess();
        if(!dirty) return false;
        dirty=false;Array.Clear(pending);
        if(!enabled) {Status="Disabled";Image=null;return true;}
        var selected=Enumerable.Range(0,4).Where(i=>settings[i].Include).ToArray();
        if(selected.Length==0) {Status="No channels selected";Image=null;return true;}
        var available=selected.Where(i=>latest[i] is not null).Select(i=>latest[i]!).ToArray();
        if(available.Length==0) {Status="Awaiting image data";Image=null;return true;}
        var first=available[0];
        if(available.Any(f=>f.Width!=first.Width || f.Height!=first.Height)) {Status="geometry mismatch";Image=null;return true;}
        width=first.Width;height=first.Height;
        int size=HostMemoryAdmissionPolicy.ArrayLength(checked((long)width*height*4),1);
        if(rgb.Length!=size) rgb=HostMemoryAdmissionPolicy.Bytes(size);
        CompositeCalculator.Blend(latest,settings,width,height,rgb);calculations++;
        if(bitmap is null || bitmap.PixelWidth!=width || bitmap.PixelHeight!=height) bitmap=new(width,height,96,96,PixelFormats.Bgra32,null);
        bitmap.WritePixels(new Int32Rect(0,0,width,height),rgb,width*4,0);
        Image=bitmap;displayed++;
        Status=available.Length==selected.Length?"Latest channel display buffers":"Latest available channels; missing channel data is black";
        return true;
    }
    internal long RetainedBytes()
    {
        dispatcher.VerifyAccess();var arrays=new HashSet<Array>(ReferenceEqualityComparer.Instance) {rgb};
        foreach(var f in latest)if(f is not null)arrays.Add(f.Pixels);
        return checked(HostMemoryAdmissionPolicy.BufferBytes(arrays)+(bitmap is null?0:checked((long)bitmap.PixelWidth*bitmap.PixelHeight*4)));
    }
    internal CompositeDiagnostics Snapshot()
    {dispatcher.VerifyAccess();return new(enabled,(CompositeChannelSetting[])settings.Clone(),generation,Status,displayed,dropped,calculations,width,height);}
}
