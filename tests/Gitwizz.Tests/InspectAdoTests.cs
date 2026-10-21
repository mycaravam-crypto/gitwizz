using System.Text.Json;
using Gitwizz;

namespace Gitwizz.Tests;

public class InspectAdoTests
{
    sealed class FakeAdo : IAdoClient
    {
        public readonly List<string> Calls = new();
        public string Run(params string[] args)
        {
            string command = string.Join(" ", args);
            Calls.Add(command);
            if (command.StartsWith("commit context "))
            {
                string sha = args[2];
                return """
                {"schema":"ado.commit-context/v1","commit":{"commitId":"SHA_PLACEHOLDER"},
                 "workItems":{"status":"verified","items":[{"id":42,"source":"commit-workitem"}]}}
                """.Replace("SHA_PLACEHOLDER", sha, StringComparison.Ordinal);
            }
            if (command == "pr context 9")
                return """
                    {"pullRequest":{"pullRequestId":9,"title":"Payment fix","status":"completed",
                      "targetRefName":"refs/heads/main"},
                     "commits":[{"commitId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],
                     "workItems":[{"id":42}]}
                    """;
            if (command == "workitem show 42 --json")
                return """{"id":42,"fields":{"System.Title":"Fix payment rounding","System.State":"Closed"}}""";
            throw new InvalidOperationException("unexpected: " + command);
        }
    }

    [Fact]
    public void Resolves_commit_work_item_and_confirmed_pr_title()
    {
        FakeAdo ado = new();
        FileInspect.DirectoryEntry entry = new("src/Payment.cs", new string('a', 40),
            "Developer", "2026-01-01", "2026-01-01", "Fix payment", "#9",
            Array.Empty<string>(), 123);
        FileInspect.DirectoryResult report = new("gitwizz.inspect-directory/v1", "src", "HEAD", 1,
            200, false, new[] { entry }, Array.Empty<string>());
        FileInspect.DirectoryResult result = InspectAdo.Enrich(report, ado);
        FileInspect.DirectoryEntry enriched = Assert.Single(result.Files);
        Assert.Equal("Payment fix", enriched.PullRequestTitle);
        InspectLinks.WorkItem item = Assert.Single(enriched.LinkedWorkItems);
        Assert.Equal("AB#42", item.Id);
        Assert.Contains(ado.Calls, c => c == "pr context 9");
    }

    [Fact]
    public void Commit_work_item_is_preserved_without_pr_reference()
    {
        FakeAdo ado = new();
        FileInspect.DirectoryEntry entry = new("a.cs", new string('b', 40), "Dev", "", "", "Direct commit",
            null, Array.Empty<string>(), 100);
        FileInspect.DirectoryResult report = new("gitwizz.inspect-directory/v1", ".", "HEAD", 1,
            200, false, new[] { entry }, Array.Empty<string>());
        FileInspect.DirectoryEntry enriched = Assert.Single(InspectAdo.Enrich(report, ado).Files);
        Assert.Null(enriched.PullRequestTitle);
        Assert.Equal("AB#42", Assert.Single(enriched.LinkedWorkItems).Id);
        Assert.DoesNotContain(ado.Calls, c => c.StartsWith("pr context "));
    }
}
