using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gitwizz;
using Spectre.Console;

namespace Gitwizz.Tests;

public class WorkspaceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("gitwizz-ws").FullName;
    readonly string _tools = Directory.CreateTempSubdirectory("gitwizz-ws-tools").FullName;
    readonly Git _git;
    readonly WorkspaceStore _store;

    public WorkspaceTests()
    {
        _git = new Git(_dir);
        _git.Run("init", "-q", "-b", "main");
        _git.Run("config", "user.email", "t@t"); _git.Run("config", "user.name", "t");
        Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.19m; }\n}\n");
        Write("src/Food.cs", "class Food { }\n");
        Policy();
        Commit("init");
        Branch("feature", () => Write("src/Food.cs", "class Food { decimal Rate() => 0.07m; }\n"));
        Branch("broken", () => Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return BROKEN; }\n}\n"));
        Branch("nobuild", () => Write("src/Extra.cs", "class Extra { }\n"));
        _git.Run("checkout", "-q", "main");
        _store = WorkspaceStore.For(_git);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, true);
        Directory.Delete(_tools, true);
    }

    void Write(string f, string c) { var p = Path.Combine(_dir, f); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, c); }
    void Commit(string m) { _git.Run("add", "-A"); _git.Run("commit", "-q", "-m", m); }
    void Branch(string name, Action change) { _git.Run("checkout", "-q", "-b", name, "main"); change(); Commit(name); }
    void OnBranch(string name, Action change, string message = "change") { _git.Run("checkout", "-q", name); change(); Commit(message); _git.Run("checkout", "-q", "main"); }

    string Log(string name) => Path.Combine(_tools, name);
    int Runs(string name) => File.Exists(Log(name)) ? File.ReadAllLines(Log(name)).Length : 0;

    /// <summary>
    /// build: logs and fails when src/Tax.cs says BROKEN, through a tool outside the repository (an absolute path);
    /// lint: a command whose tool can't be found, so it errors; tests: one suite logging its runs.
    /// </summary>
    void Policy(string buildTool = "build.sh", string suite = "suite.log", bool lint = false) => Write(".gitwizz.yml", $$"""
        gates:
          - { id: build, run: "{{Tool(buildTool)}}" }
          - id: traceability
        {{(lint ? "  - { id: lint, run: gitwizz-no-such-linter }" : "")}}
        tests:
          - { id: unit, run: "echo ran >> '{{Log(suite)}}'", covers: ["src/*"] }
        """);

    string Tool(string name)
    {
        var path = Path.Combine(_tools, name);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, $"#!/bin/sh\necho ran >> '{Log(name + ".log")}'\n! grep -q BROKEN src/Tax.cs\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    void MainPolicy(string buildTool = "build.sh", string suite = "suite.log", bool lint = false)
    {
        Policy(buildTool, suite, lint);
        Commit("policy");
    }

    (string Sha, List<PullRequest> Prs) Load(params string[] branches) => Providers.Local(_git, "main", branches);

    Evaluation Evaluate(string branch, WorkspaceStore? store = null)
    {
        var (sha, prs) = Load(branch);
        var pr = prs.Single();
        var analysis = (store ?? _store).Analyze(_git, sha, pr, "local");
        Analyzer.ResolveDependencies(_git, sha, prs);
        using var wf = new PullRequestWorkflow(_git, "local", "main", sha, pr, new BranchPolicy([]), RepoConfig.Load(_git, sha), store: store ?? _store)
            { Analysis = analysis };
        return wf.Evaluate();
    }

    [Fact]
    public void Analysis_is_reused_only_for_the_same_target_and_head()
    {
        var (sha, prs) = Load("feature", "nobuild");
        Assert.Equal("missing", _store.Analyze(_git, sha, prs[0], "local").Status);
        _store.Analyze(_git, sha, prs[1], "local");

        var (_, again) = Load("feature");
        Assert.Equal("current", _store.Analyze(_git, sha, again[0], "local").Status);
        Assert.Equal(prs[0].Files, again[0].Files);           // restored, not recomputed
        Assert.Equal(prs[0].BaseSha, again[0].BaseSha);

        OnBranch("feature", () => Write("src/Food.cs", "class Food { decimal Rate() => 0.05m; }\n"));
        var (_, moved) = Load("feature", "nobuild");
        var state = _store.Analyze(_git, sha, moved[0], "local");
        Assert.Equal("stale", state.Status);
        Assert.Equal("new commits on the PR", state.Reason);
        Assert.Equal("current", _store.Analyze(_git, sha, moved[1], "local").Status); // the other PR is untouched

        _git.Run("commit", "-q", "--allow-empty", "-m", "main moves");
        var (newSha, _) = Load("feature");
        Assert.Equal("the target branch moved", _store.AnalysisState("local", moved[1], newSha).Reason);
    }

    [Fact]
    public void Gate_results_are_reused_only_for_the_same_state_definition_and_tools()
    {
        var first = Evaluate("feature");
        Assert.True(first.Ready);
        Assert.Equal(1, Runs("build.sh.log"));
        Assert.Equal(1, Runs("suite.log"));

        var second = Evaluate("feature");
        Assert.True(second.Ready);
        Assert.Equal(1, Runs("build.sh.log"));                 // nothing ran again
        Assert.Equal(1, Runs("suite.log"));
        Assert.NotNull(second.Gates.Single(g => g.Id == "build").CachedAt);
        Assert.NotNull(second.Trace!.Runs.Single().CachedAt);
        Assert.Contains("[cached]", Evaluator.Text(second));

        // The tool changed (a new version of the same file): build runs again; the test suite doesn't.
        File.AppendAllText(Tool("build.sh"), "# v2\n");
        Evaluate("feature");
        Assert.Equal(2, Runs("build.sh.log"));
        Assert.Equal(1, Runs("suite.log"));

        // The suite's command changed in the policy: that suite runs again; build's definition is unchanged but the
        // policy commit moved the target, so the merged state is new and build runs again too.
        MainPolicy(suite: "suite2.log");
        Evaluate("feature");
        Assert.Equal(1, Runs("suite2.log"));

        // Another PR never reuses this one's results.
        Evaluate("nobuild");
        Assert.Equal(4, Runs("build.sh.log"));
    }

    [Fact]
    public void A_cached_ready_never_survives_a_relevant_change()
    {
        Assert.True(Evaluate("feature").Ready);
        OnBranch("feature", () => Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return BROKEN; }\n}\n"));
        var e = Evaluate("feature");
        Assert.Equal("blocked", e.Verdict);
        Assert.Null(e.Gates.Single(g => g.Id == "build").CachedAt);
    }

    [Fact]
    public void Errors_are_never_cached()
    {
        MainPolicy(lint: true);
        Assert.Equal("undetermined", Evaluate("feature").Verdict);
        var again = Evaluate("feature");
        Assert.Equal("undetermined", again.Verdict);
        Assert.Null(again.Gates.Single(g => g.Id == "lint").CachedAt);
    }

    [Fact]
    public void Corrupt_or_tampered_workspace_degrades_to_recomputation()
    {
        Assert.Equal("blocked", Evaluate("broken").Verdict);
        Assert.Equal(1, Runs("build.sh.log"));
        // Flip every stored failure to a pass without touching the inputs: the value digest no longer matches.
        foreach (var f in Directory.EnumerateFiles(_store.Dir!, "*.json", SearchOption.AllDirectories))
            File.WriteAllText(f, File.ReadAllText(f).Replace("\"Fail\"", "\"Pass\"").Replace("\"blocked\"", "\"ready\""));
        var e = Evaluate("broken");
        Assert.Equal("blocked", e.Verdict);
        Assert.Equal(2, Runs("build.sh.log"));

        // Garbage everywhere: still recomputed, still blocked.
        foreach (var f in Directory.EnumerateFiles(_store.Dir!, "*.json", SearchOption.AllDirectories)) File.WriteAllText(f, "{ not json");
        Assert.Equal("blocked", Evaluate("broken").Verdict);
        Assert.Equal(3, Runs("build.sh.log"));
    }

    sealed class CountingModel : HttpMessageHandler
    {
        public int Calls;
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var answer = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = """{"findings": []}""" } }),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(Send(request, ct));
    }

    [Fact]
    public void Ai_review_is_reused_only_for_the_same_evidence_model_and_prompt()
    {
        var model = new CountingModel();
        GateResult Review(string modelName)
        {
            var (sha, prs) = Load("feature");
            var pr = prs.Single();
            Analyzer.Analyze(_git, sha, pr);
            var config = RepoConfig.Load(_git, sha);
            config = config with { Review = config.Review with { Endpoint = "http://llm.internal:8000/v1", Model = modelName } };
            using var ctx = new GateContext { Git = _git, Pr = pr, Target = "main", TargetSha = sha, Config = config, Store = _store };
            new MergeGate().Run(ctx, new GateSpec { Id = "merge" });
            return new AiReviewGate(model).Run(ctx, new GateSpec { Id = "review", Type = "ai-review" });
        }
        Assert.Null(Review("qwen-coder").CachedAt);
        Assert.NotNull(Review("qwen-coder").CachedAt);
        Assert.Equal(1, model.Calls);
        Assert.Null(Review("llama-coder").CachedAt);          // another model: asked again
        Assert.Equal(2, model.Calls);
        OnBranch("feature", () => Write("src/Food.cs", "class Food { decimal Rate() => 0.05m; }\n"));
        Assert.Null(Review("qwen-coder").CachedAt);           // another evidence package
        Assert.Equal(3, model.Calls);
    }

    [Fact]
    public void Tool_fingerprint_follows_the_programs_a_command_starts()
    {
        var a = Fingerprint.Tools($"{Tool("x.sh")} --flag && echo done");
        Assert.Equal(a, Fingerprint.Tools($"{Tool("x.sh")} --other && echo other")); // arguments are in the command, not the tools
        Assert.NotEqual(a, Fingerprint.Tools($"{Tool("y.sh")} --flag"));
        Assert.NotEqual(Fingerprint.Tools("scripts/check.sh"), Fingerprint.Tools("gitwizz-no-such-tool"));
        Assert.Equal(Fingerprint.Tools("echo a; true"), Fingerprint.Tools("true | echo b"));
    }

    [Fact]
    public void Status_distinguishes_current_stale_and_missing_and_counts_verdicts()
    {
        RepositoryStatus Status()
        {
            var (sha, prs) = Load("feature", "broken", "nobuild");
            return Repository.Status(_git, _store, "local", "main", new BranchPolicy([]), sha, prs);
        }
        var empty = Status();
        Assert.Equal("missing", empty.Workspace);
        Assert.Equal(3, empty.In("missing").Count);
        Assert.Equal("PR feature has not been evaluated", empty.Next.Text);

        var (sha, prs) = Load("feature", "broken", "nobuild");
        var done = Repository.Refresh(_git, _store, "local", "main", new BranchPolicy([]), sha, prs, allOpen: false);
        Assert.Empty(done);                                    // nothing stale; never-evaluated PRs need --all-open
        done = Repository.Refresh(_git, _store, "local", "main", new BranchPolicy([]), sha, prs, allOpen: true);
        Assert.Equal(3, done.Count);

        var current = Status();
        Assert.Equal("current", current.Workspace);
        Assert.Equal(["feature", "nobuild"], current.In("ready").Select(p => p.Pr.Id));
        Assert.Equal(["broken"], current.In("blocked").Select(p => p.Pr.Id));
        Assert.All(current.Facts, f => Assert.True(f.State.Current));
        Assert.Equal("PR broken is blocked: see what blocks it", current.Next.Text);

        OnBranch("nobuild", () => Write("src/Extra.cs", "class Extra { int X; }\n"));
        var stale = Status();
        Assert.Equal("stale", stale.Workspace);
        Assert.Equal("nobuild", stale.In("stale").Single().Pr.Id);
        Assert.Equal("new commits on the PR", stale.In("stale").Single().Evaluation.Reason);
        Assert.Equal(["feature"], stale.In("ready").Select(p => p.Pr.Id));  // others keep their verdict

        (sha, prs) = Load("feature", "broken", "nobuild");
        Assert.Equal(["nobuild"], Repository.Refresh(_git, _store, "local", "main", new BranchPolicy([]), sha, prs, allOpen: false).Select(e => e.Pr.Id));
        Assert.Equal("current", Status().Workspace);

        MainPolicy(lint: true);                                // policy change: every verdict depends on it
        var policy = Status();
        Assert.Equal(3, policy.In("stale").Count);
        Assert.Contains($"{RepoConfig.FileName} changed", policy.In("stale")[0].Evaluation.Reason);
        Assert.Equal("stale", policy.Facts.Single(f => f.Name == "policy and test topology").State.Status);

        (sha, prs) = Load("feature", "broken", "nobuild");
        Repository.Refresh(_git, _store, "local", "main", new BranchPolicy([]), sha, prs, allOpen: false);
        var after = Status();
        Assert.Equal(["feature", "nobuild"], after.In("undetermined").Select(p => p.Pr.Id)); // lint can't run
        Assert.Equal(["broken"], after.In("blocked").Select(p => p.Pr.Id));                 // a real failure still decides
    }

    [Fact]
    public void Repository_guide_shows_the_state_and_continues_into_the_pr_guide()
    {
        var (sha, prs) = Load("feature", "broken");
        Repository.Refresh(_git, _store, "local", "main", new BranchPolicy([]), sha, prs, allOpen: true);
        OnBranch("feature", () => Write("src/Food.cs", "class Food { decimal Rate() => 0.05m; }\n"));
        (sha, prs) = Load("feature", "broken");

        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
            { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No });
        console.Profile.Width = 200;
        var opened = new List<string>();
        int Open(PrStatus p)
        {
            opened.Add(p.Pr.Id);
            _store.Analyze(_git, sha, p.Pr, "local");
            using var wf = new PullRequestWorkflow(_git, "local", "main", sha, p.Pr, new BranchPolicy([]), RepoConfig.Load(_git, sha), store: _store);
            return new Guide(wf, new GuideOptions("gitwizz guide " + p.Pr.Id), console, console, null).Run();
        }
        RepositoryStatus Status() => Repository.Status(_git, _store, "local", "main", new BranchPolicy([]), sha, prs);

        // Without a terminal: the summary and the recommended command, nothing runs.
        new RepositoryGuide(Status, Open, _ => [], null, console, console, null).Run();
        var text = writer.ToString();
        Assert.Contains("GITWIZZ REPOSITORY GUIDE", text);
        Assert.Contains("Workspace  stale", text);
        Assert.Contains("blocked          1  broken", text);
        Assert.Contains("stale            1  feature", text);
        Assert.Contains("feature                  was ready; new commits on the PR", text);
        Assert.Contains("gitwizz guide feature", text);
        Assert.Empty(opened);

        // Interactive: the stale PR is recommended first; it opens the PR guide, which re-evaluates it.
        Assert.Equal("Review stale PR feature", RepositoryGuide.Menu(Status())[0].Label);
        Assert.DoesNotContain(RepositoryGuide.Menu(Status()), c => c.Label.Contains("Merge", StringComparison.OrdinalIgnoreCase) && c.Action != "plan");
        var script = new GuideTests.Script(
            (Func<IReadOnlyList<RepositoryGuide.Choice>, Func<RepositoryGuide.Choice, string>, RepositoryGuide.Choice>)((o, _) => o[0]),
            (Func<IReadOnlyList<RepositoryGuide.Choice>, Func<RepositoryGuide.Choice, string>, RepositoryGuide.Choice>)((o, _) => o.Single(c => c.Action == "exit")));
        writer.GetStringBuilder().Clear();
        new RepositoryGuide(Status, Open, _ => [], null, console, console, script).Run();
        Assert.Equal(["feature"], opened);
        text = writer.ToString();
        Assert.Contains("last verdict READY is stale (new commits on the PR): evaluating again", text);
        Assert.Contains("VERDICT: READY", text);
        Assert.Equal("current", Status().In("ready").Single().Evaluation.Status); // the workspace was updated
    }

    [Fact]
    public void Workspace_lives_outside_tracked_content_and_clears_safely()
    {
        Evaluate("feature");
        Assert.StartsWith(Path.Combine(_dir, ".git"), _store.Dir!);
        Assert.Equal("", _git.Run("status", "--porcelain"));
        Assert.True(_store.Size().Files > 0);
        Assert.True(_store.Clear().Files > 0);
        Assert.False(Directory.Exists(_store.Dir));
        Assert.Equal((0, 0L), _store.Clear());

        var foreign = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "x");
        Assert.Throws<InvalidOperationException>(() => WorkspaceStore.At(foreign).Clear());
        Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")));
        Directory.Delete(foreign, true);

        // --no-cache: nothing is read or written, and evaluation is the same.
        Assert.True(Evaluate("feature", WorkspaceStore.Off).Ready);
        Assert.False(Directory.Exists(_store.Dir));
    }
}
