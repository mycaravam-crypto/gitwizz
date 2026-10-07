using System.Text.Json;
using Gitwizz;

namespace Gitwizz.Tests;

public class ProviderTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("gitwizz-ado").FullName;
    readonly Git _server, _git;
    readonly string _featureSha, _stackedSha;

    public ProviderTests()
    {
        var server = Path.Combine(_root, "server");
        Directory.CreateDirectory(server);
        _server = new Git(server);
        _server.Run("init", "-q", "-b", "main");
        _server.Run("config", "user.email", "t@t"); _server.Run("config", "user.name", "t");
        File.WriteAllText(Path.Combine(server, "a.txt"), "a\n");
        _server.Run("add", "-A"); _server.Run("commit", "-q", "-m", "init");
        _server.Run("checkout", "-q", "-b", "feature/tax");
        File.WriteAllText(Path.Combine(server, "tax.cs"), "class Tax { }\n");
        _server.Run("add", "-A"); _server.Run("commit", "-q", "-m", "tax");
        _featureSha = _server.RevParse("HEAD");
        _server.Run("checkout", "-q", "-b", "feature/tax-ui");
        File.WriteAllText(Path.Combine(server, "ui.cs"), "class Ui { }\n");
        _server.Run("add", "-A"); _server.Run("commit", "-q", "-m", "ui");
        _stackedSha = _server.RevParse("HEAD");
        _server.Run("checkout", "-q", "main");

        var work = Path.Combine(_root, "work");
        Git.Exec(_root, "git", ["clone", "-q", server, work]);
        _git = new Git(work);
    }

    public void Dispose() => Directory.Delete(_root, true);

    /// <summary>Answers ado commands from fixtures, as `ado ... --json` would print them.</summary>
    sealed class FakeAdo(Dictionary<string, string> responses) : IAdoClient
    {
        public List<string> Calls { get; } = [];
        public string Run(params string[] args)
        {
            var key = string.Join(' ', args);
            Calls.Add(key);
            return responses.TryGetValue(key, out var r) ? r : throw new InvalidOperationException($"ado {key} failed (exit 4): denied");
        }
    }

    string PrList() => $$"""
        [
          { "pullRequestId": 42, "title": "Reduced tax rate", "description": "Implements AB#4711", "status": "active",
            "createdBy": { "displayName": "Jane" }, "sourceRefName": "refs/heads/feature/tax", "targetRefName": "refs/heads/main",
            "lastMergeSourceCommit": { "commitId": "{{_featureSha}}" },
            "reviewers": [ { "displayName": "Bob", "vote": 10 }, { "displayName": "Ann", "vote": 0 } ] },
          { "pullRequestId": 43, "title": "Tax UI", "description": "depends on #42", "status": "active",
            "createdBy": { "displayName": "Jane" }, "sourceRefName": "refs/heads/feature/tax-ui", "targetRefName": "refs/heads/feature/tax",
            "lastMergeSourceCommit": { "commitId": "{{_stackedSha}}" },
            "reviewers": [ { "displayName": "Bob", "vote": -5 } ] },
          { "pullRequestId": 50, "title": "Release fix", "status": "active", "createdBy": { "displayName": "Jim" },
            "sourceRefName": "refs/heads/hotfix", "targetRefName": "refs/heads/release", "lastMergeSourceCommit": { "commitId": "0000000" } }
        ]
        """;

    const string Builds = """
        [
          { "id": 9, "buildNumber": "9", "status": "inProgress", "definition": { "id": 1, "name": "ci" }, "sourceBranch": "refs/pull/42/merge" },
          { "id": 8, "buildNumber": "8", "status": "completed", "result": "failed", "definition": { "id": 1, "name": "ci" }, "sourceBranch": "refs/pull/42/merge" },
          { "id": 7, "buildNumber": "7", "status": "completed", "result": "succeeded", "definition": { "id": 2, "name": "lint" }, "sourceBranch": "refs/pull/42/merge",
            "_links": { "web": { "href": "https://tfs/build/7" } } },
          { "id": 6, "buildNumber": "6", "status": "completed", "result": "failed", "definition": { "id": 1, "name": "ci" }, "sourceBranch": "refs/heads/main" }
        ]
        """;

    // As real ado prints it: pr context asks the server for title, type and state only.
    const string Context = """
        { "pullRequest": { "pullRequestId": 42 }, "commits": [], "changes": [],
          "workItems": [
            { "id": 4712, "fields": { "System.WorkItemType": "Bug", "System.Title": "Rounding", "System.State": "New" } },
            { "id": 4711, "fields": { "System.WorkItemType": "User Story", "System.Title": "Reduced VAT", "System.State": "Active" } }
          ] }
        """;

    // ado workitem show <id> --json: every field.
    const string Story = """
        { "id": 4711, "fields": { "System.WorkItemType": "User Story", "System.Title": "Reduced VAT", "System.State": "Active",
          "System.AssignedTo": { "displayName": "Jane" },
          "Microsoft.VSTS.Common.AcceptanceCriteria": "<ul><li>Food uses 7&nbsp;%</li><li>Books use 7 %</li></ul>" } }
        """;
    const string Bug = """
        { "id": 4712, "fields": { "System.WorkItemType": "Bug", "System.Title": "Rounding", "System.State": "New",
          "System.Description": "<div>Context</div><h2>Acceptance criteria</h2><div>- rounds half up</div><div>- two decimals</div>" } }
        """;

    static Dictionary<string, string> Ado(string prList) => new()
    {
        ["pr list --json --status active"] = prList, ["build list --json --limit 500"] = Builds, ["pr context 42"] = Context,
        ["workitem show 4711 --json"] = Story, ["workitem show 4712 --json"] = Bug,
    };

    [Fact]
    public void Azure_DevOps_provider_normalizes_prs_votes_builds_and_work_items()
    {
        var ado = new FakeAdo(Ado(PrList()));
        var (targetSha, prs) = AzureDevOps.Load(_git, "main", null, ado);
        Assert.Equal(_server.RevParse("main"), targetSha);
        Assert.Equal(["#42", "#43"], prs.Select(p => p.Id)); // #50 targets release; #43 is stacked on #42
        var pr = prs[0];
        Assert.Equal(("feature/tax", "main", _featureSha, "Jane"), (pr.HeadRef, pr.BaseRef, pr.HeadSha, pr.Author));
        Assert.Equal("APPROVED", pr.ReviewDecision);
        Assert.Equal([new Check("ci", "pending"), new Check("lint", "success", "https://tfs/build/7")], pr.Checks);
        Assert.Equal("PENDING", pr.CiStatus); // newest ci build is running; the older failure is superseded
        Assert.Equal("CHANGES_REQUESTED", prs[1].ReviewDecision); // waiting for author
        Assert.Empty(pr.WorkItems); // details only on request
        Assert.DoesNotContain("pr context 42", ado.Calls);

        var (_, one) = AzureDevOps.Load(_git, "main", [42], ado, details: true);
        var items = one.Single().WorkItems;
        Assert.Equal(["AB#4711", "AB#4712"], items.Select(w => w.Id));
        Assert.Equal(["Food uses 7 %", "Books use 7 %"], items[0].AcceptanceCriteria);
        Assert.Equal(["rounds half up", "two decimals"], items[1].AcceptanceCriteria);
        Assert.Equal(("User Story", "Reduced VAT", "Active"), (items[0].Type, items[0].Title, items[0].State));
        Assert.Contains("workitem show 4711 --json", ado.Calls); // criteria need the full work item, not pr context's summary

        // A work item that can't be read in full keeps its summary, without criteria.
        var noBug = Ado(PrList());
        noBug.Remove("workitem show 4712 --json");
        var (_, kept) = AzureDevOps.Load(_git, "main", [42], new FakeAdo(noBug), details: true);
        Assert.Equal(["AB#4711", "AB#4712"], kept.Single().WorkItems.Select(w => w.Id));
        Assert.Empty(kept.Single().WorkItems[1].AcceptanceCriteria);
    }

    [Fact]
    public void Azure_DevOps_works_without_build_access_and_reports_ado_errors()
    {
        var ado = new FakeAdo(new() { ["pr list --json --status active"] = PrList() });
        var (_, prs) = AzureDevOps.Load(_git, "main", [42], ado);
        Assert.Empty(prs.Single().Checks);
        Assert.Null(prs.Single().CiStatus);

        var missing = new AdoCli(_git.RepoDir, "no-such-ado-xyz");
        Assert.Contains("not found", Assert.Throws<InvalidOperationException>(() => missing.Run("pr", "list", "--json")).Message);
    }

    [Fact]
    public void Context_json_is_provider_neutral_and_stable()
    {
        var ado = new FakeAdo(Ado(PrList()));
        var (sha, prs) = AzureDevOps.Load(_git, "main", [42], ado, details: true);
        var pr = prs.Single();
        Analyzer.Analyze(_git, sha, pr);
        var json = Report.Context(_git, pr, "azure-devops", "main", sha);
        Assert.Equal(json, Report.Context(_git, pr, "azure-devops", "main", sha));
        var doc = JsonDocument.Parse(json).RootElement;
        Assert.Equal("gitwizz.context/v1", doc.GetProperty("schema").GetString());
        Assert.Equal(_featureSha, doc.GetProperty("pr").GetProperty("source").GetProperty("sha").GetString());
        Assert.Equal("tax.cs", doc.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal("approved", doc.GetProperty("reviewers")[1].GetProperty("vote").GetString());
        Assert.Equal("PENDING", doc.GetProperty("policy").GetProperty("checks").GetString());
        Assert.Equal(2, doc.GetProperty("workItems")[0].GetProperty("acceptanceCriteria").GetArrayLength());
        Assert.Equal("tax", doc.GetProperty("commits")[0].GetProperty("subject").GetString());
    }

    [Fact]
    public void Azure_DevOps_next_step_merges_with_ado()
    {
        var plan = new Plan { Target = "main", Strategy = MergeStrategy.Squash, Provider = "azure-devops" };
        plan.Steps.Add(new PlanStep(new PullRequest { Id = "#42", HeadSha = "x" }, 0, "", []));
        Assert.Equal("ado pr merge 42 --yes --squash", Report.NextCommand(plan));
        Assert.True(AzureDevOps.IsRemote("https://tfs.company.local/tfs/DefaultCollection/Platform/_git/billing"));
        Assert.True(AzureDevOps.IsRemote("https://dev.azure.com/org/project/_git/repo"));
        Assert.False(AzureDevOps.IsRemote("https://github.com/o/r.git"));
    }

    [Fact]
    public void GitHub_reviews_checks_and_issues_normalize_the_same_way()
    {
        JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        Assert.Equal([new Reviewer("ann", "approved"), new Reviewer("bob", "changes-requested")],
            Providers.Reviews(J("""[{"author":{"login":"ann"},"state":"APPROVED"},{"author":{"login":"bob"},"state":"CHANGES_REQUESTED"}]""")));
        Assert.Equal([new Check("build", "success"), new Check("e2e", "pending"), new Check("lint", "failure", "https://ci/1")],
            Providers.Checks(J("""[{"name":"lint","status":"COMPLETED","conclusion":"FAILURE","detailsUrl":"https://ci/1"},""" +
                """{"name":"build","status":"COMPLETED","conclusion":"SUCCESS"},{"context":"e2e","state":"PENDING"}]""")));
        var issue = Providers.Issue(J("""
            {"number":57,"title":"Traceability","state":"OPEN","url":"https://github.com/o/r/issues/57",
             "body":"## Goal\nx\n\n## Acceptance criteria\n- Each criterion is reported\n- [ ] Selected tests explain why\n\n## Notes\n- not a criterion"}
            """));
        Assert.Equal("#57", issue.Id);
        Assert.Equal(["Each criterion is reported", "Selected tests explain why"], issue.AcceptanceCriteria);
    }

    [Theory]
    [InlineData("Line one\nLine two", new[] { "Line one", "Line two" })]
    [InlineData("1. first\n2) second\n* third", new[] { "first", "second", "third" })]
    [InlineData("<div>Given a cart</div><div>When I pay</div>", new[] { "Given a cart", "When I pay" })]
    public void Acceptance_criteria_are_split_into_items(string text, string[] expected) =>
        Assert.Equal(expected, Requirements.Criteria(text));

    [Fact]
    public void No_acceptance_section_means_no_criteria() =>
        Assert.Empty(Requirements.CriteriaSection("Just a description\n- with a list"));
}
