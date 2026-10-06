using Spectre.Console;
using Spectre.Console.Rendering;

namespace PrOptimizer;

/// <summary>Rich terminal report. Every color is paired with an icon or text, so nothing relies on color alone.</summary>
public static class Pretty
{
    // Calm, high-contrast palette that reads well on dark and light terminals.
    static readonly Color Ok = Color.SpringGreen3, Warn = Color.Gold1, Risk = Color.Orange1, Bad = Color.IndianRed1,
        Accent = Color.SteelBlue1, Muted = Color.Grey;

    const string IconOk = "✔", IconRegen = "⟳", IconBlocked = "✘", IconPolicy = "⚑", Arrow = "➜";
    const int HeatmapMaxPrs = 12;

    static string Hex(Color c) => c.ToMarkup();
    static string Esc(string s) => Markup.Escape(s);
    static string Id(string id) => $"[bold {Hex(Accent)}]{Esc(id)}[/]";

    /// <summary>PRs in plan order (merged, then blocked), so tables read top to bottom like the plan.</summary>
    static List<PullRequest> Ordered(Plan plan) => [.. plan.Steps.Select(s => s.Pr), .. plan.Blocked.Select(b => b.Pr)];

    static Color CostColor(double cost) => cost <= 0 ? Ok : cost < 0.5 ? Warn : cost < 1 ? Risk : Bad;

    /// <summary>The same rich report as a self-contained HTML page (dark background, true colors).</summary>
    public static string Html(Plan plan, int width = 120)
    {
        var c = AnsiConsole.Create(new AnsiConsoleSettings
            { Ansi = AnsiSupport.Yes, ColorSystem = ColorSystemSupport.TrueColor, Out = new AnsiConsoleOutput(TextWriter.Null) });
        c.Profile.Width = width;
        var rec = new Recorder(c);
        Render(plan, rec);
        return $"""
            <!doctype html><html><head><meta charset="utf-8"><title>PR merge plan: {System.Net.WebUtility.HtmlEncode(plan.Target)}</title></head>
            <body style="margin:0;padding:24px;background:#1b1d23;color:#d7dae0;font:14px/1.25 ui-monospace,Menlo,Consolas,monospace">
            {rec.ExportHtml()}
            </body></html>
            """;
    }

    public static void Render(Plan plan, IAnsiConsole c)
    {
        Header(plan, c);
        Flow(plan, c);
        StepsTable(plan, c);
        Dependencies(plan, c);
        Conflicts(plan, c);
        Explanations(plan, c);
        Verification(plan, c);
        if (Report.NextCommand(plan) is { } next)
            c.Write(new Panel(new Markup($"[bold]{Esc(next)}[/]\n[{Hex(Muted)}]then re-run pr-optimizer: the plan is re-evaluated after every real merge[/]"))
                .Header($" next step: merge {Esc(plan.Steps[0].Pr.Id)} ").RoundedBorder().BorderColor(Ok).Expand());
        c.Write(new Rule($"[{Hex(Muted)}]total cost[/] [bold]{plan.TotalCost:0.##}[/]").RuleStyle(Style.Parse("grey")).RightJustified());
    }

    static void Header(Plan plan, IAnsiConsole c)
    {
        if (c.Profile.Width >= 90) c.Write(new FigletText("Merge Plan").Color(Accent));
        else c.Write(new Rule("[bold]PR MERGE PLAN[/]").RuleStyle(Style.Parse("steelblue1")).LeftJustified());

        var regen = plan.Steps.Count(s => s.RegenerateFiles.Count > 0);
        var facts = new Grid().AddColumn().AddColumn().AddColumn().AddColumn()
            .AddRow($"[{Hex(Muted)}]Target[/]", $"[bold]{Esc(plan.Target)}[/]", $"[{Hex(Muted)}]Strategy[/]", $"[bold]{plan.Strategy.ToString().ToLowerInvariant()}[/]")
            .AddRow($"[{Hex(Muted)}]Pull requests[/]", $"[bold]{plan.Steps.Count + plan.Blocked.Count}[/]", $"[{Hex(Muted)}]Total cost[/]", $"[bold]{plan.TotalCost:0.##}[/]");

        var chart = new BreakdownChart().Width(Math.Min(c.Profile.Width - 6, 70)).ShowTagValues();
        if (plan.Steps.Count - regen > 0) chart.AddItem($"{IconOk} clean", plan.Steps.Count - regen, Ok);
        if (regen > 0) chart.AddItem($"{IconRegen} regenerate", regen, Warn);
        var policy = plan.Blocked.Count(b => b.Policy);
        if (plan.Blocked.Count - policy > 0) chart.AddItem($"{IconBlocked} blocked", plan.Blocked.Count - policy, Bad);
        if (policy > 0) chart.AddItem($"{IconPolicy} policy blocked", policy, Risk);

        var notes = plan.Notes.Select(n => (IRenderable)new Markup($"[{Hex(Warn)}]⚑[/] [{Hex(Muted)}]{Esc(n)}[/]"));
        c.Write(new Panel(new Rows([facts, new Text(""), chart, .. notes])).Header(" summary ").RoundedBorder().BorderColor(Muted).Expand());
    }

