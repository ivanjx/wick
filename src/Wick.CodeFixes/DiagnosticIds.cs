namespace Wick.CodeFixes;

/// <summary>The IDs of the Wick.Generators diagnostics these fixes handle (see <c>Diagnostics.cs</c> there).</summary>
internal static class DiagnosticIds
{
    public const string TypeMustBePartial = "WK0001";

    public const string AutoPropertyIsNotReactive = "WK0002";

    public const string MutableCollectionProperty = "WK0006";

    public const string DisposalBypassesReactiveCleanup = "WK0007";
}
