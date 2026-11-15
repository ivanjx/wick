namespace Wick.Core;

/// <summary>
/// The nodes whose changes invalidated one observer since its last report, distinct and bounded. Only created and
/// filled while <see cref="WickDiagnostics.Enabled"/> is set.
/// </summary>
internal sealed class ChangeCauses
{
    internal const int MaxListed = 8;

    private readonly Lock _gate = new();
    private readonly List<ReactiveNode> _nodes = [];
    private int _omitted;

    public void Add(ReactiveNode node)
    {
        lock (_gate)
        {
            if (_nodes.Contains(node))
            {
                return;
            }

            if (_nodes.Count < MaxListed)
            {
                _nodes.Add(node);
                return;
            }

            _omitted++;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _nodes.Clear();
            _omitted = 0;
        }
    }

    /// <summary>
    /// Describes the recorded changes and starts over: <c>"CounterState.Count, CounterState.Step"</c>, with
    /// <c>" and 3 more"</c> appended past <see cref="MaxListed"/> nodes. Null when nothing was recorded.
    /// </summary>
    public string? Take()
    {
        lock (_gate)
        {
            if (_nodes.Count == 0)
            {
                return null;
            }

            var description = string.Join(", ", _nodes);
            if (_omitted > 0)
            {
                description += $" and {_omitted} more";
            }

            _nodes.Clear();
            _omitted = 0;

            return description;
        }
    }
}
