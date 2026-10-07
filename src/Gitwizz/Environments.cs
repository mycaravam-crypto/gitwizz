using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gitwizz;

/// <summary>
/// An isolated test environment per pull request, from .gitwizz.yml environment:. Env values are plain settings;
/// Secrets name variables passed through from gitwizz's own environment, never written anywhere and masked in output.
/// </summary>
public record EnvironmentPolicy
{
    public string Provisioner { get; init; } = "compose";
    public string File { get; init; } = "docker-compose.yml";   // in the merged workspace
    public Dictionary<string, string> Env { get; init; } = [];
    public List<string> Secrets { get; init; } = [];
    public List<ReadyCheck> Ready { get; init; } = [];
    public int ReadyTimeout { get; init; } = 180;               // seconds for all readiness checks together
    public int UpTimeout { get; init; } = 900;                  // seconds for building and starting
    public string? Smoke { get; init; }                          // command run once ready
    public bool Keep { get; init; }                              // leave it running after the evaluation
}

/// <summary>A readiness check: an HTTP URL that must answer 2xx, or a command that must exit 0. ${VAR} is expanded.</summary>
public record ReadyCheck
{
    public string? Url { get; init; }
    public string? Command { get; init; }
}

/// <summary>A running environment: its name and the variables tests use to reach it (published ports).</summary>
public record EnvironmentInfo(string Project, Dictionary<string, string> Variables);

/// <summary>Thrown when the provisioning tool itself is missing or unusable: an execution error, not a deployment failure.</summary>
public class ProvisionerUnavailableException(string message) : InvalidOperationException(message);

/// <summary>A way to run per-PR environments. Up and Down must be idempotent for the same project name.</summary>
public interface IEnvironmentProvisioner
{
    /// <summary>Builds and starts the environment from dir. Returns null and the output on a deployment failure.</summary>
    (EnvironmentInfo? Info, string Output) Up(string project, string dir, EnvironmentPolicy policy, IDictionary<string, string> env);

    /// <summary>Recent logs of the environment, for evidence.</summary>
    string Logs(string project);

    /// <summary>Stops and removes everything of the environment; succeeds when there is nothing to remove.</summary>
    void Down(string project);
}

/// <summary>Docker Compose: one compose project per PR, so several PR environments coexist without name collisions.</summary>
public sealed class ComposeProvisioner(Func<string, string[], IDictionary<string, string>?, TimeSpan?, GitResult>? docker = null) : IEnvironmentProvisioner
{
    readonly Func<string, string[], IDictionary<string, string>?, TimeSpan?, GitResult> _docker =
        docker ?? ((dir, args, env, timeout) => Git.Exec(dir, "docker", args, env, timeout: timeout));

    GitResult Compose(string dir, IDictionary<string, string>? env, TimeSpan? timeout, params string[] args)
    {
        var r = _docker(dir, ["compose", .. args], env, timeout);
        if (r.ExitCode == 127 || r.Stderr.Contains("Cannot connect to the Docker daemon") || r.Stderr.Contains("is not a docker command"))
            throw new ProvisionerUnavailableException("docker compose is not available: " + (r.Stderr.Trim() is { Length: > 0 } e ? e.Split('\n')[0] : "docker not installed"));
        return r;
    }

    /// <summary>docker compose up -d --build, then the published host ports as GITWIZZ_PORT_{SERVICE}_{PORT}.</summary>
    public (EnvironmentInfo? Info, string Output) Up(string project, string dir, EnvironmentPolicy policy, IDictionary<string, string> env)
    {
        if (!System.IO.File.Exists(Path.Combine(dir, policy.File)))
            return (null, $"compose file {policy.File} not found in the merged tree");
        var up = Compose(dir, env, TimeSpan.FromSeconds(policy.UpTimeout), "-p", project, "-f", policy.File, "up", "-d", "--build", "--remove-orphans");
        var output = (up.Stdout + "\n" + up.Stderr).Trim();
        if (up.TimedOut) return (null, $"timed out after {policy.UpTimeout} s\n{output}");
        if (up.ExitCode != 0) return (null, output);
        var ps = Compose(dir, env, TimeSpan.FromMinutes(1), "-p", project, "-f", policy.File, "ps", "--format", "json");
        var vars = new Dictionary<string, string> { ["GITWIZZ_ENV_PROJECT"] = project, ["GITWIZZ_ENV_HOST"] = "localhost" };
        foreach (var (k, v) in Ports(ps.Stdout)) vars[k] = v;
        return (new EnvironmentInfo(project, vars), output);
    }

