using System.Text.Json;
using Gitwizz;
using Json.Schema;
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

    /// <summary>The export, checked against the v2 schema on the way.</summary>
    VersionHistoryExport Export(int limit = VersionHistory.DefaultLimit, string prefix = "release/", string? target = null)
    {
        var export = VersionHistory.Export(_git, limit, prefix, target);
        var result = VersionHistoryContractTests.Schema.Evaluate(JsonDocument.Parse(VersionHistory.Json(export)).RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, string.Join("\n", (result.Details ?? []).Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key}: {e.Value}")).Take(20)));
        return export;
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

        var history = Export();
        Assert.Equal(VersionHistory.Schema, history.Schema);
        Assert.Equal(["1.1.0", "1.0.1", "1.0.0"], history.Versions.Select(v => v.Version));
        var latest = history.Versions[0];
        Assert.Equal("released", latest.Status);
        Assert.Equal(["1.0.1"], latest.Bases.Select(b => b.Version));
        Assert.Equal([feature], latest.Changes.Select(c => c.Sha));
        Assert.Equal([fix], history.Versions[1].Changes.Select(c => c.Sha));
        Assert.Empty(history.Versions[2].Bases);
        Assert.Empty(history.Versions[2].Warnings);
        Assert.Contains("gitwizz.version-history/v2", VersionHistory.Json(history));
    }

    [Fact]
    public void Release_branch_is_unreleased_listed_first_and_builds_on_the_nearest_tag()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        var fix = Commit("fix: maintenance");
        G("branch", "release/1.0");
        Commit("feat: mainline");
        G("tag", "v2.0.0");

        var history = Export();
        var branch = history.Versions[0];
        Assert.Equal(("1.0", "unreleased", "branch"), (branch.Version, branch.Status, branch.Source));
        Assert.Equal("refs/heads/release/1.0", branch.Reference);
        Assert.Equal(["1.0.0"], branch.Bases.Select(b => b.Version));
        Assert.Equal([fix], branch.Changes.Select(c => c.Sha));
        Assert.Equal("release/*", history.BranchPattern);
    }

    [Fact]
    public void Commit_limit_sets_explicit_truncation_marker()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        Commit("fix one");
        Commit("fix two");
        G("tag", "v1.0.1");
        var version = Export(1).Versions[0];
        Assert.True(version.Truncated);
        Assert.Single(version.Changes);
        Assert.NotEmpty(version.Warnings);
    }

    [Fact]
    public void Without_matching_refs_report_is_empty_with_warning()
    {
        Commit("initial");
        var history = Export();
        Assert.Empty(history.Versions);
        Assert.NotEmpty(history.Warnings);
        Assert.Contains("_No versions found._", VersionHistory.Markdown(history));
    }

    [Fact]
    public void Export_is_deterministic_and_rejects_invalid_limits()
    {
        Commit("initial");
        G("tag", "v0.1.0");
        Assert.Equal(VersionHistory.Json(Export()), VersionHistory.Json(Export()));
        Assert.Equal(VersionHistory.Markdown(Export()), VersionHistory.Markdown(Export()));
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

        var versions = Export().Versions;
        Assert.Equal(["1.0", "1.1.0", "1.0.1", "1.0.0"], versions.Select(v => v.Version));
        Assert.Equal(["1.0.1"], versions[0].Bases.Select(b => b.Version));
        Assert.Equal([pending], versions[0].Changes.Select(c => c.Sha));
        Assert.Equal(["1.0.0"], versions[1].Bases.Select(b => b.Version));
        Assert.Equal([feature], versions[1].Changes.Select(c => c.Sha));
        Assert.Equal([patch], versions[2].Changes.Select(c => c.Sha));
    }

    [Fact]
    public void Merged_back_backport_is_listed_once()
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

        // v1.2.0 contains v1.1.0 and v1.0.1, neither contains the other: both are bases, so neither feat: one/two
        // (shipped in v1.1.0) nor the backport (shipped in v1.0.1) is listed again.
        var versions = Export().Versions;
        var latest = versions.Single(v => v.Version == "1.2.0");
        Assert.Equal(["1.1.0", "1.0.1"], latest.Bases.Select(b => b.Version));
        Assert.Equal([feature, merge], latest.Changes.Select(c => c.Sha));
        Assert.DoesNotContain(backport, latest.Changes.Select(c => c.Sha));
        Assert.Equal("merge", latest.Changes[1].Category);
        Assert.Contains("1 direct commit and 1 merge commit", VersionHistory.Markdown(Export()));
    }

    [Fact]
    public void A_release_builds_on_releases_and_a_prerelease_on_any_tag()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        var feature = Commit("feat: big change");
        G("tag", "v2.0.0-rc.1");
        var fix = Commit("fix: found in rc");
        G("tag", "v2.0.0");

        var versions = Export().Versions;
        Assert.Equal(["2.0.0", "2.0.0-rc.1", "1.0.0"], versions.Select(v => v.Version));
        Assert.Equal(["1.0.0"], versions[0].Bases.Select(b => b.Version));
        Assert.Equal([fix, feature], versions[0].Changes.Select(c => c.Sha));
        Assert.Equal(["1.0.0"], versions[1].Bases.Select(b => b.Version));
        Assert.Equal([feature], versions[1].Changes.Select(c => c.Sha));
    }

    [Fact]
    public void Tagged_branch_head_has_no_unreleased_changes()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        Commit("fix");
        G("tag", "v1.0.1");
        G("branch", "release/1.0");

        var branch = Assert.Single(Export().Versions.Where(v => v.Source == "branch"));
        Assert.Equal(["1.0.1"], branch.Bases.Select(b => b.Version));
        Assert.Empty(branch.Changes);
    }

    [Fact]
    public void Remote_only_release_branches_are_listed_and_a_local_branch_wins()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        var local = Commit("fix: local work");
        G("branch", "release/1.0");
        var remote = Commit("feat: on the remote");
        G("remote", "add", "origin", "https://example.invalid/shop.git");
        G("update-ref", "refs/remotes/origin/release/2.0", remote);
        G("update-ref", "refs/remotes/origin/release/1.0", "v1.0.0");
        G("update-ref", "refs/remotes/origin/main", remote);

        var versions = Export().Versions;
        Assert.Equal(["2.0", "1.0", "1.0.0"], versions.Select(v => v.Version));
        Assert.Equal(("remote-branch", "refs/remotes/origin/release/2.0"), (versions[0].Source, versions[0].Reference));
        Assert.Equal(("branch", local), (versions[1].Source, versions[1].CommitSha));
        Assert.Contains("Remote branch `origin/release/2.0`", VersionHistory.Markdown(Export()));
    }

    [Fact]
    public void Annotated_tags_resolve_carry_their_date_and_survive_a_branch_of_the_same_name()
    {
        var first = Commit("initial");
        G("tag", "-a", "v1.0.0", "-m", "release 1.0.0");
        G("branch", "v1.0.0");
        G("tag", "not-a-version");
        G("tag", "v1.1.0", first + "^{tree}");

        var version = Assert.Single(Export().Versions);
        Assert.Equal(("1.0.0", first, "refs/tags/v1.0.0"), (version.Version, version.CommitSha, version.Reference));
        Assert.NotNull(version.TaggedAt);
    }

    [Fact]
    public void Prereleases_order_below_their_release_and_numerically()
    {
        Commit("initial");
        foreach (var tag in new[] { "v1.0.0-rc.2", "v1.0.0", "v1.0.0-rc.10", "v1.0.0-beta" }) G("tag", tag);

        Assert.Equal(["1.0.0", "1.0.0-rc.10", "1.0.0-rc.2", "1.0.0-beta"], Export().Versions.Select(v => v.Version));
    }

    [Fact]
    public void Same_version_with_and_without_v_is_reported_once()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        G("tag", "1.0.0");

        var history = Export();
        Assert.Equal("refs/tags/v1.0.0", Assert.Single(history.Versions).Reference);
        Assert.Contains(history.Warnings, w => w.Contains("1.0.0 skipped"));
    }

    [Theory]
    [InlineData("feat(api)!: drop v1", "feature", "feat", "api", "drop v1", true)]
    [InlineData("fix: empty orders\n\nBREAKING CHANGE: orders need a customer", "fix", "fix", null, "empty orders", true)]
    [InlineData("perf(db): cache lookups", "performance", "perf", "db", "cache lookups", false)]
    [InlineData("Docs: typo", "other", "docs", null, "typo", false)]
    [InlineData("Update the readme", "other", null, null, "Update the readme", false)]
    [InlineData("feat:no space is no header", "other", null, null, "feat:no space is no header", false)]
    public void Commits_are_grouped_by_their_conventional_type(string message, string category, string? type, string? scope,
        string description, bool breaking)
    {
        var c = VersionHistory.Classify("abc", false, "2026-10-01T00:00:00+00:00", message);
        Assert.Equal((category, type, scope, description, breaking), (c.Category, c.Type, c.Scope, c.Description, c.Breaking));
        Assert.Equal(message.Split('\n')[0], c.Subject);
        Assert.Equal("merge", VersionHistory.Classify("abc", true, "2026-10-01T00:00:00+00:00", "feat!: x").Category);
    }

    [Fact]
    public void Markdown_groups_changes_escapes_them_and_links_commits()
    {
        G("remote", "add", "origin", "https://github.com/example/shop.git");
        Commit("chore: initial");
        G("tag", "v1.0.0");
        var feature = Commit("feat(orders): export *all* orders");
        var breaking = Commit("refactor!: rename <Order> to [Sale]");
        Commit("- leading dash");
        G("tag", "v1.1.0");

        var md = VersionHistory.Markdown(Export());
        Assert.StartsWith("# Version history\n", md);
        Assert.Contains("## 1.1.0 (", md);
        Assert.Contains("Tag `v1.1.0` at [`", md);
        Assert.Contains("· since 1.0.0 · 3 direct commits\n", md);
        Assert.Contains($"· **orders:** export \\*all\\* orders ([`{feature[..8]}`](https://github.com/example/shop/commit/{feature}))", md);
        Assert.Contains($"· refactor\\!: rename \\<Order\\> to \\[Sale\\] ([`{breaking[..8]}`]", md);
        Assert.Contains("· \\- leading dash (", md);
        Assert.Contains("· first release · 1 direct commit", md);
        Assert.True(md.IndexOf("### ⚠ Breaking changes", StringComparison.Ordinal) < md.IndexOf("### Features", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_lists_versions_with_their_bases_and_commits()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        var fix = Commit("fix: patch");
        G("tag", "v1.0.1");

        var text = VersionHistory.Text(Export());
        Assert.Contains("1.0.1 [released] refs/tags/v1.0.1", text);
        Assert.Contains("since: 1.0.0 | pull requests: 0 | commits: 1", text);
        Assert.Contains($"{fix[..8]} fix: patch", text);
        Assert.Contains("since: (initial)", text);
    }

    [Fact]
    public void Cli_takes_version_history_options_and_writes_the_report()
    {
        var o = Cli.Parse(["--limit", "5", "--branch-pattern", "maint/*", "-f", "markdown", "-o", "v.md"], "version-history");
        Assert.Equal(("5", "maint/*", "markdown", "v.md"), (o["limit"], o["branch-pattern"], o["format"], o["output"]));
        Assert.Throws<ArgumentException>(() => Cli.Parse(["--since", "v1.0.0"], "version-history"));

        Commit("initial");
        G("tag", "v1.0.0");
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Out = new Spectre.Console.AnsiConsoleOutput(new StringWriter()), Ansi = Spectre.Console.AnsiSupport.No,
        });
        int Run(string sub, Dictionary<string, string> opt) => Cli.VersionHistoryCommand(sub, new(opt) { ["repo"] = _dir }, console);

        var json = Path.Combine(_dir, "versions.json");
        Assert.Equal(0, Run("export", new() { ["output"] = json }));
        Assert.Contains("\"version\": \"1.0.0\"", File.ReadAllText(json));

        // The format follows the extension: export to versions.md writes the changelog page, not JSON.
        var md = Path.Combine(_dir, "versions.md");
        Assert.Equal(0, Run("export", new() { ["output"] = md }));
        Assert.StartsWith("# Version history", File.ReadAllText(md));
        Assert.Equal(0, Run("list", new() { ["output"] = json, ["format"] = "md" }));
        Assert.StartsWith("# Version history", File.ReadAllText(json));

        Assert.Throws<ArgumentException>(() => Run("list", new() { ["branch-pattern"] = "release" }));
        Assert.Throws<ArgumentException>(() => Run("list", new() { ["limit"] = "0" }));
        Assert.Throws<ArgumentException>(() => Run("list", new() { ["format"] = "html" }));
    }

    [Fact]
    public void Target_lists_its_unreleased_changes_first_then_its_release_branches_and_their_releases()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        G("checkout", "-q", "-b", "release/1.0");
        Commit("fix: backport");
        G("tag", "v1.0.1"); // on the maintenance line cut from main: a release of main's app
        G("checkout", "-q", "main");
        Commit("feat: one");
        G("tag", "v1.1.0");
        var fix = Commit("fix: not released yet");
        var feature = Commit("feat: neither");

        var history = Export(target: "main");
        Assert.Equal(new VersionTarget("main", "refs/heads/main", feature), history.Target);
        Assert.Equal(["main", "1.0", "1.1.0", "1.0.1", "1.0.0"], history.Versions.Select(v => v.Version));
        var main = history.Versions[0];
        Assert.Equal(("unreleased", "target", "refs/heads/main"), (main.Status, main.Source, main.Reference));
        Assert.Equal(["1.1.0"], main.Bases.Select(b => b.Version)); // v1.0.1 is not in main: not one of its bases
        Assert.Equal([feature, fix], main.Changes.Select(c => c.Sha));
        Assert.Equal(["1.0.1"], history.Versions[1].Bases.Select(b => b.Version)); // the release branch builds on its own tag

        var md = VersionHistory.Markdown(history);
        Assert.Contains("The history of `main`", md);
        Assert.Contains("## main (unreleased)\n\nBranch `main` at ", md);
        Assert.Contains("## 1.0 (unreleased)", md);

        // Without --target nothing changes: no target, every tag, no entry for main.
        var all = Export();
        Assert.Null(all.Target);
        Assert.Equal(["1.0", "1.1.0", "1.0.1", "1.0.0"], all.Versions.Select(v => v.Version));
        Assert.Contains("\"target\": null", VersionHistory.Json(all));
    }

    [Fact]
    public void Apps_on_separate_branches_each_get_only_their_own_history()
    {
        // App A on main, app B on an unrelated branch: neither history may show the other's tags or release branches.
        Commit("feat: app a");
        G("tag", "v1.0.0");
        G("checkout", "-q", "-b", "release/1.1");
        Commit("fix: app a maintenance");
        G("checkout", "-q", "--orphan", "app-b");
        G("rm", "-q", "-rf", ".");
        var b = Commit("feat: app b");
        G("tag", "2.0.0");
        G("checkout", "-q", "-b", "release/2.1");
        Commit("fix: app b in progress");
        G("checkout", "-q", "app-b");
        var pending = Commit("feat: app b merged, not released");

        var appA = Export(target: "main");
        Assert.Equal(["main", "1.1", "1.0.0"], appA.Versions.Select(v => v.Version));
        Assert.Empty(appA.Versions[0].Changes);

        var appB = Export(target: "app-b");
        Assert.Equal(["app-b", "2.1", "2.0.0"], appB.Versions.Select(v => v.Version));
        Assert.Equal([pending], appB.Versions[0].Changes.Select(c => c.Sha));
        Assert.Equal([b], appB.Versions[2].Changes.Select(c => c.Sha));
    }

    [Fact]
    public void A_release_branch_belongs_to_the_branch_it_was_cut_from_when_apps_share_history()
    {
        // app-b started from main: main's first-parent line meets app-b's release branch only where the apps split.
        Commit("chore: shared start");
        G("checkout", "-q", "-b", "app-b");
        Commit("feat: app b");
        G("checkout", "-q", "-b", "release/b-1.0");
        Commit("fix: app b release");
        G("checkout", "-q", "main");
        Commit("feat: app a");
        G("checkout", "-q", "-b", "release/a-1.0");
        Commit("fix: app a release");
        G("checkout", "-q", "-b", "fix/a-hotfix"); // a pull request branch made from the release branch, not merged yet
        Commit("fix: hotfix in review");
        G("checkout", "-q", "main");
        G("merge", "-q", "--no-ff", "-m", "Merge branch 'release/a-1.0'", "release/a-1.0"); // merged back: still main's

        Assert.Equal(["main", "a-1.0"], Export(target: "main").Versions.Select(v => v.Version));
        Assert.Equal(["app-b", "b-1.0"], Export(target: "app-b").Versions.Select(v => v.Version));
    }

    [Fact]
    public void Release_branches_without_tags_each_list_what_they_add_to_the_one_before()
    {
        // Two apps, no tags: release/cxm/* and release/bfm/* cut from develop one after the other.
        Commit("chore: start");
        G("checkout", "-q", "-b", "develop");
        var base106 = Commit("feat: cxm before 1.0.6");
        G("branch", "release/cxm/1.0.6");
        var bfm = Commit("feat: bfm work");
        G("branch", "release/bfm/2.0.0");
        var a = Commit("feat(cart): cxm for 1.8.0");
        G("branch", "release/cxm/1.8.0");
        var b1 = Commit("fix: cxm for 1.9.0");
        G("branch", "release/cxm/1.9.0");
        G("checkout", "-q", "release/cxm/1.8.0");
        var hotfix = Commit("fix: hotfix only on 1.8.0");
        G("checkout", "-q", "develop");

        var cxm = Export(prefix: "release/cxm/");
        Assert.Equal(["1.9.0", "1.8.0", "1.0.6"], cxm.Versions.Select(v => v.Version));
        Assert.Equal(["1.8.0"], cxm.Versions[0].Bases.Select(x => x.Version));
        Assert.Equal("refs/heads/release/cxm/1.8.0", cxm.Versions[0].Bases[0].Reference);
        Assert.Equal([b1], cxm.Versions[0].Changes.Select(c => c.Sha));                 // 1.8.0..1.9.0, as git log does
        Assert.Equal([hotfix, a, bfm], cxm.Versions[1].Changes.Select(c => c.Sha));     // 1.0.6..1.8.0
        Assert.Empty(cxm.Versions[2].Bases);                                             // the first release
        Assert.Contains(base106, cxm.Versions[2].Changes.Select(c => c.Sha));

        // One pattern for both apps: each folder is its own line, so cxm/1.8.0 builds on cxm/1.0.6, not on bfm/2.0.0.
        var all = Export(prefix: "release/").Versions.ToDictionary(v => v.Version);
        Assert.Equal(["cxm/1.0.6"], all["cxm/1.8.0"].Bases.Select(x => x.Version));
        Assert.Empty(all["bfm/2.0.0"].Bases);

        // --from 1.0.6 --to 1.8.0: 1.8.0 alone, still since 1.0.6.
        var range = VersionHistory.Export(_git, branchPrefix: "release/cxm/", from: "1.0.6", to: "1.8.0");
        Assert.Equal(["1.8.0"], range.Versions.Select(v => v.Version));
        Assert.Equal(["1.0.6"], range.Versions[0].Bases.Select(x => x.Version));
        Assert.Contains("· since 1.0.6 · 3 direct commits", VersionHistory.Markdown(range));
        Assert.Throws<ArgumentException>(() => VersionHistory.Export(_git, from: "1.9.0", to: "1.8.0"));
        Assert.Equal("1.0.6", Cli.Parse(["--from", "1.0.6", "--to", "1.9.0"], "version-history")["from"]);
    }

    [Fact]
    public void Clean_output_has_only_the_day_and_description_of_each_change()
    {
        Commit("chore: initial");
        G("tag", "v1.0.0");
        G("checkout", "-q", "-b", "feature/csv");
        Commit("feat(orders): export as CSV");
        G("checkout", "-q", "main");
        G("merge", "-q", "--no-ff", "-m", "Merge pull request #12 from me/feature/csv\n\nfeat(orders): CSV export", "feature/csv");
        Commit("fix: round prices (#13)");
        Commit("Merged PR 14: Kundenliste schneller laden");
        Commit("docs: update the manual");
        Commit("PR 15 (Lieferadresse speichern) gemergt");
        Commit("fix: round prices (#16)"); // the same change again: listed once
        G("tag", "v1.1.0");
        G("remote", "add", "origin", "https://github.com/example/shop.git");

        var history = Export();
        var day = history.Versions[0].CommittedAt[..10];
        var md = VersionHistory.Markdown(history, clean: true);
        Assert.StartsWith($"# Version history\n\n## 1.1.0 ({day})\n\n### Features\n\n- {day} · CSV export\n\n### Fixes\n\n- {day} · Round prices\n", md);
        Assert.Contains($"### Other changes\n\n- {day} · Kundenliste schneller laden\n- {day} · Lieferadresse speichern\n- {day} · Update the manual\n", md);
        Assert.Contains($"## 1.0.0 ({day})\n\n### Other changes\n\n- {day} · Initial\n", md);
        foreach (var jargon in new[] { "#1", "PR", "pull", "merge", "Merge", "commit", "`", "http", "since", "orders:" })
            Assert.DoesNotContain(jargon, md);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(md, "Round prices"));

        Assert.Contains($"1.1.0 ({day})\n  {day} CSV export\n", VersionHistory.Text(history, clean: true));
        Assert.Equal("Upcoming (main)", VersionHistory.Text(Export(target: "main"), clean: true).Split('\n')[0]);
        Assert.Equal("true", Cli.Parse(["--clean"], "version-history")["clean"]);
        Assert.Equal("Fix the login", VersionHistory.CleanText("Merge pull request #7 from me/x fix the login"));
    }

    [Theory]
    [InlineData("PR 1234 (Kundenliste schneller laden) gemergt", "Kundenliste schneller laden")]
    [InlineData("PR 0000 (fix: Preise runden) gemergt: Preise runden auf 2 Stellen", "Preise runden")]
    [InlineData("PR 77 (Export (CSV) ergänzt) gemergt", "Export (CSV) ergänzt")]
    [InlineData("pr 5 (export) GEMERGT", "Export")]
    [InlineData("PR 12: Kundenliste (neu)", "Kundenliste (neu)")]   // no "gemergt": brackets are part of the text
    public void Clean_keeps_only_the_text_in_brackets_of_a_merged_pr(string description, string expected) =>
        Assert.Equal(expected, VersionHistory.CleanText(description));

    [Fact]
    public void Pull_requests_group_the_commits_they_came_with()
    {
        Commit("chore: initial");
        G("tag", "v1.0.0");
        G("checkout", "-q", "-b", "feature/csv");
        var csv1 = Commit("feat(orders): export as CSV");
        var csv2 = Commit("test: CSV export");
        G("checkout", "-q", "main");
        G("merge", "-q", "--no-ff", "-m", "Merge pull request #12 from me/feature/csv\n\nfeat(orders): CSV export", "feature/csv");
        var merge = G("rev-parse", "HEAD");
        var squash = Commit("fix: round prices (#13)");
        var ado = Commit("Merged PR 14: perf: cache lookups");
        var direct = Commit("docs: readme");
        G("checkout", "-q", "-b", "feature/plain");
        var plain = Commit("chore: tidy up");
        G("checkout", "-q", "main");
        G("merge", "-q", "--no-ff", "-m", "Merge pull request #15 from me/feature/plain\n\nTidy up", "feature/plain");

        var main = Export(target: "main").Versions[0];
        Assert.Equal(["#15", "#14", "#13", "#12"], main.PullRequests.Select(p => p.Id));
        var pr12 = main.PullRequests[3];
        Assert.Equal(("feat(orders): CSV export", "merge", merge, "feature", "orders"), (pr12.Title, pr12.Strategy, pr12.MergeCommit, pr12.Category, pr12.Scope));
        Assert.Equal([merge, csv2, csv1], pr12.Commits);
        Assert.Equal(("fix: round prices", "squash", "fix"), (main.PullRequests[2].Title, main.PullRequests[2].Strategy, main.PullRequests[2].Category));
        Assert.Equal([squash], main.PullRequests[2].Commits);
        Assert.Equal(("perf: cache lookups", "performance"), (main.PullRequests[1].Title, main.PullRequests[1].Category));
        Assert.Equal([ado], main.PullRequests[1].Commits);
        Assert.Equal(("Tidy up", null, "other"), (main.PullRequests[0].Title, main.PullRequests[0].Type, main.PullRequests[0].Category)); // from its commits
        Assert.Equal("#12", main.Changes.Single(c => c.Sha == csv1).PullRequest);
        Assert.Equal("#15", main.Changes.Single(c => c.Sha == plain).PullRequest);
        Assert.Null(main.Changes.Single(c => c.Sha == direct).PullRequest);

        G("remote", "add", "origin", "https://github.com/example/shop.git");
        var md = VersionHistory.Markdown(Export(target: "main"));
        Assert.Contains("· 4 pull requests and 1 direct commit\n", md);
        var day = main.PullRequests[3].CommittedAt[..10]; // the merge commit's day
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", day);
        Assert.Contains($"### Features\n\n- {day} · **orders:** CSV export ([#12](https://github.com/example/shop/pull/12), 2 commits)\n", md);
        Assert.Contains("· round prices ([#13](https://github.com/example/shop/pull/13))\n", md);
        Assert.Contains("· Tidy up ([#15](https://github.com/example/shop/pull/15), 1 commit)\n", md);
        Assert.Contains("· docs: readme ([`", md);
        Assert.DoesNotContain("export as CSV", md); // a pull request's commits are not listed again
        Assert.Contains($"{day} #12 feat(orders): CSV export", VersionHistory.Text(Export(target: "main")));
    }

    [Fact]
    public void Target_contains_a_backport_once_it_is_merged_back()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        G("checkout", "-q", "-b", "release/1.0");
        Commit("fix: backport");
        G("tag", "v1.0.1");
        G("checkout", "-q", "main");
        G("merge", "-q", "--no-ff", "-m", "merge release/1.0", "release/1.0");

        var history = Export(target: "main");
        Assert.Contains(history.Versions, v => v.Version == "1.0.1");
        Assert.Equal(["1.0.1"], history.Versions[0].Bases.Select(b => b.Version));
        Assert.Equal("merge", Assert.Single(history.Versions[0].Changes).Category);
    }

    [Fact]
    public void A_release_branch_as_target_is_listed_once_and_origin_stands_in_for_a_missing_local_branch()
    {
        Commit("initial");
        G("tag", "v1.0.0");
        G("checkout", "-q", "-b", "release/1.0");
        var fix = Commit("fix: maintenance");
        G("checkout", "-q", "main");

        var history = Export(target: "release/1.0");
        Assert.Equal(["release/1.0", "1.0.0"], history.Versions.Select(v => v.Version)); // not again as branch "1.0"
        Assert.Equal([fix], history.Versions[0].Changes.Select(c => c.Sha));

        G("update-ref", "refs/remotes/origin/develop", fix);
        var remote = Export(target: "develop");
        Assert.Equal(("refs/remotes/origin/develop", fix), (remote.Target!.Reference, remote.Target.Sha));
        Assert.Equal(("HEAD", G("rev-parse", "HEAD")), (Export(target: "HEAD").Target!.Reference, Export(target: "HEAD").Target!.Sha));

        var unknown = Assert.Throws<InvalidOperationException>(() => VersionHistory.Export(_git, target: "no-such-branch"));
        Assert.StartsWith("unknown branch or ref", unknown.Message);
    }

    [Fact]
    public void Cli_takes_a_target()
    {
        Assert.Equal("main", Cli.Parse(["-t", "main"], "version-history")["target"]);
        Assert.Equal("main", Cli.Parse(["--target", "main"], "version-history")["target"]);

        Commit("initial");
        G("tag", "v1.0.0");
        var head = Commit("feat: pending");
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Out = new Spectre.Console.AnsiConsoleOutput(new StringWriter()), Ansi = Spectre.Console.AnsiSupport.No,
        });
        var md = Path.Combine(_dir, "versions.md");
        Assert.Equal(0, Cli.VersionHistoryCommand("export", new() { ["repo"] = _dir, ["output"] = md, ["target"] = "main" }, console));
        Assert.Contains($"## main (unreleased)\n\nBranch `main` at `{head[..8]}` · since 1.0.0 · 1 direct commit", File.ReadAllText(md));
    }

    public void Dispose() => Directory.Delete(_dir, true);
}
