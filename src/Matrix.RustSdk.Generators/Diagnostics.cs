using Microsoft.CodeAnalysis;

namespace Matrix.RustSdk.Generators;

/// <summary>
/// The errors of the generators. They are reported on the declaration, a declaration with an error isn't implemented,
/// so the compiler reports a missing implementation (CS8795) next to it.
/// </summary>
internal static class Diagnostics
{
    private const string Category = "Matrix.RustSdk";

    public static readonly DiagnosticDescriptor InvalidDeclaration = new(
        "MRSG001",
        "Invalid subscription declaration",
        "Invalid subscription declaration: {0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor MethodNotFound = new(
        "MRSG002",
        "Subscription method not found",
        "'{0}' has no instance method '{1}' returning a TaskHandle that takes a single method listener and {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor ValueTypeMismatch = new(
        "MRSG003",
        "Subscription value type mismatch",
        "The listener of '{0}' receives {1}, the declaration has to return IAsyncEnumerable<{2}>",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor CurrentNotFound = new(
        "MRSG004",
        "Current value method not found",
        "'{0}' has no instance method '{1}' returning {2} or a task of it that takes the extra parameters of the "
            + "subscription",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor InvalidConversion = new(
        "MRSG005",
        "Invalid diff conversion declaration",
        "Invalid diff conversion declaration: {0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor NotADiff = new(
        "MRSG006",
        "Not a diff",
        "'{0}' can't be converted to VectorDiff<{1}>: {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
}
