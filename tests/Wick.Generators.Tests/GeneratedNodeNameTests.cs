namespace Wick.Generators.Tests;

public sealed class GeneratedNodeNameTests
{
    [Fact]
    public void Getters_pass_the_type_and_property_name_as_a_constant()
    {
        var generated = Generate(
            """
            using Wick.Core;

            namespace App;

            [ReactiveState]
            public partial class CounterState
            {
                public partial int Count { get; set; }

                public partial string Name { get; init; } = "";
            }
            """);

        Assert.Contains("ReactiveProperty.Get(ref field, ref __reactiveNode_Count, \"CounterState.Count\");", generated);
        Assert.Contains("ReactiveProperty.Get(ref field, ref __reactiveNode_Name, \"CounterState.Name\");", generated);
    }

    [Fact]
    public void Generic_and_escaped_names_are_emitted_as_valid_literals()
    {
        var generated = Generate(
            """
            using Wick.Core;

            [ReactiveState]
            public partial class Box<T>
            {
                public partial T Value { get; set; } = default!;

                public partial int @event { get; set; }
            }
            """);

        Assert.Contains("\"Box<T>.Value\"", generated);
        Assert.Contains("\"Box<T>.event\"", generated);
    }

    private static string Generate(string source)
    {
        var run = GeneratorRunner.CreateDriver().RunGenerators(GeneratorRunner.CreateCompilation(source)).GetRunResult();

        return string.Concat(run.GeneratedTrees.Select(tree => tree.ToString()));
    }
}
