using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Wick.Benchmarks.State;
using Wick.Core;

namespace Wick.Benchmarks;

/// <summary>Reads and writes of a generated reactive property, compared with a plain auto-property.</summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ReactiveStateBenchmarks
{
    private const int BatchSize = 100;

    private readonly PlainCounter _plain = new();
    private readonly ReactiveCounter _read = new();

    // Never read while tracking, so it has no node and nothing to notify.
    private readonly ReactiveCounter _write = new();

    private readonly ReactiveCounter _batched = new();
    private readonly NoopObserver _readObserver = new();
    private readonly NoopObserver _derivedObserver = new();
    private Derived<int>? _derived;
    private Action? _writeBatch;
    private int _value;

    [GlobalSetup]
    public void Setup()
    {
        // An observed derived value recomputes synchronously on every notification it receives, which is what
        // batching saves.
        _derived = Reactive.Derived(() => _batched.Value * 2);

        using (_derivedObserver.BeginTracking())
        {
            _ = _derived.Value;
        }

        _writeBatch = WriteBatch;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _derived?.Dispose();
        _derivedObserver.Dispose();
        _readObserver.Dispose();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Read")]
    public int ReadPlain() => _plain.Value;

    [Benchmark]
    [BenchmarkCategory("Read")]
    public int ReadUntracked() => _read.Value;

    /// <summary>Includes beginning and ending the tracking scope; the observer is already subscribed after the first run.</summary>
    [Benchmark]
    [BenchmarkCategory("Read")]
    public int ReadTracked()
    {
        using (_readObserver.BeginTracking())
        {
            return _read.Value;
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Write")]
    public void WritePlain() => _plain.Value = ++_value;

    [Benchmark]
    [BenchmarkCategory("Write")]
    public void WriteUnobserved() => _write.Value = ++_value;

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Batch")]
    public void HundredWritesUnbatched() => WriteBatch();

    [Benchmark]
    [BenchmarkCategory("Batch")]
    public void HundredWritesBatched() => Reactive.Batch(_writeBatch!);

    private void WriteBatch()
    {
        for (var i = 0; i < BatchSize; i++)
        {
            _batched.Value = ++_value;
        }
    }
}
