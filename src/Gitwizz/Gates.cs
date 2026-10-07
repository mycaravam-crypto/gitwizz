using System.Diagnostics;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace Gitwizz;

/// <summary>
/// Pass and Warn let the merge go ahead; Fail is a quality failure; Error means the gate couldn't run (tool missing,
/// timeout, crash), so nothing is known about quality; Skipped means it didn't apply or a gate it needs didn't pass.
/// </summary>
public enum GateStatus { Pass, Warn, Fail, Skipped, Error }

/// <summary>One thing a gate found. Severity: error, warning or info. Evidence: the raw line or excerpt it came from.</summary>
public record Finding(string Message, string Severity = "error", string? File = null, int? Line = null, string? Rule = null, string? Evidence = null);

/// <summary>
/// The common, machine-readable result of every gate. Blocking comes from policy; BlocksMerge is the evaluator's
/// verdict: a blocking gate that failed, errored, or was skipped because a gate it needs didn't pass.
/// </summary>
public record GateResult
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public GateStatus Status { get; init; }
    public string Summary { get; init; } = "";
    public bool Blocking { get; init; }
    public bool BlocksMerge { get; init; }
    public List<Finding> Findings { get; init; } = [];
    public List<string> Evidence { get; init; } = []; // references: commits, log files, URLs
    public TimeSpan Duration { get; init; }
    public string? Command { get; init; }
    public string? Tool { get; init; }
    public string? ToolVersion { get; init; }
    public string? Log { get; init; } // tail of the output, redacted
    public bool NeedsUnmet { get; init; } // skipped because a needed gate didn't pass

    /// <summary>A result with just a status and a summary.</summary>
    public static GateResult Of(GateStatus status, string summary, params Finding[] findings) =>
        new() { Status = status, Summary = summary, Findings = [.. findings] };
}

/// <summary>A gate as configured in .gitwizz.yml. Type defaults to the id when that names a type (build, test, ...), else command.</summary>
public record GateSpec
{
    public string Id { get; init; } = "";
    public string? Type { get; init; }
    public string? Run { get; init; }               // command line, for workspace gates (build/test detect one when omitted)
    public bool? Blocking { get; init; }             // default: true, except ai-review (advisory)
    public int Timeout { get; init; } = 1800;       // seconds
    public List<string>? Needs { get; init; }       // gates that must pass first; workspace gates need merge by default
    public List<string> Paths { get; init; } = [];  // run only when a changed file matches; else skipped

    [YamlIgnore] public string Kind => Type ?? (Gates.Types.Contains(Id) ? Id : "command");
    [YamlIgnore] public bool IsBlocking => Blocking ?? Kind != "ai-review";
    [YamlIgnore] public IReadOnlyList<string> Requires => Needs ?? (Gates.Workspace.Contains(Kind) ? ["merge"] : []);
}

/// <summary>A pluggable quality gate: deterministic evidence about one aspect of a pull request.</summary>
public interface IQualityGate
{
    /// <summary>Runs the gate. Throwing is allowed: the evaluator turns an exception into an Error result.</summary>
    GateResult Run(GateContext ctx, GateSpec spec);
}

/// <summary>Everything a gate may look at, plus the merged workspace shared by the gates of one evaluation.</summary>
public sealed class GateContext : IDisposable
{
    public required Git Git { get; init; }
    public required PullRequest Pr { get; init; }
    public required string Target { get; init; }
    public required string TargetSha { get; init; }
    public string Provider { get; init; } = "local";
    public RepoConfig Config { get; init; } = RepoConfig.Default;
    public MergeStrategy Strategy { get; init; } = MergeStrategy.Merge;
    public Redactor Redactor { get; init; } = new([]);
    public string? EvidenceDir { get; init; }

    /// <summary>Synthetic commit of the PR merged into the target, set by the merge gate.</summary>
    public string? MergedState { get; set; }

    /// <summary>Extra environment for workspace commands; starts with the GITWIZZ_* variables.</summary>
    public Dictionary<string, string> Env { get; } = [];

    /// <summary>Requirement-to-test trace, set by the traceability gate.</summary>
    public Trace? Trace { get; set; }

