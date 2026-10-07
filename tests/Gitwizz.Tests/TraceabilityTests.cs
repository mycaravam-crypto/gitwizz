using System.Text.Json;
using Gitwizz;

namespace Gitwizz.Tests;

public class TraceabilityTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("gitwizz-trace").FullName;
    readonly Git _git;

    static readonly WorkItem Story = new("AB#4711", "User Story", "Reduced VAT", "Active",
        ["Food uses 7 %", "Books use 7 %", "Invoices show the rate", "Old invoices keep their rate"]);
    static readonly WorkItem Ux = new("#99", "Issue", "Looks right", "OPEN", ["[manual] the rate reads well on the invoice"]);

    public TraceabilityTests()
    {
        _git = new Git(_dir);
        _git.Run("init", "-q", "-b", "main");
        _git.Run("config", "user.email", "t@t"); _git.Run("config", "user.name", "t");
        Write("src/Billing/Tax.cs", "class Tax\n{\n    decimal Rate(decimal x) { return 0.19m; }\n}\n");
        Write("src/Ui/View.cs", "class View { }\n");
        Write("tests/BillingTests.cs", "// verifies AB#4711.1\nclass BillingTests { void T() { new Tax().Rate(1); } }\n");
        Write("tests/UiTests.cs", "class UiTests { }\n");
        Policy(contract: "exit 1", require: false);
        Commit("init");
        _git.Run("checkout", "-q", "-b", "feature");
        Write("src/Billing/Tax.cs", "class Tax\n{\n    decimal Rate(decimal x) { return x > 0 ? 0.07m : 0.19m; }\n}\n");
        Commit("reduced rate");
        _git.Run("checkout", "-q", "main");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    void Write(string f, string c) { var p = Path.Combine(_dir, f); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, c); }
    void Commit(string m) { _git.Run("add", "-A"); _git.Run("commit", "-q", "-m", m); }

    void Policy(string contract, bool require) => Write(".gitwizz.yml", $$"""
        gates:
          - id: traceability
        traceability: { require: {{(require ? "true" : "false")}} }
        tests:
          - { id: billing, run: "true", files: ["tests/Billing*"] }
          - { id: ui, kind: ui, run: "true", covers: ["src/Ui/*"], files: ["tests/Ui*"] }
          - { id: contract, kind: contract, run: "{{contract}}", criteria: ["AB#4711.2"] }
          - { id: smoke, run: "true", always: true }
          - { id: e2e, kind: e2e, run: "true", high_risk: true }
          - { id: exploratory, kind: manual, criteria: ["AB#4711.3"] }
        """);

    Evaluation Evaluate()
    {
        var (sha, prs) = Providers.Local(_git, "main", ["feature"]);
        var pr = prs.Single();
        Analyzer.Analyze(_git, sha, pr);
        pr.WorkItems = [Story, Ux];
        using var ctx = new GateContext { Git = _git, Pr = pr, Target = "main", TargetSha = sha, Config = RepoConfig.Load(_git, sha) };
        return Evaluator.Run(ctx);
    }

    [Fact]
    public void Selects_tests_by_impact_and_traces_every_criterion_to_a_result()
    {
        var e = Evaluate();
        var t = e.Trace!;
        Assert.Equal(["billing", "contract", "smoke"], t.Selected.Select(s => s.Suite.Id));
        Assert.Contains(t.Selected[0].Reasons, r => r.Contains("tests/BillingTests.cs:2 uses Rate"));
        Assert.Contains(t.Selected[0].Reasons, r => r == "verifies AB#4711.1");
        Assert.Contains("verifies AB#4711.2", t.Selected[1].Reasons);
        Assert.Equal(["always runs"], t.Selected[2].Reasons);
        Assert.Contains("ui: not affected by this change", t.NotSelected);
        Assert.Contains("e2e: not affected by this change", t.NotSelected); // low risk: no high-risk suites

        Assert.Equal(["covered", "failed", "manual", "uncovered", "manual"], t.Criteria.Select(c => c.Status));
        Assert.Equal(["AB#4711.1", "AB#4711.2", "AB#4711.3", "AB#4711.4", "#99.1"], t.Criteria.Select(c => c.Id));
        Assert.Contains("tests/BillingTests.cs:1 mentions AB#4711.1", t.Criteria[0].Evidence);

        var gate = e.Gates.Single(g => g.Id == "traceability");
        Assert.Equal(GateStatus.Fail, gate.Status); // contract failed
        Assert.False(e.Ready);
        var json = JsonDocument.Parse(Evaluator.Json(e)).RootElement.GetProperty("traceability");
        Assert.Equal("gitwizz.trace/v1", json.GetProperty("schema").GetString());
        Assert.Equal("contract", json.GetProperty("runs")[1].GetProperty("suite").GetString());
        Assert.Equal("fail", json.GetProperty("runs")[1].GetProperty("status").GetString());
        Assert.Contains("AB#4711.4", Evaluator.Explain(e));
    }

    [Theory]
    [InlineData(false, GateStatus.Warn)]
    [InlineData(true, GateStatus.Fail)]
    public void Uncovered_criteria_block_only_when_policy_requires_it(bool require, GateStatus expected)
    {
        Policy(contract: "true", require);
        Commit("policy");
        var e = Evaluate();
        var gate = e.Gates.Single(g => g.Id == "traceability");
        Assert.Equal(expected, gate.Status);
        Assert.Contains(gate.Findings, f => f.Message.StartsWith("AB#4711.4 uncovered"));
        Assert.Equal(!require, e.Ready);
    }

    [Fact]
    public void Links_are_not_coverage_until_a_test_ran()
    {
        var (sha, prs) = Providers.Local(_git, "main", ["feature"]);
        var pr = prs.Single();
        Analyzer.Analyze(_git, sha, pr);
        pr.WorkItems = [Story];
        var t = Traceability.Build(_git, pr, RepoConfig.Load(_git, sha), pr.HeadSha);
        Assert.Equal(["unknown", "unknown", "manual", "uncovered"], t.Criteria.Select(c => c.Status));
        Assert.Contains("SELECTED TESTS".ToLowerInvariant(), Traceability.Text(t).ToLowerInvariant());
    }

    [Fact]
    public void High_risk_changes_also_run_high_risk_suites()
    {
        Write(".gitwizz.yml", File.ReadAllText(Path.Combine(_dir, ".gitwizz.yml")) + "\nrisk:\n  high_paths: [\"src/Billing/*\"]\n");
        Commit("billing is critical");
        var t = Evaluate().Trace!;
        var e2e = t.Selected.Single(s => s.Suite.Id == "e2e");
        Assert.StartsWith("high-risk change (changes src/Billing/Tax.cs", e2e.Reasons.Single());
    }

    [Fact]
    public void Invalid_test_suites_are_rejected()
    {
        Assert.Contains("needs a run", Assert.Throws<InvalidOperationException>(() => RepoConfig.Parse("tests:\n  - { id: a }")).Message);
        Assert.Contains("defined twice", Assert.Throws<InvalidOperationException>(() => RepoConfig.Parse("tests:\n  - { id: a, run: x }\n  - { id: a, run: y }")).Message);
        Assert.Equal("", RepoConfig.Parse("tests:\n  - { id: a, kind: manual }").Tests.Single().Run);
    }
}
