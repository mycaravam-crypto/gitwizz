using System.Text.Json;

namespace Gitwizz;

/// <summary>Access to Azure DevOps Server: runs a command and returns its JSON output, or throws with a message free of secrets.</summary>
public interface IAdoClient
{
    /// <summary>Runs one ado command (e.g. "pr", "list", "--json") and returns stdout.</summary>
    string Run(params string[] args);
}

/// <summary>
/// The ado CLI (github.com/mycaravam-crypto/ado-devops) as the API adapter: server, project, PAT, API version, TLS and
/// proxy all come from its configuration (ado auth login, ADO_* variables), so gitwizz never sees or logs a credential.
/// </summary>
public sealed class AdoCli(string dir, string? executable = null) : IAdoClient
{
    /// <summary>The ado executable: GITWIZZ_ADO, else "ado" on the PATH.</summary>
    public string Executable { get; } = executable ?? Environment.GetEnvironmentVariable("GITWIZZ_ADO") ?? "ado";

    /// <summary>Runs ado; maps its exit codes (127 missing, 3 not logged in, 4 denied, 5 not found) to clear errors.</summary>
    public string Run(params string[] args)
    {
        var r = Git.Exec(dir, Executable, args, timeout: TimeSpan.FromMinutes(5));
        if (r.ExitCode == 0) return r.Stdout;
        var what = $"ado {string.Join(' ', args.TakeWhile(a => !a.StartsWith('-')))}";
        throw new InvalidOperationException(r.ExitCode switch
        {
            127 => $"{what} failed: '{Executable}' not found (install ado from github.com/mycaravam-crypto/ado-devops or set GITWIZZ_ADO)",
            3 => $"{what} failed: not logged in to Azure DevOps (exit 3): {r.Stderr.Trim()}",
            4 => $"{what} failed: permission denied (exit 4): {r.Stderr.Trim()}",
            5 => $"{what} failed: not found (exit 5): {r.Stderr.Trim()}",
            124 => $"{what} timed out",
            _ => $"{what} failed (exit {r.ExitCode}): {r.Stderr.Trim()}",
        });
    }
}

/// <summary>Azure DevOps Server provider: pull requests, reviewer votes, PR builds and linked work items, normalized.</summary>
public static class AzureDevOps
{
    /// <summary>True for an Azure DevOps remote URL (dev.azure.com, *.visualstudio.com, or an on-prem /_git/ path).</summary>
    public static bool IsRemote(string url) =>
        url.Contains("dev.azure.com") || url.Contains(".visualstudio.com") || url.Contains("/_git/");

    /// <summary>
    /// Active PRs into target (and PRs stacked on them), or only the given ids. details: also read each PR's linked
    /// work items (one ado pr context call per PR, so meant for a single PR). Fetches target and source branches.
    /// </summary>
    public static (string TargetSha, List<PullRequest> Prs) Load(Git git, string target, HashSet<int>? only, IAdoClient ado, bool details = false)
    {
        var all = JsonDocument.Parse(ado.Run("pr", "list", "--json", "--status", "active")).RootElement.EnumerateArray()
            .Select(e => (Number: e.GetProperty("pullRequestId").GetInt32(), Json: e.Clone())).ToList();
        var stubs = all.Select(x => (x.Number, Pr: new PullRequest
        {
            Id = "#" + x.Number, HeadSha = "", HeadRef = Branch(Str(x.Json, "sourceRefName")), BaseRef = Branch(Str(x.Json, "targetRefName")),
            Body = Str(x.Json, "description"),
        })).ToList();
        var selected = Providers.Select(stubs, target, only).Select(p => int.Parse(p.Id[1..])).ToHashSet();
        if (selected.Count == 0) return (git.RevParse(target), []);

        // PR builds run on refs/pull/N/merge; the latest per definition is the PR's check. No access: no checks.
        List<JsonElement> builds;
        try { builds = JsonDocument.Parse(ado.Run("build", "list", "--json", "--limit", "500")).RootElement.EnumerateArray().Select(b => b.Clone()).ToList(); }
        catch (InvalidOperationException) { builds = []; }

        var prs = all.Where(x => selected.Contains(x.Number)).Select(x => Parse(x.Json, Checks(builds, x.Number))).ToList();
        foreach (var pr in prs)
        {
            var stub = stubs.Single(s => s.Pr.Id == pr.Id).Pr;
            pr.OpenOutsideDependencies = stub.OpenOutsideDependencies;
            pr.OpenDependencies = stub.OpenDependencies;
            if (details)
                pr.WorkItems = LinkedWorkItems(ado, pr.Id[1..]);
        }
        git.Run([.. new[] { "fetch", "--quiet", "origin", target }, .. prs.Select(p => "refs/heads/" + p.HeadRef).Distinct()]);
        foreach (var pr in prs.Where(p => git.Try("cat-file", "-e", p.HeadSha + "^{commit}").ExitCode != 0))
            throw new InvalidOperationException($"pull request {pr.Id}: source commit {pr.HeadSha} not found after fetching {pr.HeadRef}");
        return (git.RevParse($"origin/{target}"), prs);
    }

    static string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    static string Branch(string refName) => refName.StartsWith("refs/heads/") ? refName["refs/heads/".Length..] : refName;

