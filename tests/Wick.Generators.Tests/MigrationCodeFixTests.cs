using Wick.CodeFixes;

namespace Wick.Generators.Tests;

public sealed class MigrationCodeFixTests
{
    private const string NotifyingTitle = "Convert to [ReactiveState(NotifyPropertyChanged = true)]";
    private const string PlainTitle = "Convert to [ReactiveState]";

    [Fact]
    public async Task Removing_a_subscription_removes_the_handler_and_the_unsubscription()
    {
        var fixedSource = await CodeFixRunner.FixAsync(
            new RemoveSubscriptionCodeFixProvider(),
            "WK0011",
            """
            using System.ComponentModel;
            using Wick.Blazor;

            namespace App;

            public class Layout : ReactiveComponentCore
            {
                public Settings Settings { get; set; } = new();

                protected override void OnInitialized()
                {
                    Settings.PropertyChanged += OnSettingsChanged;
                }

                private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(Settings.Language))
                    {
                        _ = InvokeAsync(StateHasChanged);
                    }
                }

                protected override void Dispose(bool disposing)
                {
                    this.Settings.PropertyChanged -= OnSettingsChanged;
                }
            }
            """,
            otherSources: [MigrationStubs.Components, MigrationStubs.NotifyingState]);

        Assert.Equal(
            """
            using System.ComponentModel;
            using Wick.Blazor;

            namespace App;

            public class Layout : ReactiveComponentCore
            {
                public Settings Settings { get; set; } = new();

                protected override void OnInitialized()
                {
                }

                protected override void Dispose(bool disposing)
                {
                }
            }
            """,
            fixedSource);
    }

    [Fact]
    public async Task Removing_a_subscription_keeps_the_surrounding_statements_tidy()
    {
        var fixedSource = await CodeFixRunner.FixAsync(
            new RemoveSubscriptionCodeFixProvider(),
            "WK0011",
            """
            using System.ComponentModel;
            using System.IO;
            using Wick.Blazor;

            namespace App;

            public class BottomBar : ReactiveComponentCore
            {
                private readonly MemoryStream _stream = new();

                public Settings Settings { get; set; } = new();

                public Session Session { get; set; } = new();

                protected override void OnInitialized()
                {
                    _stream.WriteByte(1);

                    Settings.PropertyChanged += OnSettingsChanged;
                    Session.PropertyChanged += (_, _) => _ = InvokeAsync(StateHasChanged);
                }

                private async void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => await InvokeAsync(StateHasChanged);

                protected override void Dispose(bool disposing)
                {
                    Settings.PropertyChanged -= OnSettingsChanged;

                    _stream.Dispose();
                }
            }
            """,
            at: "Settings.PropertyChanged += OnSettingsChanged",
            otherSources: [MigrationStubs.Components, MigrationStubs.NotifyingState]);

        Assert.Equal(
            """
            using System.ComponentModel;
            using System.IO;
            using Wick.Blazor;

            namespace App;

            public class BottomBar : ReactiveComponentCore
            {
                private readonly MemoryStream _stream = new();

                public Settings Settings { get; set; } = new();

                public Session Session { get; set; } = new();

                protected override void OnInitialized()
                {
                    _stream.WriteByte(1);

                    Session.PropertyChanged += (_, _) => _ = InvokeAsync(StateHasChanged);
                }

                protected override void Dispose(bool disposing)
                {
                    _stream.Dispose();
                }
            }
            """,
            fixedSource);
    }

