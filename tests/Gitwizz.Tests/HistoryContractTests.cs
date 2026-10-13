using System.Text.Json;
using System.Text.Json.Nodes;
using Gitwizz;
using Json.Schema;

namespace Gitwizz.Tests;

/// <summary>
/// The gitwizz.history/v2 contract (docs/history): the example document is the export of a scripted repository with
/// fixed dates and identity, so its SHAs and content are reproducible; every export validates against the schema.
/// Set GITWIZZ_UPDATE_GOLDEN=1 to rewrite the example after an intended change (and review the diff).
/// </summary>
public class HistoryContractTests : IDisposable
{
    static readonly string Root = FindRoot();
    static readonly string Docs = Path.Combine(Root, "docs", "history");
    static readonly JsonSchema Schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(Docs, "gitwizz.history.v2.schema.json")));

    readonly string _dir = Directory.CreateTempSubdirectory("gitwizz-contract").FullName;
    readonly Git _git;
    int _tick;

    public HistoryContractTests() => _git = new Git(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gitwizz.slnx"))) return d.FullName;
        throw new InvalidOperationException("gitwizz.slnx not found above the test directory");
    }

    /// <summary>git with a fixed identity and a clock that ticks one minute per call, so every SHA is reproducible.</summary>
    string G(params string[] args)
    {
        var date = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero).AddMinutes(_tick++).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Ada", ["GIT_AUTHOR_EMAIL"] = "ada@example.com", ["GIT_AUTHOR_DATE"] = date,
            ["GIT_COMMITTER_NAME"] = "Ada", ["GIT_COMMITTER_EMAIL"] = "ada@example.com", ["GIT_COMMITTER_DATE"] = date,
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = "/dev/null",
        };
        var r = Git.Exec(_dir, "git", args, env: env);
        if (r.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.Stderr}");
        return r.Stdout.Trim();
    }

    void Write(string f, string c) { var p = Path.Combine(_dir, f); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, c); }
    string Commit(string m) { G("add", "-A"); G("commit", "-q", "--no-gpg-sign", "-m", m); return G("rev-parse", "HEAD"); }

    sealed class ScriptedAdo(Func<string[], string> answer) : IAdoClient
    {
        public string Run(params string[] args) => answer(args);
    }

    /// <summary>
    /// main: v1.0, a merged PR 7 (a line change AB#100, a pure rename, a binary file), a squash-merged PR 9 whose PR
    /// lookup is denied, a zero-line deletion pushed directly that mentions #12, and a commit both PR 10 and PR 11 merge.
    /// </summary>
    (HistoryExport Export, Dictionary<string, string> Shas) Scenario()
    {
        G("init", "-q", "-b", "main");
        G("remote", "add", "origin", "https://build:pat-123@tfs.example/coll/Platform/_git/backend?x=1");
        Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.19m; }\n\n    decimal Round(decimal v) { return v; }\n}\n");
        Write("README.md", "# demo\n");
        Commit("init");
        G("tag", "v1.0");
        var s = new Dictionary<string, string>();

        G("checkout", "-q", "-b", "feature/round");
        Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.19m; }\n\n    decimal Round(decimal v) { return Math.Round(v, 2); }\n}\n");
        s["line"] = Commit("Round half up AB#100");
        Directory.CreateDirectory(Path.Combine(_dir, "docs"));
        G("mv", "README.md", "docs/README.md");
        s["rename"] = Commit("Move readme");
        File.WriteAllBytes(Path.Combine(_dir, "logo.png"), [0, 1, 2, 0, 255]);
        s["binary"] = Commit("Logo");
        G("checkout", "-q", "main");
        G("merge", "-q", "--no-ff", "--no-gpg-sign", "-m", "Merged PR 7: Rounding", "feature/round");
        s["merge"] = G("rev-parse", "HEAD");

        Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.07m; }\n\n    decimal Round(decimal v) { return Math.Round(v, 2); }\n}\n");
        s["squash"] = Commit("Merged PR 9: Reduced rate");
        Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.07m; }\n}\n");
        s["delete"] = Commit("Drop Round, see #12");

        // Two PRs merge the same commit (into main, and into dev that is merged later): its attribution is ambiguous.
        G("checkout", "-q", "-b", "dev");
        G("checkout", "-q", "-b", "shared");
        Write("src/Shared.cs", "class Shared { }\n");
        s["shared"] = Commit("Shared helper");
        G("checkout", "-q", "main");
        G("merge", "-q", "--no-ff", "--no-gpg-sign", "-m", "Merged PR 10: shared to main", "shared");
        G("checkout", "-q", "dev");
        G("merge", "-q", "--no-ff", "--no-gpg-sign", "-m", "Merged PR 11: shared to dev", "shared");
        G("checkout", "-q", "main");
        G("merge", "-q", "--no-ff", "--no-gpg-sign", "-m", "Merge branch dev", "dev");

        var ado = new ScriptedAdo(a => a switch
        {
            ["pr", "context", "7"] => $$"""
                {"pullRequest": {"pullRequestId": 7, "title": "Rounding", "status": "completed", "targetRefName": "refs/heads/main"},
                 "commits": [{"commitId": "{{s["line"]}}"}, {"commitId": "{{s["rename"]}}"}, {"commitId": "{{s["binary"]}}"}],
                 "workItems": [{"id": 4711}]}
                """,
            ["pr", "context", "9"] => throw new AdoCommandException(4, "ado pr context failed: permission denied (exit 4): TF401027"),
            ["pr", "context", var n] => throw new AdoCommandException(5, $"ado pr context failed: not found (exit 5): pull request {n}"),
            ["commit", "context", var c] when c == s["squash"] => Context(c, "verified", """
                [{"id": 4712, "source": "commit-workitem", "url": "https://tfs.example/coll/_apis/wit/workItems/4712", "webUrl": "https://tfs.example/coll/_workitems/edit/4712"}]
                """),
            ["commit", "context", var c] when c == s["merge"] => Context(c, "unavailable", null),
            ["commit", "context", var c] when c == s["line"] => throw new AdoCommandException(4, "ado commit context failed: permission denied (exit 4): TF401019"),
            ["commit", "context", var c] => Context(c, "verified", "[]"),
            _ => throw new InvalidOperationException("unexpected ado call"),
        });
        var export = History.Export(_git, WorkspaceStore.Off, new AdoHistory(ado, AdoHistory.RepositoryName(History.RepositoryIdentity(_git))),
            "v1.0", "main", History.DefaultLimit);
        return (export, s);

        static string Context(string sha, string status, string? items) => $$"""
            {"schema": "ado.commit-context/v1", "server": "https://tfs.example/coll", "project": "Platform",
             "repository": {"id": "00000000-0000-0000-0000-000000000001", "name": "backend"},
             "commit": {"commitId": "{{sha}}", "parents": [], "comment": ""},
             "workItems": {"status": "{{status}}"{{(items != null ? ", \"items\": " + items : "")}}},
             "warnings": [{{(items == null ? "\"work item links unavailable: the server did not report work items for the commit\"" : "")}}]}
            """;
    }

    static EvaluationResults Evaluate(JsonNode? doc) =>
        Schema.Evaluate(JsonDocument.Parse(doc!.ToJsonString()).RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

    static void AssertValid(string json)
    {
        var result = Evaluate(JsonNode.Parse(json));
        Assert.True(result.IsValid, string.Join("\n", (result.Details ?? []).Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key}: {e.Value}")).Take(20)));
    }

    [Fact]
    public void Example_document_is_the_export_of_the_scripted_repository()
    {
        var json = History.Json(Scenario().Export) + "\n";
        var path = Path.Combine(Docs, "example.json");
        if (Environment.GetEnvironmentVariable("GITWIZZ_UPDATE_GOLDEN") == "1") File.WriteAllText(path, json);
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), json);
        AssertValid(json);
    }

    [Fact]
    public void Export_covers_every_case_the_contract_names()
    {
        var (e, s) = Scenario();
        HistoryCommit C(string k) => e.Commits.Single(c => c.Sha == s[k]);
        Assert.Equal(History.Schema, e.Schema);
        Assert.Equal("https://tfs.example/coll/Platform/_git/backend", e.Repository);
        Assert.DoesNotContain("pat-123", History.Json(e));
        Assert.DoesNotContain("ada@example.com", History.Json(e));

        Assert.Equal(("lines", "exact"), (C("line").Files.Single().Granularity, C("line").PullRequest.Resolution));
        Assert.Equal(("renamed", "README.md", "file"), (C("rename").Files.Single().Status, C("rename").Files.Single().OldPath, C("rename").Files.Single().Granularity));
        Assert.Equal(("file", 0), (C("binary").Files.Single().Granularity, C("binary").Files.Single().Ranges.Count));
        Assert.Equal(("merge", 0), (C("merge").Kind, C("merge").Files.Count));
        Assert.Equal(new HistoryRange(4, 2, 3, 0), C("delete").Files.Single().Ranges.Single()); // zero lines left: newCount 0
        Assert.Equal(("unknown", "AB#12", "mention"), (C("delete").PullRequest.Resolution, C("delete").WorkItems.Single().WorkItem, C("delete").WorkItems.Single().Kind));
        Assert.Equal("ambiguous", C("shared").PullRequest.Resolution);
        Assert.Equal(["#10", "#11"], C("shared").PullRequest.Candidates);

        Assert.Equal(["denied", "unavailable", "verified"], new[] { "line", "merge", "squash" }.Select(k => C(k).ProviderLinks.Status).Order());
        Assert.Equal(["AB#4712"], C("squash").ProviderLinks.Items!.Select(i => i.WorkItem));
        Assert.Empty(C("rename").ProviderLinks.Items!); // verified: no links
        Assert.Equal(("squash", "unconfirmed", "denied"), (e.PullRequests.Single(p => p.Id == "#9").Strategy, e.PullRequests.Single(p => p.Id == "#9").Status,
            e.PullRequests.Single(p => p.Id == "#9").WorkItemsStatus));
        Assert.Equal(["AB#4711"], e.PullRequests.Single(p => p.Id == "#7").WorkItems.Select(w => w.WorkItem));
    }

    [Fact]
    public void Every_provider_and_truncated_shallow_exports_validate()
    {
        Scenario();
        AssertValid(History.Json(History.Export(_git, WorkspaceStore.Off, new LocalHistory(), "v1.0", "main", 3)));
        AssertValid(History.Json(History.Export(_git, WorkspaceStore.Off, new LocalHistory(), null, "main", History.DefaultLimit)));
        Assert.True(History.Export(_git, WorkspaceStore.Off, new LocalHistory(), "v1.0", "main", 3).Range.Truncated);

        // A shallow clone says so in range.shallow, not only in a warning.
        var clone = Path.Combine(_dir, "..", Path.GetFileName(_dir) + "-shallow");
        try
        {
            Assert.Equal(0, Git.Exec(Path.GetTempPath(), "git", ["clone", "-q", "--depth", "2", "--no-local", "file://" + _dir, clone]).ExitCode);
            var shallow = History.Export(new Git(clone), WorkspaceStore.Off, new LocalHistory(), null, "HEAD", History.DefaultLimit);
            Assert.True(shallow.Range.Shallow);
            AssertValid(History.Json(shallow));
        }
        finally { if (Directory.Exists(clone)) Directory.Delete(clone, true); }
    }

    [Fact]
    public void Schema_rejects_documents_that_collapse_evidence_states()
    {
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(Docs, "example.json")))!;
        Assert.True(Evaluate(doc).IsValid);

        JsonNode Mutate(Action<JsonNode> change) { var d = doc.DeepClone(); change(d); return d; }
        var commit = doc["commits"]!.AsArray().First(c => c!["providerLinks"]!["status"]!.GetValue<string>() == "denied")!;
        var index = doc["commits"]!.AsArray().IndexOf(commit);
        // Unknown evidence must not pass as an empty verified answer, nor a message mention as a provider link.
        Assert.False(Evaluate(Mutate(d => d["commits"]![index]!["providerLinks"]!["items"] = new JsonArray())).IsValid);
        Assert.False(Evaluate(Mutate(d => d["commits"]![index]!["workItems"] = new JsonArray(JsonNode.Parse(
            """{"workItem": "AB#1", "source": "provider", "kind": "commit-link", "evidence": "x"}""")))).IsValid);
        Assert.False(Evaluate(Mutate(d => d["schema"] = "gitwizz.history/v1")).IsValid);
    }
}