    /// <summary>Results of the gates run so far, by id.</summary>
    public Dictionary<string, GateResult> Results { get; } = [];

    Worktree? _worktree;

    /// <summary>A checkout of the merged state (the PR head without one), created on first use and shared.</summary>
    public string Workspace => (_worktree ??= new Worktree(Git, MergedState ?? Pr.HeadSha)).Dir;

    /// <summary>Removes the workspace, if one was created.</summary>
    public void Dispose() => _worktree?.Dispose();
}

/// <summary>Masks secret values (declared environment variables and well-known tokens) in anything gitwizz prints or stores.</summary>
public sealed class Redactor
{
    static readonly string[] Known = ["GITHUB_TOKEN", "GH_TOKEN", "ADO_PAT", "SYSTEM_ACCESSTOKEN", "AZURE_DEVOPS_EXT_PAT"];
    readonly string[] _values;

    /// <summary>Collects the current values of the named variables and of the well-known token variables.</summary>
    public Redactor(IEnumerable<string> names, IEnumerable<string>? values = null) =>
        _values = Known.Concat(names).Select(Environment.GetEnvironmentVariable).Concat(values ?? [])
            .OfType<string>().Where(v => v.Trim().Length >= 4).Distinct().OrderByDescending(v => v.Length).ToArray();

    /// <summary>The text with every secret value replaced by ***.</summary>
    public string Apply(string? text) => text is null or "" ? text ?? "" : _values.Aggregate(text, (t, v) => t.Replace(v, "***"));

    /// <summary>The result with secrets masked in every text field.</summary>
    public GateResult Apply(GateResult r) => r with
    {
        Summary = Apply(r.Summary),
        Command = r.Command is null ? null : Apply(r.Command),
        Log = r.Log is null ? null : Apply(r.Log),
        Evidence = r.Evidence.Select(Apply).ToList(),
        Findings = r.Findings.Select(f => f with { Message = Apply(f.Message), Evidence = f.Evidence is null ? null : Apply(f.Evidence) }).ToList(),
    };
}

/// <summary>Gate registry and the built-in gates.</summary>
public static partial class Gates
{
    /// <summary>Known gate types.</summary>
    public static readonly string[] Types = ["merge", "policy", "build", "test", "docwizz", "command", "traceability", "ai-review"];

    /// <summary>Types that work on the merged state, so they need the merge gate.</summary>
    public static readonly string[] Workspace = ["build", "test", "docwizz", "command", "traceability", "ai-review"];

    /// <summary>Available without configuration: structural mergeability and repository policy (reviews, checks).</summary>
    public static readonly GateSpec[] BuiltIn = [new() { Id = "merge" }, new() { Id = "policy" }];

    /// <summary>The implementation for a gate type.</summary>
    public static IQualityGate Create(string kind) => kind switch
    {
        "merge" => new MergeGate(),
        "policy" => new PolicyGate(),
        "traceability" => new TraceabilityGate(),
        "ai-review" => new AiReviewGate(),
        _ => new CommandGate(),
    };

    /// <summary>Risk of a change: high (critical paths or many files), medium (API, migration or configuration changes), else low.</summary>
    public static (string Level, List<string> Reasons) Risk(PullRequest pr, RepoConfig config)
    {
        var paths = pr.Files.Select(f => f.Path).ToList();
        var high = new List<string>();
        foreach (var g in config.Risk.HighPaths)
            if (paths.FirstOrDefault(p => RepoConfig.Matches(g, p)) is { } hit) high.Add($"changes {hit} ({g})");
        if (paths.Count > config.Risk.MaxFiles) high.Add($"changes {paths.Count} files (more than {config.Risk.MaxFiles})");
        if (high.Count > 0) return ("high", high);
        var medium = new List<string>();
        if (pr.Api.Count > 0) medium.Add("changes the signature of " + string.Join(", ", pr.Api.Keys.Order().Take(3)));
        foreach (var c in new[] { FileClass.Migration, FileClass.Configuration })
            if (paths.FirstOrDefault(p => config.Classify(p) == c) is { } f) medium.Add($"changes {c.ToString().ToLowerInvariant()} {f}");
        return medium.Count > 0 ? ("medium", medium) : ("low", [$"{paths.Count} file(s), no API, migration or configuration change"]);
    }

