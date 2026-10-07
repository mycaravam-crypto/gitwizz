using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace Gitwizz;

/// <summary>The merge-readiness verdict for one pull request, with every gate's result as evidence.</summary>
public class Evaluation
{
    public required PullRequest Pr { get; init; }
    public required string Target { get; init; }
    public required string TargetSha { get; init; }
    public string Provider { get; init; } = "local";
    public MergeStrategy Strategy { get; init; }
    public string PolicySource { get; init; } = "built-in";
    public string Profile { get; init; } = "built-in";
    public string Risk { get; init; } = "low";
    public List<string> RiskReasons { get; init; } = [];
    public List<GateResult> Gates { get; init; } = [];
    public Trace? Trace { get; init; } // when a traceability gate ran

    /// <summary>No blocking gate failed, errored or was left unrun.</summary>
    public bool Ready => !Gates.Any(g => g.BlocksMerge);

    /// <summary>ready; blocked (a blocking gate failed: the change needs work); undetermined (only errors: rerun or fix the tooling).</summary>
    public string Verdict => Ready ? "ready" : Gates.Any(g => g.BlocksMerge && g.Status == GateStatus.Fail) ? "blocked" : "undetermined";

    public IEnumerable<GateResult> Blockers => Gates.Where(g => g.BlocksMerge);

    /// <summary>0 ready, 3 blocked, 4 undetermined.</summary>
    public int ExitCode => Verdict switch { "ready" => 0, "blocked" => 3, _ => 4 };
}

/// <summary>Selects the gates for a pull request from the repository's policy and runs them in dependency order.</summary>
public static class Evaluator
{
    /// <summary>
    /// The profile to run and its gates in run order: --profile, else the profile for the PR's risk level, else
    /// "default", else every gate. merge always runs first, and a gate's needs run before it.
    /// </summary>
    public static (string Profile, List<GateSpec> Gates) Select(RepoConfig config, string? profile, string risk)
    {
        var specs = config.GateSpecs().ToDictionary(g => g.Id);
        string name;
        IEnumerable<string> ids;
        if (profile != null)
            (name, ids) = (profile, config.Profiles.GetValueOrDefault(profile)
                ?? throw new ArgumentException($"unknown profile '{profile}'" + (config.Profiles.Count > 0 ? $" ({string.Join(", ", config.Profiles.Keys)})" : " (.gitwizz.yml defines none)")));
        else if (config.Risk.Profiles.TryGetValue(risk, out var byRisk)) (name, ids) = (byRisk, config.Profiles[byRisk]);
        else if (config.Profiles.TryGetValue("default", out var d)) (name, ids) = ("default", d);
        else (name, ids) = (config.Gates.Count > 0 ? "all" : "built-in", config.GateSpecs().Select(g => g.Id));

        var order = new List<GateSpec>();
        var visiting = new List<string>();
        void Add(string id)
        {
            if (order.Any(o => o.Id == id)) return;
            if (visiting.Contains(id)) throw new InvalidOperationException($"invalid {RepoConfig.FileName}: gate needs form a cycle: {string.Join(" -> ", visiting.SkipWhile(v => v != id).Append(id))}");
            visiting.Add(id);
            foreach (var n in specs[id].Requires) Add(n);
            visiting.Remove(id);
            order.Add(specs[id]);
        }
        Add("merge");
        foreach (var id in ids) Add(id);
        return (name, order);
    }

