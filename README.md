# Wick

**Fine-grained reactive state for Blazor.** Change a property, from any thread, and exactly the components that read it re-render. No events, no subscriptions, no `StateHasChanged`, no `InvokeAsync`.

```csharp
[ReactiveState]
public partial class CounterState
{
    public partial int Count { get; set; }
}
```

```razor
@inject CounterState State

<p>@State.Count</p>
```

```csharp
// Anywhere: an event handler, a timer, a BackgroundService, a SignalR callback.
State.Count++;
```

That's it. The component re-renders because it read `Count` during its last render.

---

## Why

The usual way to share state in Blazor looks like this:

```csharp
public class CounterState
{
    public int Count { get; private set; }
    public event Action? Changed;

    public void Increment()
    {
        Count++;
        Changed?.Invoke();
    }
}
```

```razor
@implements IDisposable
@inject CounterState State

<p>@State.Count</p>

@code {
    protected override void OnInitialized() => State.Changed += OnChanged;

    private void OnChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => State.Changed -= OnChanged;
}
```

Every component subscribes by hand, must remember to unsubscribe, must marshal to the dispatcher, and re-renders on *every* change to the state, even properties it never shows.

With Wick, the component just reads the property. Wick records the read, re-renders the component when that property changes, does so on the right dispatcher, and forgets the dependency when the component stops reading it or is disposed.

## Getting started

**Requirements:** .NET 10 and C# 14 (the default for `net10.0`).

Add Wick to your repository as a git submodule:

```sh
git submodule add https://github.com/ivanjx/wick.git lib/wick
dotnet sln add lib/wick/src/Wick.Core/Wick.Core.csproj lib/wick/src/Wick.Blazor/Wick.Blazor.csproj lib/wick/src/Wick.Generators/Wick.Generators.csproj
```

Then reference the projects from your Blazor app:

```xml
<ItemGroup>
  <ProjectReference Include="..\lib\wick\src\Wick.Blazor\Wick.Blazor.csproj" />
  <ProjectReference Include="..\lib\wick\src\Wick.Generators\Wick.Generators.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```

Paths are relative to your app's `.csproj`; adjust them to where it lives. Anyone cloning your repository needs `git clone --recurse-submodules`, or `git submodule update --init` after a plain clone. To move to a newer Wick, run `git submodule update --remote lib/wick` and commit the change.

**1. Make components reactive.** Add this to `_Imports.razor` so every component inherits it:

```razor
@using Wick.Blazor
@using Wick.Core
@inherits ReactiveComponentBase
```

A component can opt out with `@inherits ComponentBase`. For layouts, use `@inherits ReactiveLayoutComponentBase`.

**2. Declare state.** Mark the class `[ReactiveState]` and make each tracked property `partial`. The source generator implements them.

```csharp
[ReactiveState]
public partial class TodoState
{
    public partial string Filter { get; set; } = "all";

    public partial bool ShowCompleted { get; set; }
}
```

**3. Register and use it** like any other service:

```csharp
builder.Services.AddScoped<TodoState>();      // one per circuit (browser tab)
builder.Services.AddSingleton<MarketState>(); // shared by everyone
```

## How it works

1. While a component renders, Wick records every reactive property it reads.
2. When one of those properties is set to a different value, the component is scheduled for a re-render on its own dispatcher. The write can come from any thread.
3. On each render the recorded reads are replaced, so dependencies follow the markup: if an `@if` stops showing a property, changes to it no longer re-render the component.

Re-renders are coalesced: a thousand writes in a row cause a handful of renders, not a thousand.

## Guide

### Computed properties

Plain C# properties that read reactive ones just work. Whoever reads `Doubled` depends on `Count`:

```csharp
public int Doubled => Count * 2;
```

### Nested state

Reactive objects can contain reactive objects. Each property of each object is tracked on its own, so `<p>@State.User.Name</p>` re-renders when `Name` changes or when `User` is replaced, and not when `User.Email` changes.

### Collections

