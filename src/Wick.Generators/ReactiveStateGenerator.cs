using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Wick.Generators;

/// <summary>
/// Implements the <c>partial</c> properties of <c>[ReactiveState]</c> classes:
/// <code>
/// public partial int Count
/// {
///     get => ReactiveProperty.Get(ref field, ref __reactiveNode_Count, "State.Count");
///     set => ReactiveProperty.Set(ref field, value, ref __reactiveNode_Count);
/// }
/// </code>
/// and implements <c>IReactiveState</c> with a name-to-node switch.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ReactiveStateGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "Wick.Core.ReactiveStateAttribute";
    private const string ReactiveObjectMetadataName = "Wick.Core.ReactiveObject";
    private const string Core = "global::Wick.Core";
    private const string NodeFieldPrefix = "__reactiveNode_";
    private const string FindNodeMethod = "__FindReactiveNode";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var results = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeMetadataName,
            static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
            static (context, cancellationToken) => Analyze(context, cancellationToken));

        context.RegisterSourceOutput(results, static (context, result) =>
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                context.ReportDiagnostic(diagnostic.ToDiagnostic());
            }

            if (result.State is not null)
            {
                context.AddSource(result.State.HintName, Emit(result.State));
            }
        });
    }

    private static GenerationResult Analyze(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        var declaration = (TypeDeclarationSyntax)context.TargetNode;
        var type = (INamedTypeSymbol)context.TargetSymbol;
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var typeName = type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

        if (((CSharpParseOptions)declaration.SyntaxTree.Options).LanguageVersion < LanguageVersion.CSharp14)
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.RequiresCSharp14, declaration.Identifier.GetLocation()));
        }

        foreach (var typeDeclaration in declaration.AncestorsAndSelf().OfType<TypeDeclarationSyntax>())
        {
            if (!typeDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.TypeMustBePartial, typeDeclaration.Identifier.GetLocation(), typeDeclaration.Identifier.Text));
            }
        }

        var typeProblem = FindTypeProblem(type, context.SemanticModel.Compilation);
        if (typeProblem is not null)
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnsupportedType, declaration.Identifier.GetLocation(), typeName, typeProblem));
        }

        var properties = ImmutableArray.CreateBuilder<PropertyModel>();
        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is not PropertyDeclarationSyntax syntax)
            {
                continue;
            }

            // Warnings about properties (WK0002, WK0006) come from ReactiveStateAnalyzer: only analyzer
            // diagnostics have syntax tree locations, which #pragma and .editorconfig suppressions need.
            if (!property.IsPartialDefinition)
            {
                continue;
            }

            if (property.PartialImplementationPart is not null)
            {
                // Implemented by hand; not ours to generate.
                continue;
            }

            var propertyProblem = FindPropertyProblem(property, syntax);
            if (propertyProblem is not null)
            {
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnsupportedProperty, syntax.Identifier.GetLocation(), property.Name, propertyProblem));
                continue;
            }

            var nullableContext = context.SemanticModel.Compilation.GetSemanticModel(syntax.SyntaxTree).GetNullableContext(syntax.SpanStart);

            properties.Add(CreatePropertyModel(property, syntax, nullableContext));
        }

        if (diagnostics.Any(diagnostic => diagnostic.Descriptor.DefaultSeverity == DiagnosticSeverity.Error))
        {
            return new GenerationResult(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));
        }

        var state = new StateModel(
            HintName: CreateHintName(type),
            Namespace: type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            ContainingTypes: new EquatableArray<TypeHeader>([.. declaration.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(CreateHeader)]),
            Type: CreateHeader(declaration),
            IsSealed: type.IsSealed,
            HasReactiveBase: HasReactiveBase(type),
            Properties: new EquatableArray<PropertyModel>(properties.ToImmutable()));

        return new GenerationResult(state, new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));
    }

    private static string? FindTypeProblem(INamedTypeSymbol type, Compilation compilation)
    {
        if (type.IsStatic)
        {
            return "static classes cannot hold instance state";
        }

        if (type.IsRecord)
        {
            // A 'with' expression copies every field, including the per-property nodes, so the copy
            // and the original would notify each other's observers.
            return "records are not supported because 'with' copies would share reactive subscriptions";
        }

        var reactiveObject = compilation.GetTypeByMetadataName(ReactiveObjectMetadataName);
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, reactiveObject))
            {
                return "it derives from ReactiveObject; use either the runtime base class or the generator, not both";
            }
        }

        return null;
    }

    private static string? FindPropertyProblem(IPropertySymbol property, PropertyDeclarationSyntax syntax)
    {
        if (property.IsStatic)
        {
            return "static properties are not supported";
        }

        if (property.ReturnsByRef || property.ReturnsByRefReadonly)
        {
            return "ref-returning properties are not supported";
        }

        if (syntax.ExplicitInterfaceSpecifier is not null)
        {
            return "explicit interface implementations are not supported";
        }

        if (property.GetMethod is null || property.SetMethod is null)
        {
            return "it needs both a getter and a 'set' or 'init' accessor";
        }

        return null;
    }

    private static bool HasReactiveBase(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == AttributeMetadataName))
            {
                return true;
            }
        }

        return false;
    }

    private static PropertyModel CreatePropertyModel(IPropertySymbol property, PropertyDeclarationSyntax syntax, NullableContext nullableContext)
    {
        var accessors = syntax.AccessorList!.Accessors;
        var getter = accessors.First(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration));
        var setter = accessors.First(accessor => accessor.IsKind(SyntaxKind.SetAccessorDeclaration) || accessor.IsKind(SyntaxKind.InitAccessorDeclaration));

        return new PropertyModel(
            Name: property.Name,
            Identifier: syntax.Identifier.Text,
            Type: property.Type.ToDisplayString(TypeFormat),
            Modifiers: Join(syntax.Modifiers),
            GetModifiers: Join(getter.Modifiers),
            SetKeyword: setter.Keyword.Text,
            SetModifiers: Join(setter.Modifiers),
            NullableAnnotations: nullableContext.AnnotationsEnabled(),
            NullableWarnings: nullableContext.WarningsEnabled());

        static string Join(SyntaxTokenList tokens) => string.Join(" ", tokens.Select(token => token.Text));
    }

    private static TypeHeader CreateHeader(TypeDeclarationSyntax declaration)
    {
        var keyword = declaration is RecordDeclarationSyntax record && !record.ClassOrStructKeyword.IsKind(SyntaxKind.None)
            ? $"record {record.ClassOrStructKeyword.Text}"
            : declaration.Keyword.Text;

        // Identifiers and variance only: attributes and constraints must not be repeated on other parts.
        var typeParameters = declaration.TypeParameterList is null
            ? ""
            : "<" + string.Join(", ", declaration.TypeParameterList.Parameters.Select(parameter =>
                parameter.VarianceKeyword.IsKind(SyntaxKind.None)
                    ? parameter.Identifier.Text
                    : $"{parameter.VarianceKeyword.Text} {parameter.Identifier.Text}")) + ">";

        return new TypeHeader(keyword, declaration.Identifier.Text + typeParameters);
    }

    private static string CreateHintName(INamedTypeSymbol type)
    {
        var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '_' ? character : '_');
        }

        return builder.Append(".ReactiveState.g.cs").ToString();
    }

    private static string Emit(StateModel state)
    {
        var writer = new CodeWriter();

        writer.Line("// <auto-generated/>");
        writer.Line("#nullable enable");
        writer.Line();

        if (state.Namespace is not null)
        {
            writer.Line($"namespace {state.Namespace};");
            writer.Line();
        }

        foreach (var container in state.ContainingTypes)
        {
            writer.Line($"partial {container.Keyword} {container.Name}");
            writer.Open();
        }

        writer.Line(state.HasReactiveBase
            ? $"partial {state.Type.Keyword} {state.Type.Name}"
            : $"partial {state.Type.Keyword} {state.Type.Name} : {Core}.IReactiveState");
        writer.Open();

        foreach (var property in state.Properties)
        {
            writer.Line($"private {Core}.ReactiveNode? {NodeFieldPrefix}{property.Name};");
        }

        foreach (var property in state.Properties)
        {
            var node = NodeFieldPrefix + property.Name;
            var nodeName = SymbolDisplay.FormatLiteral($"{state.Type.Name}.{property.Name}", quote: true);

            writer.Line();

            // The implementation must be compiled in the declaration's nullable context, otherwise an
            // oblivious declaration gets nullable warnings reported in generated code.
            var matchesFileContext = property.NullableAnnotations && property.NullableWarnings;
            if (!matchesFileContext)
            {
                writer.Directive($"#nullable {(property.NullableAnnotations ? "enable" : "disable")} annotations");
                writer.Directive($"#nullable {(property.NullableWarnings ? "enable" : "disable")} warnings");
            }

            writer.Line($"{property.Modifiers} {property.Type} {property.Identifier}");
            writer.Open();
            writer.Line($"{Prefix(property.GetModifiers)}get => {Core}.ReactiveProperty.Get(ref field, ref {node}, {nodeName});");
            writer.Line(property.SetKeyword == "init"
                ? $"{Prefix(property.SetModifiers)}init => field = value;"
                : $"{Prefix(property.SetModifiers)}set => {Core}.ReactiveProperty.Set(ref field, value, ref {node});");
            writer.Close();

            if (!matchesFileContext)
            {
                writer.Directive("#nullable enable");
            }
        }

        writer.Line();

        if (!state.HasReactiveBase)
        {
            writer.Line($"{Core}.ReactiveNode? {Core}.IReactiveState.FindNode(string propertyName) => {FindNodeMethod}(propertyName);");
            writer.Line();
        }

        var signature = state.HasReactiveBase ? "protected override" : state.IsSealed ? "private" : "protected virtual";
        var fallback = state.HasReactiveBase ? $"base.{FindNodeMethod}(propertyName)" : "null";

        writer.Line("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        writer.Line($"{signature} {Core}.ReactiveNode? {FindNodeMethod}(string propertyName) => propertyName switch");
        writer.Open();

        foreach (var property in state.Properties)
        {
            writer.Line($"{SymbolDisplay.FormatLiteral(property.Name, quote: true)} => global::System.Threading.Volatile.Read(ref {NodeFieldPrefix}{property.Name}),");
        }

        writer.Line($"_ => {fallback},");
        writer.Close(";");

        writer.Close();

        foreach (var _ in state.ContainingTypes)
        {
            writer.Close();
        }

        return writer.ToString();

        static string Prefix(string modifiers) => modifiers.Length == 0 ? "" : modifiers + " ";
    }

    private sealed class CodeWriter
    {
        private readonly StringBuilder _builder = new();
        private int _indent;

        public void Line(string text = "")
        {
            if (text.Length > 0)
            {
                _builder.Append(' ', _indent * 4).Append(text);
            }

            _builder.Append('\n');
        }

        public void Directive(string text) => _builder.Append(text).Append('\n');

        public void Open()
        {
            Line("{");
            _indent++;
        }

        public void Close(string suffix = "")
        {
            _indent--;
            Line("}" + suffix);
        }

        public override string ToString() => _builder.ToString();
    }
}
