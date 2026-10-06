namespace PrOptimizer;

public enum MergeStrategy { Merge, Squash, Rebase, FfOnly }

public enum FileClass { Normal, Generated, Lockfile, Migration, Configuration, Binary, Submodule }

public enum ChangeKind { Added, Modified, Deleted, Renamed }

public record FileChange(string Path, ChangeKind Kind, string? OldPath = null);

/// <summary>Changed line range on the merge-base side (old file), from git diff -U0.</summary>
public record Hunk(string Path, int Start, int Count)
{
    // Pure insertions (Count 0) still touch the boundary between lines Start and Start+1.
    public bool Overlaps(Hunk o) =>
        Path == o.Path && Start <= o.Start + Math.Max(o.Count, 1) && o.Start <= Start + Math.Max(Count, 1);
}

public class PullRequest
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public required string HeadSha { get; init; }
    public string HeadRef { get; init; } = "";
    public string BaseRef { get; init; } = "";
    public List<string> Labels { get; init; } = [];

    // Filled by the analyzer.
    public string BaseSha { get; set; } = "";
    public List<FileChange> Files { get; set; } = [];
    public List<Hunk> Hunks { get; set; } = [];
    public HashSet<string> Members { get; set; } = []; // e.g. "UserService.Login(string)", C# only
    public Dictionary<string, (HashSet<int> Before, HashSet<int> After)> Api { get; set; } = []; // C# names whose accepted arg counts changed
    public HashSet<(string Name, int Args)> Uses { get; set; } = []; // names the PR's new C# code calls or references
    public HashSet<string> Dependencies { get; } = [];
    public Dictionary<string, string> DependencyNotes { get; } = []; // why, for inferred (semantic) dependencies

    // Readiness (provider-supplied). Null reason == ready.
    public bool IsDraft { get; init; }
    public string? ReviewDecision { get; init; }
    public string? CiStatus { get; init; }

    public override string ToString() => Id;
}

/// <summary>Clean: the merged tree is the next state. RegenerationRequired: only regenerable files conflict; the next state
/// takes the PR's version of them and the step needs a regenerate. Conflict: no next state.</summary>
public enum MergeOutcome { Clean, RegenerationRequired, Conflict }

/// <summary>Commit is never built from a tree that still holds conflicts (see Simulator).</summary>
public record SimulationResult(MergeOutcome Outcome, List<string> ConflictFiles, List<string> RegenerateFiles, Lazy<string>? Commit)
{
    public bool Mergeable => Outcome != MergeOutcome.Conflict;
}

public record PlanStep(PullRequest Pr, double Cost, string Reason, List<string> RegenerateFiles);

public record BlockedPr(PullRequest Pr, string Reason);

/// <summary>"Why A before B?": both orders simulated from the target. Outcomes are "clean", "conflict" or "conflict on X".</summary>
public record Explanation(string A, string B, List<string> Shared, string AThenB, string BThenA)
{
    public override string ToString() =>
        $"Why {A} before {B}?\n  both modify: {string.Join(", ", Shared)}\n  {A} -> {B} = {AThenB}\n  {B} -> {A} = {BThenA}";
}

/// <summary>
/// Plan quality, compared lexicographically (lower is better): first merge as many PRs as possible, then lowest cost,
/// then cheap PRs early (TieBreak = -Σ cost·position). No numeric penalty can trade a blocked PR for a lower cost.
/// </summary>
public readonly record struct PlanObjective(int Unmerged, double Cost, double TieBreak) : IComparable<PlanObjective>
{
    public int CompareTo(PlanObjective o) =>
        Unmerged != o.Unmerged ? Unmerged.CompareTo(o.Unmerged)
        : Math.Round(Cost, 6) != Math.Round(o.Cost, 6) ? Cost.CompareTo(o.Cost)
        : Math.Round(TieBreak, 6).CompareTo(Math.Round(o.TieBreak, 6));
}

public record ConflictPair(string A, string B, double Weight);

/// <summary>Target branch rules (GitHub branch protection + rulesets). Empty when unknown or unprotected.</summary>
public record BranchPolicy(HashSet<string> RequiredChecks, bool MergeQueue = false, MergeStrategy? QueueStrategy = null, bool LinearHistory = false);

public class Plan
{
    public required string Target { get; init; }
    public required MergeStrategy Strategy { get; init; }
    public string Provider { get; set; } = "local";
    public List<PlanStep> Steps { get; } = [];
    public List<BlockedPr> Blocked { get; } = [];
    public List<string> Parallelizable { get; set; } = [];
    public List<Explanation> Explanations { get; set; } = [];
    public List<PullRequest> Prs { get; set; } = [];
    public List<ConflictPair> Conflicts { get; set; } = []; // pairwise risk > 0, each pair once
    public string? FinalState { get; set; }
    public string? Verification { get; set; }
    public bool MergeQueue { get; set; }
    public List<string> Notes { get; } = []; // policy remarks shown with the plan
    public double TotalCost => Steps.Sum(s => s.Cost);
}
