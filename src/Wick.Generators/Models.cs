using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Wick.Generators;

/// <summary>Everything needed to emit one reactive state type. Value-equatable so the pipeline caches.</summary>
internal sealed record StateModel(
    string HintName,
    string? Namespace,
    EquatableArray<TypeHeader> ContainingTypes,
    TypeHeader Type,
    bool IsSealed,
    bool HasReactiveBase,
    EquatableArray<PropertyModel> Properties);

/// <summary>A <c>partial</c> type declaration header, e.g. keyword <c>record struct</c> and name <c>Outer&lt;T&gt;</c>.</summary>
internal sealed record TypeHeader(string Keyword, string Name);

/// <param name="Identifier">The property identifier as written (keeps a leading <c>@</c>).</param>
/// <param name="Modifiers">The declaration's modifiers, which the implementation must repeat.</param>
/// <param name="SetKeyword"><c>set</c> or <c>init</c>.</param>
/// <param name="NullableAnnotations">Nullable annotation context at the declaration.</param>
/// <param name="NullableWarnings">Nullable warning context at the declaration.</param>
internal sealed record PropertyModel(
    string Name,
    string Identifier,
    string Type,
    string Modifiers,
    string GetModifiers,
    string SetKeyword,
    string SetModifiers,
    bool NullableAnnotations,
    bool NullableWarnings);

/// <summary>A diagnostic without syntax tree references, so it does not defeat incremental caching.</summary>
internal sealed record DiagnosticInfo(
    DiagnosticDescriptor Descriptor,
    string FilePath,
    TextSpan Span,
    LinePositionSpan LineSpan,
    EquatableArray<string> Arguments)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, Location location, params string[] arguments)
    {
        var lineSpan = location.GetLineSpan();

        return new DiagnosticInfo(descriptor, lineSpan.Path, location.SourceSpan, lineSpan.Span, new EquatableArray<string>([.. arguments]));
    }

    public Diagnostic ToDiagnostic()
        => Diagnostic.Create(Descriptor, Location.Create(FilePath, Span, LineSpan), [.. Arguments]);
}

internal sealed record GenerationResult(StateModel? State, EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>An immutable array with value equality.</summary>
internal readonly struct EquatableArray<T>(ImmutableArray<T> items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _items = items;

    public ImmutableArray<T> Items => _items.IsDefault ? [] : _items;

    public int Length => Items.Length;

    public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in Items)
        {
            hash = unchecked((hash * 31) + item.GetHashCode());
        }

        return hash;
    }

    public ImmutableArray<T>.Enumerator GetEnumerator() => Items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)Items).GetEnumerator();
}
