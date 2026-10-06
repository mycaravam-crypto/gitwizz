using PrOptimizer;

namespace PrOptimizer.Tests;

public class CliTests
{
    [Fact]
    public void Parses_aliases_flags_and_suggests_typos()
    {
        var o = Cli.Parse(["-t", "dev", "-a", "-s", "squash", "--beam", "3", "-o", "plan.html"]);
        Assert.Equal("dev", o["target"]);
        Assert.Equal("true", o["all-open"]);
        Assert.Equal("squash", o["strategy"]);
        Assert.Equal("plan.html", o["output"]);
        var e = Assert.Throws<ArgumentException>(() => Cli.Parse(["--strat", "squash"]));
        Assert.Contains("--strategy", e.Message);
        Assert.Throws<ArgumentException>(() => Cli.Parse(["--target"]));
    }

    [Fact]
    public void Example_repository_shows_every_outcome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pr-opt-example-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Example.Create(dir);
            Example.Create(dir); // re-creating its own example is allowed
            var git = new Git(dir);
            var branches = git.Run("for-each-ref", "--format=%(refname:short)", "--no-merged", "main", "refs/heads").Split('\n');
            var (sha, prs) = Providers.Local(git, "main", branches);
            foreach (var pr in prs) Analyzer.Analyze(git, sha, pr);
            Analyzer.ResolveDependencies(git, sha, prs);
            var plan = new Planner(new Simulator(git, MergeStrategy.Merge), "main", sha, prs).Build(8);

            Assert.Equal(["fix/billing-rounding"], plan.Blocked.Select(b => b.Pr.Id));
            Assert.Contains(plan.Steps, s => s.RegenerateFiles.Count > 0);
            Assert.Contains("feature/billing-tax", prs.Single(p => p.Id == "feature/billing-refactor").Dependencies);
            Assert.Contains("BillingService.CalculateTax(decimal)", prs.Single(p => p.Id == "fix/billing-rounding").Members);
            Assert.Equal("git checkout main && git merge --no-ff docs/getting-started", Report.NextCommand(plan));
        }
        finally { Directory.Delete(dir, true); }

        // Never deletes a directory it didn't create.
        var other = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(other, "keep.txt"), "x");
        Assert.Throws<InvalidOperationException>(() => Example.Create(other));
        Assert.True(File.Exists(Path.Combine(other, "keep.txt")));
        Directory.Delete(other, true);
    }
}
