using System.Text.Json;

namespace Gitwizz;

public static class Providers
{
    /// <summary>Local provider: each "PR" is a branch or ref in the current repository.</summary>
    public static (string TargetSha, List<PullRequest> Prs) Local(Git git, string target, IEnumerable<string> refs) =>
        (git.RevParse(target), refs.AsParallel().AsOrdered().Select(r => new PullRequest
        {
            Id = r, HeadRef = r, HeadSha = git.RevParse(r),
            Title = git.Run("log", "-1", "--format=%s", r), // head commit subject stands in for a PR title
        }).ToList());

    /// <summary>
    /// GitHub provider via the gh CLI. Includes PRs stacked on other selected PRs. details: also read the issues each
    /// PR closes, as linked work items with their acceptance criteria (two gh calls per PR, so meant for a single PR).
    /// </summary>
    public static (string TargetSha, List<PullRequest> Prs) GitHub(Git git, string target, HashSet<int>? only, BranchPolicy policy, bool details = false)
    {
        var json = Git.Exec(git.RepoDir, "gh", ["pr", "list", "--state", "open", "--limit", "500", "--json",
            "number,title,body,headRefName,baseRefName,headRefOid,isDraft,reviewDecision,statusCheckRollup,labels,mergeStateStatus,autoMergeRequest,author,url,latestReviews"]);
        if (json.ExitCode != 0) throw new InvalidOperationException("gh pr list failed: " + json.Stderr.Trim());

        var all = JsonDocument.Parse(json.Stdout).RootElement.EnumerateArray().Select(e => (
            Number: e.GetProperty("number").GetInt32(),
            Pr: new PullRequest
            {
                Id = "#" + e.GetProperty("number").GetInt32(),
                Title = e.GetProperty("title").GetString() ?? "",
                Body = e.GetProperty("body").GetString() ?? "",
                HeadRef = e.GetProperty("headRefName").GetString() ?? "",
                BaseRef = e.GetProperty("baseRefName").GetString() ?? "",
                HeadSha = e.GetProperty("headRefOid").GetString()!,
                IsDraft = e.GetProperty("isDraft").GetBoolean(),
                ReviewDecision = e.GetProperty("reviewDecision").GetString() is { Length: > 0 } rd ? rd : null,
                CiStatus = CiStatus(e.GetProperty("statusCheckRollup"), policy.RequiredChecks),
                MergeStateStatus = e.TryGetProperty("mergeStateStatus", out var ms) ? ms.GetString() : null,
                AutoMerge = e.TryGetProperty("autoMergeRequest", out var am) && am.ValueKind == JsonValueKind.Object,
                Labels = e.GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("name").GetString()!).ToList(),
                Author = e.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("login", out var login) ? login.GetString() ?? "" : "",
                Url = e.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                Reviewers = e.TryGetProperty("latestReviews", out var rv) && rv.ValueKind == JsonValueKind.Array ? Reviews(rv) : [],
                Checks = Checks(e.GetProperty("statusCheckRollup")),
            })).ToList();

