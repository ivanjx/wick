using Wick.CodeFixes;

namespace Wick.Generators.Tests;

public sealed class CodeFixTests
{
    // Stand-in for the Blazor adapter, with the same disposal hooks as ReactiveComponentCore.
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

    [Fact]
    public async Task Making_a_nested_type_partial_also_fixes_its_containing_types()
    {
        var fixedSource = await CodeFixRunner.FixAsync(
            new MakeTypePartialCodeFixProvider(),
            "WK0001",
            """
            using Wick.Core;

            namespace App;

            // Holds the state.
            class Outer
            {
                internal sealed class Middle
                {
                    [ReactiveState]
                    public class Counter
                    {
                        public partial int Count { get; set; }
                    }
                }

                public partial class Untouched { }
            }
            """,
            at: "Counter");

        Assert.Equal(
            """
            using Wick.Core;

            namespace App;

            // Holds the state.
            partial class Outer
            {
                internal sealed partial class Middle
                {
                    [ReactiveState]
                    public partial class Counter
                    {
                        public partial int Count { get; set; }
                    }
                }

                public partial class Untouched { }
            }
            """,
            fixedSource);
    }

    [Theory]
    [InlineData("public string Theme { get; set; } = \"dark\";", "public partial string Theme { get; set; } = \"dark\";")]
    [InlineData("int Volume { get; set; }", "partial int Volume { get; set; }")]
    public async Task Making_an_auto_property_reactive_declares_it_partial(string property, string expected)
    {
        const string Template =
            """
            using Wick.Core;

            [ReactiveState]
            public partial class Settings
            {
                /// <summary>A setting.</summary>
                {0}
            }
            """;

        var fixedSource = await CodeFixRunner.FixAsync(new MakePropertyReactiveCodeFixProvider(), "WK0002", Template.Replace("{0}", property));

        Assert.Equal(Template.Replace("{0}", expected), fixedSource);
    }

    [Fact]
    public async Task Using_a_reactive_collection_imports_Wick_Core_and_makes_the_property_get_only()
    {
        var fixedSource = await CodeFixRunner.FixAsync(
            new UseReactiveCollectionCodeFixProvider(),
            "WK0006",
            """
            using System.Collections.Generic;

            namespace App;

            [Wick.Core.ReactiveState]
            public partial class TodoState
            {
                public partial List<string> Items { get; set; } = [];
            }
            """);

        Assert.Equal(
            """
            using System.Collections.Generic;
            using Wick.Core;

            namespace App;

            [Wick.Core.ReactiveState]
            public partial class TodoState
            {
                public ReactiveList<string> Items { get; } = [];
            }
            """,
            fixedSource);
    }

    public static TheoryData<string, string> CollectionProperties => new()
    {
        { "public partial IList<int> Items { get; set; } = new List<int> { 1, 2 };", "public ReactiveList<int> Items { get; } = new ReactiveList<int> { 1, 2 };" },
        { "public partial List<int> Numbers { get; private set; } = new List<int>(16);", "public ReactiveList<int> Numbers { get; } = new ReactiveList<int>();" },
        { "public partial List<string> Names { get; set; } = null!;", "public ReactiveList<string> Names { get; } = [];" },
        { "public partial List<int> Loaded { get; set; } = Load();", "public ReactiveList<int> Loaded { get; } = new ReactiveList<int>(Load());" },
        { "public partial Dictionary<string, decimal> Prices { get; set; } = new();", "public ReactiveDictionary<string, decimal> Prices { get; } = new();" },
        { "public partial Dictionary<string, int> Counts { get; set; } = [];", "public ReactiveDictionary<string, int> Counts { get; } = new();" },
        { "public partial Dictionary<string, int> Map { get; set; } = new Dictionary<string, int>(4, StringComparer.Ordinal);", "public ReactiveDictionary<string, int> Map { get; } = new ReactiveDictionary<string, int>(StringComparer.Ordinal);" },
        { "public partial HashSet<string> Tags { get; set; } = new(StringComparer.Ordinal) { \"a\" };", "public ReactiveSet<string> Tags { get; } = new(StringComparer.Ordinal) { \"a\" };" },
        { "public required partial ISet<int>? Ids { get; set; }", "public ReactiveSet<int> Ids { get; } = [];" },
    };