    /// <summary>Default build or test command for the project at dir, from its build files; null if none is recognised.</summary>
    public static string? Detect(string dir, string kind)
    {
        bool Has(string pattern) => Directory.EnumerateFiles(dir, pattern).Any();
        var test = kind == "test";
        if (Has("*.sln") || Has("*.slnx") || Has("*.csproj") || Has("*.fsproj")) return test ? "dotnet test" : "dotnet build";
        if (Has("package.json")) return test ? "npm test" : "npm run build --if-present";
        if (Has("go.mod")) return test ? "go test ./..." : "go build ./...";
        if (Has("Cargo.toml")) return test ? "cargo test" : "cargo build";
        if (Has("pom.xml")) return test ? "mvn -B -q test" : "mvn -B -q -DskipTests package";
        if (Has("build.gradle") || Has("build.gradle.kts")) return (Has("gradlew") ? "./gradlew" : "gradle") + (test ? " test" : " build -x test");
        if (Has("pyproject.toml") || Has("setup.py")) return test ? "python -m pytest" : "python -m compileall -q .";
        if (Has("Makefile")) return test ? "make test" : "make";
        return null;
    }

    // Compiler and test-runner lines worth a finding of their own.
    [GeneratedRegex(@"^\s*(?<file>[^\s(][^(]*?)\((?<line>\d+)(,\d+)?\)\s*:\s*(?<sev>error|warning)\s+(?<rule>[A-Za-z]+\d+)\s*:\s*(?<msg>.+?)(\s+\[[^\]]+\])?\s*$")]
    private static partial Regex MsBuild();
    [GeneratedRegex(@"^(?<file>[\w./\\-]+\.\w+):(?<line>\d+)(:\d+)?:\s*(?<sev>error|warning)[:\s]\s*(?<msg>.+)$")]
    private static partial Regex Compiler();
    [GeneratedRegex(@"^\s*(Failed (?<test>[\w.`<>,\[\]-]+) \[|FAILED (?<test>\S+::\S+)|--- FAIL: (?<test>\S+))")]
    private static partial Regex FailedTest();

    /// <summary>Findings from command output: compiler errors and warnings (MSBuild and file:line: style) and failed tests.</summary>
    public static List<Finding> ParseOutput(string output, string? workspace = null)
    {
        string Rel(string f)
        {
            f = f.Trim().Replace('\\', '/');
            var ws = workspace?.Replace('\\', '/').TrimEnd('/') + "/";
            return workspace != null && f.StartsWith(ws) ? f[ws.Length..] : f;
        }
        var findings = new List<Finding>();
        foreach (var line in output.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (MsBuild().Match(l) is { Success: true } m)
                findings.Add(new(m.Groups["msg"].Value, m.Groups["sev"].Value, Rel(m.Groups["file"].Value), int.Parse(m.Groups["line"].Value), m.Groups["rule"].Value, l.Trim()));
            else if (Compiler().Match(l) is { Success: true } c)
                findings.Add(new(c.Groups["msg"].Value.Trim(), c.Groups["sev"].Value, Rel(c.Groups["file"].Value), int.Parse(c.Groups["line"].Value), null, l.Trim()));
            else if (FailedTest().Match(l) is { Success: true } t)
                findings.Add(new($"test failed: {t.Groups["test"].Value}", "error", Rule: "test", Evidence: l.Trim()));
        }
        // MSBuild repeats every diagnostic in its summary; keep errors first, at most 50.
        return findings.DistinctBy(f => (f.Message, f.File, f.Line, f.Severity)).OrderBy(f => f.Severity == "error" ? 0 : 1).Take(50).ToList();
    }

    /// <summary>The last lines of a log, for a result that must stay small.</summary>
    public static string Tail(string text, int lines = 40)
    {
        var all = text.Trim('\n', '\r', ' ').Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }

