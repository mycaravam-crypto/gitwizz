using System.Net;
using System.Text.Json;
using Gitwizz;

namespace Gitwizz.Tests;

public class HealthTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("gitwizz-health").FullName;
    readonly Git _git;

    public HealthTests()
    {
        _git = new Git(_dir);
        _git.Run("init", "-q", "-b", "main");
        _git.Run("config", "user.email", "t@t"); _git.Run("config", "user.name", "t");
        Write("app.cs", "class App { }\n");
        Commit("init");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    void Write(string f, string c) => File.WriteAllText(Path.Combine(_dir, f), c);
    void Commit(string m) { _git.Run("add", "-A"); _git.Run("commit", "-q", "-m", m); }
    void Config(string yaml) { Write(RepoConfig.FileName, yaml); Commit("config"); }

    const string Ai = """
        review:
          endpoint: http://llm.internal:8000/v1
          model: qwen
        """;

    /// <summary>Answers tool commands from a table (missing: not installed); git runs for real. Records every call.</summary>
    sealed class FakeTools(Dictionary<string, GitResult> answers)
    {
        public List<string> Calls { get; } = [];

        public GitResult Exec(string dir, string file, string[] args, TimeSpan timeout)
        {
            var line = string.Join(' ', [file, .. args]);
            Calls.Add(line);
            if (file == "git") return Git.Exec(dir, file, args, timeout: timeout);
            return answers.TryGetValue(line, out var r) ? r : new GitResult(127, "", $"{file}: not found");
        }
    }

    static GitResult Ok(string stdout = "") => new(0, stdout, "");

    static Dictionary<string, GitResult> Everything() => new()
    {
        ["docwizz --version"] = Ok("0.9.1\n"),
        ["dotnet --version"] = Ok("10.0.100\n"),
        ["docker info --format {{.ServerVersion}}"] = Ok("28.1.1\n"),
        ["docker compose version --short"] = Ok("2.36.0\n"),
        ["ado --version"] = Ok("0.6.0\n"),
        ["ado auth status"] = Ok("Logged in to https://tfs.corp.local/tfs/DefaultCollection as Jane Doe\nTLS: system certificate store\n"),
    };

    /// <summary>An OpenAI-compatible model list; Throw: unreachable. Counts requests and keeps the last one.</summary>
    sealed class FakeEndpoint(string models = """{"data":[{"id":"qwen"},{"id":"llama"}]}""", HttpStatusCode status = HttpStatusCode.OK, bool down = false) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public HttpRequestMessage? Last { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Last = request;
            if (down) throw new HttpRequestException("Connection refused");
            return new HttpResponseMessage(status) { Content = new StringContent(models) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(Send(request, ct));
    }

    HealthReport Diagnose(Dictionary<string, GitResult> tools, FakeEndpoint? ai = null, string provider = "local", FakeTools? fake = null)
    {
        fake ??= new FakeTools(tools);
        return Cli.Diagnose(_dir, new() { ["provider"] = provider }, new DependencyHealthService(fake.Exec, ai ?? new FakeEndpoint()));
    }

    DependencyHealth Dep(HealthReport r, string name) => r.Dependencies.Single(d => d.Name == name);

    [Fact]
    public void Everything_available_is_ok_and_arbitrary_commands_are_never_run()
    {
        Config(Ai + """

            gates:
              - id: build
                run: dotnet build
              - id: lint
                run: ./scripts/lint.sh --strict
              - id: docwizz
              - id: environment
              - id: ai-review
            """);
        var tools = new FakeTools(Everything());
        var endpoint = new FakeEndpoint();
        var r = Diagnose([], endpoint, "azure-devops", tools);

        Assert.Equal(OverallHealth.Ok, r.Overall);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("ready", r.BestVerdict);
        Assert.Empty(r.Affected);
        Assert.Equal(DependencyState.Available, Dep(r, "dotnet").State);
        Assert.Equal(DependencyState.Available, Dep(r, "Docker").State);
        Assert.Equal(DependencyState.Available, Dep(r, "AI endpoint").State);
        Assert.Equal(["system-context", "documentation-analysis"], Dep(r, "docwizz").Capabilities);
        Assert.Contains("docwizz", Dep(r, "docwizz").RequiredBy);
        Assert.Single(r.Dependencies, d => d.Name == "docwizz"); // context and gate share one check

        // A script is unknown, not run; no analysis is started; the model list is asked for, nothing is posted.
        var lint = Dep(r, "./scripts/lint.sh");
        Assert.Equal(DependencyState.Unknown, lint.State);
        Assert.Equal(["lint"], lint.RequiredBy);
        Assert.DoesNotContain(tools.Calls, c => c.StartsWith("./scripts") || c.Contains("docwizz check") || c.Contains("docwizz scan"));
        Assert.Equal(1, endpoint.Requests);
        Assert.Equal(HttpMethod.Get, endpoint.Last!.Method);
        Assert.Equal("http://llm.internal:8000/v1/models", endpoint.Last.RequestUri!.ToString());
        Assert.Null(endpoint.Last.Content);

        // Only the server's host is shown: not the user, not the collection path.
        var ado = Dep(r, "Azure DevOps (ado)");
        Assert.Equal(DependencyState.Available, ado.State);
        Assert.Contains("tfs.corp.local", ado.Message);
        Assert.DoesNotContain("Jane", ado.Message);
        Assert.DoesNotContain("DefaultCollection", ado.Message);
    }

    [Fact]
    public void Unreachable_advisory_ai_degrades_but_the_pr_can_still_become_ready()
    {
        Config(Ai + "\ngates:\n  - id: ai-review\n");
        var r = Diagnose(Everything(), new FakeEndpoint(down: true));

        var ai = Dep(r, "AI endpoint");
        Assert.Equal(DependencyState.Unavailable, ai.State);
        Assert.Contains("llm.internal unreachable", ai.Message);
        Assert.NotNull(ai.Remediation);
        Assert.Equal(OverallHealth.Degraded, r.Overall);
        Assert.Equal(5, r.ExitCode);
        Assert.Equal("ready", r.BestVerdict);
        var gate = Assert.Single(r.Affected);
        Assert.Equal(("ai-review", false), (gate.Id, gate.Blocking));
        Assert.Contains("ai-review", Health.Text(r).Split('\n').Single(l => l.StartsWith("Unavailable capabilities")));
    }

    [Fact]
    public void Missing_docwizz_for_a_blocking_gate_puts_the_verdict_at_risk_including_gates_that_need_it()
    {
        Config("""
            gates:
              - id: docs
                type: docwizz
              - id: publish
                run: make docs
                needs: [docs]
            """);
        var tools = Everything();
        tools.Remove("docwizz --version");
        tools["make --version"] = Ok("GNU Make 4.4\n");
        var r = Diagnose(tools);

        Assert.Equal(DependencyState.Unavailable, Dep(r, "docwizz").State);
        Assert.Contains("install docwizz", Dep(r, "docwizz").Remediation);
        Assert.Equal(OverallHealth.VerdictAtRisk, r.Overall);
        Assert.Equal(6, r.ExitCode);
        Assert.Equal("undetermined", r.BestVerdict);
        Assert.Equal(["docs", "publish"], r.Affected.Select(a => a.Id));
        Assert.Contains("needs docs", r.Affected[1].Reason);
        Assert.Contains("UNDETERMINED", Health.Text(r));
    }

    [Fact]
    public void Missing_docker_affects_the_environment_gate()
    {
        Config("gates:\n  - id: environment\n");
        var tools = Everything();
        tools["docker info --format {{.ServerVersion}}"] = new GitResult(1, "", "Cannot connect to the Docker daemon at unix:///var/run/docker.sock\n");
        var r = Diagnose(tools);
        var docker = Dep(r, "Docker");
        Assert.Equal(DependencyState.Unavailable, docker.State);
        Assert.Contains("daemon is not reachable", docker.Message);
        Assert.Equal(["environment"], r.Affected.Select(a => a.Id));
        Assert.Equal(OverallHealth.VerdictAtRisk, r.Overall);

        tools.Remove("docker info --format {{.ServerVersion}}");
        Assert.Contains("not installed", Dep(Diagnose(tools), "Docker").Message);
    }

    [Fact]
    public void Ado_missing_unauthenticated_or_denied_stops_with_a_remediation()
    {
        var missing = Everything();
        missing.Remove("ado --version");
        var r = Diagnose(missing, provider: "azure-devops");
        Assert.Equal(DependencyState.Unavailable, Dep(r, "Azure DevOps (ado)").State);
        Assert.Contains("GITWIZZ_ADO", Dep(r, "Azure DevOps (ado)").Remediation);
        Assert.Equal(OverallHealth.CannotStart, r.Overall);
        Assert.Equal(7, r.ExitCode);
        Assert.Null(r.BestVerdict);

        var loggedOut = Everything();
        loggedOut["ado auth status"] = new GitResult(3, "", "Not logged in.\n");
        var ado = Dep(Diagnose(loggedOut, provider: "azure-devops"), "Azure DevOps (ado)");
        Assert.Equal(DependencyState.Misconfigured, ado.State);
        Assert.Equal("ado auth login <server-url>", ado.Remediation);

        var denied = Everything();
        denied["ado auth status"] = new GitResult(4, "", "permission denied\n");
        Assert.Contains("not permitted", Dep(Diagnose(denied, provider: "azure-devops"), "Azure DevOps (ado)").Message);
    }

    [Fact]
    public void Secrets_never_appear_even_when_a_tool_echoes_them()
    {
        const string pat = "s3cr3t-pat-value-1234";
        Environment.SetEnvironmentVariable("ADO_PAT", pat);
        try
        {
            var tools = Everything();
            tools["ado auth status"] = new GitResult(1, "", $"request failed for token {pat}\n");
            var r = Diagnose(tools, provider: "azure-devops");
            Assert.DoesNotContain(pat, Health.Text(r));
            Assert.DoesNotContain(pat, Health.Json(r));
            Assert.Contains("***", Dep(r, "Azure DevOps (ado)").Message);
        }
        finally { Environment.SetEnvironmentVariable("ADO_PAT", null); }
    }

    [Fact]
    public void A_public_or_invalid_ai_endpoint_is_never_contacted()
    {
        var endpoint = new FakeEndpoint();
        var service = new DependencyHealthService(new FakeTools([]).Exec, endpoint);
        foreach (var url in new[] { "https://api.openai.com/v1", "http://8.8.8.8/v1", "https://user:pw@llm.internal/v1", "not a url" })
        {
            var d = service.AiEndpoint(new ReviewPolicy { Endpoint = url, Model = "gpt" });
            Assert.Equal(DependencyState.Misconfigured, d.State);
            Assert.DoesNotContain("pw", d.Message);
        }
        Assert.Equal(0, endpoint.Requests);
        Assert.Equal(DependencyState.Misconfigured, service.AiEndpoint(new ReviewPolicy { Endpoint = "http://llm.internal/v1" }).State); // no model
        Assert.Equal(0, endpoint.Requests);
    }

    [Fact]
    public void Ai_endpoint_states_from_the_model_list()
    {
        DependencyHealth Check(FakeEndpoint e, string model = "qwen") =>
            new DependencyHealthService(new FakeTools([]).Exec, e).AiEndpoint(new ReviewPolicy { Endpoint = "http://llm.internal:8000/v1", Model = model, ApiKeyEnv = "GITWIZZ_TEST_KEY" });

        Assert.Equal(DependencyState.Available, Check(new FakeEndpoint()).State);
        var missing = Check(new FakeEndpoint(), "mixtral");
        Assert.Equal(DependencyState.Misconfigured, missing.State);
        Assert.Contains("serves qwen, llama", missing.Message);
        var refused = Check(new FakeEndpoint(status: HttpStatusCode.Unauthorized));
        Assert.Equal(DependencyState.Misconfigured, refused.State);
        Assert.Contains("$GITWIZZ_TEST_KEY", refused.Remediation);
        Assert.Equal(DependencyState.Unknown, Check(new FakeEndpoint(status: HttpStatusCode.NotFound)).State);
        Assert.Equal(DependencyState.Unknown, Check(new FakeEndpoint("not json")).State);

        Environment.SetEnvironmentVariable("GITWIZZ_TEST_KEY", "key-123456");
        try
        {
            var e = new FakeEndpoint();
            Check(e);
            Assert.Equal("Bearer key-123456", e.Last!.Headers.Authorization!.ToString());
        }
        finally { Environment.SetEnvironmentVariable("GITWIZZ_TEST_KEY", null); }
    }

    [Fact]
    public void Invalid_configuration_or_no_repository_cannot_start()
    {
        Config("gates:\n  - id: x\n    type: nonsense\n");
        var r = Diagnose(Everything());
        Assert.Equal(DependencyState.Misconfigured, Dep(r, RepoConfig.FileName).State);
        Assert.Contains("unknown type", Dep(r, RepoConfig.FileName).Message);
        Assert.Equal(OverallHealth.CannotStart, r.Overall);

        var empty = Directory.CreateTempSubdirectory("gitwizz-health-norepo").FullName;
        try
        {
            var none = Cli.Diagnose(empty, [], new DependencyHealthService(new FakeTools(Everything()).Exec, new FakeEndpoint()));
            Assert.Equal(DependencyState.Unavailable, Dep(none, "repository").State);
            Assert.Equal(OverallHealth.CannotStart, none.Overall);
        }
        finally { Directory.Delete(empty, true); }
    }

    [Fact]
    public void Unknown_and_known_tools_and_an_invalid_docwizz_yaml()
    {
        Config("gates:\n  - id: test\n");
        Write("docwizz.yaml", "layers: [unclosed\n");
        Commit("broken docwizz.yaml");
        var r = Diagnose(Everything());
        Assert.Equal(DependencyState.Unknown, Dep(r, "test command").State); // detected only in the merged workspace
        var docwizz = Dep(r, "docwizz");
        Assert.Equal(DependencyState.Misconfigured, docwizz.State);
        Assert.Contains("docwizz.yaml", docwizz.Message);
        Assert.Equal(OverallHealth.Degraded, r.Overall); // advisory system context only
        Assert.Equal("ready", r.BestVerdict);

        var git = Dep(r, "git");
        Assert.Equal(DependencyState.Available, git.State);
        Assert.True(git.Essential);
        var current = new DependencyHealthService(new FakeTools([]).Exec).CheckGit(_dir);
        Assert.Equal(DependencyState.Available, current[0].State); // the real git here is recent enough

        var ancient = new DependencyHealthService((d, f, a, t) => f == "git" && a[0] == "--version" ? Ok("git version 2.25.1\n") : Git.Exec(d, f, a, timeout: t)).CheckGit(_dir);
        Assert.Equal(DependencyState.Misconfigured, ancient[0].State);
    }

    [Fact]
    public void Json_is_stable_and_machine_readable()
    {
        Config(Ai + "\ngates:\n  - id: ai-review\n");
        var json = JsonDocument.Parse(Health.Json(Diagnose(Everything(), new FakeEndpoint(down: true)))).RootElement;
        Assert.Equal("gitwizz.health/v1", json.GetProperty("schema").GetString());
        Assert.Equal("DEGRADED", json.GetProperty("overall").GetString());
        Assert.Equal(5, json.GetProperty("exitCode").GetInt32());
        Assert.Equal("ready", json.GetProperty("bestVerdict").GetString());
        var ai = json.GetProperty("dependencies").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "AI endpoint");
        Assert.Equal("UNAVAILABLE", ai.GetProperty("state").GetString());
        Assert.Equal(["ai-review"], ai.GetProperty("requiredBy").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["ai-review"], json.GetProperty("unavailableCapabilities").EnumerateArray().Select(e => e.GetString()));
        var gate = json.GetProperty("affectedGates").EnumerateArray().Single();
        Assert.False(gate.GetProperty("blocking").GetBoolean());
    }

    [Fact]
    public void Doctor_takes_its_options()
    {
        var o = Cli.Parse(["-f", "json", "--profile", "strict", "--provider", "ado"], "doctor");
        Assert.Equal(("json", "strict", "ado"), (o["format"], o["profile"], o["provider"]));
        Assert.Throws<ArgumentException>(() => Cli.Parse(["--all-open"], "doctor"));
        Assert.Throws<ArgumentException>(() => Cli.Diagnose(_dir, new() { ["provider"] = "gitlab" }, new DependencyHealthService()));
    }

    [Fact]
    public void Evaluation_with_an_erroring_advisory_ai_review_is_ready()
    {
        Config(Ai + "\ngates:\n  - id: ai-review\n");
        _git.Run("checkout", "-q", "-b", "feature");
        Write("docs.md", "docs\n");
        Commit("docs");
        _git.Run("checkout", "-q", "main");
        var (sha, prs) = Providers.Local(_git, "main", ["feature"]);
        Analyzer.Analyze(_git, sha, prs[0]);
        Gates.Override = kind => kind == "ai-review" ? new Erroring() : null;
        try
        {
            using var ctx = new GateContext { Git = _git, Pr = prs[0], Target = "main", TargetSha = sha, Config = RepoConfig.Load(_git, sha) };
            var e = Evaluator.Run(ctx);
            Assert.Equal(GateStatus.Error, e.Gates.Single(g => g.Id == "ai-review").Status);
            Assert.Equal("ready", e.Verdict);
        }
        finally { Gates.Override = null; }
    }

    // --- Guide preflight, phase A ----------------------------------------------------------------------------------

    static (Spectre.Console.IAnsiConsole Console, StringWriter Text) Capture()
    {
        var writer = new StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Out = new Spectre.Console.AnsiConsoleOutput(writer), Ansi = Spectre.Console.AnsiSupport.No, ColorSystem = Spectre.Console.ColorSystemSupport.NoColors,
            Interactive = Spectre.Console.InteractionSupport.No, Enrichment = new Spectre.Console.ProfileEnrichment { UseDefaultEnrichers = false },
        });
        console.Profile.Width = 200;
        return (console, writer);
    }

    (int? Code, string Output) Preflight(FakeTools tools, string provider = "azure-devops", IGuidePrompts? prompts = null, string? dir = null)
    {
        var (console, text) = Capture();
        var code = Cli.GuidePreflight(dir ?? _dir, _ => provider, new DependencyHealthService(tools.Exec, new FakeEndpoint()), console, console, prompts);
        return (code, text.ToString());
    }

    [Fact]
    public void Guide_preflight_shows_the_environment_and_goes_on()
    {
        var tools = new FakeTools(Everything());
        var (code, output) = Preflight(tools);
        Assert.Null(code);
        Assert.Contains("Environment", output);
        Assert.Contains("✓ git", output);
        Assert.Contains("✓ repository", output);
        Assert.Contains("✓ Azure DevOps (ado)  authenticated to tfs.corp.local", output);
        Assert.DoesNotContain("CANNOT START", output);
        // Phase A only: nothing the gates rely on is checked yet.
        Assert.DoesNotContain(tools.Calls, c => c.StartsWith("docwizz") || c.StartsWith("docker") || c.StartsWith("dotnet"));
    }

    [Fact]
    public void Guide_preflight_without_a_terminal_stops_with_the_fix_and_never_retries()
    {
        var loggedOut = Everything();
        loggedOut["ado auth status"] = new GitResult(3, "", "Not logged in.\n");
        var tools = new FakeTools(loggedOut);
        var (code, output) = Preflight(tools);
        Assert.Equal(7, code);
        Assert.Contains("✗ Azure DevOps (ado)  not logged in to Azure DevOps", output);
        Assert.Contains("fix: ado auth login <server-url>", output);
        Assert.Contains("CANNOT START", output);
        Assert.Single(tools.Calls, c => c == "ado auth status");

        var missing = Everything();
        missing.Remove("ado --version");
        (code, output) = Preflight(new FakeTools(missing));
        Assert.Equal(7, code);
        Assert.Contains("GITWIZZ_ADO", output);
    }

    [Fact]
    public void Guide_preflight_on_a_terminal_offers_retry_and_exit()
    {
        var answers = Everything();
        answers["ado auth status"] = new GitResult(3, "", "Not logged in.\n");
        var tools = new FakeTools(answers);
        // The developer logs in, then retries: the second check passes and the guide goes on.
        Func<IReadOnlyList<PreflightAction>, Func<PreflightAction, string>, PreflightAction> loginThenRetry = (options, label) =>
        {
            Assert.Equal(["Retry", "Exit"], options.Select(label));
            answers["ado auth status"] = Ok("Logged in to https://tfs.corp.local/tfs as Jane\n");
            return PreflightAction.Retry;
        };
        var (code, output) = Preflight(tools, prompts: new GuideTests.Script(loginThenRetry));
        Assert.Null(code);
        Assert.Equal(2, tools.Calls.Count(c => c == "ado auth status"));
        Assert.Contains("not logged in", output);
        Assert.Contains("authenticated to tfs.corp.local", output);

        answers["ado auth status"] = new GitResult(3, "", "Not logged in.\n");
        var again = new FakeTools(answers);
        (code, _) = Preflight(again, prompts: new GuideTests.Script(PreflightAction.Exit));
        Assert.Equal(7, code);
        Assert.Single(again.Calls, c => c == "ado auth status");
    }

    [Fact]
    public void Guide_preflight_outside_a_repository_stops_before_choosing_a_provider()
    {
        var empty = Directory.CreateTempSubdirectory("gitwizz-health-norepo").FullName;
        try
        {
            var (console, text) = Capture();
            var asked = false;
            var code = Cli.GuidePreflight(empty, _ => { asked = true; return "azure-devops"; },
                new DependencyHealthService(new FakeTools(Everything()).Exec), console, console, null);
            Assert.Equal(7, code);
            Assert.False(asked);
            Assert.Contains("not a git repository", text.ToString());
            Assert.Contains("--repo", text.ToString());
        }
        finally { Directory.Delete(empty, true); }
    }

    [Fact]
    public void Guide_preflight_masks_secrets_a_provider_tool_echoes()
    {
        const string pat = "s3cr3t-pat-in-preflight-99";
        Environment.SetEnvironmentVariable("SYSTEM_ACCESSTOKEN", pat);
        try
        {
            var tools = Everything();
            tools["ado auth status"] = new GitResult(1, "", $"request failed for token {pat}\n");
            var (code, output) = Preflight(new FakeTools(tools));
            Assert.Equal(7, code);
            Assert.DoesNotContain(pat, output);
            Assert.Contains("***", output);
        }
        finally { Environment.SetEnvironmentVariable("SYSTEM_ACCESSTOKEN", null); }
    }

    [Fact]
    public void Guide_stops_before_loading_anything_when_the_provider_is_unusable()
    {
        // With a missing ado, loading the PR would throw; the preflight stops first with exit code 7.
        Environment.SetEnvironmentVariable("GITWIZZ_ADO", Path.Combine(_dir, "no-such-ado"));
        try
        {
            var (err, _) = Capture();
            Assert.Equal(7, Cli.Guide("42", new() { ["repo"] = _dir, ["provider"] = "azure-devops", ["yes"] = "true" }, err));
            Assert.Equal(7, Cli.RepositoryGuide(new() { ["repo"] = _dir, ["provider"] = "azure-devops", ["yes"] = "true" }, err));
        }
        finally { Environment.SetEnvironmentVariable("GITWIZZ_ADO", null); }
    }

    sealed class Erroring : IQualityGate
    {
        public GateResult Run(GateContext ctx, GateSpec spec) => GateResult.Of(GateStatus.Error, "AI review endpoint llm.internal unreachable");
    }
}