    static void Flow(Plan plan, IAnsiConsole c)
    {
        var parts = plan.Steps.Select(s => $"[{Hex(s.RegenerateFiles.Count > 0 ? Warn : Ok)}]{Esc(s.Pr.Id)}[/]");
        var flow = string.Join($" [{Hex(Muted)}]{Arrow}[/] ", parts.Prepend($"[{Hex(Muted)}]{Esc(plan.Target)}[/]"));
        if (plan.Blocked.Count > 0)
            flow += $"   [{Hex(Bad)}]{IconBlocked} {string.Join(", ", plan.Blocked.Select(b => Esc(b.Pr.Id)))}[/]";
        if (plan.Parallelizable.Count > 0)
            flow += $"\n[{Hex(Ok)}]⇉ parallelizable now:[/] {string.Join(", ", plan.Parallelizable.Select(Id))}";
        c.Write(new Panel(new Markup(flow)).Header(" merge order ").RoundedBorder().BorderColor(Accent).Expand());
    }

    static string CostBar(double cost, double max)
    {
        const int width = 10;
        var filled = cost <= 0 ? 0 : Math.Max(1, (int)Math.Round(width * cost / max));
        return $"[{Hex(CostColor(cost))}]{new string('█', filled)}[/][{Hex(Muted)}]{new string('░', width - filled)}[/] {cost:0.00}";
    }

    static void StepsTable(Plan plan, IAnsiConsole c)
    {
        // Narrow terminals: fold the reason into the PR cell instead of a squeezed column.
        var compact = c.Profile.Width < 90;
        var t = new Table().RoundedBorder().BorderColor(Muted).Expand()
            .AddColumn(new TableColumn("[bold]#[/]").RightAligned())
            .AddColumn("[bold]Pull request[/]")
            .AddColumn(new TableColumn("[bold]Cost[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]Status[/]").NoWrap());
        if (!compact) t.AddColumn("[bold]Reason[/]");

        void Row(string num, string id, string title, string cost, string status, string reason)
        {
            var pr = $"{id}\n[{Hex(Muted)}]{Esc(title)}[/]";
            if (compact) t.AddRow(num, $"{pr}\n{reason}", cost, status);
            else t.AddRow(num, pr, cost, status, reason);
        }

        var max = Math.Max(1, plan.Steps.Select(s => s.Cost).DefaultIfEmpty(0).Max());
        int i = 1;
        foreach (var s in plan.Steps)
            Row($"{i++}", Id(s.Pr.Id), s.Pr.Title, CostBar(s.Cost, max),
                s.RegenerateFiles.Count > 0 ? $"[{Hex(Warn)}]{IconRegen} REGENERATE[/]" : $"[{Hex(Ok)}]{IconOk} CLEAN[/]",
                $"[{Hex(Muted)}]{Esc(s.Reason)}[/]");
        foreach (var b in plan.Blocked)
            Row($"[{Hex(Bad)}]{i++}[/]", $"[bold {Hex(Bad)}]{Esc(b.Pr.Id)}[/]", b.Pr.Title, $"[{Hex(Bad)}]—[/]",
                b.Policy ? $"[bold {Hex(Risk)}]{IconPolicy} POLICY BLOCKED[/]" : $"[bold {Hex(Bad)}]{IconBlocked} BLOCKED[/]",
                $"[{Hex(b.Policy ? Risk : Bad)}]{Esc(b.Reason)}[/]");

        c.Write(t);
    }

    static void Dependencies(Plan plan, IAnsiConsole c)
    {
        if (!plan.Prs.Any(p => p.Dependencies.Count > 0)) return;
        var blocked = plan.Blocked.Select(b => b.Pr.Id).ToHashSet();
        string Label(PullRequest p) => blocked.Contains(p.Id) ? $"[{Hex(Bad)}]{IconBlocked} {Esc(p.Id)}[/]" : $"[{Hex(Ok)}]{IconOk}[/] {Id(p.Id)}";
        var tree = new Tree($"[bold]{Esc(plan.Target)}[/]").Guide(TreeGuide.Line).Style(Style.Parse("grey"));
        // Dependencies are acyclic (checked in the analyzer), so plain recursion terminates.
        void Add(IHasTreeNodes parent, PullRequest p)
        {
            var node = parent.AddNode(Label(p));
            foreach (var child in plan.Prs.Where(x => x.Dependencies.Contains(p.Id))) Add(node, child);
        }
        foreach (var root in Ordered(plan).Where(p => p.Dependencies.Count == 0)) Add(tree, root);
        c.Write(new Panel(tree).Header(" dependencies ").RoundedBorder().BorderColor(Muted));
    }

