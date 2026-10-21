using System.Text.Json;

namespace Gitwizz;

/// <summary>Optional GitHub evidence for inspect output. Provider failures never turn local history into a failed inspection.</summary>
public static class InspectLinks
{
    public record WorkItem(string Id, string? Title, string? Url, string Source);

    public static FileInspect.DirectoryResult Enrich(Git git, FileInspect.DirectoryResult report, ProgressBars? progress = null)
    {
        if (!report.Files.Any(entry => entry.PullRequest != null)) return report;
        Dictionary<string, (string? Title, string? Url, IReadOnlyList<WorkItem> Items)> resolved = new(StringComparer.Ordinal);
        List<FileInspect.DirectoryEntry> rows = new();
        List<string> warnings = report.Warnings.ToList();
        int total = report.Files.Where(e => e.PullRequest != null).Select(e => e.PullRequest).Distinct(StringComparer.Ordinal).Count();
        if (total >= 5) progress?.Start($"Loading {total} pull request details", total);
        foreach (FileInspect.DirectoryEntry entry in report.Files)
        {
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
                progress?.Advance();
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
