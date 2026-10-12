using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HmmExample.Acquisition;

namespace HmmExample.Display;

internal static class FrameRenderer
{
    internal static byte[] Normalize(CompletedFrame frame, FrameGeometry geometry)
    {
        if (frame.Width != geometry.Width || frame.Height != geometry.AcquisitionRows || frame.Data.Length != geometry.PixelCount)
            throw new ArgumentException("Only a complete acquisition frame can be displayed.");
        if (geometry.Dummy != 0 && geometry.DummyPolicy == DummyPolicy.Unconfirmed)
            throw new InvalidOperationException("Dummy row location is unconfirmed; display cropping is blocked.");
        // Only after full acquisition completion: retain first Height rows if tail-crop was confirmed.
        int length = geometry.DummyPolicy == DummyPolicy.RawIncludesOverscan ? geometry.PixelCount : checked(geometry.Width * geometry.Height);
        return NormalizePixels(frame.Data, length, length);
    }
    private static byte[] NormalizePixels(float[] data, int length, int acquiredLength)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = 0; i < acquiredLength; i++) if (float.IsFinite(data[i])) { min = Math.Min(min, data[i]); max = Math.Max(max, data[i]); }
        var pixels = HostMemoryAdmissionPolicy.Bytes(length);
        if (max <= min || !double.IsFinite(min)) return pixels;
        for (int i = 0; i < acquiredLength; i++)
            if (float.IsFinite(data[i])) pixels[i] = (byte)Math.Clamp(Math.Round((data[i] - min) / (max - min) * 255), 0, 255);
        return pixels;
    }
    internal static byte[] NormalizeLive(LiveFrameSnapshot snapshot)
    {
        if (snapshot.Width <= 0 || snapshot.Height <= 0 || snapshot.Data.Length != checked(snapshot.Width * snapshot.Height)
            || snapshot.AccumulatedLines < 0 || snapshot.AccumulatedLines > snapshot.Height)
            throw new ArgumentException("Invalid full-size live snapshot.");
        // Only acquired rows determine contrast. Unacquired rows remain black even for negative native values.
        return NormalizePixels(snapshot.Data, snapshot.Data.Length, checked(snapshot.Width * snapshot.AccumulatedLines));
    }
    internal static WriteableBitmap RenderLive(LiveFrameSnapshot snapshot)
    {
        Application.Current.Dispatcher.VerifyAccess();
        var pixels = NormalizeLive(snapshot);
        return RenderGray(snapshot.Width,snapshot.Height,pixels);
    }
    internal static WriteableBitmap Render(CompletedFrame frame, FrameGeometry geometry)
    {
        Application.Current.Dispatcher.VerifyAccess();
        var pixels = Normalize(frame, geometry);
        int rows = geometry.DummyPolicy == DummyPolicy.RawIncludesOverscan ? geometry.AcquisitionRows : geometry.Height;
        return RenderGray(geometry.Width,rows,pixels);
    }
    internal static WriteableBitmap RenderGray(int width,int height,byte[] pixels)
    {
        Application.Current.Dispatcher.VerifyAccess();
        if(width<=0 || height<=0 || pixels.Length!=checked(width*height)) throw new ArgumentException("Invalid grayscale display buffer.");
        var bitmap = new WriteableBitmap(width,height,96,96,PixelFormats.Gray8,null);
        bitmap.WritePixels(new Int32Rect(0,0,width,height),pixels,width,0);
        return bitmap;
    }
}
