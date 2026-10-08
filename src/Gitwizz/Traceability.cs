using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gitwizz;

/// <summary>
/// A test suite from .gitwizz.yml: a command that runs some tests, what it covers, and which requirements it verifies.
/// Kind is informational (unit, integration, contract, e2e, ui), except manual: never run, its criteria stay manual.
/// </summary>
public record TestSuite
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "unit";
    public string Run { get; init; } = "";
    public List<string> Covers { get; init; } = [];   // code paths: a change to one selects the suite
    public List<string> Files { get; init; } = [];    // its test files: searched for changed names and requirement ids
    public List<string> Criteria { get; init; } = []; // requirements it verifies: "AB#4711" (all criteria) or "#57.2" (one)
    public bool Always { get; init; }
    public bool HighRisk { get; init; }               // also run for every high-risk change
    public int Timeout { get; init; } = 1800;
}

/// <summary>Whether uncovered acceptance criteria block (only when policy says so), from .gitwizz.yml traceability:.</summary>
public record TraceabilityPolicy
{
    public bool Require { get; init; }
}

/// <summary>A selected suite and every reason it is required.</summary>
public record SelectedTest(TestSuite Suite, List<string> Reasons);

/// <summary>One suite's execution.</summary>
public record SuiteRun(string Suite, GateStatus Status, int ExitCode, TimeSpan Duration, string Summary, List<Finding> Findings);

/// <summary>
/// One acceptance criterion traced to tests. Status: covered (a linked suite ran and passed), failed (one failed),
/// uncovered (no linked suite), unknown (linked, but not run here), manual (only manual suites, or marked [manual]).
/// </summary>
public record CriterionTrace(string Id, string WorkItem, string Text, string Status, List<string> Tests, List<string> Evidence);

/// <summary>Requirement → change → test → execution → result, for one pull request.</summary>
public class Trace
{
    public required string Pr { get; init; }
    public required string Commit { get; init; }
    public string Risk { get; init; } = "low";
    public List<string> RiskReasons { get; init; } = [];
    public List<string> ChangedSymbols { get; init; } = [];
    public List<SelectedTest> Selected { get; init; } = [];
    public List<string> NotSelected { get; init; } = [];
    public List<WorkItem> WorkItems { get; init; } = [];
    public Dictionary<string, List<(string Criterion, string Evidence)>> Links { get; init; } = []; // suite -> criteria it verifies
    public List<SuiteRun> Runs { get; } = [];
    public List<CriterionTrace> Criteria { get; set; } = [];
}

/// <summary>Selects the tests a change needs, runs them, and traces every acceptance criterion to its result.</summary>
public static partial class Traceability
{
    [GeneratedRegex(@"\[manual\]", RegexOptions.IgnoreCase)]
    private static partial Regex ManualMarker();

    /// <summary>Criterion ids of a work item: "AB#4711.1", "AB#4711.2", … (1-based, in the order written).</summary>
    public static string CriterionId(WorkItem w, int index) => $"{w.Id}.{index + 1}";

