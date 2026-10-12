using HmmExample.Acquisition;
namespace HmmExample.Display;
internal sealed record GrayPreview(int Width,int Height,byte[] Pixels);
internal static class PreviewRenderer
{
    internal static GrayPreview Complete(CompletedFrame frame,FrameGeometry geometry,int maxDimension)
    {
        if(frame.Width!=geometry.Width || frame.Height!=geometry.AcquisitionRows || frame.Data.Length!=geometry.PixelCount)
            throw new ArgumentException("Only exact complete acquisition frames can be previewed.");
        if(geometry.Dummy!=0 && geometry.DummyPolicy==DummyPolicy.Unconfirmed) throw new InvalidOperationException("Dummy row location is unconfirmed.");
        int rows=geometry.DummyPolicy==DummyPolicy.RawIncludesOverscan?geometry.AcquisitionRows:geometry.Height;
        var sampling=PreviewSampling.Create(frame.Width,rows,maxDimension);
        if(sampling.Width==frame.Width && sampling.Height==rows) return new(frame.Width,rows,FrameRenderer.Normalize(frame,geometry));
        var preview=HostMemoryAdmissionPolicy.Floats(sampling.PixelCount);sampling.Copy(frame.Data,preview);
        return new(sampling.Width,sampling.Height,FrameRenderer.NormalizeLive(new(frame.Channel,sampling.Width,sampling.Height,preview,
            frame.FrameSequence,sampling.Height,frame.FrameSequence,frame.Timestamp,frame.SessionGeneration)));
    }
}
