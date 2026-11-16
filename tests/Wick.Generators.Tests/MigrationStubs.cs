namespace Wick.Generators.Tests;

/// <summary>Stand-ins for the libraries the migration diagnostics look for, which the tests do not reference.</summary>
internal static class MigrationStubs
{
    /// <summary>Blazor's component base and the Wick.Blazor adapter on top of it.</summary>
    public const string Components =
        """
        using System;
        using System.Threading.Tasks;

        namespace Microsoft.AspNetCore.Components
        {
            public abstract class ComponentBase
            {
                protected virtual void OnInitialized() { }

                protected void StateHasChanged() { }

                protected Task InvokeAsync(Action workItem) => Task.CompletedTask;

                protected Task InvokeAsync(Func<Task> workItem) => Task.CompletedTask;
            }
        }

        namespace Wick.Blazor
        {
            public abstract class ReactiveComponentCore : Microsoft.AspNetCore.Components.ComponentBase, IDisposable, IAsyncDisposable
            {
                public void Dispose() => Dispose(true);

                public ValueTask DisposeAsync() => DisposeAsyncCore();

                protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;

                protected virtual void Dispose(bool disposing) { }
            }
        }
        """;

    /// <summary>State whose generated setters raise <c>PropertyChanged</c>.</summary>
    public const string NotifyingState =
        """
        using Wick.Core;

        namespace App
        {
            [ReactiveState(NotifyPropertyChanged = true)]
            public partial class Settings
            {
                public partial string Language { get; set; } = "en";

                public partial bool Dark { get; set; }
            }

            [ReactiveState(NotifyPropertyChanged = true)]
            public partial class Session
            {
                public partial string? User { get; set; }
            }
        }
        """;

    /// <summary>The parts of CommunityToolkit.Mvvm the conversion deals with.</summary>
    public const string Toolkit =
        """
        using System;
        using System.ComponentModel;
        using System.Runtime.CompilerServices;

        namespace CommunityToolkit.Mvvm.ComponentModel
        {
            public abstract class ObservableObject : INotifyPropertyChanged, INotifyPropertyChanging
            {
                public event PropertyChangedEventHandler? PropertyChanged;

                public event PropertyChangingEventHandler? PropertyChanging;

                protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));

                protected void OnPropertyChanging([CallerMemberName] string? propertyName = null) => PropertyChanging?.Invoke(this, new(propertyName));

                protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
                {
                    field = value;
                    OnPropertyChanged(propertyName);

                    return true;
                }
            }

            [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
            public sealed class ObservablePropertyAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true)]
            public sealed class NotifyPropertyChangedForAttribute(string propertyName) : Attribute
            {
                public string PropertyName { get; } = propertyName;
            }
        }
        """;
}
