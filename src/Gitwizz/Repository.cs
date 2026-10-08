using System.Text.Json;
using Spectre.Console;

namespace Gitwizz;

/// <summary>
/// One open PR as the workspace sees it. Category: ready, blocked or undetermined when its latest evaluation still
/// applies; stale when an input changed since; missing when it was never evaluated; other when it can't be judged
/// against the repository's target (Skip says why, e.g. stacked on another PR).
/// </summary>
public record PrStatus(PullRequest Pr, ArtifactState Analysis, ArtifactState Evaluation, WorkspaceStore.EvaluationData? Last, string? Skip = null)
{
    public string Category => Skip != null ? "other" : Evaluation.Current ? Last!.Verdict : Evaluation.Status == "stale" ? "stale" : "missing";
}

/// <summary>A repository-level fact the workspace keeps, and whether it is current.</summary>
public record FactStatus(string Name, ArtifactState State);

/// <summary>The repository's state: target, workspace, repository facts and every open PR's overlay.</summary>
public sealed class RepositoryStatus
{
    public required string Name { get; init; }
    public required string Provider { get; init; }
    public required string Target { get; init; }
    public required string TargetSha { get; init; }
    public required bool StoreEnabled { get; init; }
    public required bool Exists { get; init; }
    public required List<FactStatus> Facts { get; init; }
    public required List<PrStatus> Prs { get; init; }

    public static readonly string[] Categories = ["ready", "blocked", "undetermined", "stale", "missing", "other"];

    /// <summary>off (--no-cache), missing (nothing stored yet), current (every fact and open PR current), else stale.</summary>
    public string Workspace => !StoreEnabled ? "off" : !Exists ? "missing"
        : Facts.All(f => f.State.Current) && Prs.All(p => p.Category is "ready" or "blocked" or "undetermined" or "other") ? "current" : "stale";

    public List<PrStatus> In(string category) => Prs.Where(p => p.Category == category).ToList();

    /// <summary>The one thing to do next, most urgent first: stale verdicts, unevaluated PRs, stale facts, blockers, merging.</summary>
    public (string Text, PrStatus? Pr, string Command) Next
    {
        get
        {
            var id = (PrStatus p) => p.Pr.Id.TrimStart('#');
            if (In("stale").FirstOrDefault() is { } s) return ($"PR {s.Pr.Id} needs evaluation: {s.Evaluation.Reason}", s, $"gitwizz guide {id(s)}");
            if (In("missing").FirstOrDefault() is { } m) return ($"PR {m.Pr.Id} has not been evaluated", m, $"gitwizz guide {id(m)}");
            if (StoreEnabled && Facts.Any(f => !f.State.Current)) return ("Refresh the repository facts", null, "gitwizz refresh");
            if (In("undetermined").FirstOrDefault() is { } u) return ($"PR {u.Pr.Id} could not be judged: fix the tooling", u, $"gitwizz guide {id(u)}");
            if (In("blocked").FirstOrDefault() is { } b) return ($"PR {b.Pr.Id} is blocked: see what blocks it", b, $"gitwizz guide {id(b)}");
            if (In("ready").Count > 0) return ($"{In("ready").Count} ready: see the merge order", null, $"gitwizz plan --all-open --target {Target}" + (Provider == "local" ? " --provider local" : ""));
            return (Prs.Count == 0 ? "No open pull requests" : "Nothing to do", null, "");
        }
    }
}

/// <summary>Repository-level workflow: status from the workspace, incremental refresh, and rendering.</summary>
public static class Repository
{
    public const int HistoryDepth = 200;

    /// <summary>The repository's name: the origin URL's last segment, else the directory name.</summary>
    public static string Name(Git git) =>
        git.Try("remote", "get-url", "origin") is { ExitCode: 0 } r && r.Stdout.Trim().TrimEnd('/').Split('/', ':')[^1] is { Length: > 0 } n
            ? n.EndsWith(".git") ? n[..^4] : n
            : Path.GetFileName(git.Run("rev-parse", "--show-toplevel"));

