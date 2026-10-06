namespace PrOptimizer;

/// <summary>
/// Finds a sequence minimising Σ marginalCost(PR_i | State_i) via beam search over real merge simulations.
/// Beam width 1 is plain greedy.
/// </summary>
public class Planner(Simulator sim, string targetName, string targetSha, List<PullRequest> prs)
{
    public const double BlockedCost = 10;

    readonly Dictionary<(string, string), (double W, List<string> Files)> _weights = Pairwise(prs);

    record Node(List<PlanStep> Steps, Lazy<string> State, HashSet<string> Merged, double Cost, List<BlockedPr>? Blocked)
    {
        public bool Done => Blocked != null;
        public string Key => string.Join(",", Steps.Select(s => s.Pr.Id));
    }

    static Dictionary<(string, string), (double, List<string>)> Pairwise(List<PullRequest> prs)
    {
        var d = new Dictionary<(string, string), (double, List<string>)>();
        foreach (var a in prs)
            foreach (var b in prs.Where(b => b != a))
                // A stacked PR "overlaps" its parent only by containing the parent's own changes.
                d[(a.Id, b.Id)] = a.Dependencies.Contains(b.Id) || b.Dependencies.Contains(a.Id)
                    ? (0, [])
                    : (Analyzer.ConflictWeight(a, b, out var f), f);
        return d;
    }

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

        var beam = new List<Node> { new([], new(targetSha), [], 0, null) };
        while (beam.Any(n => !n.Done))
        {
            // Simulate all candidates of all live nodes in parallel; Expand then reads from the cache.
            Parallel.ForEach(beam.Where(n => !n.Done).SelectMany(n => Candidates(n, pool).Select(pr => (n, pr))),
                x => sim.Simulate(x.n.State.Value, x.pr));
            var next = beam.Where(n => n.Done).ToList();
            foreach (var n in beam.Where(n => !n.Done)) next.AddRange(Expand(n, pool));
            // Equal totals: prefer cheap PRs early (maximising Σ cost·position pushes expensive ones late), then a stable order.
            beam = next.DistinctBy(n => n.Key).OrderBy(n => Math.Round(n.Cost, 6))
                .ThenByDescending(n => n.Steps.Select((s, i) => s.Cost * (i + 1)).Sum())
                .ThenBy(n => n.Key, StringComparer.Ordinal)
                .Take(beamWidth).ToList();
        }

        var best = beam[0];
        plan.Steps.AddRange(best.Steps);
        plan.Blocked.AddRange(best.Blocked!);
        plan.FinalState = best.State.Value;
        plan.Parallelizable = Parallelizable(pool);
        plan.Explanations = Explain(best.Steps);
        return plan;
    }

    static IEnumerable<PullRequest> Candidates(Node n, List<PullRequest> pool) =>
        pool.Where(p => !n.Merged.Contains(p.Id) && p.Dependencies.All(n.Merged.Contains));

    IEnumerable<Node> Expand(Node n, List<PullRequest> pool)
    {
        var remaining = pool.Where(p => !n.Merged.Contains(p.Id)).ToList();
        if (remaining.Count == 0) { yield return n with { Blocked = [] }; yield break; }

        var any = false;
        foreach (var pr in Candidates(n, pool))
        {
            var r = sim.Simulate(n.State.Value, pr);
            if (!r.Mergeable) continue;
            any = true;
            var others = remaining.Where(o => o != pr).ToList();
            var (cost, reason) = Score(pr, others, r);
            yield return new Node([.. n.Steps, new PlanStep(pr, cost, reason, r.RegenerateFiles)], r.Commit!,
                [.. n.Merged, pr.Id], n.Cost + cost, null);
        }
        if (any) yield break;

        // Dead end: nothing remaining merges cleanly on this state.
        var blocked = remaining.Select(pr =>
        {
            var missing = pr.Dependencies.Where(d => !n.Merged.Contains(d)).ToList();
            if (missing.Count > 0) return new BlockedPr(pr, $"depends on {string.Join(", ", missing)}");
            var r = sim.Simulate(n.State.Value, pr);
            var after = n.Steps.Count > 0 ? $" after {n.Steps[^1].Pr.Id}" : "";
            return new BlockedPr(pr, $"conflict{after}: {string.Join(", ", r.ConflictFiles)}");
        }).ToList();
        yield return n with { Blocked = blocked, Cost = n.Cost + blocked.Count * BlockedCost };
    }

    /// <summary>Marginal cost: risk pushed onto still-pending PRs, regeneration work, minus unblock bonus.</summary>
    (double Cost, string Reason) Score(PullRequest pr, List<PullRequest> others, SimulationResult r)
    {
        var reasons = new List<string>();
        var overlaps = others.Where(o => Weight(pr, o) > 0).OrderByDescending(o => Weight(pr, o)).ToList();
        var unlocks = others.Where(o => o.Dependencies.Contains(pr.Id)).ToList();

        double cost = overlaps.Sum(o => Weight(pr, o)) + 0.5 * r.RegenerateFiles.Count - 0.1 * unlocks.Count;

        if (pr.Dependencies.Count > 0) reasons.Add("dependency: " + string.Join(", ", pr.Dependencies));
        if (unlocks.Count > 0) reasons.Add("unlocks " + string.Join(", ", unlocks));
        if (r.RegenerateFiles.Count > 0) reasons.Add("regenerate after merge: " + string.Join(", ", r.RegenerateFiles));
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
    List<string> Explain(List<PlanStep> steps)
    {
        var res = new List<string>();
        for (int i = 0; i < steps.Count; i++)
            for (int j = i + 1; j < steps.Count; j++)
            {
                var (a, b) = (steps[i].Pr, steps[j].Pr);
                if (b.Dependencies.Contains(a.Id) || Weight(a, b) == 0) continue;
                var ab = Pair(a, b);
                var ba = Pair(b, a);
                if (ab == ba) continue;
                var members = a.Members.Intersect(b.Members).ToList();
                res.Add($"Why {a} before {b}?\n" +
                        $"  both modify: {string.Join(", ", members.Count > 0 ? members : _weights[(a.Id, b.Id)].Files)}\n" +
                        $"  {a} -> {b} = {ab}\n  {b} -> {a} = {ba}");
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
