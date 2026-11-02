using System.Text.RegularExpressions;

namespace Gitwizz.Tests;

public class DocumentationLinksTests
{
    // Checks the filesystem portion of local Markdown links. Fragment/heading validation
    // is intentionally separate: generated GitHub anchors have their own rules.
    [Fact]
    public void RepositoryMarkdownLinksResolveToExistingFiles()
    {
        var root = RepositoryRoot();
        var pages = new[] { Path.Combine(root, "README.md"), Path.Combine(root, "PLAN.md") }
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories));
        var missing = new List<string>();
        var link = new Regex(@"!?\[[^\]\r\n]*\]\((?<target>[^)\s]+)(?:\s+""[^""]*"")?\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        foreach (var page in pages)
        {
            var relativePage = Path.GetRelativePath(root, page).Replace('\\', '/');
            var text = File.ReadAllText(page);
            // Ignore fenced examples; commands and sample Markdown there are not navigation.
            text = Regex.Replace(text, @"(?ms)^\s*`{3,}[^\r\n]*\r?\n.*?^\s*`{3,}[^\r\n]*(?:\r?\n|$)", "");
            foreach (Match match in link.Matches(text))
            {
                var target = match.Groups["target"].Value;
                if (target.StartsWith('#') || target.StartsWith('/') || target.StartsWith("//")
                    || Uri.TryCreate(target, UriKind.Absolute, out _)) continue;
                var filePart = target.Split('#', '?')[0];
                if (filePart.Length == 0) continue;
                var destination = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(page)!, Uri.UnescapeDataString(filePart)));
                if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || !File.Exists(destination) && !Directory.Exists(destination))
                    missing.Add($"{relativePage}: {target}");
            }
        }

        Assert.True(missing.Count == 0, "Broken local Markdown links:\n" + string.Join("\n", missing));
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "README.md"))
                && File.Exists(Path.Combine(dir.FullName, "src", "Gitwizz", "Gitwizz.csproj")))
                return dir.FullName;
        throw new InvalidOperationException("Could not locate GitWizz repository root.");
    }
}
