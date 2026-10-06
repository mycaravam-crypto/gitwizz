using PrOptimizer;
using Spectre.Console;

const string Usage = """
usage: pr-optimizer plan --target <branch> (--prs <a,b,...> | --all-open)
         [--provider local|github] [--strategy merge|squash|rebase|ff-only]
         [--beam <width>] [--verify <command>] [--format pretty|text|json|html] [--repo <dir>]

  --provider  local: --prs are branches/refs.  github: PR numbers via gh (default when
              --all-open or all --prs are numeric)
  --beam      beam search width (default 8, 1 = greedy)
  --verify    run command in a temporary worktree on the final merged state
  --format    pretty (default on a terminal), text (default when piped), json, html
""";

if (args.Length == 0 || args[0] != "plan") { Console.Error.Write(Usage); return 2; }

var opt = new Dictionary<string, string>();
for (int i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) { Console.Error.Write($"unexpected argument: {args[i]}\n{Usage}"); return 2; }
    var key = args[i][2..];
    opt[key] = key == "all-open" ? "true" : i + 1 < args.Length ? args[++i] : "";
}

try
{
    var git = new Git(opt.GetValueOrDefault("repo", Directory.GetCurrentDirectory()));
    var target = opt.GetValueOrDefault("target", "main");
    var prArgs = opt.GetValueOrDefault("prs", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.TrimStart('#')).ToList();
    var allOpen = opt.ContainsKey("all-open");
    if (prArgs.Count == 0 && !allOpen) throw new ArgumentException("need --prs or --all-open");

    var provider = opt.GetValueOrDefault("provider") ?? (allOpen || prArgs.All(p => p.All(char.IsDigit)) ? "github" : "local");
    var strategy = opt.GetValueOrDefault("strategy", "merge") switch
    {
        "merge" => MergeStrategy.Merge,
        "squash" => MergeStrategy.Squash,
        "rebase" => MergeStrategy.Rebase,
        "ff-only" => MergeStrategy.FfOnly,
        var s => throw new ArgumentException($"unknown strategy: {s}"),
    };
    var beam = int.Parse(opt.GetValueOrDefault("beam", "8"));

    var format = opt.GetValueOrDefault("format") ?? (Console.IsOutputRedirected ? "text" : "pretty");
    if (format is not ("pretty" or "text" or "json" or "html")) throw new ArgumentException($"unknown format: {format}");

    Plan Pipeline(Action<string> status)
    {
        status($"Loading pull requests ({provider})…");
        var (targetSha, prs) = provider switch
        {
            "local" => Providers.Local(git, target, prArgs),
            "github" => Providers.GitHub(git, target, allOpen ? null : prArgs.Select(int.Parse).ToHashSet()),
            _ => throw new ArgumentException($"unknown provider: {provider}"),
        };

        status($"Analyzing {prs.Count} pull requests…");
        Parallel.ForEach(prs, pr => Analyzer.Analyze(git, targetSha, pr));
        Analyzer.ResolveDependencies(git, targetSha, prs);

        status("Simulating merge orders…");
        var plan = new Planner(new Simulator(git, strategy), target, targetSha, prs).Build(beam);

        if (opt.TryGetValue("verify", out var cmd) && cmd is not ("" or "none") && plan.FinalState != null)
        {
            status($"Verifying: {cmd}");
            var (ok, output) = Verify.Run(git, plan.FinalState, cmd);
            plan.Verification = ok ? $"passed ({cmd})" : $"FAILED ({cmd})\n{output.TrimEnd()}";
        }
        return plan;
    }

    if (format == "pretty")
    {
        var plan = AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("steelblue1"))
            .Start("Starting…", ctx => Pipeline(m => ctx.Status(Markup.Escape(m))));
        Pretty.Render(plan, AnsiConsole.Console);
        return plan.Verification?.StartsWith("FAILED") == true ? 1 : 0;
    }

    var result = Pipeline(_ => { });
    Console.Write(format switch { "json" => Report.Json(result) + "\n", "html" => Pretty.Html(result), _ => Report.Text(result) });
    return result.Verification?.StartsWith("FAILED") == true ? 1 : 0;
}
catch (Exception e) when ((e is AggregateException a ? a.InnerException : e) is InvalidOperationException or ArgumentException or FormatException)
{
    Console.Error.WriteLine("error: " + (e is AggregateException a2 ? a2.InnerException! : e).Message);
    return 1;
}
