using BenchmarkDotNet.Attributes;
using Wick.Benchmarks.State;
using Wick.Core;

namespace Wick.Benchmarks;

/// <summary>
/// Writes to a reactive property with <see cref="Observers"/> dependents. Kept apart from
/// <see cref="ReactiveStateBenchmarks"/> so that the parameter does not multiply the unobserved cases.
/// </summary>
[MemoryDiagnoser]
public class ObservedWriteBenchmarks
{
    private readonly ReactiveCounter _derivedSource = new();
    private readonly ReactiveCounter _effectSource = new();
    private readonly NoopObserver _derivedReader = new();
    private Derived<int>[] _deriveds = [];
    private ReactiveEffect[] _effects = [];
    private int _effectRuns;
    private int _expectedEffectRuns;
    private int _value;

    [Params(1, 100)]
    public int Observers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Derived values are recomputed on write only while something reads them, so one observer tracks them all.
        _deriveds = [.. Enumerable.Range(0, Observers).Select(offset => Reactive.Derived(() => _derivedSource.Value + offset))];

        using (_derivedReader.BeginTracking())
        {
            foreach (var derived in _deriveds)
            {
                _ = derived.Value;
            }
        }

        _effects = [.. Enumerable.Range(0, Observers).Select(_ => Reactive.Effect(() =>
        {
            _ = _effectSource.Value;
            Interlocked.Increment(ref _effectRuns);
        }))];

        // Every effect ran once synchronously when it was created.
        _expectedEffectRuns = Observers;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var effect in _effects)
        {
            effect.Dispose();
        }

        foreach (var derived in _deriveds)
        {
            derived.Dispose();
        }

        _derivedReader.Dispose();
    }

    /// <summary>Observed derived values recompute synchronously, so the write returns once all of them are current.</summary>
    [Benchmark]
    public void WriteObservedByDeriveds() => _derivedSource.Value = ++_value;

    /// <summary>Effects re-run on the thread pool; this waits until every one of them has re-run.</summary>
    [Benchmark]
    public void WriteAndAwaitEffects()
    {
        _expectedEffectRuns += Observers;
        _effectSource.Value = ++_value;

        var spinner = new SpinWait();
        while (Volatile.Read(ref _effectRuns) < _expectedEffectRuns)
        {
            spinner.SpinOnce(sleep1Threshold: -1);
        }
    }
}
