using System.Text.Json;
using Gitwizz;

namespace Gitwizz.Tests;

public class HistoryTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("gitwizz-history").FullName;
    readonly Git _git;

    public HistoryTests()
    {
        _git = new Git(_dir);
        _git.Run("init", "-q", "-b", "main");
        _git.Run("config", "user.email", "t@t"); _git.Run("config", "user.name", "t");
        Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.19m; }\n\n    decimal Round(decimal v) { return v; }\n}\n");
        Write("README.md", "# demo\n");
        Commit("init");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    void Write(string f, string c) { var p = Path.Combine(_dir, f); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, c); }
    string Commit(string m) { _git.Run("add", "-A"); _git.Run("commit", "-q", "--allow-empty", "-m", m); return _git.RevParse("HEAD"); }
    string Head => _git.RevParse("HEAD");

    /// <summary>A provider answering from a table; a missing number is an unreachable provider.</summary>
    sealed class FakeProvider(string name, Dictionary<int, ProviderPr> prs) : IHistoryProvider
    {
        public string Name => name;
        public string ProviderCommitLinks => "unavailable: fake";
        public List<int> Calls { get; } = [];
        public ProviderPr PullRequest(int number)
        {
            Calls.Add(number);
            return prs.TryGetValue(number, out var p) ? p : throw new InvalidOperationException($"{name} is offline");
        }
    }

    /// <summary>An ado answering through a function (thread-safe: commit lookups run in parallel); records each call.</summary>
    sealed class ScriptedAdo(Func<string[], string> answer) : IAdoClient
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Calls { get; } = new();
        public string Run(params string[] args) { Calls.Enqueue(string.Join(' ', args)); return answer(args); }
    }

    static string CommitContext(string sha, string status = "verified", string items = "[]", string repo = "backend", string schema = "ado.commit-context/v1") => $$"""
        {"schema": "{{schema}}", "server": "https://tfs.example/coll", "project": "Platform",
         "repository": {"id": "00000000-0000-0000-0000-000000000001", "name": "{{repo}}"},
         "commit": {"commitId": "{{sha}}", "parents": [], "comment": "x"},
         "workItems": {"status": "{{status}}"{{(status == "verified" ? $", \"items\": {items}" : "")}}},
         "warnings": [{{(status == "verified" ? "" : "\"work item links unavailable: the server did not report work items\"")}}]}
        """;

    sealed class FakeAdo(Dictionary<string, string> responses) : IAdoClient
    {
        public string Run(params string[] args) =>
            responses.TryGetValue(string.Join(' ', args), out var r) ? r : throw new InvalidOperationException($"ado {string.Join(' ', args)} failed (exit 4): denied");
    }

    HistoryExport Export(IHistoryProvider provider, string? since = null, int limit = 50, WorkspaceStore? store = null,
        int commitLookups = History.DefaultCommitLookups) =>
        History.Export(_git, store ?? WorkspaceStore.Off, provider, since, "main", limit, commitLookups: commitLookups);

    static HistoryCommit Of(HistoryExport e, string sha) => e.Commits.Single(c => c.Sha == sha);

    [Fact]
    public void Commit_messages_link_work_items_with_their_exact_source()
    {
        var since = Head;
        var sha = Commit("Round tax AB#4711\n\nAlso fixes #12 and touches AB#4711 again; see #99");
        var e = Export(new LocalHistory(), since);

        var c = Assert.Single(e.Commits);
        Assert.Equal(sha, c.Sha);
        Assert.Equal([("#12", "closes", "fixes #12"), ("AB#4711", "ab", "AB#4711")], c.WorkItems.Select(l => (l.WorkItem, l.Kind, l.Evidence)));
        Assert.All(c.WorkItems, l => Assert.Equal("commit-message", l.Source));
        Assert.Equal("unknown", c.PullRequest.Resolution); // nothing says which PR: never "none"
        Assert.Equal("unknown: local provider", e.Sources.ProviderCommitLinks);
        Assert.Equal(("unknown", null), (c.ProviderLinks.Status, c.ProviderLinks.Items)); // not asked: never "no links"

        // Azure DevOps reads #99 as a work item; GitHub doesn't link a plain mention.
        var ado = Export(new AdoHistory(new FakeAdo([])), since);
        Assert.Contains(Single(ado).WorkItems, l => l is { WorkItem: "AB#99", Kind: "mention" });
        Assert.Contains(Single(ado).WorkItems, l => l is { WorkItem: "AB#12", Kind: "mention" });
        Assert.DoesNotContain(Single(e).WorkItems, l => l.Kind == "mention");
    }

    static HistoryCommit Single(HistoryExport e) => Assert.Single(e.Commits);

    [Fact]
    public void Merged_pull_request_is_exact_and_keeps_its_work_items_apart_from_commit_links()
    {
        var since = Head;
        _git.Run("checkout", "-q", "-b", "feature/round");
        var a = Commit("Round half up AB#100");
        Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.19m; }\n\n    decimal Round(decimal v) { return Math.Round(v, 2); }\n}\n");
        var b = Commit("Two decimals");
        _git.Run("checkout", "-q", "main");
        _git.Run("merge", "-q", "--no-ff", "-m", "Merged PR 7: Rounding", "feature/round");
        var merge = Head;

        var ado = new FakeAdo(new()
        {
            ["pr context 7"] = $$$"""
                {"pullRequest": {"pullRequestId": 7, "title": "Rounding", "status": "completed", "targetRefName": "refs/heads/main"},
                 "commits": [{"commitId": "{{{a}}}"}, {"commitId": "{{{b}}}"}],
                 "workItems": [{"id": 4711, "fields": {"System.Title": "Rounding"}}]}
                """,
        });
        var e = Export(new AdoHistory(ado), since);

        var pr = Assert.Single(e.PullRequests);
        Assert.Equal(("#7", "confirmed", "merge", merge, "main"), (pr.Id, pr.Status, pr.Strategy, pr.MergeCommit, pr.Target));
        Assert.Equal([merge, b, a], pr.Commits);
        Assert.Equal("verified", pr.WorkItemsStatus);
        Assert.Equal([("AB#4711", "provider", "pr-link")], pr.WorkItems.Select(l => (l.WorkItem, l.Source, l.Kind)));

        Assert.Equal("merge", Of(e, merge).Kind);
        Assert.Empty(Of(e, merge).Files); // a merge's changes are its parents'
        Assert.All(new[] { merge, a, b }, s => Assert.Equal(("#7", "exact"), (Of(e, s).PullRequest.Id, Of(e, s).PullRequest.Resolution)));
        Assert.Equal(["merged-branch", "provider"], Of(e, a).PullRequest.Basis);
        // The commit's own link stays the commit's: the PR's work item is not copied onto it, nor the reverse.
        Assert.Equal(["AB#100"], Of(e, a).WorkItems.Select(l => l.WorkItem));
        Assert.Empty(Of(e, b).WorkItems);
    }

    [Fact]
    public void Squash_commit_is_exact_only_when_the_provider_names_it_as_the_merge_commit()
    {
        var since = Head;
        Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.07m; }\n\n    decimal Round(decimal v) { return v; }\n}\n");
        var squash = Commit("Reduced rate (#9)");

        ProviderPr Pr(string mergeCommit) => new("#9", "Reduced rate", "https://example/pull/9", "main", true, mergeCommit, [],
            [new("#3", "provider", "pr-link", "closingIssuesReferences")]);
        var exact = Export(new FakeProvider("github", new() { [9] = Pr(squash) }), since);
        Assert.Equal(("#9", "exact"), (Single(exact).PullRequest.Id, Single(exact).PullRequest.Resolution));
        Assert.Equal(("squash", "confirmed", "verified"), (exact.PullRequests[0].Strategy, exact.PullRequests[0].Status, exact.PullRequests[0].WorkItemsStatus));

        // The provider's merge commit isn't this one (a cherry-pick, or a commit not in this clone): not confirmed.
        var other = Export(new FakeProvider("github", new() { [9] = Pr(new string('a', 40)) }), since);
        Assert.Equal("inferred", Single(other).PullRequest.Resolution);
        Assert.Equal(("unconfirmed", "unavailable"), (other.PullRequests[0].Status, other.PullRequests[0].WorkItemsStatus));
        Assert.Empty(other.PullRequests[0].WorkItems); // unconfirmed links are not reported as the PR's
        Assert.Contains(other.Warnings, w => w.Contains("aaaaaaaaaaaa"));
    }

    [Fact]
    public void Issue_reference_inside_a_merged_branch_is_not_a_squash_merge()
    {
        var since = Head;
        _git.Run("checkout", "-q", "-b", "feature/x");
        var inner = Commit("Check the provider first (#75)"); // "(#75)" names an issue here
        _git.Run("checkout", "-q", "main");
        _git.Run("merge", "-q", "--no-ff", "-m", "Merge pull request #78: preflight (#75, part 2/3)", "feature/x");

        var e = Export(new LocalHistory(), since);
        Assert.Equal(["#78"], e.PullRequests.Select(p => p.Id));
        Assert.Equal(("#78", "inferred", "local"), (Of(e, inner).PullRequest.Id, Of(e, inner).PullRequest.Resolution, e.PullRequests[0].Status));
    }

    [Fact]
    public void Commit_merged_by_two_pull_requests_is_ambiguous()
    {
        var since = Head;
        _git.Run("checkout", "-q", "-b", "dev");
        _git.Run("checkout", "-q", "-b", "topic");
        var x = Commit("Shared change");
        _git.Run("checkout", "-q", "main");
        _git.Run("merge", "-q", "--no-ff", "-m", "Merged PR 2: topic to main", "topic");
        _git.Run("checkout", "-q", "dev");
        _git.Run("merge", "-q", "--no-ff", "-m", "Merged PR 1: topic to dev", "topic");
        _git.Run("checkout", "-q", "main");
        _git.Run("merge", "-q", "--no-ff", "-m", "Merge branch dev", "dev");

        var e = Export(new LocalHistory(), since);
        var c = Of(e, x).PullRequest;
        Assert.Equal((null, "ambiguous"), (c.Id, c.Resolution));
        Assert.Equal(["#1", "#2"], c.Candidates);
    }

    [Fact]
    public void Ranges_say_which_lines_changed_so_a_method_body_is_not_the_whole_file()
    {
        var since = Head;
        Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.19m; }\n\n    decimal Round(decimal v) { return decimal.Round(v, 2); }\n}\n");
        Commit("Round body");
        var file = Assert.Single(Single(Export(new LocalHistory(), since)).Files);
        Assert.Equal(("src/Tax.cs", "modified", "lines"), (file.Path, file.Status, file.Granularity));
        Assert.Equal([new HistoryRange(5, 1, 5, 1)], file.Ranges); // Round's body only; Rate (line 3) untouched
    }

    [Fact]
    public void Renames_keep_both_paths_and_a_pure_rename_has_no_line_evidence()
    {
        var since = Head;
        _git.Run("mv", "src/Tax.cs", "src/Taxes.cs");
        var pure = Commit("Rename");
        var f = Assert.Single(Of(Export(new LocalHistory(), since), pure).Files);
        Assert.Equal(("src/Taxes.cs", "src/Tax.cs", "renamed", 100, "file"), (f.Path, f.OldPath, f.Status, f.Similarity, f.Granularity));
        Assert.Empty(f.Ranges);

        since = Head;
        _git.Run("mv", "src/Taxes.cs", "src/Tax Rules.cs");
        Write("src/Tax Rules.cs", "class Tax\n{\n    decimal Rate() { return 0.20m; }\n\n    decimal Round(decimal v) { return v; }\n}\n");
        var moved = Commit("Rename and change");
        var g = Assert.Single(Of(Export(new LocalHistory(), since), moved).Files);
        Assert.Equal(("src/Tax Rules.cs", "src/Taxes.cs", "renamed", "lines"), (g.Path, g.OldPath, g.Status, g.Granularity));
        Assert.Equal([new HistoryRange(3, 1, 3, 1)], g.Ranges);
    }

    [Fact]
    public void Added_deleted_and_binary_files()
    {
        var since = Head;
        Write("src/New.cs", "class New { }\n");
        File.Delete(Path.Combine(_dir, "README.md"));
        File.WriteAllBytes(Path.Combine(_dir, "logo.png"), [0, 1, 2, 0, 255]);
        Commit("Files");
        var c = Single(Export(new LocalHistory(), since));
        var byPath = c.Files.ToDictionary(f => f.Path);
        Assert.Equal(("added", "lines", new HistoryRange(0, 0, 1, 1)), (byPath["src/New.cs"].Status, byPath["src/New.cs"].Granularity, byPath["src/New.cs"].Ranges.Single()));
        Assert.Equal(("deleted", new HistoryRange(1, 1, 0, 0)), (byPath["README.md"].Status, byPath["README.md"].Ranges.Single()));
        Assert.Equal(("added", "file"), (byPath["logo.png"].Status, byPath["logo.png"].Granularity));
    }

    [Fact]
    public void Offline_provider_leaves_pull_requests_unconfirmed_never_empty()
    {
        var since = Head;
        Commit("Change (#5)");
        var provider = new FakeProvider("github", []);
        var e = Export(provider, since);
        var pr = Assert.Single(e.PullRequests);
        Assert.Equal(("unconfirmed", "unavailable", "github is offline"), (pr.Status, pr.WorkItemsStatus, pr.Problem));
        Assert.Equal("inferred", Single(e).PullRequest.Resolution);
        Assert.Contains("#5: github is offline", e.Warnings);
        Assert.Equal([5], provider.Calls);

        // ado: denied is the same; its message carries no credential.
        var ado = Export(new AdoHistory(new FakeAdo([])), since);
        Assert.Equal("unavailable", ado.PullRequests[0].WorkItemsStatus);
        Assert.StartsWith("ado pr context 5 failed", ado.PullRequests[0].Problem);
    }

    [Fact]
    public void Ado_pull_request_not_completed_or_with_other_commits_is_unconfirmed()
    {
        var since = Head;
        _git.Run("checkout", "-q", "-b", "f");
        Commit("work");
        _git.Run("checkout", "-q", "main");
        _git.Run("merge", "-q", "--no-ff", "-m", "Merged PR 8: f", "f");
        string Context(string status, string commit) => $$"""
            {"pullRequest": {"pullRequestId": 8, "status": "{{status}}", "targetRefName": "refs/heads/main"}, "commits": [{"commitId": "{{commit}}"}], "workItems": []}
            """;
        var head = _git.RevParse("f");
        Assert.Equal("confirmed", Export(new AdoHistory(new FakeAdo(new() { ["pr context 8"] = Context("completed", head) })), since).PullRequests[0].Status);
        Assert.Equal("unconfirmed", Export(new AdoHistory(new FakeAdo(new() { ["pr context 8"] = Context("active", head) })), since).PullRequests[0].Status);
        var other = Export(new AdoHistory(new FakeAdo(new() { ["pr context 8"] = Context("completed", new string('b', 40)) })), since);
        Assert.Equal("unconfirmed", other.PullRequests[0].Status);
        Assert.Contains("don't include the merged head", other.PullRequests[0].Problem);
        Assert.Equal("verified", Export(new AdoHistory(new FakeAdo(new() { ["pr context 8"] = Context("completed", head) })), since).PullRequests[0].WorkItemsStatus);
    }

    [Fact]
    public void Limit_bounds_the_export_and_says_when_it_cut()
    {
        for (int i = 0; i < 4; i++) Commit($"c{i}");
        var e = Export(new LocalHistory(), limit: 3);
        Assert.Equal((3, true), (e.Commits.Count, e.Range.Truncated));
        Assert.StartsWith("truncated", e.Warnings[0]);
        Assert.False(Export(new LocalHistory(), limit: 10).Range.Truncated);
        Assert.Throws<ArgumentException>(() => Export(new LocalHistory(), limit: 0));
        Assert.Throws<ArgumentException>(() => Export(new LocalHistory(), limit: History.MaxLimit + 1));
        Assert.StartsWith("unknown branch or ref", Assert.Throws<InvalidOperationException>(() => Export(new LocalHistory(), since: "no-such-ref")).Message);
        Assert.Empty(Export(new LocalHistory(), since: "main").Commits);
    }

    [Fact]
    public void Export_is_deterministic_and_the_cache_only_reuses_identical_ranges()
    {
        var since = Head;
        Commit("a AB#1");
        _git.Run("checkout", "-q", "-b", "f");
        Commit("b");
        _git.Run("checkout", "-q", "main");
        _git.Run("merge", "-q", "--no-ff", "-m", "Merged PR 3: f", "f");

        var store = WorkspaceStore.At(Path.Combine(_dir, ".git", "gitwizz"));
        var fresh = History.Json(Export(new LocalHistory(), since));
        Assert.Equal(fresh, History.Json(Export(new LocalHistory(), since)));
        Assert.Equal(fresh, History.Json(Export(new LocalHistory(), since, store: store)));
        Assert.Equal(1, store.Count("results"));
        Assert.Equal(fresh, History.Json(Export(new LocalHistory(), since, store: store))); // reused
        Assert.Equal(1, store.Count("results"));

        Export(new LocalHistory(), since, limit: 2, store: store); // another limit: another entry
        Commit("c");
        var moved = Export(new LocalHistory(), since, store: store); // main moved: recomputed, not reused
        Assert.Equal(4, moved.Commits.Count);
        Assert.Equal(3, store.Count("results"));

        var doc = JsonDocument.Parse(fresh).RootElement;
        Assert.Equal(History.Schema, doc.GetProperty("schema").GetString());
        Assert.Equal(["schema", "provider", "repository", "range", "sources", "commits", "pullRequests", "warnings"],
            doc.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Repository_identity_never_carries_credentials()
    {
        Assert.Equal("https://dev.azure.com/org/project/_git/repo", History.Sanitize("https://user:s3cret@dev.azure.com/org/project/_git/repo?token=x#f"));
        Assert.Equal("git@github.com:o/r.git", History.Sanitize("git@github.com:o/r.git"));
        _git.Run("remote", "add", "origin", "https://build:pat-123@tfs.example/coll/p/_git/r");
        var json = History.Json(Export(new LocalHistory(), limit: 5));
        Assert.DoesNotContain("pat-123", json);
        Assert.Contains("\"repository\": \"https://tfs.example/coll/p/_git/r\"", json);
    }

    [Fact]
    public void Patch_paths_are_unquoted()
    {
        Assert.Equal("a\tb.cs", History.Unquote("\"a\\tb.cs\""));
        Assert.Equal("ü.cs", History.Unquote("\"\\303\\274.cs\""));
        Assert.Equal("plain.cs", History.Unquote("plain.cs"));
        var files = History.ParsePatch("diff --git \"a/x\\ty.cs\" \"b/x\\ty.cs\"\nold mode 100644\nnew mode 100755\n");
        Assert.Equal(("x\ty.cs", "modified", "file"), (files[0].Path, files[0].Status, files[0].Granularity));
        // Removed and added lines that look like file headers stay content.
        var sql = History.ParsePatch("diff --git a/q.sql b/q.sql\nindex 1..2 100644\n--- a/q.sql\n+++ b/q.sql\n@@ -2 +2 @@\n--- old comment\n+++ new\n@@ -9,0 +10,2 @@\n+a\n+b\n");
        Assert.Equal(("q.sql", null), (sql[0].Path, sql[0].OldPath));
        Assert.Equal([new HistoryRange(2, 1, 2, 1), new HistoryRange(9, 0, 10, 2)], sql[0].Ranges);
    }

    [Fact]
    public void Provider_answers_are_parsed()
    {
        var gh = GitHubHistory.Parse(JsonDocument.Parse("""
            {"number": 12, "title": "T", "state": "MERGED", "url": "u", "baseRefName": "main", "mergeCommit": {"oid": "abc"},
             "closingIssuesReferences": [{"number": 4}, {"number": 2}]}
            """).RootElement);
        Assert.Equal(("#12", true, "abc", "main"), (gh.Id, gh.Completed, gh.MergeCommit, gh.Target));
        Assert.Equal(["#4", "#2"], gh.WorkItems!.Select(w => w.WorkItem));
        Assert.Null(GitHubHistory.Parse(JsonDocument.Parse("""{"number": 1, "state": "OPEN"}""").RootElement).WorkItems);
    }

    [Fact]
    public void Github_closing_issue_urls_preserve_repository_identity()
    {
        var pr = GitHubHistory.Parse(JsonDocument.Parse("""
            {"number": 12, "state": "MERGED", "closingIssuesReferences": [
                {"number": 4, "url": "https://github.com/acme/one/issues/4"},
                {"number": 4, "url": "https://github.com/acme/two/issues/4"}]}
            """).RootElement);
        Assert.Equal(["#4", "#4"], pr.WorkItems!.Select(w => w.WorkItem));
        Assert.Equal(["https://github.com/acme/one/issues/4", "https://github.com/acme/two/issues/4"],
            pr.WorkItems!.Select(w => w.Uri));
    }

    [Fact]
    public void Cli_takes_history_options()
    {
        var o = Cli.Parse(["--since", "v1.0", "--until", "main", "--limit", "50", "-o", "h.json", "--no-cache"], "history");
        Assert.Equal(("v1.0", "main", "50", "h.json", "true"), (o["since"], o["until"], o["limit"], o["output"], o["no-cache"]));
        Assert.Throws<ArgumentException>(() => Cli.Parse(["--prs", "1"], "history"));
    }

    [Fact]
    public void Ado_commit_links_are_verified_per_commit_and_kept_apart_from_message_mentions()
    {
        var since = Head;
        var linked = Commit("Round tax #12"); // the message mentions #12; the server links 4711
        var unlinked = Commit("Docs");
        var denied = Commit("Secret");
        var older = Commit("Older server");
        var other = Commit("Other commit answered");
        var foreign = Commit("Other repository answered");
        var timeout = Commit("Slow");
        var ado = new ScriptedAdo(a => a switch
        {
            ["commit", "context", var s] when s == linked => CommitContext(s, items: """
                [{"id": 4711, "source": "commit-workitem", "url": "https://tfs.example/coll/_apis/wit/workItems/4711", "webUrl": "https://tfs.example/coll/_workitems/edit/4711"}]
                """),
            ["commit", "context", var s] when s == unlinked => CommitContext(s),
            ["commit", "context", var s] when s == denied => throw new AdoCommandException(4, "ado commit context failed: permission denied (exit 4): TF401019"),
            ["commit", "context", var s] when s == older => CommitContext(s, status: "unavailable"),
            ["commit", "context", var s] when s == other => CommitContext(new string('c', 40)),
            ["commit", "context", var s] when s == foreign => CommitContext(s, repo: "frontend"),
            ["commit", "context", var s] when s == timeout => throw new AdoCommandException(124, "ado commit context timed out"),
            _ => throw new AdoCommandException(5, "ado commit context failed: not found (exit 5)"),
        });
        var e = Export(new AdoHistory(ado, "backend"), since);

        var l = Of(e, linked).ProviderLinks;
        Assert.Equal("verified", l.Status);
        var item = Assert.Single(l.Items!);
        Assert.Equal(("AB#4711", "provider", "commit-link", "https://tfs.example/coll/_workitems/edit/4711"), (item.WorkItem, item.Source, item.Kind, item.Uri));
        // The mention stays a mention: it is not upgraded to a provider link, and the provider link is not copied into it.
        Assert.Equal([("AB#12", "commit-message", "mention")], Of(e, linked).WorkItems.Select(w => (w.WorkItem, w.Source, w.Kind)));

        Assert.Equal(("verified", 0), (Of(e, unlinked).ProviderLinks.Status, Of(e, unlinked).ProviderLinks.Items!.Count)); // verified: no links
        Assert.Equal(("denied", null), (Of(e, denied).ProviderLinks.Status, Of(e, denied).ProviderLinks.Items));
        foreach (var (sha, why) in new[] { (older, "server did not report"), (other, "answered for commit cccc"), (foreign, "repository frontend, not backend"), (timeout, "timed out") })
        {
            var x = Of(e, sha).ProviderLinks;
            Assert.Equal(("unavailable", null), (x.Status, x.Items));
            Assert.Contains(why, x.Problem);
        }
        Assert.Contains(e.Warnings, w => w.StartsWith("provider commit links: 2 verified, 4 unavailable, 1 denied"));
        Assert.Equal(7, ado.Calls.Count(c => c.StartsWith("commit context")));
        Assert.All(ado.Calls.Where(c => c.StartsWith("commit context")), c => Assert.Matches("^commit context [0-9a-f]{40}$", c)); // full SHAs only
        Assert.Equal("ado commit context, per commit (see commits[].providerLinks)", e.Sources.ProviderCommitLinks);
    }

    [Fact]
    public void Commit_lookups_are_bounded_and_stop_when_ado_cannot_be_asked()
    {
        var since = Head;
        for (int i = 0; i < 5; i++) Commit($"c{i}");
        var ok = new ScriptedAdo(a => CommitContext(a[2]));
        var e = Export(new AdoHistory(ok), since, commitLookups: 2);
        Assert.Equal(["verified", "verified", "unknown", "unknown", "unknown"], e.Commits.Select(c => c.ProviderLinks.Status)); // newest first
        Assert.Equal("not looked up: over --commit-lookups 2", e.Commits[4].ProviderLinks.Problem);
        Assert.Equal(2, ok.Calls.Count);
        Assert.All(Export(new AdoHistory(ok), since, commitLookups: 0).Commits, c => Assert.Equal("unknown", c.ProviderLinks.Status));
        Assert.Throws<ArgumentException>(() => Export(new AdoHistory(ok), since, commitLookups: -1));

        // Not logged in: one call fails, the rest are unavailable for the same reason without asking again.
        var offline = new ScriptedAdo(_ => throw new AdoCommandException(3, "ado commit context failed: not logged in to Azure DevOps (exit 3): run ado auth login"));
        var o = Export(new AdoHistory(offline), since);
        Assert.All(o.Commits, c => Assert.Equal(("unavailable", "ado commit context failed: not logged in to Azure DevOps (exit 3): run ado auth login"),
            (c.ProviderLinks.Status, c.ProviderLinks.Problem)));
        Assert.InRange(offline.Calls.Count, 1, History.LookupConcurrency);

        // GitHub keeps no commit links: never asked, unknown with the reason.
        var gh = Export(new FakeProvider("github", []), since);
        Assert.All(gh.Commits, c => Assert.Equal(("unknown", "unavailable: fake"), (c.ProviderLinks.Status, c.ProviderLinks.Problem)));
    }

    [Fact]
    public void Merge_and_squash_commits_are_looked_up_and_a_pr_link_stays_a_candidate()
    {
        var since = Head;
        _git.Run("checkout", "-q", "-b", "f");
        var work = Commit("work");
        _git.Run("checkout", "-q", "main");
        _git.Run("merge", "-q", "--no-ff", "-m", "Merged PR 8: f", "f");
        var merge = Head;
        var squash = Commit("Merged PR 9: squashed");
        var ado = new ScriptedAdo(a => a switch
        {
            ["pr", "context", "8"] => $$"""{"pullRequest": {"pullRequestId": 8, "status": "completed"}, "commits": [{"commitId": "{{work}}"}], "workItems": [{"id": 5}]}""",
            ["pr", "context", "9"] => throw new AdoCommandException(4, "ado pr context failed: permission denied (exit 4): x"),
            ["commit", "context", var s] when s == merge => CommitContext(s, items: """[{"id": 5, "source": "commit-workitem", "webUrl": "w"}]"""),
            ["commit", "context", var s] => CommitContext(s),
            _ => throw new InvalidOperationException("unexpected"),
        });
        var e = Export(new AdoHistory(ado), since);
        Assert.Equal(["AB#5"], Of(e, merge).ProviderLinks.Items!.Select(i => i.WorkItem)); // the server links the PR's merge commit
        Assert.Empty(Of(e, work).ProviderLinks.Items!); // the PR's work item is not this commit's verified link
        Assert.Empty(Of(e, work).WorkItems);
        Assert.Equal(["AB#5"], e.PullRequests.Single(p => p.Id == "#8").WorkItems.Select(w => w.WorkItem));
        Assert.Equal("verified", Of(e, squash).ProviderLinks.Status);
        var denied = e.PullRequests.Single(p => p.Id == "#9");
        Assert.Equal(("unconfirmed", "denied"), (denied.Status, denied.WorkItemsStatus));
    }

    [Fact]
    public void Ado_answers_and_repository_names_are_parsed()
    {
        Assert.Equal("backend", AdoHistory.RepositoryName("https://tfs.example/coll/Platform/_git/backend"));
        Assert.Equal("my repo", AdoHistory.RepositoryName("https://dev.azure.com/o/p/_git/my%20repo/"));
        Assert.Null(AdoHistory.RepositoryName("git@github.com:o/r.git"));
        Assert.Null(AdoHistory.RepositoryName(null));
        var sha = new string('a', 40);
        var upper = AdoHistory.ParseCommitLinks(JsonDocument.Parse(CommitContext(sha.ToUpperInvariant())).RootElement, sha, null);
        Assert.Equal("verified", upper.Status); // SHAs compare without case
        var v2 = AdoHistory.ParseCommitLinks(JsonDocument.Parse(CommitContext(sha, schema: "ado.commit-context/v2")).RootElement, sha, null);
        Assert.Equal("unavailable", v2.Status);
        Assert.Contains("unsupported schema ado.commit-context/v2", v2.Problem);
        var broken = new AdoHistory(new ScriptedAdo(_ => "not json")).CommitLinks(sha);
        Assert.Equal("unavailable", broken.Status);
        Assert.Equal("https://tfs/x failed", History.Redact("https://build:pat-123@tfs/x failed"));
        // committedAt has one form whatever the git version writes for UTC.
        Assert.Equal("2026-10-01T08:36:00+00:00", History.IsoDate("2026-10-01T08:36:00Z"));
        Assert.Equal("2026-10-01T08:36:00+00:00", History.IsoDate("2026-10-01T08:36:00+00:00"));
        Assert.Equal("2026-10-01T10:36:00+02:00", History.IsoDate("2026-10-01T10:36:00+02:00"));
    }

    [Fact]
    public void Cli_takes_commit_lookups()
    {
        Assert.Equal("10", Cli.Parse(["--commit-lookups", "10"], "history")["commit-lookups"]);
    }
}
