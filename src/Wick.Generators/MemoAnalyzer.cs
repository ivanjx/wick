using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Wick.Generators;

/// <summary>
/// Reports <c>Memo</c> calls whose call site cannot identify one cached value: calls outside the getter of a property
/// without parameters (WK0017), and computations capturing a local or parameter, of which only the first value
/// would ever be used (WK0018). Covers the helpers generated for <c>[ReactiveState]</c> types and those of
/// <c>ReactiveObject</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MemoAnalyzer : DiagnosticAnalyzer
{
    private const string AttributeMetadataName = "Wick.Core.ReactiveStateAttribute";
    private const string ReactiveObjectMetadataName = "Wick.Core.ReactiveObject";
    private const string GeneratedFileSuffix = ".ReactiveState.g.cs";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Diagnostics.MemoOutsideGetter, Diagnostics.MemoCapturesLocal];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static start =>
        {
            var attribute = start.Compilation.GetTypeByMetadataName(AttributeMetadataName);
            var reactiveObject = start.Compilation.GetTypeByMetadataName(ReactiveObjectMetadataName);
            if (attribute is null && reactiveObject is null)
            {
                return;
            }

            start.RegisterOperationAction(
                context =>
                {
                    var invocation = (IInvocationOperation)context.Operation;
                    if (IsMemo(invocation.TargetMethod, attribute, reactiveObject))
                    {
                        Analyze(context, invocation);
                    }
                },
                OperationKind.Invocation);
        });
    }

    private static bool IsMemo(IMethodSymbol method, INamedTypeSymbol? attribute, INamedTypeSymbol? reactiveObject)
    {
        if (method.Name != "Memo")
        {
            return false;
        }

        var type = method.ContainingType;
        if (SymbolEqualityComparer.Default.Equals(type, reactiveObject))
        {
            return true;
        }

        return attribute is not null
               && method.DeclaredAccessibility == Accessibility.Private
               && method.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath.EndsWith(GeneratedFileSuffix, System.StringComparison.Ordinal) == true
               && type.GetAttributes().Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute));
    }

    private static void Analyze(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        var semanticModel = invocation.SemanticModel!;
        var enclosing = semanticModel.GetEnclosingSymbol(invocation.Syntax.SpanStart, context.CancellationToken);

        var misplacedIn = enclosing switch
        {
            IMethodSymbol { MethodKind: MethodKind.PropertyGet, AssociatedSymbol: IPropertySymbol { IsIndexer: false } } => null,
            IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } => "a lambda or local function",
            IMethodSymbol { MethodKind: MethodKind.PropertyGet } => "an indexer",
            null => "this context",
            _ => $"'{enclosing.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)}'",
        };

        if (misplacedIn is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.MemoOutsideGetter, invocation.Syntax.GetLocation(), misplacedIn));
        }

        var compute = invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 0)?.Value;
        if (compute is IDelegateCreationOperation { Target: IAnonymousFunctionOperation { Syntax: AnonymousFunctionExpressionSyntax lambda } }
            && !lambda.Modifiers.Any(modifier => modifier.ValueText == "static"))
        {
            ReportCaptures(context, semanticModel, lambda);
        }
    }

    private static void ReportCaptures(OperationAnalysisContext context, SemanticModel semanticModel, AnonymousFunctionExpressionSyntax lambda)
    {
        var dataFlow = semanticModel.AnalyzeDataFlow(lambda);
        if (dataFlow is null || !dataFlow.Succeeded)
        {
            return;
        }

        // Captured also lists 'this', primary constructor parameters (stored like fields), and variables declared in
        // the lambda that nested lambdas capture; none of them goes stale.
        foreach (var symbol in dataFlow.Captured)
        {
            if (symbol is IParameterSymbol { IsThis: true } or IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor } }
                || symbol.Locations.Any(location => lambda.Span.Contains(location.SourceSpan)))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.MemoCapturesLocal, lambda.GetLocation(), symbol.Name));
        }
    }
}