    [Theory]
    [MemberData(nameof(CollectionProperties))]
    public async Task Using_a_reactive_collection_rewrites_the_type_and_initializer(string property, string expected)
    {
        const string Template =
            """
            using System;
            using System.Collections.Generic;
            using Wick.Core;

            [ReactiveState]
            public partial class State
            {
                {0}

                private static List<int> Load() => [3];
            }
            """;

        var fixedSource = await CodeFixRunner.FixAsync(new UseReactiveCollectionCodeFixProvider(), "WK0006", Template.Replace("{0}", property));

        Assert.Equal(Template.Replace("{0}", expected), fixedSource);
    }

    [Fact]
    public async Task Fixing_all_collection_properties_imports_Wick_Core_once()
    {
        var fixedSource = await CodeFixRunner.FixAllAsync(
            new UseReactiveCollectionCodeFixProvider(),
            "WK0006",
            """
            using System.Collections.Generic;

            [Wick.Core.ReactiveState]
            public partial class State
            {
                public partial List<int> Items { get; set; } = [];

                public partial HashSet<int> Ids { get; set; } = [];
            }
            """);

        Assert.Equal(
            """
            using System.Collections.Generic;
            using Wick.Core;

            [Wick.Core.ReactiveState]
            public partial class State
            {
                public ReactiveList<int> Items { get; } = [];

                public ReactiveSet<int> Ids { get; } = [];
            }
            """,
            fixedSource);
    }

    [Fact]
    public async Task Collections_without_a_reactive_counterpart_get_no_fix()
    {
        var actions = await CodeFixRunner.GetActionsAsync(
            new UseReactiveCollectionCodeFixProvider(),
            "WK0006",
            """
            using System.Collections.ObjectModel;
            using Wick.Core;

            [ReactiveState]
            public partial class State
            {
                public partial ObservableCollection<int> Items { get; set; } = [];
            }
            """);

        Assert.Empty(actions);
    }

    public static TheoryData<string, string> DisposalMembers => new()
    {
        {
            """
                public new void Dispose()
                {
                    _stream.Dispose();
                }
            """,
            """
                protected override void Dispose(bool disposing)
                {
                    _stream.Dispose();
                    base.Dispose(disposing);
                }
            """
        },
        {
            """
                public new void Dispose() { }
            """,
            """
                protected override void Dispose(bool disposing) { base.Dispose(disposing); }
            """
        },
        {
            """
                /// <summary>Releases the stream.</summary>
                public new void Dispose() => _stream.Dispose();
            """,
            """
                /// <summary>Releases the stream.</summary>
                protected override void Dispose(bool disposing)
                {
                    _stream.Dispose();
                    base.Dispose(disposing);
                }
            """
        },
        {
            """
                public new ValueTask DisposeAsync() => _stream.DisposeAsync();
            """,
            """
                protected override async ValueTask DisposeAsyncCore()
                {
                    await _stream.DisposeAsync();
                    await base.DisposeAsyncCore();
                }
            """
        },
        {
            """
                public new async ValueTask DisposeAsync()
                {
                    await _stream.FlushAsync();
                    await _stream.DisposeAsync();
                }
            """,
            """
                protected override async ValueTask DisposeAsyncCore()
                {
                    await _stream.FlushAsync();
                    await _stream.DisposeAsync();
                    await base.DisposeAsyncCore();
                }
            """
        },
        {
            """
                public new ValueTask DisposeAsync()
                {
                    _stream.Flush();
                    return _stream.DisposeAsync();
                }
            """,
            """
                protected override async ValueTask DisposeAsyncCore()
                {
                    _stream.Flush();
                    await _stream.DisposeAsync();
                    await base.DisposeAsyncCore();
                }
            """
        },
        {
            """
                public new ValueTask DisposeAsync() => ValueTask.CompletedTask;
            """,
            """
                protected override ValueTask DisposeAsyncCore() => base.DisposeAsyncCore();
            """
        },
    };

