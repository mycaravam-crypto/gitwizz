namespace Gitwizz;

public static class Verify
{
    public static readonly string[] Levels = ["final", "critical", "step"];

    /// <summary>
    /// Verifies the chosen plan's states only, never search candidates: final = the last state, critical = after each
    /// high-risk step and the last, step = after every step. Stops at the first failure and names the step.
    /// </summary>
    public static (bool Ok, string Summary) Plan(Git git, Plan plan, string command, string level, ProgressBars? progress = null)
    {
        if (plan.Steps.Count == 0) return (true, $"nothing to verify ({command})");
        var steps = level switch
        {
            "step" => plan.Steps,
            "critical" => plan.Steps.Where((s, i) => s.Cost >= PlanStep.HighRisk || i == plan.Steps.Count - 1).ToList(),
            _ => [plan.Steps[^1]],
        };
        progress?.Resize(steps.Count);
        foreach (var s in steps)
        {
            progress?.Describe($"Verifying after {s.Pr.Id} ({steps.IndexOf(s) + 1}/{steps.Count}): {command}");
            var (ok, log) = Run(git, s.State, command);
            progress?.Advance();
            if (!ok) return (false, $"FAILED after {s.Pr.Id} ({command})\n{log.TrimEnd()}");
        }
        return (true, $"passed ({command}) at {steps.Count} state{(steps.Count == 1 ? "" : "s")}: {level}");
    }

    /// <summary>Runs a command in a temporary detached worktree on the given commit, then removes it.</summary>
    public static (bool Ok, string Output) Run(Git git, string commit, string command)
    {
        using var wt = new Worktree(git, commit);
        var r = Git.Shell(wt.Dir, command);
        return (r.ExitCode == 0, r.Stdout + r.Stderr);
    }
}
