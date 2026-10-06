using System.Collections.Concurrent;

namespace PrOptimizer;

/// <summary>
/// Finds a sequence minimising Σ marginalCost(PR_i | State_i) via beam search over real merge simulations.
/// Beam width 1 is plain greedy.
/// </summary>
public class Planner(Simulator sim, string targetName, string targetSha, List<PullRequest> prs,
    IReadOnlyDictionary<string, double>? history = null)
{
    readonly Dictionary<(string, string), (double W, List<string> Files, bool Independent)> _weights = Pairwise(prs, history, sim.Config);

    record Node(List<PlanStep> Steps, Lazy<string> State, HashSet<string> Merged, double Cost, List<BlockedPr>? Blocked, Node? Parent = null)
    {
        public bool Done => Blocked != null;
        public string Key => string.Join(",", Steps.Select(s => s.Pr.Id));
        public PullRequest? Last => Steps.Count > 0 ? Steps[^1].Pr : null;
        public ConcurrentDictionary<string, SimulationResult> Results { get; } = new();
        public PlanObjective Objective => new(Blocked?.Count ?? 0, Cost, -Steps.Select((s, i) => s.Cost * (i + 1)).Sum());
    }

    static Dictionary<(string, string), (double, List<string>, bool)> Pairwise(List<PullRequest> prs,
        IReadOnlyDictionary<string, double>? history, RepoConfig config)
    {
        var d = new Dictionary<(string, string), (double, List<string>, bool)>();
        foreach (var a in prs)
            foreach (var b in prs.Where(b => b != a))
            {
                var w = Analyzer.ConflictWeight(a, b, out var f, history, config);
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

    /// <summary>
    /// Plans the merge order: blocks PRs that aren't ready (and their dependents), then beam-searches simulated orders
    /// and returns the best complete plan, with its states, parallelizable PRs and "why A before B?" explanations.
    /// </summary>
    public Plan Build(int beamWidth)
    {
        var plan = new Plan { Target = targetName, Strategy = sim.Strategy };

        // Readiness is a hard constraint, applied before optimisation; dependents of blocked PRs are blocked too.
        // Already in the target: nothing to merge, and dependencies on them are satisfied.
        var merged = prs.Where(p => p.AlreadyMerged).ToList();
        if (merged.Count > 0) plan.Notes.Add($"already merged into {targetName}, left out: {string.Join(", ", merged)}");
        // Known before simulating: a PR that merges into another branch than the target (its base isn't the target or
        // a planned PR's branch), or one that waits on an open PR outside the plan, can't be merged by following it.
        var heads = prs.Select(p => p.HeadRef).Where(h => h != "").ToHashSet();
        string? Structural(PullRequest p) =>
            p.BaseRef != "" && p.BaseRef != targetName && !heads.Contains(p.BaseRef) ? $"targets '{p.BaseRef}', not '{targetName}'"
            : p.OpenOutsideDependencies.Count > 0 ? $"depends on {string.Join(", ", p.OpenOutsideDependencies)} (open, not in this plan)"
            : null;
        var blocked = new Dictionary<string, (string Reason, bool Policy)>();
        foreach (var p in prs.Except(merged))
            if (Structural(p) is { } r) blocked[p.Id] = (r, false);
            else if (Analyzer.NotReadyReason(p) is { } n) blocked[p.Id] = (n, true);
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var p in prs.Except(merged).Where(p => !blocked.ContainsKey(p.Id)))
                if (p.Dependencies.FirstOrDefault(blocked.ContainsKey) is { } d)
                { blocked[p.Id] = ($"depends on {d} ({blocked[d].Reason})", blocked[d].Policy); changed = true; }
        }
        plan.Blocked.AddRange(prs.Where(p => blocked.ContainsKey(p.Id)).Select(p => new BlockedPr(p, blocked[p.Id].Reason, blocked[p.Id].Policy)));
        var pool = prs.Except(merged).Where(p => !blocked.ContainsKey(p.Id)).ToList();

        // Pairwise outcomes (first -> second from the target) for every overlapping pair, batched once up front.
        var overlapping = pool.SelectMany(a => pool.Where(b => b != a && Weight(a, b) > 0).Select(b => (a, b))).ToList();
        sim.Prefetch(overlapping.Select(x => x.a).Distinct().Select(a => (targetSha, a)));
        var firsts = overlapping.Select(x => (x.a, r: sim.Simulate(targetSha, x.a))).Where(x => x.r.Mergeable).DistinctBy(x => x.a).ToDictionary(x => x.a, x => x.r);
        firsts.Values.AsParallel().ForAll(r => _ = r.Commit!.Value);
        sim.Prefetch(overlapping.Where(x => firsts.ContainsKey(x.a)).Select(x => (firsts[x.a].Commit!.Value, x.b)));
        foreach (var (a, b) in overlapping)
            _pairOutcome[(a.Id, b.Id)] = firsts.TryGetValue(a, out var r) ? sim.Simulate(r.Commit!.Value, b).Outcome : null;

        // Finished plans live outside the beam, so the best complete plan can never be pruned by unfinished ones.
        Node? best = null;
        var beam = new List<Node> { new([], new(targetSha), [.. merged.Select(p => p.Id)], 0, null) };
        while (beam.Count > 0)
        {
            // Batch-simulate what can't be reused, then evaluate all candidates in parallel; Expand reads the results.
            var fresh = beam.SelectMany(n => Candidates(n, pool).Where(pr => !Reusable(n, pr)).Select(pr => (n, pr))).ToList();
            // Materialize states: kept nodes built on reused results still need their real merge; batch those first.
            sim.Prefetch(beam.Where(n => !n.State.IsValueCreated && n.Parent != null).Select(n => (n.Parent!.State.Value, n.Last!)));
            fresh.Select(x => x.n).Distinct().AsParallel().ForAll(n => _ = n.State.Value);
            sim.Prefetch(fresh.Select(x => (x.n.State.Value, x.pr)));
            Parallel.ForEach(beam.SelectMany(n => Candidates(n, pool).Select(pr => (n, pr))), x => Result(x.n, x.pr));
            // State-aware cost merges risky pending PRs onto each candidate's resulting state: batch those too.
            var ahead = beam.SelectMany(n => Candidates(n, pool).Select(pr => (n, pr, r: Result(n, pr)))
                .Where(x => x.r.Mergeable && Risky(n, pool, x.pr).Any())).ToList();
            sim.Prefetch(ahead.Where(x => Reusable(x.n, x.pr)).Select(x => (x.n.State.Value, x.pr)));
            ahead.AsParallel().ForAll(x => _ = x.r.Commit!.Value);
            sim.Prefetch(ahead.SelectMany(x => Risky(x.n, pool, x.pr).Select(o => (x.r.Commit!.Value, o))));
            var next = beam.SelectMany(n => Expand(n, pool)).ToList();
            foreach (var done in next.Where(n => n.Done))
                if (best == null || Better(done, best)) best = done;
            beam = next.Where(n => !n.Done).DistinctBy(n => n.Key)
                .OrderBy(n => n.Objective).ThenBy(n => n.Key, StringComparer.Ordinal)
                .Take(beamWidth).ToList();
        }

        var final = best!; // the search always finishes at least one plan
        // Node i on the path holds the state after step i (the finished node copies the last live one).
        var states = new List<string>();
        for (var n = final; n.Parent != null; n = n.Parent) states.Insert(0, n.State.Value);
        plan.Steps.AddRange(final.Steps.Select((s, i) => s with { State = states[i] }));
        plan.Blocked.AddRange(final.Blocked!);
        plan.FinalState = final.State.Value;
        plan.Parallelizable = Parallelizable(pool);
        plan.Explanations = Explain(final.Steps, plan.Blocked);
        plan.Prs = prs;
        plan.Conflicts = prs.SelectMany((a, i) => prs.Skip(i + 1).Select(b => new ConflictPair(a.Id, b.Id, Weight(a, b))))
            .Where(c => c.Weight > 0).OrderByDescending(c => c.Weight).ToList();
        return plan;
    }

    /// <summary>
    /// Outcome of second merged right after first, both from the target, for overlapping pairs. Computed once per
    /// search, not once per state. Null when first doesn't merge on the target alone.
    /// </summary>
    readonly Dictionary<(string, string), MergeOutcome?> _pairOutcome = [];
    MergeOutcome? PairOutcome(PullRequest first, PullRequest second) => _pairOutcome[(first.Id, second.Id)];

    /// <summary>
    /// Pending PRs that pr's merge may block: they overlap pr and, merged after pr from the target, conflict.
    /// Only these get the exact simulation on each state; the others take their pairwise outcome.
    /// </summary>
    // ponytail: pairwise clean/regenerate outcomes are assumed to hold on later states too (a regenerate pair needs it
    // in either order anyway). Three-way interactions still surface when that PR is simulated as a candidate.
    IEnumerable<PullRequest> Risky(Node n, List<PullRequest> pool, PullRequest pr) =>
        pool.Where(o => o != pr && !n.Merged.Contains(o.Id) && Weight(pr, o) > 0 && PairOutcome(pr, o) is null or MergeOutcome.Conflict);

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

    /// <summary>
    /// Marginal cost of merging pr now: regeneration work, minus an unblock bonus, plus the risk it pushes onto pending
    /// PRs that overlap it. That risk is state-aware: each risky one is simulated on the state after pr, and the simulation
    /// outranks the static graph. A real conflict costs 1, a forced regenerate 0.5 (both configurable), and a clean merge keeps half the
    /// static weight as residual (semantic) risk.
    /// </summary>
    (double Cost, string Reason) Score(PullRequest pr, List<PullRequest> others, SimulationResult r)
    {
        var reasons = new List<string>();
        var overlaps = others.Where(o => Weight(pr, o) > 0).OrderByDescending(o => Weight(pr, o)).ToList();
        var unlocks = others.Where(o => o.Dependencies.Contains(pr.Id)).ToList();
        var after = overlaps.ToLookup(o => PairOutcome(pr, o) is { } p && p != MergeOutcome.Conflict ? p : sim.Simulate(r.Commit!.Value, o).Outcome);
        var c = sim.Config.Costs;
        double Risk(PullRequest o) => after[MergeOutcome.Conflict].Contains(o) ? c.Conflict
            : after[MergeOutcome.RegenerationRequired].Contains(o) ? c.Regeneration : 0.5 * Weight(pr, o);

        double cost = overlaps.Sum(Risk) + c.Regeneration * r.RegenerateFiles.Count - c.DependencyUnblock * unlocks.Count;

        if (Analyzer.GitHubNote(pr) is { } gh) reasons.Add(gh);
        if (pr.Dependencies.Count > 0)
            reasons.Add("dependency: " + string.Join(", ", pr.Dependencies.Select(d => pr.DependencyNotes.TryGetValue(d, out var why) ? $"{d} ({why})" : d)));
        if (unlocks.Count > 0) reasons.Add("unlocks " + string.Join(", ", unlocks));
        if (r.RegenerateFiles.Count > 0)
            reasons.Add("regenerate after merge: " + string.Join(", ", r.RegenerateFiles.Select(f => sim.Config.RegenerateCommand(f) is { } cmd ? $"{f} ({cmd})" : f)));
        if (after[MergeOutcome.Conflict].Any()) reasons.Add("then conflicts: " + string.Join(", ", after[MergeOutcome.Conflict]));
        if (after[MergeOutcome.RegenerationRequired].Any())
            reasons.Add("then needs regenerate: " + string.Join(", ", after[MergeOutcome.RegenerationRequired]));
        var hot = overlaps.SelectMany(o => _weights[(pr.Id, o.Id)].Files).Distinct()
            .Where(f => history?.GetValueOrDefault(f) > 0).ToList();
        var semantic = overlaps.SelectMany(o => Analyzer.SemanticRisks(pr, o)).ToList();
        if (semantic.Count > 0) reasons.Add("semantic: " + string.Join("; ", semantic));
        if (hot.Count > 0) reasons.Add("conflict-prone in past merges: " + string.Join(", ", hot.Select(f => $"{f} ({history![f]:P0})")));
        reasons.Add(overlaps.Count == 0
            ? "no overlapping changes with pending PRs"
            : "overlaps " + string.Join(", ", overlaps.Select(o => $"{o} ({Report.RiskLevel(Weight(pr, o))}{SharedMembers(pr, o)})")));
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

    /// <summary>
    /// For overlapping, independent planned pairs whose two orders simulate differently: why the plan's order wins.
    /// Plus, per policy-blocked PR, the planned PR it overlaps most: it can't be ordered until its policy is resolved.
    /// </summary>
    List<Explanation> Explain(List<PlanStep> steps, List<BlockedPr> blocked)
    {
        var res = new List<Explanation>();
        Explanation New(PullRequest a, PullRequest b, string ab, string ba, string reason) =>
            new(a.Id, b.Id, _weights[(a.Id, b.Id)].Files, a.Members.Intersect(b.Members).ToList(), ab, ba, reason);
        for (int i = 0; i < steps.Count; i++)
            for (int j = i + 1; j < steps.Count; j++)
            {
                var (a, b) = (steps[i].Pr, steps[j].Pr);
                if (b.Dependencies.Contains(a.Id) || Weight(a, b) == 0) continue;
                var (ab, ba) = (PairOutcome(a, b), PairOutcome(b, a));
                if (ab == ba) continue;
                var why = Analyzer.SemanticRisks(a, b).FirstOrDefault()
                    ?? $"after {a}, {b} {Effect(ab)}; after {b}, {a} {Effect(ba)}";
                res.Add(New(a, b, Word(ab), Word(ba), why));
            }
        foreach (var p in blocked.Where(x => x.Policy).Select(x => x.Pr))
            if (steps.Select(s => s.Pr).Where(s => Weight(s, p) > 0).MaxBy(s => Weight(s, p)) is { } s)
                res.Add(New(s, p, "policy blocked", "policy blocked", $"{p} overlaps {s} but can't be ordered until its policy block is resolved"));
        return res;
    }

    static string Word(MergeOutcome? o) => o switch
    {
        MergeOutcome.Clean => "clean",
        MergeOutcome.RegenerationRequired => "regenerate",
        _ => "conflict", // null: the first PR alone already conflicts on the target
    };

    static string Effect(MergeOutcome? o) => o switch
    {
        MergeOutcome.Clean => "merges cleanly",
        MergeOutcome.RegenerationRequired => "needs a regenerate",
        _ => "conflicts",
    };
}