    [Theory]
    [MemberData(nameof(DisposalMembers))]
    public async Task Own_disposal_becomes_an_override_that_calls_the_base(string member, string expected)
    {
        const string Template =
            """
            using System.IO;
            using System.Threading.Tasks;
            using Wick.Blazor;

            public class View : ReactiveComponentCore
            {
                private readonly MemoryStream _stream = new();

            {0}
            }
            """;

        var fixedSource = await CodeFixRunner.FixAsync(
            new OverrideDisposalCodeFixProvider(),
            "WK0007",
            Template.Replace("{0}", member),
            otherSources: [ComponentCore]);

        Assert.Equal(Template.Replace("{0}", expected), fixedSource);
    }

    [Fact]
    public async Task Overriding_Dispose_removes_IDisposable_from_the_base_list()
    {
        var fixedSource = await CodeFixRunner.FixAsync(
            new OverrideDisposalCodeFixProvider(),
            "WK0007",
            """
            using System;
            using System.IO;
            using Wick.Blazor;

            public class View : ReactiveComponentCore, IDisposable, IAsyncDisposable
            {
                private readonly MemoryStream _stream = new();

                public new void Dispose() => _stream.Dispose();
            }
            """,
            otherSources: [ComponentCore]);

        Assert.Equal(
            """
            using System;
            using System.IO;
            using Wick.Blazor;

            public class View : ReactiveComponentCore, IAsyncDisposable
            {
                private readonly MemoryStream _stream = new();

                protected override void Dispose(bool disposing)
                {
                    _stream.Dispose();
                    base.Dispose(disposing);
                }
            }
            """,
            fixedSource);
    }

    [Fact]
    public async Task Overriding_an_explicit_implementation_in_a_code_behind_removes_the_base_list()
    {
        var fixedSource = await CodeFixRunner.FixAsync(
            new OverrideDisposalCodeFixProvider(),
            "WK0007",
            """
            using System;
            using System.Threading.Tasks;

            namespace App;

            public partial class Counter : IAsyncDisposable
            {
                ValueTask IAsyncDisposable.DisposeAsync() => default;
            }
            """,
            otherSources:
            [
                ComponentCore,
                // The part a .razor file compiles to.
                "namespace App { public partial class Counter : Wick.Blazor.ReactiveComponentCore { } }",
            ]);

        Assert.Equal(
            """
            using System;
            using System.Threading.Tasks;

            namespace App;

            public partial class Counter
            {
                protected override ValueTask DisposeAsyncCore() => base.DisposeAsyncCore();
            }
            """,
            fixedSource);
    }

    public static TheoryData<string, string> UnfixableDisposals => new()
    {
        { "declared in a .razor file", "#line 12 \"View.razor\"\n    public new void Dispose() { }\n#line default" },
        { "early return", "public new void Dispose() { if (_disposed) { return; } _disposed = true; }" },
        { "Dispose(bool) already overridden", "public new void Dispose() { }\n    protected override void Dispose(bool disposing) { }" },
    };

    [Theory]
    [MemberData(nameof(UnfixableDisposals))]
    public async Task Disposal_that_cannot_be_rewritten_gets_no_fix(string shape, string member)
    {
        var actions = await CodeFixRunner.GetActionsAsync(
            new OverrideDisposalCodeFixProvider(),
            "WK0007",
            $$"""
            using Wick.Blazor;

            public class View : ReactiveComponentCore
            {
                private bool _disposed;

                {{member}}
            }
            """,
            ComponentCore);

        Assert.True(actions.IsEmpty, shape);
    }
}
