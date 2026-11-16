using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Wick.Generators;

/// <summary>
/// Reports <c>state.PropertyChanged += handler</c> in a reactive component when the handler only re-renders it:
/// <c>StateHasChanged()</c>, <c>InvokeAsync(StateHasChanged)</c> (discarded or awaited), or either behind an <c>if</c>
/// that only tests the event's <c>PropertyName</c>. The component re-renders by itself when state its rendering read
/// changes, so the subscription is redundant.
/// </summary>
/// <remarks>
/// Only state whose <c>PropertyChanged</c> is the event Wick generates (<c>[ReactiveState(NotifyPropertyChanged = true)]</c>)
/// qualifies, and only if no hand-written code of the type raises it: then every event is a reactive property change.
/// A handler method is followed only when it is declared in the same file as the subscription.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PropertyChangedSubscriptionAnalyzer : DiagnosticAnalyzer
{
    private const string ComponentCoreMetadataName = "Wick.Blazor.ReactiveComponentCore";
    private const string ComponentBaseMetadataName = "Microsoft.AspNetCore.Components.ComponentBase";
    private const string HandlerMetadataName = "System.ComponentModel.PropertyChangedEventHandler";
    private const string GeneratedFileSuffix = ".ReactiveState.g.cs";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Diagnostics.RedundantPropertyChangedSubscription];

    public override void Initialize(AnalysisContext context)
    {
        // Razor components are compiled from generated code; their @code members map back to the .razor file.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static start =>
        {
            var componentCore = start.Compilation.GetTypeByMetadataName(ComponentCoreMetadataName);
            var componentBase = start.Compilation.GetTypeByMetadataName(ComponentBaseMetadataName);
            var handler = start.Compilation.GetTypeByMetadataName(HandlerMetadataName);
            if (componentCore is null || componentBase is null || handler is null)
            {
                return;
            }

            var analyzer = new Analysis(componentCore, componentBase, handler);
            start.RegisterOperationAction(analyzer.Analyze, OperationKind.EventAssignment);
        });
    }

    private sealed class Analysis(INamedTypeSymbol componentCore, INamedTypeSymbol componentBase, INamedTypeSymbol handler)
    {
        // Per state type: whether only generated setters raise its PropertyChanged.
        private readonly ConcurrentDictionary<INamedTypeSymbol, bool> _onlyGeneratedRaises = new(SymbolEqualityComparer.Default);

        public void Analyze(OperationAnalysisContext context)
        {
            var assignment = (IEventAssignmentOperation)context.Operation;
            if (!assignment.Adds
                || assignment.EventReference is not IEventReferenceOperation reference
                || reference.Instance?.Type is not INamedTypeSymbol state
                || reference.Event.Name != "PropertyChanged"
                || !SymbolEqualityComparer.Default.Equals(reference.Event.Type, handler))
            {
                return;
            }

            var component = context.ContainingSymbol.ContainingType;
            if (component is null
                || !TypeHelpers.DerivesFrom(component, componentCore)
                || !IsGenerated(reference.Event.OriginalDefinition)
                || !OnlyGeneratedRaises(state, reference.Event.ContainingType, context.CancellationToken)
                || !OnlyRenders(assignment.HandlerValue, component, context.CancellationToken))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.RedundantPropertyChangedSubscription, assignment.Syntax.GetLocation(), component.Name));
        }

        private static bool IsGenerated(ISymbol symbol)
            => symbol.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree.FilePath.EndsWith(GeneratedFileSuffix, System.StringComparison.Ordinal));

        /// <summary>
        /// Whether no hand-written code of <paramref name="state"/>, or of its bases up to the one declaring the event,
        /// mentions <c>PropertyChanged</c> or <c>OnPropertyChanged</c>: such code could raise the event for state Wick
        /// does not track.
        /// </summary>
        private bool OnlyGeneratedRaises(INamedTypeSymbol state, INamedTypeSymbol declaring, System.Threading.CancellationToken cancellationToken)
        {
            for (var current = state; current is not null; current = current.BaseType)
            {
                var type = current.OriginalDefinition;
                if (!_onlyGeneratedRaises.GetOrAdd(type, candidate => !MentionsPropertyChanged(candidate, cancellationToken)))
                {
                    return false;
                }

                if (SymbolEqualityComparer.Default.Equals(type, declaring.OriginalDefinition))
                {
                    return true;
                }
            }

            return true;
        }

        private static bool MentionsPropertyChanged(INamedTypeSymbol type, System.Threading.CancellationToken cancellationToken)
            => type.DeclaringSyntaxReferences
                .Where(reference => !reference.SyntaxTree.FilePath.EndsWith(GeneratedFileSuffix, System.StringComparison.Ordinal))
                .SelectMany(reference => reference.GetSyntax(cancellationToken).DescendantNodes())
                .OfType<IdentifierNameSyntax>()
                .Any(name => name.Identifier.ValueText is "PropertyChanged" or "OnPropertyChanged");

        /// <summary>Whether the subscribed delegate only re-renders <paramref name="component"/>.</summary>
        private bool OnlyRenders(IOperation handlerValue, INamedTypeSymbol component, System.Threading.CancellationToken cancellationToken)
        {
            if (handlerValue is not IDelegateCreationOperation creation)
            {
                return false;
            }

            switch (creation.Target)
            {
                case IAnonymousFunctionOperation lambda:
                    return IsRenderBlock(lambda.Body, Arguments(lambda.Symbol));

                case IMethodReferenceOperation method
                    when method.Instance is IInstanceReferenceOperation
                         && SymbolEqualityComparer.Default.Equals(method.Method.ContainingType, component)
                         && method.Method.DeclaringSyntaxReferences.Length == 1:
                    var syntax = method.Method.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken);
                    if (syntax is not MethodDeclarationSyntax || syntax.SyntaxTree != creation.Syntax.SyntaxTree)
                    {
                        return false;
                    }

                    return creation.SemanticModel!.GetOperation(syntax, cancellationToken) is IMethodBodyOperation body
                           && (body.BlockBody ?? body.ExpressionBody) is IBlockOperation block
                           && IsRenderBlock(block, Arguments(method.Method));

                default:
                    return false;
            }

            static IParameterSymbol? Arguments(IMethodSymbol method) => method.Parameters.Length == 2 ? method.Parameters[1] : null;
        }

        private bool IsRenderBlock(IBlockOperation block, IParameterSymbol? arguments)
        {
            var statements = block.Operations.Where(operation => !(operation is IReturnOperation returned && returned.IsImplicit && returned.ReturnedValue is null)).ToList();

            return statements.Count > 0 && statements.All(statement => IsRenderStatement(statement, arguments));
        }

        private bool IsRenderStatement(IOperation statement, IParameterSymbol? arguments) => statement switch
        {
            IBlockOperation block => IsRenderBlock(block, arguments),
            IExpressionStatementOperation expression => IsRender(expression.Operation, arguments),
            IConditionalOperation conditional => conditional.WhenFalse is null
                                                 && TestsPropertyName(conditional.Condition, arguments)
                                                 && IsRenderStatement(conditional.WhenTrue, arguments),
            _ => false,
        };

        /// <summary><c>StateHasChanged()</c> or <c>InvokeAsync(StateHasChanged)</c>, possibly discarded or awaited.</summary>
        private bool IsRender(IOperation operation, IParameterSymbol? arguments)
        {
            switch (operation)
            {
                case ISimpleAssignmentOperation assignment when assignment.Target is IDiscardOperation:
                    return IsRender(assignment.Value, arguments);

                case IAwaitOperation awaited:
                    return IsRender(awaited.Operation, arguments);

                case IInvocationOperation invocation when invocation.Instance is IInstanceReferenceOperation && IsComponentBaseMethod(invocation.TargetMethod):
                    if (invocation.TargetMethod.Name == "StateHasChanged")
                    {
                        return invocation.Arguments.IsEmpty;
                    }

                    return invocation.TargetMethod.Name == "InvokeAsync"
                           && invocation.Arguments.Length == 1
                           && invocation.Arguments[0].Value is IDelegateCreationOperation callback
                           && callback.Target switch
                           {
                               IMethodReferenceOperation method => method.Instance is IInstanceReferenceOperation
                                                                   && method.Method.Name == "StateHasChanged"
                                                                   && IsComponentBaseMethod(method.Method),
                               IAnonymousFunctionOperation lambda => IsRenderBlock(lambda.Body, arguments),
                               _ => false,
                           };

                default:
                    return false;
            }
        }

        private bool IsComponentBaseMethod(IMethodSymbol method)
            => SymbolEqualityComparer.Default.Equals(method.OriginalDefinition.ContainingType, componentBase);

        /// <summary>Whether a condition only reads the event arguments' <c>PropertyName</c> and constants.</summary>
        private static bool TestsPropertyName(IOperation operation, IParameterSymbol? arguments)
        {
            if (operation.ConstantValue.HasValue)
            {
                return true;
            }

            switch (operation)
            {
                case INameOfOperation:
                    return true;

                case IParameterReferenceOperation parameter:
                    return SymbolEqualityComparer.Default.Equals(parameter.Parameter, arguments);

                case IPropertyReferenceOperation property:
                    return property.Property.Name == "PropertyName"
                           && property.Instance is not null
                           && TestsPropertyName(property.Instance, arguments);

                case IInvocationOperation invocation:
                    // string.Equals, string.IsNullOrEmpty and the like.
                    return invocation.TargetMethod.ContainingType.SpecialType == SpecialType.System_String
                           && invocation.ChildOperations.All(child => TestsPropertyName(child, arguments));

                case IBinaryOperation:
                case IUnaryOperation:
                case IConversionOperation:
                case IArgumentOperation:
                case IIsPatternOperation:
                case IPatternOperation:
                    return operation.ChildOperations.All(child => TestsPropertyName(child, arguments));

                default:
                    return false;
            }
        }
    }
}
