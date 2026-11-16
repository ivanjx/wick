using Microsoft.Extensions.Time.Testing;

namespace Wick.Core.Tests;

public sealed class CollectionChangesTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void A_list_reports_each_mutation_in_order()
    {
        var list = new ReactiveList<string>(["a", "b"]);
        var calls = new Calls<ListChange<string>>();
        using var watch = Reactive.Watch(list.Changes, calls.Add);

        list.Add("c");
        list.Insert(0, "z");
        list.Remove("a");
        list.RemoveAt(0);
        list[0] = "B";
        list.Move(0, 1);
        list.Clear();
        WaitForIdle();

        Assert.Equal(
            [
                "Added c 2",
                "Added z 0",
                "Removed a 1",
                "Removed z 0",
                "Replaced B (was b) 0",
                "Moved B 0 -> 1",
                "Reset []",
            ],
            calls.All().Select(Describe));
    }

    [Fact]
    public void A_list_reports_bulk_mutations_one_item_at_a_time_and_replace_all_as_a_reset()
    {
        var list = new ReactiveList<int>([1, 2, 3, 4, 5]);
        var calls = new Calls<ListChange<int>>();
        using var watch = Reactive.Watch(list.Changes, calls.Add);

        list.AddRange([6, 7]);
        list.RemoveAll(item => item % 2 == 0);
        list.ReplaceAll([9, 8]);
        WaitForIdle();

        Assert.Equal(
            [
                "Added 6 5",
                "Added 7 6",
                "Removed 2 1",
                "Removed 4 2",
                "Removed 6 3",
                "Reset [9, 8]",
            ],
            calls.All().Select(Describe));
    }

    [Fact]
    public void Replaying_the_changes_of_a_list_reproduces_it()
    {
        var list = new ReactiveList<int>([1, 2, 3]);
        var replica = new List<int>([1, 2, 3]);
        var calls = new Calls<ListChange<int>>();
        using var watch = Reactive.Watch(list.Changes, calls.Add);

        var random = new Random(42);
        for (var i = 0; i < 300; i++)
        {
            switch (random.Next(7))
            {
                case 0:
                    list.Add(i);
                    break;
                case 1:
                    list.Insert(random.Next(list.Count + 1), i);
                    break;
                case 2 when list.Count > 0:
                    list.RemoveAt(random.Next(list.Count));
                    break;
                case 3 when list.Count > 0:
                    list[random.Next(list.Count)] = i;
                    break;
                case 4 when list.Count > 0:
                    list.Move(random.Next(list.Count), random.Next(list.Count));
                    break;
                case 5:
                    list.RemoveAll(item => item % 3 == 0);
                    break;
                default:
                    list.AddRange([i, i + 1000]);
                    break;
            }
        }

        WaitForIdle();

        foreach (var change in calls.All())
        {
            Apply(replica, change);
        }

        Assert.Equal(list.ToArray(), replica.ToArray());
    }

    [Fact]
    public void A_set_reports_additions_and_removals_and_a_clear_as_one_removal_per_item()
    {
        var set = new ReactiveSet<string>(["a"]);
        var calls = new Calls<SetChange<string>>();
        using var watch = Reactive.Watch(set.Changes, calls.Add);

        set.Add("b");
        set.Add("b");
        set.Remove("a");
        set.Remove("missing");
        set.UnionWith(["c", "b"]);
        set.Clear();
        WaitForIdle();

        var changes = calls.All();

        Assert.Equal(["Added b", "Removed a", "Added c"], changes[..3].Select(Describe));
        Assert.Equal(["Removed b", "Removed c"], changes[3..].Select(Describe).Order());
    }

    [Fact]
    public void A_dictionary_reports_additions_replacements_and_removals_and_a_clear_as_one_removal_per_entry()
    {
        var dictionary = new ReactiveDictionary<string, int>();
        var calls = new Calls<DictionaryChange<string, int>>();
        using var watch = Reactive.Watch(dictionary.Changes, calls.Add);

        dictionary.Add("a", 1);
        dictionary["b"] = 2;
        dictionary["a"] = 1;
        dictionary["a"] = 3;
        dictionary.TryAdd("c", 4);
        dictionary.Remove("b");
        dictionary.Clear();
        WaitForIdle();

        var changes = calls.All();

        Assert.Equal(["Added a 1", "Added b 2", "Replaced a 3 (was 1)", "Added c 4", "Removed b 2"], changes[..5].Select(Describe));
        Assert.Equal(["Removed a 3", "Removed c 4"], changes[5..].Select(Describe).Order());
    }

    [Fact]
    public void The_changes_of_a_batch_arrive_together_and_in_order_once_it_ended()
    {
        var list = new ReactiveList<string>();
        var calls = new Calls<ListChange<string>>();
        using var watch = Reactive.Watch(list.Changes, calls.Add);

        Reactive.Batch(() =>
        {
            list.Add("first");
            list.Add("second");

            Thread.Sleep(50);
            Assert.Empty(calls.Snapshot());
        });

        WaitForIdle();

        var call = Assert.Single(calls.Snapshot());
        Assert.Equal(["Added first 0", "Added second 1"], call.Select(Describe));
    }

    [Fact]
    public async Task The_changes_of_an_async_batch_arrive_together_once_it_completed()
    {
        var list = new ReactiveList<string>();
        var calls = new Calls<ListChange<string>>();
        using var watch = Reactive.Watch(list.Changes, calls.Add);

        await Reactive.BatchAsync(async () =>
        {
            list.Add("first");
            await Task.Delay(10);
            list.Add("second");
        });

        await PendingWork.WhenIdle().WaitAsync(Timeout);

        var call = Assert.Single(calls.Snapshot());
        Assert.Equal(["Added first 0", "Added second 1"], call.Select(Describe));
    }

    [Fact]
    public void A_change_made_outside_a_batch_waits_for_an_earlier_change_of_a_batch_still_open()
    {
        var list = new ReactiveList<string>();
        var calls = new Calls<ListChange<string>>();
        using var watch = Reactive.Watch(list.Changes, calls.Add);

        Reactive.Batch(() =>
        {
            list.Add("batched");

            Task.Run(() => list.Add("outside")).Wait(Timeout);
            WaitForIdle();

            Assert.Empty(calls.Snapshot());

            list.Add("batched later");
        });

        WaitForIdle();

        Assert.Equal(["Added batched 0", "Added outside 1", "Added batched later 2"], calls.All().Select(Describe));
    }

    [Fact]
    public void Changes_before_the_watch_and_after_its_disposal_are_not_delivered()
    {
        var list = new ReactiveList<string>(["a"]);
        list.Add("before");

        var calls = new Calls<ListChange<string>>();
        var watch = Reactive.Watch(list.Changes, calls.Add);

        list.Add("during");
        WaitUntil(() => calls.All().Length == 1);

        watch.Dispose();
        list.Add("after");
        WaitForIdle();

        Assert.False(list.Changes.IsWatched);
        Assert.Equal(["Added during 2"], calls.All().Select(Describe));
    }

    [Fact]
    public void Each_watch_receives_every_change()
    {
        var set = new ReactiveSet<int>();
        var first = new Calls<SetChange<int>>();
        var second = new Calls<SetChange<int>>();
        using var firstWatch = Reactive.Watch(set.Changes, first.Add);
        using var secondWatch = Reactive.Watch(set.Changes, second.Add);

        set.Add(1);
        set.Add(2);
        WaitForIdle();

        Assert.Equal(["Added 1", "Added 2"], first.All().Select(Describe));
        Assert.Equal(["Added 1", "Added 2"], second.All().Select(Describe));
    }

    [Fact]
    public void A_debounced_watch_receives_the_changes_of_the_quiet_period_in_one_call()
    {
        var time = new FakeTimeProvider();
        var list = new ReactiveList<int>();
        var calls = new Calls<ListChange<int>>();
        using var watch = Reactive.Watch(
            list.Changes,
            calls.Add,
            new EffectOptions { Debounce = TimeSpan.FromSeconds(1), TimeProvider = time });

        list.Add(1);
        list.Add(2);
        list.Add(3);
        WaitForIdle(advance: () => time.Advance(TimeSpan.FromSeconds(1)));

        var call = Assert.Single(calls.Snapshot());
        Assert.Equal(["Added 1 0", "Added 2 1", "Added 3 2"], call.Select(Describe));
    }

    [Fact]
    public void A_handler_may_write_the_collection_it_watches_and_receives_its_own_changes_later()
    {
        var list = new ReactiveList<string>();
        var calls = new Calls<ListChange<string>>();
        using var watch = Reactive.Watch(list.Changes, changes =>
        {
            calls.Add(changes);
            if (changes[0].Item == "ping")
            {
                list.Add("pong");
            }
        });

        list.Add("ping");
        WaitUntil(() => calls.Snapshot().Length == 2);
        WaitForIdle();

        Assert.Equal(["Added ping 0", "Added pong 1"], calls.All().Select(Describe));
    }

    [Fact]
    public void Writes_without_a_watch_record_nothing_and_allocate_nothing()
    {
        var neverWatched = new ReactiveList<int>([0]);
        var unwatched = new ReactiveList<int>([0]);
        Reactive.Watch(unwatched.Changes, _ => { }).Dispose();

        WriteIndexer(neverWatched, 0);
        WriteIndexer(unwatched, 0);

        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteIndexer(neverWatched, 1);
        WriteIndexer(unwatched, 1);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);

        var calls = new Calls<ListChange<int>>();
        using var watch = Reactive.Watch(unwatched.Changes, calls.Add);
        unwatched.Add(42);
        WaitForIdle();

        Assert.Equal(["Added 42 1"], calls.All().Select(Describe));

        static void WriteIndexer(ReactiveList<int> list, int offset)
        {
            for (var i = 1; i <= 100; i++)
            {
                list[0] = i + offset * 1000;
            }
        }
    }

    private static void Apply(List<int> replica, ListChange<int> change)
    {
        switch (change.Kind)
        {
            case CollectionChangeKind.Added:
                replica.Insert(change.Index, change.Item);
                break;
            case CollectionChangeKind.Removed:
                Assert.Equal(replica[change.Index], change.Item);
                replica.RemoveAt(change.Index);
                break;
            case CollectionChangeKind.Replaced:
                Assert.Equal(replica[change.Index], change.OldItem);
                replica[change.Index] = change.Item;
                break;
            case CollectionChangeKind.Moved:
                Assert.Equal(replica[change.OldIndex], change.Item);
                replica.RemoveAt(change.OldIndex);
                replica.Insert(change.Index, change.Item);
                break;
            default:
                replica.Clear();
                replica.AddRange(change.Items);
                break;
        }
    }

    private static string Describe<T>(ListChange<T> change) => change.Kind switch
    {
        CollectionChangeKind.Replaced => $"Replaced {change.Item} (was {change.OldItem}) {change.Index}",
        CollectionChangeKind.Moved => $"Moved {change.Item} {change.OldIndex} -> {change.Index}",
        CollectionChangeKind.Reset => $"Reset [{string.Join(", ", change.Items)}]",
        _ => $"{change.Kind} {change.Item} {change.Index}",
    };

    private static string Describe<T>(SetChange<T> change) => $"{change.Kind} {change.Item}";

    private static string Describe<TKey, TValue>(DictionaryChange<TKey, TValue> change) => change.Kind == CollectionChangeKind.Replaced
        ? $"Replaced {change.Key} {change.Value} (was {change.OldValue})"
        : $"{change.Kind} {change.Key} {change.Value}";

    private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, Timeout), "Timed out waiting for the watch.");

    /// <summary>Waits until no effect run is scheduled, delayed or in progress anywhere in the process.</summary>
    private static void WaitForIdle(Action? advance = null)
    {
        advance?.Invoke();

        Assert.True(PendingWork.WhenIdle().Wait(Timeout), "Effects did not settle.");
    }

    /// <summary>The calls a watch received, each with its changes.</summary>
    private sealed class Calls<TChange>
    {
        private readonly List<TChange[]> _calls = [];

        public void Add(IReadOnlyList<TChange> changes)
        {
            Assert.NotEmpty(changes);

            lock (_calls)
            {
                _calls.Add([.. changes]);
            }
        }

        public TChange[][] Snapshot()
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }

        public TChange[] All() => [.. Snapshot().SelectMany(call => call)];
    }
}
