using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wick.Generators.Tests;

public sealed class MigrationAnalyzerTests
{
    private const string Component =
        """
        using System.ComponentModel;
        using System.Threading.Tasks;
        using Wick.Blazor;

        namespace App;

        public class View : ReactiveComponentCore
        {
            public Settings Settings { get; set; } = new();

        {0}
        }
        """;

    public static TheoryData<string, string> RedundantSubscriptions => new()
    {
        {
            "method group",
            """
                protected override void OnInitialized() => Settings.PropertyChanged += OnSettingsChanged;

                private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => StateHasChanged();
            """
        },
        {
            "lambda through InvokeAsync",
            """
                protected override void OnInitialized() => Settings.PropertyChanged += (_, _) => _ = InvokeAsync(StateHasChanged);
            """
        },
        {
            "filter on the property name",
            """
                protected override void OnInitialized() => Settings.PropertyChanged += OnSettingsChanged;

                private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(Settings.Language) || e.PropertyName is "Dark")
                    {
                        _ = InvokeAsync(StateHasChanged);
                    }
                }
            """
        },
        {
            "awaited InvokeAsync",
            """
                protected override void OnInitialized() => Settings.PropertyChanged += OnSettingsChanged;

                private async void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => await InvokeAsync(() => StateHasChanged());
            """
        },
    };

    public static TheoryData<string, string> NeededSubscriptions => new()
    {
        {
            "handler does more than re-render",
            """
                private int _changes;

                protected override void OnInitialized() => Settings.PropertyChanged += OnSettingsChanged;

                private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
                {
                    _changes++;
                    StateHasChanged();
                }
            """
        },
        {
            "condition reads other state",
            """
                private bool _visible;

                protected override void OnInitialized() => Settings.PropertyChanged += (_, _) =>
                {
                    if (_visible)
                    {
                        StateHasChanged();
                    }
                };
            """
        },
        {
            "handler declared elsewhere",
            """
                protected override void OnInitialized() => Settings.PropertyChanged += Handlers.Render;
            """
        },
    };

    [Theory]
    [MemberData(nameof(RedundantSubscriptions))]
    public void Subscriptions_that_only_re_render_a_reactive_component_are_reported(string shape, string members)
    {
        var reported = Analyze(Component.Replace("{0}", members), MigrationStubs.Components, MigrationStubs.NotifyingState);

        Assert.True(reported.Count(diagnostic => diagnostic.Id == "WK0011") == 1, $"{shape}: {string.Join(", ", reported)}");
    }

    [Theory]
    [MemberData(nameof(NeededSubscriptions))]
    public void Subscriptions_that_do_more_are_not_reported(string shape, string members)
    {
        const string handlers =
            """
            namespace App;

            public static class Handlers
            {
                public static void Render(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { }
            }
            """;

        var reported = Analyze(Component.Replace("{0}", members), MigrationStubs.Components, MigrationStubs.NotifyingState, handlers);

        Assert.True(!reported.Any(diagnostic => diagnostic.Id == "WK0011"), shape);
    }

    [Fact]
    public void Subscriptions_in_a_plain_component_are_not_reported()
    {
        var source = Component
            .Replace("ReactiveComponentCore", "Microsoft.AspNetCore.Components.ComponentBase")
            .Replace("{0}", "    protected override void OnInitialized() => Settings.PropertyChanged += (_, _) => StateHasChanged();");

        Assert.DoesNotContain("WK0011", Analyze(source, MigrationStubs.Components, MigrationStubs.NotifyingState).Select(diagnostic => diagnostic.Id));
    }

    [Fact]
    public void Events_raised_by_hand_written_code_are_not_reported()
    {
        // Either PropertyChanged is not Wick's, or hand-written code raises it for state Wick does not track.
        const string state =
            """
            using System.ComponentModel;
            using Wick.Core;

            namespace App
            {
                public class Settings : INotifyPropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;

                    public void Touch() => PropertyChanged?.Invoke(this, new(null));
                }

                [ReactiveState(NotifyPropertyChanged = true)]
                public partial class Session
                {
                    private int _untracked;

                    public partial string? User { get; set; }

                    public void Bump()
                    {
                        _untracked++;
                        OnPropertyChanged("Untracked");
                    }
                }
            }
            """;

        var members =
            """
                public Session Session { get; set; } = new();

                protected override void OnInitialized()
                {
                    Settings.PropertyChanged += (_, _) => StateHasChanged();
                    Session.PropertyChanged += (_, _) => StateHasChanged();
                }
            """;

        Assert.DoesNotContain("WK0011", Analyze(Component.Replace("{0}", members), MigrationStubs.Components, state).Select(diagnostic => diagnostic.Id));
    }

