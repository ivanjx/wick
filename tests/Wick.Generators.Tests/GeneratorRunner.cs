using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Wick.Core;

namespace Wick.Generators.Tests;

/// <summary>Compiles source against the runtime and Wick.Core with the generator applied.</summary>
internal static class GeneratorRunner
{
    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)),
        MetadataReference.CreateFromFile(typeof(ReactiveStateAttribute).Assembly.Location),
    ];

    public static GeneratorRun Run(string source, LanguageVersion languageVersion = LanguageVersion.CSharp14)
    {
        var compilation = CreateCompilation(source, languageVersion);
        var driver = CreateDriver(languageVersion).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        var analyzerDiagnostics = output
            .WithAnalyzers([new ReactiveStateAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();

        return new GeneratorRun(
            Reported: [.. generatorDiagnostics, .. analyzerDiagnostics],
            Compilation: output.GetDiagnostics().Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning).ToImmutableArray(),
            GeneratedSources: driver.GetRunResult().GeneratedTrees.Length);
    }

    public static CSharpCompilation CreateCompilation(string source, LanguageVersion languageVersion = LanguageVersion.CSharp14)
        => CSharpCompilation.Create(
            "Consumer",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(languageVersion), path: "Consumer.cs")],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    public static GeneratorDriver CreateDriver(LanguageVersion languageVersion = LanguageVersion.CSharp14)
        => CSharpGeneratorDriver.Create(
            [new ReactiveStateGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(languageVersion),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
}

/// <param name="Reported">Diagnostics reported by the generator and the reactive state analyzer.</param>
/// <param name="Compilation">Warnings and errors of the compilation including generated code.</param>
internal sealed record GeneratorRun(ImmutableArray<Diagnostic> Reported, ImmutableArray<Diagnostic> Compilation, int GeneratedSources)
{
    public IEnumerable<string> ReportedIds => Reported.Select(diagnostic => diagnostic.Id);

    public string Describe() => string.Join(Environment.NewLine, Reported.Concat(Compilation));
}
