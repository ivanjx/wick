using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wick.Generators;

/// <summary>
/// Reports reactive components that declare their own <c>Dispose()</c> or <c>DisposeAsync()</c>. With
/// <c>@implements IDisposable</c> (or <c>IAsyncDisposable</c>) the method re-implements the interface and Blazor
/// calls it instead of the base cleanup; without it, the method only hides the base one and Blazor never calls it.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ReactiveComponentDisposalAnalyzer : DiagnosticAnalyzer
{
    private const string ComponentCoreMetadataName = "Wick.Blazor.ReactiveComponentCore";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Diagnostics.DisposalBypassesReactiveCleanup];

    public override void Initialize(AnalysisContext context)
    {
        // Razor components are compiled from generated code; their @code members map back to the .razor file.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static start =>
        {
            var componentCore = start.Compilation.GetTypeByMetadataName(ComponentCoreMetadataName);
            if (componentCore is null)
            {
                return;
            }

            var asyncDisposable = start.Compilation.GetTypeByMetadataName("System.IAsyncDisposable");

            start.RegisterSymbolAction(context => Analyze(context, componentCore, asyncDisposable), SymbolKind.NamedType);
        });
    }

    private static void Analyze(SymbolAnalysisContext context, INamedTypeSymbol componentCore, INamedTypeSymbol? asyncDisposable)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!DerivesFrom(type, componentCore))
        {
            return;
        }

        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.IsImplicitlyDeclared || method.Locations.FirstOrDefault() is not { IsInSource: true } location)
            {
                continue;
            }

            if (IsDisposalMethod(method, asyncDisposable))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.DisposalBypassesReactiveCleanup,
                    location,
                    method.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                    type.Name));
            }
        }
    }

    private static bool IsDisposalMethod(IMethodSymbol method, INamedTypeSymbol? asyncDisposable)
    {
        if (method.MethodKind == MethodKind.ExplicitInterfaceImplementation)
        {
            return method.ExplicitInterfaceImplementations.Any(implemented =>
                implemented.ContainingType.SpecialType == SpecialType.System_IDisposable
                || SymbolEqualityComparer.Default.Equals(implemented.ContainingType, asyncDisposable));
        }

        return method.MethodKind == MethodKind.Ordinary
               && !method.IsStatic
               && !method.IsOverride
               && method.Parameters.IsEmpty
               && method.Name is "Dispose" or "DisposeAsync";
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }
}