    /// <summary>Published ports from docker compose ps --format json (a JSON array, or one object per line in newer versions).</summary>
    public static Dictionary<string, string> Ports(string psJson)
    {
        var text = psJson.Trim();
        IEnumerable<JsonElement> services = text.StartsWith('[')
            ? JsonDocument.Parse(text).RootElement.EnumerateArray().Select(e => e.Clone()).ToList()
            : text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        var vars = new Dictionary<string, string>();
        foreach (var s in services)
        {
            var name = Regex.Replace((s.TryGetProperty("Service", out var n) ? n.GetString() : null) ?? "service", @"[^A-Za-z0-9]", "_").ToUpperInvariant();
            if (!s.TryGetProperty("Publishers", out var pubs) || pubs.ValueKind != JsonValueKind.Array) continue;
            foreach (var p in pubs.EnumerateArray())
                if (p.TryGetProperty("PublishedPort", out var pp) && pp.GetInt32() > 0 && p.TryGetProperty("TargetPort", out var tp))
                    vars[$"GITWIZZ_PORT_{name}_{tp.GetInt32()}"] = pp.GetInt32().ToString();
        }
        return vars;
    }

    /// <summary>The last 200 log lines of every service.</summary>
    public string Logs(string project) =>
        Compose(".", null, TimeSpan.FromMinutes(1), "-p", project, "logs", "--no-color", "--tail", "200") is var r ? (r.Stdout + r.Stderr).Trim() : "";

    /// <summary>docker compose down -v --remove-orphans: containers, networks and volumes of the project.</summary>
    public void Down(string project) => Compose(".", null, TimeSpan.FromMinutes(5), "-p", project, "down", "-v", "--remove-orphans");
}

