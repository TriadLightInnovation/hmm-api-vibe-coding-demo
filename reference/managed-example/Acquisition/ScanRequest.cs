namespace HmmExample.Acquisition;

internal sealed record ScanRequest(bool InternalMode, int XPixel, int YPixel, int OversamplePixel,
    int OversampleFrame, int NumberOfLines, int SpeedPosition, bool LineAverage = false, int AverageLines = 3)
{
    private static readonly int[] frequencies = { 800, 400, 200, 100, 50, 20, 10, 5 };
    internal static ScanRequest Default => new(true,1024,1024,16,10,32,1);
    public int FrequencyHz => FrequencyAt(SpeedPosition);
    internal static int FrequencyAt(int position) => position is >=0 and <8 ? frequencies[position] : throw new ArgumentOutOfRangeException(nameof(position));
    public int LineFactor => InternalMode && LineAverage ? AverageLines : 1;
    internal string CapabilityNotice => XPixel>=4096 || (long)YPixel*LineFactor>=4096 ? "Native SDK / FPGA Geometry Not Yet Validated — Hardware Validation Required." : "";
    internal void Validate()
    {
        if(XPixel<=0 || YPixel<=0 || OversamplePixel<0 || OversampleFrame<0 || NumberOfLines is not (16 or 32)
            || SpeedPosition is <0 or >7 || (InternalMode && AverageLines is <1 or >1024)) throw new ArgumentException("Enter positive X/Y pixels, nonnegative oversampling and a valid block size / Speed.");
        if(InternalMode && (long)YPixel+OversampleFrame<NumberOfLines)
            throw new ArgumentException("Number Of Lines must not exceed total requested output rows; callbacks support at most one frame boundary.");
        long rows=checked(((long)YPixel+OversampleFrame)*LineFactor), columns=checked((long)XPixel+2L*OversamplePixel);
        if((long)NumberOfLines*LineFactor>int.MaxValue || rows>int.MaxValue || columns>int.MaxValue)
            throw new ArgumentException("Effective SDK geometry exceeds Int32 field range.");
        GeometryMath.Pixels(XPixel,checked(YPixel+OversampleFrame));
        HostMemoryAdmissionPolicy.ArrayLength(checked((long)XPixel*(InternalMode?(long)NumberOfLines*LineFactor:rows)),sizeof(float));
    }
}
