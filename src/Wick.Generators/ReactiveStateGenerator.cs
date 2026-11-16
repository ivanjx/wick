using System.Collections.Generic;
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
    private const string EqualityAttributeMetadataName = "Wick.Core.ReactiveEqualityAttribute";
    private const string ReactiveObjectMetadataName = "Wick.Core.ReactiveObject";
    private const string NotifyPropertyChangedMetadataName = "System.ComponentModel.INotifyPropertyChanged";
    private const string Core = "global::Wick.Core";
    private const string PropertyChangedNamespace = "global::System.ComponentModel";
    private const string NodeFieldPrefix = "__reactiveNode_";
    private const string ComparerFieldPrefix = "__reactiveComparer_";
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

        var propertyChangedProblem = ReadPropertyChanged(type, context.Attributes[0], context.SemanticModel.Compilation, out var propertyChanged);
        if (propertyChangedProblem is not null)
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.InvalidNotifyPropertyChanged, declaration.Identifier.GetLocation(), typeName, propertyChangedProblem));
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

            // Implemented by another generator, which cannot see our implementation nor we its. ReactiveStateAnalyzer
            // reports instance properties (WK0013); static ones could never be reactive.
            if (OtherGenerators.FindClaim(property) is not null)
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

            var equalityProblem = ReadEquality(property, type, context.SemanticModel.Compilation, out var equality, out var comparerType, out var attribute);
            if (equalityProblem is not null)
            {
                var location = attribute?.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? syntax.Identifier.GetLocation();
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.InvalidEquality, location, property.Name, equalityProblem));
                continue;
            }

            var nullableContext = context.SemanticModel.Compilation.GetSemanticModel(syntax.SyntaxTree).GetNullableContext(syntax.SpanStart);

            properties.Add(CreatePropertyModel(property, syntax, nullableContext, equality, comparerType));
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
            PropertyChanged: propertyChanged,
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

    /// <summary>
    /// Reads <c>NotifyPropertyChanged</c>, which derived reactive types inherit; returns why it cannot be used, or null.
    /// Generators do not see their own output, so a reactive base that requested the event counts as declaring the
    /// <c>OnPropertyChanged(string)</c> generated for it.
    /// </summary>
    private static string? ReadPropertyChanged(INamedTypeSymbol type, AttributeData attribute, Compilation compilation, out PropertyChangedKind kind)
    {
        kind = PropertyChangedKind.None;

        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.GetAttributes().Any(candidate => candidate.AttributeClass?.ToDisplayString() == AttributeMetadataName && RequestsPropertyChanged(candidate)))
            {
                kind = PropertyChangedKind.Call;
                return null;
            }
        }

        if (!RequestsPropertyChanged(attribute))
        {
            return null;
        }

        var notifying = compilation.GetTypeByMetadataName(NotifyPropertyChangedMetadataName);
        if (notifying is not null && type.AllInterfaces.Contains(notifying, SymbolEqualityComparer.Default))
        {
            if (!HasOnPropertyChanged(type, compilation))
            {
                return "it implements INotifyPropertyChanged without an accessible OnPropertyChanged(string) method for the generated setters to call";
            }

            kind = PropertyChangedKind.Call;
            return null;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            var conflict = current.GetMembers().FirstOrDefault(member =>
                !member.IsImplicitlyDeclared
                && member.Name is "PropertyChanged" or "OnPropertyChanged"
                && (SymbolEqualityComparer.Default.Equals(current, type) || compilation.IsSymbolAccessibleWithin(member, type)));

            if (conflict is not null)
            {
                return $"'{conflict.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)}' conflicts with the generated PropertyChanged event and OnPropertyChanged method; implement INotifyPropertyChanged with an OnPropertyChanged(string) method instead";
            }
        }

        kind = PropertyChangedKind.Declare;
        return null;
    }

    private static bool RequestsPropertyChanged(AttributeData attribute)
        => attribute.NamedArguments.Any(argument => argument.Key == "NotifyPropertyChanged" && argument.Value.Value is true);

    /// <summary>An <c>OnPropertyChanged</c> instance method the generated setters can call with a property name.</summary>
    private static bool HasOnPropertyChanged(INamedTypeSymbol type, Compilation compilation)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var method in current.GetMembers("OnPropertyChanged").OfType<IMethodSymbol>())
            {
                if (!method.IsStatic
                    && method.TypeParameters.IsEmpty
                    && method.Parameters.Length > 0
                    && method.Parameters[0].Type.SpecialType == SpecialType.System_String
                    && method.Parameters[0].RefKind == RefKind.None
                    && method.Parameters.Skip(1).All(parameter => parameter.IsOptional)
                    && compilation.IsSymbolAccessibleWithin(method, type))
                {
                    return true;
                }
            }
        }

        return false;
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

    /// <summary>Reads <c>[ReactiveEquality]</c>; returns why it is invalid, or null.</summary>
    private static string? ReadEquality(
        IPropertySymbol property,
        INamedTypeSymbol state,
        Compilation compilation,
        out EqualityKind equality,
        out string? comparerType,
        out AttributeData? attribute)
    {
        equality = EqualityKind.Default;
        comparerType = null;
        attribute = property.GetAttributes().FirstOrDefault(candidate => candidate.AttributeClass?.ToDisplayString() == EqualityAttributeMetadataName);

        // A missing or malformed argument is reported by the compiler.
        if (attribute is null || attribute.ConstructorArguments.Length != 1)
        {
            return null;
        }

        var argument = attribute.ConstructorArguments[0];
        if (argument.Kind == TypedConstantKind.Type)
        {
            if (argument.Value is not INamedTypeSymbol comparer)
            {
                return "a comparer type is required";
            }

            var problem = FindComparerProblem(comparer, property.Type, state, compilation);
            if (problem is not null)
            {
                return problem;
            }

            equality = EqualityKind.Comparer;
            comparerType = comparer.ToDisplayString(TypeFormat);
            return null;
        }

        switch (argument.Value)
        {
            case (int)EqualityKind.Default:
                return null;

            case (int)EqualityKind.Reference:
                if (!property.Type.IsReferenceType)
                {
                    return $"reference equality needs a reference type, but '{property.Type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)}' is not one";
                }

                equality = EqualityKind.Reference;
                return null;

            case (int)EqualityKind.Never:
                equality = EqualityKind.Never;
                return null;

            default:
                return $"'{argument.Value}' is not a ReactiveEqualityMode";
        }
    }

    private static string? FindComparerProblem(INamedTypeSymbol comparer, ITypeSymbol propertyType, INamedTypeSymbol state, Compilation compilation)
    {
        var name = comparer.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

        if (comparer.IsUnboundGenericType)
        {
            return $"the open generic type '{name}' cannot be created";
        }

        if (comparer.IsAbstract || comparer.IsStatic)
        {
            return $"'{name}' cannot be created because it is abstract or static";
        }

        // Structs always have a parameterless constructor.
        if (comparer.TypeKind == TypeKind.Class
            && !comparer.InstanceConstructors.Any(constructor => constructor.Parameters.IsEmpty && compilation.IsSymbolAccessibleWithin(constructor, state)))
        {
            return $"'{name}' has no accessible parameterless constructor; for reference equality use ReactiveEqualityMode.Reference";
        }

        var target = compilation.GetTypeByMetadataName("System.Collections.Generic.IEqualityComparer`1")?.Construct(propertyType);
        if (target is null || !compilation.ClassifyCommonConversion(comparer, target).IsImplicit)
        {
            return $"'{name}' does not implement IEqualityComparer<{propertyType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)}>";
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

    private static PropertyModel CreatePropertyModel(
        IPropertySymbol property,
        PropertyDeclarationSyntax syntax,
        NullableContext nullableContext,
        EqualityKind equality,
        string? comparerType)
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
            NullableWarnings: nullableContext.WarningsEnabled(),
            Equality: equality,
            ComparerType: comparerType);

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

        List<string> interfaces = state.HasReactiveBase ? [] : [$"{Core}.IReactiveState"];
        if (state.PropertyChanged == PropertyChangedKind.Declare)
        {
            interfaces.Add(PropertyChangedNamespace + ".INotifyPropertyChanged");
        }

        writer.Line(interfaces.Count == 0
            ? $"partial {state.Type.Keyword} {state.Type.Name}"
            : $"partial {state.Type.Keyword} {state.Type.Name} : {string.Join(", ", interfaces)}");
        writer.Open();

        foreach (var property in state.Properties)
        {
            writer.Line($"private {Core}.ReactiveNode? {NodeFieldPrefix}{property.Name};");
        }

        if (state.PropertyChanged == PropertyChangedKind.Declare)
        {
            writer.Line();
            writer.Line("/// <inheritdoc/>");
            writer.Line($"public event {PropertyChangedNamespace}.PropertyChangedEventHandler? PropertyChanged;");
            writer.Line();
            writer.Line("/// <summary>Raises <see cref=\"PropertyChanged\"/>; the generated setters call it when a write notifies.</summary>");
            writer.Line("/// <param name=\"propertyName\">The name of the property that changed.</param>");
            writer.Line($"{(state.IsSealed ? "private" : "protected virtual")} void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new {PropertyChangedNamespace}.PropertyChangedEventArgs(propertyName));");
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

            var isInit = property.SetKeyword == "init";

            // Shared by every instance; declared in the property's nullable context so that its type matches.
            if (!isInit && property.Equality == EqualityKind.Comparer)
            {
                writer.Line($"private static readonly global::System.Collections.Generic.IEqualityComparer<{property.Type}> {ComparerFieldPrefix}{property.Name} = new {property.ComparerType}();");
                writer.Line();
            }

            var setter = property.Equality switch
            {
                EqualityKind.Reference => $"{Core}.ReactiveProperty.SetByReference(ref field, value, ref {node})",
                EqualityKind.Never => $"{Core}.ReactiveProperty.SetAlways(ref field, value, ref {node})",
                EqualityKind.Comparer => $"{Core}.ReactiveProperty.Set(ref field, value, ref {node}, {ComparerFieldPrefix}{property.Name})",
                _ => $"{Core}.ReactiveProperty.Set(ref field, value, ref {node})",
            };

            writer.Line($"{property.Modifiers} {property.Type} {property.Identifier}");
            writer.Open();
            writer.Line($"{Prefix(property.GetModifiers)}get => {Core}.ReactiveProperty.Get(ref field, ref {node}, {nodeName});");

            if (isInit)
            {
                writer.Line($"{Prefix(property.SetModifiers)}init => field = value;");
            }
            else if (state.PropertyChanged == PropertyChangedKind.None)
            {
                writer.Line($"{Prefix(property.SetModifiers)}set => {setter};");
            }
            else
            {
                // The comparison happens here rather than in ReactiveProperty so the event is raised only on a change.
                var equals = property.Equality switch
                {
                    EqualityKind.Reference => "global::System.Object.ReferenceEquals(field, value)",
                    EqualityKind.Never => null,
                    EqualityKind.Comparer => $"{ComparerFieldPrefix}{property.Name}.Equals(field, value)",
                    _ => $"global::System.Collections.Generic.EqualityComparer<{property.Type}>.Default.Equals(field, value)",
                };

                writer.Line($"{Prefix(property.SetModifiers)}set");
                writer.Open();

                if (equals is not null)
                {
                    writer.Line($"if ({equals})");
                    writer.Open();
                    writer.Line("return;");
                    writer.Close();
                    writer.Line();
                }

                writer.Line($"{Core}.ReactiveProperty.SetAlways(ref field, value, ref {node});");
                writer.Line($"OnPropertyChanged({SymbolDisplay.FormatLiteral(property.Name, quote: true)});");
                writer.Close();
            }

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
