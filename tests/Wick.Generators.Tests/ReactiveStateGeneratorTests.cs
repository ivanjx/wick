using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Wick.Generators.Tests;

public sealed class ReactiveStateGeneratorTests
{
    public static TheoryData<string, string> SupportedShapes => new()
    {
        {
            "nested in partial class and record",
            """
            using Wick.Core;

            namespace App;

            public partial class Outer
            {
                public partial record Inner
                {
                    [ReactiveState]
                    public partial class State
                    {
                        public partial int Count { get; set; }
                    }
                }
            }
            """
        },
        {
            "generic with constraints",
            """
            using System.Collections.Generic;
            using Wick.Core;

            [ReactiveState]
            public partial class Box<T> where T : notnull
            {
                public partial T Value { get; set; } = default!;

                public partial IReadOnlyList<T>? Items { get; set; }
            }
            """
        },
        {
            "modifiers, accessors and non-reactive members",
            """
            using Wick.Core;

            namespace App;

            [ReactiveState]
            public partial class State
            {
                public partial int Count { get; private set; }

                public partial string Name { get; init; } = "";

                public required partial string Id { get; set; }

                public partial string? Note { get; set; }

                public partial int @event { get; set; }

                public partial (int X, int Y) Point { get; set; }

                public partial bool Flag { protected get; set; }

                public int Doubled => Count * 2;

                public string Fixed { get; } = "fixed";

                public string Created { get; init; } = "";

                public void Increment() => Count++;
            }
            """
        },
        {
            "inheritance with virtual and override properties",
            """
            using Wick.Core;

            [ReactiveState]
            public partial class Base
            {
                public partial int A { get; set; }

                public virtual partial int V { get; set; }
            }

            [ReactiveState]
            public sealed partial class Derived : Base
            {
                public partial int B { get; set; }

                public override partial int V { get; set; }
            }
            """
        },
        {
            "nullable disabled",
            """
            #nullable disable
            using Wick.Core;

            [ReactiveState]
            public partial class Legacy
            {
                public partial string Name { get; set; }
            }
            """
        },
        {
            "hand-implemented partial property is left alone",
            """
            using Wick.Core;

            [ReactiveState]
            public partial class Manual
            {
                public partial int Generated { get; set; }

                public partial int Count { get; set; }

                public partial int Count { get => 1; set { } }
            }
            """
        },
        {
            "reactive equality",
            """
            using System;
            using System.Collections.Generic;
            using Wick.Core;

            [ReactiveState]
            public partial class Document<T>
                where T : class
            {
                [ReactiveEquality(ReactiveEqualityMode.Reference)]
                public partial string Content { get; set; } = "";

                [ReactiveEquality(ReactiveEqualityMode.Reference)]
                public partial T? Item { get; set; }

                [ReactiveEquality(ReactiveEqualityMode.Never)]
                public partial int Pulse { get; set; }

                [ReactiveEquality(ReactiveEqualityMode.Default)]
                public partial int Count { get; set; }

                [ReactiveEquality(typeof(IgnoreCase))]
                public partial string? Search { get; set; }

                [ReactiveEquality(typeof(Rounded))]
                public partial double Zoom { get; set; }
            }

            internal sealed class IgnoreCase : IEqualityComparer<string?>
            {
                public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

                public int GetHashCode(string? obj) => obj is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj);
            }

            public struct Rounded : IEqualityComparer<double>
            {
                public bool Equals(double x, double y) => Math.Round(x, 2) == Math.Round(y, 2);

                public int GetHashCode(double obj) => Math.Round(obj, 2).GetHashCode();
            }
            """
        },
    };