    /// <summary>Names a change touches: changed C# members and types, and names whose signature changed (4+ characters).</summary>
    public static List<string> ChangedSymbols(PullRequest pr) =>
        pr.Members.Select(m => m.Split('(')[0].Split('.')[^1]).Concat(pr.Api.Keys)
            .Where(n => n.Length >= 4 && Regex.IsMatch(n, @"^\w+$")).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Selection and requirement links at commit (the merged state or the PR head). Reads the suites' test files in one
    /// git process; no test runs here.
    /// </summary>
    public static Trace Build(Git git, PullRequest pr, RepoConfig config, string commit)
    {
        var (risk, riskReasons) = Gates.Risk(pr, config);
        var symbols = ChangedSymbols(pr);
        var changed = pr.Files.SelectMany(f => f.OldPath is null ? [f.Path] : new[] { f.Path, f.OldPath }).Distinct().ToList();
        var criteria = pr.WorkItems.SelectMany(w => w.AcceptanceCriteria.Select((c, i) => (Id: CriterionId(w, i), Item: w.Id))).ToList();

        // Test files of every suite, read once.
        var tree = git.Run("ls-tree", "-r", "--name-only", "-z", commit).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var suiteFiles = config.Tests.ToDictionary(s => s.Id, s => tree.Where(p => s.Files.Any(g => RepoConfig.Matches(g, p))).ToList());
        var paths = suiteFiles.Values.SelectMany(f => f).Distinct().ToList();
        var contents = paths.Zip(git.ReadBlobs(paths.Select(p => $"{commit}:{p}").ToList())).ToDictionary(x => x.First, x => x.Second);
        var symbolPattern = symbols.Count > 0 ? new Regex(@"\b(" + string.Join('|', symbols.Select(Regex.Escape)) + @")\b") : null;

        var links = new Dictionary<string, List<(string Criterion, string Evidence)>>();
        var selected = new List<SelectedTest>();
        var notSelected = new List<string>();
        foreach (var s in config.Tests)
        {
            var linked = new List<(string Criterion, string Evidence)>();
            foreach (var r in s.Criteria)
                foreach (var c in criteria.Where(c => Refers(r, c.Id, c.Item)))
                    linked.Add((c.Id, $"{RepoConfig.FileName}: tests.{s.Id}.criteria lists {r}"));
            var uses = new List<string>();
            foreach (var f in suiteFiles[s.Id])
            {
                var lines = contents[f].Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (var item in pr.WorkItems)
                        foreach (Match m in MentionPattern(item.Id).Matches(lines[i]))
                            foreach (var c in criteria.Where(c => c.Item == item.Id && Refers(m.Value, c.Id, c.Item)))
                                linked.Add((c.Id, $"{f}:{i + 1} mentions {m.Value}"));
                    if (symbolPattern?.Match(lines[i]) is { Success: true } sm && uses.Count < 3) uses.Add($"{f}:{i + 1} uses {sm.Value} (changed)");
                }
            }
            links[s.Id] = linked.DistinctBy(l => l.Criterion).ToList();

            var reasons = new List<string>();
            if (s.Kind == "manual") { notSelected.Add($"{s.Id}: manual"); continue; }
            if (s.Always) reasons.Add("always runs");
            if (changed.Where(p => s.Covers.Any(g => RepoConfig.Matches(g, p))).Take(3).ToList() is { Count: > 0 } covered)
                reasons.Add("covers changed " + string.Join(", ", covered));
            if (changed.Intersect(suiteFiles[s.Id]).Take(3).ToList() is { Count: > 0 } own) reasons.Add("its test files changed: " + string.Join(", ", own));
            reasons.AddRange(uses);
            if (links[s.Id].Count > 0) reasons.Add("verifies " + string.Join(", ", links[s.Id].Select(l => l.Criterion)));
            if (s.HighRisk && risk == "high") reasons.Add($"high-risk change ({string.Join("; ", riskReasons)})");
            if (reasons.Count > 0) selected.Add(new(s, reasons));
            else notSelected.Add($"{s.Id}: not affected by this change");
        }
        var trace = new Trace
        {
            Pr = pr.Id, Commit = commit, Risk = risk, RiskReasons = riskReasons, ChangedSymbols = symbols, Selected = selected,
            NotSelected = notSelected, WorkItems = pr.WorkItems, Links = links,
        };
        Resolve(trace, config);
        return trace;
    }

    /// <summary>"AB#4711" refers to all criteria of AB#4711, "AB#4711.2" to one.</summary>
    static bool Refers(string reference, string criterion, string item) => reference == criterion || reference == item;

    static Regex MentionPattern(string item) => new((item.StartsWith('#') ? @"(?<![\w#])" : @"\b") + Regex.Escape(item) + @"(\.\d+)?\b");

    /// <summary>Runs the selected suites in dir, in order, and records their results; then re-resolves the criteria.</summary>
    public static void RunSuites(Trace trace, RepoConfig config, string dir, IDictionary<string, string> env, Redactor redactor, string? evidenceDir = null,
        ProgressBars? progress = null)
    {
        progress?.Start($"Running {trace.Selected.Count} test suites", trace.Selected.Count);
        foreach (var (t, i) in trace.Selected.Select((t, i) => (t, i)))
        {
            progress?.Describe($"Test suite {t.Suite.Id} ({i + 1}/{trace.Selected.Count})");
            var sw = Stopwatch.StartNew();
            var r = Git.Shell(dir, t.Suite.Run, env, TimeSpan.FromSeconds(t.Suite.Timeout));
            var output = redactor.Apply(string.Join('\n', new[] { r.Stdout.TrimEnd(), r.Stderr.TrimEnd() }.Where(o => o != "")));
            if (evidenceDir != null) File.WriteAllText(Path.Combine(evidenceDir, $"test-{t.Suite.Id}.log"), $"$ {redactor.Apply(t.Suite.Run)}\n{output}");
            var findings = Gates.ParseOutput(output, dir).Where(f => f.Severity == "error").ToList();
            var (status, summary) = r.TimedOut ? (GateStatus.Error, $"timed out after {t.Suite.Timeout} s")
                : r.ExitCode is 126 or 127 ? (GateStatus.Error, $"could not run (exit code {r.ExitCode})")
                : r.ExitCode != 0 ? (GateStatus.Fail, $"failed (exit code {r.ExitCode})")
                : (GateStatus.Pass, "passed");
            if (status != GateStatus.Pass && findings.Count == 0) findings.Add(new($"{t.Suite.Id}: {summary}", Evidence: Gates.Tail(output, 10)));
            trace.Runs.Add(new(t.Suite.Id, status, r.ExitCode, sw.Elapsed, summary, findings));
            progress?.Advance();
        }
        Resolve(trace, config);
    }

    /// <summary>Each criterion's status from its links and the runs so far. A link alone is never coverage: only a passing run is.</summary>
    static void Resolve(Trace trace, RepoConfig config)
    {
        var kinds = config.Tests.ToDictionary(s => s.Id, s => s.Kind);
        trace.Criteria = trace.WorkItems.SelectMany(w => w.AcceptanceCriteria.Select((text, i) =>
        {
            var id = CriterionId(w, i);
            var suites = trace.Links.Where(l => l.Value.Any(x => x.Criterion == id)).Select(l => l.Key).Order().ToList();
            var evidence = trace.Links.SelectMany(l => l.Value.Where(x => x.Criterion == id).Select(x => x.Evidence)).ToList();
            var runs = trace.Runs.Where(r => suites.Contains(r.Suite)).ToList();
            evidence.AddRange(runs.Select(r => $"{r.Suite}: {r.Summary} ({r.Duration.TotalSeconds:0.0} s)"));
            var status = runs.Any(r => r.Status == GateStatus.Fail) ? "failed"
                : runs.Any(r => r.Status == GateStatus.Pass) ? "covered"
                : ManualMarker().IsMatch(text) || (suites.Count > 0 && suites.All(s => kinds.GetValueOrDefault(s) == "manual")) ? "manual"
                : suites.Count > 0 ? "unknown"
                : "uncovered";
            return new CriterionTrace(id, w.Id, text, status, suites, evidence);
        })).ToList();
    }

    /// <summary>Stable JSON (schema gitwizz.trace/v1) for publishing, e.g. to Azure DevOps test results.</summary>
    public static object Model(Trace t) => new
    {
        schema = "gitwizz.trace/v1",
        pr = t.Pr,
        commit = t.Commit,
        risk = new { level = t.Risk, reasons = t.RiskReasons },
        changedSymbols = t.ChangedSymbols,
        selected = t.Selected.Select(s => new { id = s.Suite.Id, kind = s.Suite.Kind, command = s.Suite.Run, reasons = s.Reasons }),
        notSelected = t.NotSelected,
        runs = t.Runs.Select(r => new { suite = r.Suite, status = Evaluator.Name(r.Status), exitCode = r.ExitCode, durationMs = (long)r.Duration.TotalMilliseconds, summary = r.Summary,
            findings = r.Findings.Select(f => new { severity = f.Severity, message = f.Message, file = f.File, line = f.Line }) }),
        workItems = t.WorkItems.Select(w => new { id = w.Id, type = w.Type, title = w.Title, state = w.State, url = w.Url }),
        criteria = t.Criteria.Select(c => new { id = c.Id, workItem = c.WorkItem, text = c.Text, status = c.Status, tests = c.Tests, evidence = c.Evidence }),
        summary = t.Criteria.GroupBy(c => c.Status).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
    };

    /// <summary>Indented JSON of Model.</summary>
    public static string Json(Trace t) => JsonSerializer.Serialize(Model(t), new JsonSerializerOptions
        { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>Plain text: selected tests with reasons, then the requirement → test → result matrix.</summary>
    public static string Text(Trace t)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TRACEABILITY {t.Pr} ({t.Commit[..Math.Min(12, t.Commit.Length)]})");
        sb.AppendLine($"Risk: {t.Risk} ({string.Join("; ", t.RiskReasons)})");
        if (t.ChangedSymbols.Count > 0) sb.AppendLine($"Changed symbols: {string.Join(", ", t.ChangedSymbols)}");
        sb.AppendLine();
        sb.AppendLine(t.Selected.Count > 0 ? "Selected tests:" : "Selected tests: none");
        foreach (var s in t.Selected)
        {
            var run = t.Runs.FirstOrDefault(r => r.Suite == s.Suite.Id);
            sb.AppendLine($"  {s.Suite.Id} ({s.Suite.Kind}){(run != null ? $": {run.Summary}" : "")}");
            foreach (var r in s.Reasons) sb.AppendLine($"    why: {r}");
        }
        foreach (var n in t.NotSelected) sb.AppendLine($"  skipped {n}");
        sb.AppendLine();
        if (t.WorkItems.Count == 0) sb.AppendLine("No linked work items: nothing to trace.");
        foreach (var w in t.WorkItems)
        {
            sb.AppendLine($"{w.Id} {w.Title} ({w.Type}, {w.State})");
            if (w.AcceptanceCriteria.Count == 0) sb.AppendLine("  no acceptance criteria found");
            foreach (var c in t.Criteria.Where(c => c.WorkItem == w.Id))
            {
                sb.AppendLine($"  {c.Status.ToUpperInvariant(),-9} {c.Id}  {c.Text}");
                if (c.Tests.Count > 0) sb.AppendLine($"            tests: {string.Join(", ", c.Tests)}");
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// The traceability gate: runs the tests the change needs and traces each acceptance criterion to a result. Failing
/// tests fail it; uncovered criteria fail it only when traceability.require is set, else they warn.
/// </summary>
public sealed class TraceabilityGate : IQualityGate
{
    /// <summary>Builds the trace on the merged state, runs the selected suites, and stores the trace on the context.</summary>
    public GateResult Run(GateContext ctx, GateSpec spec)
    {
        if (ctx.Config.Tests.Count == 0)
            return GateResult.Of(GateStatus.Skipped, $"no tests: defined in {RepoConfig.FileName}");
        var commit = ctx.MergedState ?? ctx.Pr.HeadSha;
        var trace = Traceability.Build(ctx.Git, ctx.Pr, ctx.Config, commit);
        Traceability.RunSuites(trace, ctx.Config, ctx.Workspace, ctx.Env, ctx.Redactor, ctx.EvidenceDir);
        ctx.Trace = trace;

        var findings = trace.Runs.Where(r => r.Status != GateStatus.Pass).SelectMany(r => r.Findings.Select(f => f with { Rule = f.Rule ?? r.Suite })).ToList();
        var open = trace.Criteria.Where(c => c.Status is "uncovered" or "unknown" or "failed").ToList();
        var require = ctx.Config.Traceability.Require;
        findings.AddRange(open.Select(c => new Finding($"{c.Id} {c.Status}: {c.Text}", require ? "error" : "warning", Rule: "traceability",
            Evidence: c.Evidence.Count > 0 ? string.Join('\n', c.Evidence) : null)));
        var counts = string.Join(", ", trace.Criteria.GroupBy(c => c.Status).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"));
        var summary = $"{trace.Selected.Count} of {ctx.Config.Tests.Count} suite(s) selected"
            + (trace.Runs.Count > 0 ? $", {trace.Runs.Count(r => r.Status == GateStatus.Pass)} passed" : "")
            + (trace.Criteria.Count > 0 ? $"; criteria: {counts}" : "; no acceptance criteria linked");
        var evidence = trace.Runs.Select(r => $"{r.Suite}: exit code {r.ExitCode} after {r.Duration.TotalSeconds:0.0} s").ToList();
        if (ctx.EvidenceDir != null) evidence.AddRange(trace.Runs.Select(r => Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.Combine(ctx.EvidenceDir, $"test-{r.Suite}.log"))));
        var status = trace.Runs.Any(r => r.Status == GateStatus.Fail) ? GateStatus.Fail
            : trace.Runs.Any(r => r.Status == GateStatus.Error) ? GateStatus.Error
            : open.Count > 0 ? (require ? GateStatus.Fail : GateStatus.Warn)
            : GateStatus.Pass;
        return new GateResult { Status = status, Summary = summary, Findings = findings, Evidence = evidence };
    }
}
