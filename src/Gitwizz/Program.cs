using Gitwizz;
using Spectre.Console;

var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
// Redirected (CI, pipes), Spectre sees width -1 and renders nothing at all: errors would vanish.
if (Console.IsErrorRedirected) err.Profile.Width = 120;
if (Console.IsOutputRedirected) AnsiConsole.Profile.Width = 120;

// Command: first non-option argument; bare options mean "plan".
var command = args.Length == 0 ? "help" : args[0].StartsWith('-') ? "plan" : args[0];
var rest = args.Length > 0 && !args[0].StartsWith('-') ? args[1..] : args;
if (command is "help" or "-h" || rest.Contains("-h") || rest.Contains("--help")) { Cli.Help(AnsiConsole.Console); return 0; }
if (command is "version" || rest.Contains("--version"))
{
    Console.WriteLine(typeof(Cli).Assembly.GetName().Version?.ToString(3));
    return 0;
}

try
{
    // evaluate / explain take the pull request as their first argument.
    string? subject = command is "evaluate" or "explain" or "guide" or "context" or "trace" or "evidence" or "env" && rest.Length > 0 && !rest[0].StartsWith('-') ? rest[0] : null;
    var opt = Cli.Parse(command is "env" or "cache" ? rest.SkipWhile(a => !a.StartsWith('-')).ToArray() : subject != null ? rest[1..] : rest, command);
    if (command == "example")
    {
        var dir = opt.GetValueOrDefault("repo") ?? Path.Combine(Path.GetTempPath(), "gitwizz-example");
        Example.Create(dir);
        err.MarkupLine($"[grey]example repository:[/] [bold]{Markup.Escape(dir)}[/]\n");
        opt["repo"] = dir;
        opt["all-open"] = "true";
        opt.TryAdd("target", "main");
        var code = Cli.Plan(opt, err);
        err.MarkupLine($"\n[grey]Try it yourself:[/]\n  cd {Markup.Escape(dir)}\n  gitwizz plan --all-open --strategy squash\n  gitwizz plan -p feature/billing-tax,fix/billing-rounding -f json\n  gitwizz plan --all-open -o plan.html");
        return code;
    }
    if (command == "benchmark") return Cli.Benchmark(opt, err);
    if (command == "env")
        return Cli.EnvDown(subject is "down" && rest.Length > 1 && !rest[1].StartsWith('-') ? rest[1]
            : throw new ArgumentException("need a pull request: gitwizz env down <pr>"), opt, err);
    if (command == "evidence")
        return Cli.EvidencePackage(subject ?? throw new ArgumentException("need a pull request: gitwizz evidence <pr>"), opt, err);
    if (command == "guide") return subject != null ? Cli.Guide(subject, opt, err) : Cli.RepositoryGuide(opt, err);
    if (command == "status") return Cli.Status(opt, err);
    if (command == "doctor") return Cli.Doctor(opt, err);
    if (command == "refresh") return Cli.Refresh(opt, err);
    if (command == "cache")
        return Cli.Cache(rest.Length > 0 && rest[0] is "status" or "clear" ? rest[0]
            : throw new ArgumentException("need a cache command: gitwizz cache status | gitwizz cache clear"), opt, err);
    if (command == "trace")
        return Cli.Trace(subject ?? throw new ArgumentException("need a pull request: gitwizz trace <pr>"), opt, err);
    if (command == "context")
        return Cli.Context(subject ?? throw new ArgumentException("need a pull request: gitwizz context <pr>"), opt, err);
    if (command is "evaluate" or "explain")
        return Cli.Evaluate(subject ?? throw new ArgumentException($"need a pull request: gitwizz {command} <pr>"), opt, command == "explain", err);
    if (command != "plan") throw new ArgumentException($"unknown command '{command}' (try: plan, guide, doctor, status, refresh, cache, evaluate, explain, trace, context, evidence, benchmark, env, example, help)");
    return Cli.Plan(opt, err);
}
catch (Exception e) when ((e is AggregateException a ? a.InnerException : e) is InvalidOperationException or ArgumentException or FormatException)
{
    var msg = (e is AggregateException a2 ? a2.InnerException! : e).Message;
    err.MarkupLine($"[bold indianred1]✘ error:[/] {Markup.Escape(msg)}");
    if (Cli.Hint(msg) is { } hint) err.MarkupLine($"[grey]  hint: {Markup.Escape(hint)}[/]");
    return msg.StartsWith("unknown") || msg.StartsWith("need") ? 2 : 1;
}

public static partial class Cli
{
    static readonly Dictionary<string, string> Aliases = new()
    {
        ["-t"] = "target", ["-p"] = "prs", ["-s"] = "strategy", ["-b"] = "beam", ["-f"] = "format",
        ["-o"] = "output", ["-r"] = "repo", ["-a"] = "all-open", ["--all"] = "all-open", ["-y"] = "yes",
    };
    static readonly string[] Common = ["target", "provider", "strategy", "format", "output", "repo", "no-cache"];
    static readonly Dictionary<string, string[]> Options = new()
    {
        ["plan"] = [.. Common, "prs", "all-open", "beam", "history", "verify", "verify-at"],
        ["example"] = [.. Common, "prs", "all-open", "beam", "history", "verify", "verify-at"],
        ["evaluate"] = [.. Common, "profile", "evidence"],
        ["explain"] = [.. Common, "profile", "evidence"],
        ["guide"] = ["target", "provider", "profile", "evidence", "yes", "repo", "no-cache"],
        ["status"] = ["target", "provider", "format", "repo"],
        ["doctor"] = ["target", "provider", "profile", "format", "output", "repo"],
        ["refresh"] = ["target", "provider", "all-open", "repo"],
        ["cache"] = ["repo"],
        ["context"] = ["target", "provider", "output", "repo", "no-cache", "system"],
        ["trace"] = [.. Common, "run", "evidence"],
        ["evidence"] = ["target", "provider", "strategy", "output", "repo", "no-cache"],
        ["benchmark"] = ["cases", "baseline", "accept", "format", "output", "repo"],
        ["env"] = ["repo"],
    };
    static readonly string[] Flags = ["all-open", "run", "accept", "yes", "no-cache", "system"];

