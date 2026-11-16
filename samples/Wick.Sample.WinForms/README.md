# Wick.Sample.WinForms

A Blazor Hybrid app for Windows: native WinForms controls next to a `BlazorWebView`, both over the same state.

The Blazor cards re-render through Wick. The native labels are set by `Reactive.Effect`s that marshal to the UI thread. Click either side and both update.

```sh
dotnet run
```
