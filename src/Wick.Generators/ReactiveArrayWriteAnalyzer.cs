using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Wick.Generators;

/// <summary>
/// Reports writes to an element of an array read straight from a reactive property, such as
/// <c>state.Cells[i] = value</c>. Only replacing the property's value notifies, so readers of the property never see
/// the change. Writes through a copy of the reference (<c>var cells = state.Cells; cells[i] = value;</c>) or a ref
/// local are not followed.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ReactiveArrayWriteAnalyzer : DiagnosticAnalyzer
{
    private const string AttributeMetadataName = "Wick.Core.ReactiveStateAttribute";
    private const string ReactiveObjectMetadataName = "Wick.Core.ReactiveObject";
    private const string GeneratedFileSuffix = ".ReactiveState.g.cs";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Diagnostics.UntrackedArrayElementWrite];

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

            var properties = new ReactivePropertyCache(attribute, reactiveObject);

            start.RegisterOperationAction(
                context => AnalyzeTarget(context, ((IAssignmentOperation)context.Operation).Target, properties),
                OperationKind.SimpleAssignment,
                OperationKind.CompoundAssignment,
                OperationKind.CoalesceAssignment,
                OperationKind.DeconstructionAssignment);

            start.RegisterOperationAction(
                context => AnalyzeTarget(context, ((IIncrementOrDecrementOperation)context.Operation).Target, properties),
                OperationKind.Increment,
                OperationKind.Decrement);

            start.RegisterOperationAction(
                context =>
                {
                    var argument = (IArgumentOperation)context.Operation;
                    if (argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out)
                    {
                        AnalyzeTarget(context, argument.Value, properties);
                    }
                },
                OperationKind.Argument);
        });
    }

    private static void AnalyzeTarget(OperationAnalysisContext context, IOperation target, ReactivePropertyCache properties)
    {
        // A deconstruction assigns every element of the tuple on its left.
        if (target is ITupleOperation tuple)
        {
            foreach (var item in tuple.Elements)
            {
                AnalyzeTarget(context, item, properties);
            }

            return;
        }

        if (target is IArrayElementReferenceOperation { ArrayReference: IPropertyReferenceOperation { Property: var property } } element
            && properties.IsReactive(property, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.UntrackedArrayElementWrite, element.Syntax.GetLocation(), property.Name));
        }
    }

    /// <summary>Decides, once per property, whether reading it is tracked.</summary>
    private sealed class ReactivePropertyCache(INamedTypeSymbol? attribute, INamedTypeSymbol? reactiveObject)
    {
        private readonly ConcurrentDictionary<IPropertySymbol, bool> _cache = new(SymbolEqualityComparer.Default);

        public bool IsReactive(IPropertySymbol property, System.Threading.CancellationToken cancellationToken)
        {
            var definition = property.OriginalDefinition;
            definition = definition.PartialDefinitionPart ?? definition;

            return _cache.GetOrAdd(definition, candidate => Compute(candidate, cancellationToken));
        }

        private bool Compute(IPropertySymbol property, System.Threading.CancellationToken cancellationToken)
        {
            var type = property.ContainingType;

            if (attribute is not null
                && property.IsPartialDefinition
                && IsGenerated(property)
                && type.GetAttributes().Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute)))
            {
                return true;
            }

            return reactiveObject is not null
                   && DerivesFrom(type, reactiveObject)
                   && GetterCallsGet(property.PartialImplementationPart ?? property, cancellationToken);
        }

        // Hand-written implementations of partial properties are not reactive unless their author made them so.
        private static bool IsGenerated(IPropertySymbol property)
            => property.PartialImplementationPart?.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath
                .EndsWith(GeneratedFileSuffix, System.StringComparison.Ordinal) == true;

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

        /// <summary>
        /// Matches the <c>ReactiveObject</c> opt-in, <c>get => Get(ref field)</c>, by syntax:
        /// binding the getter would need a semantic model of another syntax tree.
        /// </summary>
        private static bool GetterCallsGet(IPropertySymbol property, System.Threading.CancellationToken cancellationToken)
        {
            if (property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is not PropertyDeclarationSyntax syntax)
            {
                return false;
            }

            SyntaxNode? getter = syntax.ExpressionBody;
            if (getter is null)
            {
                var accessor = syntax.AccessorList?.Accessors.FirstOrDefault(candidate => candidate.IsKind(SyntaxKind.GetAccessorDeclaration));
                getter = (SyntaxNode?)accessor?.ExpressionBody ?? accessor?.Body;
            }

            return getter is not null && getter.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(IsGetCall);
        }

        private static bool IsGetCall(InvocationExpressionSyntax invocation)
        {
            var name = invocation.Expression switch
            {
                SimpleNameSyntax simple => simple,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax } access => access.Name,
                _ => null,
            };

            return name?.Identifier.ValueText == "Get"
                   && invocation.ArgumentList.Arguments.FirstOrDefault()?.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) == true;
        }
    }
}
