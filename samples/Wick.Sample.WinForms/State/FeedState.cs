using Wick.Core;

namespace Wick.Sample.WinForms.State;

/// <summary>
/// The same feed with Wick: no event, no subscribers. Components that read the list re-render when it changes,
/// whichever thread wrote.
/// </summary>
public sealed class FeedState
{
    public ReactiveList<Message> Messages { get; } = [];
}
