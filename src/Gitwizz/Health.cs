using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace Gitwizz;

/// <summary>
/// What a dependency check found. Unknown: availability can't be determined safely without running something
/// preflight must not run (an arbitrary script) or without data it doesn't have (a PR to read).
/// </summary>
public enum DependencyState { Available, Unavailable, Misconfigured, Unknown }

/// <summary>
/// The environment as a whole: Ok; Degraded (only advisory gates or context affected, the verdict is unaffected);
/// VerdictAtRisk (a blocking gate can't run, so the best reachable verdict is undetermined); CannotStart (git, the
/// repository, the provider or .gitwizz.yml is unusable, so no PR can be evaluated).
/// </summary>
public enum OverallHealth { Ok, Degraded, VerdictAtRisk, CannotStart }

/// <summary>What the guide offers when its preflight finds git, the repository or the provider down.</summary>
public enum PreflightAction { Retry, Exit }

/// <summary>One external dependency: its state, the capabilities and gates relying on it, and how to fix it.</summary>
public record DependencyHealth
{
    public required string Name { get; init; }       // display name: "git", "Azure DevOps (ado)", "docwizz", "AI endpoint", "Docker", a tool
    public required DependencyState State { get; init; }
    public required string Message { get; init; }
    public string? Version { get; init; }
    public string? Remediation { get; init; }
    public List<string> Capabilities { get; init; } = [];
    public List<string> RequiredBy { get; init; } = [];  // gate ids
    /// <summary>Without it nothing can be evaluated: git, the repository, the provider, the configuration.</summary>
    public bool Essential { get; init; }

    public bool Down => State is DependencyState.Unavailable or DependencyState.Misconfigured;
}

/// <summary>A gate that cannot run, or is skipped because a gate it needs cannot run.</summary>
public record GateImpact(string Id, string Type, bool Blocking, string Reason);

/// <summary>Dependency health and its impact on the gates: what gitwizz doctor prints and the guide's preflight uses.</summary>
public record HealthReport
{
    public List<DependencyHealth> Dependencies { get; init; } = [];
    public List<GateImpact> Affected { get; init; } = [];
    /// <summary>The best verdict an evaluation can still reach: ready or undetermined; null when evaluation can't start.</summary>
    public string? BestVerdict { get; init; }
    public string Provider { get; init; } = "local";
    public string? PolicySource { get; init; }
    public List<string> Profiles { get; init; } = [];

    public OverallHealth Overall =>
        Dependencies.Any(d => d.Essential && d.Down) || BestVerdict == null ? OverallHealth.CannotStart
        : BestVerdict != "ready" ? OverallHealth.VerdictAtRisk
        : Dependencies.Any(d => d.Down) ? OverallHealth.Degraded
        : OverallHealth.Ok;

    /// <summary>0 ok, 5 degraded, 6 verdict at risk, 7 cannot start (1-4 keep their meaning: error, usage, blocked, undetermined).</summary>
    public int ExitCode => Overall switch { OverallHealth.Ok => 0, OverallHealth.Degraded => 5, OverallHealth.VerdictAtRisk => 6, _ => 7 };
}

