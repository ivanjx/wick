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

    private static SyntaxToken PartialKeyword(SyntaxTriviaList leading) => Token(leading, SyntaxKind.PartialKeyword, TriviaList(Space));
}