    public static TheoryData<string, string> InvalidDeclarations => new()
    {
        { "WK0001", "[ReactiveState] public class State { }" },
        { "WK0001", "public class Outer { [ReactiveState] public partial class State { public partial int A { get; set; } } }" },
        { "WK0002", "[ReactiveState] public partial class State { public int A { get; set; } }" },
        { "WK0002", "[ReactiveState] public partial class State { public int A { get; private set; } }" },
        { "WK0003", "[ReactiveState] public partial class State { public static partial int A { get; set; } }" },
        { "WK0003", "[ReactiveState] public partial class State { public partial int A { get; } }" },
        { "WK0004", "[ReactiveState] public partial record State { public partial int A { get; set; } }" },
        { "WK0004", "[ReactiveState] public partial class State : ReactiveObject { public partial int A { get; set; } }" },
        { "WK0004", "[ReactiveState] public static partial class State { }" },
        { "WK0009", "[ReactiveState] public partial class State { [ReactiveEquality(ReactiveEqualityMode.Reference)] public partial int A { get; set; } }" },
        { "WK0009", "[ReactiveState] public partial class State { [ReactiveEquality(typeof(System.StringComparer))] public partial string A { get; set; } = \"\"; }" },
        { "WK0009", "[ReactiveState] public partial class State { [ReactiveEquality(typeof(System.Collections.Generic.ReferenceEqualityComparer))] public partial string A { get; set; } = \"\"; }" },
        { "WK0009", "[ReactiveState] public partial class State { [ReactiveEquality(typeof(System.Collections.Generic.EqualityComparer<string>))] public partial int A { get; set; } }" },
        { "WK0009", "[ReactiveState] public partial class State { [ReactiveEquality((ReactiveEqualityMode)7)] public partial int A { get; set; } }" },
        { "WK0010", "public class State { [ReactiveEquality(ReactiveEqualityMode.Never)] public int A { get; set; } }" },
        { "WK0010", "[ReactiveState] public partial class State { [ReactiveEquality(ReactiveEqualityMode.Never)] public partial int A { get; init; } }" },
        { "WK0010", "[ReactiveState] public partial class State { [ReactiveEquality(ReactiveEqualityMode.Never)] public int A { get; set; } }" },
    };

    [Theory]
    [MemberData(nameof(SupportedShapes))]
    public void Supported_declarations_compile_without_diagnostics(string shape, string source)
    {
        var run = GeneratorRunner.Run(source);

        Assert.True(run.Reported.IsEmpty && run.Compilation.IsEmpty, $"{shape}:{Environment.NewLine}{run.Describe()}");
        Assert.True(run.GeneratedSources > 0, shape);
    }

    [Theory]
    [MemberData(nameof(InvalidDeclarations))]
    public void Invalid_declarations_report_a_diagnostic(string id, string body)
    {
        var run = GeneratorRunner.Run($"using Wick.Core;{Environment.NewLine}{body}");

        Assert.Contains(id, run.ReportedIds);
    }

    [Fact]
    public void A_non_reactive_auto_property_warns_but_the_reactive_ones_are_still_generated()
    {
        var run = GeneratorRunner.Run(
            """
            using Wick.Core;

            [ReactiveState]
            public partial class State
            {
                public partial int Reactive { get; set; }

                public int Plain { get; set; }
            }
            """);

        Assert.Equal(["WK0002"], run.ReportedIds);
        Assert.Equal(DiagnosticSeverity.Warning, run.Reported.Single().Severity);
        Assert.Empty(run.Compilation);
    }

    [Fact]
    public void Properties_assigned_by_Blazor_do_not_warn_in_a_reactive_component()
    {
        var run = GeneratorRunner.Run(
            """
            using Microsoft.AspNetCore.Components;
            using Wick.Core;

            namespace Microsoft.AspNetCore.Components
            {
                public sealed class ParameterAttribute : System.Attribute { }

                public sealed class InjectAttribute : System.Attribute { }
            }

            [ReactiveState]
            public partial class Counter
            {
                [Parameter] public int Step { get; set; }

                [Inject] public System.IServiceProvider Services { get; set; } = null!;

                public partial int Count { get; set; }
            }
            """);

        Assert.True(run.Reported.IsEmpty && run.Compilation.IsEmpty, run.Describe());
    }

