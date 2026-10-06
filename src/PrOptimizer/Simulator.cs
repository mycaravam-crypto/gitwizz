namespace PrOptimizer;

/// <summary>Merges PRs onto synthetic states without touching working tree or index.</summary>
public class Simulator(Git git, MergeStrategy strategy)
{
    readonly Dictionary<(string State, string Head), SimulationResult> _cache = new();

    public MergeStrategy Strategy => strategy;
    public int CacheHits { get; private set; }

    public SimulationResult Simulate(string state, PullRequest pr)
    {
        // Strategy is fixed per simulator instance, so it is implicitly part of the key.
        if (_cache.TryGetValue((state, pr.HeadSha), out var hit)) { CacheHits++; return hit; }
        return _cache[(state, pr.HeadSha)] = Run(state, pr);
    }

    SimulationResult Run(string state, PullRequest pr)
    {
        if (strategy == MergeStrategy.FfOnly)
            return git.IsAncestor(state, pr.HeadSha)
                ? new(true, git.Run("rev-parse", pr.HeadSha + "^{tree}"), [], pr.HeadSha, [])
                : new(false, null, ["(not fast-forwardable)"], null, []);

        var (tree, conflicts) = git.MergeTree(state, pr.HeadSha);
        var regenerate = new List<string>();
        if (conflicts.Count > 0)
        {
            // Lockfile/generated conflicts are real, but resolved by regenerating rather than by hand.
            if (!conflicts.All(f => FileClasses.IsRegenerable(FileClasses.Classify(f))))
                return new(false, null, conflicts, null, []);
            regenerate = conflicts;
            // ponytail: the synthetic tree for these keeps git's conflict markers; fine for planning,
            // a real regenerate step (npm install, etc.) belongs in verification.
        }

        var msg = $"pr-optimizer: {pr.Id}";
        // ponytail: rebase is simulated as squash (same resulting tree in the common case);
        // per-commit replay only matters when individual commits conflict.
        var commit = strategy == MergeStrategy.Merge
            ? git.CommitTree(tree, msg, state, pr.HeadSha)
            : git.CommitTree(tree, msg, state);
        return new(true, tree, [], commit, regenerate);
    }
}
