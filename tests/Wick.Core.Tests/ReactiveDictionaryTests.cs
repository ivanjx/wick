using System.Runtime.CompilerServices;

namespace Wick.Core.Tests;

public sealed class ReactiveDictionaryTests
{
    [Fact]
    public void Key_readers_are_invalidated_only_by_their_key()
    {
        var prices = new ReactiveDictionary<string, decimal> { ["AAPL"] = 100m, ["MSFT"] = 200m };
        var apple = new CountingObserver();
        var count = new CountingObserver();
        var all = new CountingObserver();

        apple.Track(() => prices["AAPL"]);
        count.Track(() => prices.Count);
        all.Track(() => prices.Values.Sum());

        prices["MSFT"] = 201m;

        Assert.Equal(0, apple.Invalidations);
        Assert.Equal(0, count.Invalidations);
        Assert.Equal(1, all.Invalidations);

        prices["AAPL"] = 101m;

        Assert.Equal(1, apple.Invalidations);
        Assert.Equal(0, count.Invalidations);
        Assert.Equal(2, all.Invalidations);
    }

    [Fact]
    public void Reading_a_missing_key_subscribes_to_its_later_addition()
    {
        var map = new ReactiveDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var observer = new CountingObserver();

        Assert.False(observer.Track(() => map.ContainsKey("key")));

        map.Add("other", 1);

        Assert.Equal(0, observer.Invalidations);

        map.Add("KEY", 2);

        Assert.Equal(1, observer.Invalidations);
        Assert.Equal(2, observer.Track(() => map.TryGetValue("key", out var value) ? value : -1));
    }

    [Fact]
    public void Removing_and_clearing_invalidate_the_removed_keys_and_the_count()
    {
        var map = new ReactiveDictionary<int, string> { [1] = "one", [2] = "two" };
        var one = new CountingObserver();
        var two = new CountingObserver();
        var count = new CountingObserver();

        one.Track(() => map.GetValueOrDefault(1));
        two.Track(() => map.GetValueOrDefault(2));
        count.Track(() => map.Count);

        Assert.True(map.Remove(1));
        Assert.False(map.Remove(1));

        Assert.Equal(1, one.Invalidations);
        Assert.Equal(0, two.Invalidations);
        Assert.Equal(1, count.Invalidations);

        map.Clear();
        map.Clear();

        Assert.Equal(1, one.Invalidations);
        Assert.Equal(1, two.Invalidations);
        Assert.Equal(2, count.Invalidations);
    }

    [Fact]
    public void Writing_an_equal_value_or_a_rejected_add_does_not_invalidate()
    {
        var map = new ReactiveDictionary<string, int> { ["a"] = 1 };
        var observer = new CountingObserver();

        observer.Track(() => (map["a"], map.Count, map.Keys.Length));

        map["a"] = 1;
        Assert.False(map.TryAdd("a", 2));
        Assert.Throws<ArgumentException>(() => map.Add("a", 3));

        Assert.Equal(0, observer.Invalidations);
        Assert.Equal(1, map["a"]);
    }

    [Fact]
    public async Task Tracked_enumeration_is_safe_while_other_threads_mutate()
    {
        var map = new ReactiveDictionary<int, int>();
        var observer = new CountingObserver();

        // Subscribed before the first write: writers that finish before the loop below starts still invalidate it.
        observer.Track(() => map.Sum(entry => (long)entry.Value));

        var writing = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < 2_000; i++)
            {
                var key = (writer * 10_000) + i;
                map[key] = i;
                map[key] = i + 1;

                if (i % 2 == 0)
                {
                    map.Remove(key);
                }
            }
        })).ToArray();

        do
        {
            observer.Track(() => map.Sum(entry => (long)entry.Value));
        }
        while (!writing.All(task => task.IsCompleted));

        await Task.WhenAll(writing);

        Assert.Equal(4 * 1_000, map.Count);
        Assert.True(observer.Invalidations > 0);
    }

    [Fact]
    public void Keys_no_longer_read_are_released_while_keys_still_read_keep_notifying()
    {
        var map = new ReactiveDictionary<object, int>();
        var kept = new object();
        var keptReader = new CountingObserver();
        var reader = new CountingObserver();

        keptReader.Track(() => map.ContainsKey(kept));
        var removed = AddReadAndRemove(map, reader);

        // The reader moves on to many other keys, which lets the dictionary drop the nodes nobody reads any more.
        for (var i = 0; i < 1_000; i++)
        {
            reader.Track(() => map.ContainsKey(i));
        }

        CollectGarbage();

        Assert.False(removed.IsAlive);

        map[kept] = 1;

        Assert.Equal(1, keptReader.Invalidations);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddReadAndRemove(ReactiveDictionary<object, int> map, CountingObserver reader)
    {
        var key = new object();
        map[key] = 1;
        reader.Track(() => map.TryGetValue(key, out _));
        map.Remove(key);

        return new WeakReference(key);
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
