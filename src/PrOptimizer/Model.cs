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
    public HashSet<string> Dependencies { get; } = [];

    // Readiness (provider-supplied). Null reason == ready.
    public bool IsDraft { get; init; }
    public string? ReviewDecision { get; init; }
    public string? CiStatus { get; init; }

    public override string ToString() => Id;
}

public record SimulationResult(bool Mergeable, string? Tree, List<string> ConflictFiles, string? Commit, List<string> RegenerateFiles);

public record PlanStep(PullRequest Pr, double Cost, string Reason, List<string> RegenerateFiles);

public record BlockedPr(PullRequest Pr, string Reason);

public class Plan
{
    public required string Target { get; init; }
    public required MergeStrategy Strategy { get; init; }
    public List<PlanStep> Steps { get; } = [];
    public List<BlockedPr> Blocked { get; } = [];
    public List<string> Parallelizable { get; set; } = [];
    public List<string> Explanations { get; set; } = [];
    public string? FinalState { get; set; }
    public string? Verification { get; set; }
    public double TotalCost => Steps.Sum(s => s.Cost) + Blocked.Count * Planner.BlockedCost;
}
