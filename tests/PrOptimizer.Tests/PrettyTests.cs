using PrOptimizer;
using Spectre.Console;

namespace PrOptimizer.Tests;

public class PrettyTests
{
    static string Render(Plan plan, int width)
    {
        var w = new StringWriter();
        var c = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(w) });
        c.Profile.Width = width;
        Pretty.Render(plan, c);
        return w.ToString();
    }

    static Plan Sample(int extraPrs = 0)
    {
        PullRequest Pr(string id, string title = "") => new() { Id = id, HeadSha = id, Title = title };
        var a = Pr("#1", "[WIP] fix [red]markup[/]");
        var b = Pr("#2");
        var x = Pr("#3");
        b.Dependencies.Add("#1");
        var plan = new Plan { Target = "main", Strategy = MergeStrategy.Squash };
        plan.Steps.Add(new PlanStep(a, 0, "no overlapping changes [x]", []));
        plan.Steps.Add(new PlanStep(b, 0.7, "dependency: #1", ["yarn.lock"]));
        plan.Blocked.Add(new BlockedPr(x, "conflict: [src]/a.cs"));
        plan.Blocked.Add(new BlockedPr(Pr("#4"), "draft", Policy: true));
        plan.Prs = [a, b, x, .. Enumerable.Range(10, extraPrs).Select(i => Pr($"#{i}"))];
        plan.Conflicts = [new ConflictPair("#1", "#3", 0.8), new ConflictPair("#2", "#3", 0.2)];
        plan.Parallelizable = ["#1"];
        plan.Explanations = [new Explanation("#1", "#3", ["A.Run()"], "clean", "conflict")];
        plan.Verification = "FAILED (dotnet test)\nerror [CS0103]";
        return plan;
    }

    [Theory]
    [InlineData(120)]
    [InlineData(60)]
    public void Renders_all_sections_with_escaped_text(int width)
    {
        var o = Render(Sample(), width);
        foreach (var s in new[] { "summary", "merge order", "CLEAN", "REGENERATE", "BLOCKED", "POLICY BLOCKED", "[WIP]", "[src]/a.cs", "dependencies",
                                  "conflict risk", "why #1 before #3?", "A.Run()", "FAILED", "[CS0103]", "parallelizable" })
            Assert.Contains(s, o);
    }

    [Fact]
    public void Shows_risk_levels_not_probabilities()
    {
        var o = Render(Sample(), 120);
        Assert.Contains("high", o);   // #1-#3 weight 0.8
        Assert.DoesNotContain("0.80", o);
        Assert.Equal(["low", "medium", "high"], new[] { 0.2, 0.4, 0.8 }.Select(Report.RiskLevel));
    }

    [Fact]
    public void Many_prs_switch_heatmap_to_bar_chart()
    {
        var o = Render(Sample(extraPrs: 12), 120);
        Assert.Contains("riskiest pairs", o);
        Assert.DoesNotContain("conflict risk", o);
    }
}
