using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Wick.Blazor.Tests.Components;
using Wick.Core;

namespace Wick.Blazor.Tests;

/// <summary>
/// Producers below know nothing about Blazor: they only assign reactive state.
/// Each test checks the final UI, that renders stayed inside the dispatcher, and render coalescing.
/// </summary>
public sealed class ExternalSourceTests : ReactiveTestContext
{
    [Fact]
    public void System_Timers_Timer()
    {
        var cut = Render<CountView>();
        using var done = new ManualResetEventSlim();
        using var timer = new System.Timers.Timer(1) { AutoReset = false };

        timer.Elapsed += (_, _) =>
        {
            State.Count++;

            if (State.Count < 20)
            {
                timer.Start();
            }
            else
            {
                done.Set();
            }
        };
        timer.Start();

        Assert.True(done.Wait(Timeout));
        AssertFinalCount(cut, 20);
    }

    [Fact]
    public void System_Threading_Timer()
    {
        var cut = Render<CountView>();
        using var done = new ManualResetEventSlim();
        Timer? timer = null;

        timer = new Timer(_ =>
        {
            State.Count++;

            if (State.Count < 20)
            {
                timer!.Change(1, System.Threading.Timeout.Infinite);
            }
            else
            {
                done.Set();
            }
        });
        timer.Change(1, System.Threading.Timeout.Infinite);

        Assert.True(done.Wait(Timeout));
        timer.Dispose();
        AssertFinalCount(cut, 20);
    }