Mutating a `List<T>` in place is invisible to Wick (and the compiler warns about it, see [Diagnostics](#diagnostics)). Use the reactive collections instead:

```csharp
public ReactiveList<TodoItem> Items { get; } = [];
public ReactiveDictionary<string, decimal> Prices { get; } = new();
public ReactiveSet<string> SelectedIds { get; } = [];
```

```csharp
State.Items.Add(item);          // re-renders components that enumerate or count the list
State.Prices["AAPL"] = 191.20m; // re-renders components that read "AAPL", not other keys
State.SelectedIds.Add("42");    // re-renders components that asked Contains("42") or enumerate the set
```

All three are thread-safe, and enumerating them while another thread writes is safe. `Snapshot()` returns a consistent `ImmutableArray<T>`. Replacing an immutable collection (`Items = Items.Add(item)`) also works.

### Batching

Several writes that belong together can be grouped so readers re-render once:

```csharp
Reactive.Batch(() =>
{
    State.FirstName = "Ada";
    State.LastName = "Lovelace";
});

await Reactive.BatchAsync(async () =>
{
    State.Loading = true;
    State.Items.ReplaceAll(await api.LoadAsync());
    State.Loading = false;
});
```

Within a single synchronous Blazor event handler you rarely need this: renders are already coalesced.

### Derived values

A computed property runs on every read. When the computation is expensive, or many components read it, cache it with `Reactive.Derived`:

```csharp
private readonly Derived<double> _average;

public MarketState()
{
    _average = Reactive.Derived(() => Tickers.Average(ticker => ticker.Price));
}

public double AveragePrice => _average.Value;
```

The value is recomputed once when its inputs change, and its readers re-render only if the result actually changed.

### Effects

Run code whenever the state it reads changes, for logging, persistence, or syncing state. In a component, use `Effect`; it is disposed together with the component:

```csharp
protected override void OnInitialized()
{
    Effect(() => storage.Save("filter", State.Filter));
}
```

Outside components (in a state class or a service), use `Reactive.Effect` and dispose it when its owner goes away:

```csharp
var effect = Reactive.Effect(() => logger.LogInformation("Filter is now {Filter}", State.Filter));

// When the owner is disposed:
effect.Dispose();
```

An effect runs once immediately and again, on the thread pool, after each change. Effects may write reactive state. UI work inside a component effect should go through `InvokeAsync`.

### Changes Wick can't see

If state was mutated behind Wick's back (a mutable object from a library, a `List<T>` you can't replace), tell the readers explicitly:

```csharp
legacyList.Add(item);
Reactive.Invalidate(() => State.LegacyList);
```

### Content passed into non-reactive components

Child content rendered *by* a non-reactive component (`CascadingValue`, `ErrorBoundary`, most third-party components) is rendered outside any tracking. Wrap it in `<ReactiveRegion>`:

```razor
<ErrorBoundary>
    <ReactiveRegion>
        <p>@State.Count</p>
    </ReactiveRegion>
</ErrorBoundary>
```

### Component cleanup

Reactive components release their dependencies automatically. For your own cleanup, override `Dispose(bool)` or `DisposeAsyncCore()` rather than adding `@implements IDisposable`, which Blazor would not call. The compiler warns about this ([WK0007](#diagnostics)).

```csharp
protected override void Dispose(bool disposing)
{
    _timer?.Dispose();
    base.Dispose(disposing);
}
```

## Diagnostics

| ID | Severity | Meaning |
|----|----------|---------|
| WK0001 | Error | A `[ReactiveState]` type, or a type containing it, is not `partial` |
| WK0002 | Warning | A writable auto-property in a reactive type is not reactive; make it `partial` |
| WK0003 | Error | This partial property cannot be reactive (static, ref-returning, explicit interface implementation, or missing a getter or setter) |
| WK0004 | Error | This type cannot be a reactive state type (records, static classes, `ReactiveObject` subclasses) |
| WK0005 | Error | Reactive state requires C# 14 |
| WK0006 | Warning | A reactive property holds a mutable collection whose in-place changes are not tracked |
| WK0007 | Warning | A reactive component declares its own `Dispose` or `DisposeAsync` |

Warnings can be suppressed with `#pragma warning disable` or in `.editorconfig`.

## Sample app

`samples/Wick.Sample` shows every feature, with render counters on each component so you can see exactly what re-renders:

```sh
dotnet run --project samples/Wick.Sample
```

Then open http://localhost:5080. Pages include a counter written from several threads, a 400-cell grid next to the classic event pattern, a live market fed by a background service, nested state, a todo list, and reactive regions.

## Repository layout

| Path | Contents |
|------|----------|
| `src/Wick.Core` | Reactive state, collections, derived values, effects, batching. No Blazor dependency. |
| `src/Wick.Blazor` | `ReactiveComponentBase`, `ReactiveLayoutComponentBase`, `ReactiveRegion` |
| `src/Wick.Generators` | Source generator for `[ReactiveState]` and the analyzers |
| `tests/` | Unit, generator and bUnit component tests |
| `samples/Wick.Sample` | Interactive Server demo app |

Build and test:

```sh
dotnet build Wick.slnx
dotnet test --solution Wick.slnx
```

## License

[MIT](LICENSE)
