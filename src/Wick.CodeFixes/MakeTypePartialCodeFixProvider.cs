using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Wick.CodeFixes;

/// <summary>
/// WK0001: declares the reported type <c>partial</c>, together with every containing type that is not.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MakeTypePartialCodeFixProvider)), Shared]
public sealed class MakeTypePartialCodeFixProvider : CodeFixProvider
{
    private const string Title = "Make the type and its containing types partial";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = [DiagnosticIds.TypeMustBePartial];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            if (root.FindToken(diagnostic.Location.SourceSpan.Start).Parent is not TypeDeclarationSyntax declaration)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(Title, cancellationToken => FixAsync(context.Document, root, declaration, cancellationToken), equivalenceKey: Title),
                diagnostic);
        }
    }

    private static Task<Document> FixAsync(Document document, SyntaxNode root, TypeDeclarationSyntax declaration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var nonPartial = declaration
            .AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .Where(candidate => !candidate.Modifiers.Any(SyntaxKind.PartialKeyword));

        var fixedRoot = root.ReplaceNodes(nonPartial, static (_, rewritten) => SyntaxHelpers.AddPartial(rewritten));

        return Task.FromResult(document.WithSyntaxRoot(fixedRoot));
    }
}