/// <summary>Per-PR environment names and the provisioner registry.</summary>
public static class Environments
{
    /// <summary>Deterministic compose project name for a PR: gitwizz-{repo}-{pr}, lower case, so cleanup can always find it.</summary>
    public static string ProjectName(string repoDir, string prId)
    {
        string Clean(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        var name = $"gitwizz-{Clean(Path.GetFileName(Path.GetFullPath(repoDir).TrimEnd(Path.DirectorySeparatorChar)))}-{Clean(prId)}";
        return name.Length <= 60 ? name : name[..60].TrimEnd('-');
    }

    /// <summary>The provisioner for a policy's provisioner: name.</summary>
    public static IEnvironmentProvisioner Create(string name) => name switch
    {
        "compose" => new ComposeProvisioner(),
        _ => throw new InvalidOperationException($"unknown environment provisioner '{name}' (compose)"),
    };

    /// <summary>${NAME} replaced from vars; unknown names stay as they are.</summary>
    public static string Expand(string text, IDictionary<string, string> vars) =>
        Regex.Replace(text, @"\$\{(\w+)\}", m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
}

/// <summary>
/// The environment gate: deploys the merged state into an isolated per-PR environment, waits until it is ready, runs
/// the smoke check, and exposes it to later gates (GITWIZZ_ENV_*, GITWIZZ_PORT_*). Torn down when the evaluation ends.
/// Gates that test the deployment declare needs: [environment], so a failed deployment skips them with its cause.
/// </summary>
public sealed class EnvironmentGate(IEnvironmentProvisioner? provisioner = null, HttpMessageHandler? http = null) : IQualityGate
{
    /// <summary>Provisions, checks readiness and smoke, and registers the teardown.</summary>
    public GateResult Run(GateContext ctx, GateSpec spec)
    {
        var policy = ctx.Config.Environment;
        var p = provisioner ?? Environments.Create(policy.Provisioner);
        var project = Environments.ProjectName(ctx.Git.Run("rev-parse", "--show-toplevel"), ctx.Pr.Id);
        if (policy.Secrets.FirstOrDefault(s => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(s))) is { } missing)
            return GateResult.Of(GateStatus.Error, $"secret {missing} is not set in gitwizz's environment");
        var env = new Dictionary<string, string>(ctx.Env);
        foreach (var (k, v) in policy.Env) env[k] = v;
        foreach (var s in policy.Secrets) env[s] = Environment.GetEnvironmentVariable(s)!;
        var evidence = new List<string> { $"environment {project} ({policy.Provisioner}, {policy.File})" };
        GateResult Failed(string summary, string output)
        {
            string logs;
            try { logs = p.Logs(project); } catch (ProvisionerUnavailableException) { logs = ""; }
            var all = ctx.Redactor.Apply((output + "\n" + logs).Trim());
            if (ctx.EvidenceDir != null)
            {
                var file = Path.Combine(ctx.EvidenceDir, $"{spec.Id}.log");
                System.IO.File.WriteAllText(file, all);
                evidence.Add(Path.GetRelativePath(Directory.GetCurrentDirectory(), file));
            }
            return new GateResult { Status = GateStatus.Fail, Summary = summary, Evidence = evidence, Log = Gates.Tail(all),
                Findings = [new Finding(summary, Rule: "environment", Evidence: Gates.Tail(all, 10))] };
        }

        try
        {
            p.Down(project); // leftovers of an earlier run of this PR
            if (!policy.Keep) ctx.Cleanup.Add(() => p.Down(project));
            var sw = Stopwatch.StartNew();
            var (info, output) = p.Up(project, ctx.Workspace, policy, env);
            if (info is null) return Failed("deployment failed", output);
            evidence.Add($"up after {sw.Elapsed.TotalSeconds:0.0} s");
            foreach (var (k, v) in info.Variables) { ctx.Env[k] = v; env[k] = v; }

            var deadline = DateTime.UtcNow.AddSeconds(policy.ReadyTimeout);
            using var client = http is null ? new HttpClient() : new HttpClient(http, disposeHandler: false);
            client.Timeout = TimeSpan.FromSeconds(10);
            foreach (var check in policy.Ready)
            {
                var what = check.Url is { } u ? Environments.Expand(u, env) : Environments.Expand(check.Command!, env);
                string last = "";
                for (bool ok = false; !ok;)
                {
                    if (check.Url != null)
                    {
                        try { using var r = client.GetAsync(what).GetAwaiter().GetResult(); ok = r.IsSuccessStatusCode; last = $"HTTP {(int)r.StatusCode}"; }
                        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { last = e.Message; }
                    }
                    else
                    {
                        var r = Git.Shell(ctx.Workspace, what, env, TimeSpan.FromSeconds(60));
                        ok = r.ExitCode == 0;
                        last = $"exit code {r.ExitCode}";
                    }
                    if (ok) break;
                    if (DateTime.UtcNow >= deadline) return Failed($"not ready after {policy.ReadyTimeout} s: {what} ({last})", output);
                    Thread.Sleep(TimeSpan.FromSeconds(Math.Min(2, Math.Max(0, (deadline - DateTime.UtcNow).TotalSeconds))));
                }
                evidence.Add($"ready: {what}");
            }

            if (policy.Smoke is { } smoke)
            {
                var r = Git.Shell(ctx.Workspace, smoke, env, TimeSpan.FromMinutes(10));
                if (r.ExitCode != 0) return Failed($"smoke check failed (exit code {r.ExitCode})", r.Stdout + r.Stderr);
                evidence.Add("smoke check passed");
            }
            return new GateResult
            {
                Status = GateStatus.Pass, Evidence = evidence, Tool = policy.Provisioner,
                Summary = $"{project} up" + (policy.Ready.Count > 0 ? $", ready ({policy.Ready.Count} check(s))" : "") + (policy.Smoke != null ? ", smoke passed" : "")
                    + (policy.Keep ? " (kept running)" : ""),
            };
        }
        catch (ProvisionerUnavailableException e) { return GateResult.Of(GateStatus.Error, e.Message) with { Evidence = evidence }; }
    }
}
