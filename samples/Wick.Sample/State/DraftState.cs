using Wick.Core;

namespace Wick.Sample.State;

/// <summary>
/// State of the listener timing page: a draft, what was last saved, and a preview of it. The page's effects decide
/// when the draft is saved and when the preview is refreshed.
/// </summary>
[ReactiveState]
public sealed partial class DraftState
{
    private const int MaxLog = 8;

    private int _logged;

    public partial string Text { get; set; } = "";

    public partial string SavedText { get; set; } = "";

    public partial string Preview { get; set; } = "";

    public ReactiveList<string> Log { get; } = [];

    /// <summary>Adds a line to the log, newest first. Capped with a plain counter so that it never reads the list.</summary>
    public void AddLog(string entry)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss.f} · {entry}");

        if (Interlocked.Increment(ref _logged) > MaxLog)
        {
            Log.RemoveAt(MaxLog);
        }
    }
}