/// <summary>
/// Checks what gitwizz depends on before any expensive step, without side effects: version and status commands of known
/// tools, a read-only provider call, the AI endpoint's model list. Never runs a configured command or repository
/// script, never sends repository content, never contacts an AI endpoint that the self-hosting policy refuses, and
/// never prints a secret. Phase A (git, provider) needs nothing loaded; phase B needs the configuration.
/// </summary>
public sealed class DependencyHealthService(
    Func<string, string, string[], TimeSpan, GitResult>? exec = null, HttpMessageHandler? http = null)
{
    readonly Func<string, string, string[], TimeSpan, GitResult> _exec = exec ?? ((dir, file, args, timeout) => Git.Exec(dir, file, args, timeout: timeout));
    static readonly TimeSpan Quick = TimeSpan.FromSeconds(15);

    public const string RepositoryAccess = "repository-access", ProviderContext = "provider-context", Configuration = "configuration",
        DocumentationAnalysis = "documentation-analysis", SystemContext = "system-context", TestExecution = "test-execution",
        EnvironmentProvisioning = "environment-provisioning", AiReview = "ai-review";

    GitResult Probe(string dir, string file, params string[] args) => _exec(dir, file, args, Quick);

    static string FirstLine(string text) => text.Trim().Split('\n')[0].Trim();

    // --- Phase A ------------------------------------------------------------------------------------------------------

    /// <summary>git itself (installed, recent enough for merge-tree --write-tree) and the repository.</summary>
    /// <param name="dir">The directory that should be (inside) the repository.</param>
    public List<DependencyHealth> CheckGit(string dir)
    {
        var v = Probe(dir, "git", "--version");
        if (v.ExitCode != 0)
            return [new() { Name = "git", State = DependencyState.Unavailable, Essential = true, Capabilities = [RepositoryAccess],
                Message = v.ExitCode is 126 or 127 ? "git is not installed" : $"git --version failed: {FirstLine(v.Stderr)}",
                Remediation = "install git 2.38 or later" }];
        var version = FirstLine(v.Stdout).Replace("git version ", "");
        var tooOld = ParseVersion(version) is { } pv && pv < new Version(2, 38);
        var git = new DependencyHealth
        {
            Name = "git", Version = version, Essential = true, Capabilities = [RepositoryAccess],
            State = tooOld ? DependencyState.Misconfigured : DependencyState.Available,
            Message = tooOld ? $"git {version} is too old: merge simulation needs merge-tree --write-tree (2.38)" : $"git {version}",
            Remediation = tooOld ? "upgrade git to 2.38 or later (2.40 for --strategy rebase)" : null,
        };
        var repo = Probe(dir, "git", "rev-parse", "--show-toplevel");
        var repository = repo.ExitCode == 0
            ? new DependencyHealth { Name = "repository", State = DependencyState.Available, Essential = true, Capabilities = [RepositoryAccess], Message = FirstLine(repo.Stdout) }
            : new DependencyHealth { Name = "repository", State = DependencyState.Unavailable, Essential = true, Capabilities = [RepositoryAccess],
                Message = $"not a git repository: {dir}", Remediation = "run inside a repository or pass --repo <dir>" };
        return [git, repository];
    }

    /// <summary>
    /// The provider's CLI and its login, by a read-only status call: ado auth status (which asks the server, so it also
    /// proves the server is reachable and the token valid) or gh auth status. Reading PRs isn't proven without a PR.
    /// </summary>
    public DependencyHealth CheckProvider(string provider, string dir) => provider switch
    {
        "azure-devops" => Ado(dir),
        "github" => GitHub(dir),
        _ => new() { Name = "provider (local)", State = DependencyState.Available, Essential = true, Capabilities = [ProviderContext],
            Message = "local branches: no provider service needed" },
    };

    DependencyHealth Ado(string dir)
    {
        var exe = Environment.GetEnvironmentVariable("GITWIZZ_ADO") ?? "ado";
        const string name = "Azure DevOps (ado)";
        var v = Probe(dir, exe, "--version");
        if (v.ExitCode is 126 or 127)
            return new() { Name = name, State = DependencyState.Unavailable, Essential = true, Capabilities = [ProviderContext],
                Message = $"'{exe}' not found", Remediation = "install ado from github.com/mycaravam-crypto/ado-devops, or set GITWIZZ_ADO" };
        var version = v.ExitCode == 0 ? FirstLine(v.Stdout) : null;
        var s = _exec(dir, exe, ["auth", "status"], TimeSpan.FromSeconds(30));
        DependencyHealth Result(DependencyState state, string message, string? fix = null) => new()
        {
            Name = name, Version = version, State = state, Essential = true, Capabilities = [ProviderContext], Message = message, Remediation = fix,
        };
        return s.ExitCode switch
        {
            0 => Result(DependencyState.Available, $"authenticated{ServerOf(s.Stdout)}; PR read access is verified when the PR loads"),
            3 => Result(DependencyState.Misconfigured, "not logged in to Azure DevOps", "ado auth login <server-url>"),
            4 => Result(DependencyState.Misconfigured, "the token is not permitted to read the server", "create a PAT with Code (read) and Work Items (read), then: ado auth login <server-url>"),
            124 => Result(DependencyState.Unavailable, "the server did not answer within 30 s", "check the network or proxy: ado auth status"),
            _ => Result(DependencyState.Unavailable, $"ado auth status failed (exit {s.ExitCode}): {FirstLine(s.Stderr)}", "run ado auth status for details"),
        };
    }

    /// <summary>" to host" from ado auth status' "Logged in to https://host/... as user": the host only, no user or path.</summary>
    static string ServerOf(string stdout) =>
        stdout.Split(' ').FirstOrDefault(w => w.StartsWith("http")) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) ? $" to {u.Host}" : "";

    DependencyHealth GitHub(string dir)
    {
        const string name = "GitHub (gh)";
        var v = Probe(dir, "gh", "--version");
        if (v.ExitCode is 126 or 127)
            return new() { Name = name, State = DependencyState.Unavailable, Essential = true, Capabilities = [ProviderContext],
                Message = "gh not found", Remediation = "install the GitHub CLI, or use --provider local" };
        var version = v.ExitCode == 0 ? FirstLine(v.Stdout).Replace("gh version ", "").Split(' ')[0] : null;
        var s = _exec(dir, "gh", ["auth", "status", "--hostname", "github.com"], TimeSpan.FromSeconds(30));
        return s.ExitCode == 0
            ? new() { Name = name, Version = version, State = DependencyState.Available, Essential = true, Capabilities = [ProviderContext], Message = "authenticated to github.com" }
            : new() { Name = name, Version = version, State = DependencyState.Misconfigured, Essential = true, Capabilities = [ProviderContext],
                Message = "not logged in to github.com", Remediation = "gh auth login" };
    }

    // --- Phase B ------------------------------------------------------------------------------------------------------

    /// <summary>A .gitwizz.yml that can't be read: nothing can be evaluated until it is fixed.</summary>
    public static DependencyHealth InvalidConfiguration(string message) => new()
    {
        Name = RepoConfig.FileName, State = DependencyState.Misconfigured, Essential = true, Capabilities = [Configuration], Message = message,
        Remediation = "fix the file or remove it to use the defaults; see the README section 'Configuration'",
    };

    /// <summary>
    /// The dependencies of the given gates and of the configured context: tools of workspace gates and test suites
    /// (version only, for well-known tools), docwizz, Docker for the environment gate, the AI endpoint.
    /// </summary>
    /// <param name="config">The policy: test suites, the docwizz context and the AI review settings.</param>
    /// <param name="gates">Every gate an evaluation may run.</param>
    /// <param name="git">The repository; tools are asked for their version in its directory.</param>
    /// <param name="commit">Where docwizz.yaml is read; null: the working tree.</param>
    public List<DependencyHealth> ForGates(RepoConfig config, IReadOnlyCollection<GateSpec> gates, Git git, string? commit)
    {
        var deps = new List<DependencyHealth>();
        var dir = git.RepoDir;
        var tools = new Dictionary<string, (List<string> Gates, HashSet<string> Caps)>();
        void Tool(string command, string? gate, string capability)
        {
            var tool = command.TrimStart().Split(' ', 2)[0];
            if (tool == "") return;
            if (!tools.TryGetValue(tool, out var t)) tools[tool] = t = ([], []);
            if (gate != null && !t.Gates.Contains(gate)) t.Gates.Add(gate);
            t.Caps.Add(capability);
        }

        foreach (var g in gates)
            switch (g.Kind)
            {
                case "docwizz": Tool(g.Run ?? "docwizz", g.Id, DocumentationAnalysis); break;
                case "build" or "test" or "command" when g.Run is { Length: > 0 } run: Tool(run, g.Id, TestExecution); break;
                case "build" or "test":
                    deps.Add(new() { Name = $"{g.Id} command", State = DependencyState.Unknown, RequiredBy = [g.Id], Capabilities = [TestExecution],
                        Message = "detected from the merged workspace when the gate runs" });
                    break;
            }
        var traceGates = gates.Where(g => g.Kind == "traceability").Select(g => g.Id).ToList();
        foreach (var s in config.Tests.Where(t => t.Kind != "manual" && t.Run.Trim() != ""))
            if (traceGates.Count == 0) Tool(s.Run, null, TestExecution);
            else foreach (var id in traceGates) Tool(s.Run, id, TestExecution);

        var dw = config.Context.Docwizz;
        var contextGates = gates.Where(g => g.Kind == "system-context").Select(g => g.Id).ToList();
        if (dw.Enabled || contextGates.Count > 0)
        {
            // A docwizz gate running the same executable shares this entry, so docwizz is checked and listed once.
            var exe = dw.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            List<string> gateIds = [];
            HashSet<string> gateCaps = [];
            if (tools.Remove(exe, out var shared)) (gateIds, gateCaps) = (shared.Gates, shared.Caps);
            deps.Add(Docwizz(dw, dir, git, commit) with { RequiredBy = [.. contextGates, .. gateIds], Capabilities = [SystemContext, .. gateCaps] });
        }

        foreach (var (tool, (requiredBy, caps)) in tools)
            deps.Add(KnownTool(tool, dir) with { RequiredBy = requiredBy, Capabilities = [.. caps] });

        if (gates.Where(g => g.Kind == "environment").Select(g => g.Id).ToList() is { Count: > 0 } envGates)
            deps.Add(Docker(dir) with { RequiredBy = envGates });
        if (gates.Where(g => g.Kind == "ai-review").Select(g => g.Id).ToList() is { Count: > 0 } aiGates)
            deps.Add(AiEndpoint(config.Review) with { RequiredBy = aiGates });
        return deps;
    }

    /// <summary>A tool's version for the well-known tools (Gates.Versioned); anything else (a script) is never run: Unknown.</summary>
    DependencyHealth KnownTool(string tool, string dir)
    {
        if (!Gates.Versioned.Contains(tool))
            return new() { Name = tool, State = DependencyState.Unknown, Message = "not run during preflight; checked when its gate runs" };
        var r = Probe(dir, tool, "--version");
        return r.ExitCode switch
        {
            0 => new() { Name = tool, State = DependencyState.Available, Version = FirstLine(r.Stdout), Message = FirstLine(r.Stdout) },
            126 or 127 => new() { Name = tool, State = DependencyState.Unavailable, Message = $"{tool} is not installed", Remediation = $"install {tool} and make sure it is on the PATH" },
            _ => new() { Name = tool, State = DependencyState.Unknown, Message = $"{tool} --version failed (exit {r.ExitCode}): {FirstLine(r.Stderr)}" },
        };
    }

    /// <summary>docwizz --version (no analysis is run) and, when present, that docwizz.yaml is valid YAML.</summary>
    /// <param name="policy">context.docwizz: the command line that runs docwizz.</param>
    /// <param name="dir">Where docwizz is asked for its version.</param>
    /// <param name="git">The repository docwizz.yaml is read from.</param>
    /// <param name="commit">The commit docwizz.yaml is read at; null: the working tree.</param>
    DependencyHealth Docwizz(DocwizzPolicy policy, string dir, Git git, string? commit)
    {
        var words = policy.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return new() { Name = "docwizz", State = DependencyState.Misconfigured, Message = "context.docwizz.command is empty", Remediation = $"set context.docwizz.command in {RepoConfig.FileName}" };
        var r = Probe(dir, words[0], [.. words[1..], "--version"]);
        if (r.ExitCode is 126 or 127)
            return new() { Name = "docwizz", State = DependencyState.Unavailable, Message = $"'{words[0]}' not found",
                Remediation = $"install docwizz (github.com/mycaravam-crypto/docwizz) or set context.docwizz.command in {RepoConfig.FileName}" };
        if (r.ExitCode != 0)
            return new() { Name = "docwizz", State = DependencyState.Unavailable, Message = $"docwizz --version failed (exit {r.ExitCode}): {FirstLine(r.Stderr)}" };
        var version = FirstLine(r.Stdout);
        var yaml = commit != null
            ? git.Try("show", $"{commit}:docwizz.yaml") is { ExitCode: 0 } shown ? shown.Stdout : null
            : File.Exists(Path.Combine(dir, "docwizz.yaml")) ? File.ReadAllText(Path.Combine(dir, "docwizz.yaml")) : null;
        if (yaml != null)
            try { new YamlDotNet.RepresentationModel.YamlStream().Load(new StringReader(yaml)); }
            catch (YamlDotNet.Core.YamlException e)
            {
                return new() { Name = "docwizz", Version = version, State = DependencyState.Misconfigured, Message = $"docwizz.yaml is not valid YAML: {e.Message}",
                    Remediation = "fix docwizz.yaml, or regenerate it with docwizz init --force" };
            }
        return new() { Name = "docwizz", Version = version, State = DependencyState.Available, Message = $"docwizz {version}" + (yaml != null ? ", docwizz.yaml readable" : "") };
    }

    /// <summary>The compose provisioner: docker info (the daemon answers), then docker compose version (the plugin).</summary>
    DependencyHealth Docker(string dir)
    {
        DependencyHealth Down(string message, string fix) => new()
        {
            Name = "Docker", State = DependencyState.Unavailable, Capabilities = [EnvironmentProvisioning], Message = message, Remediation = fix,
        };
        var info = Probe(dir, "docker", "info", "--format", "{{.ServerVersion}}");
        if (info.ExitCode is 126 or 127) return Down("docker is not installed", "install Docker with the compose plugin");
        if (info.TimedOut) return Down("docker info did not answer within 15 s", "check that the Docker daemon is running");
        if (info.ExitCode != 0) return Down($"the Docker daemon is not reachable: {FirstLine(info.Stderr)}", "start the Docker daemon (or check DOCKER_HOST and your permissions)");
        var compose = Probe(dir, "docker", "compose", "version", "--short");
        if (compose.ExitCode != 0) return Down("docker compose is not available", "install the Docker compose plugin");
        var version = $"Docker {FirstLine(info.Stdout)}, compose {FirstLine(compose.Stdout)}";
        return new() { Name = "Docker", State = DependencyState.Available, Capabilities = [EnvironmentProvisioning], Version = version, Message = version };
    }

    /// <summary>
    /// The AI endpoint: the self-hosting policy first (a refused host is never contacted), then GET {endpoint}/models,
    /// which sends nothing but the key, and that the configured model is served. Only the host is ever shown.
    /// </summary>
    /// <param name="review">review: in .gitwizz.yml: endpoint, model, allowed hosts and the key's variable.</param>
    public DependencyHealth AiEndpoint(ReviewPolicy review)
    {
        const string name = "AI endpoint";
        DependencyHealth Result(DependencyState state, string message, string? fix = null) => new()
        {
            Name = name, State = state, Capabilities = [AiReview], Message = message, Remediation = fix,
        };
        if (Endpoints.Problem(review.Endpoint, review.AllowedHosts) is { } problem)
            return Result(DependencyState.Misconfigured, problem, $"point review.endpoint in {RepoConfig.FileName} at a self-hosted, OpenAI-compatible endpoint");
        if (string.IsNullOrWhiteSpace(review.Model))
            return Result(DependencyState.Misconfigured, "review.model is not set", $"set review.model in {RepoConfig.FileName}");
        var host = new Uri(review.Endpoint!).Host;
        using var client = http != null ? new HttpClient(http, disposeHandler: false) : new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Min(10, review.Timeout));
        using var request = new HttpRequestMessage(HttpMethod.Get, review.Endpoint!.TrimEnd('/') + "/models");
        if (review.ApiKeyEnv is { } env && Environment.GetEnvironmentVariable(env) is { Length: > 0 } key)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        HttpResponseMessage response;
        string body;
        try
        {
            response = client.Send(request);
            body = new StreamReader(response.Content.ReadAsStream()).ReadToEnd();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return Result(DependencyState.Unavailable,
                $"{host} unreachable" + (e is TaskCanceledException ? $" (no answer within {client.Timeout.TotalSeconds:0} s)" : ""),
                $"check that the inference server is running and reachable, then retry; review.endpoint is set in {RepoConfig.FileName}");
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Result(DependencyState.Misconfigured, $"{host} refused the key (HTTP {(int)response.StatusCode})",
                    review.ApiKeyEnv is { } e ? $"check the key in ${e}" : $"set review.api_key_env in {RepoConfig.FileName} to the variable holding the key");
            if (!response.IsSuccessStatusCode)
                return Result(DependencyState.Unknown, $"{host} is reachable, but its model list answered HTTP {(int)response.StatusCode}; the model is checked when the gate runs");
            List<string>? models = null;
            try { models = JsonNode.Parse(body)?["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>()).OfType<string>().ToList(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
            if (models == null)
                return Result(DependencyState.Unknown, $"{host} is reachable, but its model list is unreadable; the model is checked when the gate runs");
            return models.Contains(review.Model)
                ? Result(DependencyState.Available, $"{host} serves {review.Model}")
                : Result(DependencyState.Misconfigured, $"{host} does not serve model {review.Model}" + (models.Count > 0 ? $" (it serves {string.Join(", ", models.Take(5))})" : ""),
                    $"set review.model in {RepoConfig.FileName} to a served model, or load {review.Model} on the server");
        }
    }

    // --- Impact -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The report for dependencies and gate selections (one per profile that may run). A gate is affected when a
    /// dependency it relies on is down, or when a gate it needs is affected; the best reachable verdict is the evaluator's
    /// verdict with the affected gates erroring and every other gate passing, worst over the selections.
    /// </summary>
    /// <param name="deps">The checked dependencies; messages and fixes are redacted here.</param>
    /// <param name="config">The policy, or null when it can't be read (evaluation can't start).</param>
    /// <param name="selections">The gate selections an evaluation may run, each in run order.</param>
    /// <param name="promotion">Null if an ai-review gate may block, else why not (as for Evaluator.IsBlocking).</param>
    /// <param name="provider">The provider checked, for the report.</param>
    /// <param name="redactor">Masks secrets in everything the report shows.</param>
    public static HealthReport Assess(List<DependencyHealth> deps, RepoConfig? config, IReadOnlyList<(string Profile, List<GateSpec> Gates)> selections,
        Func<string?> promotion, string provider, Redactor redactor)
    {
        deps = Redact(deps, redactor);
        if (config == null || deps.Any(d => d.Essential && d.Down))
            return new() { Dependencies = deps, BestVerdict = null, Provider = provider, PolicySource = config?.Source };

        var down = deps.Where(d => d.Down).SelectMany(d => d.RequiredBy.Select(g => (Gate: g, Dep: d))).ToLookup(x => x.Gate, x => x.Dep);
        var affected = new Dictionary<string, GateImpact>();
        var verdicts = new List<string>();
        foreach (var (_, gates) in selections)
        {
            var results = new Dictionary<string, GateResult>();
            foreach (var spec in gates)
            {
                var blocking = Evaluator.IsBlocking(spec, config, promotion, out _);
                var unmet = spec.Requires.Where(n => results.TryGetValue(n, out var u) && (u.Status == GateStatus.Error || u.NeedsUnmet)).ToList();
                GateResult r;
                if (unmet.Count > 0)
                {
                    r = GateResult.Of(GateStatus.Skipped, "") with { NeedsUnmet = true };
                    affected.TryAdd(spec.Id, new(spec.Id, spec.Kind, blocking, $"needs {string.Join(", ", unmet)}"));
                }
                else if (down[spec.Id].ToList() is { Count: > 0 } missing)
                {
                    r = GateResult.Of(GateStatus.Error, "");
                    affected.TryAdd(spec.Id, new(spec.Id, spec.Kind, blocking, string.Join(", ", missing.Select(m => m.Name)) + " " + (missing.Count == 1 ? "is" : "are") + " not available"));
                }
                else r = GateResult.Of(GateStatus.Pass, "");
                results[spec.Id] = r with { Id = spec.Id, Blocking = blocking, BlocksMerge = blocking && (r.Status == GateStatus.Error || r.NeedsUnmet) };
            }
            verdicts.Add(Evaluation.VerdictOf(results.Values));
        }
        return new()
        {
            Dependencies = deps, Affected = [.. affected.Values], Provider = provider, PolicySource = config.Source,
            Profiles = selections.Select(s => s.Profile).Distinct().ToList(),
            BestVerdict = verdicts.Contains("undetermined") ? "undetermined" : "ready",
        };
    }

    /// <summary>The dependencies with secrets masked in their messages and fixes.</summary>
    public static List<DependencyHealth> Redact(IEnumerable<DependencyHealth> deps, Redactor redactor) => deps.Select(d => d with
    {
        Message = redactor.Apply(d.Message), Remediation = d.Remediation is null ? null : redactor.Apply(d.Remediation),
    }).ToList();

    /// <summary>The gate selections an evaluation may run: the given profile, else one per risk level (deduplicated).</summary>
    public static List<(string Profile, List<GateSpec> Gates)> Selections(RepoConfig config, string? profile) =>
        (profile != null ? [Evaluator.Select(config, profile, "low")]
            : new[] { "low", "medium", "high" }.Select(risk => Evaluator.Select(config, null, risk)))
        .DistinctBy(s => string.Join(',', s.Gates.Select(g => g.Id))).ToList();

    static Version? ParseVersion(string text)
    {
        var digits = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        var parts = digits.Split('.');
        return parts.Length >= 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) ? new Version(a, b) : null;
    }
}

