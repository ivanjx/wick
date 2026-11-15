using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Wick.CodeFixes;

/// <summary>
/// WK0002: declares the auto-property <c>partial</c> so the generator implements it. Accessors and the
/// initializer stay on the declaration, which is where the generator expects them.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MakePropertyReactiveCodeFixProvider)), Shared]
public sealed class MakePropertyReactiveCodeFixProvider : CodeFixProvider
{
    private const string Title = "Make the property reactive (partial)";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = [DiagnosticIds.AutoPropertyIsNotReactive];

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
            if (root.FindToken(diagnostic.Location.SourceSpan.Start).Parent is not PropertyDeclarationSyntax property
                || property.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(Title, cancellationToken => FixAsync(context.Document, root, property, cancellationToken), equivalenceKey: Title),
                diagnostic);
        }
    }

    private static Task<Document> FixAsync(Document document, SyntaxNode root, PropertyDeclarationSyntax property, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(property, SyntaxHelpers.AddPartial(property))));
    }
}
