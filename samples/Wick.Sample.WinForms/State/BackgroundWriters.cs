using System.Collections.Concurrent;

namespace Wick.Sample.WinForms.State;

/// <summary>One message written by a background thread.</summary>
public sealed record Message(int Thread, int Index)
{
    public override string ToString() => $"thread {Thread}: message {Index + 1:N0}";
}

/// <summary>
/// Writes from several dedicated threads at once, the way a device feed, a socket or a timer would. Both thread
/// cards run the same writers; only what the write does differs.
/// </summary>
public static class BackgroundWriters
{
    public const int Threads = 4;

    public const int WritesPerThread = 2_000;

    public const int Total = Threads * WritesPerThread;

    /// <summary>
    /// Calls <paramref name="write"/> <see cref="WritesPerThread"/> times on each of <see cref="Threads"/> new threads.
    /// A write that throws is passed to <paramref name="onError"/> and the thread carries on, so the demo survives
    /// what would otherwise end the process.
    /// </summary>
    public static Task RunAsync(Action<Message> write, Action<Exception> onError)
    {
        var workers = new Task[Threads];

        for (var i = 0; i < Threads; i++)
        {
            workers[i] = Task.Factory.StartNew(() =>
            {
                for (var index = 0; index < WritesPerThread; index++)
                {
                    try
                    {
                        write(new Message(Environment.CurrentManagedThreadId, index));
                    }
                    catch (Exception error)
                    {
                        onError(error);
                    }

                    // Spreads the writes over a second or so, so that renders run while the threads write.
                    if (index % 50 == 49)
                    {
                        Thread.Sleep(1);
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        return Task.WhenAll(workers);
    }
}

/// <summary>Counts exceptions by type and message; safe to add to from any thread.</summary>
public sealed class ErrorTally
{
    private readonly ConcurrentDictionary<string, int> _counts = new();

    public bool IsEmpty => _counts.IsEmpty;

    public IEnumerable<KeyValuePair<string, int>> Entries => _counts.OrderByDescending(entry => entry.Value);

    public void Add(Exception error) =>
        _counts.AddOrUpdate($"{error.GetType().Name}: {error.Message}", 1, static (_, count) => count + 1);

    public void Clear() => _counts.Clear();
}
