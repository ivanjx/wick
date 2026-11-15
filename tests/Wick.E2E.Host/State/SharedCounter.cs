using Wick.Core;

namespace Wick.E2E.Host.State;

[ReactiveState]
public sealed partial class SharedCounter
{
    public partial int Count { get; set; }
}
