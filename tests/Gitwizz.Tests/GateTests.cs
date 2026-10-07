using System.Text.Json;
using Gitwizz;

namespace Gitwizz.Tests;

public class GateTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("pr-opt-gates").FullName;
    readonly Git _git;

    public GateTests()
    {
        _git = new Git(_dir);
        _git.Run("init", "-q", "-b", "main");
        _git.Run("config", "user.email", "t@t"); _git.Run("config", "user.name", "t");
        Write("app.cs", "class App\n{\n    void Run() { }\n}\n");
        Write("docs.md", "docs\n");
        Commit("init");
        Branch("feature", () => Write("docs.md", "better docs\n"));
        Branch("clash", () => Write("app.cs", "class App\n{\n    void Run() { Clash(); }\n}\n"));
        _git.Run("checkout", "-q", "main");
        Write("app.cs", "class App\n{\n    void Run() { Main(); }\n}\n");
        Commit("main moves on");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    void Write(string f, string c) => File.WriteAllText(Path.Combine(_dir, f), c);
    void Commit(string m) { _git.Run("add", "-A"); _git.Run("commit", "-q", "-m", m); }
    void Branch(string name, Action change) { _git.Run("checkout", "-q", "-b", name, "main"); change(); Commit(name); }

    Evaluation Evaluate(string branch, string? profile = null, string provider = "local", Action<PullRequest>? setup = null, string[]? secrets = null)
    {
        var (sha, prs) = Providers.Local(_git, "main", [branch]);
        var pr = prs.Single();
        Analyzer.Analyze(_git, sha, pr);
        setup?.Invoke(pr);
        var config = RepoConfig.Load(_git, sha);
        using var ctx = new GateContext
        {
            Git = _git, Pr = pr, Target = "main", TargetSha = sha, Provider = provider, Config = config,
            Redactor = new Redactor(config.Secrets, secrets),
        };
        return Evaluator.Run(ctx, profile);
    }

    [Fact]
    public void Built_in_gates_judge_mergeability_without_configuration()
    {
        var ok = Evaluate("feature");
        Assert.True(ok.Ready);
        Assert.Equal(0, ok.ExitCode);
        Assert.Equal(["merge", "policy"], ok.Gates.Select(g => g.Id));
        Assert.Equal(GateStatus.Pass, ok.Gates[0].Status);
        Assert.Equal(GateStatus.Skipped, ok.Gates[1].Status); // local branches have no review data
        Assert.Equal("built-in", ok.PolicySource);

        var clash = Evaluate("clash");
        Assert.False(clash.Ready);
        Assert.Equal("blocked", clash.Verdict);
        Assert.Equal(3, clash.ExitCode);
        Assert.Equal("app.cs", Assert.Single(clash.Gates[0].Findings).File);
        Assert.Contains("Why it can't merge", Evaluator.Explain(clash));
    }

    [Fact]
    public void Gates_from_the_target_commit_decide_fail_error_skip_and_advisory()
    {
        Write(".gitwizz.yml", """
            secrets: [GATE_TEST_SECRET]
            gates:
              - id: build
                run: test -f docs.md
              - id: lint
                run: "echo 'app.cs(3,5): error CS1002: ; expected' && exit 1"
                blocking: false
              - id: test
                run: echo "token $GATE_TEST_SECRET" && exit 1
                needs: [build]
              - id: e2e
                run: "true"
                needs: [test]
              - id: missing
                run: no-such-tool-xyz --check
              - id: slow
                run: sleep 5
                timeout: 1
              - id: ui
                run: "true"
                paths: ["web/*"]
            """);
        Commit("gates");
        // A PR can't relax the gates it is judged by: only the target's committed file counts.
        Branch("relax", () => Write(".gitwizz.yml", "gates: []\n"));
        _git.Run("checkout", "-q", "main");

        Environment.SetEnvironmentVariable("GATE_TEST_SECRET", "s3cr3t-value");
        try
        {
            var e = Evaluate("relax");
            var g = e.Gates.ToDictionary(x => x.Id);
            Assert.StartsWith(".gitwizz.yml@", e.PolicySource);
            Assert.Equal("all", e.Profile);
            Assert.Equal(GateStatus.Pass, g["build"].Status);

            Assert.Equal(GateStatus.Fail, g["lint"].Status);
            Assert.False(g["lint"].BlocksMerge); // advisory
            var f = Assert.Single(g["lint"].Findings);
            Assert.Equal(("app.cs", 3, "CS1002"), (f.File, f.Line, f.Rule));

            Assert.Equal(GateStatus.Fail, g["test"].Status);
            Assert.DoesNotContain("s3cr3t-value", g["test"].Log);
            Assert.Contains("***", g["test"].Log);
            Assert.Equal(GateStatus.Skipped, g["e2e"].Status);
            Assert.True(g["e2e"].BlocksMerge); // its evidence is missing
            Assert.Contains("test, which did not pass", g["e2e"].Summary);

            Assert.Equal(GateStatus.Error, g["missing"].Status); // execution failure, not a quality failure
            Assert.Equal(GateStatus.Error, g["slow"].Status);
            Assert.Contains("timed out", g["slow"].Summary);
            Assert.Equal(GateStatus.Skipped, g["ui"].Status);
            Assert.False(g["ui"].BlocksMerge);

            Assert.Equal("blocked", e.Verdict);
            Assert.Equal(["test", "e2e", "missing", "slow"], e.Blockers.Select(b => b.Id));
            var json = JsonDocument.Parse(Evaluator.Json(e)).RootElement;
            Assert.Equal("gitwizz.evaluation/v1", json.GetProperty("schema").GetString());
            Assert.False(json.GetProperty("ready").GetBoolean());
            Assert.DoesNotContain("s3cr3t-value", Evaluator.Json(e));
        }
        finally { Environment.SetEnvironmentVariable("GATE_TEST_SECRET", null); }
    }

    [Fact]
    public void Only_errors_make_the_verdict_undetermined()
    {
        Write(".gitwizz.yml", "gates:\n  - id: check\n    run: no-such-tool-xyz\n");
        Commit("gates");
        var e = Evaluate("feature");
        Assert.Equal("undetermined", e.Verdict);
        Assert.Equal(4, e.ExitCode);
    }

    [Fact]
    public void Profiles_follow_risk_and_needs_pull_in_their_gates()
    {
        var config = RepoConfig.Parse("""
            gates:
              - { id: build, run: "true" }
              - { id: test, run: "true", needs: [build] }
              - { id: docs, type: docwizz, blocking: false }
            profiles:
              fast: [build]
              full: [test, docs]
            risk:
              high_paths: ["src/billing/*"]
              profiles: { low: fast, high: full }
            """);
        Assert.Equal(["merge", "build"], Evaluator.Select(config, null, "low").Gates.Select(g => g.Id));
        var (name, gates) = Evaluator.Select(config, null, "high");
        Assert.Equal("full", name);
        Assert.Equal(["merge", "build", "test", "docs"], gates.Select(g => g.Id));
        Assert.Equal(["merge", "policy"], Evaluator.Select(RepoConfig.Default, null, "low").Gates.Select(g => g.Id));
        Assert.Throws<ArgumentException>(() => Evaluator.Select(config, "nightly", "low"));

        var pr = new PullRequest { Id = "x", HeadSha = "x", Files = [new("src/billing/tax.cs", ChangeKind.Modified)] };
        Assert.Equal("high", Gates.Risk(pr, config).Level);
        pr.Files = [new("appsettings.json", ChangeKind.Modified)];
        Assert.Equal("medium", Gates.Risk(pr, config).Level);
        pr.Files = [new("readme.md", ChangeKind.Modified)];
        Assert.Equal("low", Gates.Risk(pr, config).Level);
    }

    [Theory]
    [InlineData("gates:\n  - { id: a, type: lint }", "unknown type")]
    [InlineData("gates:\n  - { id: a }", "needs a run")]
    [InlineData("gates:\n  - { id: a, run: x }\n  - { id: a, run: y }", "defined twice")]
    [InlineData("gates:\n  - { id: a, run: x, needs: [b] }", "unknown gate 'b'")]
    [InlineData("profiles:\n  p: [nope]", "unknown gate 'nope'")]
    [InlineData("risk:\n  profiles: { high: p }", "unknown profile 'p'")]
    [InlineData("gates:\n  - { id: a, run: x, timeout: 0 }", "timeout")]
    public void Invalid_policies_are_rejected(string yaml, string message)
    {
        var e = Assert.Throws<InvalidOperationException>(() => RepoConfig.Parse(yaml));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void Needs_cycles_are_rejected()
    {
        var config = RepoConfig.Parse("gates:\n  - { id: a, run: x, needs: [b] }\n  - { id: b, run: y, needs: [a] }");
        Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(() => Evaluator.Select(config, null, "low")).Message);
    }

    [Fact]
    public void Policy_gate_lists_every_unmet_rule()
    {
        var e = Evaluate("feature", provider: "github", setup: pr => pr.OpenOutsideDependencies = ["#9"]);
        var policy = e.Gates.Single(g => g.Id == "policy");
        Assert.Equal(GateStatus.Fail, policy.Status);
        Assert.Equal("depends on open pull request #9", Assert.Single(policy.Findings).Message);
        Assert.True(Evaluate("feature", provider: "github").Ready);

        var pr = new PullRequest { Id = "#5", HeadSha = "x", IsDraft = true, CiStatus = "FAILURE", BaseRef = "release" };
        var ctx = new GateContext { Git = _git, Pr = pr, Target = "main", TargetSha = "x", Provider = "github" };
        var r = new PolicyGate().Run(ctx, new GateSpec { Id = "policy" });
        Assert.Equal(GateStatus.Fail, r.Status);
        Assert.Equal(["draft", "checks: FAILURE", "targets 'release', not 'main'"], r.Findings.Select(f => f.Message));
    }

    [Fact]
    public void Parses_compiler_and_test_runner_output()
    {
        var findings = Gates.ParseOutput("""
            /tmp/ws/src/A.cs(12,5): error CS1002: ; expected [/tmp/ws/src/A.csproj]
            /tmp/ws/src/A.cs(12,5): error CS1002: ; expected [/tmp/ws/src/A.csproj]
            src/b.go:7:2: warning: unused variable
              Failed Billing.Tests.Rounds_half_up [12 ms]
            FAILED tests/test_tax.py::test_vat - assert 1 == 2
            """, "/tmp/ws");
        Assert.Equal(4, findings.Count);
        Assert.Equal(("src/A.cs", 12, "CS1002"), (findings[0].File, findings[0].Line, findings[0].Rule));
        Assert.Contains(findings, f => f.Message == "test failed: Billing.Tests.Rounds_half_up");
        Assert.Contains(findings, f => f.Message == "test failed: tests/test_tax.py::test_vat");
        Assert.Equal("warning", findings[^1].Severity);
    }

    [Fact]
    public void Evaluate_options_are_checked_per_command()
    {
        var o = Cli.Parse(["--profile", "fast", "--evidence", "out", "-f", "json"], "evaluate");
        Assert.Equal("fast", o["profile"]);
        Assert.Contains("belongs to plan", Assert.Throws<ArgumentException>(() => Cli.Parse(["--beam", "3"], "evaluate")).Message);
        Assert.Throws<ArgumentException>(() => Cli.Parse(["--profile", "x"], "plan"));
    }
}
