using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Matrix.RustSdk.Bindings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Matrix.RustSdk.Generators.Tests;

/// <summary>
/// Runs <see cref="SubscriptionGenerator"/> on a compilation like <c>Matrix.RustSdk</c>: the runtime part of the
/// subscriptions (copied from <c>src/Matrix.RustSdk/Subscriptions</c>), a source with declarations and references to
/// the real bindings. Changes of the bindings show up in these tests as well.
/// </summary>
internal sealed class GeneratorRun
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp13);

    // like ImplicitUsings in Matrix.RustSdk
    private const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path)),
        MetadataReference.CreateFromFile(typeof(Room).Assembly.Location),
    ];

    private static readonly ImmutableArray<SyntaxTree> Runtime =
    [
        CSharpSyntaxTree.ParseText(GlobalUsings, ParseOptions, "GlobalUsings.cs"),
        .. Directory
            .GetFiles(Path.Join(AppContext.BaseDirectory, "Runtime"), "*.cs")
            .Order(StringComparer.Ordinal)
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), ParseOptions, path)),
    ];

    private GeneratorRun(GeneratorDriver driver, Compilation compilation)
    {
        Driver = driver;
        Compilation = compilation;
    }

    /// <summary>
    /// The driver after the run.
    /// </summary>
    public GeneratorDriver Driver { get; }

    /// <summary>
    /// The compilation including the generated sources.
    /// </summary>
    public Compilation Compilation { get; }

    /// <summary>
    /// The diagnostics reported by the generator.
    /// </summary>
    public ImmutableArray<Diagnostic> GeneratorDiagnostics => Driver.GetRunResult().Diagnostics;

    /// <summary>
    /// The errors of the compilation including the generated sources, empty if the generated code compiles.
    /// </summary>
    public IEnumerable<string> CompilationErrors =>
        Compilation
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString());

    /// <summary>
    /// The generated sources ordered by their hint name, followed by the diagnostics of the generator, for snapshots.
    /// </summary>
    public string Render()
    {
        GeneratorDriverRunResult result = Driver.GetRunResult();
        StringBuilder builder = new();
        foreach (
            GeneratedSourceResult source in result
                .Results.SelectMany(r => r.GeneratedSources)
                .OrderBy(s => s.HintName, StringComparer.Ordinal)
        )
        {
            builder.Append("// ----- ").Append(source.HintName).Append(" -----\n");
            builder.Append(source.SourceText.ToString().ReplaceLineEndings("\n"));
            builder.Append('\n');
        }
        foreach (Diagnostic diagnostic in result.Diagnostics.OrderBy(d => d.Location.SourceSpan.Start))
        {
            FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
            builder
                .Append("// ----- ")
                .Append(diagnostic.Id)
                .Append(' ')
                .Append(diagnostic.Severity)
                .Append(" at ")
                .Append(span.StartLinePosition.Line + 1)
                .Append(':')
                .Append(span.StartLinePosition.Character + 1)
                .Append(" -----\n")
                .Append(diagnostic.GetMessage(CultureInfo.InvariantCulture))
                .Append('\n');
        }
        return builder.ToString();
    }

    public static GeneratorRun Run(string source) => Run(CreateCompilation(source));

    public static GeneratorRun Run(Compilation compilation, GeneratorDriver? driver = null)
    {
        driver ??= CSharpGeneratorDriver.Create(
            [new SubscriptionGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions,
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true
            )
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);
        return new GeneratorRun(driver, output);
    }

    public static CSharpCompilation CreateCompilation(string source) =>
        CSharpCompilation.Create(
            "Matrix.RustSdk.Generators.Tests.Compilation",
            [.. Runtime, CSharpSyntaxTree.ParseText(source, ParseOptions, "Declarations.cs")],
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
}
