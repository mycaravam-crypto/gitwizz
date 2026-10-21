using System.Text.Json;

namespace Gitwizz;

/// <summary>Azure DevOps Server evidence enrichment for file inspection, via the existing ado CLI adapter.</summary>
public static class InspectAdo
{
    /// <summary>Enriches commit rows with verified commit work-item links and available PR context.</summary>
    public static FileInspect.DirectoryResult Enrich(FileInspect.DirectoryResult report, IAdoClient ado)
    {
        Dictionary<string, HistoryCommitLinks> commits = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, (ProviderPr Pr, IReadOnlyList<InspectLinks.WorkItem> Items)> prs = new();
        List<FileInspect.DirectoryEntry> rows = new();
        List<string> warnings = report.Warnings.ToList();
        AdoHistory history = new(ado);
        foreach (FileInspect.DirectoryEntry entry in report.Files)
        {
            if (!commits.TryGetValue(entry.Sha, out HistoryCommitLinks? links))
            {
                links = history.CommitLinks(entry.Sha);
                commits.Add(entry.Sha, links);
            }
            List<InspectLinks.WorkItem> linked = new();
            if (links.Status == "verified" && links.Items != null)
                foreach (HistoryLink link in links.Items)
                {
                    string? title = null;
                    try
                    {
                        string id = link.WorkItem.Replace("AB#", "", StringComparison.Ordinal);
                        using JsonDocument doc = JsonDocument.Parse(ado.Run("workitem", "show", id, "--json"));
                        title = AzureDevOps.ParseWorkItem(doc.RootElement).Title;
                    }
                    catch (InvalidOperationException) { /* Keep the confirmed link if title lookup fails. */ }
                    linked.Add(new InspectLinks.WorkItem(link.WorkItem, title, link.Uri, "ado-commit-link"));
                }
            else if (links.Status != "verified")
                warnings.Add($"ADO commit links unavailable for {entry.Sha[..12]}: {links.Status}.");
            string? title = null;
            string? reference = entry.PullRequest;
            if (reference != null && int.TryParse(reference.TrimStart('#'), out int number))
            {
                if (!prs.TryGetValue(number, out var details))
                {
                    try
                    {
                        ProviderPr pr = history.PullRequest(number);
                        List<InspectLinks.WorkItem> items = new();
                        foreach (HistoryLink link in pr.WorkItems ?? [])
                        {
                            string? itemTitle = null;
                            try
                            {
                                string id = link.WorkItem.Replace("AB#", "", StringComparison.Ordinal);
                                using JsonDocument doc = JsonDocument.Parse(ado.Run("workitem", "show", id, "--json"));
                                itemTitle = AzureDevOps.ParseWorkItem(doc.RootElement).Title;
                            }
                            catch (InvalidOperationException) { /* Preserve the verified link without an optional title. */ }
                            items.Add(new InspectLinks.WorkItem(link.WorkItem, itemTitle, link.Uri, "ado-pr-link"));
                        }
                        details = (pr, items.AsReadOnly());
                        prs.Add(number, details);
                    }
                    catch (InvalidOperationException)
                    {
                        warnings.Add($"ADO pull request {reference} unavailable; showing commit evidence only.");
                    }
                }
                if (prs.TryGetValue(number, out details))
                {
                    // Verify that the provider attributes this commit to the PR.
                    bool exact = details.Pr.MergeCommit == entry.Sha
                        || details.Pr.Commits.Contains(entry.Sha, StringComparer.OrdinalIgnoreCase);
                    if (details.Pr.Completed && exact)
                    {
                        title = details.Pr.Title;
                        linked.AddRange(details.Items);
                    }
                    else warnings.Add($"Commit {entry.Sha[..12]} -> PR {reference} could not be verified.");
                }
            }
            rows.Add(entry with
            {
                PullRequestTitle = title,
                LinkedWorkItems = linked.DistinctBy(item => item.Id).ToList().AsReadOnly()
            });
        }
        return report with { Files = rows.AsReadOnly(), Warnings = warnings.Distinct().ToList().AsReadOnly() };
    }
}