/// <summary>Renders a HealthReport: the terminal view, plain text and stable JSON (schema gitwizz.health/v1).</summary>
public static class Health
{
    static string Mark(DependencyState s) => s switch { DependencyState.Available => "✓", DependencyState.Unknown => "?", _ => "✗" };

    static string Color(DependencyState s) => s switch
    {
        DependencyState.Available => "springgreen3", DependencyState.Unknown => "grey", _ => "indianred1",
    };

    public static string Name(DependencyState s) => s.ToString().ToUpperInvariant();

    public static string Name(OverallHealth o) => o switch
    {
        OverallHealth.Ok => "OK", OverallHealth.Degraded => "DEGRADED", OverallHealth.VerdictAtRisk => "VERDICT_AT_RISK", _ => "CANNOT_START",
    };

    /// <summary>What the overall state means for the user, in one sentence.</summary>
    public static string Meaning(HealthReport r) => r.Overall switch
    {
        OverallHealth.Ok => "Everything gitwizz needs is available.",
        OverallHealth.Degraded => "You can continue safely: only advisory checks are affected, the verdict is not.",
        OverallHealth.VerdictAtRisk => "You can continue, but a blocking gate cannot run: the verdict can be no better than UNDETERMINED until it is fixed.",
        _ => "Evaluation cannot start until the problems above are fixed.",
    };

