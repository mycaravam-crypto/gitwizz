using System.Text.Json;

namespace Gitwizz;

/// <summary>Optional GitHub evidence for inspect output. Provider failures never turn local history into a failed inspection.</summary>
public static class InspectLinks
{
    public record WorkItem(string Id, string? Title, string? Url, string Source);

    public static FileInspect.DirectoryResult Enrich(Git git, FileInspect.DirectoryResult report)
    {

        Dictionary<string, (string? Title, string? Url, IReadOnlyList<WorkItem> Items)> resolved = new(StringComparer.Ordinal);
        List<FileInspect.DirectoryEntry> rows = new();
        List<string> warnings = report.Warnings.ToList();
        Dictionary<string, string?> shaToPr = new(StringComparer.Ordinal);
        foreach (FileInspect.DirectoryEntry original in report.Files)
        {
            FileInspect.DirectoryEntry entry = original;
            if (entry.PullRequest == null)
            {
                if (!shaToPr.TryGetValue(entry.Sha, out string? discovered))
                {
                    discovered = FindPullRequest(git, entry.Sha);
                    shaToPr.Add(entry.Sha, discovered);
                }
                if (discovered != null) entry = entry with { PullRequest = discovered };
            }
            if (entry.PullRequest is not { } reference
                || !int.TryParse(reference.TrimStart('#'), out int number))
            {
                rows.Add(entry);
                continue;
            }
            if (!resolved.TryGetValue(reference, out var detail))
            {
                GitResult command = Git.Exec(git.RepoDir, "gh",
                    ["pr", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--json", "number,title,url,closingIssuesReferences"],
                    timeout: TimeSpan.FromSeconds(20));
                if (command.ExitCode != 0)
                {
                    warnings.Add($"Could not verify GitHub PR {reference} (gh not authenticated, missing, or inaccessible).");
                    resolved[reference] = (null, null, Array.Empty<WorkItem>());
                }
                else
                {
                    try { resolved[reference] = ParseGitHubPr(command.Stdout, number); }
                    catch (JsonException)
                    {
                        warnings.Add($"GitHub PR {reference} returned invalid JSON.");
                        resolved[reference] = (null, null, Array.Empty<WorkItem>());
                    }
                }
                detail = resolved[reference];
            }
            rows.Add(entry with
            {
                PullRequestTitle = detail.Title,
                PullRequestUrl = detail.Url,
                LinkedWorkItems = detail.Items
            });
        }
        return report with { Files = rows.AsReadOnly(), Warnings = warnings.AsReadOnly() };
    }

    /// <summary>Ask GitHub for the PR containing the exact commit. No guess when association is missing or ambiguous.</summary>
    public static string? FindPullRequest(Git git, string sha)
    {
        GitResult result = Git.Exec(git.RepoDir, "gh",
            ["api", "--header", "Accept: application/vnd.github+json",
             $"repos/{{owner}}/{{repo}}/commits/{sha}/pulls"],
            timeout: TimeSpan.FromSeconds(20));
        if (result.ExitCode != 0) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(result.Stdout);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            JsonElement[] merged = doc.RootElement.EnumerateArray()
                .Where(pr => pr.TryGetProperty("merged_at", out JsonElement date)
                    && date.ValueKind == JsonValueKind.String)
                .ToArray();
            return merged.Length == 1 && merged[0].TryGetProperty("number", out JsonElement number)
                ? "#" + number.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Parse the provider-verified metadata of a PR without asserting its relation to the inspected commit.</summary>
    public static (string? Title, string? Url, IReadOnlyList<WorkItem> Items) ParseGitHubPr(string json, int expectedNumber)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("number", out JsonElement id) || id.GetInt32() != expectedNumber)
            throw new JsonException("PR number mismatch");
        string? title = root.TryGetProperty("title", out JsonElement t) ? t.GetString() : null;
        string? url = root.TryGetProperty("url", out JsonElement u) ? u.GetString() : null;
        List<WorkItem> items = new();
        if (root.TryGetProperty("closingIssuesReferences", out JsonElement links) && links.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in links.EnumerateArray())
            {
                string identifier = "#" + item.GetProperty("number").GetInt32();
                string? itemTitle = item.TryGetProperty("title", out JsonElement it) ? it.GetString() : null;
                string? itemUrl = item.TryGetProperty("url", out JsonElement iu) ? iu.GetString() : null;
                items.Add(new WorkItem(identifier, itemTitle, itemUrl, "github-pr-closing-issue"));
            }
        return (title, url, items.AsReadOnly());
    }
}