    static void Conflicts(Plan plan, IAnsiConsole c)
    {
        if (plan.Conflicts.Count == 0) return;
        var w = plan.Conflicts.ToDictionary(x => (x.A, x.B), x => x.Weight);
        double W(string a, string b) => w.GetValueOrDefault((a, b), w.GetValueOrDefault((b, a)));

        if (plan.Prs.Count > HeatmapMaxPrs)
        {
            // Too many PRs for a readable matrix: show the riskiest pairs instead.
            var bars = new BarChart().Width(Math.Min(c.Profile.Width - 4, 80)).Label("[bold]riskiest pairs[/]");
            foreach (var p in plan.Conflicts.Take(10)) bars.AddItem($"{p.A} ↔ {p.B}", Math.Round(p.Weight, 2), CostColor(p.Weight * 1.5));
            c.Write(new Panel(bars).RoundedBorder().BorderColor(Muted));
            return;
        }

        // Columns are numbered like the plan table (#), so long branch names only appear once, on the rows.
        var ordered = Ordered(plan);
        var t = new Table().RoundedBorder().BorderColor(Muted).Title("[bold]conflict risk[/]").AddColumn("[bold]#[/]").AddColumn("");
        for (int i = 0; i < ordered.Count; i++) t.AddColumn(new TableColumn($"[bold]{i + 1}[/]").Centered());
        for (int i = 0; i < ordered.Count; i++)
        {
            var a = ordered[i];
            t.AddRow(ordered.Select(b => a == b ? $"[{Hex(Muted)}]—[/]"
                : a.Dependencies.Contains(b.Id) || b.Dependencies.Contains(a.Id) ? $"[{Hex(Accent)}]dep[/]"
                : W(a.Id, b.Id) is var x && x <= 0 ? $"[{Hex(Muted)}]·[/]"
                : $"[black on {Hex(CostColor(x * 1.5))}] {Report.RiskLevel(x)} [/]").Prepend(Id(a.Id)).Prepend($"{i + 1}").ToArray());
        }
        c.Write(t);
        c.MarkupLine($"  [{Hex(Muted)}]· none[/]  [{Hex(Accent)}]dep[/] dependency  [black on {Hex(Warn)}] low [/] [black on {Hex(Risk)}] medium [/] [black on {Hex(Bad)}] high [/]");
        c.WriteLine();
    }

    static string Outcome(string o) => o switch
    {
        "clean" => $"[{Hex(Ok)}]{IconOk} clean[/]",
        "regenerate" => $"[{Hex(Warn)}]{IconRegen} regenerate[/]",
        "policy blocked" => $"[{Hex(Risk)}]{IconPolicy} policy blocked[/]",
        _ => $"[{Hex(Bad)}]{IconBlocked} {Esc(o)}[/]",
    };

    static void Explanations(Plan plan, IAnsiConsole c)
    {
        if (plan.Explanations.Count == 0) return;
        var panels = plan.Explanations.Select(e => new Panel(new Markup(
                $"[{Hex(Muted)}]both modify[/] {Esc(string.Join(", ", e.Files))}\n" +
                (e.Members.Count > 0 ? $"[{Hex(Muted)}]shared member[/] {Esc(string.Join(", ", e.Members))}\n" : "") +
                $"{Id(e.A)} {Arrow} {Id(e.B)}  {Outcome(e.AThenB)}\n" +
                $"{Id(e.B)} {Arrow} {Id(e.A)}  {Outcome(e.BThenA)}\n" +
                $"[{Hex(Muted)}]{Esc(e.Reason)}[/]"))
            .Header($" why {Esc(e.A)} before {Esc(e.B)}? ").RoundedBorder().BorderColor(Warn));
        c.Write(new Columns(panels) { Expand = false });
    }

    static void Verification(Plan plan, IAnsiConsole c)
    {
        if (plan.Verification == null) return;
        var ok = !plan.Verification.StartsWith("FAILED");
        var lines = plan.Verification.Split('\n');
        var body = $"[bold {Hex(ok ? Ok : Bad)}]{(ok ? IconOk : IconBlocked)} {Esc(lines[0])}[/]";
        if (lines.Length > 1) body += $"\n[{Hex(Muted)}]{Esc(string.Join('\n', lines.Skip(1).TakeLast(15)))}[/]";
        c.Write(new Panel(new Markup(body)).Header(" verification ").RoundedBorder().BorderColor(ok ? Ok : Bad).Expand());
    }
}
