using Spectre.Console;

namespace Gitwizz;

/// <summary>Terminal-only, evidence-based presentation of file and directory inspection reports.</summary>
public static class InspectPretty
{
    public static void Directory(FileInspect.DirectoryResult report, IAnsiConsole console)
    {
        console.Write(new Rule("[steelblue1]File history · " + Markup.Escape(report.Directory) + "[/]").LeftJustified());
        console.MarkupLine($"[grey]{report.Files.Count} of {report.TotalFiles} tracked files · newest commit first · revision {Markup.Escape(report.Until)}[/]");
        console.WriteLine();

        Table table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn(new TableColumn("Last changed").NoWrap());
        table.AddColumn("File / change");
        table.AddColumn("Author");
        table.AddColumn(new TableColumn("Commit").NoWrap());
        table.AddColumn("References");
        foreach (FileInspect.DirectoryEntry f in report.Files)
        {
            string date = DateTimeOffset.TryParse(f.CommittedAt, out DateTimeOffset parsed)
                ? parsed.ToString("yyyy-MM-dd HH:mm") : f.CommittedAt;
            string description = $"[bold]{Markup.Escape(f.Path)}[/]\n[grey]{Markup.Escape(f.Subject)}[/]";
            string refs = f.PullRequest is null ? "—" : Markup.Escape(f.PullRequest) + " [grey]*[/]";
            if (f.WorkItems.Count > 0)
                refs += "\n" + Markup.Escape(string.Join(", ", f.WorkItems)) + " [grey]*[/]";
            table.AddRow(Markup.Escape(date), description, Markup.Escape(f.Author),
                $"[grey]{Markup.Escape(f.Sha[..12])}[/]", refs);
        }
        console.Write(table);
        Warnings(report.Warnings, console);
    }

    public static void File(FileInspect.Result report, IAnsiConsole console)
    {
        console.Write(new Rule("[steelblue1]File history · " + Markup.Escape(report.File) + "[/]").LeftJustified());
        console.MarkupLine($"[grey]{report.Changes.Count} commits · newest first · revision {Markup.Escape(report.Until)}[/]");
        foreach (FileInspect.Change c in report.Changes)
        {
            console.WriteLine();
            string heading = $"[bold]{Markup.Escape(c.Subject)}[/]";
            Table table = new Table().Border(TableBorder.Rounded).Expand().HideHeaders();
            table.AddColumn(new TableColumn("Property").Width(14));
            table.AddColumn("Value");
            table.AddRow("Change", $"[steelblue1]{Markup.Escape(c.Action)}[/]  [grey]{Markup.Escape(c.Sha[..12])}[/]");
            table.AddRow("Author", Markup.Escape(c.Author));
            table.AddRow("Authored", Markup.Escape(c.AuthoredAt));
            table.AddRow("Committed", Markup.Escape(c.CommittedAt));
            if (c.PullRequest != null) table.AddRow("PR mentioned", Markup.Escape(c.PullRequest) + " [grey](unverified)[/]");
            if (c.WorkItems.Count > 0)
                table.AddRow("Work items", Markup.Escape(string.Join(", ", c.WorkItems)) + " [grey](unverified)[/]");
            string[] hunks = c.Diff.Split('\n').Where(line => line.StartsWith("@@ ", StringComparison.Ordinal)).ToArray();
            if (hunks.Length > 0)
                table.AddRow("Changed lines", Markup.Escape(string.Join("\n", hunks.Select(line => line.TrimEnd('\r')))));
            table.Caption = new TableTitle("[grey]Why: commit subject is evidence, not verified intent[/]");
            console.Write(new Panel(table).Header(heading).Border(BoxBorder.Rounded).Expand());
        }
        Warnings(report.Warnings, console);
    }

    static void Warnings(IReadOnlyList<string> warnings, IAnsiConsole console)
    {
        if (warnings.Count == 0) return;
        console.WriteLine();
        foreach (string warning in warnings)
            console.MarkupLine("[grey]Note: " + Markup.Escape(warning) + "[/]");
        console.MarkupLine("[grey]* PR and work-item references are inferred from commit messages, not provider-verified.[/]");
    }
}