    [Fact]
    public async Task A_handler_that_is_also_called_directly_is_kept()
    {
        var fixedSource = await CodeFixRunner.FixAsync(
            new RemoveSubscriptionCodeFixProvider(),
            "WK0011",
            """
            using System.ComponentModel;
            using Wick.Blazor;

            namespace App;

            public class Thumbnails : ReactiveComponentCore
            {
                public Settings Settings { get; set; } = new();

                protected override void OnInitialized()
                {
                    Settings.PropertyChanged += OnSettingsChanged;
                    OnSettingsChanged(null, new(null));
                }

                private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => StateHasChanged();
            }
            """,
            otherSources: [MigrationStubs.Components, MigrationStubs.NotifyingState]);

        Assert.Equal(
            """
            using System.ComponentModel;
            using Wick.Blazor;

            namespace App;

            public class Thumbnails : ReactiveComponentCore
            {
                public Settings Settings { get; set; } = new();

                protected override void OnInitialized()
                {
                    OnSettingsChanged(null, new(null));
                }

                private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => StateHasChanged();
            }
            """,
            fixedSource);
    }

    [Fact]
    public async Task Fixing_all_subscriptions_removes_a_shared_handler_once()
    {
        var fixedSource = await CodeFixRunner.FixAllAsync(
            new RemoveSubscriptionCodeFixProvider(),
            "WK0011",
            """
            using System.ComponentModel;
            using Wick.Blazor;

            namespace App;

            public class Header : ReactiveComponentCore
            {
                public Settings Settings { get; set; } = new();

                public Session Session { get; set; } = new();

                protected override void OnInitialized()
                {
                    Settings.PropertyChanged += OnChanged;
                    Session.PropertyChanged += OnChanged;
                }

                private void OnChanged(object? sender, PropertyChangedEventArgs e) => StateHasChanged();

                protected override void Dispose(bool disposing)
                {
                    Settings.PropertyChanged -= OnChanged;
                    Session.PropertyChanged -= OnChanged;
                }
            }
            """,
            MigrationStubs.Components,
            MigrationStubs.NotifyingState);

        Assert.Equal(
            """
            using System.ComponentModel;
            using Wick.Blazor;

            namespace App;

            public class Header : ReactiveComponentCore
            {
                public Settings Settings { get; set; } = new();

                public Session Session { get; set; } = new();

                protected override void OnInitialized()
                {
                }

                protected override void Dispose(bool disposing)
                {
                }
            }
            """,
            fixedSource);
    }

    [Fact]
    public async Task Subscriptions_mapped_to_a_razor_file_get_no_fix()
    {
        var actions = await CodeFixRunner.GetActionsAsync(
            new RemoveSubscriptionCodeFixProvider(),
            "WK0011",
            """
            using Wick.Blazor;

            namespace App;

            public class Counter : ReactiveComponentCore
            {
                public Settings Settings { get; set; } = new();

                protected override void OnInitialized()
                {
            #line 12 "Counter.razor"
                    Settings.PropertyChanged += (_, _) => StateHasChanged();
            #line default
                }
            }
            """,
            MigrationStubs.Components,
            MigrationStubs.NotifyingState);

        Assert.Empty(actions);
    }

    [Fact]
    public async Task ObservableObject_converts_to_notifying_reactive_state()
    {
        var fixedSource = await CodeFixRunner.FixAsync(
            new ConvertObservableObjectCodeFixProvider(),
            "WK0012",
            """
            using System.Text.Json.Serialization;
            using CommunityToolkit.Mvvm.ComponentModel;

            namespace App;

            /// <summary>A toast.</summary>
            public partial class Toast : ObservableObject
            {
                [ObservableProperty]
                private string _message = "";

                /// <summary>How it looks.</summary>
                [ObservableProperty]
                [property: JsonIgnore]
                private ToastMode _mode;

                [ObservableProperty] private bool m_visible, closing;

                public bool _Logout;

                public void Show(string message) => Message = message;
            }

            public enum ToastMode { Info, Error }
            """,
            title: NotifyingTitle,
            otherSources: [MigrationStubs.Toolkit]);

        Assert.Equal(
            """
            using System.Text.Json.Serialization;
            using Wick.Core;

            namespace App;

            /// <summary>A toast.</summary>
            [ReactiveState(NotifyPropertyChanged = true)]
            public partial class Toast
            {
                public partial string Message { get; set; } = "";

                /// <summary>How it looks.</summary>
                [JsonIgnore]
                public partial ToastMode Mode { get; set; }

                public partial bool Visible { get; set; }
                public partial bool Closing { get; set; }

                public bool _Logout;

                public void Show(string message) => Message = message;
            }

            public enum ToastMode { Info, Error }
            """,
            fixedSource);
    }

