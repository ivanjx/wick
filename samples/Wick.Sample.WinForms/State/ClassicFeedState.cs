namespace Wick.Sample.WinForms.State;

/// <summary>
/// Shared state the usual Blazor way: a plain list and an event that components subscribe to. Nothing here is
/// thread-safe, and subscribers run on whichever thread wrote.
/// </summary>
public sealed class ClassicFeedState
{
    public List<Message> Messages { get; } = [];

    public event Action? Changed;

    public void Add(Message message)
    {
        Messages.Add(message);
        Changed?.Invoke();
    }

    public void Clear()
    {
        Messages.Clear();
        Changed?.Invoke();
    }
}