    /// <summary>
    /// What the workspace holds for these PRs, checked against their current inputs. Computes nothing expensive: it
    /// reads stored artifacts and compares fingerprints (re-checking tool versions of the commands evaluations ran).
    /// </summary>
    public static RepositoryStatus Status(Git git, WorkspaceStore store, string provider, string target, BranchPolicy policy, string targetSha,
        List<PullRequest> prs)
    {
        var strategy = PullRequestWorkflow.DefaultStrategy(policy);
        return new RepositoryStatus
        {
            Name = Name(git), Provider = provider, Target = target, TargetSha = targetSha, StoreEnabled = store.Enabled, Exists = store.Exists,
            Facts = [new("conflict history", store.HistoryState(targetSha, HistoryDepth)), new("policy and test topology", store.TopologyState(git, targetSha)),
                .. DocwizzFact(git, store, targetSha)],
            Prs = [.. prs.Select(pr =>
            {
                if (pr.BaseRef != "" && pr.BaseRef != target)
                    return new PrStatus(pr, ArtifactState.Missing, ArtifactState.Missing, null, $"stacked on {pr.BaseRef}: gitwizz guide {pr.Id.TrimStart('#')}");
                var (state, last) = store.EvaluationState(git, provider, pr, targetSha, strategy);
                return new PrStatus(pr, store.AnalysisState(provider, pr, targetSha), state, last);
            })],
        };
    }

    /// <summary>docwizz's repository facts for the target, when docwizz is enabled and installed (otherwise there is nothing to keep current).</summary>
    static IEnumerable<FactStatus> DocwizzFact(Git git, WorkspaceStore store, string targetSha)
    {
        RepoConfig config;
        try { config = RepoConfig.Load(git, targetSha); }
        catch (InvalidOperationException) { yield break; }
        if (SystemContexts.ModelState(git, store, config, targetSha) is { Status: not "off" } state) yield return new("docwizz system model", state);
    }

    /// <summary>
    /// Brings stale repository facts up to date, then evaluates the PRs whose latest evaluation is stale (allOpen: also
    /// those never evaluated). Current overlays are left alone; within an evaluation, unchanged gate results are reused.
    /// </summary>
    public static List<Evaluation> Refresh(Git git, WorkspaceStore store, string provider, string target, BranchPolicy policy, string targetSha,
        List<PullRequest> prs, bool allOpen, ProgressBars? progress = null)
    {
        if (!store.Enabled) throw new InvalidOperationException("refresh needs the workspace: remove --no-cache");
        if (!store.HistoryState(targetSha, HistoryDepth).Current)
        {
            progress?.Start($"Learning from the last {HistoryDepth} merges", HistoryDepth);
            store.ConflictHistory(git, targetSha, HistoryDepth, progress);
        }
        if (!store.TopologyState(git, targetSha).Current) store.RefreshTopology(git, targetSha);
        var config = RepoConfig.Load(git, targetSha);
        if (SystemContexts.ModelState(git, store, config, targetSha) is { Status: "stale" or "missing" })
        {
            progress?.Start("Analyzing the system with docwizz");
            SystemContexts.RefreshModel(git, store, config, targetSha);
        }
        var todo = Status(git, store, provider, target, policy, targetSha, prs).Prs
            .Where(p => p.Category == "stale" || (allOpen && p.Category == "missing")).ToList();
        var done = new List<Evaluation>();
        progress?.Start($"Evaluating {todo.Count} pull requests", todo.Count);
        foreach (var p in todo)
        {
            progress?.Describe($"Evaluating {p.Pr.Id} ({done.Count + 1}/{todo.Count})");
            var analysis = store.Analyze(git, targetSha, p.Pr, provider);
            Analyzer.ResolveDependencies(git, targetSha, [p.Pr]);
            using var wf = new PullRequestWorkflow(git, provider, target, targetSha, p.Pr, policy, config, store: store) { Analysis = analysis };
            done.Add(wf.Evaluate());
            progress?.Advance();
        }
        return done;
    }

    static string Short(string sha) => sha[..Math.Min(7, sha.Length)];

