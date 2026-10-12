using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace HmmExample;

// Static PE inspection only: never loads a DLL or executes DllMain. Export presence
// is a minimum compatibility check, not proof of native parameter layout semantics.
internal static class RuntimeCompatibility
{
    internal static readonly string[] RequiredExports =
    ["initialize", "setParameters", "setAux", "setAo2Value", "getFrequency", "startScan", "stopScan",
        "setResampledImageCallback", "setErrorMessageCallback"];
    internal sealed record Result(string Path, bool Exists, bool X64, string[] Exports, string[] MissingExports, string? Error)
    {
        public bool Passed => Exists && X64 && MissingExports.Length == 0 && Error is null;
    }

    internal static Result Inspect(string path, bool requireCoreExports = true)
    {
        bool exists = File.Exists(path), x64 = false;
        var exports = new HashSet<string>(StringComparer.Ordinal);
        var required = requireCoreExports ? RequiredExports : [];
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            x64 = pe.PEHeaders.CoffHeader.Machine == Machine.Amd64 &&
                pe.PEHeaders.PEHeader?.Magic == PEMagic.PE32Plus &&
                pe.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.Dll);
            if (requireCoreExports)
            {
                var table = pe.PEHeaders.PEHeader?.ExportTableDirectory ?? default;
                if (table.RelativeVirtualAddress == 0 || table.Size < 40) throw new BadImageFormatException("Missing export directory.");
                byte[] Read(int rva, int size) => pe.GetSectionData(rva).GetContent(0, size).ToArray();
                int Int(byte[] bytes, int offset) => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)));
                var header = Read(table.RelativeVirtualAddress, 40);
                int functions = Int(header, 20), names = Int(header, 24);
                if (functions > 100000 || names > 100000) throw new BadImageFormatException("Excessive export count.");
                var addresses = Read(Int(header, 28), checked(functions * 4));
                var nameRvas = Read(Int(header, 32), checked(names * 4));
                var ordinals = Read(Int(header, 36), checked(names * 2));
                for (int i = 0; i < names; i++)
                {
                    int ordinal = BinaryPrimitives.ReadUInt16LittleEndian(ordinals.AsSpan(i * 2, 2));
                    if (ordinal >= functions) throw new BadImageFormatException("Invalid export ordinal.");
                    if (Int(addresses, ordinal * 4) == 0) continue;
                    var block = pe.GetSectionData(Int(nameRvas, i * 4));
                    var bytes = block.GetContent(0, Math.Min(block.Length, 1024)).ToArray();
                    int end = Array.IndexOf(bytes, (byte)0);
                    if (end < 0) throw new BadImageFormatException("Unterminated export name.");
                    exports.Add(Encoding.ASCII.GetString(bytes, 0, end));
                }
            }
            return new(path, exists, x64, exports.Order().ToArray(), required.Except(exports).ToArray(), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or OverflowException)
        {
            return new(path, exists, x64, exports.Order().ToArray(), required.Except(exports).ToArray(), error.Message);
        }
    }

    internal static void Require(string directory)
    {
        foreach (var result in new[] { Inspect(System.IO.Path.Combine(directory, "HMMSysCoreAPILib.dll")),
            Inspect(System.IO.Path.Combine(directory, "DataInsightLib.dll"), false) })
            if (!result.Passed) throw new BadImageFormatException(
                $"SDK compatibility check failed: {result.Path}; x64={result.X64}; missing exports={string.Join(", ", result.MissingExports)}; {result.Error}");
    }
}
