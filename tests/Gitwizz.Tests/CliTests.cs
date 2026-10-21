using System.Text.Json;
using Gitwizz;

namespace Gitwizz.Tests;

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
    public void Folded_commands_are_rewritten_to_their_replacement_with_a_note()
    {
        var (args, note) = Cli.Deprecated(["explain", "57", "-f", "json"]);
        Assert.Equal(["evaluate", "57", "-f", "json", "--explain"], args);
        Assert.Equal("'gitwizz explain' is now 'gitwizz evaluate --explain'; the old name will be removed in a later release", note);
        Assert.Equal(["context", "57", "-o", "e.json", "--ai"], Cli.Deprecated(["evidence", "57", "-o", "e.json"]).Args);
        Assert.Equal(["status", "--refresh", "--all-open"], Cli.Deprecated(["refresh", "--all-open"]).Args);
        Assert.Equal(["status", "-r", "x"], Cli.Deprecated(["cache", "status", "-r", "x"]).Args);
        (args, note) = Cli.Deprecated(["cache", "clear"]);
        Assert.Equal(["status", "--clear-cache"], args);
        Assert.Contains("'gitwizz cache clear' is now 'gitwizz status --clear-cache'", note);

        // Everything else is left alone, including an incomplete cache command (it stays an unknown command).
        Assert.Null(Cli.Deprecated(["evaluate", "57"]).Note);
        Assert.Null(Cli.Deprecated(["cache"]).Note);
        Assert.Null(Cli.Deprecated([]).Note);
    }

    [Fact]
    public void Folded_commands_became_options()
    {
        Assert.Equal("true", Cli.Parse(["--explain", "-f", "json"], "evaluate")["explain"]);
        Assert.Equal("true", Cli.Parse(["--ai", "-s", "squash"], "context")["ai"]);
        var o = Cli.Parse(["--refresh", "--all-open"], "status");
        Assert.Equal("true", o["refresh"]);
        Assert.Equal("true", o["all-open"]);
        Assert.Equal("true", Cli.Parse(["--clear-cache"], "status")["clear-cache"]);
        var e = Assert.Throws<ArgumentException>(() => Cli.Parse(["--explain"], "trace"));
        Assert.Contains("it belongs to evaluate", e.Message);
    }

    [Fact]
    public void Example_repository_shows_every_outcome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gitwizz_example-" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public void Reads_branch_policy_and_gates_on_required_checks()
    {
        var policy = Providers.ParsePolicy(
            """{"protection":{"required_status_checks":{"contexts":["build"]}}}""",
            """[{"type":"merge_queue","parameters":{"merge_method":"SQUASH"}}, {"type":"required_linear_history"},""" +
            """ {"type":"required_status_checks","parameters":{"required_status_checks":[{"context":"test"}]}}]""");
        Assert.True(policy.MergeQueue);
        Assert.Equal(MergeStrategy.Squash, policy.QueueStrategy);
        Assert.True(policy.LinearHistory);
        Assert.Equal(["build", "test"], policy.RequiredChecks.Order());
        Assert.Empty(Providers.ParsePolicy("", "").RequiredChecks);

        JsonElement Rollup(string json) => JsonDocument.Parse(json).RootElement;
        var lintFailed = Rollup("""[{"name":"build","status":"COMPLETED","conclusion":"SUCCESS"}, {"context":"test","state":"SUCCESS"},""" +
            """ {"name":"lint","status":"COMPLETED","conclusion":"FAILURE"}]""");
        Assert.Equal("SUCCESS", Providers.CiStatus(lintFailed, policy.RequiredChecks)); // lint isn't required
        Assert.Equal("FAILURE", Providers.CiStatus(lintFailed, new HashSet<string>()));
        Assert.Equal("PENDING", Providers.CiStatus(Rollup("""[{"name":"build","status":"COMPLETED","conclusion":"SUCCESS"}]"""), policy.RequiredChecks));
        Assert.Null(Providers.CiStatus(Rollup("[]"), new HashSet<string>()));

        var plan = new Plan { Target = "main", Strategy = MergeStrategy.Squash, Provider = "github", MergeQueue = true };
        plan.Steps.Add(new PlanStep(new PullRequest { Id = "#7", HeadSha = "x" }, 0, "", []));
        Assert.Equal("gh pr merge 7", Report.NextCommand(plan));
    }
}
