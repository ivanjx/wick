using Wick.Core;

namespace Wick.Sample.State;

/// <summary>
/// State of the search page. The search itself is an asynchronous effect owned by the page; it reads
/// <see cref="Query"/> and writes the rest.
/// </summary>
[ReactiveState]
public sealed partial class SearchState
{
    private const int MaxLog = 8;

    private int _logged;

    public partial string Query { get; set; } = "";

    public partial IReadOnlyList<string> Results { get; set; } = [];

    public partial bool IsSearching { get; set; }

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

/// <summary>A fake remote API. Shorter queries answer slower, so a stale search would arrive after a newer one.</summary>
public sealed class LanguageCatalog
{
    private static readonly string[] Languages =
    [
        "Ada", "ALGOL", "APL", "Assembly", "AWK", "Bash", "BASIC", "C", "C#", "C++", "Clojure", "COBOL", "Crystal",
        "D", "Dart", "Delphi", "Elixir", "Elm", "Erlang", "F#", "Forth", "Fortran", "Go", "Groovy", "Haskell", "Java",
        "JavaScript", "Julia", "Kotlin", "Lisp", "Lua", "MATLAB", "Nim", "OCaml", "Pascal", "Perl", "PHP", "Prolog",
        "Python", "R", "Racket", "Ruby", "Rust", "Scala", "Scheme", "Smalltalk", "SQL", "Swift", "TypeScript", "Zig",
    ];

    public static TimeSpan LatencyFor(string query) => TimeSpan.FromMilliseconds(300 + 900 / Math.Max(query.Length, 1));

    public async Task<IReadOnlyList<string>> SearchAsync(string query, CancellationToken token)
    {
        await Task.Delay(LatencyFor(query), token);

        return [.. Languages.Where(language => language.Contains(query, StringComparison.OrdinalIgnoreCase))];
    }
}
