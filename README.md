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
dotnet sln add lib/wick/src/Wick.Core/Wick.Core.csproj lib/wick/src/Wick.Blazor/Wick.Blazor.csproj lib/wick/src/Wick.Generators/Wick.Generators.csproj lib/wick/src/Wick.CodeFixes/Wick.CodeFixes.csproj
```

Then reference the projects from your Blazor app:

```xml
<ItemGroup>
  <ProjectReference Include="..\lib\wick\src\Wick.Blazor\Wick.Blazor.csproj" />
  <ProjectReference Include="..\lib\wick\src\Wick.Generators\Wick.Generators.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  <!-- Optional: IDE quick fixes for Wick's warnings. -->
  <ProjectReference Include="..\lib\wick\src\Wick.CodeFixes\Wick.CodeFixes.csproj"
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

## Performance

One cell of a rendered grid changes. In the traditional version, the grid subscribes to each cell's change event and calls `InvokeAsync(StateHasChanged)`, the same pattern shown in [Why](#why). With Wick, each cell component just reads `@Cell.Value`. The time is measured from the write until rendering has finished.

| Cells | Approach | Components re-rendered | Time per update | Allocated |
|------:|----------|-----------------------:|----------------:|----------:|
| 100 | Traditional | 101 | 23.5 μs | 3,264 B |
| 100 | Wick | 1 | 1.5 μs | 168 B |
| 400 | Traditional | 401 | 89.4 μs | 12,864 B |
| 400 | Wick | 1 | 1.5 μs | 160 B |

The traditional version gets slower as the grid grows; Wick's cost stays flat, because only the changed cell re-renders. Wick's time includes its hop to the thread pool.

Measured with BenchmarkDotNet using a headless Blazor `Renderer`, so browser and network time are not included. Absolute numbers depend on the machine. To reproduce:

```sh
dotnet run -c Release --project benchmarks/Wick.Benchmarks -- --filter '*GridRender*'
```

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

Arrays are fine as long as you replace them (`Cells = [.. cells]`). Writing one element in place (`State.Cells[i] = x`) is not tracked, and the compiler warns about it.

### Local component state

State that belongs to one component doesn't need a service. In a `@code` block, use `ReactiveValue<T>`; the source generator can't see partial properties declared in a `.razor` file:

```razor
<p>@_count.Value</p>
<button @onclick="() => _count.Value++">+</button>

@code {
    private readonly ReactiveValue<int> _count = new(0);
}
```

A write from any thread (a timer, a callback after an `await`) re-renders the component without `InvokeAsync` or `StateHasChanged`. Pass the value to a child component (`<CountDisplay Count="_count" />`) and only the child that reads `Count.Value` re-renders when it changes; the parent doesn't. The reactive collections and `Reactive.Derived` work as component fields too. The optional second argument names the value in [diagnostics](#why-did-this-re-render): `new(0, "Counter.Count")`.

In a code-behind file, the component itself can be a `[ReactiveState]` type. Properties set by Blazor (`[Parameter]`, `[CascadingParameter]`, `[Inject]`, `[SupplyParameterFrom...]`) stay plain auto-properties:

```csharp
[ReactiveState]
public partial class Counter
{
    [Parameter]
    public int Step { get; set; } = 1;

    public partial int Count { get; set; }
}
```

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

### Why did this re-render?

Turn on diagnostics during development to log every reactive re-render with its cause:

```csharp
if (builder.Environment.IsDevelopment())
{
    WickDiagnostics.Enabled = true;
    WickDiagnostics.Message += message => Console.WriteLine(message);
}
```

```text
CountDisplay re-render: CounterState.Count changed
DetailsView re-render: ProfileState.User, ProfileState.ShowBio changed
```

Without a `Message` subscriber, messages go to `System.Diagnostics.Trace` (category `Wick`). To see what a component currently depends on, call `WickDiagnostics.GetDependencies(component)`; it also accepts effects and derived values. When diagnostics are off they cost a single flag check.

### Testing components

Wick re-renders asynchronously, so a test that changes state must wait before checking the markup. `ReactiveRendering.WhenIdleAsync()` completes once every scheduled re-render and effect has finished, including chains where one triggers another:

```csharp
[Fact]
public async Task Shows_the_new_count()
{
    var state = new CounterState();
    Services.AddSingleton(state);
    var cut = Render<CountDisplay>();

    await Task.Run(() => state.Count = 5);
    await ReactiveRendering.WhenIdleAsync();

    cut.MarkupMatches("<p>5</p>");
}
```

It waits for the whole process, so tests running in parallel can delay it, but never make it return early.

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
| WK0008 | Warning | An element of an array held by a reactive property is written in place; readers are not notified |

WK0001, WK0002, WK0006 and WK0007 come with IDE quick fixes: add `partial`, switch to the reactive collection, or override `Dispose(bool)`/`DisposeAsyncCore()`. For WK0007, the fix only applies to C# files; in a `.razor` file, move the cleanup into the override yourself.

Warnings can be suppressed with `#pragma warning disable` or in `.editorconfig`.

## Sample apps

`samples/Wick.Sample` (Interactive Server) shows every feature, with render counters on each component so you can see exactly what re-renders:

```sh
dotnet run --project samples/Wick.Sample
```

Then open http://localhost:5080. Pages include a counter written from several threads, a 400-cell grid next to the classic event pattern, a live market fed by a background service, nested state, a todo list, and reactive regions.

`samples/Wick.Sample.Wasm` is a smaller WebAssembly app: a background loop, a derived value, a component-owned effect and a batch, all on the browser's single thread.

```sh
dotnet run --project samples/Wick.Sample.Wasm
```

`samples/Wick.Sample.WinForms` is a Blazor Hybrid app for Windows: native WinForms controls next to a `BlazorWebView`, both over the same state. The Blazor cards re-render through Wick; the native labels are set by `Reactive.Effect`s that marshal to the UI thread. Click either side and both update.

```sh
dotnet run --project samples/Wick.Sample.WinForms
```

## Repository layout

| Path | Contents |
|------|----------|
| `src/Wick.Core` | Reactive state, collections, derived values, effects, batching. No Blazor dependency. |
| `src/Wick.Blazor` | `ReactiveComponentBase`, `ReactiveLayoutComponentBase`, `ReactiveRegion` |
| `src/Wick.Generators` | Source generator for `[ReactiveState]` and the analyzers |
| `src/Wick.CodeFixes` | IDE quick fixes for the analyzer warnings |
| `tests/` | Unit, generator and bUnit component tests |
| `tests/Wick.E2E.*` | Browser end-to-end tests: a Blazor Web App host driven by Playwright in Interactive Server and WebAssembly modes |
| `samples/Wick.Sample` | Interactive Server demo app |
| `samples/Wick.Sample.Wasm` | WebAssembly demo app |
| `samples/Wick.Sample.WinForms` | Blazor Hybrid (WinForms) demo app |
| `benchmarks/Wick.Benchmarks` | BenchmarkDotNet benchmarks, including a grid render comparison with the classic event pattern |

Build and test:

```sh
dotnet build Wick.slnx
dotnet test --solution Wick.slnx
```

The end-to-end tests drive the browser already installed on the machine (Edge on Windows, Chrome elsewhere), so Playwright doesn't download one. To run only them:

```sh
dotnet test --project tests/Wick.E2E.Tests
```

## License

[MIT](LICENSE)
