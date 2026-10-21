namespace Gitwizz;

/// <summary>Explicit or remote-derived provider selection for inspection enrichment.</summary>
public static class InspectProvider
{
    public const string Github = "github";
    public const string AzureDevOps = "azure-devops";

    /// <summary>Returns a recognized provider, or null for local-only evidence.</summary>
    public static string? Select(string? requested, string? remote)
    {
        if (requested != null)
            return requested switch
            {
                Github => Github,
                AzureDevOps or "ado" or "azure" => AzureDevOps,
                _ => throw new ArgumentException($"Unknown inspect provider: {requested}")
            };
        if (Gitwizz.AzureDevOps.IsRemote(remote ?? "")) return AzureDevOps;
        if (remote?.Contains("github.com", StringComparison.OrdinalIgnoreCase) == true) return Github;
        return null;
    }
}