    // Tools whose --version is safe to ask; anything else (a script) is never run just to read a version.
    static readonly string[] Versioned = ["dotnet", "npm", "node", "go", "cargo", "mvn", "gradle", "python", "python3", "make", "docwizz", "git", "docker"];

    /// <summary>First word of a command line and, for well-known tools, its version.</summary>
    public static (string Tool, string? Version) ToolOf(string command, string dir)
    {
        var tool = command.TrimStart().Split(' ', 2)[0];
        if (!Versioned.Contains(tool)) return (tool, null);
        var r = Git.Exec(dir, tool, ["--version"], timeout: TimeSpan.FromSeconds(15));
        return (tool, r.ExitCode == 0 ? r.Stdout.Split('\n')[0].Trim() : null);
    }
}

/// <summary>Structural mergeability: simulates the merge into the target with the plan's strategy.</summary>
public sealed class MergeGate : IQualityGate
{
    /// <summary>Pass on a clean merge, Warn when only regenerable files conflict, Fail on a real conflict.</summary>
    public GateResult Run(GateContext ctx, GateSpec spec)
    {
        var (git, pr) = (ctx.Git, ctx.Pr);
        var tool = ("git", git.Try("--version").Stdout.Trim());
        if (pr.AlreadyMerged || git.IsAncestor(pr.HeadSha, ctx.TargetSha))
        {
            ctx.MergedState = ctx.TargetSha;
            return GateResult.Of(GateStatus.Pass, $"already contained in {ctx.Target}") with { Tool = tool.Item1, ToolVersion = tool.Item2 };
        }
        var sim = new Simulator(git, ctx.Strategy, ctx.Config).Simulate(ctx.TargetSha, pr);
        var strategy = ctx.Strategy.ToString().ToLowerInvariant();
        List<string> evidence = [$"simulated {strategy} of {pr.HeadSha[..Math.Min(12, pr.HeadSha.Length)]} into {ctx.Target} ({ctx.TargetSha[..Math.Min(12, ctx.TargetSha.Length)]})"];
        GateResult r;
        switch (sim.Outcome)
        {
            case MergeOutcome.Conflict:
                r = GateResult.Of(GateStatus.Fail, $"conflicts with {ctx.Target} in {sim.ConflictFiles.Count} file(s)",
                    [.. sim.ConflictFiles.Select(f => new Finding($"merge conflict in {f}", File: f.Split(' ')[0], Rule: "merge-conflict"))]);
                break;
            case MergeOutcome.RegenerationRequired:
                ctx.MergedState = sim.Commit!.Value;
                r = GateResult.Of(GateStatus.Warn, $"merges after regenerating {string.Join(", ", sim.RegenerateFiles)}",
                    [.. sim.RegenerateFiles.Select(f => new Finding(
                        "regenerate after merge" + (ctx.Config.RegenerateCommand(f) is { } c ? $" ({c})" : ""), "warning", f, Rule: "regenerate"))]);
                evidence.Add($"merged state {ctx.MergedState}");
                break;
            default:
                ctx.MergedState = sim.Commit!.Value;
                r = GateResult.Of(GateStatus.Pass, $"merges cleanly into {ctx.Target} ({strategy})");
                evidence.Add($"merged state {ctx.MergedState}");
                break;
        }
        return r with { Evidence = evidence, Tool = tool.Item1, ToolVersion = tool.Item2 };
    }
}

