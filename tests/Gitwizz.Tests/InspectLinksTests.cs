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

    [Fact]
    public void Incorrect_pr_identifier_is_rejected()
    {
        Assert.Throws<System.Text.Json.JsonException>(() =>
            InspectLinks.ParseGitHubPr("{\"number\":3,\"title\":\"Other\"}", 14));
    }
}