    static string Mark(ArtifactState s) => s.Status switch
    {
        "current" => "[springgreen3]✓ current[/]",
        "stale" => $"[gold1]! stale[/] [grey]({Markup.Escape(s.Reason ?? "")})[/]",
        "off" => "[grey]– off[/]",
        _ => "[grey]– missing[/]",
    };

    /// <summary>The repository summary: target, workspace, PRs by category, repository facts and the next action.</summary>
    public static void Render(RepositoryStatus s, IAnsiConsole c, string heading = "REPOSITORY")
    {
        c.MarkupLine($"[bold steelblue1]{Markup.Escape(heading)}[/]  [bold]{Markup.Escape(s.Name)}[/]");
        c.MarkupLine($"[grey]Target[/]     {Markup.Escape(s.Target)} @ {Short(s.TargetSha)}  [grey]({Markup.Escape(s.Provider)})[/]");
        var ws = s.Workspace switch { "current" => "[springgreen3]current[/]", "stale" => "[gold1]stale[/]", var w => $"[grey]{w}[/]" };
        c.MarkupLine($"[grey]Workspace[/]  {ws}");
        c.WriteLine();
        c.MarkupLine($"[bold]Open PRs[/]   {s.Prs.Count}");
        foreach (var cat in RepositoryStatus.Categories)
        {
            var prs = s.In(cat);
            if (prs.Count == 0) continue;
            var color = cat switch { "ready" => "springgreen3", "blocked" => "indianred1", "undetermined" => "orange1", "stale" => "gold1", _ => "grey" };
            var label = cat switch { "missing" => "not evaluated", "other" => "not judged here", _ => cat };
            c.MarkupLine($"  [{color}]{label,-15}[/] {prs.Count,2}  [grey]{Markup.Escape(string.Join(", ", prs.Take(6).Select(p => p.Pr.Id)) + (prs.Count > 6 ? ", …" : ""))}[/]");
        }
        if (s.StoreEnabled)
        {
            c.WriteLine();
            c.MarkupLine("[bold]Repository facts[/]");
            foreach (var f in s.Facts) c.MarkupLine($"  {Markup.Escape(f.Name),-26} {Mark(f.State)}");
            var stale = s.In("stale");
            if (stale.Count > 0)
            {
                c.WriteLine();
                c.MarkupLine("[bold]Stale[/]");
                foreach (var p in stale.Take(8)) c.MarkupLine($"  {Markup.Escape(p.Pr.Id),-24} [grey]was {p.Last!.Verdict}; {Markup.Escape(p.Evaluation.Reason ?? "")}[/]");
            }
        }
        var (text, _, command) = s.Next;
        c.WriteLine();
        c.MarkupLine($"[bold]Next suggested action:[/] {Markup.Escape(text)}" + (command != "" ? $"\n  [grey]{Markup.Escape(command)}[/]" : ""));
    }

