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
/// WK0012: turns a CommunityToolkit.Mvvm <c>ObservableObject</c> into a <c>[ReactiveState]</c> class.
/// <c>[ObservableProperty] private T _name = value;</c> becomes <c>public partial T Name { get; set; } = value;</c> (the
/// toolkit's naming: <c>_name</c>, <c>m_name</c> and <c>name</c> give <c>Name</c>; <c>[property: X]</c> attributes move to
/// the property), <c>[ObservableProperty]</c> is dropped from partial properties, and the <c>ObservableObject</c> base
/// is removed. One fix keeps <c>INotifyPropertyChanged</c> through <c>NotifyPropertyChanged = true</c>, so existing
/// listeners keep compiling and working; the other drops it.
/// </summary>
/// <remarks>
/// No fix is offered when the class relies on more than what the conversion carries over: other toolkit attributes
/// (<c>[NotifyPropertyChangedFor]</c>, <c>[NotifyCanExecuteChangedFor]</c>, ...), <c>On{Name}Changing</c>/<c>On{Name}Changed</c>
/// methods, other attributes on the fields, uses of the fields or of inherited toolkit members (<c>OnPropertyChanged</c>,
/// <c>SetProperty</c>, <c>PropertyChanging</c>), a base other than <c>ObservableObject</c>, or members in other files.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ConvertObservableObjectCodeFixProvider)), Shared]
public sealed class ConvertObservableObjectCodeFixProvider : CodeFixProvider
{
    private const string ToolkitNamespace = "CommunityToolkit.Mvvm.ComponentModel";
    private const string ObservablePropertyMetadataName = ToolkitNamespace + ".ObservablePropertyAttribute";
    private const string ObservableObjectMetadataName = ToolkitNamespace + ".ObservableObject";
    private const string AttributeMetadataName = "Wick.Core.ReactiveStateAttribute";
    private const string CoreNamespace = "Wick.Core";
    private const string NotifyingTitle = "Convert to [ReactiveState(NotifyPropertyChanged = true)]";
    private const string Title = "Convert to [ReactiveState]";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = [DiagnosticIds.ObservablePropertyCanBeReactive];

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
            if (root.FindToken(diagnostic.Location.SourceSpan.Start).Parent is not ClassDeclarationSyntax declaration
                || model.GetDeclaredSymbol(declaration, context.CancellationToken) is not INamedTypeSymbol type)
            {
                continue;
            }

            var conversion = Plan(context.Document, root, model, type, context.CancellationToken);
            if (conversion is null)
            {
                continue;
            }

            // Already [ReactiveState]: only the members and the base change.
            if (!conversion.IsReactiveState)
            {
                context.RegisterCodeFix(
                    CodeAction.Create(NotifyingTitle, _ => Task.FromResult(Apply(context.Document, root, model, conversion, notify: true)), equivalenceKey: NotifyingTitle),
                    diagnostic);
            }

