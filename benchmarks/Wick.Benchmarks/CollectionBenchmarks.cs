using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Wick.Core;

namespace Wick.Benchmarks;

/// <summary>Untracked use of the reactive collections compared with the BCL collections they wrap.</summary>
/// <remarks>Except for enumeration, every benchmark performs <see cref="Size"/> operations and reports the time per operation.</remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class CollectionBenchmarks
{
    private const int Size = 1_000;

    private readonly List<int> _list = [.. Enumerable.Range(0, Size)];
    private readonly ReactiveList<int> _reactiveList = new(Enumerable.Range(0, Size));
    private readonly Dictionary<int, int> _dictionary = Enumerable.Range(0, Size).ToDictionary(key => key);
    private readonly ReactiveDictionary<int, int> _reactiveDictionary = new(Enumerable.Range(0, Size).ToDictionary(key => key));
    private int _round;

    [Benchmark(Baseline = true, OperationsPerInvoke = Size)]
    [BenchmarkCategory("List.Add")]
    public List<int> ListAdd()
    {
        var list = new List<int>();
        for (var i = 0; i < Size; i++)
        {
            list.Add(i);
        }

        return list;
    }

    [Benchmark(OperationsPerInvoke = Size)]
    [BenchmarkCategory("List.Add")]
    public ReactiveList<int> ReactiveListAdd()
    {
        var list = new ReactiveList<int>();
        for (var i = 0; i < Size; i++)
        {
            list.Add(i);
        }

        return list;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Size)]
    [BenchmarkCategory("List[i]")]
    public int ListIndexer()
    {
        var sum = 0;
        for (var i = 0; i < Size; i++)
        {
            sum += _list[i];
        }

        return sum;
    }

    [Benchmark(OperationsPerInvoke = Size)]
    [BenchmarkCategory("List[i]")]
    public int ReactiveListIndexer()
    {
        var sum = 0;
        for (var i = 0; i < Size; i++)
        {
            sum += _reactiveList[i];
        }

        return sum;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("List foreach")]
    public int ListEnumerate()
    {
        var sum = 0;
        foreach (var item in _list)
        {
            sum += item;
        }

        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("List foreach")]
    public int ReactiveListEnumerate()
    {
        var sum = 0;
        foreach (var item in _reactiveList)
        {
            sum += item;
        }

        return sum;
    }

    /// <summary>Overwrites every key with a new value, so each set is a real change.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = Size)]
    [BenchmarkCategory("Dictionary[key] set")]
    public void DictionarySet()
    {
        var round = ++_round;
        for (var key = 0; key < Size; key++)
        {
            _dictionary[key] = round;
        }
    }

    [Benchmark(OperationsPerInvoke = Size)]
    [BenchmarkCategory("Dictionary[key] set")]
    public void ReactiveDictionarySet()
    {
        var round = ++_round;
        for (var key = 0; key < Size; key++)
        {
            _reactiveDictionary[key] = round;
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Size)]
    [BenchmarkCategory("Dictionary[key] get")]
    public int DictionaryGet()
    {
        var sum = 0;
        for (var key = 0; key < Size; key++)
        {
            sum += _dictionary[key];
        }

        return sum;
    }

    [Benchmark(OperationsPerInvoke = Size)]
    [BenchmarkCategory("Dictionary[key] get")]
    public int ReactiveDictionaryGet()
    {
        var sum = 0;
        for (var key = 0; key < Size; key++)
        {
            sum += _reactiveDictionary[key];
        }

        return sum;
    }
}
