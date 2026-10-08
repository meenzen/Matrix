using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Matrix.RustSdk.Generators;

/// <summary>
/// Implements the <c>static partial</c> extension methods marked with <c>[Subscription]</c>: writes a listener that
/// passes the values of the SDK to a channel and an implementation that subscribes with it.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SubscriptionGenerator : IIncrementalGenerator
{
    private const string AttributeName = "Matrix.RustSdk.Subscriptions.SubscriptionAttribute";
    private const string Stream = "global::Matrix.RustSdk.Subscriptions.SubscriptionStream";
    private const string TaskHandle = "Matrix.RustSdk.Bindings.TaskHandle";

    private static readonly SymbolDisplayFormat TypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
        );

    private static readonly DiagnosticDescriptor InvalidDeclaration = new(
        "MRSG001",
        "Invalid subscription declaration",
        "{0}",
        "Matrix.RustSdk",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor MethodNotFound = new(
        "MRSG002",
        "Subscription method not found",
        "'{0}' has no method '{1}' taking a listener and {2} returning a TaskHandle",
        "Matrix.RustSdk",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor ValueTypeMismatch = new(
        "MRSG003",
        "Subscription value type mismatch",
        "'{0}' passes {1} to the listener, the declaration has to return IAsyncEnumerable<{1}>",
        "Matrix.RustSdk",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<Result> results = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            static (node, _) => node is MethodDeclarationSyntax,
            static (attributeContext, cancellationToken) => Generate(attributeContext, cancellationToken)
        );

        context.RegisterSourceOutput(
            results,
            static (output, result) =>
            {
                if (result.Diagnostic is not null)
                {
                    output.ReportDiagnostic(result.Diagnostic);
                }
                if (result.Source is not null)
                {
                    output.AddSource(result.HintName, result.Source);
                }
            }
        );
    }

    private static Result Generate(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        IMethodSymbol declaration = (IMethodSymbol)context.TargetSymbol;
        Location? location = declaration.Locations.FirstOrDefault();
        AttributeData attribute = context.Attributes[0];
        string methodName = (string)attribute.ConstructorArguments[0].Value!;
        int buffer = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Buffer").Value.Value as int? ?? 0;

        // static partial IAsyncEnumerable<T> Name(this Owner owner, extra..., CancellationToken cancellationToken)
        if (
            !declaration.IsPartialDefinition
            || !declaration.IsExtensionMethod
            || declaration.ContainingType.ContainingType is not null
            || declaration.Parameters.Length < 2
            || declaration.Parameters.Last().Type.ToDisplayString() != "System.Threading.CancellationToken"
            || declaration.ReturnType is not INamedTypeSymbol { Arity: 1 } returnType
            || returnType.ConstructedFrom.ToDisplayString() != "System.Collections.Generic.IAsyncEnumerable<T>"
        )
        {
            return Error(
                InvalidDeclaration,
                location,
                "A subscription has to be a static partial extension method of a top level class returning "
                    + "IAsyncEnumerable<T> with a CancellationToken as last parameter"
            );
        }

        ITypeSymbol valueType = returnType.TypeArguments[0];
        ITypeSymbol owner = declaration.Parameters[0].Type;
        ImmutableArray<IParameterSymbol> extra = declaration
            .Parameters.RemoveAt(0)
            .RemoveAt(declaration.Parameters.Length - 2);

        IMethodSymbol? target = null;
        IParameterSymbol? listenerParameter = null;
        foreach (IMethodSymbol candidate in owner.GetMembers(methodName).OfType<IMethodSymbol>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            IParameterSymbol? listener = candidate.Parameters.FirstOrDefault(p =>
                GetListenerMethod(p.Type) is not null
            );
            if (
                listener is not null
                && ReturnsTaskHandle(candidate.ReturnType)
                && candidate.Parameters.Length == extra.Length + 1
                && candidate
                    .Parameters.Where(p => !SymbolEqualityComparer.Default.Equals(p, listener))
                    .Zip(extra, (expected, actual) => expected.Name == actual.Name)
                    .All(matches => matches)
            )
            {
                target = candidate;
                listenerParameter = listener;
                break;
            }
        }

        if (target is null || listenerParameter is null)
        {
            string parameters =
                extra.Length == 0 ? "no other parameters" : string.Join(", ", extra.Select(p => p.Name));
            return Error(MethodNotFound, location, owner.Name, methodName, parameters);
        }

        IMethodSymbol listenerMethod = GetListenerMethod(listenerParameter.Type)!;
        if (!MatchesValueType(listenerMethod, valueType))
        {
            string expected =
                listenerMethod.Parameters.Length == 1
                    ? listenerMethod.Parameters[0].Type.ToDisplayString()
                    : "(" + string.Join(", ", listenerMethod.Parameters.Select(p => p.Type.ToDisplayString())) + ")";
            return Error(ValueTypeMismatch, location, methodName, expected);
        }

        string source = Render(declaration, target, listenerParameter, listenerMethod, valueType, extra, buffer);
        string hintName = $"{declaration.ContainingType.Name}.{declaration.Name}.g.cs";
        return new Result(hintName, source, null);
    }

    private static string Render(
        IMethodSymbol declaration,
        IMethodSymbol target,
        IParameterSymbol listenerParameter,
        IMethodSymbol listenerMethod,
        ITypeSymbol valueType,
        ImmutableArray<IParameterSymbol> extra,
        int buffer
    )
    {
        string value = valueType.ToDisplayString(TypeFormat);
        string listenerClass = declaration.Name + "Listener";
        IParameterSymbol self = declaration.Parameters[0];
        IParameterSymbol cancellationToken = declaration.Parameters.Last();

        IEnumerable<string> declarationParameters = new[] { $"this {Parameter(self)}" }
            .Concat(extra.Select(Parameter))
            .Concat(new[] { Parameter(cancellationToken) });
        IEnumerable<string> arguments = target.Parameters.Select(p =>
            SymbolEqualityComparer.Default.Equals(p, listenerParameter) ? $"new {listenerClass}(writer)" : "@" + p.Name
        );
        string listenerValue =
            listenerMethod.Parameters.Length == 1
                ? "@" + listenerMethod.Parameters[0].Name
                : "(" + string.Join(", ", listenerMethod.Parameters.Select(p => "@" + p.Name)) + ")";

        StringBuilder builder = new();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        if (!declaration.ContainingNamespace.IsGlobalNamespace)
        {
            builder.AppendLine($"namespace {declaration.ContainingNamespace.ToDisplayString()};");
        }
        builder.AppendLine();
        builder.AppendLine($"static partial class {declaration.ContainingType.Name}");
        builder.AppendLine("{");
        builder.AppendLine(
            $"    {Accessibility(declaration)} static partial {declaration.ReturnType.ToDisplayString(TypeFormat)} "
                + $"{declaration.Name}({string.Join(", ", declarationParameters)}) =>"
        );
        builder.AppendLine($"        {Stream}.CreateAsync<{value}>(");
        builder.AppendLine(
            $"            writer => new global::System.Threading.Tasks.ValueTask<global::{TaskHandle}>("
                + $"@{self.Name}.{target.Name}({string.Join(", ", arguments)})),"
        );
        builder.AppendLine($"            (global::Matrix.RustSdk.Subscriptions.SubscriptionBuffer){buffer},");
        builder.AppendLine($"            @{cancellationToken.Name});");
        builder.AppendLine();
        builder.AppendLine(
            $"    private sealed class {listenerClass}(global::System.Threading.Channels.ChannelWriter<{value}> writer)"
        );
        builder.AppendLine($"        : {listenerParameter.Type.ToDisplayString(TypeFormat)}");
        builder.AppendLine("    {");
        builder.AppendLine(
            $"        public void {listenerMethod.Name}("
                + $"{string.Join(", ", listenerMethod.Parameters.Select(Parameter))}) =>"
        );
        builder.AppendLine($"            {Stream}.Write(writer, {listenerValue});");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// The single void method of a callback interface of the bindings, null for every other type.
    /// </summary>
    private static IMethodSymbol? GetListenerMethod(ITypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Interface)
        {
            return null;
        }
        IMethodSymbol[] methods = type.GetMembers().OfType<IMethodSymbol>().ToArray();
        return methods.Length == 1 && methods[0].ReturnsVoid ? methods[0] : null;
    }

    private static bool ReturnsTaskHandle(ITypeSymbol type) =>
        type.ToDisplayString() == TaskHandle
        || type is INamedTypeSymbol { Arity: 1 } task
            && task.ConstructedFrom.ToDisplayString() == "System.Threading.Tasks.Task<TResult>"
            && task.TypeArguments[0].ToDisplayString() == TaskHandle;

    private static bool MatchesValueType(IMethodSymbol listenerMethod, ITypeSymbol valueType)
    {
        ImmutableArray<IParameterSymbol> parameters = listenerMethod.Parameters;
        if (parameters.Length == 1)
        {
            return SymbolEqualityComparer.Default.Equals(parameters[0].Type, valueType);
        }
        return valueType is INamedTypeSymbol { IsTupleType: true } tuple
            && tuple.TupleElements.Length == parameters.Length
            && tuple
                .TupleElements.Zip(parameters, (e, p) => SymbolEqualityComparer.Default.Equals(e.Type, p.Type))
                .All(matches => matches);
    }

    private static string Parameter(IParameterSymbol parameter) =>
        $"{parameter.Type.ToDisplayString(TypeFormat)} @{parameter.Name}";

    private static string Accessibility(IMethodSymbol method) =>
        method.DeclaredAccessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => "public",
            Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
            _ => "private",
        };

    private static Result Error(DiagnosticDescriptor descriptor, Location? location, params object[] arguments) =>
        new("", null, Diagnostic.Create(descriptor, location, arguments));

    // records need IsExternalInit, which netstandard2.0 doesn't have. The equality lets the incremental pipeline skip
    // the output step when an edit doesn't change the generated source.
    private sealed class Result(string hintName, string? source, Diagnostic? diagnostic)
    {
        public string HintName { get; } = hintName;
        public string? Source { get; } = source;
        public Diagnostic? Diagnostic { get; } = diagnostic;

        public override bool Equals(object? obj) =>
            obj is Result other
            && HintName == other.HintName
            && Source == other.Source
            && Equals(Diagnostic, other.Diagnostic);

        public override int GetHashCode() => (HintName.GetHashCode() * 31) ^ (Source?.GetHashCode() ?? 0);
    }
}
