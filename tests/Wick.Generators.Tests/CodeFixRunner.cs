using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wick.Generators.Tests;

/// <summary>
/// Runs the generator and analyzers over a workspace project, applies a code fix to one of the reported
/// diagnostics, and checks that the fixed project compiles.
/// </summary>
internal static class CodeFixRunner
{
    private const string FileName = "Consumer.cs";

    /// <summary>
    /// Applies the first fix offered for the <paramref name="id"/> diagnostic in <c>Consumer.cs</c> (the one reported
    /// on <paramref name="at"/> if given), or the one titled <paramref name="title"/>, and returns the fixed text.
    /// <paramref name="otherSources"/> become other files.
    /// </summary>
    public static async Task<string> FixAsync(CodeFixProvider provider, string id, string source, string? at = null, string? title = null, params string[] otherSources)
    {
        var (document, diagnostics) = await AnalyzeAsync(source, otherSources);
        var diagnostic = diagnostics.First(candidate => IsReported(candidate, id) && (at is null || source[candidate.Location.SourceSpan.Start..candidate.Location.SourceSpan.End] == at));

        var actions = await GetActionsAsync(provider, document, diagnostic);
        Assert.NotEmpty(actions);

        return await ApplyAsync(document, title is null ? actions[0] : actions.Single(action => action.Title == title));
    }

    /// <summary>Applies the fix-all of the first fix to every <paramref name="id"/> diagnostic in <c>Consumer.cs</c>.</summary>
    public static async Task<string> FixAllAsync(CodeFixProvider provider, string id, string source, params string[] otherSources)
    {
        var (document, diagnostics) = await AnalyzeAsync(source, otherSources);
        var fixable = diagnostics.Where(candidate => IsReported(candidate, id)).ToImmutableArray();
        var actions = await GetActionsAsync(provider, document, fixable[0]);

        var context = new FixAllContext(
            document,
            provider,
            FixAllScope.Document,
            actions[0].EquivalenceKey,
            [id],
            new FixedDiagnostics(fixable),
            TestContext.Current.CancellationToken);

        var fixAll = await provider.GetFixAllProvider()!.GetFixAsync(context);
        Assert.NotNull(fixAll);

        return await ApplyAsync(document, fixAll);
    }

    /// <summary>The fixes offered for every <paramref name="id"/> diagnostic in <c>Consumer.cs</c>.</summary>
    public static async Task<ImmutableArray<CodeAction>> GetActionsAsync(CodeFixProvider provider, string id, string source, params string[] otherSources)
    {
        var (document, diagnostics) = await AnalyzeAsync(source, otherSources);
        var reported = diagnostics.Where(candidate => IsReported(candidate, id)).ToList();
        Assert.NotEmpty(reported);

        var actions = ImmutableArray.CreateBuilder<CodeAction>();
        foreach (var diagnostic in reported)
        {
            actions.AddRange(await GetActionsAsync(provider, document, diagnostic));
        }

        return actions.ToImmutable();
    }

    private static async Task<(Document Document, ImmutableArray<Diagnostic> Diagnostics)> AnalyzeAsync(string source, string[] otherSources)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.CurrentSolution
            .AddProject("Consumer", "Consumer", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.CSharp14))
            .WithMetadataReferences(GeneratorRunner.CreateCompilation("").References);

        for (var index = 0; index < otherSources.Length; index++)
        {
            project = project.AddDocument($"Other{index}.cs", otherSources[index], filePath: $"Other{index}.cs").Project;
        }

        var document = project.AddDocument(FileName, source, filePath: FileName);

        return (document, await DiagnoseAsync(document.Project));
    }

    private static async Task<ImmutableArray<Diagnostic>> DiagnoseAsync(Project project)
    {
        var compilation = (await project.GetCompilationAsync(TestContext.Current.CancellationToken))!;
        GeneratorRunner.CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        var analyzerDiagnostics = await output
            .WithAnalyzers([new ReactiveStateAnalyzer(), new ReactiveComponentDisposalAnalyzer(), new PropertyChangedSubscriptionAnalyzer(), new ObservablePropertyAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        return
        [
            .. generatorDiagnostics,
            .. analyzerDiagnostics,
            .. output.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
        ];
    }

    private static async Task<ImmutableArray<CodeAction>> GetActionsAsync(CodeFixProvider provider, Document document, Diagnostic diagnostic)
    {
        var actions = ImmutableArray.CreateBuilder<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), TestContext.Current.CancellationToken);
        await provider.RegisterCodeFixesAsync(context);

        return actions.ToImmutable();
    }

    private static async Task<string> ApplyAsync(Document document, CodeAction action)
    {
        var operations = await action.GetOperationsAsync(TestContext.Current.CancellationToken);
        var solution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;

        var errors = (await DiagnoseAsync(solution.GetProject(document.Project.Id)!)).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "The fixed code does not compile:" + Environment.NewLine + string.Join(Environment.NewLine, errors));

        var text = await solution.GetDocument(document.Id)!.GetTextAsync(TestContext.Current.CancellationToken);

        return text.ToString();
    }

    // Generator diagnostics carry a file path but no syntax tree.
    private static bool IsReported(Diagnostic diagnostic, string id) => diagnostic.Id == id && diagnostic.Location.GetLineSpan().Path == FileName;

    private sealed class FixedDiagnostics(ImmutableArray<Diagnostic> diagnostics) : FixAllContext.DiagnosticProvider
    {
        public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken)
        {
            var tree = await document.GetSyntaxTreeAsync(cancellationToken);

            return diagnostics.Where(diagnostic => diagnostic.Location.SourceTree == tree);
        }

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<Diagnostic>>([]);

        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);
    }
}
