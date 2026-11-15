namespace Wick.Core.Tests;

public sealed class ReactiveSetTests
{
    [Fact]
    public void Item_readers_are_invalidated_only_by_their_item()
    {
        var tags = new ReactiveSet<string> { "a" };
        var a = new CountingObserver();
        var count = new CountingObserver();
        var all = new CountingObserver();

        a.Track(() => tags.Contains("a"));
        count.Track(() => tags.Count);
        all.Track(() => tags.Snapshot().Length);

        tags.Add("b");

        Assert.Equal(0, a.Invalidations);
        Assert.Equal(1, count.Invalidations);
        Assert.Equal(1, all.Invalidations);

        tags.Remove("a");

        Assert.Equal(1, a.Invalidations);
        Assert.Equal(2, count.Invalidations);
        Assert.Equal(2, all.Invalidations);

        tags.Add("a");

        Assert.Equal(2, a.Invalidations);
    }

    [Fact]
    public void Checking_a_missing_item_subscribes_to_its_later_addition()
    {
        var tags = new ReactiveSet<string>(StringComparer.OrdinalIgnoreCase);
        var observer = new CountingObserver();

        Assert.False(observer.Track(() => tags.Contains("tag")));

        tags.Add("other");

        Assert.Equal(0, observer.Invalidations);

        Assert.True(tags.Add("TAG"));

        Assert.Equal(1, observer.Invalidations);
        Assert.True(observer.Track(() => tags.Contains("tag")));
        Assert.False(tags.Add("Tag"));
        Assert.Equal(2, tags.Count);
    }

    [Fact]
    public void Structural_readers_are_invalidated_by_any_membership_change()
    {
        var tags = new ReactiveSet<int> { 1, 2 };
        var enumeration = new CountingObserver();
        var subset = new CountingObserver();
        var equals = new CountingObserver();

        enumeration.Track(() => tags.Sum());
        subset.Track(() => tags.IsSubsetOf([1, 2, 3]));
        equals.Track(() => tags.SetEquals([1, 2]));

        tags.Add(3);

        Assert.Equal(1, enumeration.Invalidations);
        Assert.Equal(1, subset.Invalidations);
        Assert.Equal(1, equals.Invalidations);
    }

    [Fact]
    public void Mutations_that_do_not_change_membership_do_not_invalidate()
    {
        var tags = new ReactiveSet<int> { 1 };
        var empty = new ReactiveSet<int>();
        var observer = new CountingObserver();
        var emptyObserver = new CountingObserver();

        observer.Track(() => (tags.Contains(1), tags.Contains(2), tags.Count, tags.Snapshot().Length));
        emptyObserver.Track(() => empty.Count);

        Assert.False(tags.Add(1));
        Assert.False(tags.Remove(2));
        tags.UnionWith([1]);
        tags.ExceptWith([2]);
        tags.IntersectWith([1, 2]);
        tags.SymmetricExceptWith([]);
        empty.Clear();

        Assert.Equal(0, observer.Invalidations);
        Assert.Equal(0, emptyObserver.Invalidations);
    }

    [Fact]
    public void Bulk_mutations_invalidate_each_affected_reader_once()
    {
        var tags = new ReactiveSet<int> { 1, 2, 3 };
        var one = new CountingObserver();
        var four = new CountingObserver();
        var five = new CountingObserver();
        var count = new CountingObserver();

        one.Track(() => tags.Contains(1));
        four.Track(() => tags.Contains(4));
        five.Track(() => tags.Contains(5));
        count.Track(() => tags.Count);

        tags.UnionWith([3, 4, 5]);

        Assert.Equal(0, one.Invalidations);
        Assert.Equal(1, four.Invalidations);
        Assert.Equal(1, five.Invalidations);
        Assert.Equal(1, count.Invalidations);

        tags.ExceptWith([1, 4, 9]);

        Assert.Equal(1, one.Invalidations);
        Assert.Equal(2, four.Invalidations);
        Assert.Equal(1, five.Invalidations);
        Assert.Equal(2, count.Invalidations);

        tags.SymmetricExceptWith([1, 5]);

        Assert.Equal(2, one.Invalidations);
        Assert.Equal(2, five.Invalidations);
        Assert.Equal(3, count.Invalidations);
        Assert.True(tags.SetEquals([1, 2, 3]));

        tags.IntersectWith([1, 2]);

        Assert.Equal(2, one.Invalidations);
        Assert.Equal(4, count.Invalidations);

        tags.Clear();

        Assert.Equal(3, one.Invalidations);
        Assert.Equal(2, four.Invalidations);
        Assert.Equal(5, count.Invalidations);
        Assert.Empty(tags);
    }

    [Fact]
    public void Collection_expressions_initialise_the_set()
    {
        ReactiveSet<string> empty = [];
        ReactiveSet<string> tags = ["a", "b", "a"];

        Assert.Empty(empty);
        Assert.Equal(2, tags.Count);
        Assert.Contains("b", tags.Snapshot());
    }

    [Fact]
    public async Task Tracked_enumeration_is_safe_while_other_threads_mutate()
    {
        var set = new ReactiveSet<int>();
        var observer = new CountingObserver();

        // Subscribed before the first write: writers that finish before the loop below starts still invalidate it.
        observer.Track(() => set.Sum(item => (long)item));

        var writing = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < 2_000; i++)
            {
                var item = (writer * 10_000) + i;
                set.Add(item);

                if (i % 2 == 0)
                {
                    set.Remove(item);
                }
            }
        })).ToArray();

        do
        {
            observer.Track(() => set.Sum(item => (long)item));
        }
        while (!writing.All(task => task.IsCompleted));

        await Task.WhenAll(writing);

        Assert.Equal(4 * 1_000, set.Count);
        Assert.True(observer.Invalidations > 0);
    }
}
