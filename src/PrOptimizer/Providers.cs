using System.Text.Json;

namespace PrOptimizer;

public static class Providers
{
    /// <summary>Local provider: each "PR" is a branch or ref in the current repository.</summary>
    public static (string TargetSha, List<PullRequest> Prs) Local(Git git, string target, IEnumerable<string> refs) =>
        (git.RevParse(target), refs.AsParallel().AsOrdered().Select(r => new PullRequest
        {
            Id = r, HeadRef = r, HeadSha = git.RevParse(r),
            Title = git.Run("log", "-1", "--format=%s", r), // head commit subject stands in for a PR title
        }).ToList());

    /// <summary>GitHub provider via the gh CLI. Includes PRs stacked on other selected PRs.</summary>
    public static (string TargetSha, List<PullRequest> Prs) GitHub(Git git, string target, HashSet<int>? only, BranchPolicy policy)
    {
        var json = Git.Exec(git.RepoDir, "gh", ["pr", "list", "--state", "open", "--limit", "500", "--json",
            "number,title,body,headRefName,baseRefName,headRefOid,isDraft,reviewDecision,statusCheckRollup,labels,mergeStateStatus,autoMergeRequest"]);
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
            })).ToList();

        List<PullRequest> prs;
        if (only != null)
            prs = all.Where(x => only.Contains(x.Number)).Select(x => x.Pr).ToList();
        else
        {
            // All PRs targeting the branch, plus PRs stacked (transitively) on top of them.
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

        // A declared dependency on an open PR that isn't in the plan can't be satisfied by the plan: remember it.
        var open = all.Select(x => x.Pr.Id).ToHashSet();
        var selected = prs.Select(p => p.Id).ToHashSet();
        foreach (var p in prs)
            p.OpenOutsideDependencies = Analyzer.ExplicitDependencies(p).Where(d => open.Contains(d) && !selected.Contains(d)).Distinct().ToList();

        git.Run([.. new[] { "fetch", "--quiet", "origin", target }, .. prs.Select(p => $"refs/pull/{p.Id[1..]}/head")]);
        return (git.RevParse($"origin/{target}"), prs);
    }

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
