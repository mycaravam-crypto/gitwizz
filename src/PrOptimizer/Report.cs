using System.Text;
using System.Text.Json;

namespace PrOptimizer;

public static class Report
{
    public static string Status(PlanStep s) => s.RegenerateFiles.Count > 0 ? "REGENERATE" : "CLEAN";
    public static string Status(BlockedPr b) => b.Policy ? "POLICY BLOCKED" : "BLOCKED";

    /// <summary>Pair weights are heuristics, not probabilities, so they are shown as levels, never as percentages.</summary>
    public static string RiskLevel(double weight) => weight < 0.3 ? "low" : weight < 0.6 ? "medium" : "high";

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
        explanations = plan.Explanations.Select(e => new { a = e.A, b = e.B, shared = e.Shared, aThenB = e.AThenB, bThenA = e.BThenA }),
        conflicts = plan.Conflicts.Select(c => new { a = c.A, b = c.B, risk = RiskLevel(c.Weight), weight = c.Weight }),
        finalState = plan.FinalState,
        verification = plan.Verification,
        totalCost = plan.TotalCost,
        next = NextCommand(plan),
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
