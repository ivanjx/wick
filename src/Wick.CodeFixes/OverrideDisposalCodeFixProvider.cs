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
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Wick.CodeFixes;

/// <summary>
/// WK0007: turns a reactive component's own <c>Dispose()</c> into an override of <c>Dispose(bool)</c>, and its own
/// <c>DisposeAsync()</c> into an override of <c>DisposeAsyncCore()</c>, both ending with the base call; the
/// re-implemented interface is removed from the component's base lists. Members declared in a <c>.razor</c> file
/// (and <c>@implements</c>) live in generated code and cannot be rewritten, so no fix is offered there.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(OverrideDisposalCodeFixProvider)), Shared]
public sealed class OverrideDisposalCodeFixProvider : CodeFixProvider
{
    private const string DisposeTitle = "Override Dispose(bool) instead";
    private const string DisposeAsyncTitle = "Override DisposeAsyncCore() instead";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = [DiagnosticIds.DisposalBypassesReactiveCleanup];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        if (context.Document is SourceGeneratedDocument)
        {
            return;
        }

        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            // A #line-mapped location is generated code standing in for another file, such as a .razor component.
            var location = diagnostic.Location;
            if (!location.IsInSource
                || location.GetMappedLineSpan().HasMappedPath
                || root.FindToken(location.SourceSpan.Start).Parent is not MethodDeclarationSyntax method
                || model.GetDeclaredSymbol(method, context.CancellationToken) is not { } symbol)
            {
                continue;
            }

            var isAsync = method.Identifier.ValueText == "DisposeAsync";
            if (DeclaresOverride(symbol.ContainingType, isAsync))
            {
                continue;
            }

            var rewritten = isAsync ? RewriteDisposeAsync(method) : RewriteDispose(method);
            if (rewritten is null)
            {
                continue;
            }

