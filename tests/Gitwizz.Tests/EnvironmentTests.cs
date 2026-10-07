using Gitwizz;

namespace Gitwizz.Tests;

public class EnvironmentTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("gitwizz-env").FullName;
    readonly Git _git;

    public EnvironmentTests()
    {
        _git = new Git(_dir);
        _git.Run("init", "-q", "-b", "main");
        _git.Run("config", "user.email", "t@t"); _git.Run("config", "user.name", "t");
        File.WriteAllText(Path.Combine(_dir, "docker-compose.yml"), "services:\n  web:\n    image: nginx\n    ports: [\"80\"]\n");
        File.WriteAllText(Path.Combine(_dir, "app.txt"), "v1\n");
        _git.Run("add", "-A"); _git.Run("commit", "-q", "-m", "init");
        _git.Run("checkout", "-q", "-b", "feature");
        File.WriteAllText(Path.Combine(_dir, "app.txt"), "v2\n");
        _git.Run("commit", "-q", "-am", "v2");
        _git.Run("checkout", "-q", "main");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>Records calls; Up answers with the given ports, or fails, or reports docker missing.</summary>
    sealed class FakeProvisioner(string mode = "ok") : IEnvironmentProvisioner
    {
        public List<string> Calls { get; } = [];
        public (EnvironmentInfo? Info, string Output) Up(string project, string dir, EnvironmentPolicy policy, IDictionary<string, string> env)
        {
            Calls.Add($"up {project}");
            if (mode == "missing") throw new ProvisionerUnavailableException("docker compose is not available: docker not installed");
            if (mode == "fail") return (null, $"web: build failed, token {env["DB_PASSWORD"]}");
            Assert.True(File.Exists(Path.Combine(dir, "docker-compose.yml")));
            Assert.Equal("test", env["APP_MODE"]);
            return (new(project, new() { ["GITWIZZ_ENV_PROJECT"] = project, ["GITWIZZ_PORT_WEB_80"] = "32768" }), "started");
        }
        public string Logs(string project) => "web | listening";
        public void Down(string project) => Calls.Add($"down {project}");
    }

    Evaluation Evaluate(FakeProvisioner p, string ready = "test \"$GITWIZZ_PORT_WEB_80\" = 32768", int timeout = 30)
    {
        File.WriteAllText(Path.Combine(_dir, ".gitwizz.yml"), $$"""
            secrets: []
            environment:
              env: { APP_MODE: test }
              secrets: [DB_PASSWORD]
              ready:
                - command: '{{ready}}'
              ready_timeout: {{timeout}}
              smoke: test "$APP_MODE" = test
            gates:
              - id: environment
              - id: e2e
                run: test "$GITWIZZ_PORT_WEB_80" = 32768 && test -n "$GITWIZZ_ENV_PROJECT"
                needs: [environment]
            profiles:
              fast: []
              full: [environment, e2e]
            """);
        _git.Run("add", "-A"); _git.Run("commit", "-q", "--allow-empty", "-m", "policy");
        var (sha, prs) = Providers.Local(_git, "main", ["feature"]);
        var pr = prs.Single();
        Analyzer.Analyze(_git, sha, pr);
        var config = RepoConfig.Load(_git, sha);
        var ctx = new GateContext
        {
            Git = _git, Pr = pr, Target = "main", TargetSha = sha, Config = config,
            Redactor = new Redactor(config.Secrets.Concat(config.Environment.Secrets)),
        };
        try
        {
            Gates.Override = kind => kind == "environment" ? new EnvironmentGate(p) : null;
            return Evaluator.Run(ctx, "full");
        }
        finally { Gates.Override = null; ctx.Dispose(); }
    }

    [Fact]
    public void Deploys_waits_for_readiness_runs_smoke_and_exposes_the_environment()
    {
        Environment.SetEnvironmentVariable("DB_PASSWORD", "hunter2-secret");
        try
        {
            var p = new FakeProvisioner();
            var e = Evaluate(p);
            var env = e.Gates.Single(g => g.Id == "environment");
            Assert.Equal(GateStatus.Pass, env.Status);
            Assert.Contains("ready (1 check(s)), smoke passed", env.Summary);
            Assert.Equal(GateStatus.Pass, e.Gates.Single(g => g.Id == "e2e").Status); // saw the published port
            var project = Environments.ProjectName(_dir, "feature");
            Assert.Equal([$"down {project}", $"up {project}", $"down {project}"], p.Calls); // clean start, guaranteed teardown
            Assert.True(e.Ready);
        }
        finally { Environment.SetEnvironmentVariable("DB_PASSWORD", null); }
    }

    [Fact]
    public void A_failed_deployment_blocks_dependent_tests_with_its_cause_and_hides_secrets()
    {
        Environment.SetEnvironmentVariable("DB_PASSWORD", "hunter2-secret");
        try
        {
            var p = new FakeProvisioner("fail");
            var e = Evaluate(p);
            var env = e.Gates.Single(g => g.Id == "environment");
            Assert.Equal(GateStatus.Fail, env.Status);
            Assert.Equal("deployment failed", env.Summary);
            Assert.Contains("web | listening", env.Log);   // service logs as evidence
            Assert.DoesNotContain("hunter2-secret", Evaluator.Json(e));
            var e2e = e.Gates.Single(g => g.Id == "e2e");
            Assert.Equal(GateStatus.Skipped, e2e.Status);
            Assert.Contains("needs environment, which did not pass (fail: deployment failed)", e2e.Summary);
            Assert.True(e2e.BlocksMerge);
            Assert.Equal("down", p.Calls[^1].Split(' ')[0]); // torn down even after a failure
        }
        finally { Environment.SetEnvironmentVariable("DB_PASSWORD", null); }
    }

    [Fact]
    public void Readiness_has_a_deadline()
    {
        Environment.SetEnvironmentVariable("DB_PASSWORD", "x-secret");
        try
        {
            var env = Evaluate(new FakeProvisioner(), ready: "false", timeout: 1).Gates.Single(g => g.Id == "environment");
            Assert.Equal(GateStatus.Fail, env.Status);
            Assert.StartsWith("not ready after 1 s: false (exit code 1)", env.Summary);
        }
        finally { Environment.SetEnvironmentVariable("DB_PASSWORD", null); }
    }

    [Fact]
    public void Missing_docker_or_secret_is_an_execution_error_not_a_failure()
    {
        Environment.SetEnvironmentVariable("DB_PASSWORD", "x-secret");
        try
        {
            var env = Evaluate(new FakeProvisioner("missing")).Gates.Single(g => g.Id == "environment");
            Assert.Equal(GateStatus.Error, env.Status);
            Assert.Contains("docker compose is not available", env.Summary);
        }
        finally { Environment.SetEnvironmentVariable("DB_PASSWORD", null); }
        var e = Evaluate(new FakeProvisioner());
        Assert.Equal("secret DB_PASSWORD is not set in gitwizz's environment", e.Gates.Single(g => g.Id == "environment").Summary);
        Assert.Equal("undetermined", e.Verdict);
    }

    [Fact]
    public void Compose_provisioner_isolates_projects_and_reads_published_ports()
    {
        var calls = new List<string>();
        var compose = new ComposeProvisioner((dir, args, env, timeout) =>
        {
            calls.Add(string.Join(' ', args));
            return args.Contains("ps")
                ? new GitResult(0, """{"Service":"web","Publishers":[{"TargetPort":80,"PublishedPort":49153},{"TargetPort":443,"PublishedPort":0}]}""" + "\n" +
                                   """{"Service":"db-main","Publishers":[{"TargetPort":5432,"PublishedPort":49154}]}""", "")
                : new GitResult(0, "", "");
        });
        var (info, _) = compose.Up("gitwizz-shop-42", _dir, new EnvironmentPolicy(), new Dictionary<string, string>());
        Assert.Equal("compose -p gitwizz-shop-42 -f docker-compose.yml up -d --build --remove-orphans", calls[0]);
        Assert.Equal("49153", info!.Variables["GITWIZZ_PORT_WEB_80"]);
        Assert.Equal("49154", info.Variables["GITWIZZ_PORT_DB_MAIN_5432"]);
        Assert.False(info.Variables.ContainsKey("GITWIZZ_PORT_WEB_443"));
        compose.Down("gitwizz-shop-42");
        Assert.Equal("compose -p gitwizz-shop-42 down -v --remove-orphans", calls[^1]);

        Assert.Equal("49153", ComposeProvisioner.Ports("""[{"Service":"web","Publishers":[{"TargetPort":80,"PublishedPort":49153}]}]""")["GITWIZZ_PORT_WEB_80"]);
        var (none, why) = compose.Up("p", _dir, new EnvironmentPolicy { File = "missing.yml" }, new Dictionary<string, string>());
        Assert.Null(none);
        Assert.Contains("missing.yml not found", why);

        var gone = new ComposeProvisioner((_, _, _, _) => new GitResult(1, "", "Cannot connect to the Docker daemon at unix:///var/run/docker.sock"));
        Assert.Throws<ProvisionerUnavailableException>(() => gone.Down("p"));
    }

    [Fact]
    public void Project_names_are_deterministic_and_distinct_per_pr()
    {
        Assert.Equal("gitwizz-my-shop-42", Environments.ProjectName("/src/My Shop", "#42"));
        Assert.Equal("gitwizz-my-shop-feature-x", Environments.ProjectName("/src/My Shop/", "feature/X"));
        Assert.NotEqual(Environments.ProjectName("/r", "#1"), Environments.ProjectName("/r", "#2"));
        Assert.Equal("http://localhost:49153/health", Environments.Expand("http://localhost:${GITWIZZ_PORT_WEB_80}/health", new Dictionary<string, string> { ["GITWIZZ_PORT_WEB_80"] = "49153" }));
        Assert.Contains("exactly one of url or command", Assert.Throws<InvalidOperationException>(() => RepoConfig.Parse("environment: { ready: [ { } ] }")).Message);
    }
}
