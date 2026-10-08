using System.Text.Json.Nodes;
using Spectre.Console;

namespace Gitwizz;

/// <summary>
/// Where the guide is: the steps in order, then one of the three verdicts, then done. Environment: what the selected
/// gates rely on (preflight phase B), between the PR context and the system context.
/// </summary>
public enum GuideState { Context, Environment, SystemContext, Trace, Test, Evaluate, Ready, Blocked, Undetermined, Done }

/// <summary>What the developer can do at a step.</summary>
public enum GuideAction
{
    ContinueDegraded, RetryEnvironment, ShowConfiguration,
    ContinueWithContext, ShowContextGraph, ShowMissingContext, RefreshContext, ShowContextHelp,
    RunTests, ShowTrace, SkipTests, ShowPlan, ShowExplanation, InspectEvidence, SaveEvidence, Exit,
}

/// <summary>
/// What the guide knows so far, as far as it decides the next step. Context: the system context's quality (GOOD,
/// PARTIAL, MISSING, STALE); DocwizzAvailable: docwizz could be run at all.
/// </summary>
public record GuideFacts(bool Merges = false, int Selected = 0, bool TestsDeferred = false, string? Verdict = null, string? Context = null,
    bool DocwizzAvailable = true);

/// <summary>Asks the developer; the guide only uses it on a terminal.</summary>
public interface IGuidePrompts
{
    T Choose<T>(string question, IReadOnlyList<T> options, Func<T, string> label) where T : notnull;
    string Ask(string question, string fallback);
}

/// <summary>Arrow-key menus and text prompts on the terminal.</summary>
public sealed class SpectrePrompts(IAnsiConsole console) : IGuidePrompts
{
    public T Choose<T>(string question, IReadOnlyList<T> options, Func<T, string> label) where T : notnull =>
        console.Prompt(new SelectionPrompt<T>().Title(question).AddChoices(options).UseConverter(a => Markup.Escape(label(a))));

    public string Ask(string question, string fallback) =>
        console.Prompt(new TextPrompt<string>(Markup.Escape(question)).DefaultValue(fallback));
}

/// <summary>Everything the guide needs besides the pull request. Rerun: the command line that recomputes the guide.</summary>
public record GuideOptions(string Rerun, string? Profile = null, string? EvidenceDir = null);

