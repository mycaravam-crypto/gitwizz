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

    public void Dispose() => Directory.Delete(_dir, true);
}
