namespace Wick.Generators.Tests;

public sealed class MemoGeneratorTests
{
    public static TheoryData<string, string> SupportedShapes => new()
    {
        {
            "both forms",
            """
            using Wick.Core;

            [ReactiveState]
            public partial class CartState
            {
                public partial decimal Price { get; set; }

                public partial int Quantity { get; set; }

                public decimal Total => Memo(() => Price * Quantity);

                public decimal TotalFast => Memo(static (CartState state) => state.Price * state.Quantity);

                public decimal Uncached => Price * Quantity;

                public bool Big
                {
                    get
                    {
                        return Memo(() => Total > 100);
                    }
                }
            }
            """
        },
        {
            "generic, nested and TValue type parameter",
            """
            using Wick.Core;

            public partial class Outer<TValue>
            {
                [ReactiveState]
                public partial class Box<T>
                {
                    public partial T Value { get; set; } = default!;

                    public partial TValue Other { get; set; } = default!;

                    public string Text => Memo(static (Box<T> box) => $"{box.Value}/{box.Other}");
                }
            }
            """
        },
        {
            "reactive base and derived types",
            """
            using Wick.Core;

            [ReactiveState]
            public partial class BaseState
            {
                public partial int A { get; set; }

                public int Twice => Memo(() => A * 2);
            }

            [ReactiveState]
            public sealed partial class DerivedState : BaseState
            {
                public partial int B { get; set; }

                public int Sum => Memo(static (DerivedState state) => state.Twice + state.B);
            }
            """
        },
        {
            "no properties, primary constructor and nested lambdas",
            """
            using System.Linq;
            using Wick.Core;

            [ReactiveState]
            public partial class Totals(ReactiveList<int> items)
            {
                public int Sum => Memo(() => items.Sum());

                public int Scaled => Memo(() => items.Select(item => { var factor = 2; return item * factor; }).Sum(item => item));
            }
            """
        },
        {
            "ReactiveObject",
            """
            using Wick.Core;

            public class RuntimeCart : ReactiveObject
            {
                public decimal Price { get => Get(ref field); set => Set(ref field, value); }

                public decimal Total => Memo(() => Price * 2);

                public decimal TotalFast => Memo(static (RuntimeCart cart) => cart.Price * 2);
            }
            """
        },
        {
            "existing Reactive.Derived",
            """
            using Wick.Core;

            [ReactiveState]
            public partial class CartState
            {
                private readonly Derived<decimal> _total;

                public CartState() => _total = Reactive.Derived(() => Price * 2);

                public partial decimal Price { get; set; }

                public decimal Total => _total.Value;
            }
            """
        },
    };

    public static TheoryData<string, string, string> Misuses => new()
    {
        { "WK0017", "public int Compute() => Memo(() => A);", "'State.Compute()'" },
        { "WK0017", "public int this[int index] => Memo(() => A);", "an indexer" },
        { "WK0017", "public System.Func<int> Later => () => Memo(() => A);", "a lambda or local function" },
        { "WK0017", "public State() { _ = Memo(() => A); }", "'State.State()'" },
        { "WK0018", "public int Scaled { get { var factor = A; return Memo(() => B * factor); } }", "'factor'" },
    };

    [Theory]
    [MemberData(nameof(SupportedShapes))]
    public void Supported_usages_compile_without_diagnostics(string shape, string source)
    {
        var run = GeneratorRunner.Run(source);

        Assert.True(run.Reported.IsEmpty && run.Compilation.IsEmpty, $"{shape}:{Environment.NewLine}{run.Describe()}");
    }

    [Theory]
    [MemberData(nameof(Misuses))]
    public void Misuses_are_reported(string id, string member, string detail)
    {
        var run = GeneratorRunner.Run(
            $$"""
            using Wick.Core;

            [ReactiveState]
            public partial class State
            {
                public partial int A { get; set; }

                public partial int B { get; set; }

                {{member}}
            }
            """);

        var diagnostic = Assert.Single(run.Reported);
        Assert.Equal(id, diagnostic.Id);
        Assert.Contains(detail, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.True(run.Compilation.All(candidate => candidate.Id == id), run.Describe());
    }

    [Theory]
    [InlineData("public int Memo => 1;", "State.Memo")]
    [InlineData("public int Memo(int value) => value;", "State.Memo(int)")]
    public void A_member_named_Memo_suppresses_the_helpers_without_breaking_the_build(string member, string conflict)
    {
        var run = GeneratorRunner.Run(
            $$"""
            using Wick.Core;

            [ReactiveState]
            public partial class State
            {
                public partial int A { get; set; }

                {{member}}
            }
            """);

        var diagnostic = Assert.Single(run.Reported);
        Assert.Equal("WK0016", diagnostic.Id);
        Assert.Contains($"'{conflict}'", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.True(run.Compilation.IsEmpty, run.Describe());
    }

    [Fact]
    public void An_inherited_Memo_suppresses_the_helpers()
    {
        var run = GeneratorRunner.Run(
            """
            using Wick.Core;

            public class Base
            {
                protected int Memo(System.Func<int> compute) => compute();
            }

            [ReactiveState]
            public partial class State : Base
            {
                public partial int A { get; set; }

                public int Twice => Memo(() => A * 2);
            }
            """);

        Assert.Equal("WK0016", Assert.Single(run.Reported).Id);
        Assert.True(run.Compilation.IsEmpty, run.Describe());
    }

    [Fact]
    public void Warnings_can_be_suppressed_with_pragma()
    {
        var run = GeneratorRunner.Run(
            """
            using Wick.Core;

            [ReactiveState]
            public partial class State
            {
                public partial int A { get; set; }

            #pragma warning disable WK0017
                public int Compute() => Memo(() => A);
            #pragma warning restore WK0017
            }
            """);

        Assert.True(run.Reported.All(diagnostic => diagnostic.IsSuppressed), run.Describe());
        Assert.True(run.Compilation.IsEmpty, run.Describe());
    }
}