        var prs = Select(all, target, only);
        if (details)
            foreach (var p in prs) p.WorkItems = LinkedIssues(git, int.Parse(p.Id[1..]));
        git.Run([.. new[] { "fetch", "--quiet", "origin", target }, .. prs.Select(p => $"refs/pull/{p.Id[1..]}/head")]);
        return (git.RevParse($"origin/{target}"), prs);
    }

    /// <summary>
    /// The PRs to plan: the given numbers, or all PRs into target plus PRs stacked (transitively) on top of them. Records
    /// each one's declared dependencies on open PRs outside the selection, which the plan can't satisfy.
    /// </summary>
    public static List<PullRequest> Select(List<(int Number, PullRequest Pr)> all, string target, HashSet<int>? only)
    {
        List<PullRequest> prs;
        if (only != null)
            prs = all.Where(x => only.Contains(x.Number)).Select(x => x.Pr).ToList();
        else
        {
            var bases = new HashSet<string> { target };
            prs = [];
            for (bool added = true; added;)
            {
                var next = all.Select(x => x.Pr).Where(p => bases.Contains(p.BaseRef) && !prs.Contains(p)).ToList();
                prs.AddRange(next);
                foreach (var p in next) bases.Add(p.HeadRef);
                added = next.Count > 0;
            }
        }
        var open = all.Select(x => x.Pr.Id).ToHashSet();
        var selected = prs.Select(p => p.Id).ToHashSet();
        foreach (var p in prs)
        {
            p.OpenDependencies = Analyzer.ExplicitDependencies(p).Where(open.Contains).Distinct().Order(StringComparer.Ordinal).ToList();
            p.OpenOutsideDependencies = p.OpenDependencies.Where(d => !selected.Contains(d)).ToList();
        }
        return prs;
    }

    /// <summary>Latest review per reviewer, as a provider-neutral vote.</summary>
    public static List<Reviewer> Reviews(JsonElement latestReviews) =>
        latestReviews.EnumerateArray().Select(r => new Reviewer(
            r.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "",
            (r.TryGetProperty("state", out var s) ? s.GetString() : null) switch
            {
                "APPROVED" => "approved",
                "CHANGES_REQUESTED" => "changes-requested",
                "COMMENTED" => "commented",
                _ => "none",
            })).ToList();

    /// <summary>Every check in a GitHub status rollup, as success, failure or pending.</summary>
    public static List<Check> Checks(JsonElement rollup)
    {
        string? S(JsonElement c, string p) => c.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (rollup.ValueKind != JsonValueKind.Array) return [];
        return rollup.EnumerateArray().Select(c => new Check(
            S(c, "name") ?? S(c, "context") ?? "check",
            S(c, "conclusion") is "FAILURE" or "CANCELLED" or "TIMED_OUT" or "ACTION_REQUIRED" || S(c, "state") is "FAILURE" or "ERROR" ? "failure"
            : (S(c, "status") is { } st && st != "COMPLETED") || S(c, "state") is "PENDING" or "EXPECTED" ? "pending"
            : "success",
            S(c, "detailsUrl") ?? S(c, "targetUrl"))).OrderBy(c => c.Name).ToList();
    }

    /// <summary>Issues the PR closes (closingIssuesReferences), with acceptance criteria from an "Acceptance criteria" section.</summary>
    static List<WorkItem> LinkedIssues(Git git, int number)
    {
        var r = Git.Exec(git.RepoDir, "gh", ["pr", "view", number.ToString(), "--json", "closingIssuesReferences"]);
        if (r.ExitCode != 0) return []; // older gh: no linked issues rather than no plan
        var issues = JsonDocument.Parse(r.Stdout).RootElement.GetProperty("closingIssuesReferences").EnumerateArray()
            .Select(i => i.GetProperty("number").GetInt32()).ToList();
        return issues.Select(n => Git.Exec(git.RepoDir, "gh", ["issue", "view", n.ToString(), "--json", "number,title,state,body,url"]))
            .Where(x => x.ExitCode == 0).Select(x => Issue(JsonDocument.Parse(x.Stdout).RootElement)).ToList();
    }

    /// <summary>A GitHub issue (gh issue view --json number,title,state,body,url) as a work item.</summary>
    public static WorkItem Issue(JsonElement i) => new(
        "#" + i.GetProperty("number").GetInt32(), "Issue", i.GetProperty("title").GetString() ?? "", i.GetProperty("state").GetString() ?? "",
        Requirements.CriteriaSection(i.TryGetProperty("body", out var b) ? b.GetString() ?? "" : ""),
        i.TryGetProperty("url", out var u) ? u.GetString() : null);

    /// <summary>Commits on origin/target missing from the local target, or 0 (no such remote branch, or up to date).</summary>
    public static int BehindRemote(Git git, string target) =>
        git.Try("rev-list", "--count", $"{target}..refs/remotes/origin/{target}") is { ExitCode: 0 } r ? int.Parse(r.Stdout.Trim()) : 0;

    /// <summary>
    /// Rollup of the checks that gate merging: the required ones if the branch names any, else all.
    /// A required check that hasn't reported yet counts as pending, as GitHub won't merge without it.
    /// </summary>
    public static string? CiStatus(JsonElement rollup, IReadOnlySet<string> required)
    {
        string? S(JsonElement c, string p) => c.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var all = rollup.ValueKind == JsonValueKind.Array ? rollup.EnumerateArray().ToList() : [];
        var checks = all.Where(c => required.Count == 0 || required.Contains(S(c, "name") ?? S(c, "context") ?? "")).ToList();
        if (checks.Any(c => S(c, "conclusion") is "FAILURE" or "CANCELLED" or "TIMED_OUT" or "ACTION_REQUIRED" || S(c, "state") is "FAILURE" or "ERROR"))
            return "FAILURE";
        if (checks.Any(c => (S(c, "status") is { } s && s != "COMPLETED") || S(c, "state") is "PENDING" or "EXPECTED")
            || required.Except(all.Select(c => S(c, "name") ?? S(c, "context"))).Any())
            return "PENDING";
        return checks.Count == 0 ? null : "SUCCESS";
    }

    /// <summary>Required checks, merge queue and linear history of the target, from branch protection and rulesets.</summary>
    public static BranchPolicy GitHubPolicy(Git git, string target)
    {
        // Both endpoints only need read access; on failure (no gh auth, unknown branch) assume no policy.
        string Api(string path) => Git.Exec(git.RepoDir, "gh", ["api", path]) is { ExitCode: 0 } r ? r.Stdout : "";
        var branch = Uri.EscapeDataString(target);
        return ParsePolicy(Api($"repos/{{owner}}/{{repo}}/branches/{branch}"), Api($"repos/{{owner}}/{{repo}}/rules/branches/{branch}"));
    }

    /// <summary>
    /// Branch policy from GitHub's branch and rulesets JSON: required checks, merge queue and its method, linear history.
    /// An empty string means that API call returned nothing.
    /// </summary>
    public static BranchPolicy ParsePolicy(string branchJson, string rulesJson)
    {
        var policy = new BranchPolicy([]);
        if (branchJson != "" && JsonDocument.Parse(branchJson).RootElement is var b
            && b.TryGetProperty("protection", out var p) && p.TryGetProperty("required_status_checks", out var rsc)
            && rsc.TryGetProperty("contexts", out var contexts))
            policy.RequiredChecks.UnionWith(contexts.EnumerateArray().Select(c => c.GetString()!));
        if (rulesJson == "") return policy;
        foreach (var rule in JsonDocument.Parse(rulesJson).RootElement.EnumerateArray())
        {
            var prm = rule.TryGetProperty("parameters", out var x) ? x : default;
            switch (rule.GetProperty("type").GetString())
            {
                case "merge_queue":
                    var method = prm.ValueKind == JsonValueKind.Object && prm.TryGetProperty("merge_method", out var m) ? m.GetString() : null;
                    policy = policy with
                    {
                        MergeQueue = true,
                        QueueStrategy = method switch { "SQUASH" => MergeStrategy.Squash, "REBASE" => MergeStrategy.Rebase, "MERGE" => MergeStrategy.Merge, _ => null },
                    };
                    break;
                case "required_status_checks":
                    policy.RequiredChecks.UnionWith(prm.GetProperty("required_status_checks").EnumerateArray()
                        .Select(c => c.GetProperty("context").GetString()!));
                    break;
                case "required_linear_history":
                    policy = policy with { LinearHistory = true };
                    break;
            }
        }
        return policy;
    }
}
