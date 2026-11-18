namespace Wick.Core.Tests;

public sealed class ReactiveListTests
{
    [Fact]
    public void Count_readers_ignore_replace_and_move_but_content_readers_do_not()
    {
        var list = new ReactiveList<string>(["a", "b", "c"]);
        var count = new CountingObserver();
        var items = new CountingObserver();

        count.Track(() => list.Count);
        items.Track(() => list.ToArray());

        list[0] = "x";
        list.Move(0, 2);
        list.ReplaceAll(["p", "q", "r"]);

        Assert.Equal(0, count.Invalidations);
        Assert.Equal(3, items.Invalidations);

        list.Add("d");
        list.RemoveAt(0);

        Assert.Equal(2, count.Invalidations);
        Assert.Equal(5, items.Invalidations);
    }

    [Fact]
    public void Operations_that_change_nothing_do_not_invalidate()
    {
        var list = new ReactiveList<int>([1, 2]);
        var observer = new CountingObserver();

        observer.Track(() => (list.Count, list.Sum()));

        list[0] = 1;
        list.Remove(42);
        list.RemoveAll(item => item > 10);
        list.AddRange([]);
        list.Move(1, 1);

        Assert.Equal(0, observer.Invalidations);

        list.Clear();
        list.Clear();

        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public void One_mutation_invalidates_an_observer_of_both_count_and_contents_once()
    {
        var list = new ReactiveList<int>();
        var observer = new CountingObserver();

        observer.Track(() => (list.Count, list.Contains(1)));
        list.Add(1);

        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public void Remove_all_leaves_the_list_unchanged_when_the_predicate_throws()
    {
        var list = new ReactiveList<int>([1, 2, 3, 4, 5]);
        var observer = new CountingObserver();

        observer.Track(() => list.ToArray());

        Assert.Throws<InvalidOperationException>(() => list.RemoveAll(item => item == 4 ? throw new InvalidOperationException() : item % 2 == 0));

        Assert.Equal([1, 2, 3, 4, 5], list.Snapshot());
        Assert.Equal(0, observer.Invalidations);
    }

    [Fact]
    public void Enumeration_iterates_a_snapshot_that_mutations_do_not_affect()
    {
        var list = new ReactiveList<int>([1, 2, 3]);
        var seen = new List<int>();

        foreach (var item in list)
        {
            seen.Add(item);
            list.Add(item * 10);
        }

        Assert.Equal([1, 2, 3], seen);
        Assert.Equal([1, 2, 3, 10, 20, 30], list.Snapshot().ToArray());
    }

    [Fact]
    public void Mutating_while_tracking_throws_and_leaves_the_list_unchanged()
    {
        var list = new ReactiveList<int>();
        var observer = new CountingObserver();

        Assert.Throws<InvalidOperationException>(() => observer.Track(() =>
        {
            list.Add(1);
            return 0;
        }));

        Assert.Empty(list);
    }

    [Fact]
    public async Task Tracked_enumeration_is_safe_while_other_threads_mutate()
    {
        const int writers = 4;
        const int itemsPerWriter = 2_000;

        var list = new ReactiveList<int>();
        var observer = new CountingObserver();

        // Subscribed before the first write: writers that finish before the loop below starts still invalidate it.
        observer.Track(() => list.Sum(item => (long)item));

        var writing = Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < itemsPerWriter; i++)
            {
                list.Add(i);

                if (i % 3 == 0)
                {
                    list.RemoveAt(0);
                }
            }
        })).ToArray();

        do
        {
            observer.Track(() => list.Sum(item => (long)item));
        }
        while (!writing.All(task => task.IsCompleted));

        await Task.WhenAll(writing);

        Assert.Equal(writers * (itemsPerWriter - ((itemsPerWriter + 2) / 3)), list.Count);
        Assert.True(observer.Invalidations > 0);
    }
}
