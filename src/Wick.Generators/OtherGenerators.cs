using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Wick.Generators;

/// <summary>
/// Attributes whose source generators implement the partial property they are applied to. Generators cannot see each
/// other's output, so Wick recognizes these by name and leaves the property to its generator rather than implementing
/// it a second time (which would fail with raw CS0102/CS9251 errors in generated code).
/// </summary>
internal static class OtherGenerators
{
    private static readonly ImmutableHashSet<string> PropertyAttributes =
    [
        "CommunityToolkit.Mvvm.ComponentModel.ObservablePropertyAttribute",
        "CommunityToolkit.WinUI.GeneratedDependencyPropertyAttribute",
        "ReactiveUI.SourceGenerators.ReactiveAttribute",
        "System.Text.RegularExpressions.GeneratedRegexAttribute",
    ];

    /// <summary>The attribute by which another generator implements <paramref name="property"/>, or null.</summary>
    public static AttributeData? FindClaim(IPropertySymbol property)
        => property.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass is not null && PropertyAttributes.Contains(attribute.AttributeClass.ToDisplayString()));

    /// <summary>The attribute as written in source: its name without the <c>Attribute</c> suffix.</summary>
    public static string DisplayName(AttributeData attribute)
    {
        var name = attribute.AttributeClass!.Name;

        return name.EndsWith("Attribute", System.StringComparison.Ordinal) ? name.Substring(0, name.Length - "Attribute".Length) : name;
    }
}
