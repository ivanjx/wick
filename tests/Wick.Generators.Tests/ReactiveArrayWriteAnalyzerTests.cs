using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wick.Generators.Tests;

public sealed class ReactiveArrayWriteAnalyzerTests
{
    private const string Types =
        """
        [ReactiveState]
        public partial class Board
        {
            public partial int[] Cells { get; set; } = [];

            public partial int[,] Grid { get; set; } = new int[2, 2];

            public partial string?[] Names { get; set; } = [];

            public int[] Plain { get; set; } = [];
        }

        public class Legacy : ReactiveObject
        {
            public int[] Cells { get => Get(ref field); set => Set(ref field, value); } = [];

            public int[] Untracked { get; set; } = [];
        }

        public class Holder
        {
            public int[] Cells { get; set; } = [];
        }

        public static class Helpers
        {
            public static void Increment(ref int value) => value++;

            public static void Reset(out int value) => value = 0;

            public static int Peek(in int value) => value;
        }
        """;

    public static TheoryData<string, string, string> Writes => new()
    {
        { "state.Cells[i] = 1;", "state.Cells[i]", "Cells" },
        { "state.Cells[i] += 1;", "state.Cells[i]", "Cells" },
        { "state.Cells[i]++;", "state.Cells[i]", "Cells" },
        { "--state.Cells[i];", "state.Cells[i]", "Cells" },
        { "state.Names[i] ??= \"name\";", "state.Names[i]", "Names" },
        { "Helpers.Increment(ref state.Cells[i]);", "state.Cells[i]", "Cells" },
        { "Helpers.Reset(out state.Cells[i]);", "state.Cells[i]", "Cells" },
        { "state.Grid[i, 0] = 1;", "state.Grid[i, 0]", "Grid" },
        { "(state.Cells[0], i) = (1, 2);", "state.Cells[0]", "Cells" },
        { "legacy.Cells[i] = 1;", "legacy.Cells[i]", "Cells" },
    };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Writing_an_element_of_a_reactive_array_is_reported(string statement, string target, string property)
    {
        var (diagnostics, source) = await AnalyzeAsync(UseSource(statement));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("WK0008", diagnostic.Id);
        Assert.Contains($"'{property}'", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(target, source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public async Task Writing_an_element_inside_the_reactive_type_is_reported()
    {
        var (diagnostics, source) = await AnalyzeAsync(UseSource("", boardMember: "public void Clear(int i) => Cells[i] = 0;"));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("WK0008", diagnostic.Id);
        Assert.Contains("'Cells'", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal("Cells[i]", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public async Task Reads_copies_replacements_and_non_reactive_arrays_are_not_reported()
    {
        var (diagnostics, _) = await AnalyzeAsync(
            UseSource(
                """
                var read = state.Cells[i] + state.Grid[0, i] + state.Cells.Length;
                read += Helpers.Peek(in state.Cells[i]);

                var copy = state.Cells;
                copy[0] = 1;
                Helpers.Increment(ref copy[0]);

                state.Cells = [1, 2];
                legacy.Cells = [];

                state.Plain[i] = 1;
                legacy.Untracked[i] = 1;
                holder.Cells[i]++;
                """));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Warnings_can_be_suppressed_with_pragma()
    {
        var (diagnostics, _) = await AnalyzeAsync(
            UseSource(
                """
                #pragma warning disable WK0008
                        state.Cells[i] = 1;
                        Helpers.Increment(ref legacy.Cells[i]);
                #pragma warning restore WK0008
                """));

        Assert.Empty(diagnostics);
    }

    private static string UseSource(string statements, string boardMember = "")
        => $$"""
            using Wick.Core;

            namespace App
            {
                {{Types}}

                public partial class Board
                {
                    {{boardMember}}
                }

                public static class Consumer
                {
                    public static void Use(Board state, Legacy legacy, Holder holder, int i)
                    {
                        {{statements}}
                    }
                }
            }
            """;

    private static async Task<(ImmutableArray<Diagnostic> Diagnostics, string Source)> AnalyzeAsync(string source)
    {
        var compilation = GeneratorRunner.CreateCompilation(source);
        GeneratorRunner.CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var diagnostics = await output
            .WithAnalyzers([new ReactiveArrayWriteAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        return (diagnostics, source);
    }
}
