using Wick.Core;

namespace Wick.Sample.WinForms.State;

public sealed class LogState
{
    private const int Capacity = 8;

    public ReactiveList<string> Entries { get; } = [];

    // Insert and RemoveAt are writes only: the effect that calls this does not come to depend on the list.
    public void Add(string entry)
    {
        lock (Entries)
        {
            Entries.Insert(0, $"{DateTime.Now:HH:mm:ss} {entry}");
            if (++_size > Capacity)
            {
                Entries.RemoveAt(Capacity);
                _size = Capacity;
            }
        }
    }

    private int _size;
}
