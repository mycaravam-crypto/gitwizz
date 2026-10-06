using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace PrOptimizer;

/// <summary>Level 3 conflict analysis: maps hunks to the code members they touch (C# via Roslyn).</summary>
// ponytail: C# only; other languages fall back to file/hunk overlap. Add a parser per language when needed.
public static class Structure
{
    public static bool Supports(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Members like "Billing.Service.Charge(decimal)" touched by the given old-side hunks of one file.</summary>
    public static IEnumerable<string> TouchedMembers(string source, IEnumerable<Hunk> hunks)
    {
        var text = SourceText.From(source);
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        foreach (var h in hunks)
        {
            // Pure insertions (Count 0) sit after line Start; attribute them to that line.
            var first = Math.Clamp(h.Start, 1, text.Lines.Count);
            var last = Math.Clamp(h.Start + Math.Max(h.Count, 1) - 1, first, text.Lines.Count);
            var span = TextSpan.FromBounds(text.Lines[first - 1].Start, text.Lines[last - 1].End);

            var members = root.DescendantNodes(span)
                .OfType<MemberDeclarationSyntax>()
                .Where(m => m is not (BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax or IncompleteMemberSyntax or GlobalStatementSyntax)
                            && m.Span.IntersectsWith(span))
                .ToList();
            if (members.Count > 0)
                foreach (var m in members) yield return Qualified(m, Name(m));
            else if (root.FindNode(span).AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault() is { } type)
                yield return Qualified(type, type.Identifier.Text);
        }
    }

    static string Qualified(SyntaxNode node, string name) =>
        string.Join('.', node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text).Append(name));

    static string Params(BaseParameterListSyntax? p) =>
        "(" + string.Join(", ", p?.Parameters.Select(x => x.Type?.ToString()) ?? []) + ")";

    static string Name(MemberDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax x => x.Identifier.Text + Params(x.ParameterList),
        ConstructorDeclarationSyntax x => x.Identifier.Text + Params(x.ParameterList),
        PropertyDeclarationSyntax x => x.Identifier.Text,
        EventDeclarationSyntax x => x.Identifier.Text,
        IndexerDeclarationSyntax x => "this" + Params(x.ParameterList),
        BaseFieldDeclarationSyntax x => string.Join(",", x.Declaration.Variables.Select(v => v.Identifier.Text)),
        EnumMemberDeclarationSyntax x => x.Identifier.Text,
        OperatorDeclarationSyntax x => "operator " + x.OperatorToken.Text,
        _ => m.Kind().ToString(),
    };
}
