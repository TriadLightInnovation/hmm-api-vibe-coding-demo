using System.Runtime.InteropServices;

namespace HmmExample.Interop;

// ABI authority: baseline HMMSysCore.h and HMMSysCoreBind.h, Windows x64.
// Pack=8 explicitly states the default MSVC x64 packing (no pragma pack in headers).
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct WaveParams
{
    public float ao0_amplitude, ao1_amplitude, ao3_amplitude;
    public float ao0_offset, ao1_offset, ao3_offset;
    [MarshalAs(UnmanagedType.I1)] public bool ao3_static_mode;
    [MarshalAs(UnmanagedType.I1)] public bool signal_invert;
    [MarshalAs(UnmanagedType.I1)] public bool line_mode;
    public int number_of_lines, channel_start_delay;
    [MarshalAs(UnmanagedType.I1)] public bool external_line_trigger;
    public int line_frequency_hz, pixel_per_line, line_per_frame, steps_per_stack;
    [MarshalAs(UnmanagedType.I1)] public bool scan_dir;
    [MarshalAs(UnmanagedType.I1)] public bool trig0_mode;
    public int line_average, frame_average, oversample_head, oversample_tail, oversample_frame, oversample_stack;
    [MarshalAs(UnmanagedType.I1)] public bool channel0_enable;
    [MarshalAs(UnmanagedType.I1)] public bool channel1_enable;
    [MarshalAs(UnmanagedType.I1)] public bool channel2_enable;
    [MarshalAs(UnmanagedType.I1)] public bool channel3_enable;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct FrequencyParams
{
    public float line_frequency_hz;
    public int pos_time, neg_time;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct CResampledImage
{
    public int channel, width, height, index0_line_number, index0, index1_line_number, index1;
    public IntPtr image_data;
    public int image_size;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct ErrorMessage
{
    public IntPtr error_message;
    public int message_size;
}

internal static class HmmNative
{
    private const string Library = "HMMSysCoreAPILib.dll";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ImageCallback(IntPtr image); // const CResampledImage*
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ErrorCallback(IntPtr error); // const ErrorMessage*

    [DllImport(Library, EntryPoint = "initialize", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool Initialize();
    // Baseline HMMSysCoreBind.h: extern "C" { HMM_SYS_COREAPI bool adcCalibration(); }
    // Windows x64 C ABI; native C++ bool is one byte, not Win32 BOOL.
    [DllImport(Library, EntryPoint = "adcCalibration", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool AdcCalibration();
    [DllImport(Library, EntryPoint = "setParameters", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetParameters(ref WaveParams parameters, int iterationNumber);
    [DllImport(Library, EntryPoint = "setAo2Value", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SetAo2Value(float voltage, float offset);
    [DllImport(Library, EntryPoint = "getFrequency", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void GetFrequency(out FrequencyParams parameters);
    [DllImport(Library, EntryPoint = "setAux", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetAux([MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport(Library, EntryPoint = "startScan", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool StartScan();
    [DllImport(Library, EntryPoint = "stopScan", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void StopScan();
    [DllImport(Library, EntryPoint = "setResampledImageCallback", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetResampledImageCallback(ImageCallback callback);
    [DllImport(Library, EntryPoint = "setErrorMessageCallback", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetErrorMessageCallback(ErrorCallback callback);
}
