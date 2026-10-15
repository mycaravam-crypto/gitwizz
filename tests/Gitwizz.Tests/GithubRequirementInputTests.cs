using Gitwizz;

namespace Gitwizz.Tests;

public class GithubRequirementInputTests
{
    [Fact]
    public void Github_and_ado_requirements_keep_distinct_ids_and_repository_evidence()
    {
        const string symbol = "cs:Shop.Orders.Place(int)";
        const string target = "abcdef0123456789abcdef0123456789abcdef0123";
        var context = RequirementContexts.Parse("""
            {"schema":"gitwizz.requirement-input/v1","analyzedCommit":"abcdef0123456789abcdef0123456789abcdef0123",
             "links":[
               {"symbol":"cs:Shop.Orders.Place(int)","requirement":"AB#42","type":"verified","source":"commit-link",
                "freshness":"current","evidence":["linked commit abcdef0 changed Orders.cs:4"]},
               {"symbol":"cs:Shop.Orders.Place(int)","requirement":"github#42","repository":"acme/shop","type":"candidate",
                "source":"pr-link","freshness":"current","evidence":[
                  "GitHub issue github:acme/shop#42 (https://github.com/acme/shop/issues/42)",
                  "commit of linked PR 7 abcdef0 changed Orders.cs:4"]}]}
            """, [symbol], target);
        Assert.Equal("available", context.Status);
        Assert.Equal(0, context.Dropped);
        Assert.Equal(["AB#42", "github#42"], context.Links.Select(l => l.Requirement));
        var github = context.Links.Single(l => l.Requirement == "github#42");
        Assert.Equal("candidate", github.Type);
        Assert.Contains("https://github.com/acme/shop/issues/42", github.Evidence[0]);
    }
}
