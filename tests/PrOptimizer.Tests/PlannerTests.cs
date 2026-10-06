using PrOptimizer;

namespace PrOptimizer.Tests;

public class PlannerTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("pr-opt-test").FullName;
    readonly Git _git;

    public PlannerTests()
    {
        _git = new Git(_dir);
        _git.Run("init", "-q", "-b", "main");
        _git.Run("config", "user.email", "t@t"); _git.Run("config", "user.name", "t");
        Write("billing.cs", Lines(10));
        Write("docs.md", "docs\n");
        Write("package-lock.json", "{\n\"v\": 1\n}\n");
        Commit("init");

        Branch("docs", "main", () => Write("docs.md", "better docs\n"));
        Branch("billing", "main", () => Write("billing.cs", Lines(10).Replace("line2\n", "billing2\n")));
        Branch("refactor", "billing", () => Write("billing.cs", File.ReadAllText(P("billing.cs")).Replace("line8\n", "refactor8\n")));
        Branch("clash", "main", () => Write("billing.cs", Lines(10).Replace("line2\n", "clash2\n")));
        Branch("deps1", "main", () => Write("package-lock.json", "{\n\"v\": 2\n}\n"));
        Branch("deps2", "main", () => Write("package-lock.json", "{\n\"v\": 3\n}\n"));
        _git.Run("checkout", "-q", "main");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    string P(string f) => Path.Combine(_dir, f);
    void Write(string f, string c) => File.WriteAllText(P(f), c);
    static string Lines(int n) => string.Concat(Enumerable.Range(1, n).Select(i => $"line{i}\n"));
    void Commit(string m) { _git.Run("add", "-A"); _git.Run("commit", "-q", "-m", m); }
    void Branch(string name, string from, Action change)
    {
        _git.Run("checkout", "-q", "-b", name, from);
        change();
        Commit(name);
    }

    Plan PlanFor(MergeStrategy strategy, int beam, params string[] refs)
    {
        var (sha, prs) = Providers.Local(_git, "main", refs);
        foreach (var pr in prs) Analyzer.Analyze(_git, sha, pr);
        Analyzer.ResolveDependencies(_git, sha, prs);
        return new Planner(new Simulator(_git, strategy), "main", sha, prs).Build(beam);
    }

    [Theory]
    [InlineData(MergeStrategy.Merge, 8)]
    [InlineData(MergeStrategy.Squash, 8)]
    [InlineData(MergeStrategy.Merge, 1)]
    public void Plans_dependencies_conflicts_and_lockfiles(MergeStrategy strategy, int beam)
    {
        var headBefore = _git.RevParse("HEAD");
        var plan = PlanFor(strategy, beam, "clash", "refactor", "docs", "billing", "deps1", "deps2");
        var order = plan.Steps.Select(s => s.Pr.Id).ToList();

        // Structural dependency respected; clash loses because blocking it keeps billing+refactor.
        Assert.True(order.IndexOf("billing") < order.IndexOf("refactor"));
        Assert.Equal(["clash"], plan.Blocked.Select(b => b.Pr.Id));
        Assert.Contains("billing.cs", plan.Blocked[0].Reason);

        // Lockfile conflict is not a block, but flagged for regeneration.
        Assert.Contains(plan.Steps, s => s.RegenerateFiles.Contains("package-lock.json"));
        Assert.Contains("docs", plan.Parallelizable);
        if (beam > 1) Assert.Equal("docs", order[0]); // zero-cost PR goes first on equal totals

        // In-process synthetic commits are valid git objects.
        Assert.Equal("commit", _git.Run("cat-file", "-t", plan.FinalState!));
        Assert.Equal(0, _git.Try("fsck", "--no-dangling").ExitCode);

        // Simulation must not touch the working tree or HEAD.
        Assert.Equal(headBefore, _git.RevParse("HEAD"));
        Assert.Equal("", _git.Run("status", "--porcelain"));
    }

    [Fact]
    public void FfOnly_blocks_diverged_branches()
    {
        var plan = PlanFor(MergeStrategy.FfOnly, 8, "docs", "billing");
        Assert.Single(plan.Steps);
        Assert.Single(plan.Blocked);
    }

    [Fact]
    public void Verify_runs_in_temporary_worktree()
    {
        var plan = PlanFor(MergeStrategy.Merge, 8, "docs", "billing");
        Assert.True(Verify.Run(_git, plan.FinalState!, "grep -q billing2 billing.cs && grep -q better docs.md").Ok);
        Assert.False(Verify.Run(_git, plan.FinalState!, "false").Ok);
        Assert.DoesNotContain("pr-optimizer-", _git.Run("worktree", "list"));
    }

    [Fact]
    public void Parses_hunks_and_explicit_dependencies()
    {
        var hunks = Analyzer.ParseHunks("--- a/x.cs\n+++ b/x.cs\n@@ -3,2 +3,2 @@\n@@ -10 +10,0 @@\n--- /dev/null\n+++ b/new.cs\n@@ -0,0 +1,5 @@\n");
        Assert.Equal([new Hunk("x.cs", 3, 2), new Hunk("x.cs", 10, 1), new Hunk("new.cs", 0, 0)], hunks);
        Assert.True(new Hunk("x.cs", 3, 2).Overlaps(new Hunk("x.cs", 4, 1)));
        Assert.False(new Hunk("x.cs", 3, 2).Overlaps(new Hunk("x.cs", 10, 1)));

        var pr = new PullRequest { Id = "#2", HeadSha = "x", Body = "Depends on #101, blocked-by: #7", Labels = ["depends-on #9"] };
        Assert.Equal(["#101", "#7", "#9"], Analyzer.ExplicitDependencies(pr));

        var a = new PullRequest { Id = "a", HeadSha = "1" };
        var b = new PullRequest { Id = "b", HeadSha = "2" };
        a.Dependencies.Add("b"); b.Dependencies.Add("a");
        Assert.NotNull(Analyzer.FindCycle([a, b]));

        Assert.Equal(FileClass.Lockfile, FileClasses.Classify("web/yarn.lock"));
        Assert.Equal(FileClass.Generated, FileClasses.Classify("Forms/Main.Designer.cs"));
        Assert.Equal(FileClass.Migration, FileClasses.Classify("db/Migrations/001_init.sql"));
    }
}
