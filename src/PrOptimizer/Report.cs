using System.Text;
using System.Text.Json;

namespace PrOptimizer;

public static class Report
{
    public static string Text(Plan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PR MERGE PLAN");
        sb.AppendLine($"Target: {plan.Target}");
        sb.AppendLine($"Strategy: {plan.Strategy.ToString().ToLowerInvariant()}");
        sb.AppendLine();

        int i = 1;
        foreach (var s in plan.Steps)
        {
            sb.AppendLine($"{i++,-3}{s.Pr.Id}  {s.Pr.Title}");
            sb.AppendLine($"   Cost: {s.Cost:0.##}");
            sb.AppendLine($"   Reason: {s.Reason}");
            sb.AppendLine();
        }
        foreach (var b in plan.Blocked)
        {
            sb.AppendLine($"{i++,-3}{b.Pr.Id}  {b.Pr.Title}");
            sb.AppendLine("   BLOCKED");
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
        sb.AppendLine($"Total cost: {plan.TotalCost:0.##}");
        return sb.ToString();
    }

    public static string Json(Plan plan) => JsonSerializer.Serialize(new
    {
        target = plan.Target,
        strategy = plan.Strategy.ToString().ToLowerInvariant(),
        steps = plan.Steps.Select(s => new
        {
            id = s.Pr.Id, title = s.Pr.Title, cost = s.Cost, reason = s.Reason,
            dependencies = s.Pr.Dependencies, regenerate = s.RegenerateFiles,
        }),
        blocked = plan.Blocked.Select(b => new { id = b.Pr.Id, title = b.Pr.Title, reason = b.Reason }),
        parallelizable = plan.Parallelizable,
        explanations = plan.Explanations.Select(e => new { a = e.A, b = e.B, shared = e.Shared, aThenB = e.AThenB, bThenA = e.BThenA }),
        conflicts = plan.Conflicts.Select(c => new { a = c.A, b = c.B, weight = c.Weight }),
        finalState = plan.FinalState,
        verification = plan.Verification,
        totalCost = plan.TotalCost,
    }, new JsonSerializerOptions { WriteIndented = true });
}
