using PrOptimizer;
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
    string? subject = command is "evaluate" or "explain" && rest.Length > 0 && !rest[0].StartsWith('-') ? rest[0] : null;
    var opt = Cli.Parse(subject != null ? rest[1..] : rest, command);
    if (command == "example")
    {
        var dir = opt.GetValueOrDefault("repo") ?? Path.Combine(Path.GetTempPath(), "pr-optimizer-example");
        Example.Create(dir);
        err.MarkupLine($"[grey]example repository:[/] [bold]{Markup.Escape(dir)}[/]\n");
        opt["repo"] = dir;
        opt["all-open"] = "true";
        opt.TryAdd("target", "main");
        var code = Cli.Plan(opt, err);
        err.MarkupLine($"\n[grey]Try it yourself:[/]\n  cd {Markup.Escape(dir)}\n  pr-optimizer plan --all-open --strategy squash\n  pr-optimizer plan -p feature/billing-tax,fix/billing-rounding -f json\n  pr-optimizer plan --all-open -o plan.html");
        return code;
    }
    if (command is "evaluate" or "explain")
        return Cli.Evaluate(subject ?? throw new ArgumentException($"need a pull request: pr-optimizer {command} <pr>"), opt, command == "explain", err);
    if (command != "plan") throw new ArgumentException($"unknown command '{command}' (try: plan, evaluate, explain, example, help)");
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
        ["-o"] = "output", ["-r"] = "repo", ["-a"] = "all-open", ["--all"] = "all-open",
    };
    static readonly string[] Common = ["target", "provider", "strategy", "format", "output", "repo"];
    static readonly Dictionary<string, string[]> Options = new()
    {
        ["plan"] = [.. Common, "prs", "all-open", "beam", "history", "verify", "verify-at"],
        ["example"] = [.. Common, "prs", "all-open", "beam", "history", "verify", "verify-at"],
        ["evaluate"] = [.. Common, "profile", "evidence"],
        ["explain"] = [.. Common, "profile", "evidence"],
    };
    static readonly string[] Flags = ["all-open"];

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
    /// renders or writes the report. Returns 1 if verification failed, else 0.
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
        var timing = Environment.GetEnvironmentVariable("PR_OPT_TIMING") == "1";
        Plan Pipeline(Action<string> status)
        {
            if (timing) status += m => Console.Error.WriteLine($"{sw.ElapsedMilliseconds,6} ms  {m}");
            status($"Loading pull requests ({provider})…");
            var (policy, targetSha, prs) = LoadPrs(git, provider, target, allOpen && prArgs.Count == 0 ? null : prArgs);
            status($"Analyzing {prs.Count} pull requests…");
            Parallel.ForEach(prs, pr => Analyzer.Analyze(git, targetSha, pr));
            Analyzer.ResolveDependencies(git, targetSha, prs);
            if (historyDepth > 0) status($"Learning from the last {historyDepth} merges…");
            var history = Analyzer.ConflictHistory(git, targetSha, historyDepth);

            status($"Simulating merge orders for {prs.Count} pull requests…");
            // No --strategy: plan for what the branch enforces (merge queue method, linear history), else merge.
            var strategy = chosen ?? policy.QueueStrategy ?? (policy.LinearHistory ? MergeStrategy.Squash : MergeStrategy.Merge);
            var plan = new Planner(new Simulator(git, strategy, RepoConfig.Load(git)), target, targetSha, prs, history).Build(beam);
            plan.Provider = provider;
            plan.MergeQueue = policy.MergeQueue;
            // Local branches carry no review/CI/protection data: say so instead of implying "mergeable".
            if (provider == "local")
                plan.Notes.Add("local branches: only structural mergeability is checked (no reviews, checks or branch protection)");
            if (provider == "local" && Providers.BehindRemote(git, target) is > 0 and var behind)
                plan.Notes.Add($"{target} is {behind} commit(s) behind origin/{target}: update it first (git pull on {target}), or the plan misses newer upstream changes");
            if (policy.MergeQueue)
                plan.Notes.Add($"merge queue on {target}: plan only. Enqueue in this order; the queue re-tests and merges"
                    + (policy.QueueStrategy is { } q && q != strategy ? $" (queue merges with {q.ToString().ToLowerInvariant()})" : ""));
            if (policy.LinearHistory && strategy == MergeStrategy.Merge)
                plan.Notes.Add($"{target} requires linear history: merge commits are rejected, use --strategy squash or rebase");
            if (policy.RequiredChecks.Count > 0)
                plan.Notes.Add("required checks: " + string.Join(", ", policy.RequiredChecks.Order()));

            if (opt.TryGetValue("verify", out var cmd) && cmd is not ("" or "none"))
            {
                status($"Verifying ({verifyAt}): {cmd}");
                plan.Verification = Verify.Plan(git, plan, cmd, verifyAt).Summary;
            }
            if (timing) status("done");
            return plan;
        }

        Plan result;
        if (format == "pretty" && output == null)
        {
            result = AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("steelblue1"))
                .Start("Starting…", ctx => Pipeline(m => ctx.Status(Markup.Escape(m))));
            Pretty.Render(result, AnsiConsole.Console);
        }
        else
        {
            result = Pipeline(_ => { });
            Write(format switch { "json" => Report.Json(result) + "\n", "html" => Pretty.Html(result), _ => Report.Text(result) }, format, output, err);
        }
        return result.Verification?.StartsWith("FAILED") == true ? 1 : 0;
    }

    /// <summary>
    /// Runs evaluate (or explain): loads one pull request, runs the quality gates its target's policy selects and
    /// reports the merge-readiness verdict. Returns 0 ready, 3 blocked, 4 undetermined.
    /// </summary>
    public static int Evaluate(string subject, Dictionary<string, string> opt, bool explain, IAnsiConsole err)
    {
        var git = OpenRepo(opt);
        var id = subject.TrimStart('#');
        var provider = ProviderFor(git, opt, [id]);
        var target = opt.GetValueOrDefault("target") ?? DefaultBranch(git);
        var (format, output) = explain ? FormatOption(opt, "text", "text", "json") : FormatOption(opt, "pretty", "pretty", "text", "json");
        var evidenceDir = opt.GetValueOrDefault("evidence") is { } ed ? Directory.CreateDirectory(ed).FullName : null;

        Evaluation Pipeline(Action<string> status)
        {
            status($"Loading pull request {subject} ({provider})…");
            var (policy, targetSha, prs) = LoadPrs(git, provider, target, [id]);
            var pr = prs.Single();
            // Without --target, judge a PR against the branch it targets.
            if (!opt.ContainsKey("target") && pr.BaseRef != "" && pr.BaseRef != target)
            {
                target = pr.BaseRef;
                (policy, targetSha, prs) = LoadPrs(git, provider, target, [id]);
                pr = prs.Single();
            }
            status($"Analyzing {pr.Id}…");
            Analyzer.Analyze(git, targetSha, pr);
            Analyzer.ResolveDependencies(git, targetSha, prs);
            var config = RepoConfig.Load(git, targetSha);
            using var ctx = new GateContext
            {
                Git = git, Pr = pr, Target = target, TargetSha = targetSha, Provider = provider, Config = config,
                Strategy = StrategyOption(opt) ?? policy.QueueStrategy ?? (policy.LinearHistory ? MergeStrategy.Squash : MergeStrategy.Merge),
                Redactor = new Redactor(config.Secrets), EvidenceDir = evidenceDir,
            };
            return Evaluator.Run(ctx, opt.GetValueOrDefault("profile"), status);
        }

        Evaluation result;
        if (format == "pretty" && output == null)
        {
            result = AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("steelblue1"))
                .Start("Starting…", ctx => Pipeline(m => ctx.Status(Markup.Escape(m))));
            Evaluator.Pretty(result, AnsiConsole.Console);
        }
        else
        {
            result = Pipeline(_ => { });
            Write(format == "json" ? Evaluator.Json(result) + "\n" : explain ? Evaluator.Explain(result) : Evaluator.Text(result), format, output, err);
        }
        if (evidenceDir != null) File.WriteAllText(Path.Combine(evidenceDir, "evaluation.json"), Evaluator.Json(result) + "\n");
        return result.ExitCode;
    }

    static Git OpenRepo(Dictionary<string, string> opt)
    {
        var git = new Git(Path.GetFullPath(opt.GetValueOrDefault("repo", ".")));
        if (git.Try("rev-parse", "--git-dir").ExitCode != 0) throw new InvalidOperationException($"not a git repository: {git.RepoDir}");
        return git;
    }

    /// <summary>--provider, else github for PR numbers (or --all-open with a GitHub origin and gh installed), else local.</summary>
    static string ProviderFor(Git git, Dictionary<string, string> opt, List<string> prArgs) =>
        opt.GetValueOrDefault("provider")
        ?? (prArgs.Count > 0 ? (prArgs.All(p => p.All(char.IsDigit)) ? "github" : "local") : IsGitHub(git) ? "github" : "local");

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
    /// Branch policy, target commit and pull requests from the provider. ids null: every open PR (GitHub) or every
    /// unmerged branch (local).
    /// </summary>
    static (BranchPolicy Policy, string TargetSha, List<PullRequest> Prs) LoadPrs(Git git, string provider, string target, List<string>? ids)
    {
        var policy = provider == "github" ? Providers.GitHubPolicy(git, target) : new BranchPolicy([]);
        var (targetSha, prs) = provider switch
        {
            "local" => Providers.Local(git, target, ids ?? LocalBranches(git, target)),
            "github" => Providers.GitHub(git, target, ids?.Select(int.Parse).ToHashSet(), policy),
            _ => throw new ArgumentException($"unknown provider '{provider}' (local, github)"),
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
        : msg.Contains("gh pr list") ? "install the GitHub CLI and run 'gh auth login', or use --provider local"
        : msg.StartsWith("unknown branch") ? "check the name; list branches with 'git branch -a'"
        : msg.Contains("no open pull request ") ? "check the number, or pass a branch name with --provider local"
        : msg.Contains("no open pull requests") ? "use --prs to pick branches/PRs explicitly, or --target for another branch"
        : msg.StartsWith("invalid .gitwizz.yml") ? "fix the file or remove it to use the defaults; see the README section 'Configuration'"
        : msg.StartsWith("unknown option") || msg.StartsWith("need") ? "see 'pr-optimizer help'"
        : null;

    public static void Help(IAnsiConsole c)
    {
        c.MarkupLine("""
            [bold steelblue1]pr-optimizer[/] finds the merge order for pull requests with the fewest conflicts,
            by simulating real git merges. Your working tree and branches are never touched.

            [bold]Usage[/]
              pr-optimizer [grey]plan[/] [[options]]      plan a merge order (default command)
              pr-optimizer evaluate <pr> [[options]]  run the quality gates: is this PR ready to merge?
              pr-optimizer explain <pr> [[options]]   why a PR is (not) ready, with the evidence
              pr-optimizer example [[-r <dir>]]  build a demo repository and plan it
              pr-optimizer help | version

            [bold]Choose pull requests[/]
              -a, --all-open            all open PRs (GitHub) or all unmerged local branches
              -p, --prs <a,b,...>       PR numbers (GitHub) or branch names (local)
              -t, --target <branch>     branch to merge into [grey](default: origin/HEAD, main or master)[/]
                  --provider <name>     local | github [grey](default: auto-detected)[/]

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

            [bold]Output[/]
              -f, --format <name>       pretty | text | json | html [grey](html: plan only; default: pretty on a terminal, text when piped)[/]
              -o, --output <file>       write the report to a file; format from the extension (.html, .json, .txt)
              -r, --repo <dir>          repository directory [grey](default: current directory)[/]

            [bold]Examples[/]
              [grey]# all open GitHub PRs, squash merges[/]
              pr-optimizer --all-open -s squash
              [grey]# specific local branches, verified with the test suite[/]
              pr-optimizer -p feature/a,feature/b --verify "dotnet test"
              [grey]# shareable HTML report[/]
              pr-optimizer --all-open -o plan.html
              [grey]# merge readiness of PR 57 as JSON, logs kept for the audit trail[/]
              pr-optimizer evaluate 57 -f json --evidence .gitwizz-evidence
              [grey]# try it on a demo repository[/]
              pr-optimizer example
            """);
    }
}