    [Theory]
    [InlineData("List<int>")]
    [InlineData("IList<int>")]
    [InlineData("ICollection<int>")]
    [InlineData("Dictionary<string, int>")]
    [InlineData("HashSet<int>")]
    [InlineData("ObservableCollection<int>")]
    public void Mutable_collection_properties_warn_but_are_still_generated(string type)
    {
        var run = GeneratorRunner.Run(MutableCollectionSource(type));

        Assert.Equal(["WK0006"], run.ReportedIds);
        Assert.Equal(DiagnosticSeverity.Warning, run.Reported.Single().Severity);
        Assert.Empty(run.Compilation);
    }

    [Theory]
    [InlineData("int[]")]
    [InlineData("IReadOnlyList<int>")]
    [InlineData("IEnumerable<int>")]
    [InlineData("ImmutableList<int>")]
    [InlineData("ImmutableArray<int>")]
    [InlineData("FrozenDictionary<string, int>")]
    [InlineData("ReadOnlyCollection<int>")]
    [InlineData("ReactiveList<int>")]
    [InlineData("ReactiveDictionary<string, int>")]
    [InlineData("string")]
    public void Immutable_read_only_and_reactive_collections_do_not_warn(string type)
    {
        var run = GeneratorRunner.Run(MutableCollectionSource(type));

        Assert.True(run.Reported.IsEmpty && run.Compilation.IsEmpty, run.Describe());
    }

    [Fact]
    public void Warnings_can_be_suppressed_with_pragma()
    {
        var run = GeneratorRunner.Run(
            """
            using System.Collections.Generic;
            using Wick.Core;

            [ReactiveState]
            public partial class State
            {
            #pragma warning disable WK0002, WK0006
                public int Plain { get; set; }

                public partial List<int> Items { get; set; } = [];
            #pragma warning restore WK0002, WK0006
            }
            """);

        Assert.True(run.Reported.IsEmpty && run.Compilation.IsEmpty, run.Describe());
    }

    private static string MutableCollectionSource(string type)
        => $$"""
            using System.Collections.Frozen;
            using System.Collections.Generic;
            using System.Collections.Immutable;
            using System.Collections.ObjectModel;
            using Wick.Core;

            [ReactiveState]
            public partial class State
            {
                public partial {{type}} Items { get; set; } = default!;
            }
            """;

    [Fact]
    public void Older_language_versions_are_rejected()
    {
        var run = GeneratorRunner.Run(
            """
            using Wick.Core;

            [ReactiveState]
            public partial class State
            {
                public partial int Count { get; set; }
            }
            """,
            LanguageVersion.CSharp13);

        Assert.Contains("WK0005", run.ReportedIds);
    }

    [Fact]
    public void Editing_a_reactive_type_without_changing_its_shape_reuses_the_generated_output()
    {
        const string source =
            """
            using Wick.Core;

            [ReactiveState]
            public partial class State
            {
                public partial int Count { get; set; }

                public void Increment() => Count++;
            }
            """;

        var compilation = GeneratorRunner.CreateCompilation(source);
        var driver = GeneratorRunner.CreateDriver().RunGenerators(compilation);

        var edited = CSharpSyntaxTree.ParseText(
            source.Replace("Count++", "Count += 2"),
            (CSharpParseOptions)compilation.SyntaxTrees[0].Options,
            path: compilation.SyntaxTrees[0].FilePath);
        var result = driver.RunGenerators(compilation.ReplaceSyntaxTree(compilation.SyntaxTrees[0], edited)).GetRunResult().Results.Single();

        var outputs = result.TrackedOutputSteps.SelectMany(step => step.Value).SelectMany(step => step.Outputs).ToArray();

        Assert.NotEmpty(outputs);
        Assert.All(outputs, output => Assert.Contains(output.Reason, new[] { IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged }));
    }
}
