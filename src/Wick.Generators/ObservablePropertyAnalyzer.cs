using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wick.Generators;

/// <summary>
/// Points out classes using CommunityToolkit.Mvvm's <c>[ObservableProperty]</c> in projects that reference Wick; the
/// code fix turns them into <c>[ReactiveState]</c> classes. The toolkit is matched by name: Wick does not reference it.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ObservablePropertyAnalyzer : DiagnosticAnalyzer
{
    private const string AttributeMetadataName = "Wick.Core.ReactiveStateAttribute";
    private const string ObservablePropertyMetadataName = "CommunityToolkit.Mvvm.ComponentModel.ObservablePropertyAttribute";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Diagnostics.ObservablePropertyCanBeReactive];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static start =>
        {
            var observableProperty = start.Compilation.GetTypeByMetadataName(ObservablePropertyMetadataName);
            if (observableProperty is null || start.Compilation.GetTypeByMetadataName(AttributeMetadataName) is null)
            {
                return;
            }

            start.RegisterSymbolAction(context => Analyze(context, observableProperty), SymbolKind.NamedType);
        });
    }

    private static void Analyze(SymbolAnalysisContext context, INamedTypeSymbol observableProperty)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        var member = type.GetMembers().FirstOrDefault(candidate =>
            candidate.Kind is SymbolKind.Field or SymbolKind.Property
            && candidate.GetAttributes().Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, observableProperty)));

        var declaration = member?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken).FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (declaration is null)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ObservablePropertyCanBeReactive, declaration.Identifier.GetLocation(), type.Name));
    }
}