    /// <summary>Stable JSON (schema gitwizz.status/v1).</summary>
    public static string Json(RepositoryStatus s) => JsonSerializer.Serialize(new
    {
        schema = "gitwizz.status/v1",
        repository = s.Name,
        provider = s.Provider,
        target = new { name = s.Target, sha = s.TargetSha },
        workspace = s.Workspace,
        counts = RepositoryStatus.Categories.ToDictionary(c => c, c => s.In(c).Count),
        facts = s.Facts.Select(f => new { name = f.Name, status = f.State.Status, reason = f.State.Reason, computedAt = f.State.ComputedAt }),
        prs = s.Prs.Select(p => new
        {
            id = p.Pr.Id, title = p.Pr.Title, headSha = p.Pr.HeadSha, category = p.Category, skip = p.Skip,
            analysis = new { status = p.Analysis.Status, reason = p.Analysis.Reason, computedAt = p.Analysis.ComputedAt },
            evaluation = new { status = p.Evaluation.Status, reason = p.Evaluation.Reason, computedAt = p.Evaluation.ComputedAt, lastVerdict = p.Last?.Verdict },
        }),
        next = new { text = s.Next.Text, pr = s.Next.Pr?.Pr.Id, command = s.Next.Command },
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

/// <summary>
/// gitwizz guide without a PR: the repository first. Shows the workspace's view of every open PR, then leads to the
/// next meaningful action; a chosen PR continues in the PR guide. Without a terminal it prints the summary and the
/// recommended command and stops.
/// </summary>
public sealed class RepositoryGuide(Func<RepositoryStatus> status, Func<PrStatus, int> openPr, Func<ProgressBars, List<Evaluation>> refresh,
    Func<ProgressBars, Plan>? planner, IAnsiConsole output, IAnsiConsole err, IGuidePrompts? prompts)
{
    /// <summary>A menu entry: an action, optionally about one PR, or a list of PRs to choose from.</summary>
    public record Choice(string Label, string Action, PrStatus? Pr = null, List<PrStatus>? List = null);

    /// <summary>The menu for a status, the recommended action first. Merging is never among them.</summary>
    public static List<Choice> Menu(RepositoryStatus s)
    {
        var menu = new List<Choice>();
        var (_, pr, _) = s.Next;
        if (pr != null)
            menu.Add(new(pr.Category switch { "stale" => $"Review stale PR {pr.Pr.Id}", "missing" => $"Evaluate PR {pr.Pr.Id}", _ => $"Review {pr.Category} PR {pr.Pr.Id}" },
                "pr", pr));
        else if (s.StoreEnabled && s.Facts.Any(f => !f.State.Current)) menu.Add(new("Refresh repository", "refresh"));
        foreach (var (cat, label) in new[] { ("blocked", "Show blocked PRs"), ("undetermined", "Show undetermined PRs"), ("ready", "Show ready PRs") })
            if (s.In(cat) is { Count: > 0 } list) menu.Add(new($"{label} ({list.Count})", "list", List: list));
        if (s.Prs.Where(p => p.Category is "stale" or "missing").ToList() is { Count: > 0 } todo)
            menu.Add(new($"Show PRs needing evaluation ({todo.Count})", "list", List: todo));
        if (s.StoreEnabled && menu.All(m => m.Action != "refresh")) menu.Add(new("Refresh repository", "refresh"));
        if (s.Prs.Count > 0) menu.Add(new("Show merge plan", "plan"));
        menu.Add(new("Exit", "exit"));
        return menu;
    }

    /// <summary>Loops over summary and menu until Exit. Returns 0.</summary>
    public int Run()
    {
        while (true)
        {
            var s = ProgressBars.Show(err, p => { p.Start("Reading the workspace"); return status(); });
            Repository.Render(s, output, "GITWIZZ REPOSITORY GUIDE");
            if (prompts == null) return 0;
            output.WriteLine();
            var choice = prompts.Choose("What do you want to do?", Menu(s), c => c.Label);
            switch (choice.Action)
            {
                case "exit": return 0;
                case "pr": output.WriteLine(); openPr(choice.Pr!); break;
                case "list":
                    var back = new PrStatus(null!, ArtifactState.Missing, ArtifactState.Missing, null);
                    var pick = prompts.Choose("Which pull request?", [.. choice.List!, back],
                        p => p == back ? "Back" : $"{p.Pr.Id}  {p.Pr.Title}" + (p.Category == "stale" ? $"  (stale: {p.Evaluation.Reason})" : ""));
                    if (pick != back) { output.WriteLine(); openPr(pick); }
                    break;
                case "refresh":
                    var done = ProgressBars.Show(err, refresh);
                    output.WriteLine();
                    output.MarkupLine(done.Count == 0 ? "  [grey]– nothing stale to evaluate[/]" : $"  [springgreen3]✓[/] evaluated {done.Count}: "
                        + Markup.Escape(string.Join(", ", done.Select(e => $"{e.Pr.Id} {e.Verdict}"))));
                    break;
                case "plan":
                    if (planner == null) break;
                    try { Guide.RenderPlan(ProgressBars.Show(err, planner), null, output); }
                    catch (Exception ex) when ((ex is AggregateException a ? a.InnerException : ex) is InvalidOperationException or ArgumentException)
                    {
                        output.MarkupLine($"  [indianred1]✗[/] can't plan: {Markup.Escape((ex is AggregateException a2 ? a2.InnerException! : ex).Message)}");
                    }
                    break;
            }
            output.WriteLine();
        }
    }
}
