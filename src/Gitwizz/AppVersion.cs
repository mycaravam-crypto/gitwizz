using System.Reflection;

namespace Gitwizz;

/// <summary>Reports the SemVer from .NET assembly metadata, keeping prerelease labels but omitting build metadata.</summary>
public static class AppVersion
{
    public static string Display(Assembly assembly) =>
        Normalize(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string Normalize(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return "unknown";
        var version = informationalVersion.Split('+', 2)[0].Trim();
        return version.Length > 0 ? version : "unknown";
    }
}
