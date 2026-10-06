using PrOptimizer;
using Spectre.Console;

var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

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
    var opt = Cli.Parse(rest);
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
    if (command != "plan") throw new ArgumentException($"unknown command '{command}' (try: plan, example, help)");
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
    static readonly string[] Options = ["target", "prs", "all-open", "provider", "strategy", "beam", "history", "verify", "verify-at", "format", "output", "repo"];
    static readonly string[] Flags = ["all-open"];

    public static Dictionary<string, string> Parse(string[] args)
    {
        var opt = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            var key = Aliases.GetValueOrDefault(args[i]) ?? (args[i].StartsWith("--") ? args[i][2..] : null);
            if (key == null || !Options.Contains(key))
            {
                var near = Options.FirstOrDefault(o => key != null && key.Length >= 2 && (o.StartsWith(key[..2]) || o.Contains(key)));
                throw new ArgumentException($"unknown option '{args[i]}'" + (near != null ? $" (did you mean --{near}?)" : ""));
            }
            if (Flags.Contains(key)) { opt[key] = "true"; continue; }
            if (i + 1 >= args.Length) throw new ArgumentException($"need a value for --{key}");
            opt[key] = args[++i];
        }
        return opt;
    }

    public static int Plan(Dictionary<string, string> opt, IAnsiConsole err)
    {
        var git = new Git(Path.GetFullPath(opt.GetValueOrDefault("repo", ".")));
        if (git.Try("rev-parse", "--git-dir").ExitCode != 0) throw new InvalidOperationException($"not a git repository: {git.RepoDir}");

        var target = opt.GetValueOrDefault("target") ?? DefaultBranch(git);
        var prArgs = opt.GetValueOrDefault("prs", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.TrimStart('#')).ToList();
        var allOpen = opt.ContainsKey("all-open");
        if (prArgs.Count == 0 && !allOpen) throw new ArgumentException("need --prs <a,b,...> or --all-open");

        var provider = opt.GetValueOrDefault("provider")
            ?? (prArgs.Count > 0 ? (prArgs.All(p => p.All(char.IsDigit)) ? "github" : "local") : IsGitHub(git) ? "github" : "local");
        MergeStrategy? chosen = opt.GetValueOrDefault("strategy") switch
        {
            null => null,
            "merge" => MergeStrategy.Merge,
            "squash" => MergeStrategy.Squash,
            "rebase" => MergeStrategy.Rebase,
            "ff-only" => MergeStrategy.FfOnly,
            var s => throw new ArgumentException($"unknown strategy '{s}' (merge, squash, rebase, ff-only)"),
        };
        var beam = int.TryParse(opt.GetValueOrDefault("beam", "8"), out var bw) && bw > 0
            ? bw : throw new ArgumentException("--beam must be a positive number");
        var verifyAt = opt.GetValueOrDefault("verify-at", "final");
        if (!Verify.Levels.Contains(verifyAt)) throw new ArgumentException($"unknown verify level '{verifyAt}' (final, critical, step)");
        var historyDepth = int.TryParse(opt.GetValueOrDefault("history", "200"), out var hd) && hd >= 0
            ? hd : throw new ArgumentException("--history must be a number of merges (0 = off)");

        var output = opt.GetValueOrDefault("output");
        var format = opt.GetValueOrDefault("format")
            ?? (output != null ? Path.GetExtension(output).ToLowerInvariant() switch { ".html" or ".htm" => "html", ".json" => "json", _ => "text" }
                : Console.IsOutputRedirected ? "text" : "pretty");
        if (format is not ("pretty" or "text" or "json" or "html"))
            throw new ArgumentException($"unknown format '{format}' (pretty, text, json, html)");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timing = Environment.GetEnvironmentVariable("PR_OPT_TIMING") == "1";
        Plan Pipeline(Action<string> status)
        {
            if (timing) status += m => Console.Error.WriteLine($"{sw.ElapsedMilliseconds,6} ms  {m}");
            status($"Loading pull requests ({provider})…");
            var policy = provider == "github" ? Providers.GitHubPolicy(git, target) : new BranchPolicy([]);
            var (targetSha, prs) = provider switch
            {
                "local" => Providers.Local(git, target, allOpen && prArgs.Count == 0 ? LocalBranches(git, target) : prArgs),
                "github" => Providers.GitHub(git, target, allOpen ? null : prArgs.Select(int.Parse).ToHashSet(), policy),
                _ => throw new ArgumentException($"unknown provider '{provider}' (local, github)"),
            };
            if (prs.Count == 0) throw new InvalidOperationException($"no open pull requests found for '{target}'");

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
            var text = format switch { "json" => Report.Json(result) + "\n", "html" => Pretty.Html(result), _ => Report.Text(result) };
            if (output == null) Console.Write(text);
            else
            {
                File.WriteAllText(output, text);
                err.MarkupLine($"[springgreen3]✔[/] wrote {format} report to [bold]{Markup.Escape(output)}[/]");
            }
        }
        return result.Verification?.StartsWith("FAILED") == true ? 1 : 0;
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
        git.Try("remote", "get-url", "origin").Stdout.Contains("github.com") && Git.Exec(git.RepoDir, "sh", ["-c", "command -v gh"]).ExitCode == 0;

    static List<string> LocalBranches(Git git, string target) =>
        git.Run("for-each-ref", "--format=%(refname:short)", "--no-merged", target, "refs/heads")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    public static string? Hint(string msg) =>
        msg.Contains("not a git repository") ? "run inside a repository or pass --repo <dir>"
        : msg.Contains("gh pr list") ? "install the GitHub CLI and run 'gh auth login', or use --provider local"
        : msg.StartsWith("unknown branch") ? "check the name; list branches with 'git branch -a'"
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

            [bold]Output[/]
              -f, --format <name>       pretty | text | json | html [grey](default: pretty on a terminal, text when piped)[/]
              -o, --output <file>       write the report to a file; format from the extension (.html, .json, .txt)
              -r, --repo <dir>          repository directory [grey](default: current directory)[/]

            [bold]Examples[/]
              [grey]# all open GitHub PRs, squash merges[/]
              pr-optimizer --all-open -s squash
              [grey]# specific local branches, verified with the test suite[/]
              pr-optimizer -p feature/a,feature/b --verify "dotnet test"
              [grey]# shareable HTML report[/]
              pr-optimizer --all-open -o plan.html
              [grey]# try it on a demo repository[/]
              pr-optimizer example
            """);
    }
}
