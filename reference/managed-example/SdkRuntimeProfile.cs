using System.Security.Cryptography;
namespace HmmExample;
internal enum SdkProfileKind { Unknown, Baseline350, Test352 }
internal sealed record SdkRuntimeProfile(string Sha256,SdkProfileKind Kind)
{
    internal const string BaselineHash="7940722B4CAA48049DCBF2D2E98437B4D446346126DD900171BD283DA2012B7F";
    // Informational labels only. Unlisted DLLs need no registration and follow the same lifecycle policy.
    private static readonly HashSet<string> recognized352Hashes=new(StringComparer.OrdinalIgnoreCase);
    public string Name=>Kind switch {SdkProfileKind.Baseline350=>"3.5.0 baseline",SdkProfileKind.Test352=>"3.5.2 Test",_=>"Unknown / manually supplied DLL"};
    internal static SdkRuntimeProfile Recognize(string hash)=>new(hash,
        hash.Equals(BaselineHash,StringComparison.OrdinalIgnoreCase)?SdkProfileKind.Baseline350:
        recognized352Hashes.Contains(hash)?SdkProfileKind.Test352:SdkProfileKind.Unknown);
    internal static SdkRuntimeProfile Inspect(string path)
    {
        try {using var stream=File.OpenRead(path);return Recognize(Convert.ToHexString(SHA256.HashData(stream)));}
        catch(IOException) {return new("Unavailable",SdkProfileKind.Unknown);}
        catch(UnauthorizedAccessException) {return new("Unavailable",SdkProfileKind.Unknown);}
    }
}
