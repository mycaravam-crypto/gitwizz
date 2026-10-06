using System.Text.Json;

namespace PrOptimizer;

public static class Providers
{
    /// <summary>Local provider: each "PR" is a branch or ref in the current repository.</summary>
    public static (string TargetSha, List<PullRequest> Prs) Local(Git git, string target, IEnumerable<string> refs) =>
        (git.RevParse(target), refs.Select(r => new PullRequest { Id = r, Title = r, HeadRef = r, HeadSha = git.RevParse(r) }).ToList());

    /// <summary>GitHub provider via the gh CLI. Includes PRs stacked on other selected PRs.</summary>
    public static (string TargetSha, List<PullRequest> Prs) GitHub(Git git, string target, HashSet<int>? only)
    {
        var json = Git.Exec(git.RepoDir, "gh", ["pr", "list", "--state", "open", "--limit", "500", "--json",
            "number,title,body,headRefName,baseRefName,headRefOid,isDraft,reviewDecision,statusCheckRollup,labels"]);
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
                CiStatus = CiStatus(e.GetProperty("statusCheckRollup")),
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

        git.Run([.. new[] { "fetch", "--quiet", "origin", target }, .. prs.Select(p => $"refs/pull/{p.Id[1..]}/head")]);
        return (git.RevParse($"origin/{target}"), prs);
    }

    static string? CiStatus(JsonElement rollup)
    {
        if (rollup.ValueKind != JsonValueKind.Array || rollup.GetArrayLength() == 0) return null;
        string? S(JsonElement c, string p) => c.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var checks = rollup.EnumerateArray().ToList();
        if (checks.Any(c => S(c, "conclusion") is "FAILURE" or "CANCELLED" or "TIMED_OUT" or "ACTION_REQUIRED" || S(c, "state") is "FAILURE" or "ERROR"))
            return "FAILURE";
        if (checks.Any(c => (S(c, "status") is { } s && s != "COMPLETED") || S(c, "state") is "PENDING" or "EXPECTED"))
            return "PENDING";
        return "SUCCESS";
    }
}
