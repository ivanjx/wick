using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Wick.CodeFixes;

/// <summary>
/// Syntax edits shared by the fixes. New code copies the document's line endings and indentation rather than
/// relying on the formatter, so a fix changes only the lines it has to.
/// </summary>
internal static class SyntaxHelpers
{
    /// <summary>The document's line ending, taken from its first line break.</summary>
    public static SyntaxTrivia EndOfLine(SyntaxNode root)
    {
        var existing = root.DescendantTrivia(descendIntoTrivia: true).FirstOrDefault(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia));

        return existing.IsKind(SyntaxKind.EndOfLineTrivia) ? existing : LineFeed;
    }

    /// <summary>The whitespace in front of <paramref name="token"/> on its line.</summary>
    public static SyntaxTriviaList Indentation(SyntaxToken token)
    {
        var leading = token.LeadingTrivia;
        var start = leading.Count;
        while (start > 0 && leading[start - 1].IsKind(SyntaxKind.WhitespaceTrivia))
        {
            start--;
        }

        return TriviaList(leading.Skip(start));
    }

    /// <summary><paramref name="indentation"/> one level deeper, using tabs if it already does.</summary>
    public static SyntaxTriviaList Indent(SyntaxTriviaList indentation)
        => indentation.Add(Whitespace(indentation.ToFullString().IndexOf('\t') >= 0 ? "\t" : "    "));

    /// <summary>
    /// Adds <c>partial</c> as the last modifier: C# requires it right before the type keyword or return type.
    /// </summary>
    public static TypeDeclarationSyntax AddPartial(TypeDeclarationSyntax declaration)
    {
        if (declaration.Modifiers.Count > 0)
        {
            return declaration.AddModifiers(PartialKeyword(default));
        }

        var keyword = declaration.Keyword;

        return declaration
            .WithKeyword(keyword.WithLeadingTrivia())
            .WithModifiers(TokenList(PartialKeyword(keyword.LeadingTrivia)));
    }

    /// <inheritdoc cref="AddPartial(TypeDeclarationSyntax)"/>
    public static PropertyDeclarationSyntax AddPartial(PropertyDeclarationSyntax property)
    {
        if (property.Modifiers.Count > 0)
        {
            return property.AddModifiers(PartialKeyword(default));
        }

        var type = property.Type;

        return property
            .WithType(type.WithoutLeadingTrivia())
            .WithModifiers(TokenList(PartialKeyword(type.GetLeadingTrivia())));
    }

    /// <summary>
    /// Adds <c>using <paramref name="name"/>;</c> among the file's usings (System ones first, then alphabetical), or at
    /// the top of the file after any header comment if it has none.
    /// </summary>
    public static CompilationUnitSyntax AddUsing(CompilationUnitSyntax root, string name)
    {
        var endOfLine = EndOfLine(root);
        var directive = UsingDirective(ParseName(name))
            .WithUsingKeyword(Token(TriviaList(), SyntaxKind.UsingKeyword, TriviaList(Space)))
            .WithSemicolonToken(Token(TriviaList(), SyntaxKind.SemicolonToken, TriviaList(endOfLine)));

        if (root.Usings.Count > 0)
        {
            return root.WithUsings(Insert(root.Usings, directive, name));
        }

        if (root.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault() is { Usings.Count: > 0 } namespaceDeclaration)
        {
            return root.ReplaceNode(namespaceDeclaration, namespaceDeclaration.WithUsings(Insert(namespaceDeclaration.Usings, directive, name)));
        }

        var first = root.GetFirstToken(includeZeroWidth: true);

        return root
            .ReplaceToken(first, first.WithLeadingTrivia())
            .WithUsings(SingletonList(directive.WithLeadingTrivia(first.LeadingTrivia).WithTrailingTrivia(endOfLine, endOfLine)));
    }

    private static SyntaxList<UsingDirectiveSyntax> Insert(SyntaxList<UsingDirectiveSyntax> usings, UsingDirectiveSyntax directive, string name)
    {
        var index = -1;
        var lastPlain = -1;
        for (var candidate = 0; candidate < usings.Count; candidate++)
        {
            var existing = usings[candidate];
            if (existing.Alias is not null || !existing.StaticKeyword.IsKind(SyntaxKind.None) || !existing.GlobalKeyword.IsKind(SyntaxKind.None) || existing.Name is null)
            {
                continue;
            }

            lastPlain = candidate;
            if (Compare(existing.Name.ToString(), name) > 0)
            {
                index = candidate;
                break;
            }
        }

        if (index < 0)
        {
            // After the last plain using; with none, after all usings (static and alias ones included).
            var position = lastPlain >= 0 ? lastPlain + 1 : usings.Count;

            return usings.Insert(position, directive.WithLeadingTrivia(Indentation(usings[position - 1].GetFirstToken())));
        }

        // The new directive takes over the displaced one's leading trivia (header comments, blank lines).
        var displaced = usings[index];
        var indentation = Indentation(displaced.GetFirstToken());

        return usings
            .Replace(displaced, displaced.WithLeadingTrivia(indentation))
            .Insert(index, directive.WithLeadingTrivia(displaced.GetLeadingTrivia()));

        static int Compare(string left, string right)
        {
            var leftIsSystem = left == "System" || left.StartsWith("System.", StringComparison.Ordinal);
            var rightIsSystem = right == "System" || right.StartsWith("System.", StringComparison.Ordinal);

            return leftIsSystem != rightIsSystem ? (leftIsSystem ? -1 : 1) : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Removes <paramref name="directive"/>. When it comes first, the token after it takes over its leading trivia
    /// (header comments) but not the blank lines that followed it.
    /// </summary>
    public static SyntaxNode RemoveUsing(SyntaxNode root, UsingDirectiveSyntax directive)
    {
        var isFirst = !directive.GetFirstToken().GetPreviousToken().Parent.IsKind(SyntaxKind.UsingDirective);
        var leading = directive.GetLeadingTrivia();
        var next = directive.GetLastToken().GetNextToken(includeZeroWidth: true);

        var directiveMark = new SyntaxAnnotation();
        var nextMark = new SyntaxAnnotation();
        root = root.ReplaceSyntax(
            [directive],
            (_, current) => current.WithAdditionalAnnotations(directiveMark),
            [next],
            (_, current) => current.WithAdditionalAnnotations(nextMark),
            [],
            (_, current) => current);

        root = root.RemoveNode(root.GetAnnotatedNodes(directiveMark).Single(), SyntaxRemoveOptions.KeepNoTrivia)!;
        if (!isFirst)
        {
            return root;
        }

        var token = root.GetAnnotatedTokens(nextMark).Single();

        return root.ReplaceToken(token, token.WithLeadingTrivia(leading.AddRange(WithoutBlankLines(token.LeadingTrivia))));
    }

    /// <summary>
    /// Removes statements or members together with their trivia. The node after a removed run keeps the run's spacing:
    /// it takes over the blank line in front of the run, or, when the run started the block or type, loses its own.
    /// </summary>
    public static TRoot RemoveWithTrivia<TRoot>(TRoot root, IEnumerable<SyntaxNode> nodes)
        where TRoot : SyntaxNode
    {
        var all = new HashSet<SyntaxNode>(nodes);
        var removed = all.Where(node => !node.Ancestors().Any(all.Contains)).ToList();

        var leadings = new Dictionary<SyntaxNode, SyntaxTriviaList>();
        foreach (var (parent, isStatement) in removed.Select(node => (node.Parent!, node is StatementSyntax)).Distinct())
        {
            List<SyntaxNode> siblings = isStatement
                ? [.. parent.ChildNodes().OfType<StatementSyntax>()]
                : [.. parent.ChildNodes().OfType<MemberDeclarationSyntax>()];

            for (var index = 1; index < siblings.Count; index++)
            {
                var next = siblings[index];
                if (all.Contains(next) || !all.Contains(siblings[index - 1]))
                {
                    continue;
                }

                var start = index - 1;
                while (start > 0 && all.Contains(siblings[start - 1]))
                {
                    start--;
                }

                var leading = next.GetLeadingTrivia();
                var withoutBlankLines = WithoutBlankLines(leading);
                if (start == 0)
                {
                    leadings[next] = withoutBlankLines;
                }
                else if (withoutBlankLines.Count == leading.Count)
                {
                    var runLeading = siblings[start].GetLeadingTrivia();
                    var blankLines = runLeading.Take(runLeading.Count - WithoutBlankLines(runLeading).Count);
                    leadings[next] = TriviaList(blankLines.Concat(leading));
                }
            }
        }

        var tracked = root.TrackNodes(removed.Concat(leadings.Keys));
        var replacements = leadings.ToDictionary(pair => tracked.GetCurrentNode(pair.Key)!, pair => pair.Value);
        tracked = tracked.ReplaceNodes(replacements.Keys, (original, current) => current.WithLeadingTrivia(replacements[original]));

        return tracked.RemoveNodes(removed.Select(node => tracked.GetCurrentNode(node)!), SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
    }

    /// <summary>Leading trivia without the blank lines it starts with; comments and the indentation stay.</summary>
    public static SyntaxTriviaList WithoutBlankLines(SyntaxTriviaList leading)
    {
        var lastLineEnd = -1;
        for (var index = 0; index < leading.Count; index++)
        {
            if (leading[index].IsKind(SyntaxKind.EndOfLineTrivia))
            {
                lastLineEnd = index;
            }
            else if (!leading[index].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                break;
            }
        }

        return TriviaList(leading.Skip(lastLineEnd + 1));
    }

    /// <summary>
    /// Adds <paramref name="list"/> on its own line after the declaration's attributes; with none, it takes over the
    /// declaration's leading trivia (documentation comments).
    /// </summary>
    public static TypeDeclarationSyntax AddAttributeList(TypeDeclarationSyntax declaration, AttributeListSyntax list)
    {
        var first = declaration.GetFirstToken();
        var indentation = Indentation(first);
        list = list.WithTrailingTrivia(EndOfLine(declaration.SyntaxTree.GetRoot()));

        if (declaration.AttributeLists.Count > 0)
        {
            return declaration.AddAttributeLists(list.WithLeadingTrivia(indentation));
        }

        return declaration
            .ReplaceToken(first, first.WithLeadingTrivia(indentation))
            .WithAttributeLists(SingletonList(list.WithLeadingTrivia(first.LeadingTrivia)));
    }

    /// <summary>Removes the base list entry at <paramref name="index"/>, and the base list with its last entry.</summary>
    public static TypeDeclarationSyntax RemoveBaseType(TypeDeclarationSyntax declaration, int index)
    {
        var baseList = declaration.BaseList!;
        var types = baseList.Types;

        if (types.Count == 1)
        {
            // `class View : IDisposable` becomes `class View`; the token before the colon takes over the line ending.
            var previous = baseList.ColonToken.GetPreviousToken();
            var withoutList = declaration.ReplaceToken(previous, previous.WithTrailingTrivia(baseList.GetTrailingTrivia()));

            return withoutList.WithBaseList(null);
        }

        var remaining = types.RemoveAt(index);
        if (index == types.Count - 1)
        {
            remaining = remaining.Replace(remaining.Last(), remaining.Last().WithTrailingTrivia(types[index].GetTrailingTrivia()));
        }

        return declaration.WithBaseList(baseList.WithTypes(remaining));
    }

    private static SyntaxToken PartialKeyword(SyntaxTriviaList leading) => Token(leading, SyntaxKind.PartialKeyword, TriviaList(Space));
}
