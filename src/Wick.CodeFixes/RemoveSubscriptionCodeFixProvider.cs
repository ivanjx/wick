using System.Collections.Generic;
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
/// WK0011: deletes the redundant <c>state.PropertyChanged += Handler;</c>, every <c>state.PropertyChanged -= Handler;</c>
/// in the same file (typically in <c>Dispose</c>), and the handler method once nothing else refers to it. Only private
/// handlers are deleted; code in a <c>.razor</c> file is generated and gets no fix.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RemoveSubscriptionCodeFixProvider)), Shared]
public sealed class RemoveSubscriptionCodeFixProvider : CodeFixProvider
{
    private const string Title = "Remove the subscription and its handler";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = [DiagnosticIds.RedundantPropertyChangedSubscription];

    // Several subscriptions may share a handler or sit next to each other, so a document's fixes are made together.
    public override FixAllProvider GetFixAllProvider()
        => FixAllProvider.Create(static async (context, document, diagnostics) => await RemoveAsync(document, diagnostics, context.CancellationToken).ConfigureAwait(false));

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        if (context.Document is SourceGeneratedDocument)
        {
            return;
        }

        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            if (FindSubscription(root, diagnostic) is null)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(Title, cancellationToken => RemoveAsync(context.Document, [diagnostic], cancellationToken), equivalenceKey: Title),
                diagnostic);
        }
    }

    /// <summary>The reported <c>+=</c>, if it is a statement of a block in this C# file.</summary>
    private static AssignmentExpressionSyntax? FindSubscription(SyntaxNode root, Diagnostic diagnostic)
    {
        // A #line-mapped location is generated code standing in for another file, such as a .razor component.
        var location = diagnostic.Location;
        if (!location.IsInSource || location.GetMappedLineSpan().HasMappedPath || location.SourceTree != root.SyntaxTree)
        {
            return null;
        }

        return root.FindNode(location.SourceSpan, getInnermostNodeForTie: true) is AssignmentExpressionSyntax assignment
               && assignment.IsKind(SyntaxKind.AddAssignmentExpression)
               && IsBlockStatement(assignment)
            ? assignment
            : null;
    }

    private static bool IsBlockStatement(AssignmentExpressionSyntax assignment)
        => assignment.Parent is ExpressionStatementSyntax statement && statement.Parent is BlockSyntax;

    private static async Task<Document> RemoveAsync(Document document, IEnumerable<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var root = (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
        var model = (await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false))!;

        var removed = new HashSet<SyntaxNode>();
        var handlers = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var unsubscriptions = root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(candidate => candidate.IsKind(SyntaxKind.SubtractAssignmentExpression) && IsBlockStatement(candidate))
            .ToList();

        foreach (var diagnostic in diagnostics)
        {
            var subscription = FindSubscription(root, diagnostic);
            if (subscription is null)
            {
                continue;
            }

            removed.Add(subscription.Parent!);

            // A lambda cannot be unsubscribed and leaves nothing behind.
            if (Handler(model, subscription, cancellationToken) is not IMethodSymbol handler)
            {
                continue;
            }

            handlers.Add(handler);
            foreach (var unsubscription in unsubscriptions)
            {
                if (IsSameSubscription(model, subscription, unsubscription, handler, cancellationToken))
                {
                    removed.Add(unsubscription.Parent!);
                }
            }
        }

        foreach (var handler in handlers)
        {
            if (FindRemovableDeclaration(handler, root, cancellationToken) is MethodDeclarationSyntax declaration
                && !await IsReferencedOutsideAsync(model, handler, removed, cancellationToken).ConfigureAwait(false))
            {
                removed.Add(declaration);
            }
        }

        return document.WithSyntaxRoot(SyntaxHelpers.RemoveWithTrivia(root, removed));
    }

    /// <summary>Whether <paramref name="unsubscription"/> removes <paramref name="handler"/> from the same event of the same object.</summary>
    private static bool IsSameSubscription(
        SemanticModel model,
        AssignmentExpressionSyntax subscription,
        AssignmentExpressionSyntax unsubscription,
        IMethodSymbol handler,
        CancellationToken cancellationToken)
    {
        if (!SymbolEqualityComparer.Default.Equals(Handler(model, unsubscription, cancellationToken), handler)
            || !SymbolEqualityComparer.Default.Equals(
                model.GetSymbolInfo(unsubscription.Left, cancellationToken).Symbol,
                model.GetSymbolInfo(subscription.Left, cancellationToken).Symbol))
        {
            return false;
        }

        var receiver = Receiver(subscription.Left);
        var otherReceiver = Receiver(unsubscription.Left);
        if (receiver is null || otherReceiver is null)
        {
            return receiver is null && otherReceiver is null;
        }

        return receiver.IsEquivalentTo(otherReceiver, topLevel: false)
               && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(receiver, cancellationToken).Symbol, model.GetSymbolInfo(otherReceiver, cancellationToken).Symbol);

        // `State.PropertyChanged` and `this.State.PropertyChanged` have the receiver `State`; `this.PropertyChanged` has none.
        static ExpressionSyntax? Receiver(ExpressionSyntax target)
        {
            if (target is not MemberAccessExpressionSyntax access || access.Expression is ThisExpressionSyntax)
            {
                return null;
            }

            return access.Expression is MemberAccessExpressionSyntax inner && inner.Expression is ThisExpressionSyntax ? inner.Name : access.Expression;
        }
    }

    /// <summary>
    /// The method an event assignment adds or removes. Without the generated event (a document whose generator output
    /// is not available), the method group does not convert but still has its single candidate.
    /// </summary>
    private static IMethodSymbol? Handler(SemanticModel model, AssignmentExpressionSyntax assignment, CancellationToken cancellationToken)
    {
        var info = model.GetSymbolInfo(assignment.Right, cancellationToken);

        return info.Symbol as IMethodSymbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] as IMethodSymbol : null);
    }

    /// <summary>The handler's declaration in this file, if it is an ordinary private method that can go.</summary>
    private static MethodDeclarationSyntax? FindRemovableDeclaration(IMethodSymbol handler, SyntaxNode root, CancellationToken cancellationToken)
    {
        if (handler.DeclaredAccessibility != Accessibility.Private
            || handler.MethodKind != MethodKind.Ordinary
            || handler.DeclaringSyntaxReferences.Length != 1
            || handler.DeclaringSyntaxReferences[0].SyntaxTree != root.SyntaxTree)
        {
            return null;
        }

        return handler.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is MethodDeclarationSyntax declaration
               && !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)
            ? declaration
            : null;
    }

    /// <summary>
    /// Whether code that stays refers to the private <paramref name="handler"/>; it can only be referred to within its
    /// type's declarations, which may span files (a <c>.razor</c> file and its code-behind).
    /// </summary>
    private static async Task<bool> IsReferencedOutsideAsync(
        SemanticModel model,
        IMethodSymbol handler,
        HashSet<SyntaxNode> removed,
        CancellationToken cancellationToken)
    {
        foreach (var reference in handler.ContainingType.DeclaringSyntaxReferences)
        {
            var declaration = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
            var declarationModel = declaration.SyntaxTree == model.SyntaxTree ? model : model.Compilation.GetSemanticModel(declaration.SyntaxTree);

            foreach (var name in declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (name.Identifier.ValueText != handler.Name
                    || (declaration.SyntaxTree == model.SyntaxTree && name.Ancestors().Any(removed.Contains)))
                {
                    continue;
                }

                var info = declarationModel.GetSymbolInfo(name, cancellationToken);
                var candidates = info.Symbol is null ? info.CandidateSymbols : [info.Symbol];
                if (candidates.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, handler.OriginalDefinition)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
