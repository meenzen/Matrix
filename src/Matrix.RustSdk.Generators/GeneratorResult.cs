using Microsoft.CodeAnalysis;

namespace Matrix.RustSdk.Generators;

/// <summary>
/// The outcome for one declaration. The value equality lets the incremental pipeline skip the output step when an edit
/// doesn't change the generated source. A class because netstandard2.0 has no <c>IsExternalInit</c>.
/// </summary>
internal sealed class GeneratorResult(string hintName, string? source, Diagnostic? diagnostic)
{
    public static readonly GeneratorResult Empty = new("", null, null);

    public string HintName { get; } = hintName;
    public string? Source { get; } = source;
    public Diagnostic? Diagnostic { get; } = diagnostic;

    public static GeneratorResult Error(
        DiagnosticDescriptor descriptor,
        Location? location,
        params object[] arguments
    ) => new("", null, Diagnostic.Create(descriptor, location, arguments));

    public void AddTo(SourceProductionContext output)
    {
        if (Diagnostic is not null)
        {
            output.ReportDiagnostic(Diagnostic);
        }
        if (Source is not null)
        {
            output.AddSource(HintName, Source);
        }
    }

    public override bool Equals(object? obj) =>
        obj is GeneratorResult other
        && HintName == other.HintName
        && Source == other.Source
        && Equals(Diagnostic, other.Diagnostic);

    public override int GetHashCode() => (HintName.GetHashCode() * 31) ^ (Source?.GetHashCode() ?? 0);
}
