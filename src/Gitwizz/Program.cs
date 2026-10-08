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
    var opt = Cli.Parse(command == "env" ? rest.SkipWhile(a => !a.StartsWith('-')).ToArray() : subject != null ? rest[1..] : rest, command);
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
    if (command == "guide")
        return Cli.Guide(subject ?? throw new ArgumentException("need a pull request: gitwizz guide <pr>"), opt, err);
    if (command == "trace")
        return Cli.Trace(subject ?? throw new ArgumentException("need a pull request: gitwizz trace <pr>"), opt, err);
    if (command == "context")
        return Cli.Context(subject ?? throw new ArgumentException("need a pull request: gitwizz context <pr>"), opt, err);
    if (command is "evaluate" or "explain")
        return Cli.Evaluate(subject ?? throw new ArgumentException($"need a pull request: gitwizz {command} <pr>"), opt, command == "explain", err);
    if (command != "plan") throw new ArgumentException($"unknown command '{command}' (try: plan, guide, evaluate, explain, trace, context, evidence, benchmark, env, example, help)");
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
    static readonly string[] Common = ["target", "provider", "strategy", "format", "output", "repo"];
    static readonly Dictionary<string, string[]> Options = new()
    {
        ["plan"] = [.. Common, "prs", "all-open", "beam", "history", "verify", "verify-at"],
        ["example"] = [.. Common, "prs", "all-open", "beam", "history", "verify", "verify-at"],
        ["evaluate"] = [.. Common, "profile", "evidence"],
        ["explain"] = [.. Common, "profile", "evidence"],
        ["guide"] = ["target", "provider", "profile", "evidence", "yes", "repo"],
        ["context"] = ["target", "provider", "output", "repo"],
        ["trace"] = [.. Common, "run", "evidence"],
        ["evidence"] = ["target", "provider", "strategy", "output", "repo"],
        ["benchmark"] = ["cases", "baseline", "accept", "format", "output", "repo"],
        ["env"] = ["repo"],
    };
    static readonly string[] Flags = ["all-open", "run", "accept", "yes"];

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
            var plan = BuildPlan(git, provider, target, allOpen && prArgs.Count == 0 ? null : prArgs, chosen, beam, historyDepth, progress);
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
    static Plan BuildPlan(Git git, string provider, string target, List<string>? ids, MergeStrategy? chosen, int beam, int historyDepth, ProgressBars progress)
    {
        progress.Start($"Loading pull requests ({provider})");
        var (policy, targetSha, prs) = LoadPrs(git, provider, target, ids);
        progress.Start($"Analyzing {prs.Count} pull requests", prs.Count);
        Parallel.ForEach(prs, pr => { Analyzer.Analyze(git, targetSha, pr); progress.Advance(); });
        progress.Start("Resolving dependencies");
        Analyzer.ResolveDependencies(git, targetSha, prs);
        if (historyDepth > 0) progress.Start($"Learning from the last {historyDepth} merges", historyDepth);
        var history = Analyzer.ConflictHistory(git, targetSha, historyDepth, progress);

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
    /// recommended steps (the selected tests, then the evaluation) and stops at the verdict. Returns evaluate's exit code.
    /// </summary>
    public static int Guide(string subject, Dictionary<string, string> opt, IAnsiConsole err)
    {
        var git = OpenRepo(opt);
        var evidenceDir = opt.GetValueOrDefault("evidence") is { } ed ? Directory.CreateDirectory(ed).FullName : null;
        var interactive = !opt.ContainsKey("yes") && !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var rerun = "gitwizz guide " + subject + string.Concat(new[] { "target", "provider", "profile", "repo" }
            .Where(opt.ContainsKey).Select(k => $" --{k} {(opt[k].Contains(' ') ? $"\"{opt[k]}\"" : opt[k])}"));
        using var wf = ProgressBars.Show(err, progress => OpenWorkflow(git, opt, subject, evidenceDir, progress));
        var guide = new Guide(wf, new GuideOptions(rerun, opt.GetValueOrDefault("profile"), evidenceDir), AnsiConsole.Console, err,
            interactive ? new SpectrePrompts(AnsiConsole.Console) : null,
            progress => BuildPlan(git, wf.Provider, wf.Target, null, null, 8, 200, progress));
        return guide.Run();
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
    /// checks, policy state, linked work items with acceptance criteria, changed files and commits.
    /// </summary>
    public static int Context(string subject, Dictionary<string, string> opt, IAnsiConsole err)
    {
        var git = OpenRepo(opt);
        var json = ProgressBars.Show(err, progress =>
        {
            using var wf = OpenWorkflow(git, opt, subject, null, progress);
            return Report.Context(git, wf.Pr, wf.Provider, wf.Target, wf.TargetSha);
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
        Analyzer.Analyze(git, targetSha, pr);
        Analyzer.ResolveDependencies(git, targetSha, prs);
        return new PullRequestWorkflow(git, provider, target, targetSha, pr, policy, RepoConfig.Load(git, targetSha), StrategyOption(opt), evidenceDir);
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
        bool details = false)
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
        if (prs.Count == 0)
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
              gitwizz guide <pr> [[options]]     step by step from a PR to its verdict and the next action
              gitwizz evaluate <pr> [[options]]  run the quality gates: is this PR ready to merge?
              gitwizz explain <pr> [[options]]   why a PR is (not) ready, with the evidence
              gitwizz context <pr> [[options]]   the PR's normalized context as JSON (refs, reviews, checks, work items)
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
              [grey]exit code: as evaluate; 4 also when you exit before a verdict. Never merges or changes the PR.[/]

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
              [grey]# try it on a demo repository[/]
              gitwizz example
            """);
    }
}