    /// <summary>Text lines, with Spectre markup when markup is true.</summary>
    public static string Text(HealthReport r, bool markup = false)
    {
        string M(string text, string style) => markup ? $"[{style}]{Markup.Escape(text)}[/]" : text;
        string E(string text) => markup ? Markup.Escape(text) : text;
        var sb = new StringBuilder();
        sb.AppendLine(M("GITWIZZ ENVIRONMENT", "bold")).AppendLine();
        Lines(sb, r.Dependencies, markup);
        if (r.Affected.Count > 0)
        {
            sb.AppendLine().AppendLine(M("Affected gates", "bold"));
            foreach (var a in r.Affected)
                sb.AppendLine($"  {E(a.Id),-20} {M(a.Blocking ? "BLOCKING" : "advisory", a.Blocking ? "indianred1" : "grey")}  {E(a.Reason)}");
        }
        var down = r.Dependencies.Where(d => d.Down).SelectMany(d => d.Capabilities).Distinct().ToList();
        if (down.Count > 0) sb.AppendLine().AppendLine(M("Unavailable capabilities: ", "bold") + E(string.Join(", ", down)));
        var color = r.Overall switch { OverallHealth.Ok => "springgreen3", OverallHealth.Degraded => "orange1", _ => "indianred1" };
        sb.AppendLine().AppendLine(M("Overall: ", "bold") + M(Name(r.Overall), "bold " + color)
            + (r.BestVerdict != null ? E($"  (best reachable verdict: {r.BestVerdict.ToUpperInvariant()})") : ""));
        sb.AppendLine(E(Meaning(r)));
        if (r.PolicySource != null) sb.AppendLine(M($"Policy: {r.PolicySource}" + (r.Profiles.Count > 0 ? $", profiles {string.Join(", ", r.Profiles)}" : "") + $"; provider {r.Provider}", "grey"));
        return sb.ToString();
    }

