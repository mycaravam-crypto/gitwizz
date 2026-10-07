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
    void Write(string f, string c) { Directory.CreateDirectory(Path.GetDirectoryName(P(f))!); File.WriteAllText(P(f), c); }
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
    public void Tracks_renames_as_one_file_lineage()
    {
        // Large enough that git still detects the rename after small edits (similarity > 50%).
        var foo = "class Foo\n{\n    int Charge() => 1;\n\n    int Refund() => 2;\n"
            + string.Concat(Enumerable.Range(1, 10).Select(i => $"\n    int Get{i}() => {i};\n")) + "}\n";
        Write("Foo.cs", foo);
        Commit("foo");
        void Mv(string to) => _git.Run("mv", "Foo.cs", to);
        Branch("ren", "main", () => { Mv("Bar.cs"); Write("Bar.cs", foo.Replace("=> 1", "=> 10")); });
        Branch("ren-mod", "ren", () => Write("Bar.cs", foo.Replace("=> 1", "=> 10").Replace("=> 2", "=> 20")));
        Branch("ren2", "main", () => Mv("Baz.cs"));
        Branch("mod", "main", () => Write("Foo.cs", foo.Replace("=> 1", "=> 11")));
        Branch("del", "main", () => _git.Run("rm", "-q", "Foo.cs"));
        _git.Run("checkout", "-q", "main");

        var (sha, list) = Providers.Local(_git, "main", ["ren", "ren-mod", "ren2", "mod", "del"]);
        foreach (var pr in list) Analyzer.Analyze(_git, sha, pr);
        Analyzer.ResolveDependencies(_git, sha, list);
        var p = list.ToDictionary(x => x.Id);
        double W(string a, string b) => Analyzer.ConflictWeight(p[a], p[b], out _);

        // rename vs modify: same lineage, same hunk, same member.
        Assert.Contains("Foo.Charge()", p["ren"].Members.Intersect(p["mod"].Members));
        Assert.True(W("ren", "mod") >= 0.7);
        // rename vs rename to another name, delete vs rename: lineage clashes git can't merge.
        Assert.True(W("ren", "ren2") >= 0.4);
        Assert.True(W("del", "ren2") >= 0.4);
        Assert.True(Analyzer.LineageClash(new("Bar.cs", ChangeKind.Renamed, "Foo.cs"), new("Bar.cs", ChangeKind.Added)));
        Assert.False(Analyzer.LineageClash(new("Bar.cs", ChangeKind.Renamed, "Foo.cs"), new("Bar.cs", ChangeKind.Renamed, "Foo.cs")));
        // rename, then modify the new path: a stacked PR that waits for the rename.
        Assert.Equal(["ren"], p["ren-mod"].Dependencies);

        var plan = new Planner(new Simulator(_git, MergeStrategy.Merge), "main", sha, [p["ren"], p["ren-mod"], p["del"]]).Build(8);
        Assert.Equal(["ren", "ren-mod"], plan.Steps.Select(s => s.Pr.Id));
        Assert.Equal(["del"], plan.Blocked.Select(b => b.Pr.Id)); // rename/delete is a real conflict
    }

    [Fact]
    public void Generated_file_conflicts_are_regenerated_and_plans_are_deterministic()
    {
        Write("Api.g.cs", "// gen 1\n");
        Commit("generated");
        Branch("gen1", "main", () => Write("Api.g.cs", "// gen 2\n"));
        Branch("gen2", "main", () => Write("Api.g.cs", "// gen 3\n"));
        _git.Run("checkout", "-q", "main");

        var plan = PlanFor(MergeStrategy.Merge, 8, "gen1", "gen2", "docs");
        Assert.Empty(plan.Blocked);
        Assert.Contains(plan.Steps, s => s.RegenerateFiles.SequenceEqual(["Api.g.cs"]));
        Assert.DoesNotContain("<<<<<<<", _git.Run("show", $"{plan.FinalState}:Api.g.cs"));

        // Same input, same plan, same synthetic final commit, regardless of input order.
        var again = PlanFor(MergeStrategy.Merge, 8, "docs", "gen2", "gen1");
        Assert.Equal(plan.Steps.Select(s => s.Pr.Id), again.Steps.Select(s => s.Pr.Id));
        Assert.Equal(plan.FinalState, again.FinalState);
    }

    [Fact]
    public void Cost_comes_from_the_simulated_state_not_only_the_static_graph()
    {
        var plan = PlanFor(MergeStrategy.Merge, 8, "billing", "clash", "docs");
        var billing = plan.Steps.Single(s => s.Pr.Id == "billing");
        // Statically billing and clash overlap 0.4; simulated on the state after billing, clash really conflicts.
        Assert.Contains("then conflicts: clash", billing.Reason);
        Assert.True(billing.Cost >= 1);
        // Lockfile PRs: after one, the other needs a regenerate (0.5), not a static guess.
        var deps = PlanFor(MergeStrategy.Merge, 8, "deps1", "deps2").Steps[0];
        Assert.Contains("then needs regenerate:", deps.Reason);
        Assert.Equal(0.5, deps.Cost);
    }

    [Fact]
    public void Separates_structural_policy_and_github_readiness()
    {
        var (sha, prs) = Providers.Local(_git, "main", ["docs", "billing", "refactor"]);
        PullRequest With(PullRequest p, bool draft = false, string? state = null) =>
            new() { Id = p.Id, HeadSha = p.HeadSha, HeadRef = p.HeadRef, IsDraft = draft, MergeStateStatus = state };
        prs = [With(prs[0], draft: true), With(prs[1], state: "BEHIND"), prs[2]];
        foreach (var pr in prs) Analyzer.Analyze(_git, sha, pr);
        Analyzer.ResolveDependencies(_git, sha, prs);
        var plan = new Planner(new Simulator(_git, MergeStrategy.Merge), "main", sha, prs).Build(8);

        // docs merges cleanly but is a draft: POLICY BLOCKED, not BLOCKED and not clean.
        Assert.True(plan.Blocked.Single(b => b.Pr.Id == "docs").Policy);
        Assert.Contains("POLICY BLOCKED", Report.Text(plan));
        // billing is structurally clean and policy-ready, but GitHub wants the branch updated first.
        Assert.Contains("GitHub: branch is behind", plan.Steps.Single(s => s.Pr.Id == "billing").Reason);
        Assert.Equal("GitHub: merging is blocked by branch protection",
            Analyzer.NotReadyReason(new PullRequest { Id = "x", HeadSha = "x", MergeStateStatus = "BLOCKED" }));
    }

    [Fact]
    public void Never_recommends_a_merge_the_plan_cannot_deliver()
    {
        var (sha, local) = Providers.Local(_git, "main", ["docs", "billing", "refactor", "deps1"]);
        PullRequest Copy(PullRequest p, string baseRef = "", List<string>? outside = null) =>
            new() { Id = p.Id, HeadSha = p.HeadSha, HeadRef = p.HeadRef, BaseRef = baseRef, OpenOutsideDependencies = outside ?? [] };
        // docs targets another branch; refactor is stacked on billing (fine); deps1 waits on an open PR outside the plan.
        List<PullRequest> prs = [Copy(local[0], "release"), Copy(local[1], "main"), Copy(local[2], "billing"), Copy(local[3], "main", ["#9"])];
        foreach (var pr in prs) Analyzer.Analyze(_git, sha, pr);
        Analyzer.ResolveDependencies(_git, sha, prs);
        var plan = new Planner(new Simulator(_git, MergeStrategy.Merge), "main", sha, prs).Build(8);

        Assert.Equal(["billing", "refactor"], plan.Steps.Select(s => s.Pr.Id));
        var blocked = plan.Blocked.ToDictionary(b => b.Pr.Id);
        Assert.Equal("targets 'release', not 'main'", blocked["docs"].Reason);
        Assert.Equal("depends on #9 (open, not in this plan)", blocked["deps1"].Reason);
        Assert.False(blocked["docs"].Policy);
    }

    [Fact]
    public void Already_merged_branches_are_left_out_not_recommended()
    {
        _git.Run("merge", "-q", "--no-ff", "docs");
        var plan = PlanFor(MergeStrategy.Merge, 8, "docs", "billing");
        Assert.Equal(["billing"], plan.Steps.Select(s => s.Pr.Id));
        Assert.Empty(plan.Blocked);
        Assert.Contains("already merged into main, left out: docs", plan.Notes);
        Assert.Equal("git checkout main && git merge --no-ff billing", Report.NextCommand(plan));
    }

    [Fact]
    public void Detects_a_local_target_behind_its_remote()
    {
        var clone = Directory.CreateTempSubdirectory("pr-opt-clone").FullName;
        try
        {
            Git.Exec(_dir, "git", ["clone", "-q", _dir, clone]);
            var cloned = new Git(clone);
            Assert.Equal(0, Providers.BehindRemote(cloned, "main"));
            Write("new.txt", "upstream\n");
            Commit("upstream work");
            cloned.Run("fetch", "-q", "origin");
            Assert.Equal(1, Providers.BehindRemote(cloned, "main"));
            Assert.Equal(0, Providers.BehindRemote(_git, "main")); // no origin: nothing to compare
        }
        finally { Directory.Delete(clone, true); }
    }

    [Fact]
    public void Complete_plans_beat_cheaper_incomplete_ones()
    {
        var complete = new PlanObjective(Unmerged: 0, Cost: 8.2, TieBreak: 0);
        var cheaper = new PlanObjective(Unmerged: 1, Cost: 2.1, TieBreak: 0);
        Assert.True(complete.CompareTo(cheaper) < 0);
        Assert.True(new PlanObjective(0, 1, 0).CompareTo(new PlanObjective(0, 2, -100)) < 0); // then cost
        Assert.True(new PlanObjective(0, 1, -5).CompareTo(new PlanObjective(0, 1, -3)) < 0);  // then cheap-first order

        // Blocked PRs no longer add a numeric penalty to the reported cost.
        var plan = PlanFor(MergeStrategy.Merge, 8, "billing", "clash");
        Assert.Single(plan.Blocked);
        Assert.Equal(plan.Steps.Sum(s => s.Cost), plan.TotalCost);
    }

    [Fact]
    public void Regenerable_conflicts_never_become_a_conflicted_state()
    {
        Branch("deps3", "main", () => Write("package-lock.json", "{\n\"v\": 4\n}\n"));
        var plan = PlanFor(MergeStrategy.Merge, 8, "deps1", "deps2", "deps3");

        // merge-tree sees the lockfile conflict; the step is planned as REGENERATE, never as clean.
        Assert.Equal(3, plan.Steps.Count);
        Assert.Equal(2, plan.Steps.Count(s => s.RegenerateFiles.SequenceEqual(["package-lock.json"])));
        Assert.Contains("   REGENERATE\n   package-lock.json", Report.Text(plan));

        // No state along the plan carries conflict markers, so later PRs never merge against them.
        for (var c = plan.FinalState!; _git.Try("rev-parse", "--verify", "-q", c + "^").ExitCode == 0 && c != _git.RevParse("main"); c = _git.RevParse(c + "^"))
            Assert.DoesNotContain("<<<<<<<", _git.Run("show", $"{c}:package-lock.json"));
        Assert.Equal(0, _git.Try("fsck", "--no-dangling").ExitCode);
        Assert.Equal("", _git.Run("status", "--porcelain"));
    }

    [Fact]
    public void Repository_configuration_is_optional_validated_and_applied()
    {
        Assert.Same(RepoConfig.Default, RepoConfig.Load(_git)); // no .gitwizz.yml: built-in behaviour

        Write("schema.sql", "v1\n");
        Commit("schema");
        Branch("s1", "main", () => Write("schema.sql", "v2\n"));
        Branch("s2", "main", () => Write("schema.sql", "v3\n"));
        _git.Run("checkout", "-q", "main");
        Assert.Single(PlanFor(MergeStrategy.Merge, 8, "s1", "s2").Blocked); // a normal file: real conflict

        Write(".gitwizz.yml", """
            regenerators:
              - match: schema.sql
                command: make schema
            ignored: ["docs.md"]
            costs:
              regeneration: 2
            """);
        var config = RepoConfig.Load(_git);
        Assert.Equal(1.0, config.Costs.Conflict); // keys left out keep their defaults
        var (sha, prs) = Providers.Local(_git, "main", ["s1", "s2"]);
        foreach (var pr in prs) Analyzer.Analyze(_git, sha, pr);
        var plan = new Planner(new Simulator(_git, MergeStrategy.Merge, config), "main", sha, prs).Build(8);
        Assert.Empty(plan.Blocked);
        var regen = plan.Steps.Single(s => s.RegenerateFiles.Count > 0);
        Assert.Contains("schema.sql (make schema)", regen.Reason);
        Assert.Equal(2, regen.Cost);

        var a = new PullRequest { Id = "a", HeadSha = "1", Files = [new("docs.md", ChangeKind.Modified)] };
        var b = new PullRequest { Id = "b", HeadSha = "2", Files = [new("docs.md", ChangeKind.Modified)] };
        Assert.Equal(0, Analyzer.ConflictWeight(a, b, out _, config: config));

        foreach (var bad in new[] { "colour: red\n", "costs:\n  conflict: -1\n", "regenerators:\n  - match: x\n", "costs: [1\n" })
        {
            Write(".gitwizz.yml", bad);
            Assert.StartsWith("invalid .gitwizz.yml", Assert.Throws<InvalidOperationException>(() => RepoConfig.Load(_git)).Message);
        }
        File.Delete(P(".gitwizz.yml"));
    }

    [Fact]
    public void Learns_conflict_rates_from_past_merges()
    {
        // History on main: two branches edit docs.md; the second merge conflicts and is resolved by hand.
        Branch("h1", "main", () => Write("docs.md", "h1\n"));
        Branch("h2", "main", () => Write("docs.md", "h2\n"));
        _git.Run("checkout", "-q", "main");
        _git.Run("merge", "-q", "--no-ff", "h1");
        Assert.NotEqual(0, _git.Try("merge", "-q", "--no-ff", "h2").ExitCode);
        Write("docs.md", "h1+h2\n");
        Commit("resolve");

        var history = Analyzer.ConflictHistory(_git, "main", 200);
        Assert.Equal(1 / 3.0, history["docs.md"], 3); // 1 conflict, brought in by 2 merges
        Assert.False(history.ContainsKey("billing.cs"));
        Assert.Empty(Analyzer.ConflictHistory(_git, "main", 0));

        var a = new PullRequest { Id = "a", HeadSha = "1", Files = [new("docs.md", ChangeKind.Modified)] };
        var b = new PullRequest { Id = "b", HeadSha = "2", Files = [new("docs.md", ChangeKind.Modified)] };
        Assert.True(Analyzer.ConflictWeight(a, b, out _, history) > Analyzer.ConflictWeight(a, b, out _));
    }

    [Fact]
    public void Infers_semantic_dependencies_and_risks()
    {
        Write("Users.cs", "class UserService\n{\n    public User GetUser(int id) => null;\n}\n");
        Commit("users");
        // api: new type + changed signature; caller needs the new type; legacy still calls the old signature.
        Branch("api", "main", () =>
        {
            Write("Tenant.cs", "public record TenantId(string Value);\n");
            Write("Users.cs", "class UserService\n{\n    public User GetUser(int id, TenantId t) => null;\n}\n");
        });
        Branch("caller", "main", () => Write("Admin.cs", "class Admin\n{\n    void Show(UserService s) => s.GetUser(1, new TenantId(\"x\"));\n}\n"));
        Branch("legacy", "main", () => Write("Report.cs", "class Report\n{\n    void Run(UserService s) => s.GetUser(5);\n}\n"));
        Branch("m1", "main", () => Write("db/migrations/002_add_tenant.sql", "-- a\n"));
        Branch("m2", "main", () => Write("db/migrations/002_add_index.sql", "-- b\n"));
        _git.Run("checkout", "-q", "main");

        var plan = PlanFor(MergeStrategy.Merge, 8, "caller", "legacy", "api", "m1", "m2");
        var prs = plan.Prs.ToDictionary(p => p.Id);
        Assert.Equal(["api"], prs["caller"].Dependencies);
        Assert.Equal("uses TenantId", prs["caller"].DependencyNotes["api"]);
        Assert.Empty(prs["legacy"].Dependencies); // GetUser exists at base: a risk, not a dependency

        var order = plan.Steps.Select(s => s.Pr.Id).ToList();
        Assert.True(order.IndexOf("api") < order.IndexOf("caller"));
        Assert.Contains("legacy uses GetUser(1 args), changed by api", Analyzer.SemanticRisks(prs["api"], prs["legacy"]));
        Assert.Equal(["both add migrations in db/migrations/"], Analyzer.SemanticRisks(prs["m1"], prs["m2"]));
        Assert.Contains(plan.Conflicts, c => c.Weight > 0 && new[] { c.A, c.B }.Order().SequenceEqual(["api", "legacy"]));
        Assert.Contains(plan.Steps, s => s.Reason.Contains("semantic: "));
    }

    [Fact]
    public void Rebase_replays_each_commit()
    {
        // flip edits line2 and reverts it in a second commit: its final tree only changes line9.
        _git.Run("checkout", "-q", "-b", "flip", "main");
        Write("billing.cs", Lines(10).Replace("line2\n", "flip2\n"));
        Commit("flip 1");
        Write("billing.cs", Lines(10).Replace("line9\n", "flip9\n"));
        Commit("flip 2");
        _git.Run("checkout", "-q", "main");

        var (sha, prs) = Providers.Local(_git, "main", ["billing", "flip"]);
        var afterBilling = new Simulator(_git, MergeStrategy.Squash).Simulate(sha, prs[0]).Commit!.Value;
        Assert.Equal(MergeOutcome.Clean, new Simulator(_git, MergeStrategy.Squash).Simulate(afterBilling, prs[1]).Outcome);
        var rebased = new Simulator(_git, MergeStrategy.Rebase).Simulate(afterBilling, prs[1]);
        Assert.Equal(MergeOutcome.Conflict, rebased.Outcome); // the first commit conflicts with billing's line2
        Assert.Contains("billing.cs (commit ", rebased.ConflictFiles[0]);

        // The planner finds the order that rebases cleanly: flip first, then billing.
        var plan = PlanFor(MergeStrategy.Rebase, 8, "billing", "flip");
        Assert.Equal(["flip", "billing"], plan.Steps.Select(s => s.Pr.Id));
        var why = Assert.Single(plan.Explanations);
        Assert.Equal(("clean", "conflict"), (why.AThenB, why.BThenA));
        Assert.Equal("after flip, billing merges cleanly; after billing, flip conflicts", why.Reason);
        Assert.Contains("flip9", _git.Run("show", $"{plan.FinalState}:billing.cs"));
        Assert.Equal(0, _git.Try("fsck", "--no-dangling").ExitCode);
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

        // Levels verify the plan's own states; a failure names the step that introduced it.
        Assert.Equal(plan.FinalState, plan.Steps[^1].State);
        Assert.Equal("passed (true) at 2 states: step", Verify.Plan(_git, plan, "true", "step").Summary);
        Assert.Equal("passed (true) at 1 state: final", Verify.Plan(_git, plan, "true", "final").Summary);
        Assert.Equal("passed (true) at 1 state: critical", Verify.Plan(_git, plan, "true", "critical").Summary); // no high-risk step
        var (ok, summary) = Verify.Plan(_git, plan, "! grep -q billing2 billing.cs", "step");
        Assert.False(ok);
        Assert.StartsWith("FAILED after billing", summary);
        Assert.DoesNotContain("pr-optimizer-", _git.Run("worktree", "list"));
    }

    [Fact]
    public void Reads_blobs_in_one_process_like_git_show()
    {
        var head = _git.RevParse("main");
        File.WriteAllBytes(P("bom file.cs"), [0xEF, 0xBB, 0xBF, .. "class Ä {}\n"u8]);
        File.WriteAllText(P("empty.cs"), "");
        Commit("more");
        var now = _git.RevParse("main");
        Assert.Equal([_git.Run("show", $"{head}:billing.cs") + "\n", "class Ä {}\n", "", "docs\n"],
            _git.ReadBlobs([$"{head}:billing.cs", $"{now}:bom file.cs", $"{now}:empty.cs", $"{now}:docs.md"]));
        Assert.Empty(_git.ReadBlobs([]));
        Assert.Throws<InvalidOperationException>(() => _git.ReadBlobs([$"{now}:missing file.cs"]));
    }

    [Fact]
    public void Target_defaults_to_the_current_branch()
    {
        _git.Run("checkout", "-q", "billing");
        Assert.Equal("billing", Cli.DefaultBranch(_git, out var why));
        Assert.Equal("the current branch", why);
        _git.Run("checkout", "-q", "--detach", "billing");
        Assert.Equal("main", Cli.DefaultBranch(_git, out why));
        Assert.StartsWith("detached HEAD", why);
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
