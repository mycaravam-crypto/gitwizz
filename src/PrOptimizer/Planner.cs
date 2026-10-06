using System.Collections.Concurrent;

namespace PrOptimizer;

/// <summary>
/// Finds a sequence minimising Σ marginalCost(PR_i | State_i) via beam search over real merge simulations.
/// Beam width 1 is plain greedy.
/// </summary>
public class Planner(Simulator sim, string targetName, string targetSha, List<PullRequest> prs,
    IReadOnlyDictionary<string, double>? history = null)
{
    readonly Dictionary<(string, string), (double W, List<string> Files, bool Independent)> _weights = Pairwise(prs, history);

    record Node(List<PlanStep> Steps, Lazy<string> State, HashSet<string> Merged, double Cost, List<BlockedPr>? Blocked, Node? Parent = null)
    {
        public bool Done => Blocked != null;
        public string Key => string.Join(",", Steps.Select(s => s.Pr.Id));
        public PullRequest? Last => Steps.Count > 0 ? Steps[^1].Pr : null;
        public ConcurrentDictionary<string, SimulationResult> Results { get; } = new();
        public PlanObjective Objective => new(Blocked?.Count ?? 0, Cost, -Steps.Select((s, i) => s.Cost * (i + 1)).Sum());
    }

    static Dictionary<(string, string), (double, List<string>, bool)> Pairwise(List<PullRequest> prs,
        IReadOnlyDictionary<string, double>? history)
    {
        var d = new Dictionary<(string, string), (double, List<string>, bool)>();
        foreach (var a in prs)
            foreach (var b in prs.Where(b => b != a))
            {
                var w = Analyzer.ConflictWeight(a, b, out var f, history);
                // A stacked PR "overlaps" its parent only by containing the parent's own changes.
                d[(a.Id, b.Id)] = a.Dependencies.Contains(b.Id) || b.Dependencies.Contains(a.Id) ? (0, [], false) : (w, f, f.Count == 0);
            }
        return d;
    }

    /// <summary>
    /// Merge result of pr on node n. If the PR merged last into n touches none of pr's files and they share no
    /// history, pr's result on n equals its result on n's parent: the files pr touches and its merge-base are
    /// unchanged. Then the parent's result is reused and the real merge only runs if the commit is ever needed.
    /// </summary>
    bool Reusable(Node n, PullRequest pr) =>
        sim.Strategy != MergeStrategy.FfOnly && n.Parent != null && _weights[(n.Last!.Id, pr.Id)].Independent;

    SimulationResult Result(Node n, PullRequest pr) => n.Results.GetOrAdd(pr.Id, _ =>
    {
        if (Reusable(n, pr))
        {
            var r = Result(n.Parent!, pr);
            return r with { Commit = r.Mergeable ? new(() => sim.Simulate(n.State.Value, pr).Commit!.Value) : null };
        }
        return sim.Simulate(n.State.Value, pr);
    });

    public double Weight(PullRequest a, PullRequest b) => _weights[(a.Id, b.Id)].W;

    public Plan Build(int beamWidth)
    {
        var plan = new Plan { Target = targetName, Strategy = sim.Strategy };

        // Readiness is a hard constraint, applied before optimisation; dependents of blocked PRs are blocked too.
        var blocked = prs.Select(p => (p, r: Analyzer.NotReadyReason(p))).Where(x => x.r != null)
            .ToDictionary(x => x.p.Id, x => x.r!);
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var p in prs.Where(p => !blocked.ContainsKey(p.Id)))
                if (p.Dependencies.FirstOrDefault(blocked.ContainsKey) is { } d)
                { blocked[p.Id] = $"depends on {d} ({blocked[d]})"; changed = true; }
        }
        plan.Blocked.AddRange(prs.Where(p => blocked.ContainsKey(p.Id)).Select(p => new BlockedPr(p, blocked[p.Id])));
        var pool = prs.Where(p => !blocked.ContainsKey(p.Id)).ToList();

        // Finished plans live outside the beam, so the best complete plan can never be pruned by unfinished ones.
        Node? best = null;
        var beam = new List<Node> { new([], new(targetSha), [], 0, null) };
        while (beam.Count > 0)
        {
            // Batch-simulate what can't be reused, then evaluate all candidates in parallel; Expand reads the results.
            var fresh = beam.SelectMany(n => Candidates(n, pool).Where(pr => !Reusable(n, pr)).Select(pr => (n, pr))).ToList();
            // Materialize states: kept nodes built on reused results still need their real merge; batch those first.
            sim.Prefetch(beam.Where(n => !n.State.IsValueCreated && n.Parent != null).Select(n => (n.Parent!.State.Value, n.Last!)));
            fresh.Select(x => x.n).Distinct().AsParallel().ForAll(n => _ = n.State.Value);
            sim.Prefetch(fresh.Select(x => (x.n.State.Value, x.pr)));
            Parallel.ForEach(beam.SelectMany(n => Candidates(n, pool).Select(pr => (n, pr))), x => Result(x.n, x.pr));
            var next = beam.SelectMany(n => Expand(n, pool)).ToList();
            foreach (var done in next.Where(n => n.Done))
                if (best == null || Better(done, best)) best = done;
            beam = next.Where(n => !n.Done).DistinctBy(n => n.Key)
                .OrderBy(n => n.Objective).ThenBy(n => n.Key, StringComparer.Ordinal)
                .Take(beamWidth).ToList();
        }

        var final = best!; // the search always finishes at least one plan
        plan.Steps.AddRange(final.Steps);
        plan.Blocked.AddRange(final.Blocked!);
        plan.FinalState = final.State.Value;
        plan.Parallelizable = Parallelizable(pool);
        plan.Explanations = Explain(final.Steps);
        plan.Prs = prs;
        plan.Conflicts = prs.SelectMany((a, i) => prs.Skip(i + 1).Select(b => new ConflictPair(a.Id, b.Id, Weight(a, b))))
            .Where(c => c.Weight > 0).OrderByDescending(c => c.Weight).ToList();
        return plan;
    }

    static bool Better(Node a, Node b) =>
        a.Objective.CompareTo(b.Objective) is var c && (c < 0 || (c == 0 && string.CompareOrdinal(a.Key, b.Key) < 0));

    static IEnumerable<PullRequest> Candidates(Node n, List<PullRequest> pool) =>
        pool.Where(p => !n.Merged.Contains(p.Id) && p.Dependencies.All(n.Merged.Contains));

    IEnumerable<Node> Expand(Node n, List<PullRequest> pool)
    {
        var remaining = pool.Where(p => !n.Merged.Contains(p.Id)).ToList();
        if (remaining.Count == 0) { yield return n with { Blocked = [] }; yield break; }

        var any = false;
        foreach (var pr in Candidates(n, pool))
        {
            var r = Result(n, pr);
            if (!r.Mergeable) continue;
            any = true;
            var others = remaining.Where(o => o != pr).ToList();
            var (cost, reason) = Score(pr, others, r);
            yield return new Node([.. n.Steps, new PlanStep(pr, cost, reason, r.RegenerateFiles)], r.Commit!,
                [.. n.Merged, pr.Id], n.Cost + cost, null, n);
        }
        if (any) yield break;

        // Dead end: nothing remaining merges cleanly on this state.
        var blocked = remaining.Select(pr =>
        {
            var missing = pr.Dependencies.Where(d => !n.Merged.Contains(d)).ToList();
            if (missing.Count > 0) return new BlockedPr(pr, $"depends on {string.Join(", ", missing)}");
            var r = Result(n, pr);
            var after = n.Steps.Count > 0 ? $" after {n.Steps[^1].Pr.Id}" : "";
            return new BlockedPr(pr, $"conflict{after}: {string.Join(", ", r.ConflictFiles)}");
        }).ToList();
        yield return n with { Blocked = blocked };
    }

    /// <summary>Marginal cost: risk pushed onto still-pending PRs, regeneration work, minus unblock bonus.</summary>
    (double Cost, string Reason) Score(PullRequest pr, List<PullRequest> others, SimulationResult r)
    {
        var reasons = new List<string>();
        var overlaps = others.Where(o => Weight(pr, o) > 0).OrderByDescending(o => Weight(pr, o)).ToList();
        var unlocks = others.Where(o => o.Dependencies.Contains(pr.Id)).ToList();

        double cost = overlaps.Sum(o => Weight(pr, o)) + 0.5 * r.RegenerateFiles.Count - 0.1 * unlocks.Count;

        if (pr.Dependencies.Count > 0)
            reasons.Add("dependency: " + string.Join(", ", pr.Dependencies.Select(d => pr.DependencyNotes.TryGetValue(d, out var why) ? $"{d} ({why})" : d)));
        if (unlocks.Count > 0) reasons.Add("unlocks " + string.Join(", ", unlocks));
        if (r.RegenerateFiles.Count > 0) reasons.Add("regenerate after merge: " + string.Join(", ", r.RegenerateFiles));
        var hot = overlaps.SelectMany(o => _weights[(pr.Id, o.Id)].Files).Distinct()
            .Where(f => history?.GetValueOrDefault(f) > 0).ToList();
        var semantic = overlaps.SelectMany(o => Analyzer.SemanticRisks(pr, o)).ToList();
        if (semantic.Count > 0) reasons.Add("semantic: " + string.Join("; ", semantic));
        if (hot.Count > 0) reasons.Add("conflict-prone in past merges: " + string.Join(", ", hot.Select(f => $"{f} ({history![f]:P0})")));
        reasons.Add(overlaps.Count == 0
            ? "no overlapping changes with pending PRs"
            : "overlaps " + string.Join(", ", overlaps.Select(o => $"{o} ({Weight(pr, o):0.00}{SharedMembers(pr, o)})")));
        return (Math.Round(Math.Max(cost, 0), 2), string.Join("; ", reasons));
    }

    static string SharedMembers(PullRequest a, PullRequest b) =>
        a.Members.Intersect(b.Members).ToList() is { Count: > 0 } m ? ": " + string.Join(", ", m) : "";

    /// <summary>Ready PRs with no dependencies that merge cleanly now and touch nothing any other PR touches.</summary>
    List<string> Parallelizable(List<PullRequest> pool) =>
        pool.Where(p => p.Dependencies.Count == 0
                        && prs.All(o => o == p || Weight(p, o) == 0)
                        && sim.Simulate(targetSha, p).Mergeable)
            .Select(p => p.Id).ToList();

    /// <summary>For overlapping, independent pairs: show that the chosen order matters by simulating both.</summary>
    List<Explanation> Explain(List<PlanStep> steps)
    {
        var res = new List<Explanation>();
        for (int i = 0; i < steps.Count; i++)
            for (int j = i + 1; j < steps.Count; j++)
            {
                var (a, b) = (steps[i].Pr, steps[j].Pr);
                if (b.Dependencies.Contains(a.Id) || Weight(a, b) == 0) continue;
                var ab = Pair(a, b);
                var ba = Pair(b, a);
                if (ab == ba) continue;
                var members = a.Members.Intersect(b.Members).ToList();
                res.Add(new(a.Id, b.Id, members.Count > 0 ? members : _weights[(a.Id, b.Id)].Files, ab, ba));
            }
        return res;
    }

    string Pair(PullRequest first, PullRequest second)
    {
        var r1 = sim.Simulate(targetSha, first);
        if (!r1.Mergeable) return $"conflict on {first}";
        return sim.Simulate(r1.Commit!.Value, second).Mergeable ? "clean" : "conflict";
    }
}
