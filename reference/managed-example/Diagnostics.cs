using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HmmExample.Interop;

namespace HmmExample;

internal static class Diagnostics
{
    internal static bool SdkLoaded() => System.Diagnostics.Process.GetCurrentProcess().Modules
        .Cast<System.Diagnostics.ProcessModule>().Any(m => m.ModuleName is "HMMSysCoreAPILib.dll" or "DataInsightLib.dll");

    internal static bool MarshalingCheck()
    {
        // Exercise actual managed-to-unmanaged marshaling in allocated memory, never a DLL call.
        var value = new WaveParams { ao3_static_mode = true, signal_invert = true, line_mode = true,
            external_line_trigger = true, scan_dir = true, trig0_mode = true,
            channel0_enable = true, channel1_enable = true, channel2_enable = true, channel3_enable = true,
            number_of_lines = 16, line_frequency_hz = 8000 };
        IntPtr memory = Marshal.AllocHGlobal(88);
        try
        {
            Marshal.Copy(new byte[88], 0, memory, 88);
            Marshal.StructureToPtr(value, memory, false);
            foreach (var offset in new[] { 24, 25, 26, 36, 56, 57, 84, 85, 86, 87 })
                if (Marshal.ReadByte(memory, offset) != 1) return false;
            if (Marshal.ReadInt32(memory, 28) != 16 || Marshal.ReadInt32(memory, 40) != 8000) return false;
            foreach (var field in typeof(WaveParams).GetFields().Where(f => f.FieldType == typeof(bool)))
                if (field.GetCustomAttribute<MarshalAsAttribute>()?.Value != UnmanagedType.I1) return false;
            var methods = typeof(HmmNative).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(m => m.GetCustomAttribute<DllImportAttribute>() is not null).ToArray();
            if (methods.Length != 10) return false;
            foreach (var method in methods)
            {
                var import = method.GetCustomAttribute<DllImportAttribute>()!;
                if (import.CallingConvention != CallingConvention.Cdecl || !import.ExactSpelling) return false;
                if (method.ReturnType == typeof(bool) && method.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>()?.Value != UnmanagedType.I1) return false;
                foreach (var parameter in method.GetParameters().Where(p => p.ParameterType == typeof(bool)))
                    if (parameter.GetCustomAttribute<MarshalAsAttribute>()?.Value != UnmanagedType.I1) return false;
            }
            return methods.Single(m => m.Name == "GetFrequency").ReturnType == typeof(void)
                && typeof(HmmNative.ImageCallback).GetMethod("Invoke")!.GetParameters()[0].ParameterType == typeof(IntPtr)
                && typeof(HmmNative.ErrorCallback).GetMethod("Invoke")!.GetParameters()[0].ParameterType == typeof(IntPtr);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    internal sealed record FieldResult(string Field, int Offset, int Expected);
    internal sealed record LayoutResult(string Type, int Size, int ExpectedSize, FieldResult[] Fields, bool Passed);
    internal sealed record FileResult(string File, bool Exists, string ExpectedHash, string? ActualHash, bool Passed)
    {
        public bool HashMatchesBaseline => string.Equals(ExpectedHash, ActualHash, StringComparison.OrdinalIgnoreCase);
        public bool HashInformational => File.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }
    internal sealed record DeploymentResult(string Directory, FileResult[] Files, bool X64Binaries, bool Passed, RuntimeCompatibility.Result[] NativeCompatibility);
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static LayoutResult Layout<T>(int size, params (string Name, int Offset)[] fields) where T : struct
    {
        var actual = fields.Select(f => new FieldResult(f.Name, Marshal.OffsetOf<T>(f.Name).ToInt32(), f.Offset)).ToArray();
        return new(typeof(T).Name, Marshal.SizeOf<T>(), size, actual,
            Environment.Is64BitProcess && Marshal.SizeOf<T>() == size && actual.All(f => f.Offset == f.Expected)
            && typeof(T).GetFields().Length == fields.Length);
    }

    internal static LayoutResult[] Abi() =>
    [
        Layout<WaveParams>(88,
            ("ao0_amplitude",0),("ao1_amplitude",4),("ao3_amplitude",8),
            ("ao0_offset",12),("ao1_offset",16),("ao3_offset",20),
            ("ao3_static_mode",24),("signal_invert",25),("line_mode",26),
            ("number_of_lines",28),("channel_start_delay",32),("external_line_trigger",36),
            ("line_frequency_hz",40),("pixel_per_line",44),("line_per_frame",48),("steps_per_stack",52),
            ("scan_dir",56),("trig0_mode",57),("line_average",60),("frame_average",64),
            ("oversample_head",68),("oversample_tail",72),("oversample_frame",76),("oversample_stack",80),
            ("channel0_enable",84),("channel1_enable",85),("channel2_enable",86),("channel3_enable",87)),
        Layout<FrequencyParams>(12,("line_frequency_hz",0),("pos_time",4),("neg_time",8)),
        Layout<CResampledImage>(48,("channel",0),("width",4),("height",8),("index0_line_number",12),
            ("index0",16),("index1_line_number",20),("index1",24),("image_data",32),("image_size",40)),
        Layout<ErrorMessage>(16,("error_message",0),("message_size",8))
    ];

    internal static DeploymentResult Deployment(string directory)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("sdk-baseline.json")!;
        using var manifest = JsonDocument.Parse(stream);
        const string prefix = "HMM_SYS_CoreEXE-20260422/";
        var results = new List<FileResult>();
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var source = file.GetProperty("path").GetString()!;
            if (!source.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var relative = source[prefix.Length..];
            if (!(relative.StartsWith("data/") || relative is "HMMSysCoreAPILib.dll" or "DataInsightLib.dll" or "io_settings.json")) continue;
            var path = Path.Combine(directory, relative);
            string? hash = null;
            try { if (File.Exists(path)) { using var input = File.OpenRead(path); hash = Convert.ToHexString(SHA256.HashData(input)); } }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var expected = file.GetProperty("sha256").GetString()!;
            results.Add(new(relative, File.Exists(path), expected, hash, hash is not null && (relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))));
        }
        var native = new[] { RuntimeCompatibility.Inspect(Path.Combine(directory, "HMMSysCoreAPILib.dll")),
            RuntimeCompatibility.Inspect(Path.Combine(directory, "DataInsightLib.dll"), false) };
        bool x64 = native.All(n => n.X64);
        return new(directory, results.ToArray(), x64, native.All(n => n.Passed) && results.Count == 15 && results.All(f => f.Passed), native);
    }
}