    /// <summary>One line per dependency, then who uses it and, when it is down, the fix.</summary>
    static void Lines(StringBuilder sb, IEnumerable<DependencyHealth> deps, bool markup)
    {
        string M(string text, string style) => markup ? $"[{style}]{Markup.Escape(text)}[/]" : text;
        string E(string text) => markup ? Markup.Escape(text) : text;
        foreach (var d in deps)
        {
            sb.AppendLine($"  {M(Mark(d.State), Color(d.State))} {M(d.Name, "bold")}  {E(d.Message)}");
            if (d.RequiredBy.Count > 0) sb.AppendLine("      " + M($"used by: {string.Join(", ", d.RequiredBy)}", "grey"));
            if (d.Down && d.Remediation != null) sb.AppendLine("      " + M("fix: ", "grey") + E(d.Remediation));
        }
    }

    /// <summary>
    /// The guide's environment section (phase A: git, the repository, the provider), with Spectre markup; when an
    /// essential dependency is down, the fixes and that the guide cannot start.
    /// </summary>
    public static string Preflight(IReadOnlyCollection<DependencyHealth> deps)
    {
        var sb = new StringBuilder().AppendLine("[bold]Environment[/]");
        Lines(sb, deps, markup: true);
        if (deps.Any(d => d.Essential && d.Down))
            sb.AppendLine().AppendLine("[bold indianred1]CANNOT START[/]  no pull request can be loaded or evaluated until the problem above is fixed.");
        return sb.AppendLine().ToString();
    }

    /// <summary>Stable JSON (schema gitwizz.health/v1). Fields are only ever added, never renamed or removed.</summary>
    public static string Json(HealthReport r) => JsonSerializer.Serialize(new
    {
        schema = "gitwizz.health/v1",
        version = typeof(Health).Assembly.GetName().Version?.ToString(3),
        overall = Name(r.Overall),
        exitCode = r.ExitCode,
        bestVerdict = r.BestVerdict,
        provider = r.Provider,
        policy = r.PolicySource == null ? null : new { source = r.PolicySource, profiles = r.Profiles },
        dependencies = r.Dependencies.Select(d => new
        {
            name = d.Name, state = Name(d.State), essential = d.Essential, version = d.Version, message = d.Message, remediation = d.Remediation,
            capabilities = d.Capabilities, requiredBy = d.RequiredBy,
        }),
        unavailableCapabilities = r.Dependencies.Where(d => d.Down).SelectMany(d => d.Capabilities).Distinct(),
        affectedGates = r.Affected.Select(a => new { id = a.Id, type = a.Type, blocking = a.Blocking, reason = a.Reason }),
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
