using System;
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
/// WK0006: replaces a <c>List</c>, <c>Dictionary</c> or <c>HashSet</c> property (or its interface) with the matching
/// reactive collection. The property becomes a get-only auto-property, the documented shape: the collection
/// notifies its readers itself, so the property needs neither a setter nor generated tracking.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UseReactiveCollectionCodeFixProvider)), Shared]
public sealed class UseReactiveCollectionCodeFixProvider : CodeFixProvider
{
    private const string CoreNamespace = "Wick.Core";
    private const string CollectionsNamespace = "System.Collections.Generic";

    private static readonly ImmutableDictionary<string, ReactiveCollection> Mappings = new Dictionary<string, ReactiveCollection>
    {
        ["List`1"] = ReactiveCollection.List,
        ["IList`1"] = ReactiveCollection.List,
        ["Dictionary`2"] = ReactiveCollection.Dictionary,
        ["IDictionary`2"] = ReactiveCollection.Dictionary,
        ["HashSet`1"] = ReactiveCollection.Set,
        ["ISet`1"] = ReactiveCollection.Set,
    }.ToImmutableDictionary();

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = [DiagnosticIds.MutableCollectionProperty];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            if (root.FindToken(diagnostic.Location.SourceSpan.Start).Parent is not PropertyDeclarationSyntax property
                || property.AccessorList is null
                || !property.AccessorList.Accessors.Any(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                || model.GetDeclaredSymbol(property, context.CancellationToken)?.Type is not INamedTypeSymbol type
                || Map(type) is not { } collection)
            {
                continue;
            }

            var title = $"Use {collection.DisplayName}";
            context.RegisterCodeFix(
                CodeAction.Create(
                    title,
                    cancellationToken => FixAsync(context.Document, property, type, collection, cancellationToken),
                    equivalenceKey: nameof(UseReactiveCollectionCodeFixProvider)),
                diagnostic);
        }
    }

    private static ReactiveCollection? Map(INamedTypeSymbol type)
        => type.ContainingNamespace?.ToDisplayString() == CollectionsNamespace && Mappings.TryGetValue(type.MetadataName, out var collection)
            ? collection
            : null;

    private static async Task<Document> FixAsync(
        Document document,
        PropertyDeclarationSyntax property,
        INamedTypeSymbol type,
        ReactiveCollection collection,
        CancellationToken cancellationToken)
    {
        var root = (CompilationUnitSyntax)(await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
        var model = (await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false))!;

        var typeArguments = FindGenericName(property.Type)?.TypeArgumentList.ToString()
                            ?? "<" + string.Join(", ", type.TypeArguments.Select(argument => argument.ToMinimalDisplayString(model, property.SpanStart, TypeArgumentFormat))) + ">";

        var scope = Lookup(model, property.SpanStart, collection);
        var reactiveType = ParseTypeName((scope == NameScope.Hidden ? "global::Wick.Core." : "") + collection.Name + typeArguments);

        var fixedProperty = property.WithType(reactiveType.WithTriviaFrom(property.Type));
        fixedProperty = RemoveModifier(fixedProperty, SyntaxKind.PartialKeyword);
        fixedProperty = RemoveModifier(fixedProperty, SyntaxKind.RequiredKeyword);

        // Get-only: the collection is never replaced, so a getter without modifiers is all it needs.
        var accessors = property.AccessorList!;
        var getter = accessors.Accessors.First(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration));
        if (getter.Modifiers.Count > 0)
        {
            getter = getter.WithModifiers(default).WithKeyword(getter.Keyword.WithLeadingTrivia(getter.Modifiers[0].LeadingTrivia));
        }

        accessors = accessors.WithAccessors(SingletonList(getter));

        if (property.Initializer is { } initializer)
        {
            var value = RewriteInitializer(initializer.Value, model, collection, reactiveType, cancellationToken);
            fixedProperty = fixedProperty
                .WithAccessorList(accessors)
                .WithInitializer(initializer.WithValue(value.WithTriviaFrom(initializer.Value)));
        }
        else
        {
            // A get-only collection must start out non-null: add the empty one.
            var closeBrace = accessors.CloseBraceToken;
            fixedProperty = fixedProperty
                .WithAccessorList(accessors.WithCloseBraceToken(closeBrace.WithTrailingTrivia(Space)))
                .WithInitializer(EqualsValueClause(Token(TriviaList(), SyntaxKind.EqualsToken, TriviaList(Space)), collection.Empty()))
                .WithSemicolonToken(Token(TriviaList(), SyntaxKind.SemicolonToken, closeBrace.TrailingTrivia));
        }

        var fixedRoot = root.ReplaceNode(property, fixedProperty);
        if (scope == NameScope.Missing)
        {
            fixedRoot = SyntaxHelpers.AddUsing(fixedRoot, CoreNamespace);
        }

        return document.WithSyntaxRoot(fixedRoot);
    }

    private static readonly SymbolDisplayFormat TypeArgumentFormat = SymbolDisplayFormat.MinimallyQualifiedFormat.AddMiscellaneousOptions(
        SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static GenericNameSyntax? FindGenericName(TypeSyntax type) => type switch
    {
        NullableTypeSyntax nullable => FindGenericName(nullable.ElementType),
        QualifiedNameSyntax qualified => FindGenericName(qualified.Right),
        AliasQualifiedNameSyntax aliasQualified => FindGenericName(aliasQualified.Name),
        GenericNameSyntax generic => generic,
        _ => null,
    };

    private static NameScope Lookup(SemanticModel model, int position, ReactiveCollection collection)
    {
        var found = model.LookupNamespacesAndTypes(position, name: collection.Name)
            .OfType<INamedTypeSymbol>()
            .Where(candidate => candidate.Arity == collection.Arity)
            .ToList();

        if (found.Any(candidate => candidate.ContainingNamespace?.ToDisplayString() == CoreNamespace))
        {
            return NameScope.Visible;
        }

        return found.Count > 0 ? NameScope.Hidden : NameScope.Missing;
    }

    private static PropertyDeclarationSyntax RemoveModifier(PropertyDeclarationSyntax property, SyntaxKind kind)
    {
        var index = property.Modifiers.IndexOf(kind);
        if (index < 0)
        {
            return property;
        }

        var leading = property.Modifiers[index].LeadingTrivia;
        var modifiers = property.Modifiers.RemoveAt(index);
        if (index > 0)
        {
            return property.WithModifiers(modifiers);
        }

        // The first modifier carries the declaration's comments and indentation; hand them to the next token.
        return modifiers.Count > 0
            ? property.WithModifiers(modifiers.Replace(modifiers[0], modifiers[0].WithLeadingTrivia(leading)))
            : property.WithModifiers(modifiers).WithType(property.Type.WithLeadingTrivia(leading));
    }

    private static ExpressionSyntax RewriteInitializer(
        ExpressionSyntax value,
        SemanticModel model,
        ReactiveCollection collection,
        TypeSyntax reactiveType,
        CancellationToken cancellationToken)
    {
        if (IsNullOrDefault(value))
        {
            return collection.Empty();
        }

        switch (value)
        {
            case ImplicitObjectCreationExpressionSyntax creation:
                return creation.WithArgumentList(KeepArguments(creation.ArgumentList, model.GetSymbolInfo(creation, cancellationToken).Symbol)!);

            case ObjectCreationExpressionSyntax creation
                when model.GetTypeInfo(creation, cancellationToken).Type is INamedTypeSymbol created && Map(created) == collection:
                return creation
                    .WithType(reactiveType.WithTriviaFrom(creation.Type))
                    .WithArgumentList(KeepArguments(creation.ArgumentList, model.GetSymbolInfo(creation, cancellationToken).Symbol));

            case CollectionExpressionSyntax { Elements.Count: 0 } when collection == ReactiveCollection.Dictionary:
                return collection.Empty();

            case CollectionExpressionSyntax when collection != ReactiveCollection.Dictionary:
                return value;

            default:
                // Anything else is an existing collection: the reactive one copies it.
                return ParseExpression($"new {reactiveType}({value})");
        }
    }

    private static bool IsNullOrDefault(ExpressionSyntax value) => value switch
    {
        PostfixUnaryExpressionSyntax suppressed when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression) => IsNullOrDefault(suppressed.Operand),
        LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.NullLiteralExpression) || literal.IsKind(SyntaxKind.DefaultLiteralExpression),
        DefaultExpressionSyntax => true,
        _ => false,
    };

    /// <summary>
    /// The reactive collections take the same contents and comparer arguments as their counterparts but have no
    /// capacity, so capacity arguments are dropped. Names are dropped too: the parameter names differ.
    /// </summary>
    private static ArgumentListSyntax? KeepArguments(ArgumentListSyntax? arguments, ISymbol? constructor)
    {
        if (arguments is null || arguments.Arguments.Count == 0 || constructor is not IMethodSymbol method)
        {
            return arguments;
        }

        var kept = new List<ArgumentSyntax>();
        var changed = false;
        for (var index = 0; index < arguments.Arguments.Count; index++)
        {
            var argument = arguments.Arguments[index];
            var parameter = argument.NameColon is { } name
                ? method.Parameters.FirstOrDefault(candidate => candidate.Name == name.Name.Identifier.ValueText)
                : index < method.Parameters.Length ? method.Parameters[index] : null;

            if (parameter?.Type.SpecialType == SpecialType.System_Int32)
            {
                changed = true;
                continue;
            }

            if (argument.NameColon is not null)
            {
                argument = argument.WithNameColon(null).WithLeadingTrivia(argument.GetLeadingTrivia());
                changed = true;
            }

            kept.Add(argument);
        }

        if (!changed)
        {
            return arguments;
        }

        var separators = Enumerable.Repeat(Token(TriviaList(), SyntaxKind.CommaToken, TriviaList(Space)), Math.Max(kept.Count - 1, 0));

        return arguments.WithArguments(SeparatedList(kept.Select((argument, index) => index == 0 ? argument.WithoutLeadingTrivia() : argument), separators));
    }

    private enum NameScope
    {
        /// <summary>The Wick.Core type binds by its simple name.</summary>
        Visible,

        /// <summary>Another type of that name is in scope: qualify the name.</summary>
        Hidden,

        /// <summary>Nothing of that name is in scope: import Wick.Core.</summary>
        Missing,
    }

    private sealed class ReactiveCollection
    {
        public static readonly ReactiveCollection List = new("ReactiveList", 1, "ReactiveList<T>", "[]");
        public static readonly ReactiveCollection Dictionary = new("ReactiveDictionary", 2, "ReactiveDictionary<TKey, TValue>", "new()");
        public static readonly ReactiveCollection Set = new("ReactiveSet", 1, "ReactiveSet<T>", "[]");

        private readonly string _empty;

        private ReactiveCollection(string name, int arity, string displayName, string empty)
        {
            Name = name;
            Arity = arity;
            DisplayName = displayName;
            _empty = empty;
        }

        public string Name { get; }

        public int Arity { get; }

        public string DisplayName { get; }

        /// <summary>The initializer of an empty collection, in the README's style.</summary>
        public ExpressionSyntax Empty() => ParseExpression(_empty);
    }
}
