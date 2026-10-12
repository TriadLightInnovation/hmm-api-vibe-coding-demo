using System.Reflection;
using System.Runtime.InteropServices;

namespace HmmExample.Interop;

// Managed seam for offline lifecycle tests. The production implementation uses the unchanged ABI.
internal interface IHmmApi
{
    void EnsureAvailable();
    bool Initialize();
    bool AdcCalibration() => throw new NotSupportedException("ADC calibration is unavailable in this API implementation.");
    void SetParameters(ref WaveParams wave,int iterations);
    bool SetAo2Value(float amplitude,float offset);
    void GetFrequency(out FrequencyParams frequency);
    void SetAux(bool value);
    bool StartScan();
    void StopScan();
    void SetImageCallback(HmmNative.ImageCallback callback);
    void SetErrorCallback(HmmNative.ErrorCallback callback);
}
internal sealed class NativeHmmApi : IHmmApi
{
    private static readonly object gate=new();
    private static IntPtr handle;
    private static bool resolverInstalled;
    public void EnsureAvailable()
    {
        lock(gate)
        {
            RuntimeCompatibility.Require(AppContext.BaseDirectory);
            if(!resolverInstalled) { NativeLibrary.SetDllImportResolver(typeof(HmmNative).Assembly,Resolve); resolverInstalled=true; }
            if(handle==IntPtr.Zero) handle=NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory,"HMMSysCoreAPILib.dll"),typeof(HmmNative).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies|DllImportSearchPath.System32);
            foreach(string entry in RuntimeCompatibility.RequiredExports)
                if(!NativeLibrary.TryGetExport(handle,entry,out _)) throw new EntryPointNotFoundException(entry);
        }
    }
    private static IntPtr Resolve(string name,Assembly assembly,DllImportSearchPath? path)=>name=="HMMSysCoreAPILib.dll"?handle:IntPtr.Zero;
    public bool Initialize()=>HmmNative.Initialize();
    public bool AdcCalibration()
    {
        lock(gate) if(handle==IntPtr.Zero || !NativeLibrary.TryGetExport(handle,"adcCalibration",out _))
            throw new EntryPointNotFoundException("adcCalibration export is unavailable in the loaded SDK.");
        return HmmNative.AdcCalibration();
    }
    public void SetParameters(ref WaveParams wave,int iterations)=>HmmNative.SetParameters(ref wave,iterations);
    public bool SetAo2Value(float amplitude,float offset)=>HmmNative.SetAo2Value(amplitude,offset);
    public void GetFrequency(out FrequencyParams frequency)=>HmmNative.GetFrequency(out frequency);
    public void SetAux(bool value)=>HmmNative.SetAux(value);
    public bool StartScan()=>HmmNative.StartScan();
    public void StopScan()=>HmmNative.StopScan();
    public void SetImageCallback(HmmNative.ImageCallback callback)=>HmmNative.SetResampledImageCallback(callback);
    public void SetErrorCallback(HmmNative.ErrorCallback callback)=>HmmNative.SetErrorMessageCallback(callback);
}
