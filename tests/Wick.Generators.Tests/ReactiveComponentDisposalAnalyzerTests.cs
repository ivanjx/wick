using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wick.Generators.Tests;

public sealed class ReactiveComponentDisposalAnalyzerTests
{
    // Stand-in for the Blazor adapter: the analyzer matches the base class by metadata name.
    private const string ComponentCore =
        """
        using System;
        using System.Threading.Tasks;

        namespace Wick.Blazor
        {
            public abstract class ReactiveComponentCore : IDisposable, IAsyncDisposable
            {
                public void Dispose() => Dispose(true);

                public ValueTask DisposeAsync() => DisposeAsyncCore();

                protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;

                protected virtual void Dispose(bool disposing) { }
            }
        }
        """;

    public static TheoryData<string, string> Bypasses => new()
    {
        { "hides Dispose", "public new void Dispose() { }" },
        { "hides DisposeAsync", "public new ValueTask DisposeAsync() => default;" },
        { "explicit IDisposable", "void IDisposable.Dispose() { }" },
        { "explicit IAsyncDisposable", "ValueTask IAsyncDisposable.DisposeAsync() => default;" },
    };

    [Theory]
    [MemberData(nameof(Bypasses))]
    public async Task Own_disposal_in_a_reactive_component_is_reported(string shape, string member)
    {
        var diagnostics = await AnalyzeAsync($"public class View : ReactiveComponentCore, IDisposable, IAsyncDisposable {{ {member} }}");

        Assert.True(diagnostics.Length == 1 && diagnostics[0].Id == "WK0007", $"{shape}: {string.Join(", ", diagnostics)}");
    }

    [Fact]
    public async Task Overriding_the_disposal_hooks_and_unrelated_types_are_not_reported()
    {
        var diagnostics = await AnalyzeAsync(
            """
            public class View : ReactiveComponentCore
            {
                protected override void Dispose(bool disposing) => base.Dispose(disposing);

                protected override ValueTask DisposeAsyncCore() => default;

                public void Dispose(int reason) { }
            }

            public class Plain : IDisposable
            {
                public void Dispose() { }
            }
            """);

        Assert.Empty(diagnostics);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string body)
    {
        var compilation = GeneratorRunner.CreateCompilation(
            $$"""
            {{ComponentCore}}

            namespace App
            {
                using System;
                using System.Threading.Tasks;
                using Wick.Blazor;

                {{body}}
            }
            """);

        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return await compilation
            .WithAnalyzers([new ReactiveComponentDisposalAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
    }
}