/// <summary>
/// gitwizz guide: from "I have a PR" to an evidence-based verdict and the next action. Only orchestrates the workflow
/// the other commands share (context, trace, evaluate, explain, evidence, plan) and never merges or changes anything.
/// It keeps no state between runs: rerunning recomputes everything from the PR's current state.
/// </summary>
/// <param name="health">Checks what the gates rely on; null: the real tools and endpoint.</param>
public sealed class Guide(PullRequestWorkflow wf, GuideOptions options, IAnsiConsole output, IAnsiConsole err, IGuidePrompts? prompts,
    Func<ProgressBars, Plan>? planner = null, DependencyHealthService? health = null)
{
    const int Steps = 6;

    HealthReport? _health;

    SystemContext? _context;
    Trace? _trace;
    Evaluation? _evaluation;
    Dictionary<string, GateSpec>? _gates;
    bool _testsShown;

    /// <summary>The next state after state, given the facts and the developer's choice (null: none was asked for).</summary>
    public static GuideState Next(GuideState state, GuideFacts facts, GuideAction? choice = null) => state switch
    {
        GuideState.Context => GuideState.Environment,
        // Retrying or reading the configuration comes back here; continuing (or nothing to ask) moves on.
        GuideState.Environment => choice switch
        {
            GuideAction.Exit => GuideState.Done,
            GuideAction.RetryEnvironment or GuideAction.ShowConfiguration => GuideState.Environment,
            _ => GuideState.SystemContext,
        },
        // Looking at the context (graph, gaps, refresh) comes back here; anything else moves on.
        GuideState.SystemContext => choice switch
        {
            GuideAction.Exit => GuideState.Done,
            GuideAction.ShowContextGraph or GuideAction.ShowMissingContext or GuideAction.RefreshContext or GuideAction.ShowContextHelp => GuideState.SystemContext,
            _ => GuideState.Trace,
        },
        // Tests run on the merged state, so a PR that doesn't merge goes straight to the verdict.
        GuideState.Trace => facts.Merges && facts.Selected > 0 && !facts.TestsDeferred ? GuideState.Test : GuideState.Evaluate,
        GuideState.Test => choice switch { GuideAction.Exit => GuideState.Done, GuideAction.ShowTrace => GuideState.Test, _ => GuideState.Evaluate },
        GuideState.Evaluate => facts.Verdict switch { "ready" => GuideState.Ready, "blocked" => GuideState.Blocked, _ => GuideState.Undetermined },
        GuideState.Ready or GuideState.Blocked or GuideState.Undetermined => choice is null or GuideAction.Exit ? GuideState.Done : state,
        _ => GuideState.Done,
    };

    /// <summary>The choices at a state, the recommended one first.</summary>
    public static IReadOnlyList<GuideAction> Actions(GuideState state, GuideFacts? facts = null) => state switch
    {
        GuideState.Environment => [GuideAction.ContinueDegraded, GuideAction.RetryEnvironment, GuideAction.ShowConfiguration, GuideAction.Exit],
        GuideState.SystemContext when facts is { DocwizzAvailable: false } => [GuideAction.ContinueWithContext, GuideAction.ShowContextHelp, GuideAction.Exit],
        GuideState.SystemContext when facts?.Context == "MISSING" =>
            [GuideAction.ContinueWithContext, GuideAction.RefreshContext, GuideAction.ShowContextHelp, GuideAction.Exit],
        GuideState.SystemContext =>
            [GuideAction.ContinueWithContext, GuideAction.ShowContextGraph, GuideAction.ShowMissingContext, GuideAction.RefreshContext, GuideAction.Exit],
        GuideState.Test => [GuideAction.RunTests, GuideAction.ShowTrace, GuideAction.SkipTests, GuideAction.Exit],
        GuideState.Ready => [GuideAction.ShowPlan, GuideAction.ShowExplanation, GuideAction.SaveEvidence, GuideAction.Exit],
        GuideState.Blocked or GuideState.Undetermined => [GuideAction.ShowExplanation, GuideAction.InspectEvidence, GuideAction.SaveEvidence, GuideAction.Exit],
        _ => [],
    };

    /// <summary>
    /// What runs without a terminal (or with --yes): continue past missing gate dependencies (the environment section
    /// has said what they mean for the verdict), the tests, then stop at the verdict.
    /// </summary>
    public static GuideAction Default(GuideState state) => state switch
    {
        GuideState.Environment => GuideAction.ContinueDegraded,
        GuideState.Test => GuideAction.RunTests,
        GuideState.SystemContext => GuideAction.ContinueWithContext, // missing context is advisory unless the policy says otherwise
        _ => GuideAction.Exit,
    };

    string Label(GuideAction a) => a switch
    {
        GuideAction.ContinueDegraded => _health?.Overall == OverallHealth.VerdictAtRisk
            ? "Continue anyway (the verdict can be no better than UNDETERMINED)"
            : $"Continue without {string.Join(", ", _health?.Affected.Select(g => g.Id) ?? [])}",
        GuideAction.RetryEnvironment => "Retry the checks",
        GuideAction.ShowConfiguration => "Show configuration",
        GuideAction.ContinueWithContext => _context switch
        {
            { Quality: "MISSING" } => "Continue without docwizz context",
            { Quality: "STALE" } => "Continue with stale system context",
            var c when c?.Docs.Any(d => d.Status != "current") == true => "Continue with reduced documentation trust",
            _ => "Continue with available context",
        },
        GuideAction.ShowContextGraph => "Show affected system graph",
        GuideAction.ShowMissingContext => "Show missing and stale context",
        GuideAction.RefreshContext => "Refresh docwizz analysis",
        GuideAction.ShowContextHelp => "Show installation/configuration help",
        GuideAction.RunTests => "Run selected tests",
        GuideAction.ShowTrace => "Show traceability details",
        GuideAction.SkipTests => "Continue without running",
        GuideAction.ShowPlan => $"Show merge plan for {wf.Target}",
        GuideAction.ShowExplanation => "Show full explanation",
        GuideAction.InspectEvidence => "Inspect AI evidence package",
        GuideAction.SaveEvidence => "Save evidence bundle",
        _ => "Exit",
    };

    GuideFacts Facts => new(wf.Context.MergedState != null, _trace?.Selected.Count ?? 0, TestsDeferred, _evaluation?.Verdict, _context?.Quality,
        _context?.Status != "unavailable");

    /// <summary>Walks the steps to a verdict. Returns evaluate's exit code: 0 ready, 3 blocked, 4 undetermined or no verdict reached.</summary>
    public int Run()
    {
        var state = GuideState.Context;
        while (state != GuideState.Done)
        {
            GuideAction? choice = null;
            switch (state)
            {
                case GuideState.Context: ShowContext(); break;
                case GuideState.Environment:
                    if (_health == null) { _health = CheckEnvironment(); ShowEnvironment(); }
                    if (_health.Affected.Count == 0) break; // nothing for the gates to decide; a missing docwizz context is step 2's
                    choice = Choose(state, "Some gates can't run. Next:");
                    ActOnEnvironment(choice.Value);
                    break;
                case GuideState.SystemContext:
                    if (_context == null) { _context = WithProgress(p => { p.Start("Reading the system context (docwizz)"); return wf.SystemContext(); }); ShowSystemContext(); }
                    if (_context.Quality == "GOOD") break;
                    choice = Choose(state, "Next:");
                    ActOnContext(choice.Value);
                    break;
                case GuideState.Trace: _trace = WithProgress(p => { p.Start($"Simulating the merge into {wf.Target}"); return wf.BuildTrace(); }); ShowTrace(); break;
                case GuideState.Test: choice = Choose(state, "What do you want to do?"); Test(choice.Value); break;
                case GuideState.Evaluate: Evaluate(); break;
                default: choice = Choose(state, "Next:"); Act(choice.Value); break;
            }
            state = Next(state, Facts, choice);
        }
        if (_evaluation != null && options.EvidenceDir != null)
            File.WriteAllText(Path.Combine(options.EvidenceDir, "evaluation.json"), Evaluator.Json(_evaluation) + "\n");
        return _evaluation?.ExitCode ?? 4;
    }

    GuideAction Choose(GuideState state, string question)
    {
        if (prompts == null) return Default(state);
        output.WriteLine();
        return prompts.Choose(question, Actions(state, Facts), Label);
    }

    T WithProgress<T>(Func<ProgressBars, T> work) => ProgressBars.Show(err, work);

    void Step(int n, string title)
    {
        output.WriteLine();
        output.MarkupLine($"[bold]Step {n}/{Steps}[/]  [bold steelblue1]{Markup.Escape(title)}[/]");
    }

    void Line(string icon, string text, string color = "default") => output.MarkupLine($"  [{color}]{icon}[/] {Markup.Escape(text)}");
    void Ok(string text) => Line("✓", text, "springgreen3");
    void Bad(string text) => Line("✗", text, "indianred1");
    void Warn(string text) => Line("!", text, "gold1");
    void Info(string text) => Line("–", text, "grey");

    static string Short(string sha) => sha[..Math.Min(12, sha.Length)];
    static string Plural(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    void ShowContext()
    {
        var pr = wf.Pr;
        var (risk, reasons) = wf.Risk;
        output.MarkupLine("[bold steelblue1]GITWIZZ PR GUIDE[/]");
        output.MarkupLine($"[bold]PR {Markup.Escape(pr.Id)}[/]  {Markup.Escape(pr.Title)}");
        output.MarkupLine($"[grey]From[/] {Markup.Escape(pr.HeadRef is "" ? pr.Id : pr.HeadRef)} [grey]->[/] {Markup.Escape(wf.Target)}");
        output.MarkupLine($"[grey]Provider:[/] {Markup.Escape(wf.Provider)}");
        output.MarkupLine($"[grey]Risk:[/] {risk} [grey]({Markup.Escape(string.Join("; ", reasons))})[/]");

        Step(1, "PR context");
        Ok($"PR loaded (head {Short(pr.HeadSha)})");
        switch (wf.Analysis.Status)
        {
            case "current": Ok($"analysis reused from the workspace ({wf.Analysis.ComputedAt:u})"); break;
            case "stale": Warn($"analysis recomputed: {wf.Analysis.Reason}"); break;
            case "missing": Info("analysis computed and saved to the workspace"); break;
        }
        switch (wf.Previous)
        {
            case ({ Current: true }, { } last): Ok($"last verdict {last.Verdict.ToUpperInvariant()} still applies: re-checking, unchanged gate results are reused"); break;
            case ({ Status: "stale" } st, { } last): Warn($"last verdict {last.Verdict.ToUpperInvariant()} is stale ({st.Reason}): evaluating again"); break;
            case ({ Status: "missing" }, _): Info("not evaluated before"); break;
        }
        if (pr.WorkItems.Count > 0) Ok($"{Plural(pr.WorkItems.Count, "linked work item")}: {string.Join(", ", pr.WorkItems.Select(w => w.Id))}");
        else Warn(wf.Provider == "local" ? "no linked work items (local branches have none)" : "no linked work items");
        if (wf.CriteriaCount > 0) Ok($"{wf.CriteriaCount} acceptance {(wf.CriteriaCount == 1 ? "criterion" : "criteria")}");
        else if (pr.WorkItems.Count > 0) Warn("no acceptance criteria found in the linked work items");
        Ok(Plural(pr.Files.Count, "changed file"));
    }

    /// <summary>
    /// Preflight phase B: the dependencies of the gates this PR's evaluation selects (its profile and risk) and of the
    /// system context, assessed with the evaluator's rule. Checked once per run, again only on Retry.
    /// </summary>
    HealthReport CheckEnvironment() => WithProgress(p =>
    {
        p.Start("Checking what the gates rely on");
        var selection = Evaluator.Select(wf.Config, options.Profile, wf.Risk.Level);
        var deps = (health ?? new DependencyHealthService()).ForGates(wf.Config, selection.Gates, wf.Git, wf.TargetSha);
        return DependencyHealthService.Assess(deps, wf.Config, [selection], () => Promotion.Check(wf.Git, wf.TargetSha, wf.Config), wf.Provider,
            wf.Context.Redactor);
    });

    void ShowEnvironment()
    {
        output.WriteLine();
        output.Markup(Health.GuideSection(_health!));
    }

    void ActOnEnvironment(GuideAction action)
    {
        switch (action)
        {
            case GuideAction.RetryEnvironment:
                _health = CheckEnvironment();
                ShowEnvironment();
                break;
            case GuideAction.ShowConfiguration:
                output.WriteLine();
                output.WriteLine(Health.Configuration(_health!, wf.Config, wf.Target).TrimEnd());
                break;
            case GuideAction.ContinueDegraded:
                Info(_health!.Overall == OverallHealth.VerdictAtRisk ? "continuing: the affected blocking gates will report UNDETERMINED"
                    : "continuing: the affected gates are advisory, the verdict is unaffected");
                break;
        }
    }

    static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    void ShowSystemContext()
    {
        var c = _context!;
        Step(2, "System context");
        if (!c.Usable)
        {
            Warn(c.Status == "unavailable" ? c.Problem ?? "docwizz is not available" : $"docwizz failed: {c.Problem}");
            Info(wf.Config.Context.Docwizz.Required ? $"context.docwizz.required in {RepoConfig.FileName}: the verdict can't be decided without it"
                : "AI review can continue with reduced context");
            output.MarkupLine("  Context quality: [bold orange1]MISSING[/]");
            return;
        }
        output.MarkupLine($"  [bold]Technical context from {Markup.Escape((c.Tool ?? "docwizz").Split(" (")[0])}[/] "
            + $"[grey](facts at {Short(c.AnalyzedCommit)}{(c.Reused ? ", reused from the workspace" : "")})[/]");
        if (c.Freshness == "stale") Warn(c.FreshnessReason ?? "the facts are from an earlier analysis");
        if (!c.DiffAvailable) Warn($"changed symbols taken from the changed lines: {c.Problem}");
        var changed = c.Symbols.Where(s => s.Change != "removed").ToList();
        if (c.Symbols.Count == 0) Info(c.UnanalyzedFiles.Count == 0 ? "no code symbols changed" : "no changed symbol found");
        else Ok($"{Count(c.Symbols.Count, "changed symbol", "changed symbols")} mapped" + (changed.Count(s => s.Change == "added") is > 0 and var added ? $" ({added} new)" : ""));
        var modules = c.Symbols.Select(s => s.Module).Distinct().Count();
        if (modules > 0) Ok(Count(modules, "affected module", "affected modules"));
        int Distinct(string kind, Func<ContextFact, string> key) => c.Of(kind).Where(f => f.Hop == 1).Select(key).Distinct().Count();
        var (callers, callees) = (Distinct("caller", f => f.Subject), Distinct("callee", f => f.Object));
        if (callers + callees > 0) Ok($"{Count(callers, "caller", "callers")}, {Count(callees, "callee", "callees")}");
        if (Distinct("endpoint", f => f.Object) is > 0 and var endpoints) Ok(Count(endpoints, "API endpoint", "API endpoints"));
        if (c.Of("data").Select(f => f.Relation.StartsWith("is ") ? f.Subject : f.Object).Distinct().Count() is > 0 and var data)
            Ok(Count(data, "data entity", "data entities"));
        if (Distinct("external", f => f.Object) is > 0 and var externals)
            Ok(Count(externals, "external system", "external systems") + (c.Of("external").Any(f => f.Origin == "inferred") ? " (some inferred)" : ""));
        if (c.Of("flow", "event").Count() is > 0 and var flows) Ok(Count(flows, "flow", "flows"));
        if (c.Of("caller", "callee").Any(f => f.Hop == 2)) Info("one more dependency hop included: the risk or a requirement justifies it");
        if (c.Symbols.Count > 0 && c.Facts.All(f => f.Kind == "ownership")) Info("docwizz found no relationships of the changed symbols (callers, data, endpoints, external systems)");
        foreach (var f in c.Of("architecture")) Bad($"{f.Subject} -> {f.Object}: {f.Relation}");
        if (c.Unowned.ToList() is { Count: > 0 } unowned) Warn($"{Count(unowned.Count, "changed symbol has", "changed symbols have")} no architecture owner: {string.Join(", ", unowned.Take(3).Select(s => s.Name))}");
        else if (!c.LayersConfigured && c.Symbols.Count > 0) Info("no architecture layers in docwizz.yaml: ownership is by module only");
        foreach (var f in c.UnanalyzedFiles.Take(3)) Warn($"{f} is not analyzed by docwizz");
        if (c.Tests.Count > 0 || c.Unlinked.Count > 0)
            (c.Unlinked.Count == 0 ? (Action<string>)Ok : Warn)($"{Count(c.Tests.Count, "linked test", "linked tests")}" + (c.Unlinked.Count > 0 ? $", {c.Unlinked.Count} changed without a linked test" : ""));

        if (c.Docs.Count > 0)
        {
            output.MarkupLine("  [bold]Documentation[/]");
            foreach (var (status, mark) in new[] { ("current", "✓"), ("stale", "!"), ("missing", "!"), ("unknown", "?") })
                if (c.Docs.Count(d => d.Status == status) is > 0 and var n)
                    output.MarkupLine($"    [{(status == "current" ? "springgreen3" : "gold1")}]{mark}[/] {n} {status}");
            foreach (var d in c.Docs.Where(d => d.Status is "stale" or "missing").Take(4))
                output.MarkupLine($"    [gold1]{d.Status.ToUpperInvariant()}[/] {Markup.Escape(d.Document)}\n      [grey]{Markup.Escape(d.Reason)}[/]");
        }
        if (c.Criteria.Any(x => x.Related.Count > 0))
        {
            output.MarkupLine("  [bold]Requirements and the system they touch[/]");
            foreach (var x in c.Criteria.Where(x => x.Related.Count > 0))
                output.MarkupLine($"    {Markup.Escape(x.Criterion)} [grey]↔[/] {Markup.Escape(string.Join(", ", x.Related.Take(6)))}");
        }
        ShowAiContext(c);
        var color = c.Quality switch { "GOOD" => "springgreen3", "PARTIAL" => "gold1", _ => "orange1" };
        output.MarkupLine($"  Context quality: [bold {color}]{c.Quality}[/]");
        foreach (var r in c.Reasons.Take(5)) output.MarkupLine($"    [grey]- {Markup.Escape(r)}[/]");
        if (c.Reasons.Count > 5) output.MarkupLine($"    [grey]- … {c.Reasons.Count - 5} more (Show missing and stale context)[/]");
    }

    /// <summary>Which context an AI review would get, and whether the budget cuts it.</summary>
    void ShowAiContext(SystemContext c)
    {
        if (!Gates.Values.Any(g => g.Kind == "ai-review"))
        {
            Info("no AI review in this policy: the context is shown here only");
            return;
        }
        var truncated = new List<string>();
        var section = SystemContexts.Evidence(c, SystemContexts.Share(Math.Max(4000, wf.Config.Review.MaxContextChars)), truncated);
        var sent = new[] { "changedSymbols", "callers", "callees", "apiEndpoints", "dataEntities", "externalSystems", "architecture", "flows", "relevantDocs", "linkedTests" }
            .Sum(k => section[k] is JsonArray a ? a.Count : 0);
        Ok($"AI review gets {sent} of {c.Symbols.Count + c.Facts.Count + c.Docs.Count + c.Tests.Count} context items, as data with their provenance");
        if (truncated.Count > 0) Warn($"cut to fit the AI evidence budget: {string.Join(", ", truncated)} (review.max_context_chars)");
    }

    void ActOnContext(GuideAction action)
    {
        var c = _context!;
        switch (action)
        {
            case GuideAction.ShowContextGraph:
                output.WriteLine();
                output.WriteLine(SystemContexts.Text(c).TrimEnd());
                break;
            case GuideAction.ShowMissingContext:
                output.WriteLine();
                output.WriteLine(SystemContexts.Gaps(c, wf.Target).TrimEnd());
                break;
            case GuideAction.RefreshContext:
                _context = WithProgress(p => { p.Start("Running docwizz again"); return wf.SystemContext(refresh: true); });
                ShowSystemContext();
                break;
            case GuideAction.ShowContextHelp:
                output.WriteLine();
                output.WriteLine(SystemContexts.Help(wf.Config.Context.Docwizz).TrimEnd());
                break;
        }
    }

    void ShowTrace()
    {
        var t = _trace!;
        Step(3, "Test impact");
        var merge = wf.Merge();
        if (wf.Context.MergedState != null) Ok(merge.Summary);
        else Bad($"{merge.Summary}: tests can't run on the merged state");
        if (wf.Config.Tests.Count == 0) Info($"no test suites in {RepoConfig.FileName}");
        else if (t.Selected.Count == 0) Info($"none of the {wf.Config.Tests.Count} test suites is affected by this change");
        else output.MarkupLine($"  {Markup.Escape($"{t.Selected.Count} of {Plural(wf.Config.Tests.Count, "suite")} selected: {string.Join(", ", t.Selected.Select(s => s.Suite.Id))}")}");
        if (t.Criteria.Count > 0)
            output.MarkupLine($"  {t.Criteria.Count(c => c.Status != "uncovered")}/{t.Criteria.Count} acceptance criteria linked to a test");
        foreach (var c in t.Criteria.Where(c => c.Status == "uncovered")) Warn($"{c.Id} has no verifying test");
        foreach (var c in t.Criteria.Where(c => c.Status == "manual")) Info($"{c.Id} is verified manually");
    }

    /// <summary>The gates the evaluation will run, by id (throws early on an unknown profile).</summary>
    Dictionary<string, GateSpec> Gates => _gates ??= Evaluator.Select(wf.Config, options.Profile, wf.Risk.Level).Gates.ToDictionary(g => g.Id);

    /// <summary>The evaluation provisions a test environment for the traceability gate: its suites can only run there.</summary>
    bool TestsDeferred
    {
        get
        {
            bool NeedsEnvironment(GateSpec g) => g.Requires.Any(r => Gates.TryGetValue(r, out var n) && (n.Kind == "environment" || NeedsEnvironment(n)));
            return Gates.Values.Any(g => g.Kind == "traceability" && NeedsEnvironment(g));
        }
    }

    /// <summary>The traceability gate is in the selected profile, so the verdict includes the suites' results.</summary>
    bool TestsDecide => Gates.Values.Any(g => g.Kind == "traceability");

    void Test(GuideAction choice)
    {
        if (choice == GuideAction.ShowTrace) { output.WriteLine(); output.WriteLine(Traceability.Text(_trace!).TrimEnd()); return; }
        if (choice == GuideAction.Exit) return;
        Step(4, "Tests");
        _testsShown = true;
        if (choice == GuideAction.SkipTests)
        {
            Info(TestsDecide ? "not run here: the evaluation runs them for its traceability gate" : "not run");
            return;
        }
        WithProgress(p => wf.RunTests(p));
        foreach (var r in _trace!.Runs)
        {
            var text = $"{r.Suite}: {r.Summary} ({r.Duration.TotalSeconds:0.0} s)";
            if (r.Status == GateStatus.Pass) Ok(text);
            else
            {
                Bad(text);
                foreach (var f in r.Findings.Take(3)) output.MarkupLine($"      [grey]{Markup.Escape(f.Message.Split('\n')[0])}[/]");
            }
        }
        foreach (var c in _trace.Criteria.Where(c => c.Status == "failed")) Bad($"{c.Id} failed ({string.Join(", ", c.Tests)})");
        foreach (var c in _trace.Criteria.Where(c => c.Status is "uncovered" or "unknown")) Warn($"{c.Id} remains {c.Status}");
        if (!TestsDecide && _trace.Runs.Count > 0)
            Info("no traceability gate in the policy: these results don't decide the verdict");
    }

    void Evaluate()
    {
        if (!_testsShown)
        {
            Step(4, "Tests");
            Info(wf.Context.MergedState == null ? "can't run: the PR doesn't merge into " + wf.Target
                : TestsDeferred ? "need their test environment: the evaluation provisions it and runs them"
                : (_trace?.Selected.Count ?? 0) == 0 ? "no tests selected for this change"
                : "not run");
        }
        var e = _evaluation = WithProgress(p => wf.Evaluate(options.Profile, p));
        Step(5, "Merge readiness");
        output.MarkupLine($"  [grey]policy {Markup.Escape(e.PolicySource)}, profile {Markup.Escape(e.Profile)}[/]");
        foreach (var g in e.Gates)
        {
            var text = $"{g.Id}: {g.Summary}" + (g.Blocking ? "" : " (advisory)");
            switch (g.Status)
            {
                case GateStatus.Pass: Ok(text); break;
                case GateStatus.Warn: Warn(text); break;
                case GateStatus.Fail when g.BlocksMerge: Bad(text); break;
                case GateStatus.Fail: Warn(text); break;
                case GateStatus.Error: Line("?", text, "orange1"); break;
                default: Info(text); break;
            }
        }
        output.WriteLine();
        var (color, verdict) = e.Verdict switch { "ready" => ("springgreen3", "READY"), "blocked" => ("indianred1", "BLOCKED"), _ => ("orange1", "UNDETERMINED") };
        output.MarkupLine($"[bold {color}]VERDICT: {verdict}[/]");
        WhatNext(e);
    }

    void WhatNext(Evaluation e)
    {
        Step(6, "What next?");
        if (e.Ready)
        {
            Ok($"every blocking gate passed ({e.Gates.Count(g => g.Blocking && g.Status != GateStatus.Skipped)} run)");
            output.MarkupLine("  [bold]Suggested next action:[/]");
            output.MarkupLine(prompts != null ? $"    {Markup.Escape(Label(GuideAction.ShowPlan))} [grey](nothing is merged automatically)[/]"
                : $"    See where it fits in the merge order for {Markup.Escape(wf.Target)}:\n\n      {Markup.Escape(PlanCommand)}");
            return;
        }
        if (e.Verdict == "undetermined")
            output.MarkupLine("  [orange1]Not a verdict on the change:[/] a blocking gate could not run (tool or infrastructure problem).");
        output.MarkupLine(e.Verdict == "undetermined" ? "  [bold]Could not run:[/]" : "  [bold]Blocking reason:[/]");
        foreach (var b in e.Blockers)
        {
            output.MarkupLine($"    [bold]{Markup.Escape(b.Id)}[/]: {Markup.Escape(b.Summary)}");
            if (b.Status == GateStatus.Skipped) continue;
            foreach (var f in b.Findings.Where(f => f.Severity == "error").Take(3))
                output.MarkupLine($"      [grey]{Markup.Escape(f.Message.Split('\n')[0])}[/]");
        }
        output.MarkupLine("  [bold]Suggested next action:[/]");
        foreach (var s in Suggestions(e, wf)) output.MarkupLine($"    {Markup.Escape(s)}");
        output.MarkupLine($"    [grey]then rerun:[/]\n\n      {Markup.Escape(options.Rerun)}");
    }

    string PlanCommand => $"gitwizz plan --all-open --target {wf.Target}" + (wf.Provider != "local" ? "" : " --provider local");

    /// <summary>
    /// What to do about each blocker that the developer can act on: a quality failure names the fix, a gate that could
    /// not run names the tooling. Blockers skipped because a gate they need failed are covered by that gate's advice.
    /// </summary>
    public static List<string> Suggestions(Evaluation e, PullRequestWorkflow wf)
    {
        var list = new List<string>();
        foreach (var b in e.Blockers.Where(b => b.Status is GateStatus.Fail or GateStatus.Error))
        {
            if (b.Status == GateStatus.Error)
            {
                list.Add($"Fix what stopped {b.Id} from running ({b.Summary})" + (Cli.Hint(b.Summary) is { } hint ? $": {hint}" : ""));
                continue;
            }
            var trace = e.Trace;
            list.Add(b.Type switch
            {
                "merge" => $"Bring {wf.Target} into {(wf.Pr.HeadRef is "" ? wf.Pr.Id : wf.Pr.HeadRef)} and resolve the conflicts in "
                    + string.Join(", ", b.Findings.Select(f => f.File).OfType<string>().Take(3)) + ", then push",
                "policy" => $"Resolve the repository policy: {b.Summary}",
                "traceability" when trace?.Runs.Where(r => r.Status != GateStatus.Pass).Select(r => r.Suite).ToList() is { Count: > 0 } failed =>
                    $"Fix the failing test suite{(failed.Count == 1 ? "" : "s")} {string.Join(", ", failed)} (run them with: gitwizz trace {wf.Pr.Id.TrimStart('#')} --run)",
                "traceability" when trace?.Criteria.Where(c => c.Status is "uncovered" or "unknown").Select(c => c.Id).ToList() is { Count: > 0 } open =>
                    $"Add or link a test for {string.Join(", ", open.Take(5))} (tests.<suite>.criteria in {RepoConfig.FileName}, or name the criterion in a test file)",
                "environment" => $"Fix the test environment: {b.Summary}",
                _ => $"Fix {b.Id}: " + (b.Findings.FirstOrDefault(f => f.Severity == "error")?.Message.Split('\n')[0] ?? b.Summary),
            });
        }
        return list.Distinct().ToList();
    }

    void Act(GuideAction action)
    {
        switch (action)
        {
            case GuideAction.ShowExplanation:
                output.WriteLine();
                output.WriteLine(Evaluator.Explain(_evaluation!).TrimEnd());
                break;
            case GuideAction.InspectEvidence: InspectEvidence(); break;
            case GuideAction.SaveEvidence: SaveEvidence(); break;
            case GuideAction.ShowPlan: ShowPlan(); break;
        }
    }

    void InspectEvidence()
    {
        var package = JsonNode.Parse(WithProgress(p => { p.Start("Building the evidence package"); return wf.EvidenceJson(); }))!.AsObject();
        int Count(params string[] path) => path.Aggregate((JsonNode?)package, (n, k) => n?[k]) is JsonArray a ? a.Count : 0;
        output.WriteLine();
        output.MarkupLine($"  [bold]AI evidence package[/] [grey]{Markup.Escape(package["hash"]?.GetValue<string>() ?? "")}[/]");
        output.MarkupLine($"  [grey]on commit[/] {Short(package["commit"]?.GetValue<string>() ?? "")}, "
            + $"{package["budget"]?["usedChars"]} of {package["budget"]?["maxChars"]} characters");
        output.MarkupLine($"  {Plural(Count("requirements"), "requirement")}, {Plural(Count("changes", "files"), "changed file")}, {Plural(Count("diff"), "diff")}, "
            + $"{Plural(Count("callers"), "caller")}, {Plural(Count("rules"), "rule")}, {Plural(Count("docs"), "doc excerpt")}, {Plural(Count("tests"), "selected test")}");
        if (package["systemContext"] is JsonObject sc)
            output.MarkupLine($"  system context ({Markup.Escape(sc["quality"]?.GetValue<string>() ?? "")}): "
                + string.Join(", ", new[] { "changedSymbols", "callers", "callees", "apiEndpoints", "dataEntities", "externalSystems", "flows", "relevantDocs", "linkedTests" }
                    .Where(k => Count("systemContext", k) > 0).Select(k => $"{Count("systemContext", k)} {k}")) is { Length: > 0 } parts ? Markup.Escape(parts) : "none");
        if (Count("budget", "truncated") > 0)
            Warn("cut to fit the budget: " + string.Join(", ", package["budget"]!["truncated"]!.AsArray().Select(t => t!.GetValue<string>())));
        output.MarkupLine($"  [grey]full package (nothing is sent anywhere):[/] gitwizz evidence {Markup.Escape(wf.Pr.Id.TrimStart('#'))}");
    }

    void SaveEvidence()
    {
        var fallback = Path.Combine(".gitwizz-evidence", string.Concat(wf.Pr.Id.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')).Trim('-'));
        var dir = Directory.CreateDirectory(options.EvidenceDir ?? prompts?.Ask("Save to directory", fallback) ?? fallback).FullName;
        File.WriteAllText(Path.Combine(dir, "evaluation.json"), Evaluator.Json(_evaluation!) + "\n");
        File.WriteAllText(Path.Combine(dir, "evidence.json"), WithProgress(p => { p.Start("Building the evidence package"); return wf.EvidenceJson(); }) + "\n");
        Ok($"saved evaluation.json and evidence.json to {Path.GetRelativePath(Directory.GetCurrentDirectory(), dir)}");
        if (options.EvidenceDir == null) Info("gate logs are kept only with --evidence <dir>");
    }

    void ShowPlan()
    {
        output.WriteLine();
        if (planner == null) { Info($"run: {PlanCommand}"); return; }
        try { RenderPlan(WithProgress(planner), wf.Pr.Id, output); }
        catch (Exception ex) when ((ex is AggregateException a ? a.InnerException : ex) is InvalidOperationException or ArgumentException)
        {
            Bad($"can't plan: {(ex is AggregateException a2 ? a2.InnerException! : ex).Message}");
        }
    }

    /// <summary>The merge order in brief, with id's position (null: no PR in focus) and the next merge command, which the user runs.</summary>
    public static void RenderPlan(Plan plan, string? id, IAnsiConsole output)
    {
        var index = id == null ? -1 : plan.Steps.FindIndex(s => s.Pr.Id == id);
        output.MarkupLine($"  [bold]Merge order for {Markup.Escape(plan.Target)}[/] [grey]({Plural(plan.Steps.Count, "mergeable PR")}, {plan.Blocked.Count} blocked)[/]");
        foreach (var (s, i) in plan.Steps.Select((s, i) => (s, i)).Take(Math.Max(index + 1, id == null ? 5 : 3)))
            output.MarkupLine($"    {i + 1}. {(s.Pr.Id == id ? $"[bold]{Markup.Escape(s.Pr.Id)}[/]  [grey]<- this PR[/]" : Markup.Escape(s.Pr.Id))}");
        if (id != null && index < 0 && plan.Blocked.FirstOrDefault(b => b.Pr.Id == id) is { } blocked)
            output.MarkupLine($"  [gold1]![/] {Markup.Escape($"{id} is not in the order: {blocked.Reason}")}");
        if (Report.NextCommand(plan) is { } next)
        {
            output.MarkupLine(index == 0 ? "  [bold]This PR merges next:[/]" : $"  [bold]Merge next:[/] {Markup.Escape(plan.Steps[0].Pr.Id)}");
            output.MarkupLine($"      {Markup.Escape(next)}");
        }
        var command = $"gitwizz plan --all-open --target {plan.Target}" + (plan.Provider == "local" ? " --provider local" : "");
        output.MarkupLine($"  [grey]– gitwizz never merges: run it yourself. Full plan: {Markup.Escape(command)}[/]");
    }
}
