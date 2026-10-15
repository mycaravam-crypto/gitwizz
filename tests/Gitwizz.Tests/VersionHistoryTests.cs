using Gitwizz;
using Xunit;

namespace Gitwizz.Tests;

public sealed class VersionHistoryTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("gitwizz-version-history").FullName;
    readonly Git _git;
    int _counter;

    public VersionHistoryTests()
    {
        _git = new Git(_dir);
        G("init", "-q", "-b", "main");
        G("config", "user.name", "Test");
        G("config", "user.email", "test@example.invalid");
    }

    string G(params string[] args) => _git.Run(args);

    string Commit(string subject)
    {
        File.WriteAllText(Path.Combine(_dir, "change.txt"), (++_counter).ToString());
        G("add", "change.txt");
        G("commit", "-q", "-m", subject);
        return G("rev-parse", "HEAD");
    }

    [Fact]
    public void Tags_define_released_versions_and_exclusive_ranges()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        var fix = Commit("fix: first patch");
        G("tag", "v1.0.1");
        var feature = Commit("feat: addition");
        G("tag", "v1.1.0");

        var history = VersionHistory.Export(_git);
        Assert.Equal(VersionHistory.Schema, history.Schema);
        Assert.Equal(["1.1.0", "1.0.1", "1.0.0"], history.Versions.Select(v => v.Version));
        var latest = history.Versions[0];
        Assert.Equal("released", latest.Status);
        Assert.Equal("1.0.1", latest.BaseVersion);
        Assert.Equal([feature], latest.Changes.Select(c => c.Sha));
        Assert.Equal([fix], history.Versions[1].Changes.Select(c => c.Sha));
        Assert.Null(history.Versions[2].BaseVersion);
        Assert.Contains("gitwizz.version-history/v1", VersionHistory.Json(history));
    }

    [Fact]
    public void Release_branch_is_unreleased_and_uses_nearest_reachable_tag()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        var fix = Commit("fix: maintenance");
        G("branch", "release/1.0");
        Commit("feat: mainline");
        G("tag", "v2.0.0");

        var history = VersionHistory.Export(_git);
        var branch = Assert.Single(history.Versions.Where(v => v.Source == "branch"));
        Assert.Equal("1.0", branch.Version);
        Assert.Equal("unreleased", branch.Status);
        Assert.Equal("refs/heads/release/1.0", branch.Reference);
        Assert.Equal("1.0.0", branch.BaseVersion);
        Assert.Equal([fix], branch.Changes.Select(c => c.Sha));
    }

    [Fact]
    public void Commit_limit_sets_explicit_truncation_marker()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        Commit("fix one");
        Commit("fix two");
        G("tag", "v1.0.1");
        var version = VersionHistory.Export(_git, 1).Versions[0];
        Assert.True(version.Truncated);
        Assert.Single(version.Changes);
        Assert.NotEmpty(version.Warnings);
    }

    [Fact]
    public void Without_matching_refs_report_is_empty_with_warning()
    {
        Commit("initial");
        var history = VersionHistory.Export(_git);
        Assert.Empty(history.Versions);
        Assert.NotEmpty(history.Warnings);
    }

    [Fact]
    public void Export_is_deterministic_and_rejects_invalid_limits()
    {
        Commit("initial");
        G("tag", "v0.1.0");
        Assert.Equal(VersionHistory.Json(VersionHistory.Export(_git)),
            VersionHistory.Json(VersionHistory.Export(_git)));
        Assert.Throws<ArgumentException>(() => VersionHistory.Export(_git, 0));
    }

    [Fact]
    public void Diverged_maintenance_line_takes_the_nearest_tag_not_the_highest_version()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        G("checkout", "-q", "-b", "release/1.0");
        var patch = Commit("fix: backport");
        G("tag", "v1.0.1");
        var pending = Commit("fix: second backport");
        G("checkout", "-q", "main");
        var feature = Commit("feat: mainline");
        G("tag", "v1.1.0");

        var versions = VersionHistory.Export(_git).Versions;
        Assert.Equal(["1.1.0", "1.0.1", "1.0.0", "1.0"], versions.Select(v => v.Version));
        Assert.Equal("1.0.0", versions[0].BaseVersion);
        Assert.Equal([feature], versions[0].Changes.Select(c => c.Sha));
        Assert.Equal([patch], versions[1].Changes.Select(c => c.Sha));
        Assert.Equal("1.0.1", versions[3].BaseVersion);
        Assert.Equal([pending], versions[3].Changes.Select(c => c.Sha));
    }

    [Fact]
    public void Merged_back_maintenance_line_does_not_hide_the_mainline_release()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        G("checkout", "-q", "-b", "release/1.0");
        var backport = Commit("fix: backport");
        G("tag", "v1.0.1");
        G("checkout", "-q", "main");
        Commit("feat: one");
        Commit("feat: two");
        G("tag", "v1.1.0");
        G("merge", "-q", "--no-ff", "-X", "theirs", "-m", "merge release/1.0", "release/1.0");
        var merge = G("rev-parse", "HEAD");
        var feature = Commit("feat: three");
        G("tag", "v1.2.0");

        // Topological order reaches v1.0.1 first; v1.1.0 is nearer (3 commits against 4) and already shipped feat: one/two.
        var latest = VersionHistory.Export(_git).Versions[0];
        Assert.Equal("1.1.0", latest.BaseVersion);
        Assert.Equal([feature, merge, backport], latest.Changes.Select(c => c.Sha));
    }

    [Fact]
    public void Same_version_with_and_without_v_is_reported_once()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        G("tag", "1.0.0");

        var history = VersionHistory.Export(_git);
        Assert.Equal("refs/tags/v1.0.0", Assert.Single(history.Versions).Reference);
        Assert.Contains(history.Warnings, w => w.Contains("1.0.0 skipped"));
    }

    [Fact]
    public void Tagged_branch_head_has_no_unreleased_changes()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        Commit("fix");
        G("tag", "v1.0.1");
        G("branch", "release/1.0");

        var branch = Assert.Single(VersionHistory.Export(_git).Versions.Where(v => v.Source == "branch"));
        Assert.Equal("1.0.1", branch.BaseVersion);
        Assert.Empty(branch.Changes);
    }

    [Fact]
    public void Annotated_tags_resolve_and_survive_a_branch_of_the_same_name()
    {
        var first = Commit("initial");
        G("tag", "-a", "v1.0.0", "-m", "release 1.0.0");
        G("branch", "v1.0.0");
        G("tag", "not-a-version");

        var version = Assert.Single(VersionHistory.Export(_git).Versions);
        Assert.Equal("1.0.0", version.Version);
        Assert.Equal(first, version.CommitSha);
        Assert.Equal("refs/tags/v1.0.0", version.Reference);
    }

    [Fact]
    public void Prereleases_order_below_their_release_and_numerically()
    {
        Commit("initial");
        foreach (var tag in new[] { "v1.0.0-rc.2", "v1.0.0", "v1.0.0-rc.10", "v1.0.0-beta" }) G("tag", tag);

        Assert.Equal(["1.0.0", "1.0.0-rc.10", "1.0.0-rc.2", "1.0.0-beta"], VersionHistory.Export(_git).Versions.Select(v => v.Version));
    }

    [Fact]
    public void Text_lists_versions_with_their_baseline_and_commits()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        var fix = Commit("fix: patch");
        G("tag", "v1.0.1");

        var text = VersionHistory.Text(VersionHistory.Export(_git));
        Assert.Contains("1.0.1 [released] refs/tags/v1.0.1", text);
        Assert.Contains("since: 1.0.0 | commits: 1", text);
        Assert.Contains($"{fix[..8]} fix: patch", text);
        Assert.Contains("since: (initial)", text);
    }

    [Fact]
    public void Cli_takes_version_history_options_and_writes_the_report()
    {
        var o = Cli.Parse(["--limit", "5", "--branch-pattern", "maint/*", "-f", "text", "-o", "v.txt"], "version-history");
        Assert.Equal(("5", "maint/*", "text", "v.txt"), (o["limit"], o["branch-pattern"], o["format"], o["output"]));
        Assert.Throws<ArgumentException>(() => Cli.Parse(["--since", "v1.0.0"], "version-history"));

        Commit("initial");
        G("tag", "v1.0.0");
        var output = Path.Combine(_dir, "versions.json");
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Out = new Spectre.Console.AnsiConsoleOutput(new StringWriter()), Ansi = Spectre.Console.AnsiSupport.No,
        });
        Assert.Equal(0, Cli.VersionHistoryCommand("export", new() { ["repo"] = _dir, ["output"] = output }, console));
        Assert.Contains("\"version\": \"1.0.0\"", File.ReadAllText(output));
        Assert.Throws<ArgumentException>(() => Cli.VersionHistoryCommand("list", new() { ["repo"] = _dir, ["branch-pattern"] = "release" }, console));
        Assert.Throws<ArgumentException>(() => Cli.VersionHistoryCommand("list", new() { ["repo"] = _dir, ["limit"] = "0" }, console));
    }

    public void Dispose() => Directory.Delete(_dir, true);
}