    /// <summary>A PR from ado's JSON (pr list / pr context), with the given checks.</summary>
    public static PullRequest Parse(JsonElement e, List<Check> checks)
    {
        var reviewers = e.TryGetProperty("reviewers", out var rv) && rv.ValueKind == JsonValueKind.Array
            ? rv.EnumerateArray().Select(r => new Reviewer(Str(r, "displayName"), Vote(r.TryGetProperty("vote", out var v) ? v.GetInt32() : 0),
                r.TryGetProperty("isRequired", out var req) && req.ValueKind is JsonValueKind.True or JsonValueKind.False ? req.GetBoolean() : null)).ToList()
            : [];
        var number = e.GetProperty("pullRequestId").GetInt32();
        return new PullRequest
        {
            Id = "#" + number,
            Title = Str(e, "title"),
            Body = Str(e, "description"),
            HeadRef = Branch(Str(e, "sourceRefName")),
            BaseRef = Branch(Str(e, "targetRefName")),
            HeadSha = e.TryGetProperty("lastMergeSourceCommit", out var c) && c.ValueKind == JsonValueKind.Object ? Str(c, "commitId") : "",
            Author = e.TryGetProperty("createdBy", out var cb) && cb.ValueKind == JsonValueKind.Object ? Str(cb, "displayName") : "",
            IsDraft = e.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True,
            Labels = e.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().Select(x => Str(x, "name")).ToList() : [],
            Reviewers = reviewers,
            ReviewDecision = ReviewDecision(reviewers),
            Checks = checks,
            CiStatus = CiStatus(checks),
        };
    }

    /// <summary>Azure DevOps vote: 10 approved, 5 approved with suggestions, 0 none, -5 waiting for author, -10 rejected.</summary>
    public static string Vote(int vote) => vote switch
    {
        >= 10 => "approved",
        >= 5 => "approved-with-suggestions",
        <= -10 => "changes-requested",
        <= -5 => "waiting-for-author",
        _ => "none",
    };

    /// <summary>GitHub-style decision from votes: a rejection or "waiting for author" wins, then an approval; null when nobody voted.</summary>
    public static string? ReviewDecision(List<Reviewer> reviewers) =>
        reviewers.Any(r => r.Vote is "changes-requested" or "waiting-for-author") ? "CHANGES_REQUESTED"
        : reviewers.Any(r => r.Required == true && r.Vote is "none") ? "REVIEW_REQUIRED"
        : reviewers.Any(r => r.Vote.StartsWith("approved")) ? "APPROVED"
        : null;

    /// <summary>The latest build per definition on the PR's merge ref (ado build list is newest first).</summary>
    public static List<Check> Checks(IEnumerable<JsonElement> builds, int pr) =>
        builds.Where(b => Str(b, "sourceBranch") == $"refs/pull/{pr}/merge")
            .GroupBy(b => b.TryGetProperty("definition", out var d) ? Str(d, "name") : "build")
            .Select(g => g.First())
            .Select(b => new Check(
                b.TryGetProperty("definition", out var d) ? Str(d, "name") : "build",
                Str(b, "status") != "completed" ? "pending" : Str(b, "result") == "succeeded" ? "success" : "failure",
                b.TryGetProperty("_links", out var l) && l.TryGetProperty("web", out var w) ? Str(w, "href") : null))
            .OrderBy(c => c.Name).ToList();

    /// <summary>FAILURE if a check failed, PENDING if one is still running, SUCCESS if all passed, null without checks.</summary>
    public static string? CiStatus(List<Check> checks) =>
        checks.Any(c => c.Status == "failure") ? "FAILURE" : checks.Any(c => c.Status == "pending") ? "PENDING" : checks.Count > 0 ? "SUCCESS" : null;

    /// <summary>
    /// The PR's linked work items with all their fields. ado pr context names them but returns only title, type and
    /// state, so each is read again with ado workitem show, which returns every field (acceptance criteria and
    /// description included). One that can't be read in full keeps its summary, without criteria.
    /// </summary>
    public static List<WorkItem> LinkedWorkItems(IAdoClient ado, string pr)
    {
        var context = JsonDocument.Parse(ado.Run("pr", "context", pr)).RootElement;
        if (!context.TryGetProperty("workItems", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        return items.EnumerateArray().Select(summary =>
        {
            try { return ParseWorkItem(JsonDocument.Parse(ado.Run("workitem", "show", summary.GetProperty("id").GetInt32().ToString(), "--json")).RootElement); }
            catch (InvalidOperationException) { return ParseWorkItem(summary); }
        }).OrderBy(w => int.Parse(w.Id[3..])).ToList();
    }

    /// <summary>
    /// A work item from ado's JSON ({id, fields}): type, title, state and acceptance criteria (the AcceptanceCriteria
    /// field, else an "Acceptance criteria" section in the description).
    /// </summary>
    public static WorkItem ParseWorkItem(JsonElement w)
    {
        var f = w.TryGetProperty("fields", out var x) ? x : default;
        string Field(string name) => f.ValueKind == JsonValueKind.Object && f.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Object && v.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()
            : "";
        var ac = Field("Microsoft.VSTS.Common.AcceptanceCriteria");
        return new WorkItem($"AB#{w.GetProperty("id").GetInt32()}", Field("System.WorkItemType"), Field("System.Title"), Field("System.State"),
            ac.Trim() != "" ? Requirements.Criteria(ac) : Requirements.CriteriaSection(Field("System.Description")));
    }
}
