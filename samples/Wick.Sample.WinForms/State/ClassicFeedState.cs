namespace Wick.Sample.WinForms.State;

/// <summary>
/// Shared state the usual Blazor way: a plain list, a property, and an event that components subscribe to.
/// Nothing here is thread-safe, and subscribers run on whichever thread wrote.
/// </summary>
public sealed class ClassicFeedState
{
    private Message? _latest;

    public List<Message> Messages { get; } = [];

    public Message? Latest
    {
        get => _latest;
        set
        {
            _latest = value;
            Changed?.Invoke();
        }
    }

    public event Action? Changed;

    public void Add(Message message)
    {
        Messages.Add(message);
        Changed?.Invoke();
    }

    public void Clear()
    {
        Messages.Clear();
        Latest = null;
    }
}