    /// <summary>Runs the selected gates. A gate whose needs didn't pass is skipped and, if blocking, blocks the merge.</summary>
    public static Evaluation Run(GateContext ctx, string? profile = null, Action<string>? status = null)
    {
        var (risk, reasons) = Gates.Risk(ctx.Pr, ctx.Config);
        var (name, specs) = Select(ctx.Config, profile, risk);
        ctx.Env["GITWIZZ_PR"] = ctx.Pr.Id;
        ctx.Env["GITWIZZ_TARGET"] = ctx.Target;
        ctx.Env["GITWIZZ_TARGET_SHA"] = ctx.TargetSha;
        ctx.Env["GITWIZZ_HEAD_SHA"] = ctx.Pr.HeadSha;
        ctx.Env["GITWIZZ_RISK"] = risk;

        foreach (var spec in specs)
        {
            status?.Invoke($"Gate {spec.Id}…");
            var sw = Stopwatch.StartNew();
            var unmet = spec.Requires.Select(n => ctx.Results[n])
                .Where(r => r.Status is not (GateStatus.Pass or GateStatus.Warn) && !(r.Status == GateStatus.Skipped && !r.NeedsUnmet)).ToList();
            GateResult r;
            if (unmet.Count > 0)
                r = GateResult.Of(GateStatus.Skipped, "needs " + string.Join(", ", unmet.Select(u => $"{u.Id}, which did not pass ({Name(u.Status)}: {u.Summary})")))
                    with { NeedsUnmet = true };
            else if (spec.Paths.Count > 0 && !ctx.Pr.Files.Any(f => spec.Paths.Any(p => RepoConfig.Matches(p, f.Path))))
                r = GateResult.Of(GateStatus.Skipped, "no changed file matches " + string.Join(", ", spec.Paths));
            else
            {
                try { r = Gates.Create(spec.Kind).Run(ctx, spec); }
                catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    r = GateResult.Of(GateStatus.Error, $"gate could not run: {e.Message}");
                }
            }
            var blocking = spec.IsBlocking;
            if (blocking && spec.Kind == "ai-review" && Promotion.Check(ctx) is { } why)
            {
                // AI judgement blocks only once a benchmark has validated it: until then it is advisory.
                blocking = false;
                r = r with { Findings = [.. r.Findings, new Finding($"blocking: true is not in effect: {why}", "info", Rule: "ai-promotion")] };
            }
            r = r with { Id = spec.Id, Type = spec.Kind, Blocking = blocking, Duration = sw.Elapsed };
            r = ctx.Redactor.Apply(r with { BlocksMerge = blocking && (r.Status is GateStatus.Fail or GateStatus.Error || r.NeedsUnmet) });
            ctx.Results[spec.Id] = r;
        }
        return new Evaluation
        {
            Pr = ctx.Pr, Target = ctx.Target, TargetSha = ctx.TargetSha, Provider = ctx.Provider, Strategy = ctx.Strategy,
            PolicySource = ctx.Config.Source, Profile = name, Risk = risk, RiskReasons = reasons, Gates = [.. ctx.Results.Values], Trace = ctx.Trace,
        };
    }

    /// <summary>Lower-case status name, as in JSON.</summary>
    public static string Name(GateStatus s) => s.ToString().ToLowerInvariant();

    static string Short(string sha) => sha[..Math.Min(12, sha.Length)];

    static string Verdict(Evaluation e) => e.Verdict switch
    {
        "ready" => "READY",
        "blocked" => "NOT READY (blocked)",
        _ => "NOT READY (undetermined: a blocking gate could not run)",
    };

    static string Location(Finding f) => f.File is null ? "" : f.Line is { } l ? $"{f.File}:{l}  " : $"{f.File}  ";

    /// <summary>Plain-text report: verdict, then one line per gate with the findings of those that didn't pass.</summary>
    public static string Text(Evaluation e)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PR EVALUATION");
        sb.AppendLine($"PR: {e.Pr.Id}  {e.Pr.Title}");
        sb.AppendLine($"Target: {e.Target} ({Short(e.TargetSha)})");
        sb.AppendLine($"Policy: {e.PolicySource}, profile {e.Profile}, risk {e.Risk} ({string.Join("; ", e.RiskReasons)})");
        sb.AppendLine($"Verdict: {Verdict(e)}");
        sb.AppendLine();
        foreach (var g in e.Gates)
        {
            sb.AppendLine($"{g.Status.ToString().ToUpperInvariant(),-8} {g.Id,-12} {g.Summary}{(g.Blocking ? "" : "  [advisory]")}");
            if (g.Status is GateStatus.Pass or GateStatus.Skipped) continue;
            foreach (var f in g.Findings.Take(10)) sb.AppendLine($"         {Location(f)}{f.Message}");
            if (g.Findings.Count > 10) sb.AppendLine($"         … {g.Findings.Count - 10} more");
        }
        if (!e.Ready) sb.AppendLine().AppendLine("Blocking: " + string.Join(", ", e.Blockers.Select(b => b.Id)));
        return sb.ToString();
    }

    /// <summary>Why the PR is (not) ready: every blocking decision with its evidence, then advisories and passed gates.</summary>
    public static string Explain(Evaluation e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{e.Pr.Id}  {e.Pr.Title}  ->  {e.Target}: {Verdict(e)}");
        sb.AppendLine();
        void Detail(GateResult g, string indent)
        {
            if (g.Command != null) sb.AppendLine($"{indent}command: {g.Command}" + (g.ToolVersion != null ? $"  ({g.ToolVersion})" : ""));
            foreach (var f in g.Findings.Take(20))
            {
                sb.AppendLine($"{indent}- {f.Severity} {Location(f)}{f.Message}" + (f.Rule != null ? $" [{f.Rule}]" : ""));
                if (f.Evidence != null && f.Evidence != f.Message) foreach (var l in f.Evidence.Split('\n')) sb.AppendLine($"{indent}    > {l}");
            }
            if (g.Findings.Count > 20) sb.AppendLine($"{indent}- … {g.Findings.Count - 20} more findings");
            foreach (var ev in g.Evidence) sb.AppendLine($"{indent}evidence: {ev}");
        }
        if (!e.Ready)
        {
            sb.AppendLine("Why it can't merge:");
            int i = 1;
            foreach (var g in e.Blockers)
            {
                sb.AppendLine($"{i++}. {g.Id} ({g.Type}, blocking): {Name(g.Status).ToUpperInvariant()}: {g.Summary}");
                sb.AppendLine("   " + g.Status switch
                {
                    GateStatus.Error => "the gate could not run, so nothing is known about this check; fix the tooling and re-run",
                    GateStatus.Skipped => "not run because a gate it needs did not pass; fix that gate first",
                    _ => "a quality failure: the change needs work",
                });
                Detail(g, "   ");
            }
            sb.AppendLine();
        }
        else sb.AppendLine($"Every blocking gate passed ({e.Gates.Count(g => g.Blocking && g.Status != GateStatus.Skipped)} run).").AppendLine();

        var advisory = e.Gates.Where(g => !g.BlocksMerge && g.Status is GateStatus.Warn or GateStatus.Fail or GateStatus.Error).ToList();
        if (advisory.Count > 0)
        {
            sb.AppendLine("Not blocking:");
            foreach (var g in advisory)
            {
                sb.AppendLine($"- {g.Id}: {Name(g.Status).ToUpperInvariant()}: {g.Summary}" + (g.Blocking ? "" : " (advisory gate)"));
                Detail(g, "  ");
            }
            sb.AppendLine();
        }
        var passed = e.Gates.Where(g => g.Status == GateStatus.Pass).ToList();
        if (passed.Count > 0)
        {
            sb.AppendLine("Passed:");
            foreach (var g in passed) sb.AppendLine($"- {g.Id}: {g.Summary}" + (g.Evidence.Count > 0 ? $" ({string.Join("; ", g.Evidence)})" : ""));
            sb.AppendLine();
        }
        foreach (var g in e.Gates.Where(g => g.Status == GateStatus.Skipped && !g.BlocksMerge)) sb.AppendLine($"Skipped: {g.Id}: {g.Summary}");
        if (e.Trace != null) sb.AppendLine().Append(Traceability.Text(e.Trace)).AppendLine();
        sb.AppendLine($"Decided by: {e.PolicySource}, profile {e.Profile}, risk {e.Risk} ({string.Join("; ", e.RiskReasons)})");
        return sb.ToString();
    }

    /// <summary>Stable JSON (schema gitwizz.evaluation/v1). Fields are only ever added, never renamed or removed.</summary>
    public static string Json(Evaluation e) => JsonSerializer.Serialize(new
    {
        schema = "gitwizz.evaluation/v1",
        version = typeof(Evaluator).Assembly.GetName().Version?.ToString(3),
        pr = new { id = e.Pr.Id, title = e.Pr.Title, headRef = e.Pr.HeadRef, headSha = e.Pr.HeadSha, baseRef = e.Pr.BaseRef },
        target = new { name = e.Target, sha = e.TargetSha },
        provider = e.Provider,
        strategy = e.Strategy.ToString().ToLowerInvariant(),
        policy = new { source = e.PolicySource, profile = e.Profile, risk = new { level = e.Risk, reasons = e.RiskReasons } },
        ready = e.Ready,
        verdict = e.Verdict,
        blockers = e.Blockers.Select(b => b.Id),
        gates = e.Gates.Select(g => new
        {
            id = g.Id, type = g.Type, status = Name(g.Status), blocking = g.Blocking, blocksMerge = g.BlocksMerge, summary = g.Summary,
            findings = g.Findings.Select(f => new { severity = f.Severity, message = f.Message, file = f.File, line = f.Line, rule = f.Rule, evidence = f.Evidence }),
            evidence = g.Evidence,
            durationMs = (long)g.Duration.TotalMilliseconds,
            command = g.Command,
            tool = g.Tool is null ? null : new { name = g.Tool, version = g.ToolVersion },
            log = g.Log,
        }),
        traceability = e.Trace is null ? null : Traceability.Model(e.Trace),
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>Terminal rendering: verdict panel and a gate table; findings for gates that didn't pass.</summary>
    public static void Pretty(Evaluation e, IAnsiConsole c)
    {
        var (color, icon) = e.Verdict switch { "ready" => ("springgreen3", "✔"), "blocked" => ("indianred1", "✘"), _ => ("orange1", "?") };
        c.Write(new Panel(new Markup(
            $"[bold]{Markup.Escape(e.Pr.Id)}[/]  {Markup.Escape(e.Pr.Title)}\n[grey]into[/] {Markup.Escape(e.Target)} [grey]({Short(e.TargetSha)})[/]\n"
            + $"[grey]policy[/] {Markup.Escape(e.PolicySource)}, [grey]profile[/] {Markup.Escape(e.Profile)}, [grey]risk[/] {e.Risk}"))
            .Header($"[bold {color}] {icon} {Markup.Escape(Verdict(e))} [/]").BorderColor(Color.Grey));
        var t = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).AddColumns("Gate", "Status", "Result", "Time");
        foreach (var g in e.Gates)
        {
            var s = g.Status switch
            {
                GateStatus.Pass => "[springgreen3]✔ PASS[/]", GateStatus.Warn => "[gold1]⚠ WARN[/]", GateStatus.Fail => "[indianred1]✘ FAIL[/]",
                GateStatus.Error => "[orange1]! ERROR[/]", _ => "[grey]– SKIP[/]",
            };
            var detail = Markup.Escape(g.Summary) + string.Concat(g.Status is GateStatus.Pass or GateStatus.Skipped ? [] :
                g.Findings.Take(5).Select(f => $"\n[grey]{Markup.Escape(Location(f) + f.Message)}[/]"));
            t.AddRow($"{Markup.Escape(g.Id)}{(g.Blocking ? "" : " [grey](advisory)[/]")}", s, detail, $"[grey]{g.Duration.TotalSeconds:0.0}s[/]");
        }
        c.Write(t);
        if (!e.Ready) c.MarkupLine($"[grey]why:[/] gitwizz explain {Markup.Escape(e.Pr.Id.TrimStart('#'))}");
    }
}
