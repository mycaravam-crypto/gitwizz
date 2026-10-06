using System.Collections.Concurrent;

namespace PrOptimizer;

/// <summary>Merges PRs onto synthetic states without touching working tree or index. Thread-safe.</summary>
public class Simulator(Git git, MergeStrategy strategy)
{
    // Strategy is fixed per simulator instance, so it is implicitly part of the key.
    readonly ConcurrentDictionary<(string State, string Head), Lazy<SimulationResult>> _cache = new();

    public MergeStrategy Strategy => strategy;

    public SimulationResult Simulate(string state, PullRequest pr) =>
        _cache.GetOrAdd((state, pr.HeadSha), k => new(() => Run(k.State, pr))).Value;

    SimulationResult Run(string state, PullRequest pr)
    {
        if (strategy == MergeStrategy.FfOnly)
            return git.IsAncestor(state, pr.HeadSha)
                ? new(true, [], [], new(pr.HeadSha))
                : new(false, ["(not fast-forwardable)"], [], null);

        var (tree, conflicts) = git.MergeTree(state, pr.HeadSha);
        var regenerate = new List<string>();
        if (conflicts.Count > 0)
        {
            // Lockfile/generated conflicts are real, but resolved by regenerating rather than by hand.
            if (!conflicts.All(f => FileClasses.IsRegenerable(FileClasses.Classify(f))))
                return new(false, conflicts, [], null);
            regenerate = conflicts;
            // ponytail: the synthetic tree for these keeps git's conflict markers; fine for planning,
            // a real regenerate step (npm install, etc.) belongs in verification.
        }

        var msg = $"pr-optimizer: {pr.Id}";
        // ponytail: rebase is simulated as squash (same resulting tree in the common case);
        // per-commit replay only matters when individual commits conflict.
        // Commit lazily: most simulated states are pruned by the beam and never need one.
        string[] parents = strategy == MergeStrategy.Merge ? [state, pr.HeadSha] : [state];
        return new(true, [], regenerate, new(() => git.CommitTree(tree, msg, parents)));
    }
}
