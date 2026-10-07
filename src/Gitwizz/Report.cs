using System.Text;
using System.Text.Json;

namespace Gitwizz;

public static class Report
{
    public static string Status(PlanStep s) => s.RegenerateFiles.Count > 0 ? "REGENERATE" : "CLEAN";
    public static string Status(BlockedPr b) => b.Policy ? "POLICY BLOCKED" : "BLOCKED";

    /// <summary>Pair weights are heuristics, not probabilities, so they are shown as levels, never as percentages.</summary>
    public static string RiskLevel(double weight) => weight < 0.3 ? "low" : weight < 0.6 ? "medium" : "high";

    /// <summary>Plain-text report (--format text): notes, numbered steps with status, cost and reason, blocked PRs, next step.</summary>
    public static string Text(Plan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PR MERGE PLAN");
        sb.AppendLine($"Target: {plan.Target}");
        sb.AppendLine($"Strategy: {plan.Strategy.ToString().ToLowerInvariant()}");
        foreach (var n in plan.Notes) sb.AppendLine($"Note: {n}");
        sb.AppendLine();

        int i = 1;
        foreach (var s in plan.Steps)
        {
            sb.AppendLine($"{i++,-3}{s.Pr.Id}  {s.Pr.Title}");
            sb.AppendLine($"   {Status(s)}");
            if (s.RegenerateFiles.Count > 0) sb.AppendLine($"   {string.Join(", ", s.RegenerateFiles)}");
            sb.AppendLine($"   Cost: {s.Cost:0.##}");
            sb.AppendLine($"   Reason: {s.Reason}");
            sb.AppendLine();
        }
        foreach (var b in plan.Blocked)
        {
            sb.AppendLine($"{i++,-3}{b.Pr.Id}  {b.Pr.Title}");
            sb.AppendLine($"   {Status(b)}");
            sb.AppendLine($"   Reason: {b.Reason}");
            sb.AppendLine();
        }

        if (plan.Parallelizable.Count > 0)
        {
            sb.AppendLine("Currently independent candidates (parallelizable):");
            foreach (var p in plan.Parallelizable) sb.AppendLine($"    {p}");
            sb.AppendLine();
        }
        foreach (var e in plan.Explanations) sb.AppendLine(e.ToString()).AppendLine();
        if (plan.Verification != null) sb.AppendLine($"Verification: {plan.Verification}");
        if (NextCommand(plan) is { } next) sb.AppendLine($"Next: {next}");
        sb.AppendLine($"Total cost: {plan.TotalCost:0.##}");
        return sb.ToString();
    }

    /// <summary>Shell command that performs the first step of the plan, or null if nothing can merge.</summary>
    public static string? NextCommand(Plan plan)
    {
        if (plan.Steps.Count == 0) return null;
        var (id, t) = (plan.Steps[0].Pr.Id, plan.Target);
        // With a merge queue, gh adds the PR to the queue and the queue picks the merge method.
        if (plan.Provider == "azure-devops") return $"ado pr merge {id.TrimStart('#')} --yes" + (plan.Strategy == MergeStrategy.Squash ? " --squash" : "");
        if (plan.Provider == "github" && plan.MergeQueue) return $"gh pr merge {id.TrimStart('#')}";
        if (plan.Provider == "github")
            return $"gh pr merge {id.TrimStart('#')} --" + plan.Strategy switch
            { MergeStrategy.Squash => "squash", MergeStrategy.Merge => "merge", _ => "rebase" };
        return plan.Strategy switch
        {
            MergeStrategy.Squash => $"git checkout {t} && git merge --squash {id} && git commit",
            MergeStrategy.Rebase => $"git rebase {t} {id} && git checkout {t} && git merge --ff-only {id}",
            MergeStrategy.FfOnly => $"git checkout {t} && git merge --ff-only {id}",
            _ => $"git checkout {t} && git merge --no-ff {id}",
        };
    }

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Provider-neutral context of an analyzed pull request (schema gitwizz.context/v1). Lists are sorted, so the same
    /// state always gives the same document.
    /// </summary>
    public static string Context(Git git, PullRequest pr, string provider, string target, string targetSha)
    {
        var commits = git.Run("log", "--format=%H%x09%s", "--reverse", $"{targetSha}..{pr.HeadSha}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t', 2)).Select(c => new { sha = c[0], subject = c.ElementAtOrDefault(1) ?? "" });
        return JsonSerializer.Serialize(new
        {
            schema = "gitwizz.context/v1",
            provider,
            pr = new
            {
                id = pr.Id, title = pr.Title, description = pr.Body, author = pr.Author, url = pr.Url, draft = pr.IsDraft, labels = pr.Labels.Order(),
                source = new { @ref = pr.HeadRef, sha = pr.HeadSha },
                target = new { @ref = target, sha = targetSha },
                mergeBase = pr.BaseSha,
            },
            reviewers = pr.Reviewers.OrderBy(r => r.Name).Select(r => new { name = r.Name, vote = r.Vote, required = r.Required }),
            checks = pr.Checks.Select(c => new { name = c.Name, status = c.Status, url = c.Url }),
            policy = new
            {
                reviewDecision = pr.ReviewDecision, checks = pr.CiStatus, mergeState = pr.MergeStateStatus, draft = pr.IsDraft,
                problems = Analyzer.PolicyProblems(pr), openDependencies = pr.OpenOutsideDependencies,
            },
            workItems = pr.WorkItems.Select(w => new { id = w.Id, type = w.Type, title = w.Title, state = w.State, url = w.Url, acceptanceCriteria = w.AcceptanceCriteria }),
            files = pr.Files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => new { path = f.Path, change = f.Kind.ToString().ToLowerInvariant(), oldPath = f.OldPath }),
            members = pr.Members.Order(StringComparer.Ordinal),
            commits,
        }, Indented);
    }

    public static string Json(Plan plan) => JsonSerializer.Serialize(new
    {
        target = plan.Target,
        strategy = plan.Strategy.ToString().ToLowerInvariant(),
        mergeQueue = plan.MergeQueue,
        notes = plan.Notes,
        steps = plan.Steps.Select(s => new
        {
            id = s.Pr.Id, title = s.Pr.Title, status = Status(s), cost = s.Cost, reason = s.Reason,
            dependencies = s.Pr.Dependencies, regenerate = s.RegenerateFiles,
        }),
        blocked = plan.Blocked.Select(b => new { id = b.Pr.Id, title = b.Pr.Title, status = Status(b), reason = b.Reason }),
        parallelizable = plan.Parallelizable,
        explanations = plan.Explanations.Select(e => new { a = e.A, b = e.B, files = e.Files, members = e.Members, aThenB = e.AThenB, bThenA = e.BThenA, reason = e.Reason }),
        conflicts = plan.Conflicts.Select(c => new { a = c.A, b = c.B, risk = RiskLevel(c.Weight), weight = c.Weight }),
        finalState = plan.FinalState,
        verification = plan.Verification,
        totalCost = plan.TotalCost,
        next = NextCommand(plan),
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
