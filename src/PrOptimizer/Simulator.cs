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

    /// <summary>Simulates many (state, PR) pairs up front, in one git process per core, filling the cache.</summary>
    public void Prefetch(IEnumerable<(string State, PullRequest Pr)> pairs)
    {
        if (strategy == MergeStrategy.FfOnly) return;
        var todo = pairs.DistinctBy(x => (x.State, x.Pr.HeadSha)).Where(x => !_cache.ContainsKey((x.State, x.Pr.HeadSha))).ToList();
        if (todo.Count < 2) return;
        var size = (int)Math.Ceiling(todo.Count / (double)Environment.ProcessorCount);
        Parallel.ForEach(todo.Chunk(Math.Max(size, 8)), chunk =>
        {
            // On failure leave the cache alone: Simulate then runs single merges and reports the real error.
            if (git.MergeTreeBatch(chunk.Select(x => (x.State, x.Pr.HeadSha)).ToList()) is not { } results) return;
            for (int i = 0; i < chunk.Length; i++)
            {
                var (state, pr) = chunk[i];
                var (tree, conflicts) = results[i];
                _cache.TryAdd((state, pr.HeadSha), new(FromMerge(state, pr, tree, conflicts)));
            }
        });
    }

    SimulationResult Run(string state, PullRequest pr)
    {
        if (strategy == MergeStrategy.FfOnly)
            return git.IsAncestor(state, pr.HeadSha)
                ? new(MergeOutcome.Clean, [], [], new(pr.HeadSha))
                : new(MergeOutcome.Conflict, ["(not fast-forwardable)"], [], null);

        var (tree, conflicts) = git.MergeTree(state, pr.HeadSha);
        return FromMerge(state, pr, tree, conflicts);
    }

    SimulationResult FromMerge(string state, PullRequest pr, string tree, List<string> conflicts)
    {
        // Real conflicts end here: no next state exists.
        if (!conflicts.All(f => FileClasses.IsRegenerable(FileClasses.Classify(f))))
            return new(MergeOutcome.Conflict, conflicts, [], null);

        var msg = $"pr-optimizer: {pr.Id}";
        // ponytail: rebase is simulated as squash (same resulting tree in the common case);
        // per-commit replay only matters when individual commits conflict.
        string[] parents = strategy == MergeStrategy.Merge ? [state, pr.HeadSha] : [state];
        // Commit lazily: most simulated states are pruned by the beam and never need one.
        if (conflicts.Count == 0)
            return new(MergeOutcome.Clean, [], [], new(() => git.CommitTree(tree, msg, parents)));

        // Lockfile/generated conflicts: merge-tree's tree holds conflict markers and must never become a state.
        // Stand-in until the step's regenerate runs: the PR's version of those files, a real resolved tree.
        return new(MergeOutcome.RegenerationRequired, [], conflicts,
            new(() => git.CommitTree(git.ReplacePaths(tree, pr.HeadSha, conflicts), msg + " (regenerate: " + string.Join(", ", conflicts) + ")", parents)));
    }
}
