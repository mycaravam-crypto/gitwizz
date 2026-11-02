using Gitwizz;

namespace Gitwizz.Tests;

public class InspectLinksTests
{
    [Fact]
    public void Github_pr_metadata_includes_title_and_linked_issue_details()
    {
        string json = """
            {"number":14,"title":"Fix payment rounding","url":"https://github.com/org/repo/pull/14",
             "closingIssuesReferences":[{"number":27,"title":"Wrong tax rounding",
             "url":"https://github.com/org/repo/issues/27"}]}
            """;
        var details = InspectLinks.ParseGitHubPr(json, 14);
        Assert.Equal("Fix payment rounding", details.Title);
        var workItem = Assert.Single(details.Items);
        Assert.Equal("#27", workItem.Id);
        Assert.Equal("Wrong tax rounding", workItem.Title);
        Assert.Equal("github-pr-closing-issue", workItem.Source);
    }

    [Theory]
    [InlineData("""[{"number":12,"merged_at":"2026-01-01T00:00:00Z"}]""", "#12")]
    [InlineData("""[{"number":12,"merged_at":null},{"number":13,"merged_at":"2026-01-01T00:00:00Z"}]""", "#13")]
    [InlineData("""[{"number":12,"merged_at":"2026-01-01"},{"number":13,"merged_at":"2026-01-02"}]""", null)]
    [InlineData("""[{"number":12,"merged_at":null}]""", null)]
    [InlineData("""[{"number":"bad","merged_at":"2026-01-01"}]""", null)]
    [InlineData("""[{"number":0,"merged_at":"2026-01-01"}]""", null)]
    [InlineData("""[{"number":12,"merged_at":"2026-01-01"},"unexpected"]""", "#12")]
    [InlineData("{malformed", null)]
    [InlineData("{}", null)]
    public void Commit_to_pr_attribution_requires_a_single_valid_merged_pr(string response, string? expected)
    {
        Assert.Equal(expected, InspectLinks.UniqueMergedPullRequest(response));
    }

    [Fact]
    public void Incorrect_pr_identifier_is_rejected()
    {
        Assert.Throws<System.Text.Json.JsonException>(() =>
            InspectLinks.ParseGitHubPr("{\"number\":3,\"title\":\"Other\"}", 14));
    }
}
