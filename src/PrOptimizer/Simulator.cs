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
        var todo = pairs.DistinctBy(x => (x.State, x.Pr.HeadSha)).Where(x => !_cache.ContainsKey((x.State, x.Pr.HeadSha))).ToList();
        if (todo.Count < 2) return;
        // Not a single whole-tree merge: no merge-tree batch, but still in parallel.
        var single = todo.Where(x => strategy is MergeStrategy.FfOnly || (strategy is MergeStrategy.Rebase && !IsOneCommit(x.Pr))).ToList();
        single.AsParallel().ForAll(x => Simulate(x.State, x.Pr));
        todo = todo.Except(single).ToList();
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

        if (strategy == MergeStrategy.Rebase && !IsOneCommit(pr)) return Replay(state, pr);
        var (tree, conflicts) = git.MergeTree(state, pr.HeadSha);
        return FromMerge(state, pr, tree, conflicts);
    }

    /// <summary>
    /// Rebase: cherry-picks each of the PR's commits onto the state in order (merge-tree with the commit's parent as
    /// base), like git rebase. A conflict in any single commit is a conflict, even if the final tree would merge cleanly.
    /// </summary>
    SimulationResult Replay(string state, PullRequest pr)
    {
        var current = state;
        var regenerate = new List<string>();
        foreach (var (c, parent) in Commits(pr, state))
        {
            var (tree, conflicts) = git.MergeTree(current, c, parent);
            if (!conflicts.All(f => FileClasses.IsRegenerable(FileClasses.Classify(f))))
                return new(MergeOutcome.Conflict, conflicts.Select(f => $"{f} (commit {c[..7]})").ToList(), [], null);
            if (conflicts.Count > 0) tree = git.ReplacePaths(tree, c, conflicts); // as in FromMerge: never commit markers
            regenerate.AddRange(conflicts.Except(regenerate));
            current = git.CommitTree(tree, $"pr-optimizer: {pr.Id} {c[..7]}", current);
        }
        return new(regenerate.Count > 0 ? MergeOutcome.RegenerationRequired : MergeOutcome.Clean, [], regenerate, new(current));
    }

    readonly ConcurrentDictionary<string, List<(string Commit, string Parent)>> _commits = new();

    /// <summary>The PR's own non-merge commits, oldest first: since its merge-base with the target, else not in state.</summary>
    List<(string Commit, string Parent)> Commits(PullRequest pr, string state)
    {
        List<(string, string)> List() => git.Run("rev-list", "--reverse", "--no-merges", "--parents", pr.HeadSha, "--not", pr.BaseSha is "" ? state : pr.BaseSha)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')).Where(p => p.Length > 1).Select(p => (p[0], p[1])).ToList();
        return pr.BaseSha is "" ? List() : _commits.GetOrAdd(pr.HeadSha, _ => List());
    }

    /// <summary>One commit on top of its merge-base: cherry-picking it is exactly the squash merge, which can be batched.</summary>
    bool IsOneCommit(PullRequest pr) => pr.BaseSha != "" && Commits(pr, "") is [var c] && c.Parent == pr.BaseSha;

    SimulationResult FromMerge(string state, PullRequest pr, string tree, List<string> conflicts)
    {
        // Real conflicts end here: no next state exists.
        if (!conflicts.All(f => FileClasses.IsRegenerable(FileClasses.Classify(f))))
            return new(MergeOutcome.Conflict, conflicts, [], null);

        var msg = $"pr-optimizer: {pr.Id}";
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
