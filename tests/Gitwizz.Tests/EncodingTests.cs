using System.Text;
using Gitwizz;
using Xunit;

namespace Gitwizz.Tests;

/// <summary>
/// German umlauts and ß from git to every output. The pipes are UTF-8 only by StartInfo's choice: on Windows .NET
/// would read them in the console code page, which CI on Linux can't show, so the choice itself is tested.
/// </summary>
public sealed class EncodingTests : IDisposable
{
    const string Subject = "feat(größe): Übersetzung für Änderungen an Maßeinheiten";

    readonly string _dir = Directory.CreateTempSubdirectory("gitwizz-encoding").FullName;
    readonly Git _git;

    public EncodingTests()
    {
        _git = new Git(_dir);
        G("init", "-q", "-b", "main");
        G("config", "user.name", "Jörg Müller");
        G("config", "user.email", "joerg@example.invalid");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    string G(params string[] args) => _git.Run(args);

    string Commit(string file, string message)
    {
        File.WriteAllText(Path.Combine(_dir, file), message);
        G("add", "-A");
        G("commit", "-q", "-m", message);
        return G("rev-parse", "HEAD");
    }

    [Theory]
    [InlineData("git", true)]
    [InlineData("gh", true)]
    [InlineData("/usr/bin/git", true)]
    [InlineData("ado", false)]
    [InlineData("sh", false)]
    public void Git_and_gh_pipes_are_utf8_without_a_byte_order_mark(string file, bool utf8)
    {
        var psi = Git.StartInfo(".", file, [], null, stdin: true);
        foreach (var encoding in new[] { psi.StandardOutputEncoding, psi.StandardErrorEncoding, psi.StandardInputEncoding })
            if (utf8)
            {
                Assert.Equal(Encoding.UTF8.WebName, encoding!.WebName);
                Assert.Empty(encoding.GetPreamble()); // a BOM on stdin would reach git as part of the first line
            }
            else Assert.Null(encoding); // ado and build tools write in the console code page, .NET's default
        Assert.Null(Git.StartInfo(".", "git", [], null, stdin: false).StandardInputEncoding); // not allowed without a redirect
    }

    [Fact]
    public void Umlauts_survive_version_history_in_every_format()
    {
        Commit("app.txt", "chore: erster Stand");
        G("tag", "-a", "v1.0.0", "-m", "Veröffentlichung 1.0.0");
        var sha = Commit("größe.txt", Subject);
        G("tag", "v1.1.0");
        G("branch", "release/1.1-übergang");

        var export = VersionHistory.Export(_git);
        var change = Assert.Single(export.Versions.Single(v => v.Version == "1.1.0").Changes);
        Assert.Equal((sha, Subject, "größe", "Übersetzung für Änderungen an Maßeinheiten"),
            (change.Sha, change.Subject, change.Scope, change.Description));
        Assert.Contains(export.Versions, v => v.Version == "1.1-übergang");

        var json = VersionHistory.Json(export);
        Assert.Contains("Übersetzung für Änderungen an Maßeinheiten", json); // literal, not \u00FC
        Assert.DoesNotContain("\\u00", json);
        Assert.Contains("· **größe:** Übersetzung für Änderungen an Maßeinheiten", VersionHistory.Markdown(export));
        Assert.Contains("## 1.1-übergang (unreleased)", VersionHistory.Markdown(export));
        Assert.Contains(Subject, VersionHistory.Text(export));
    }

    [Fact]
    public void Umlauts_survive_history_commits_and_paths()
    {
        var sha = Commit("Größenänderung.txt", "fix: Prüfung der Bestellmenge");
        var commit = Assert.Single(History.ReadGit(_git, null, sha, 10).Commits);
        Assert.Equal("fix: Prüfung der Bestellmenge", commit.Subject);
        Assert.Equal("Größenänderung.txt", Assert.Single(commit.Files).Path);
    }

    [Fact]
    public void Reports_written_to_a_file_are_utf8_without_a_byte_order_mark()
    {
        Commit("app.txt", "chore: erster Stand");
        G("tag", "v1.0.0");
        Commit("b.txt", Subject);
        G("tag", "v1.1.0");
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Out = new Spectre.Console.AnsiConsoleOutput(new StringWriter()), Ansi = Spectre.Console.AnsiSupport.No,
        });
        var md = Path.Combine(_dir, "versions.md");
        Assert.Equal(0, Cli.VersionHistoryCommand("export", new() { ["repo"] = _dir, ["output"] = md }, console));

        var bytes = File.ReadAllBytes(md);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]);
        Assert.Contains("Übersetzung für Änderungen an Maßeinheiten", new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes));
    }
}
