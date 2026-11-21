using Wick.Core;

namespace Wick.Sample.WinForms.State;

/// <summary>
/// The same feed with Wick: no event, no subscribers. Components that read <see cref="Latest"/> or the list
/// re-render when it changes, whichever thread wrote.
/// </summary>
[ReactiveState]
public sealed partial class FeedState
{
    public partial Message? Latest { get; set; }

    public ReactiveList<Message> Messages { get; } = [];
}
