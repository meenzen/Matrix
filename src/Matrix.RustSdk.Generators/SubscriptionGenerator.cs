using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Matrix.RustSdk.Generators;

/// <summary>
/// Implements the <c>static partial</c> extension methods marked with <c>[Subscription]</c>: writes a listener that
/// passes the values of the SDK to a channel and an implementation that subscribes with it, see
/// <c>Matrix.RustSdk.Subscriptions.SubscriptionStream</c>.
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
        if (
            attribute.ConstructorArguments.Length != 2
            || attribute.ConstructorArguments[0].Value is not string methodName
            || attribute.ConstructorArguments[1] is not { Type: INamedTypeSymbol bufferType, Value: { } bufferValue }
        )
        {
            // the compiler reports the invalid attribute usage
            return Result.Empty;
        }
        string buffer = bufferType
            .GetMembers()
            .OfType<IFieldSymbol>()
            .Where(field => field.HasConstantValue && Equals(field.ConstantValue, bufferValue))
            .Select(field => $"{bufferType.ToDisplayString(TypeFormat)}.{field.Name}")
            .DefaultIfEmpty($"({bufferType.ToDisplayString(TypeFormat)}){bufferValue}")
            .First();

        // static partial IAsyncEnumerable<T> Name(this Owner owner, extra..., CancellationToken cancellationToken)
        if (
            !declaration.IsPartialDefinition
            || !declaration.IsExtensionMethod
            || declaration.IsGenericMethod
            || declaration.ContainingType.ContainingType is not null
            || declaration.ContainingType.IsGenericType
            || declaration.Parameters.Length < 2
            || declaration.Parameters.Last().Type.ToDisplayString() != "System.Threading.CancellationToken"
            || declaration.Parameters.Any(p => p.RefKind != RefKind.None || p.IsParams)
            || declaration.ReturnType is not INamedTypeSymbol { Arity: 1 } returnType
            || returnType.ConstructedFrom.ToDisplayString() != "System.Collections.Generic.IAsyncEnumerable<T>"
        )
        {
            return Result.Error(
                Diagnostics.InvalidDeclaration,
                location,
                "a subscription has to be a static partial extension method of a top level, non generic class "
                    + "returning IAsyncEnumerable<T>, with a CancellationToken as last parameter"
            );
        }

        ITypeSymbol valueType = returnType.TypeArguments[0];
        IParameterSymbol self = declaration.Parameters[0];
        ImmutableArray<IParameterSymbol> extra = declaration
            .Parameters.RemoveAt(0)
            .RemoveAt(declaration.Parameters.Length - 2);

        IMethodSymbol? target = null;
        IParameterSymbol? listener = null;
        foreach (IMethodSymbol candidate in self.Type.GetMembers(methodName).OfType<IMethodSymbol>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            IParameterSymbol? candidateListener = candidate.Parameters.FirstOrDefault(p =>
                GetListenerMethod(p.Type) is not null
            );
            if (
                candidateListener is not null
                && !candidate.IsStatic
                && ReturnsTaskHandle(candidate.ReturnType)
                && ParametersMatch(candidate, candidateListener, extra)
            )
            {
                target = candidate;
                listener = candidateListener;
                break;
            }
        }

        if (target is null || listener is null)
        {
            string parameters =
                extra.Length == 0
                    ? "no other parameters"
                    : string.Join(", ", extra.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));
            return Result.Error(Diagnostics.MethodNotFound, location, self.Type.Name, methodName, parameters);
        }

        IMethodSymbol listenerMethod = GetListenerMethod(listener.Type)!;
        if (!MatchesValueType(listenerMethod, valueType))
        {
            string expected =
                listenerMethod.Parameters.Length == 1
                    ? listenerMethod.Parameters[0].Type.ToDisplayString()
                    : "(" + string.Join(", ", listenerMethod.Parameters.Select(p => p.Type.ToDisplayString())) + ")";
            return Result.Error(Diagnostics.ValueTypeMismatch, location, methodName, expected);
        }

        IMethodSymbol? current = null;
        if (NamedArgument(attribute, "Current") is string currentName)
        {
            current = self
                .Type.GetMembers(currentName)
                .OfType<IMethodSymbol>()
                .FirstOrDefault(candidate =>
                    !candidate.IsStatic
                    && ParametersMatch(candidate.Parameters.ToArray(), extra)
                    && ReturnsValue(candidate.ReturnType, valueType)
                );
            if (current is null)
            {
                return Result.Error(
                    Diagnostics.CurrentNotFound,
                    location,
                    self.Type.Name,
                    currentName,
                    valueType.ToDisplayString()
                );
            }
        }

        // overloads need distinct file and listener names. The listener keeps the Async suffix of the method, without it
        // AccountDataAsync would get a nested AccountDataListener hiding the listener interface of the bindings.
        string suffix = OverloadSuffix(declaration);
        Subscription subscription = new(
            declaration,
            extra,
            valueType,
            target,
            listener,
            listenerMethod,
            buffer,
            current,
            NamedArgument(attribute, "ThrowWhenFinished") is true,
            declaration.Name + "Listener" + suffix
        );
        string hintName = $"{declaration.ContainingType.ToDisplayString()}.{declaration.Name}{suffix}.g.cs";
        return new Result(hintName, Render(subscription), null);
    }

    private static string Render(Subscription subscription)
    {
        IMethodSymbol declaration = subscription.Declaration;
        string value = subscription.ValueType.ToDisplayString(TypeFormat);
        string listenerClass = subscription.ListenerClass;
        IParameterSymbol self = declaration.Parameters[0];
        IParameterSymbol cancellationToken = declaration.Parameters.Last();
        string[] parameters = new[] { $"this {Parameter(self)}" }
            .Concat(subscription.Extra.Select(Parameter))
            .Concat(new[] { Parameter(cancellationToken) })
            .ToArray();
        string arguments = string.Join(
            ", ",
            subscription.Target.Parameters.Select(p =>
                SymbolEqualityComparer.Default.Equals(p, subscription.Listener)
                    ? $"new {listenerClass}(writer)"
                    : Identifier(p)
            )
        );
        IMethodSymbol listenerMethod = subscription.ListenerMethod;
        string written =
            listenerMethod.Parameters.Length == 1
                ? Identifier(listenerMethod.Parameters[0])
                : "(" + string.Join(", ", listenerMethod.Parameters.Select(Identifier)) + ")";

        CodeWriter code = new();
        code.Line("// <auto-generated/>");
        code.Line("#nullable enable");
        code.Line();
        if (!declaration.ContainingNamespace.IsGlobalNamespace)
        {
            code.Line($"namespace {declaration.ContainingNamespace.ToDisplayString()};");
            code.Line();
        }
        code.Line($"static partial class {declaration.ContainingType.Name}");
        code.Open();

        code.Line(
            $"{Accessibility(declaration)} static partial {declaration.ReturnType.ToDisplayString(TypeFormat)} "
                + $"{declaration.Name}("
        );
        code.Indented(parameters.Select((p, i) => i < parameters.Length - 1 ? p + "," : p));
        code.Line(") =>");
        code.Indent();
        code.Line($"{Stream}.CreateAsync<{value}, global::{TaskHandle}>(");
        code.Indent();
        code.Line($"writer => new global::System.Threading.Tasks.ValueTask<global::{TaskHandle}>(");
        code.Indented(new[] { $"{Identifier(self)}.{subscription.Target.Name}({arguments})" });
        code.Line("),");
        code.Line($"{subscription.Buffer},");
        if (subscription.Current is { } current)
        {
            string currentArguments = string.Join(", ", subscription.Extra.Select(Identifier));
            code.Line($"current: () => new global::System.Threading.Tasks.ValueTask<{value}>(");
            code.Indented(new[] { $"{Identifier(self)}.{current.Name}({currentArguments})" });
            code.Line("),");
        }
        if (subscription.ThrowWhenFinished)
        {
            code.Line("throwWhenFinished: true,");
        }
        code.Line($"cancellationToken: {Identifier(cancellationToken)}");
        code.Outdent();
        code.Line(");");
        code.Outdent();
        code.Line();

        code.Line($"private sealed class {listenerClass}(");
        code.Indented(new[] { $"global::Matrix.RustSdk.Subscriptions.SubscriptionWriter<{value}> writer" });
        code.Line($") : {subscription.Listener.Type.ToDisplayString(TypeFormat)}");
        code.Open();
        code.Line(
            $"public void {listenerMethod.Name}({string.Join(", ", listenerMethod.Parameters.Select(Parameter))}) =>"
        );
        code.Indented(new[] { $"writer.Write({written});" });
        code.Close();

        code.Close();
        return code.ToString();
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
        IsTaskHandle(type)
        || type is INamedTypeSymbol { Arity: 1 } task
            && task.ConstructedFrom.ToDisplayString() == "System.Threading.Tasks.Task<TResult>"
            && IsTaskHandle(task.TypeArguments[0]);

    // a TaskHandle? means the method may not subscribe at all, those aren't supported
    private static bool IsTaskHandle(ITypeSymbol type) =>
        type.NullableAnnotation != NullableAnnotation.Annotated && type.ToDisplayString() == TaskHandle;

    /// <summary>
    /// Whether the parameters of <paramref name="candidate"/> except the listener are the extra parameters of the
    /// declaration, in the same order with the same names and types.
    /// </summary>
    private static bool ParametersMatch(
        IMethodSymbol candidate,
        IParameterSymbol listener,
        ImmutableArray<IParameterSymbol> extra
    ) =>
        ParametersMatch(
            candidate.Parameters.Where(p => !SymbolEqualityComparer.Default.Equals(p, listener)).ToArray(),
            extra
        );

    private static bool ParametersMatch(IParameterSymbol[] expected, ImmutableArray<IParameterSymbol> extra) =>
        expected.Length == extra.Length
        && expected
            .Zip(extra, (e, a) => e.Name == a.Name && SymbolEqualityComparer.IncludeNullability.Equals(e.Type, a.Type))
            .All(matches => matches);

    /// <summary>
    /// Whether <paramref name="type"/> is <paramref name="valueType"/> or a task of it.
    /// </summary>
    private static bool ReturnsValue(ITypeSymbol type, ITypeSymbol valueType) =>
        SymbolEqualityComparer.IncludeNullability.Equals(type, valueType)
        || type is INamedTypeSymbol { Arity: 1 } task
            && task.ConstructedFrom.ToDisplayString() == "System.Threading.Tasks.Task<TResult>"
            && SymbolEqualityComparer.IncludeNullability.Equals(task.TypeArguments[0], valueType);

    private static object? NamedArgument(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name).Value.Value;

    private static bool MatchesValueType(IMethodSymbol listenerMethod, ITypeSymbol valueType)
    {
        ImmutableArray<IParameterSymbol> parameters = listenerMethod.Parameters;
        if (parameters.Length == 1)
        {
            return SymbolEqualityComparer.IncludeNullability.Equals(parameters[0].Type, valueType);
        }
        return valueType is INamedTypeSymbol { IsTupleType: true } tuple
            && tuple.TupleElements.Length == parameters.Length
            && tuple
                .TupleElements.Zip(
                    parameters,
                    (e, p) => SymbolEqualityComparer.IncludeNullability.Equals(e.Type, p.Type)
                )
                .All(matches => matches);
    }

    private static string OverloadSuffix(IMethodSymbol declaration)
    {
        IMethodSymbol[] overloads = declaration
            .ContainingType.GetMembers(declaration.Name)
            .OfType<IMethodSymbol>()
            .Where(m => m.IsPartialDefinition)
            .ToArray();
        if (overloads.Length < 2)
        {
            return "";
        }
        int index = overloads.TakeWhile(m => !SymbolEqualityComparer.Default.Equals(m, declaration)).Count();
        return (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // the bindings use keywords as parameter names (@event), escaping every identifier is always valid
    private static string Identifier(IParameterSymbol parameter) => "@" + parameter.Name;

    private static string Parameter(IParameterSymbol parameter) =>
        $"{parameter.Type.ToDisplayString(TypeFormat)} {Identifier(parameter)}";

    private static string Accessibility(IMethodSymbol method) =>
        method.DeclaredAccessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => "public",
            Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
            _ => "private",
        };

    /// <summary>
    /// A valid declaration and the members of the bindings it uses.
    /// </summary>
    private sealed class Subscription(
        IMethodSymbol declaration,
        ImmutableArray<IParameterSymbol> extra,
        ITypeSymbol valueType,
        IMethodSymbol target,
        IParameterSymbol listener,
        IMethodSymbol listenerMethod,
        string buffer,
        IMethodSymbol? current,
        bool throwWhenFinished,
        string listenerClass
    )
    {
        public IMethodSymbol Declaration { get; } = declaration;

        /// <summary>
        /// The parameters between the extended type and the cancellation token, passed through by name.
        /// </summary>
        public ImmutableArray<IParameterSymbol> Extra { get; } = extra;

        public ITypeSymbol ValueType { get; } = valueType;

        /// <summary>
        /// The subscription method of the bindings.
        /// </summary>
        public IMethodSymbol Target { get; } = target;

        public IParameterSymbol Listener { get; } = listener;

        public IMethodSymbol ListenerMethod { get; } = listenerMethod;

        /// <summary>
        /// The buffer as C# expression.
        /// </summary>
        public string Buffer { get; } = buffer;

        /// <summary>
        /// The method returning the current value, if the declaration names one.
        /// </summary>
        public IMethodSymbol? Current { get; } = current;

        public bool ThrowWhenFinished { get; } = throwWhenFinished;

        public string ListenerClass { get; } = listenerClass;
    }

    /// <summary>
    /// The outcome for one declaration. The value equality lets the incremental pipeline skip the output step when an
    /// edit doesn't change the generated source. A class because netstandard2.0 has no <c>IsExternalInit</c>.
    /// </summary>
    private sealed class Result(string hintName, string? source, Diagnostic? diagnostic)
    {
        public static readonly Result Empty = new("", null, null);

        public string HintName { get; } = hintName;
        public string? Source { get; } = source;
        public Diagnostic? Diagnostic { get; } = diagnostic;

        public static Result Error(DiagnosticDescriptor descriptor, Location? location, params object[] arguments) =>
            new("", null, Diagnostic.Create(descriptor, location, arguments));

        public override bool Equals(object? obj) =>
            obj is Result other
            && HintName == other.HintName
            && Source == other.Source
            && Equals(Diagnostic, other.Diagnostic);

        public override int GetHashCode() => (HintName.GetHashCode() * 31) ^ (Source?.GetHashCode() ?? 0);
    }
}
