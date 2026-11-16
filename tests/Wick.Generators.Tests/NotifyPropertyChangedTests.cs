using System.ComponentModel;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Wick.Generators.Tests;

/// <summary>Runs the setters <c>[ReactiveState(NotifyPropertyChanged = true)]</c> generates.</summary>
public sealed class NotifyPropertyChangedTests
{
    private const string Source =
        """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;
        using Wick.Core;

        [ReactiveState(NotifyPropertyChanged = true)]
        public partial class Settings
        {
            public partial string Language { get; set; } = "en";

            [ReactiveEquality(ReactiveEqualityMode.Reference)]
            public partial List<int> Items { get; set; } = [];

            [ReactiveEquality(ReactiveEqualityMode.Never)]
            public partial int Pulse { get; set; }

            [ReactiveEquality(typeof(IgnoreCase))]
            public partial string Search { get; set; } = "";

            public partial int Fixed { get; init; }
        }

        [ReactiveState]
        public partial class DerivedSettings : Settings
        {
            public partial bool Dark { get; set; }
        }

        [ReactiveState(NotifyPropertyChanged = true)]
        public partial class Existing : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;

            public List<string?> Raised { get; } = [];

            public partial int Count { get; set; }

            protected void OnPropertyChanged(string? propertyName = null)
            {
                Raised.Add(propertyName);
                PropertyChanged?.Invoke(this, new(propertyName));
            }
        }

        public sealed class IgnoreCase : IEqualityComparer<string>
        {
            public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj);
        }
        """;

    private static readonly Lazy<Assembly> Compiled = new(Compile);

    [Fact]
    public void Setters_raise_PropertyChanged_once_per_change()
    {
        var settings = Create("Settings");
        var raised = Listen(settings);

        Set(settings, "Language", "en");
        Set(settings, "Language", "de");
        Set(settings, "Language", "de");

        var items = new List<int>();
        Set(settings, "Items", items);
        Set(settings, "Items", items);

        Set(settings, "Pulse", 0);
        Set(settings, "Pulse", 0);

        Set(settings, "Search", "ABC");
        Set(settings, "Search", "abc");

        Assert.Equal(["Language", "Items", "Pulse", "Pulse", "Search"], raised);
        Assert.Equal("ABC", Get(settings, "Search"));
    }

    [Fact]
    public void Derived_state_raises_for_its_own_and_inherited_properties()
    {
        var settings = Create("DerivedSettings");
        var raised = Listen(settings);

        Set(settings, "Dark", true);
        Set(settings, "Language", "de");

        Assert.Equal(["Dark", "Language"], raised);
    }

    [Fact]
    public void State_with_its_own_INotifyPropertyChanged_raises_through_OnPropertyChanged()
    {
        var existing = Create("Existing");
        var raised = Listen(existing);

        Set(existing, "Count", 1);
        Set(existing, "Count", 1);

        Assert.Equal(["Count"], raised);
        Assert.Equal(raised, (List<string?>)Get(existing, "Raised")!);
    }

    private static Assembly Compile()
    {
        GeneratorRunner.CreateDriver().RunGeneratorsAndUpdateCompilation(GeneratorRunner.CreateCompilation(Source), out var output, out var diagnostics);
        Assert.Empty(diagnostics);

        using var stream = new MemoryStream();
        var result = output.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

        return Assembly.Load(stream.ToArray());
    }

    private static object Create(string type) => Activator.CreateInstance(Compiled.Value.GetType(type, throwOnError: true)!)!;

    private static List<string?> Listen(object state)
    {
        var raised = new List<string?>();
        ((INotifyPropertyChanged)state).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        return raised;
    }

    private static object? Get(object state, string property) => state.GetType().GetProperty(property)!.GetValue(state);

    private static void Set(object state, string property, object? value) => state.GetType().GetProperty(property)!.SetValue(state, value);
}