    [Fact]
    public async Task PeriodicTimer()
    {
        var cut = Render<CountView>();

        await Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1));

            for (var i = 0; i < 20; i++)
            {
                await timer.WaitForNextTickAsync();
                State.Count++;
            }
        });

        AssertFinalCount(cut, 20);
    }

    [Fact]
    public async Task Task_Run_burst()
    {
        var cut = Render<CountView>();

        await Task.Run(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                State.Count++;
            }
        });

        AssertFinalCount(cut, 10_000);
    }

    [Fact]
    public async Task BackgroundService()
    {
        var cut = Render<CountView>();
        var worker = new CountingWorker(State, iterations: 100);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;

        AssertFinalCount(cut, 100);
    }

    [Fact]
    public void SignalR_style_callbacks_from_concurrent_threads()
    {
        var cut = Render<LabelView>();
        var connection = new FakeHubConnection();

        connection.On(message => State.Label = message);
        Parallel.For(0, 1_000, i => connection.Receive($"message {i}"));

        cut.WaitForAssertion(() => Assert.Equal(State.Label, cut.Find("#label").TextContent), Timeout);
        Settle(cut.Instance);

        Assert.Equal(State.Label, cut.Find("#label").TextContent);
        AssertRenderedOnDispatcherOnly();
        Report(mutations: 1_000);
    }

    [Fact]
    public async Task WebSocket_style_receive_loop()
    {
        var cut = Render<CountView>();

        await Task.Run(async () =>
        {
            await foreach (var value in ReceiveAsync(500))
            {
                State.Count = value;
            }
        });

        AssertFinalCount(cut, 500);

        static async IAsyncEnumerable<int> ReceiveAsync(int count)
        {
            for (var i = 1; i <= count; i++)
            {
                await Task.Yield();
                yield return i;
            }
        }
    }

    [Fact]
    public async Task Channel_reader()
    {
        var cut = Render<CountView>();
        var channel = Channel.CreateUnbounded<int>();

        var reader = Task.Run(async () =>
        {
            await foreach (var value in channel.Reader.ReadAllAsync())
            {
                State.Count = value;
            }
        });

        await Task.Run(async () =>
        {
            for (var i = 1; i <= 1_000; i++)
            {
                await channel.Writer.WriteAsync(i);
            }

            channel.Writer.Complete();
        });
        await reader;

        AssertFinalCount(cut, 1_000);
    }

    [Fact]
    public void Concurrent_producers_on_different_properties()
    {
        var cells = Enumerable.Range(0, 8).Select(_ => new CellState()).ToArray();
        var cut = Render<CellsView>(parameters => parameters.Add(p => p.Cells, cells));

        Parallel.ForEach(cells, new ParallelOptions { MaxDegreeOfParallelism = cells.Length }, cell =>
        {
            for (var i = 1; i <= 1_000; i++)
            {
                cell.Value = i;
            }
        });

        cut.WaitForAssertion(() => Assert.Equal("8000", cut.Find("#sum").TextContent), Timeout);
        Settle(cut.Instance);

        AssertRenderedOnDispatcherOnly();
        Assert.True(Probe.Renders < 8_000);
        Report(mutations: 8_000);
    }

    [Fact]
    public void Mutations_while_the_dispatcher_is_busy_coalesce_into_one_render()
    {
        var cut = Render<CountView>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        // Occupy the dispatcher from another thread (InvokeAsync runs inline on an idle dispatcher).
        var busy = Task.Run(() => Renderer.Dispatcher.InvokeAsync(() =>
        {
            entered.Set();
            release.Wait();
        }));
        Assert.True(entered.Wait(Timeout));

        for (var i = 0; i < 10_000; i++)
        {
            State.Count++;
        }

        release.Set();
        busy.Wait();

        cut.WaitForAssertion(() => Assert.Equal("10000", cut.Find("#count").TextContent), Timeout);
        Settle(cut.Instance);

        Assert.Equal(2, cut.RenderCount);
    }

    [Fact]
    public void Naive_InvokeAsync_StateHasChanged_renders_inline_on_the_producer_thread_once_per_mutation()
    {
        var cell = new ClassicCellState();
        Render<ClassicGrid>(parameters => parameters.Add(p => p.Cells, [cell]));
        var before = Probe.Renders;

        var producer = RunOnDedicatedThread(() =>
        {
            cell.Set(1);
            cell.Set(2);
            cell.Set(3);
        });

        // Grid + cell per mutation, executed synchronously on the producer thread by the idle dispatcher.
        Assert.Equal(before + 6, Probe.Renders);
        Assert.Contains(producer, Probe.RenderThreads);
    }

    [Fact]
    public void Reactive_mutations_never_render_on_the_producer_thread_and_a_batch_renders_once()
    {
        var cut = Render<CountView>();

        var producer = RunOnDedicatedThread(() => Reactive.Batch(() =>
        {
            State.Count = 1;
            State.Count = 2;
            State.Count = 3;
        }));

        cut.WaitForAssertion(() => Assert.Equal("3", cut.Find("#count").TextContent), Timeout);
        Settle(cut.Instance);

        Assert.Equal(2, cut.RenderCount);
        Assert.DoesNotContain(producer, Probe.RenderThreads);
    }

    private void AssertFinalCount(IRenderedComponent<CountView> cut, int expected)
    {
        cut.WaitForAssertion(() => Assert.Equal(expected.ToString(), cut.Find("#count").TextContent), Timeout);
        Settle(cut.Instance);

        Assert.Equal(expected, State.Count);
        AssertRenderedOnDispatcherOnly();
        Assert.True(cut.RenderCount <= expected + 1);
        Report(mutations: expected);
    }

    private void Report(int mutations)
        => Log($"{mutations} mutations -> {Probe.Renders} renders");

    private static int RunOnDedicatedThread(Action action)
    {
        var threadId = 0;
        var thread = new Thread(() =>
        {
            threadId = Environment.CurrentManagedThreadId;
            action();
        });
        thread.Start();
        thread.Join();

        return threadId;
    }

    private sealed class CountingWorker(AppState state, int iterations) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            for (var i = 0; i < iterations && !stoppingToken.IsCancellationRequested; i++)
            {
                state.Count++;
                await Task.Yield();
            }
        }
    }

    private sealed class FakeHubConnection
    {
        private Action<string>? _handler;

        public void On(Action<string> handler) => _handler = handler;

        public void Receive(string message) => _handler?.Invoke(message);
    }
}
