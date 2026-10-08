using Gitwizz;
using Spectre.Console;

namespace Gitwizz.Tests;

public class GuideTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("gitwizz-guide").FullName;
    readonly string _runs = Path.Combine(Path.GetTempPath(), "gitwizz-guide-runs-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Git _git;

    static readonly WorkItem Story = new("AB#1", "User Story", "Reduced VAT", "Active", ["Food uses 7 %", "Old invoices keep their rate"]);

    public GuideTests()
    {
        _git = new Git(_dir);
        _git.Run("init", "-q", "-b", "main");
        _git.Run("config", "user.email", "t@t"); _git.Run("config", "user.name", "t");
        Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.19m; }\n}\n");
        Write("tests/TaxTests.cs", "// verifies AB#1.1\nclass TaxTests { }\n");
        Policy();
        Commit("init");
        Branch("feature", () => Write("src/Food.cs", "class Food { decimal Rate() => 0.07m; }\n"));
        Branch("clash", () => Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.10m; }\n}\n"));
        _git.Run("checkout", "-q", "main");
        Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.20m; }\n}\n"); // clash now conflicts
        Commit("main moves on");
    }

    public void Dispose()
    {
        Directory.Delete(_dir, true);
        if (File.Exists(_runs)) File.Delete(_runs);
    }

    void Write(string f, string c) { var p = Path.Combine(_dir, f); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, c); }
    void Commit(string m) { _git.Run("add", "-A"); _git.Run("commit", "-q", "-m", m); }
    void Branch(string name, Action change) { _git.Run("checkout", "-q", "-b", name, "main"); change(); Commit(name); }

    /// <summary>A traceability policy; tax counts its runs in _runs. extra: more gates.</summary>
    void Policy(string tax = "true", bool require = true, string extra = "") => Write(".gitwizz.yml", $$"""
        gates:
          - id: traceability
        {{extra}}
        traceability: { require: {{(require ? "true" : "false")}} }
        tests:
          - { id: tax, run: "echo ran >> '{{_runs}}' && {{tax}}", files: ["tests/Tax*"], covers: ["src/*"] }
        """);

    /// <summary>Changes main's policy (the guide reads it from the target).</summary>
    void MainPolicy(string tax = "true", bool require = true, string extra = "")
    {
        _git.Run("checkout", "-q", "main");
        Policy(tax, require, extra);
        Commit("policy");
    }

    /// <summary>Answers each question with the next scripted choice: a GuideAction, or a function picking from the options.</summary>
    internal sealed class Script(params object[] choices) : IGuidePrompts
    {
        readonly Queue<object> _choices = new(choices);
        public List<IReadOnlyList<GuideAction>> Asked { get; } = [];
        public T Choose<T>(string question, IReadOnlyList<T> options, Func<T, string> label) where T : notnull
        {
            if (options is IReadOnlyList<GuideAction> actions) Asked.Add(actions);
            var next = _choices.Dequeue();
            var c = next is Func<IReadOnlyList<T>, Func<T, string>, T> pick ? pick(options, label) : (T)next;
            Assert.Contains(c, options);
            return c;
        }
        public string Ask(string question, string fallback) => fallback;
    }

    (int Code, string Output) Guide(string branch, IGuidePrompts? prompts = null, WorkItem[]? items = null, string? evidence = null,
        Func<ProgressBars, Plan>? planner = null)
    {
        var (sha, prs) = Providers.Local(_git, "main", [branch]);
        var pr = prs.Single();
        Analyzer.Analyze(_git, sha, pr);
        Analyzer.ResolveDependencies(_git, sha, prs);
        pr.WorkItems = [.. items ?? [Story]];
        using var wf = new PullRequestWorkflow(_git, "local", "main", sha, pr, new BranchPolicy([]), RepoConfig.Load(_git, sha), evidenceDir: evidence);
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No, Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No,
        });
        console.Profile.Width = 200;
        SystemContexts.Override = _ => new FakeDocwizz { VersionText = null }; // no docwizz here, whatever is installed
        try
        {
            var code = new Guide(wf, new GuideOptions($"gitwizz guide {branch}", EvidenceDir: evidence), console, console, prompts, planner).Run();
            return (code, writer.ToString());
        }
        finally { SystemContexts.Override = null; }
    }

    int Runs => File.Exists(_runs) ? File.ReadAllLines(_runs).Length : 0;

    [Fact]
    public void State_machine_walks_context_trace_test_evaluate_to_a_verdict()
    {
        var facts = new GuideFacts(Merges: true, Selected: 2);
        Assert.Equal(GuideState.SystemContext, Gitwizz.Guide.Next(GuideState.Context, facts));
        Assert.Equal(GuideState.Trace, Gitwizz.Guide.Next(GuideState.SystemContext, facts));                                // good context: nothing asked
        Assert.Equal(GuideState.Trace, Gitwizz.Guide.Next(GuideState.SystemContext, facts, GuideAction.ContinueWithContext));
        Assert.Equal(GuideState.SystemContext, Gitwizz.Guide.Next(GuideState.SystemContext, facts, GuideAction.ShowContextGraph));
        Assert.Equal(GuideState.SystemContext, Gitwizz.Guide.Next(GuideState.SystemContext, facts, GuideAction.RefreshContext));
        Assert.Equal(GuideState.Done, Gitwizz.Guide.Next(GuideState.SystemContext, facts, GuideAction.Exit));
        Assert.Equal(GuideState.Test, Gitwizz.Guide.Next(GuideState.Trace, facts));
        Assert.Equal(GuideState.Evaluate, Gitwizz.Guide.Next(GuideState.Trace, facts with { Merges = false }));   // nothing to test on
        Assert.Equal(GuideState.Evaluate, Gitwizz.Guide.Next(GuideState.Trace, facts with { Selected = 0 }));
        Assert.Equal(GuideState.Evaluate, Gitwizz.Guide.Next(GuideState.Trace, facts with { TestsDeferred = true })); // the gates provision first
        Assert.Equal(GuideState.Evaluate, Gitwizz.Guide.Next(GuideState.Test, facts, GuideAction.RunTests));
        Assert.Equal(GuideState.Evaluate, Gitwizz.Guide.Next(GuideState.Test, facts, GuideAction.SkipTests));
        Assert.Equal(GuideState.Test, Gitwizz.Guide.Next(GuideState.Test, facts, GuideAction.ShowTrace));
        Assert.Equal(GuideState.Done, Gitwizz.Guide.Next(GuideState.Test, facts, GuideAction.Exit));
        Assert.Equal(GuideState.Ready, Gitwizz.Guide.Next(GuideState.Evaluate, facts with { Verdict = "ready" }));
        Assert.Equal(GuideState.Blocked, Gitwizz.Guide.Next(GuideState.Evaluate, facts with { Verdict = "blocked" }));
        Assert.Equal(GuideState.Undetermined, Gitwizz.Guide.Next(GuideState.Evaluate, facts with { Verdict = "undetermined" }));
        Assert.Equal(GuideState.Blocked, Gitwizz.Guide.Next(GuideState.Blocked, facts, GuideAction.ShowExplanation));
        Assert.Equal(GuideState.Done, Gitwizz.Guide.Next(GuideState.Blocked, facts, GuideAction.Exit));
        Assert.Equal(GuideState.Done, Gitwizz.Guide.Next(GuideState.Ready, facts)); // nothing asked: stop at the verdict

        // Without a terminal: run the tests, then stop. Merging is never on offer.
        Assert.Equal(GuideAction.RunTests, Gitwizz.Guide.Default(GuideState.Test));
        Assert.Equal(GuideAction.Exit, Gitwizz.Guide.Default(GuideState.Blocked));
        Assert.Equal(GuideAction.ShowPlan, Gitwizz.Guide.Actions(GuideState.Ready)[0]);
        Assert.Equal(GuideAction.RunTests, Gitwizz.Guide.Actions(GuideState.Test)[0]);
        Assert.Equal(GuideAction.ContinueWithContext, Gitwizz.Guide.Default(GuideState.SystemContext)); // missing context is advisory
        Assert.Equal([GuideAction.ContinueWithContext, GuideAction.ShowContextHelp, GuideAction.Exit],
            Gitwizz.Guide.Actions(GuideState.SystemContext, new GuideFacts(Context: "MISSING", DocwizzAvailable: false)));
    }

    [Fact]
    public void Uncovered_criterion_blocks_with_the_criterion_and_the_next_action()
    {
        var (code, output) = Guide("feature");
        Assert.Equal(3, code);
        Assert.Contains("PR feature", output);
        Assert.Contains("From feature -> main", output);
        Assert.Contains("Provider: local", output);
        Assert.Contains("1 linked work item: AB#1", output);
        Assert.Contains("2 acceptance criteria", output);
        Assert.Contains("1 of 1 suite selected: tax", output);
        Assert.Contains("1/2 acceptance criteria linked to a test", output);
        Assert.Contains("! AB#1.2 has no verifying test", output);          // shown before anything runs
        Assert.True(output.IndexOf("has no verifying test") < output.IndexOf("Step 4/6"));
        Assert.Contains("✓ tax: passed", output);
        Assert.Contains("! AB#1.2 remains uncovered", output);
        Assert.Contains("VERDICT: BLOCKED", output);
        Assert.Contains("Add or link a test for AB#1.2", output);
        Assert.Contains("gitwizz guide feature", output);
        Assert.Equal(1, Runs); // the evaluation reused the guide's test run on the same merged state
    }

    [Fact]
    public void Failed_test_blocks_and_names_the_suite()
    {
        MainPolicy(tax: "exit 1");
        var (code, output) = Guide("feature", items: [Story with { AcceptanceCriteria = ["Food uses 7 %"] }]);
        Assert.Equal(3, code);
        Assert.Contains("✗ tax: failed (exit code 1)", output);
        Assert.Contains("✗ AB#1.1 failed (tax)", output);
        Assert.Contains("VERDICT: BLOCKED", output);
        Assert.Contains("Fix the failing test suite tax", output);
    }

    [Fact]
    public void Ready_offers_the_merge_plan_and_never_merges()
    {
        var refs = _git.Run("for-each-ref");
        var planned = false;
        Plan Planner(ProgressBars p)
        {
            planned = true;
            var (sha, prs) = Providers.Local(_git, "main", ["feature"]);
            foreach (var pr in prs) Analyzer.Analyze(_git, sha, pr);
            return new Planner(new Simulator(_git, MergeStrategy.Merge), "main", sha, prs).Build(8);
        }
        var evidence = Directory.CreateDirectory(Path.Combine(_dir, ".evidence")).FullName;
        var script = new Script(GuideAction.ContinueWithContext, GuideAction.ShowTrace, GuideAction.RunTests, GuideAction.ShowPlan, GuideAction.SaveEvidence, GuideAction.Exit);
        var (code, output) = Guide("feature", script, items: [Story with { AcceptanceCriteria = ["Food uses 7 %"] }], evidence, Planner);

        Assert.Equal(0, code);
        Assert.Contains("TRACEABILITY feature", output); // the details, on request
        Assert.Contains("VERDICT: READY", output);
        Assert.True(planned);
        Assert.Contains("1. feature  <- this PR", output);
        Assert.Contains("This PR merges next:", output);
        Assert.Contains("git checkout main && git merge --no-ff feature", output);
        Assert.Contains("gitwizz never merges", output);
        Assert.True(File.Exists(Path.Combine(evidence, "evaluation.json")));
        Assert.True(File.Exists(Path.Combine(evidence, "evidence.json")));
        Assert.Equal(refs, _git.Run("for-each-ref")); // no branch moved
        Assert.Equal(1, Runs);
        Assert.Equal([GuideAction.ShowPlan, GuideAction.ShowExplanation, GuideAction.SaveEvidence, GuideAction.Exit], script.Asked[^1]);
    }

    [Fact]
    public void Tool_failure_is_undetermined_not_a_verdict_on_the_change()
    {
        MainPolicy(extra: "  - { id: build, run: gitwizz-no-such-tool }");
        var (code, output) = Guide("feature", items: [Story with { AcceptanceCriteria = ["Food uses 7 %"] }]);
        Assert.Equal(4, code);
        Assert.Contains("VERDICT: UNDETERMINED", output);
        Assert.Contains("Not a verdict on the change", output);
        Assert.Contains("Fix what stopped build from running", output);
    }

    [Fact]
    public void Conflict_skips_the_tests_and_says_how_to_resolve_it()
    {
        var (code, output) = Guide("clash");
        Assert.Equal(3, code);
        Assert.Contains("✗ conflicts with main in 1 file(s): tests can't run on the merged state", output);
        Assert.Contains("can't run: the PR doesn't merge into main", output);
        Assert.Contains("Bring main into clash and resolve the conflicts in src/Tax.cs, then push", output);
        Assert.Equal(0, Runs);
    }

    [Fact]
    public void Exit_before_a_verdict_runs_nothing_and_returns_4()
    {
        var (code, output) = Guide("feature", new Script(GuideAction.Exit));
        Assert.Equal(4, code);
        Assert.DoesNotContain("VERDICT", output);
        Assert.Equal(0, Runs);
    }

    [Fact]
    public void Rerun_recomputes_the_state_after_a_fix()
    {
        Assert.Equal(3, Guide("feature").Code);
        _git.Run("checkout", "-q", "feature");
        Write("tests/TaxTests.cs", "// verifies AB#1.1\n// verifies AB#1.2\nclass TaxTests { }\n");
        Commit("cover AB#1.2");
        _git.Run("checkout", "-q", "main");
        var (code, output) = Guide("feature");
        Assert.Equal(0, code);
        Assert.Contains("2/2 acceptance criteria linked to a test", output);
        Assert.Contains("VERDICT: READY", output);
    }

    [Fact]
    public void Verdict_matches_evaluate()
    {
        var (sha, prs) = Providers.Local(_git, "main", ["feature"]);
        var pr = prs.Single();
        Analyzer.Analyze(_git, sha, pr);
        pr.WorkItems = [Story];
        using var ctx = new GateContext { Git = _git, Pr = pr, Target = "main", TargetSha = sha, Config = RepoConfig.Load(_git, sha) };
        var evaluated = Evaluator.Run(ctx);
        Assert.Equal(evaluated.ExitCode, Guide("feature").Code);
    }
}
