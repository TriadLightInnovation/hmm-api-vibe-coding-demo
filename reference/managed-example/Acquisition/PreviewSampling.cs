namespace HmmExample.Acquisition;

// Display-only nearest-neighbour sampling. Never writes to acquisition/averaging input.
internal readonly record struct PreviewSampling(int SourceWidth,int SourceHeight,int Width,int Height)
{
    internal int PixelCount => checked((int)((long)Width*Height));
    internal static PreviewSampling Create(int width,int height,int maxDimension)
    {
        GeometryMath.Pixels(width,height);
        if(maxDimension!=0 && maxDimension is not (1024 or 2048)) throw new ArgumentOutOfRangeException(nameof(maxDimension));
        int longest=Math.Max(width,height);
        return maxDimension==0 || longest<=maxDimension ? new(width,height,width,height) :
            new(width,height,Math.Max(1,checked((int)((long)width*maxDimension/longest))),Math.Max(1,checked((int)((long)height*maxDimension/longest))));
    }
    internal int AcquiredRows(int sourceRows)
    {
        if(sourceRows<0 || sourceRows>SourceHeight) throw new ArgumentOutOfRangeException(nameof(sourceRows));
        // Count output rows whose floor(y*sourceHeight/height) samples an acquired source row.
        return checked((int)(((long)sourceRows*Height+SourceHeight-1)/SourceHeight));
    }
    internal void Copy(float[] source,float[] destination)
    {
        if(source.LongLength<(long)SourceWidth*SourceHeight || destination.Length!=PixelCount) throw new ArgumentException("Invalid preview buffers.");
        if(Width==SourceWidth && Height==SourceHeight) {Array.Copy(source,destination,PixelCount);return;}
        for(int y=0;y<Height;y++)
        {
            long row=((long)y*SourceHeight/Height)*SourceWidth;
            int target=checked(y*Width);
            if(SourceWidth%Width==0)
            {
                int sourceIndex=checked((int)row),step=SourceWidth/Width;
                for(int x=0;x<Width;x++,sourceIndex+=step) destination[target+x]=source[sourceIndex];
            }
            else for(int x=0;x<Width;x++) destination[target+x]=source[checked((int)(row+(long)x*SourceWidth/Width))];
        }
    }
}
internal static class GeometryMath
{
    internal static int Pixels(int width,int height)
    {
        if(width<=0 || height<=0) throw new ArgumentException("Image dimensions must be positive.");
        long samples=checked((long)width*height);
        return HostMemoryAdmissionPolicy.ArrayLength(samples,sizeof(float));
    }
}