            context.RegisterCodeFix(
                CodeAction.Create(Title, _ => Task.FromResult(Apply(context.Document, root, model, conversion, notify: false)), equivalenceKey: Title),
                diagnostic);
        }
    }

    /// <summary>What to rewrite, or null when the class cannot be converted safely.</summary>
    private static Conversion? Plan(Document document, SyntaxNode root, SemanticModel model, INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        var compilation = model.Compilation;
        var observableProperty = compilation.GetTypeByMetadataName(ObservablePropertyMetadataName);
        var observableObject = compilation.GetTypeByMetadataName(ObservableObjectMetadataName);
        if (observableProperty is null
            || observableObject is null
            || type.TypeKind != TypeKind.Class
            || type.IsRecord
            || !SymbolEqualityComparer.Default.Equals(type.BaseType, observableObject)
            || type.GetAttributes().Any(attribute => IsToolkit(attribute.AttributeClass)))
        {
            return null;
        }

        var conversion = new Conversion(type.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == AttributeMetadataName));
        var names = new List<string>();

        foreach (var member in type.GetMembers())
        {
            var attributes = member.GetAttributes();
            if (!attributes.Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, observableProperty)))
            {
                continue;
            }

            if (attributes.Any(attribute => IsToolkit(attribute.AttributeClass) && !SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, observableProperty))
                || member.DeclaringSyntaxReferences.Length != 1
                || member.DeclaringSyntaxReferences[0].SyntaxTree != root.SyntaxTree)
            {
                return null;
            }

            var syntax = member.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken);
            SyntaxList<AttributeListSyntax> lists;
            bool isField;
            switch (member)
            {
                case IFieldSymbol field when !field.IsStatic && !field.IsReadOnly && !field.IsConst && syntax.Parent?.Parent is FieldDeclarationSyntax declaration:
                    var name = PropertyName(field.Name);
                    if (name.Length == 0)
                    {
                        return null;
                    }

                    names.Add(name);
                    conversion.Fields.Add(field);
                    conversion.FieldDeclarations.Add(declaration);
                    lists = declaration.AttributeLists;
                    isField = true;
                    break;

                case IPropertySymbol property when property.IsPartialDefinition && syntax is PropertyDeclarationSyntax declaration:
                    names.Add(property.Name);
                    conversion.PropertyDeclarations.Add(declaration);
                    lists = declaration.AttributeLists;
                    isField = false;
                    break;

                default:
                    return null;
            }

            // [ObservableProperty] goes; on a field, only [property: ...] attributes have a place on the property.
            foreach (var list in lists)
            {
                var isPropertyTarget = list.Target is not null && list.Target.Identifier.IsKind(SyntaxKind.PropertyKeyword);
                foreach (var attribute in list.Attributes)
                {
                    var attributeType = model.GetSymbolInfo(attribute, cancellationToken).Symbol?.ContainingType;
                    if (SymbolEqualityComparer.Default.Equals(attributeType, observableProperty) && !isPropertyTarget)
                    {
                        conversion.ObservablePropertyAttributes.Add(attribute);
                    }
                    else if (isField && !isPropertyTarget)
                    {
                        return null;
                    }
                }
            }
        }

        if (names.Count == 0)
        {
            return null;
        }

        // The toolkit's change hooks (once implemented; it declares them itself) and overrides of its members have no counterpart.
        if (type.GetMembers().Any(member =>
                (member is IMethodSymbol method
                 && !(method.IsPartialDefinition && method.PartialImplementationPart is null)
                 && names.Any(name => method.Name == $"On{name}Changing" || method.Name == $"On{name}Changed"))
                || (member.IsOverride && IsToolkit(Overridden(member)?.ContainingType))))
        {
            return null;
        }

        foreach (var declaration in type.DeclaringSyntaxReferences.Select(reference => (TypeDeclarationSyntax)reference.GetSyntax(cancellationToken)))
        {
            if (declaration.BaseList is null)
            {
                continue;
            }

            var declarationModel = declaration.SyntaxTree == model.SyntaxTree ? model : compilation.GetSemanticModel(declaration.SyntaxTree);
            foreach (var baseType in declaration.BaseList.Types)
            {
                if (SymbolEqualityComparer.Default.Equals(declarationModel.GetTypeInfo(baseType.Type, cancellationToken).Type, observableObject))
                {
                    if (declaration.SyntaxTree != root.SyntaxTree || conversion.BaseType is not null)
                    {
                        return null;
                    }

                    conversion.BaseType = baseType;
                }
            }
        }

        if (conversion.BaseType is null)
        {
            return null;
        }

        // The fields disappear, and so do the members inherited from the toolkit.
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            var tree = reference.SyntaxTree;
            if (tree != root.SyntaxTree && document.Project.Solution.GetDocument(tree) is null)
            {
                // Generated, such as the toolkit's own output.
                continue;
            }

            var declarationModel = tree == model.SyntaxTree ? model : compilation.GetSemanticModel(tree);
            foreach (var name in conversion.Remaining(reference.GetSyntax(cancellationToken)))
            {
                var symbol = declarationModel.GetSymbolInfo(name, cancellationToken).Symbol;
                if ((symbol is IFieldSymbol field && conversion.Fields.Contains(field))
                    || (symbol is not null && IsToolkit(symbol as INamedTypeSymbol ?? symbol.ContainingType)))
                {
                    return null;
                }
            }
        }

        conversion.UsesToolkitElsewhere = conversion.Remaining(root)
            .Where(name => name.FirstAncestorOrSelf<UsingDirectiveSyntax>() is null)
            .Any(name => model.GetSymbolInfo(name, cancellationToken).Symbol is { } symbol
                         && (symbol as INamespaceSymbol ?? symbol.ContainingNamespace)?.ToDisplayString() == ToolkitNamespace);

        var found = model.LookupNamespacesAndTypes(conversion.BaseType.SpanStart, name: "ReactiveStateAttribute")
            .Concat(model.LookupNamespacesAndTypes(conversion.BaseType.SpanStart, name: "ReactiveState"))
            .ToList();
        conversion.Scope = found.Count == 0
            ? NameScope.Missing
            : found.All(candidate => candidate.ToDisplayString() == AttributeMetadataName) ? NameScope.Visible : NameScope.Hidden;

        return conversion;
    }

    private static Document Apply(Document document, SyntaxNode root, SemanticModel model, Conversion conversion, bool notify)
    {
        var endOfLine = SyntaxHelpers.EndOfLine(root);
        var primary = conversion.BaseType!.FirstAncestorOrSelf<TypeDeclarationSyntax>()!;
        var declarations = conversion.FieldDeclarations.Select(field => field.FirstAncestorOrSelf<TypeDeclarationSyntax>()!)
            .Concat(conversion.PropertyDeclarations.Select(property => property.FirstAncestorOrSelf<TypeDeclarationSyntax>()!))
            .Append(primary)
            .Distinct()
            .ToList();

        // The declarations are not nested in each other, so each is rewritten from its original node.
        var fixedRoot = (CompilationUnitSyntax)root.ReplaceNodes(declarations, (original, _) => Rewrite(original));

        while (!conversion.UsesToolkitElsewhere && FindToolkitUsing(fixedRoot) is { } directive)
        {
            fixedRoot = (CompilationUnitSyntax)SyntaxHelpers.RemoveUsing(fixedRoot, directive);
        }

        if (!conversion.IsReactiveState && conversion.Scope == NameScope.Missing)
        {
            fixedRoot = SyntaxHelpers.AddUsing(fixedRoot, CoreNamespace);
        }

        return document.WithSyntaxRoot(fixedRoot);

        TypeDeclarationSyntax Rewrite(TypeDeclarationSyntax declaration)
        {
            var members = new List<MemberDeclarationSyntax>();
            foreach (var member in declaration.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field when conversion.FieldDeclarations.Contains(field):
                        members.AddRange(ConvertField(field, model, endOfLine));
                        break;

                    case PropertyDeclarationSyntax property when conversion.PropertyDeclarations.Contains(property):
                        members.Add(property
                            .WithAttributeLists(WithoutAttributes(property.AttributeLists, conversion.ObservablePropertyAttributes))
                            .WithLeadingTrivia(property.GetLeadingTrivia()));
                        break;

                    default:
                        members.Add(member);
                        break;
                }
            }

            var rewritten = declaration.WithMembers(List(members));
            if (declaration != primary)
            {
                return rewritten;
            }

            rewritten = SyntaxHelpers.RemoveBaseType(rewritten, declaration.BaseList!.Types.IndexOf(conversion.BaseType));
            if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                rewritten = SyntaxHelpers.AddPartial(rewritten);
            }

            if (conversion.IsReactiveState)
            {
                return rewritten;
            }

            var attribute = Attribute(
                ParseName(conversion.Scope == NameScope.Hidden ? "global::Wick.Core.ReactiveState" : "ReactiveState"),
                notify ? ParseAttributeArgumentList("(NotifyPropertyChanged = true)") : null);

            return SyntaxHelpers.AddAttributeList(rewritten, AttributeList(SingletonSeparatedList(attribute)));
        }
    }

    /// <summary>One <c>public partial</c> property per variable of the field declaration.</summary>
    private static IEnumerable<PropertyDeclarationSyntax> ConvertField(FieldDeclarationSyntax field, SemanticModel model, SyntaxTrivia endOfLine)
    {
        var attributeLists = List(field.AttributeLists
            .Where(list => list.Target is not null && list.Target.Identifier.IsKind(SyntaxKind.PropertyKeyword))
            .Select(list => list.WithTarget(null)));

        var modifiersLeading = field.Modifiers.Count > 0 ? field.Modifiers[0].LeadingTrivia : field.Declaration.Type.GetLeadingTrivia();
        var modifiers = TokenList(Token(modifiersLeading, SyntaxKind.PublicKeyword, TriviaList(Space)), Token(TriviaList(), SyntaxKind.PartialKeyword, TriviaList(Space)));
        var type = field.Declaration.Type.WithoutTrivia().WithTrailingTrivia(Space);
        var variables = field.Declaration.Variables;

        for (var index = 0; index < variables.Count; index++)
        {
            var variable = variables[index];
            var name = PropertyName(model.GetDeclaredSymbol(variable)!.Name);
            var trailing = index == variables.Count - 1 ? field.SemicolonToken.TrailingTrivia : TriviaList(endOfLine);
            var initializer = variable.Initializer?.WithoutTrailingTrivia();

            var accessors = AccessorList(
                Token(TriviaList(), SyntaxKind.OpenBraceToken, TriviaList(Space)),
                List(
                [
                    AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(TriviaList(), SyntaxKind.SemicolonToken, TriviaList(Space))),
                    AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(TriviaList(), SyntaxKind.SemicolonToken, TriviaList(Space))),
                ]),
                Token(TriviaList(), SyntaxKind.CloseBraceToken, initializer is null ? trailing : TriviaList(Space)));

            var identifier = SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? Identifier(name) : Identifier("@" + name);

            var property = PropertyDeclaration(
                attributeLists,
                modifiers,
                type,
                explicitInterfaceSpecifier: null,
                identifier.WithTrailingTrivia(Space),
                accessors,
                expressionBody: null,
                initializer,
                initializer is null ? default : Token(TriviaList(), SyntaxKind.SemicolonToken, trailing));

            yield return property.WithLeadingTrivia(index == 0 ? field.GetLeadingTrivia() : SyntaxHelpers.Indentation(field.GetFirstToken()));
        }
    }

    private static SyntaxList<AttributeListSyntax> WithoutAttributes(SyntaxList<AttributeListSyntax> lists, HashSet<AttributeSyntax> removed)
    {
        var kept = new List<AttributeListSyntax>();
        foreach (var list in lists)
        {
            var attributes = list.Attributes.Where(attribute => !removed.Contains(attribute)).ToList();
            if (attributes.Count == list.Attributes.Count)
            {
                kept.Add(list);
            }
            else if (attributes.Count > 0)
            {
                kept.Add(list.WithAttributes(SeparatedList(attributes)));
            }
        }

        return List(kept);
    }

    private static UsingDirectiveSyntax? FindToolkitUsing(SyntaxNode root)
        => root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or UsingDirectiveSyntax)
            .OfType<UsingDirectiveSyntax>()
            .FirstOrDefault(directive => directive.Alias is null
                                         && directive.StaticKeyword.IsKind(SyntaxKind.None)
                                         && directive.GlobalKeyword.IsKind(SyntaxKind.None)
                                         && directive.Name?.ToString() == ToolkitNamespace);

    /// <summary>The toolkit's naming: <c>m_name</c>, <c>_name</c> and <c>name</c> give <c>Name</c>.</summary>
    private static string PropertyName(string field)
    {
        var name = field.StartsWith("m_", StringComparison.Ordinal) ? field.Substring(2) : field.TrimStart('_');

        return name.Length == 0 ? "" : char.ToUpperInvariant(name[0]) + name.Substring(1);
    }

    private static bool IsToolkit(INamedTypeSymbol? type)
        => type?.ContainingNamespace?.ToDisplayString().StartsWith("CommunityToolkit.Mvvm", StringComparison.Ordinal) == true;

    private static ISymbol? Overridden(ISymbol member) => member switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null,
    };

    private enum NameScope
    {
        /// <summary><c>ReactiveState</c> binds to Wick's attribute.</summary>
        Visible,

        /// <summary>Another <c>ReactiveState</c> is in scope: qualify the name.</summary>
        Hidden,

        /// <summary>Nothing of that name is in scope: import Wick.Core.</summary>
        Missing,
    }

    private sealed class Conversion(bool isReactiveState)
    {
        public bool IsReactiveState { get; } = isReactiveState;

        public HashSet<IFieldSymbol> Fields { get; } = new(SymbolEqualityComparer.Default);

        public HashSet<FieldDeclarationSyntax> FieldDeclarations { get; } = [];

        public HashSet<PropertyDeclarationSyntax> PropertyDeclarations { get; } = [];

        public HashSet<AttributeSyntax> ObservablePropertyAttributes { get; } = [];

        public BaseTypeSyntax? BaseType { get; set; }

        public bool UsesToolkitElsewhere { get; set; }

        public NameScope Scope { get; set; }

        /// <summary>The names under <paramref name="node"/> outside the syntax the conversion removes.</summary>
        public IEnumerable<SimpleNameSyntax> Remaining(SyntaxNode node)
            => node.DescendantNodes(descendant => !ObservablePropertyAttributes.Contains(descendant) && descendant != BaseType)
                .OfType<SimpleNameSyntax>();
    }
}