            var title = isAsync ? DisposeAsyncTitle : DisposeTitle;
            context.RegisterCodeFix(
                CodeAction.Create(title, cancellationToken => FixAsync(context.Document, method, rewritten, isAsync, cancellationToken), equivalenceKey: title),
                diagnostic);
        }
    }

    // The component already overrides the hook: merging two bodies is left to the author.
    private static bool DeclaresOverride(INamedTypeSymbol type, bool isAsync)
        => isAsync
            ? type.GetMembers("DisposeAsyncCore").OfType<IMethodSymbol>().Any(method => method.Parameters.IsEmpty)
            : type.GetMembers("Dispose").OfType<IMethodSymbol>().Any(method => method.Parameters.Length == 1 && method.Parameters[0].Type.SpecialType == SpecialType.System_Boolean);

    /// <summary><c>public void Dispose() { body }</c> becomes <c>protected override void Dispose(bool disposing) { body base.Dispose(disposing); }</c>.</summary>
    private static MethodDeclarationSyntax? RewriteDispose(MethodDeclarationSyntax method)
    {
        // An early return would skip the base call.
        if (method.Body is { } body && ReturnStatements(body).Any())
        {
            return null;
        }

        var header = WithHeader(method, isAsync: false, "Dispose", "(bool disposing)");
        const string BaseCall = "base.Dispose(disposing);";

        if (method.Body is not null)
        {
            return header.WithBody(Append(method.Body, BaseCall));
        }

        return method.ExpressionBody is { } arrow
            ? ToBlock(header, method, arrow.Expression + ";", BaseCall)
            : null;
    }

    /// <summary>
    /// <c>public ValueTask DisposeAsync()</c> becomes <c>protected override async ValueTask DisposeAsyncCore()</c>, awaiting
    /// what it returned and then <c>base.DisposeAsyncCore()</c>. A body that only returns a completed task forwards to the base.
    /// </summary>
    private static MethodDeclarationSyntax? RewriteDisposeAsync(MethodDeclarationSyntax method)
    {
        const string BaseCall = "await base.DisposeAsyncCore();";
        const string ForwardToBase = "base.DisposeAsyncCore()";

        List<ReturnStatementSyntax> returns = method.Body is null ? [] : [.. ReturnStatements(method.Body)];

        if (method.Modifiers.Any(SyntaxKind.AsyncKeyword))
        {
            if (method.Body is { } asyncBody)
            {
                return returns.Count == 0 ? WithHeader(method, isAsync: true).WithBody(Append(asyncBody, BaseCall)) : null;
            }

            return method.ExpressionBody is { } asyncArrow
                ? ToBlock(WithHeader(method, isAsync: true), method, asyncArrow.Expression + ";", BaseCall)
                : null;
        }

        if (method.ExpressionBody is { } arrow)
        {
            return IsCompleted(arrow.Expression)
                ? WithHeader(method, isAsync: false).WithExpressionBody(arrow.WithExpression(ParseExpression(ForwardToBase).WithTriviaFrom(arrow.Expression)))
                : ToBlock(WithHeader(method, isAsync: true), method, Await(arrow.Expression), BaseCall);
        }

        // Only a body that ends in its single return can await what it returned in that place.
        if (method.Body is not { } body || returns.Count != 1 || body.Statements.LastOrDefault() is not ReturnStatementSyntax last || last.Expression is null)
        {
            return null;
        }

        if (IsCompleted(last.Expression))
        {
            return WithHeader(method, isAsync: false)
                .WithBody(body.ReplaceNode(last.Expression, ParseExpression(ForwardToBase).WithTriviaFrom(last.Expression)));
        }

        var awaited = ParseStatement(Await(last.Expression)).WithTriviaFrom(last);

        return WithHeader(method, isAsync: true).WithBody(Append(body.ReplaceNode(last, awaited), BaseCall));
    }

    private static IEnumerable<ReturnStatementSyntax> ReturnStatements(BlockSyntax body)
        => body.DescendantNodes(node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)).OfType<ReturnStatementSyntax>();

    private static bool IsCompleted(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.DefaultLiteralExpression),
        DefaultExpressionSyntax => true,
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText == "CompletedTask",
        BaseObjectCreationExpressionSyntax creation => creation.ArgumentList is null or { Arguments.Count: 0 } && creation.Initializer is null,
        _ => false,
    };

    private static string Await(ExpressionSyntax expression)
        => expression is InvocationExpressionSyntax or MemberAccessExpressionSyntax or IdentifierNameSyntax or ElementAccessExpressionSyntax or ParenthesizedExpressionSyntax
            ? $"await {expression};"
            : $"await ({expression});";

    /// <summary>
    /// Replaces the modifiers with <c>protected override</c> (plus <c>async</c>), drops an explicit interface
    /// specifier, and renames <c>DisposeAsync</c> to <c>DisposeAsyncCore</c>.
    /// </summary>
    private static MethodDeclarationSyntax WithHeader(MethodDeclarationSyntax method, bool isAsync, string name = "DisposeAsyncCore", string? parameters = null)
    {
        var leading = method.Modifiers.Count > 0 ? method.Modifiers[0].LeadingTrivia : method.ReturnType.GetLeadingTrivia();
        SyntaxKind[] kinds = isAsync
            ? [SyntaxKind.ProtectedKeyword, SyntaxKind.OverrideKeyword, SyntaxKind.AsyncKeyword]
            : [SyntaxKind.ProtectedKeyword, SyntaxKind.OverrideKeyword];

        var header = method
            .WithModifiers(TokenList(kinds.Select((kind, index) => Token(index == 0 ? leading : default, kind, TriviaList(Space)))))
            .WithExplicitInterfaceSpecifier(null)
            .WithIdentifier(Identifier(method.Identifier.LeadingTrivia, name, method.Identifier.TrailingTrivia));

        if (method.Modifiers.Count == 0)
        {
            header = header.WithReturnType(method.ReturnType.WithoutLeadingTrivia());
        }

        return parameters is null ? header : header.WithParameterList(ParseParameterList(parameters).WithTriviaFrom(method.ParameterList));
    }

    /// <summary>Adds a statement at the end of <paramref name="block"/>, on its own line unless the block is on one line.</summary>
    private static BlockSyntax Append(BlockSyntax block, string statement)
    {
        var parsed = ParseStatement(statement);

        if (block.ToString().IndexOf('\n') < 0)
        {
            var previous = block.CloseBraceToken.GetPreviousToken();
            var separated = previous.TrailingTrivia.Any(SyntaxKind.WhitespaceTrivia);

            return block.AddStatements(parsed.WithLeadingTrivia(separated ? default : TriviaList(Space)).WithTrailingTrivia(Space));
        }

        var indentation = block.Statements.Count > 0
            ? SyntaxHelpers.Indentation(block.Statements.Last().GetFirstToken())
            : SyntaxHelpers.Indent(SyntaxHelpers.Indentation(block.CloseBraceToken));

        return block.AddStatements(parsed.WithLeadingTrivia(indentation).WithTrailingTrivia(SyntaxHelpers.EndOfLine(block.SyntaxTree.GetRoot())));
    }

    /// <summary>Replaces an expression body with a block of <paramref name="statements"/> on the lines below the signature.</summary>
    private static MethodDeclarationSyntax ToBlock(MethodDeclarationSyntax header, MethodDeclarationSyntax original, params string[] statements)
    {
        var endOfLine = SyntaxHelpers.EndOfLine(original.SyntaxTree.GetRoot());
        var indentation = SyntaxHelpers.Indentation(original.GetFirstToken());
        var inner = SyntaxHelpers.Indent(indentation);

        var block = Block(
            Token(indentation, SyntaxKind.OpenBraceToken, TriviaList(endOfLine)),
            List(statements.Select(statement => ParseStatement(statement).WithLeadingTrivia(inner).WithTrailingTrivia(endOfLine))),
            Token(indentation, SyntaxKind.CloseBraceToken, original.SemicolonToken.TrailingTrivia));

        var closeParen = header.ParameterList.CloseParenToken;

        return header
            .WithParameterList(header.ParameterList.WithCloseParenToken(closeParen.WithTrailingTrivia(endOfLine)))
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(block);
    }

    private static async Task<Solution> FixAsync(Document document, MethodDeclarationSyntax method, MethodDeclarationSyntax rewritten, bool isAsync, CancellationToken cancellationToken)
    {
        var model = (await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false))!;
        var symbol = model.GetDeclaredSymbol(method, cancellationToken)!;
        var type = symbol.ContainingType;
        var disposable = isAsync ? model.Compilation.GetTypeByMetadataName("System.IAsyncDisposable") : model.Compilation.GetSpecialType(SpecialType.System_IDisposable);

        // The base class implements the interface; listing it again is only needed by remaining explicit implementations.
        var removeInterface = disposable is not null && !type.GetMembers().Any(member =>
            !SymbolEqualityComparer.Default.Equals(member, symbol) && ImplementsExplicitly(member, disposable));

        var solution = document.Project.Solution;
        foreach (var group in type.DeclaringSyntaxReferences.GroupBy(reference => reference.SyntaxTree))
        {
            var tree = group.Key;
            var target = solution.GetDocument(tree);
            if (target is null or SourceGeneratedDocument)
            {
                continue;
            }

            // Which base list entries to remove, by declaration and index: the rewrite below works on new nodes.
            var semanticModel = model.Compilation.GetSemanticModel(tree);
            var removals = new Dictionary<TypeDeclarationSyntax, int>();
            foreach (var reference in group)
            {
                if (removeInterface && reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax { BaseList: { } baseList } declaration)
                {
                    var index = baseList.Types.IndexOf(baseType =>
                        SymbolEqualityComparer.Default.Equals(semanticModel.GetTypeInfo(baseType.Type, cancellationToken).Type, disposable));
                    if (index >= 0)
                    {
                        removals.Add(declaration, index);
                    }
                }
            }

            var containsMethod = tree == method.SyntaxTree;
            if (removals.Count == 0 && !containsMethod)
            {
                continue;
            }

            var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            var nodes = removals.Keys.Cast<SyntaxNode>();
            if (containsMethod)
            {
                nodes = nodes.Append(method);
            }

            var fixedRoot = root.ReplaceNodes(nodes, (original, current) => original == method
                ? rewritten
                : SyntaxHelpers.RemoveBaseType((TypeDeclarationSyntax)current, removals[(TypeDeclarationSyntax)original]));

            solution = solution.WithDocumentSyntaxRoot(target.Id, fixedRoot);
        }

        return solution;
    }

    private static bool ImplementsExplicitly(ISymbol member, INamedTypeSymbol interfaceType)
    {
        IEnumerable<ISymbol> implemented = member switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations,
            IPropertySymbol property => property.ExplicitInterfaceImplementations,
            IEventSymbol @event => @event.ExplicitInterfaceImplementations,
            _ => [],
        };

        return implemented.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.ContainingType, interfaceType));
    }
}
