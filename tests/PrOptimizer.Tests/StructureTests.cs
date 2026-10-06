using PrOptimizer;

namespace PrOptimizer.Tests;

public class StructureTests
{
    const string Source = """
        namespace Shop;
        public class Billing
        {
            int _count;
            public void Charge(decimal amount)
            {
                _count++;
            }
            public void Charge(decimal amount, string currency) { }
            public int Total => _count;
            class Inner
            {
                void Reset() { }
            }
        }
        """;

    static List<string> Touched(params Hunk[] hunks) => Structure.TouchedMembers(Source, hunks).ToList();

    [Fact]
    public void Maps_hunks_to_members()
    {
        Assert.Equal(["Billing.Charge(decimal)"], Touched(new Hunk("b.cs", 7, 1)));
        Assert.Equal(["Billing.Charge(decimal, string)", "Billing.Total"], Touched(new Hunk("b.cs", 9, 2)));
        Assert.Equal(["Billing.Inner.Reset()"], Touched(new Hunk("b.cs", 13, 1)));
        Assert.Equal(["Billing._count"], Touched(new Hunk("b.cs", 4, 0))); // insertion after field
        Assert.Equal(["Billing"], Touched(new Hunk("b.cs", 3, 1)));        // brace line: falls back to type
    }

    [Fact]
    public void Same_member_on_different_lines_raises_weight()
    {
        PullRequest Pr(string id, int line) => new()
        {
            Id = id, HeadSha = id,
            Files = [new FileChange("b.cs", ChangeKind.Modified)],
            Hunks = [new Hunk("b.cs", line, 1)],
            Members = ["Billing.Charge(decimal)"],
        };
        var a = Pr("a", 6);
        var b = Pr("b", 8);
        var withMember = Analyzer.ConflictWeight(a, b, out _);
        b.Members = ["Billing.Total"];
        Assert.True(withMember > Analyzer.ConflictWeight(a, b, out _));
    }
}