    [Fact]
    public void ObservableObject_classes_are_reported_once_per_class()
    {
        const string source =
            """
            using CommunityToolkit.Mvvm.ComponentModel;

            namespace App;

            public partial class Toast : ObservableObject
            {
                [ObservableProperty]
                private string _message = "";

                [ObservableProperty]
                private int _mode;
            }

            public partial class Plain : ObservableObject
            {
                public int Count { get; set; }
            }
            """;

        var reported = Analyze(source, MigrationStubs.Toolkit).Where(diagnostic => diagnostic.Id == "WK0012").ToList();

        var diagnostic = Assert.Single(reported);
        Assert.Contains("Toast", diagnostic.GetMessage());
    }

    [Fact]
    public void StateHasChanged_in_a_reactive_component_is_reported_when_enabled()
    {
        var members =
            """
                public void Refresh()
                {
                    StateHasChanged();
                    _ = InvokeAsync(StateHasChanged);
                }
            """;
        var source = Component.Replace("{0}", members);
        var plain = source.Replace("ReactiveComponentCore", "Microsoft.AspNetCore.Components.ComponentBase");

        Assert.Equal(2, Analyze(source, MigrationStubs.Components, MigrationStubs.NotifyingState).Count(diagnostic => diagnostic.Id == "WK0014"));
        Assert.DoesNotContain("WK0014", Analyze(plain, MigrationStubs.Components, MigrationStubs.NotifyingState).Select(diagnostic => diagnostic.Id));
        Assert.DoesNotContain("WK0014", Analyze(source, enableStateHasChanged: false, MigrationStubs.Components, MigrationStubs.NotifyingState).Select(diagnostic => diagnostic.Id));
    }

    [Fact]
    public void Partial_properties_another_generator_implements_are_left_to_it()
    {
        const string source =
            """
            using CommunityToolkit.Mvvm.ComponentModel;
            using Wick.Core;

            [ReactiveState]
            public partial class State
            {
                [ObservableProperty]
                public partial int Legacy { get; set; }

                public partial int Count { get; set; }
            }
            """;

        var compilation = GeneratorRunner.CreateCompilation(source)
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(MigrationStubs.Toolkit, new CSharpParseOptions(LanguageVersion.CSharp14), path: "Toolkit.cs"));
        var driver = CSharpGeneratorDriver.Create(
            [new ReactiveStateGenerator().AsSourceGenerator(), new ToolkitGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp14));

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        var analyzerDiagnostics = output.WithAnalyzers([new ReactiveStateAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();

        var errors = output.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Empty(errors);
        Assert.Empty(generatorDiagnostics);

        var warning = Assert.Single(analyzerDiagnostics);
        Assert.Equal("WK0013", warning.Id);
        Assert.Contains("'Legacy'", warning.GetMessage());
        Assert.Contains("[ObservableProperty]", warning.GetMessage());
    }

    private static ImmutableArray<Diagnostic> Analyze(string source, params string[] otherSources) => Analyze(source, true, otherSources);

    /// <summary>The migration analyzers' diagnostics; the sources must compile.</summary>
    private static ImmutableArray<Diagnostic> Analyze(string source, bool enableStateHasChanged, params string[] otherSources)
    {
        var compilation = GeneratorRunner.CreateCompilation(source)
            .AddSyntaxTrees(otherSources.Select((other, index) => CSharpSyntaxTree.ParseText(other, new CSharpParseOptions(LanguageVersion.CSharp14), path: $"Other{index}.cs")));
        if (enableStateHasChanged)
        {
            compilation = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions([new("WK0014", ReportDiagnostic.Info)]));
        }

        GeneratorRunner.CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var errors = output.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));

        return output
            .WithAnalyzers([new PropertyChangedSubscriptionAnalyzer(), new ObservablePropertyAnalyzer(), new StateHasChangedAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>Implements <c>[ObservableProperty]</c> partial properties the way CommunityToolkit.Mvvm 8.4 does.</summary>
    private sealed class ToolkitGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var properties = context.SyntaxProvider.ForAttributeWithMetadataName(
                "CommunityToolkit.Mvvm.ComponentModel.ObservablePropertyAttribute",
                static (node, _) => node is PropertyDeclarationSyntax,
                static (target, _) => (target.TargetSymbol.ContainingType.Name, target.TargetSymbol.Name, ((IPropertySymbol)target.TargetSymbol).Type.ToDisplayString()));

            context.RegisterSourceOutput(properties, static (output, property) => output.AddSource(
                $"{property.Item1}.{property.Item2}.g.cs",
                $$"""
                partial class {{property.Item1}}
                {
                    public partial {{property.Item3}} {{property.Item2}} { get => field; set => field = value; }
                }
                """));
        }
    }
}