    /// <summary>
    /// Parses arguments into option → value (flags become "true"), resolving aliases; options the command doesn't take
    /// are errors, and unknown ones suggest a near match.
    /// </summary>
    public static Dictionary<string, string> Parse(string[] args, string command = "plan")
    {
        var options = Options.GetValueOrDefault(command) ?? Options["plan"];
        var opt = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            var key = Aliases.GetValueOrDefault(args[i]) ?? (args[i].StartsWith("--") ? args[i][2..] : null);
            if (key == null || !options.Contains(key))
            {
                if (key != null && Options.Values.Any(o => o.Contains(key)))
                    throw new ArgumentException($"unknown option '{args[i]}' for {command} (it belongs to {string.Join(", ", Options.Where(o => o.Value.Contains(key)).Select(o => o.Key))})");
                var near = options.FirstOrDefault(o => key != null && key.Length >= 2 && (o.StartsWith(key[..2]) || o.Contains(key)));
                throw new ArgumentException($"unknown option '{args[i]}'" + (near != null ? $" (did you mean --{near}?)" : ""));
            }
            if (Flags.Contains(key)) { opt[key] = "true"; continue; }
            if (i + 1 >= args.Length) throw new ArgumentException($"need a value for --{key}");
            opt[key] = args[++i];
        }
        return opt;
    }

    /// <summary>
    /// Runs the plan command: validates options, loads PRs from the provider, analyzes, plans, optionally verifies, and
    /// renders or writes the report. Returns 1 if verification failed, else 0. Throws ArgumentException on invalid
    /// options and InvalidOperationException when the repository, provider or configuration can't be read.
    /// </summary>
    public static int Plan(Dictionary<string, string> opt, IAnsiConsole err)
    {
        var git = OpenRepo(opt);
        var target = opt.GetValueOrDefault("target") ?? DefaultBranch(git);
        var prArgs = opt.GetValueOrDefault("prs", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.TrimStart('#')).ToList();
        var allOpen = opt.ContainsKey("all-open");
        if (prArgs.Count == 0 && !allOpen) throw new ArgumentException("need --prs <a,b,...> or --all-open");

        var provider = ProviderFor(git, opt, prArgs);
        var chosen = StrategyOption(opt);
        var beam = int.TryParse(opt.GetValueOrDefault("beam", "8"), out var bw) && bw > 0
            ? bw : throw new ArgumentException("--beam must be a positive number");
        var verifyAt = opt.GetValueOrDefault("verify-at", "final");
        if (!Verify.Levels.Contains(verifyAt)) throw new ArgumentException($"unknown verify level '{verifyAt}' (final, critical, step)");
        var historyDepth = int.TryParse(opt.GetValueOrDefault("history", "200"), out var hd) && hd >= 0
            ? hd : throw new ArgumentException("--history must be a number of merges (0 = off)");

        var (format, output) = FormatOption(opt, "pretty", "pretty", "text", "json", "html");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Action<string>? timing = Environment.GetEnvironmentVariable("GITWIZZ_TIMING") == "1"
            ? m => Console.Error.WriteLine($"{sw.ElapsedMilliseconds,6} ms  {m}") : null;
        Plan Pipeline(ProgressBars progress)
        {
            var plan = BuildPlan(git, StoreFor(git, opt), provider, target, allOpen && prArgs.Count == 0 ? null : prArgs, chosen, beam, historyDepth, progress);
            if (opt.TryGetValue("verify", out var cmd) && cmd is not ("" or "none"))
            {
                progress.Start($"Verifying ({verifyAt}): {cmd}");
                plan.Verification = Verify.Plan(git, plan, cmd, verifyAt, progress).Summary;
            }
            timing?.Invoke("done");
            return plan;
        }

        var result = ProgressBars.Show(err, Pipeline, timing);
        if (format == "pretty" && output == null) Pretty.Render(result, AnsiConsole.Console);
        else Write(format switch { "json" => Report.Json(result) + "\n", "html" => Pretty.Html(result), _ => Report.Text(result) }, format, output, err);
        return result.Verification?.StartsWith("FAILED") == true ? 1 : 0;
    }

    /// <summary>
    /// Loads, analyzes and plans the pull requests into target (ids null: every open one): the planning path of plan
    /// and guide. chosen null: what the branch enforces, else merge. Never merges anything.
    /// </summary>
    static Plan BuildPlan(Git git, WorkspaceStore store, string provider, string target, List<string>? ids, MergeStrategy? chosen, int beam, int historyDepth,
        ProgressBars progress)
    {
        progress.Start($"Loading pull requests ({provider})");
        var (policy, targetSha, prs) = LoadPrs(git, provider, target, ids);
        progress.Start($"Analyzing {prs.Count} pull requests", prs.Count);
        Parallel.ForEach(prs, pr => { store.Analyze(git, targetSha, pr, provider); progress.Advance(); });
        progress.Start("Resolving dependencies");
        Analyzer.ResolveDependencies(git, targetSha, prs);
        if (historyDepth > 0) progress.Start($"Learning from the last {historyDepth} merges", historyDepth);
        var history = store.ConflictHistory(git, targetSha, historyDepth, progress);

        progress.Start($"Simulating merge orders for {prs.Count} pull requests");
        // No --strategy: plan for what the branch enforces (merge queue method, linear history), else merge.
        var strategy = chosen ?? policy.QueueStrategy ?? (policy.LinearHistory ? MergeStrategy.Squash : MergeStrategy.Merge);
        var plan = new Planner(new Simulator(git, strategy, RepoConfig.Load(git)), target, targetSha, prs, history).Build(beam, progress);
        plan.Provider = provider;
        plan.MergeQueue = policy.MergeQueue;
        // Local branches carry no review/CI/protection data: say so instead of implying "mergeable".
        if (provider == "local")
            plan.Notes.Add("local branches: only structural mergeability is checked (no reviews, checks or branch protection)");
        if (provider == "azure-devops")
            plan.Notes.Add("Azure DevOps: review state from reviewer votes, checks from PR builds; other branch policies aren't visible through ado");
        if (provider == "local" && Providers.BehindRemote(git, target) is > 0 and var behind)
            plan.Notes.Add($"{target} is {behind} commit(s) behind origin/{target}: update it first (git pull on {target}), or the plan misses newer upstream changes");
        if (policy.MergeQueue)
            plan.Notes.Add($"merge queue on {target}: plan only. Enqueue in this order; the queue re-tests and merges"
                + (policy.QueueStrategy is { } q && q != strategy ? $" (queue merges with {q.ToString().ToLowerInvariant()})" : ""));
        if (policy.LinearHistory && strategy == MergeStrategy.Merge)
            plan.Notes.Add($"{target} requires linear history: merge commits are rejected, use --strategy squash or rebase");
        if (policy.RequiredChecks.Count > 0)
            plan.Notes.Add("required checks: " + string.Join(", ", policy.RequiredChecks.Order()));

        return plan;
    }

    /// <summary>
    /// Runs evaluate (or explain): loads one pull request, runs the quality gates its target's policy selects and
    /// reports the merge-readiness verdict. Returns 0 ready, 3 blocked, 4 undetermined.
    /// </summary>
    public static int Evaluate(string subject, Dictionary<string, string> opt, bool explain, IAnsiConsole err)
    {
        var git = OpenRepo(opt);
        var (format, output) = explain ? FormatOption(opt, "text", "text", "json") : FormatOption(opt, "pretty", "pretty", "text", "json");
        var evidenceDir = opt.GetValueOrDefault("evidence") is { } ed ? Directory.CreateDirectory(ed).FullName : null;

        Evaluation Pipeline(ProgressBars progress)
        {
            using var wf = OpenWorkflow(git, opt, subject, evidenceDir, progress);
            return wf.Evaluate(opt.GetValueOrDefault("profile"), progress);
        }

        var result = ProgressBars.Show(err, Pipeline);
        if (format == "pretty" && output == null) Evaluator.Pretty(result, AnsiConsole.Console);
        else Write(format == "json" ? Evaluator.Json(result) + "\n" : explain ? Evaluator.Explain(result) : Evaluator.Text(result), format, output, err);
        if (evidenceDir != null) File.WriteAllText(Path.Combine(evidenceDir, "evaluation.json"), Evaluator.Json(result) + "\n");
        return result.ExitCode;
    }

    /// <summary>
    /// Runs guide: walks one pull request from its context through test impact, tests and the quality gates to a verdict
    /// and the next action. Asks only when stdin and stdout are terminals and --yes is not given; otherwise it runs the
    /// recommended steps (the selected tests, then the evaluation) and stops at the verdict. Checks git, the repository
    /// and the provider before loading the PR. Returns evaluate's exit code, or 7 when that check fails.
    /// </summary>
    public static int Guide(string subject, Dictionary<string, string> opt, IAnsiConsole err)
    {
        var interactive = !opt.ContainsKey("yes") && !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var prompts = interactive ? new SpectrePrompts(AnsiConsole.Console) : null;
        if (GuidePreflight(Path.GetFullPath(opt.GetValueOrDefault("repo", ".")), g => ProviderFor(g, opt, [subject.TrimStart('#')]),
                new DependencyHealthService(), AnsiConsole.Console, err, prompts) is { } stop)
            return stop;
        var git = OpenRepo(opt);
        var evidenceDir = opt.GetValueOrDefault("evidence") is { } ed ? Directory.CreateDirectory(ed).FullName : null;
        var rerun = "gitwizz guide " + subject + string.Concat(new[] { "target", "provider", "profile", "repo" }
            .Where(opt.ContainsKey).Select(k => $" --{k} {(opt[k].Contains(' ') ? $"\"{opt[k]}\"" : opt[k])}"));
        using var wf = ProgressBars.Show(err, progress => OpenWorkflow(git, opt, subject, evidenceDir, progress));
        var guide = new Guide(wf, new GuideOptions(rerun, opt.GetValueOrDefault("profile"), evidenceDir), AnsiConsole.Console, err, prompts,
            progress => BuildPlan(git, wf.Store, wf.Provider, wf.Target, null, null, 8, Repository.HistoryDepth, progress));
        return guide.Run();
    }

    /// <summary>The workspace, unless --no-cache.</summary>
    static WorkspaceStore StoreFor(Git git, Dictionary<string, string> opt) => opt.ContainsKey("no-cache") ? WorkspaceStore.Off : WorkspaceStore.For(git);

    /// <summary>The open PRs into the target, with their linked work items: what status, refresh and the repository guide look at.</summary>
    static (string Provider, string Target, BranchPolicy Policy, string TargetSha, List<PullRequest> Prs) LoadRepository(Git git, Dictionary<string, string> opt,
        ProgressBars progress)
    {
        var provider = ProviderFor(git, opt, []);
        var target = opt.GetValueOrDefault("target") ?? DefaultBranch(git);
        progress.Start($"Loading open pull requests ({provider})");
        var (policy, targetSha, prs) = LoadPrs(git, provider, target, null, details: true, allowEmpty: true);
        return (provider, target, policy, targetSha, prs);
    }

    /// <summary>
    /// Runs status: the repository's open PRs as the workspace sees them (current, stale or missing, with the last
    /// verdicts that still apply), the repository facts and the next suggested action. Computes no analysis.
    /// </summary>
    public static int Status(Dictionary<string, string> opt, IAnsiConsole err)
    {
        var git = OpenRepo(opt);
        var (format, output) = FormatOption(opt, "pretty", "pretty", "text", "json");
        var status = ProgressBars.Show(err, progress =>
        {
            var (provider, target, policy, targetSha, prs) = LoadRepository(git, opt, progress);
            progress.Start("Reading the workspace");
            return Repository.Status(git, WorkspaceStore.For(git), provider, target, policy, targetSha, prs);
        });
        if (format == "json") Write(Repository.Json(status) + "\n", "json", output, err);
        else Repository.Render(status, AnsiConsole.Console);
        return 0;
    }

    /// <summary>
    /// Runs refresh: brings stale repository facts up to date and re-evaluates PRs whose latest evaluation is stale;
    /// --all-open also evaluates PRs never evaluated. Current artifacts are reused, never recomputed.
    /// </summary>
    public static int Refresh(Dictionary<string, string> opt, IAnsiConsole err)
    {
        var git = OpenRepo(opt);
        var store = WorkspaceStore.For(git);
        var (status, done) = ProgressBars.Show(err, progress =>
        {
            var (provider, target, policy, targetSha, prs) = LoadRepository(git, opt, progress);
            var done = Repository.Refresh(git, store, provider, target, policy, targetSha, prs, opt.ContainsKey("all-open"), progress);
            return (Repository.Status(git, store, provider, target, policy, targetSha, prs), done);
        });
        var c = AnsiConsole.Console;
        foreach (var e in done)
            c.MarkupLine($"[grey]evaluated[/] {Markup.Escape(e.Pr.Id),-24} {e.Verdict}");
        if (done.Count == 0) c.MarkupLine("[grey]no stale PR overlay to evaluate[/]" + (opt.ContainsKey("all-open") ? "" : "[grey] (--all-open also evaluates PRs never evaluated)[/]"));
        c.WriteLine();
        Repository.Render(status, c);
        return 0;
    }

    /// <summary>
    /// Runs doctor: checks git, the provider, .gitwizz.yml and what the configured gates rely on, without running a
    /// configured command or contacting a refused AI endpoint; prints dependency health and which gates it affects.
    /// Returns the report's exit code: 0 ok, 5 degraded, 6 verdict at risk, 7 cannot start.
    /// </summary>
    public static int Doctor(Dictionary<string, string> opt, IAnsiConsole err)
    {
        var dir = Path.GetFullPath(opt.GetValueOrDefault("repo", "."));
        var (format, output) = FormatOption(opt, "pretty", "pretty", "text", "json");
        var report = ProgressBars.Show(err, progress => { progress.Start("Checking the environment"); return Diagnose(dir, opt, new DependencyHealthService()); });
        if (format == "json") Write(Health.Json(report) + "\n", "json", output, err);
        else if (format == "pretty" && output == null) AnsiConsole.Console.Markup(Health.Text(report, markup: true));
        else Write(Health.Text(report), format, output, err);
        return report.ExitCode;
    }

    /// <summary>
    /// Both preflight phases for the repository in dir: git and the provider (--provider, else from origin), then the
    /// policy at the target (origin's, else the local branch, else the working tree's .gitwizz.yml) and the
    /// dependencies of the gates its profiles (--profile, else one per risk level) may run.
    /// </summary>
    public static HealthReport Diagnose(string dir, Dictionary<string, string> opt, DependencyHealthService service)
    {
        var given = opt.GetValueOrDefault("provider") is { } p ? (p is "ado" or "azure" ? "azure-devops" : p) : null;
        if (given is not (null or "local" or "github" or "azure-devops")) throw new ArgumentException($"unknown provider '{given}' (local, github, azure-devops)");
        var (deps, provider) = PhaseA(dir, service, git =>
        {
            if (given != null) return given;
            var origin = git.Try("remote", "get-url", "origin").Stdout;
            return AzureDevOps.IsRemote(origin) ? "azure-devops" : origin.Contains("github.com") ? "github" : "local";
        });
        Func<string?> noPromotion = () => null;
        if (deps.Any(d => d.Down && d.Capabilities.Contains(DependencyHealthService.RepositoryAccess))) return DependencyHealthService.Assess(deps, null, [], noPromotion, provider, new Redactor([]));

        var git = new Git(dir);
        var target = opt.GetValueOrDefault("target") ?? DefaultBranch(git);
        var sha = new[] { $"refs/remotes/origin/{target}", target }.Where(r => r != "")
            .Select(r => git.Try("rev-parse", "--verify", "--quiet", r + "^{commit}")).FirstOrDefault(r => r.ExitCode == 0)?.Stdout.Trim();
        RepoConfig? config;
        try { config = sha != null ? RepoConfig.Load(git, sha) : RepoConfig.Load(git); }
        catch (InvalidOperationException e) { deps.Add(DependencyHealthService.InvalidConfiguration(e.Message)); config = null; }
        if (config == null) return DependencyHealthService.Assess(deps, null, [], noPromotion, provider, new Redactor([]));

        var selections = DependencyHealthService.Selections(config, opt.GetValueOrDefault("profile"));
        deps.AddRange(service.ForGates(config, selections.SelectMany(s => s.Gates).DistinctBy(g => g.Id).ToList(), git, sha));
        return DependencyHealthService.Assess(deps, config, selections, () => Promotion.Check(git, sha ?? "HEAD", config), provider, new Redactor(config.Secrets));
    }

    /// <summary>
    /// Preflight phase A, which needs nothing loaded: git and the repository in dir, then (only if both are usable) the
    /// provider that provider picks for the repository. Returns the unredacted checks and the provider ("local" when
    /// git or the repository is down and none was picked).
    /// </summary>
    static (List<DependencyHealth> Deps, string Provider) PhaseA(string dir, DependencyHealthService service, Func<Git, string> provider)
    {
        var deps = service.CheckGit(dir);
        if (deps.Any(d => d.Down)) return (deps, "local");
        var name = provider(new Git(dir));
        if (name is not ("local" or "github" or "azure-devops")) throw new ArgumentException($"unknown provider '{name}' (local, github, azure-devops)");
        deps.Add(service.CheckProvider(name, dir));
        return (deps, name);
    }

    /// <summary>
    /// The guide's preflight phase A, before any PR or repository is loaded: prints the environment (git, the
    /// repository, the provider the guide will use). When one of them is down it prints the fix and, with prompts,
    /// offers Retry (checks again) or Exit; without prompts (--yes, no terminal) it stops at once. Returns null to go
    /// on, else 7 (cannot start). Never retries on its own.
    /// </summary>
    /// <param name="dir">The repository directory (--repo).</param>
    /// <param name="provider">Picks the provider for the repository, as the guide then loads with it.</param>
    /// <param name="service">Runs the checks.</param>
    /// <param name="output">Where the environment section goes, ahead of the guide.</param>
    /// <param name="err">Where progress goes.</param>
    /// <param name="prompts">Asks Retry or Exit; null: don't ask.</param>
    public static int? GuidePreflight(string dir, Func<Git, string> provider, DependencyHealthService service, IAnsiConsole output, IAnsiConsole err,
        IGuidePrompts? prompts)
    {
        while (true)
        {
            var (deps, _) = ProgressBars.Show(err, progress => { progress.Start("Checking git and the provider"); return PhaseA(dir, service, provider); });
            deps = DependencyHealthService.Redact(deps, new Redactor([]));
            output.Markup(Health.Preflight(deps));
            if (!deps.Any(d => d.Essential && d.Down)) return null;
            if (prompts == null) return 7;
            output.WriteLine();
            if (prompts.Choose("Next:", [PreflightAction.Retry, PreflightAction.Exit], a => a == PreflightAction.Retry ? "Retry" : "Exit") == PreflightAction.Exit)
                return 7;
            output.WriteLine();
        }
    }

    /// <summary>Runs cache status (where the workspace is and what it holds) or cache clear (removes it).</summary>
    public static int Cache(string sub, Dictionary<string, string> opt, IAnsiConsole err)
    {
        var git = OpenRepo(opt);
        var store = WorkspaceStore.For(git);
        static string Size(long b) => b < 1024 ? $"{b} B" : b < 1 << 20 ? $"{b / 1024.0:0.#} KiB" : $"{b / 1048576.0:0.#} MiB";
        if (sub == "clear")
        {
            var (files, bytes) = store.Clear();
            err.MarkupLine(files == 0 ? "[grey]workspace already empty[/]" : $"[springgreen3]✔[/] removed {files} file(s), {Size(bytes)}: {Markup.Escape(store.Dir!)}");
            return 0;
        }
        var (n, size) = store.Size();
        var c = AnsiConsole.Console;
        c.MarkupLine($"[bold]Workspace[/]  {Markup.Escape(store.Dir!)}" + (store.Exists ? "" : "  [grey](not created yet)[/]"));
        c.MarkupLine($"  {n} file(s), {Size(size)}");
        c.MarkupLine($"  PR overlays       {store.Count("prs")}");
        c.MarkupLine($"  gate/test results {store.Count("results")}");
        c.MarkupLine($"  repository facts  {store.Count("repo")}");
        c.MarkupLine("[grey]Entries are reused only while their inputs match; see gitwizz status. Remove all: gitwizz cache clear[/]");
        return 0;
    }

    /// <summary>
    /// Runs guide without a PR: the repository summary, then the next action; a chosen PR continues in the PR guide.
    /// Without a terminal (or with --yes) it prints the summary and the recommended command. Checks git, the repository
    /// and the provider before loading anything; returns 7 when that check fails.
    /// </summary>
    public static int RepositoryGuide(Dictionary<string, string> opt, IAnsiConsole err)
    {
        var interactive = !opt.ContainsKey("yes") && !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var prompts = interactive ? new SpectrePrompts(AnsiConsole.Console) : null;
        if (GuidePreflight(Path.GetFullPath(opt.GetValueOrDefault("repo", ".")), g => ProviderFor(g, opt, []), new DependencyHealthService(),
                AnsiConsole.Console, err, prompts) is { } stop)
            return stop;
        var git = OpenRepo(opt);
        var store = StoreFor(git, opt);
        var (provider, target, policy, targetSha, prs) = ProgressBars.Show(err, progress => LoadRepository(git, opt, progress));
        var config = RepoConfig.Load(git, targetSha);
        var evidenceDir = opt.GetValueOrDefault("evidence") is { } ed ? Directory.CreateDirectory(ed).FullName : null;
        int OpenPr(PrStatus p)
        {
            var analysis = ProgressBars.Show(err, progress => { progress.Start($"Analyzing {p.Pr.Id}"); return store.Analyze(git, targetSha, p.Pr, provider); });
            Analyzer.ResolveDependencies(git, targetSha, [p.Pr]);
            using var wf = new PullRequestWorkflow(git, provider, target, targetSha, p.Pr, policy, config, null, evidenceDir, store) { Analysis = analysis };
            return new Guide(wf, new GuideOptions($"gitwizz guide {p.Pr.Id.TrimStart('#')}", opt.GetValueOrDefault("profile"), evidenceDir), AnsiConsole.Console, err,
                prompts, progress => BuildPlan(git, store, provider, target, null, null, 8, Repository.HistoryDepth, progress)).Run();
        }
        return new RepositoryGuide(() => Repository.Status(git, store, provider, target, policy, targetSha, prs), OpenPr,
            progress => Repository.Refresh(git, store, provider, target, policy, targetSha, prs, allOpen: true, progress),
            progress => BuildPlan(git, store, provider, target, null, null, 8, Repository.HistoryDepth, progress), AnsiConsole.Console, err, prompts).Run();
    }

    static Git OpenRepo(Dictionary<string, string> opt)
    {
        var git = new Git(Path.GetFullPath(opt.GetValueOrDefault("repo", ".")));
        if (git.Try("rev-parse", "--git-dir").ExitCode != 0) throw new InvalidOperationException($"not a git repository: {git.RepoDir}");
        return git;
    }

    /// <summary>
    /// --provider, else local for branch names; for PR numbers (or --all-open) azure-devops with an Azure DevOps origin,
    /// github with a GitHub origin (gh installed for --all-open), else local.
    /// </summary>
    static string ProviderFor(Git git, Dictionary<string, string> opt, List<string> prArgs)
    {
        if (opt.GetValueOrDefault("provider") is { } p) return p is "ado" or "azure" ? "azure-devops" : p;
        if (prArgs.Count > 0 && !prArgs.All(x => x.All(char.IsDigit))) return "local";
        if (AzureDevOps.IsRemote(git.Try("remote", "get-url", "origin").Stdout)) return "azure-devops";
        return prArgs.Count > 0 || IsGitHub(git) ? "github" : "local";
    }

    /// <summary>
    /// prints the provider-neutral context of one pull request as JSON (schema gitwizz.context/v1): refs, reviewers,
    /// checks, policy state, linked work items with acceptance criteria, changed files and commits. --system prints the
    /// system context docwizz gives for the change instead (schema gitwizz.system-context/v1).
    /// </summary>
    public static int Context(string subject, Dictionary<string, string> opt, IAnsiConsole err)
    {
        var git = OpenRepo(opt);
        var json = ProgressBars.Show(err, progress =>
        {
            using var wf = OpenWorkflow(git, opt, subject, null, progress);
            if (!opt.ContainsKey("system")) return Report.Context(git, wf.Pr, wf.Provider, wf.Target, wf.TargetSha);
            progress.Start("Reading the system context (docwizz)");
            return SystemContexts.Json(wf.SystemContext(), wf.Pr);
        });
        Write(json + "\n", "json", opt.GetValueOrDefault("output"), err);
        return 0;
    }

    /// <summary>
    /// Requirement-to-test traceability for one pull request: which test suites the change needs and why, and each
    /// acceptance criterion's tests. --run also runs the suites on the merged state. Returns 0, or 3 if a suite failed.
    /// </summary>
    public static int Trace(string subject, Dictionary<string, string> opt, IAnsiConsole err)
    {
        var git = OpenRepo(opt);
        var (format, output) = FormatOption(opt, "text", "text", "json");
        var evidenceDir = opt.GetValueOrDefault("evidence") is { } ed ? Directory.CreateDirectory(ed).FullName : null;
        var (trace, config, target) = ProgressBars.Show(err, progress =>
        {
            using var wf = OpenWorkflow(git, opt, subject, evidenceDir, progress);
            progress.Start($"Simulating the merge into {wf.Target}");
            var trace = wf.BuildTrace();
            if (opt.ContainsKey("run")) wf.RunTests(progress);
            return (trace, wf.Config, wf.Target);
        });
        if (config.Tests.Count == 0) err.MarkupLine($"[grey]no tests: in {RepoConfig.FileName} at {Markup.Escape(target)}: only acceptance criteria are listed[/]");
        Write(format == "json" ? Traceability.Json(trace) + "\n" : Traceability.Text(trace), format, output, err);
        return trace.Runs.Any(r => r.Status != GateStatus.Pass) ? 3 : 0;
    }

    /// <summary>
    /// Removes a PR's test environment (gitwizz env down 42), e.g. one kept with environment.keep or left by an
    /// interrupted run. Idempotent: removing a missing environment succeeds.
    /// </summary>
    public static int EnvDown(string subject, Dictionary<string, string> opt, IAnsiConsole err)
    {
        var git = OpenRepo(opt);
        var config = RepoConfig.Load(git);
        var project = Environments.ProjectName(git.Run("rev-parse", "--show-toplevel"), subject.TrimStart('#'));
        Environments.Create(config.Environment.Provisioner).Down(project);
        err.MarkupLine($"[springgreen3]✔[/] environment [bold]{Markup.Escape(project)}[/] removed");
        return 0;
    }

    /// <summary>
    /// Runs the AI review benchmark: every labelled case against the configured self-hosted model, scored and compared
    /// with the accepted baseline. --accept writes the result as the new baseline (commit it to promote the gate).
    /// Returns 0 when thresholds are met without regressions, else 3. Writes nothing else.
    /// </summary>
    public static int Benchmark(Dictionary<string, string> opt, IAnsiConsole err)
    {
        var git = OpenRepo(opt);
        var root = git.Run("rev-parse", "--show-toplevel");
        var config = RepoConfig.Load(git); // the working tree: measure a configuration before committing it
        if (string.IsNullOrWhiteSpace(config.Review.Model)) throw new InvalidOperationException($"review.model is not set in {RepoConfig.FileName}");
        var (format, output) = FormatOption(opt, "text", "text", "json");
        var cases = Gitwizz.Benchmark.LoadCases(Path.Combine(root, opt.GetValueOrDefault("cases") ?? config.Benchmark.Cases));
        var baselinePath = Path.Combine(root, opt.GetValueOrDefault("baseline") ?? config.Benchmark.Baseline);
        var baseline = File.Exists(baselinePath) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(baselinePath)) : null;
        var results = ProgressBars.Show(err, progress => Gitwizz.Benchmark.Run(cases, config.Review, progress: progress));
        var result = Gitwizz.Benchmark.Result(results, config.Review, config.Benchmark.Thresholds, baseline);
        var json = Gitwizz.Benchmark.Json(result);
        var m = result["metrics"]!;
        var regressions = result["baseline"]?["regressions"]?.AsArray().Select(r => r!.GetValue<string>()).ToList() ?? [];
        var text = $"""
            AI REVIEW BENCHMARK  {config.Review.Model}, prompt {AiReviewGate.PromptVersion}
            Cases: {m["cases"]} ({m["errors"]} errored)
            Precision: {m["precision"]}   Recall: {m["recall"]}   False-positive rate: {m["falsePositiveRate"]}   Evidence rejected: {m["evidenceRejectionRate"]}
            Thresholds: {((bool)result["passed"]! ? "met" : "NOT met: " + string.Join("; ", result["failures"]!.AsArray().Select(f => f!.GetValue<string>())))}
            Baseline: {(baseline is null ? "none" : result["baseline"]!["sameConfiguration"]!.GetValue<bool>() ? "same configuration" : $"other configuration ({baseline["fingerprint"]})")}{(regressions.Count > 0 ? "; regressions: " + string.Join("; ", regressions) : "")}

            """;
        Write(format == "json" ? json + "\n" : text, format, output, err);
        if (opt.ContainsKey("accept"))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            File.WriteAllText(baselinePath, json + "\n");
            err.MarkupLine($"[springgreen3]✔[/] baseline written to [bold]{Markup.Escape(Path.GetRelativePath(root, baselinePath))}[/]; commit it to make it count");
        }
        return (bool)result["passed"]! && regressions.Count == 0 ? 0 : 3;
    }

    /// <summary>
    /// Prints the bounded evidence package an AI review of the PR would get (schema gitwizz.evidence/v1), built on the
    /// merged state, without contacting any model: to audit what leaves the machine, or to review it by other means.
    /// </summary>
    public static int EvidencePackage(string subject, Dictionary<string, string> opt, IAnsiConsole err)
    {
        var git = OpenRepo(opt);
        var json = ProgressBars.Show(err, progress =>
        {
            using var wf = OpenWorkflow(git, opt, subject, null, progress);
            progress.Start($"Building the evidence package on the merge into {wf.Target}");
            return wf.EvidenceJson();
        });
        Write(json + "\n", "json", opt.GetValueOrDefault("output"), err);
        return 0;
    }

    /// <summary>
    /// Loads one pull request (its linked work items too) and analyzes it: the start of every single-PR command.
    /// Without --target, the PR is judged against the branch it targets.
    /// </summary>
    static PullRequestWorkflow OpenWorkflow(Git git, Dictionary<string, string> opt, string subject, string? evidenceDir, ProgressBars? progress)
    {
        var id = subject.TrimStart('#');
        var provider = ProviderFor(git, opt, [id]);
        var target = opt.GetValueOrDefault("target") ?? DefaultBranch(git);
        progress?.Start($"Loading pull request {subject} ({provider})");
        var (policy, targetSha, prs) = LoadOne(git, provider, ref target, id, opt.ContainsKey("target"));
        var pr = prs.Single();
        progress?.Start($"Analyzing {pr.Id}");
        var store = StoreFor(git, opt);
        var analysis = store.Analyze(git, targetSha, pr, provider);
        Analyzer.ResolveDependencies(git, targetSha, prs);
        return new PullRequestWorkflow(git, provider, target, targetSha, pr, policy, RepoConfig.Load(git, targetSha), StrategyOption(opt), evidenceDir, store)
            { Analysis = analysis };
    }

    /// <summary>One pull request with its linked work items; without an explicit target, against the branch it targets.</summary>
    static (BranchPolicy Policy, string TargetSha, List<PullRequest> Prs) LoadOne(Git git, string provider, ref string target, string id, bool targetGiven)
    {
        var loaded = LoadPrs(git, provider, target, [id], details: true);
        var pr = loaded.Prs.Single();
        if (targetGiven || pr.BaseRef == "" || pr.BaseRef == target) return loaded;
        target = pr.BaseRef;
        return LoadPrs(git, provider, target, [id], details: true);
    }

    static MergeStrategy? StrategyOption(Dictionary<string, string> opt) => opt.GetValueOrDefault("strategy") switch
    {
        null => null,
        "merge" => MergeStrategy.Merge,
        "squash" => MergeStrategy.Squash,
        "rebase" => MergeStrategy.Rebase,
        "ff-only" => MergeStrategy.FfOnly,
        var s => throw new ArgumentException($"unknown strategy '{s}' (merge, squash, rebase, ff-only)"),
    };

    /// <summary>--format, else from the --output extension, else the terminal default (text when piped).</summary>
    static (string Format, string? Output) FormatOption(Dictionary<string, string> opt, string terminal, params string[] allowed)
    {
        var output = opt.GetValueOrDefault("output");
        var format = opt.GetValueOrDefault("format")
            ?? (output != null ? Path.GetExtension(output).ToLowerInvariant() switch { ".html" or ".htm" => "html", ".json" => "json", _ => "text" }
                : Console.IsOutputRedirected ? "text" : terminal);
        if (!allowed.Contains(format)) throw new ArgumentException($"unknown format '{format}' ({string.Join(", ", allowed)})");
        return (format, output);
    }

    /// <summary>
    /// Branch policy, target commit and pull requests from the provider. ids null: every open PR (GitHub, Azure DevOps)
    /// or every unmerged branch (local). details: also read each PR's linked work items (meant for a single PR). Throws
    /// ArgumentException for an unknown provider or a non-numeric PR id, InvalidOperationException when nothing is found.
    /// </summary>
    static (BranchPolicy Policy, string TargetSha, List<PullRequest> Prs) LoadPrs(Git git, string provider, string target, List<string>? ids,
        bool details = false, bool allowEmpty = false)
    {
        HashSet<int>? Numbers() => ids?.Select(i => int.TryParse(i, out var n) ? n
            : throw new ArgumentException($"{provider} pull requests are numbers, not '{i}' (use --provider local for branches)")).ToHashSet();
        var policy = provider == "github" ? Providers.GitHubPolicy(git, target) : new BranchPolicy([]);
        var (targetSha, prs) = provider switch
        {
            "local" => Providers.Local(git, target, ids ?? LocalBranches(git, target)),
            "github" => Providers.GitHub(git, target, Numbers(), policy, details),
            "azure-devops" => AzureDevOps.Load(git, target, Numbers(), new AdoCli(git.RepoDir), details),
            _ => throw new ArgumentException($"unknown provider '{provider}' (local, github, azure-devops)"),
        };
        if (prs.Count == 0 && !allowEmpty)
            throw new InvalidOperationException(ids is [var one] ? $"no open pull request {one} found" : $"no open pull requests found for '{target}'");
        return (policy, targetSha, prs);
    }

    static void Write(string text, string format, string? output, IAnsiConsole err)
    {
        if (output == null) { Console.Write(text); return; }
        File.WriteAllText(output, text);
        err.MarkupLine($"[springgreen3]✔[/] wrote {format} report to [bold]{Markup.Escape(output)}[/]");
    }

    /// <summary>origin/HEAD, else main or master, else the current branch.</summary>
    static string DefaultBranch(Git git)
    {
        var head = git.Try("symbolic-ref", "--short", "refs/remotes/origin/HEAD");
        if (head.ExitCode == 0) return head.Stdout.Trim().Replace("origin/", "");
        foreach (var b in new[] { "main", "master" })
            if (git.Try("rev-parse", "--verify", "--quiet", $"refs/heads/{b}").ExitCode == 0) return b;
        return git.Run("branch", "--show-current");
    }

    static bool IsGitHub(Git git) =>
        git.Try("remote", "get-url", "origin").Stdout.Contains("github.com") && Git.Exec(git.RepoDir, "gh", ["--version"]).ExitCode == 0;

    static List<string> LocalBranches(Git git, string target) =>
        git.Run("for-each-ref", "--format=%(refname:short)", "--no-merged", target, "refs/heads")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>A next step to suggest for a known error message, or null.</summary>
    public static string? Hint(string msg) =>
        msg.Contains("not a git repository") ? "run inside a repository or pass --repo <dir>"
        : msg.Contains("docker compose is not available") ? "install Docker with the compose plugin and start the daemon"
        : msg.Contains("not logged in to Azure DevOps") ? "run 'ado auth login <server-url>', or set ADO_SERVER and ADO_PAT"
        : msg.Contains("gh pr list") ? "install the GitHub CLI and run 'gh auth login', or use --provider local"
        : msg.StartsWith("unknown branch") ? "check the name; list branches with 'git branch -a'"
        : msg.Contains("no open pull request ") ? "check the number, or pass a branch name with --provider local"
        : msg.Contains("no open pull requests") ? "use --prs to pick branches/PRs explicitly, or --target for another branch"
        : msg.StartsWith("invalid .gitwizz.yml") ? "fix the file or remove it to use the defaults; see the README section 'Configuration'"
        : msg.StartsWith("unknown option") || msg.StartsWith("need") ? "see 'gitwizz help'"
        : null;

    public static void Help(IAnsiConsole c)
    {
        c.MarkupLine("""
            [bold steelblue1]gitwizz[/] finds the merge order for pull requests with the fewest conflicts, by simulating
            real git merges, and runs quality gates to tell whether a PR is ready. Your working tree and branches are never touched.

            [bold]Usage[/]
              gitwizz [grey]plan[/] [[options]]      plan a merge order (default command)
              gitwizz guide [[<pr>]] [[options]]   the repository's state and next action; with a PR: step by step to its verdict
              gitwizz doctor [[options]]         is the environment ready? git, provider login, docwizz, AI endpoint, Docker, gate tools
              gitwizz status [[options]]         open PRs as the workspace sees them: current, stale or not evaluated
              gitwizz refresh [[--all-open]]     re-evaluate stale PRs (and with --all-open, unevaluated ones); reuses the rest
              gitwizz cache status | clear      where the workspace is and what it holds; remove it
              gitwizz evaluate <pr> [[options]]  run the quality gates: is this PR ready to merge?
              gitwizz explain <pr> [[options]]   why a PR is (not) ready, with the evidence
              gitwizz context <pr> [[options]]   the PR's normalized context as JSON (refs, reviews, checks, work items)
                                                --system: the system context docwizz gives for the change instead
              gitwizz evidence <pr> [[options]]  the bounded evidence package an AI review would see (JSON)
              gitwizz env down <pr>             remove a PR's test environment (idempotent)
              gitwizz benchmark [[options]]      measure AI review quality on labelled cases
              gitwizz trace <pr> [[options]]     which tests the change needs and why; acceptance criteria -> tests -> results
              gitwizz example [[-r <dir>]]  build a demo repository and plan it
              gitwizz help | version

            [bold]Choose pull requests[/]
              -a, --all-open            all open PRs (GitHub, Azure DevOps) or all unmerged local branches
              -p, --prs <a,b,...>       PR numbers (GitHub, Azure DevOps) or branch names (local)
              -t, --target <branch>     branch to merge into [grey](default: origin/HEAD, main or master)[/]
                  --provider <name>     local | github | azure-devops [grey](default: auto-detected from origin)[/]

            [bold]Planning[/]
              -s, --strategy <name>     merge | squash | rebase | ff-only [grey](default: from branch rules, else merge)[/]
              -b, --beam <width>        search width, 1 = greedy [grey](default: 8)[/]
                  --history <merges>    learn file conflict rates from past merges, 0 = off [grey](default: 200)[/]
                  --verify <command>    run a command on merged states, e.g. "dotnet test"
                  --verify-at <level>   final | critical (after high-risk steps) | step [grey](default: final)[/]

            [bold]Evaluate / explain[/]
                  --profile <name>      gate profile from .gitwizz.yml [grey](default: by risk, else "default", else all)[/]
                  --evidence <dir>      keep full gate logs and evaluation.json in dir
              [grey]exit code: 0 ready, 3 blocked by a failed gate, 4 undetermined (a blocking gate could not run)[/]

            [bold]Guide[/]
                  --profile, --evidence as for evaluate
              -y, --yes                 don't ask: run the selected tests and the gates, print the verdict [grey](default without a terminal)[/]
              [grey]checks git, the repository and the provider first, then what the PR's gates rely on (as doctor does)[/]
              [grey]exit code: as evaluate; 4 also when you exit before a verdict;[/]
              [grey]7 when git, the repository or the provider is unusable. Never merges or changes the PR.[/]

            [bold]Doctor[/]
                  --target, --provider, --profile as above; -f pretty | text | json
              [grey]exit code: 0 ok, 5 degraded (advisory checks affected), 6 verdict at risk (a blocking gate can't run), 7 cannot start[/]

            [bold]Workspace[/]
                  --no-cache            neither reuse nor store results (plan, evaluate, explain, guide, trace, context, evidence)
              [grey]stored in .git/gitwizz (or GITWIZZ_WORKSPACE); a result is reused only when every input it came from is unchanged[/]

            [bold]Trace[/]
                  --run                 also run the selected test suites on the merged state [grey](exit 3 if one fails)[/]
                  --evidence <dir>      keep each suite's full log in dir

            [bold]Benchmark[/]
                  --cases <dir>         labelled cases [grey](default: benchmark.cases in .gitwizz.yml, else benchmark)[/]
                  --baseline <file>     accepted baseline [grey](default: benchmark.baseline, else .gitwizz/benchmark-baseline.json)[/]
                  --accept              write the result as the new baseline
              [grey]exit code: 0 thresholds met without regressions, 3 otherwise[/]

            [bold]Output[/]
              -f, --format <name>       pretty | text | json | html [grey](html: plan only; default: pretty on a terminal, text when piped)[/]
              -o, --output <file>       write the report to a file; format from the extension (.html, .json, .txt)
              -r, --repo <dir>          repository directory [grey](default: current directory)[/]

            [bold]Examples[/]
              [grey]# all open GitHub PRs, squash merges[/]
              gitwizz --all-open -s squash
              [grey]# specific local branches, verified with the test suite[/]
              gitwizz -p feature/a,feature/b --verify "dotnet test"
              [grey]# shareable HTML report[/]
              gitwizz --all-open -o plan.html
              [grey]# merge readiness of PR 57 as JSON, logs kept for the audit trail[/]
              gitwizz evaluate 57 -f json --evidence .gitwizz-evidence
              [grey]# walk PR 57 to its verdict and the next action[/]
              gitwizz guide 57
              [grey]# what needs attention in the repository[/]
              gitwizz status
              [grey]# try it on a demo repository[/]
              gitwizz example
            """);
    }
}
