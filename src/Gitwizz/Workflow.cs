using System.Text.Json.Nodes;

namespace Gitwizz;

/// <summary>
/// One pull request loaded for evaluation: provider data, analysis, repository policy and the gate context its steps
/// share. The one implementation behind evaluate, explain, trace, context, evidence and guide: the merge is simulated
/// once, and tests the guide ran are reused by the evaluation's traceability gate on the same merged state.
/// </summary>
public sealed class PullRequestWorkflow : IDisposable
{
    public Git Git { get; }
    public string Provider { get; }
    public string Target { get; }
    public string TargetSha { get; }
    public PullRequest Pr { get; }
    public BranchPolicy Policy { get; }
    public RepoConfig Config { get; }
    public GateContext Context { get; }

    GateResult? _merge;

    /// <summary>pr must be analyzed. strategy null: what the branch enforces (merge queue method, linear history), else merge.</summary>
    public PullRequestWorkflow(Git git, string provider, string target, string targetSha, PullRequest pr, BranchPolicy policy, RepoConfig config,
        MergeStrategy? strategy = null, string? evidenceDir = null)
    {
        (Git, Provider, Target, TargetSha, Pr, Policy, Config) = (git, provider, target, targetSha, pr, policy, config);
        Context = new GateContext
        {
            Git = git, Pr = pr, Target = target, TargetSha = targetSha, Provider = provider, Config = config,
            Strategy = strategy ?? policy.QueueStrategy ?? (policy.LinearHistory ? MergeStrategy.Squash : MergeStrategy.Merge),
            Redactor = new Redactor(config.Secrets.Concat(config.Environment.Secrets)), EvidenceDir = evidenceDir,
        };
    }

    /// <summary>Risk level and reasons, as the gates see them.</summary>
    public (string Level, List<string> Reasons) Risk => Gates.Risk(Pr, Config);

    /// <summary>The PR's acceptance criteria across its linked work items.</summary>
    public int CriteriaCount => Pr.WorkItems.Sum(w => w.AcceptanceCriteria.Count);

    /// <summary>Simulates the merge into the target once; on success the merged state is Context.MergedState.</summary>
    public GateResult Merge() => _merge ??= new MergeGate().Run(Context, new GateSpec { Id = "merge" });

    /// <summary>The commit tests and evidence look at: the merged state, else (it doesn't merge) the PR head.</summary>
    public string Commit => Context.MergedState ?? Pr.HeadSha;

    /// <summary>Test selection and requirement links on the merged state; no test runs.</summary>
    public Trace BuildTrace()
    {
        Merge();
        return Context.Trace = Traceability.Build(Git, Pr, Config, Commit);
    }

    /// <summary>Runs the selected suites on the merged state. Throws InvalidOperationException when the PR doesn't merge.</summary>
    public Trace RunTests(ProgressBars? progress = null)
    {
        var trace = Context.Trace ?? BuildTrace();
        if (Context.MergedState == null) throw new InvalidOperationException($"can't run tests: {Merge().Summary}");
        Evaluator.SetEnvironment(Context, Risk.Level);
        Traceability.RunSuites(trace, Config, Context.Workspace, Context.Env, Context.Redactor, Context.EvidenceDir, progress);
        return trace;
    }

    /// <summary>The quality-gate evaluation, exactly as gitwizz evaluate runs it.</summary>
    public Evaluation Evaluate(string? profile = null, ProgressBars? progress = null) => Evaluator.Run(Context, profile, progress);

    /// <summary>The bounded evidence package an AI review would get, built on the merged state; secrets are masked.</summary>
    public JsonObject EvidencePackage()
    {
        Merge();
        var trace = Context.Trace ?? (Config.Tests.Count > 0 ? Traceability.Build(Git, Pr, Config, Commit) : null);
        return Evidence.Build(Git, Pr, Config, Target, TargetSha, Commit, trace);
    }

    /// <summary>The evidence package as redacted JSON.</summary>
    public string EvidenceJson() => Context.Redactor.Apply(Evidence.Json(EvidencePackage()));

    public void Dispose() => Context.Dispose();
}
