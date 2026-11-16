using Microsoft.CodeAnalysis;

namespace Wick.Generators;

internal static class TypeHelpers
{
    /// <summary>Whether <paramref name="baseType"/> is a base class of <paramref name="type"/> (not the type itself).</summary>
    public static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType))
            {
                return true;
            }
        }

        return false;
    }
}