/// <summary>Repository policy from the provider: draft, reviews, required checks, branch protection, declared dependencies.</summary>
public sealed class PolicyGate : IQualityGate
{
    /// <summary>Fail with every unmet rule; Warn on provider notes (behind, auto-merge); Skipped for local branches.</summary>
    public GateResult Run(GateContext ctx, GateSpec spec)
    {
        var pr = ctx.Pr;
        if (ctx.Provider == "local")
            return GateResult.Of(GateStatus.Skipped, "local branches carry no review, check or branch-protection data");
        var problems = Analyzer.PolicyProblems(pr).Select(p => new Finding(p, Rule: "policy")).ToList();
        problems.AddRange(pr.OpenOutsideDependencies.Select(d => new Finding($"depends on open pull request {d}", Rule: "dependency")));
        if (pr.BaseRef != "" && pr.BaseRef != ctx.Target)
            problems.Add(new($"targets '{pr.BaseRef}', not '{ctx.Target}'", Rule: "target"));
        var state = $"review: {pr.ReviewDecision ?? "none required"}, checks: {pr.CiStatus ?? "none"}, draft: {(pr.IsDraft ? "yes" : "no")}";
        if (problems.Count > 0)
            return GateResult.Of(GateStatus.Fail, string.Join("; ", problems.Select(p => p.Message)), [.. problems]) with { Evidence = [state] };
        if (Analyzer.GitHubNote(pr) is { } note)
            return GateResult.Of(GateStatus.Warn, note, new Finding(note, "warning", Rule: "provider")) with { Evidence = [state] };
        return GateResult.Of(GateStatus.Pass, state) with { Evidence = [state] };
    }
}

/// <summary>
/// Runs a command in the merged workspace: build, test, docwizz or any external command. Exit 0 passes; another exit
/// code fails; a missing tool (126/127) or a timeout is an Error, since it says nothing about the change.
/// </summary>
public sealed class CommandGate : IQualityGate
{
    /// <summary>The command line a spec runs: its run:, else the type's default (detected for build and test).</summary>
    public static string? CommandFor(GateSpec spec, string workspace) =>
        spec.Run is { Length: > 0 } run ? run
        : spec.Kind == "docwizz" ? "docwizz check . --since \"$GITWIZZ_TARGET_SHA\""
        : spec.Kind is "build" or "test" ? Gates.Detect(workspace, spec.Kind)
        : null;

    /// <summary>Runs the command with the gate's timeout and turns its exit code and output into a result.</summary>
    public GateResult Run(GateContext ctx, GateSpec spec)
    {
        var ws = ctx.Workspace;
        if (CommandFor(spec, ws) is not { } command)
            return GateResult.Of(GateStatus.Error, $"no {spec.Kind} command configured, and none detected: set run: for gate '{spec.Id}'");
        var (tool, version) = Gates.ToolOf(command, ws);
        var sw = Stopwatch.StartNew();
        var r = Git.Shell(ws, command, ctx.Env, TimeSpan.FromSeconds(spec.Timeout));
        var output = ctx.Redactor.Apply(string.Join('\n', new[] { r.Stdout.TrimEnd(), r.Stderr.TrimEnd() }.Where(o => o != "")));
        var evidence = new List<string> { $"exit code {r.ExitCode} after {sw.Elapsed.TotalSeconds:0.0} s" };
        if (ctx.EvidenceDir != null)
        {
            var log = Path.Combine(ctx.EvidenceDir, spec.Id + ".log");
            File.WriteAllText(log, $"$ {ctx.Redactor.Apply(command)}\n{output}");
            evidence.Add(Path.GetRelativePath(Directory.GetCurrentDirectory(), log));
        }
        var findings = Gates.ParseOutput(output, ws);
        var res = new GateResult { Command = command, Tool = tool, ToolVersion = version, Evidence = evidence, Log = Gates.Tail(output), Findings = findings };
        if (r.TimedOut)
            return res with { Status = GateStatus.Error, Summary = $"timed out after {spec.Timeout} s" };
        if (r.ExitCode is 126 or 127)
            return res with { Status = GateStatus.Error, Summary = $"could not run '{tool}' (exit code {r.ExitCode}): is it installed?" };
        if (r.ExitCode != 0)
        {
            var errors = findings.Count(f => f.Severity == "error");
            if (errors == 0) findings.Insert(0, new($"'{command}' exited with code {r.ExitCode}", Evidence: Gates.Tail(output, 10)));
            return res with { Status = GateStatus.Fail, Summary = $"failed (exit code {r.ExitCode})" + (errors > 0 ? $", {errors} error(s)" : ""), Findings = findings };
        }
        var warnings = findings.Count(f => f.Severity == "warning");
        return res with { Status = GateStatus.Pass, Summary = "passed" + (warnings > 0 ? $" with {warnings} warning(s)" : "") };
    }
}
