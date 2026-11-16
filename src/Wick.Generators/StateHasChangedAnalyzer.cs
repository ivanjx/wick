using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Wick.Generators;

/// <summary>
/// Reports <c>StateHasChanged</c> calls and method group references (<c>InvokeAsync(StateHasChanged)</c>) in reactive
/// components. Disabled by default: they are still right for changes to non-reactive state, so this is an audit aid
/// for migrations rather than a warning.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StateHasChangedAnalyzer : DiagnosticAnalyzer
{
    private const string ComponentCoreMetadataName = "Wick.Blazor.ReactiveComponentCore";
    private const string ComponentBaseMetadataName = "Microsoft.AspNetCore.Components.ComponentBase";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Diagnostics.StateHasChangedInReactiveComponent];

    public override void Initialize(AnalysisContext context)
    {
        // Razor components are compiled from generated code; their @code members map back to the .razor file.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static start =>
        {
            var componentCore = start.Compilation.GetTypeByMetadataName(ComponentCoreMetadataName);
            var componentBase = start.Compilation.GetTypeByMetadataName(ComponentBaseMetadataName);
            if (componentCore is null || componentBase is null)
            {
                return;
            }

            start.RegisterOperationAction(
                context =>
                {
                    var method = context.Operation switch
                    {
                        IInvocationOperation invocation => invocation.TargetMethod,
                        IMethodReferenceOperation reference => reference.Method,
                        _ => null,
                    };

                    var component = context.ContainingSymbol.ContainingType;
                    if (method is null
                        || method.Name != "StateHasChanged"
                        || !SymbolEqualityComparer.Default.Equals(method.ContainingType, componentBase)
                        || component is null
                        || !TypeHelpers.DerivesFrom(component, componentCore))
                    {
                        return;
                    }

                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.StateHasChangedInReactiveComponent, context.Operation.Syntax.GetLocation(), component.Name));
                },
                OperationKind.Invocation,
                OperationKind.MethodReference);
        });
    }
}
