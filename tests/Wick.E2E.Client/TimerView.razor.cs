using Wick.Core;

namespace Wick.E2E.Client;

/// <summary>Code-behind reactive state written by a .NET timer, off the dispatcher on the server.</summary>
[ReactiveState]
public partial class TimerView
{
    private Timer? _timer;

    public partial int Ticks { get; set; }

    protected override void OnInitialized() => _timer = new Timer(_ => Ticks++, null, 20, 20);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer?.Dispose();
        }

        base.Dispose(disposing);
    }
}
