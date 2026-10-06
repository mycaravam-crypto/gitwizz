using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace PrOptimizer;

/// <summary>
/// Level 3/4 analysis on C# syntax (Roslyn): members touched by hunks, declared API and the names new code uses.
/// </summary>
// ponytail: C# only, and syntax only: names aren't resolved to types, so same-named members of different
// classes look alike. A compilation with a semantic model would fix that at the cost of restoring references.
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

    /// <summary>
    /// Declared names with the argument counts they accept: methods and constructors give each count from required
    /// to all parameters, other members and types give -1 (referenced by name only).
    /// </summary>
    public static IEnumerable<(string Name, int Arity)> Declarations(string source)
    {
        static IEnumerable<int> Arities(BaseParameterListSyntax p)
        {
            var required = p.Parameters.Count(x => x.Default == null && !x.Modifiers.Any(SyntaxKind.ParamsKeyword));
            return Enumerable.Range(required, p.Parameters.Count - required + 1);
        }
        return CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>().SelectMany(m => m switch
        {
            MethodDeclarationSyntax x => Arities(x.ParameterList).Select(n => (x.Identifier.Text, n)),
            ConstructorDeclarationSyntax x => Arities(x.ParameterList).Select(n => (x.Identifier.Text, n)),
            BaseTypeDeclarationSyntax x => [(x.Identifier.Text, -1)],
            DelegateDeclarationSyntax x => [(x.Identifier.Text, -1)],
            PropertyDeclarationSyntax x => [(x.Identifier.Text, -1)],
            EventDeclarationSyntax x => [(x.Identifier.Text, -1)],
            BaseFieldDeclarationSyntax x => x.Declaration.Variables.Select(v => (v.Identifier.Text, -1)),
            EnumMemberDeclarationSyntax x => [(x.Identifier.Text, -1)],
            _ => [],
        });
    }

    /// <summary>Names used by the code in the given new-side hunks: calls and constructions with their argument count, other references with -1.</summary>
    public static IEnumerable<(string Name, int Args)> Uses(string source, IEnumerable<Hunk> hunks)
    {
        var text = SourceText.From(source);
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        foreach (var h in hunks.Where(h => h.Count > 0 && h.Start >= 1 && h.Start <= text.Lines.Count))
        {
            var last = Math.Min(h.Start + h.Count - 1, text.Lines.Count);
            var span = TextSpan.FromBounds(text.Lines[h.Start - 1].Start, text.Lines[last - 1].End);
            foreach (var n in root.DescendantNodes(span).Where(n => n.Span.IntersectsWith(span)))
                switch (n)
                {
                    case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: var name } } i:
                        yield return (name.Identifier.Text, i.ArgumentList.Arguments.Count); break;
                    case InvocationExpressionSyntax { Expression: SimpleNameSyntax name } i:
                        yield return (name.Identifier.Text, i.ArgumentList.Arguments.Count); break;
                    case ObjectCreationExpressionSyntax o:
                        var type = o.Type is QualifiedNameSyntax q ? q.Right : o.Type as SimpleNameSyntax;
                        if (type != null) yield return (type.Identifier.Text, o.ArgumentList?.Arguments.Count ?? 0);
                        break;
                    case SimpleNameSyntax name:
                        yield return (name.Identifier.Text, -1); break;
                }
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