    [Fact]
    public async Task ObservableObject_converts_to_plain_reactive_state()
    {
        var fixedSource = await CodeFixRunner.FixAsync(
            new ConvertObservableObjectCodeFixProvider(),
            "WK0012",
            """
            using CommunityToolkit.Mvvm.ComponentModel;
            using Wick.Core;

            namespace App
            {
                [Serializable]
                internal class AuthState : ObservableObject, IDisposable
                {
                    [ObservableProperty]
                    private string? _currentUser;

                    [ObservableProperty]
                    public partial int Attempts { get; set; }

                    public void Dispose() { }
                }

                public partial class Other : ObservableObject
                {
                }
            }
            """,
            title: PlainTitle,
            otherSources: ["global using System;", MigrationStubs.Toolkit]);

        Assert.Equal(
            """
            using CommunityToolkit.Mvvm.ComponentModel;
            using Wick.Core;

            namespace App
            {
                [Serializable]
                [ReactiveState]
                internal partial class AuthState : IDisposable
                {
                    public partial string? CurrentUser { get; set; }

                    public partial int Attempts { get; set; }

                    public void Dispose() { }
                }

                public partial class Other : ObservableObject
                {
                }
            }
            """,
            fixedSource);
    }

    [Fact]
    public async Task ObservableObject_conversion_qualifies_the_attribute_when_another_one_is_in_scope()
    {
        var fixedSource = await CodeFixRunner.FixAsync(
            new ConvertObservableObjectCodeFixProvider(),
            "WK0012",
            """
            using CommunityToolkit.Mvvm.ComponentModel;

            namespace App;

            public partial class Counter : ObservableObject
            {
                [ObservableProperty]
                private int _count;
            }

            public sealed class ReactiveStateAttribute : System.Attribute { }
            """,
            title: PlainTitle,
            otherSources: [MigrationStubs.Toolkit]);

        Assert.Equal(
            """
            namespace App;

            [global::Wick.Core.ReactiveState]
            public partial class Counter
            {
                public partial int Count { get; set; }
            }

            public sealed class ReactiveStateAttribute : System.Attribute { }
            """,
            fixedSource);
    }

    [Theory]
    [InlineData("[ObservableProperty]\n    [NotifyPropertyChangedFor(nameof(Upper))]\n    private string _name = \"\";\n\n    public string Upper => Name.ToUpperInvariant();")]
    [InlineData("[ObservableProperty]\n    private string _name = \"\";\n\n    partial void OnNameChanged(string value) { }")]
    [InlineData("[ObservableProperty]\n    private string _name = \"\";\n\n    public void Reset() => SetProperty(ref _name, \"\");")]
    [InlineData("[ObservableProperty]\n    private string _name = \"\";\n\n    public string Raw => _name;")]
    [InlineData("[ObservableProperty]\n    [field: System.NonSerialized]\n    private string _name = \"\";")]
    [InlineData("[ObservableProperty]\n    private static string _name = \"\";")]
    public async Task ObservableObject_classes_relying_on_more_of_the_toolkit_get_no_fix(string members)
    {
        var actions = await CodeFixRunner.GetActionsAsync(
            new ConvertObservableObjectCodeFixProvider(),
            "WK0012",
            $$"""
            using CommunityToolkit.Mvvm.ComponentModel;

            public partial class Person : ObservableObject
            {
                {{members}}
            }
            """,
            MigrationStubs.Toolkit);

        Assert.Empty(actions);
    }
}
