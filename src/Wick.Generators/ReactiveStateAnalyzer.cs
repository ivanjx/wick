using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wick.Generators;

/// <summary>
/// Warnings about the properties of <c>[ReactiveState]</c> types. They are reported here rather than by
/// <see cref="ReactiveStateGenerator"/> because generator diagnostics cannot be suppressed with <c>#pragma</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ReactiveStateAnalyzer : DiagnosticAnalyzer
{
    private const string AttributeMetadataName = "Wick.Core.ReactiveStateAttribute";
    private const string GeneratedFileSuffix = ".ReactiveState.g.cs";
    private const string BlazorNamespace = "Microsoft.AspNetCore.Components";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [Diagnostics.AutoPropertyIsNotReactive, Diagnostics.MutableCollectionProperty];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static start =>
        {
            var attribute = start.Compilation.GetTypeByMetadataName(AttributeMetadataName);
            if (attribute is null)
            {
                return;
            }

            start.RegisterSymbolAction(context => Analyze(context, attribute), SymbolKind.NamedType);
        });
    }

    private static void Analyze(SymbolAnalysisContext context, INamedTypeSymbol attribute)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!type.GetAttributes().Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute)))
        {
            return;
        }

        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is not PropertyDeclarationSyntax syntax)
            {
                continue;
            }

            var location = syntax.Identifier.GetLocation();

            if (!property.IsPartialDefinition)
            {
                if (IsWritableAutoProperty(property, syntax) && !IsSetByBlazor(property))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.AutoPropertyIsNotReactive, location, property.Name));
                }

                continue;
            }

            if (IsGenerated(property) && IsMutableCollection(property.Type))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.MutableCollectionProperty,
                    location,
                    property.Name,
                    property.Type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
            }
        }
    }

    // Init-only auto-properties cannot change after construction, so they need no tracking.
    private static bool IsWritableAutoProperty(IPropertySymbol property, PropertyDeclarationSyntax syntax)
        => !property.IsStatic
           && !property.IsAbstract
           && property.SetMethod is not null
           && !property.SetMethod.IsInitOnly
           && syntax.AccessorList is not null
           && syntax.AccessorList.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null);

    // A [ReactiveState] component's [Parameter], [CascadingParameter], [Inject] and [SupplyParameterFrom...]
    // properties are assigned by Blazor, which re-renders the component afterwards; they need no tracking.
    private static bool IsSetByBlazor(IPropertySymbol property)
        => property.GetAttributes().Any(attribute => attribute.AttributeClass?.ContainingNamespace?.ToDisplayString() == BlazorNamespace);

    // Hand-written implementations of partial properties are not reactive unless their author made them so.
    private static bool IsGenerated(IPropertySymbol property)
        => property.PartialImplementationPart?.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath
            .EndsWith(GeneratedFileSuffix, System.StringComparison.Ordinal) == true;

    /// <summary>
    /// Types whose contents can change without a setter call (anything implementing <c>ICollection&lt;T&gt;</c>).
    /// Arrays are excluded: replacing a whole array is the common, tracked usage. Immutable, frozen, read-only
    /// wrappers and the reactive collections only implement the interface to be read.
    /// </summary>
    private static bool IsMutableCollection(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return false;
        }

        var containingNamespace = named.ContainingNamespace?.ToDisplayString();
        if (containingNamespace is "System.Collections.Immutable" or "System.Collections.Frozen" or "Wick.Core"
            || (containingNamespace == "System.Collections.ObjectModel" && named.Name.StartsWith("ReadOnly", System.StringComparison.Ordinal)))
        {
            return false;
        }

        return IsCollectionInterface(named) || named.AllInterfaces.Any(IsCollectionInterface);

        static bool IsCollectionInterface(INamedTypeSymbol candidate)
            => candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_ICollection_T;
    }
}
