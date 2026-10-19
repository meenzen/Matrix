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

    // the conversion of diffs in the generated listener
    private const string ConvertMethod = "ToVectorDiff";

    // the name of the writer in the generated code, reserved so it can't hide or be hidden by a parameter
    private const string Writer = "__writer";

    internal static readonly SymbolDisplayFormat TypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
        );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<GeneratorResult> results = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            static (node, _) => node is MethodDeclarationSyntax,
            static (attributeContext, cancellationToken) => Generate(attributeContext, cancellationToken)
        );

        context.RegisterSourceOutput(results, static (output, result) => result.AddTo(output));
    }

    private static GeneratorResult Generate(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken
    )
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
            return GeneratorResult.Empty;
        }

        if (!IsValidDeclaration(declaration))
        {
            return GeneratorResult.Error(
                Diagnostics.InvalidDeclaration,
                location,
                "a subscription has to be a static partial extension method of a top level, non generic class "
                    + "returning IAsyncEnumerable<T>, with a CancellationToken as last parameter and no parameter named "
                    + Writer
            );
        }

        ITypeSymbol valueType = GetValueType(declaration);
        ITypeSymbol self = declaration.Parameters[0].Type;
        ImmutableArray<IParameterSymbol> extra = GetExtraParameters(declaration);

        if (FindSubscribeMethod(self, methodName, extra, cancellationToken) is not { } subscribe)
        {
            string parameters =
                extra.Length == 0
                    ? "no other parameters"
                    : string.Join(", ", extra.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));
            return GeneratorResult.Error(Diagnostics.MethodNotFound, location, self.Name, methodName, parameters);
        }

        IMethodSymbol listenerMethod = subscribe.ListenerMethod;
        VectorDiffShape? conversion = null;
        if (!MatchesValueType(listenerMethod, valueType))
        {
            conversion = DiffConversion(listenerMethod, valueType, out string received, out string expected);
            if (conversion is null)
            {
                return GeneratorResult.Error(Diagnostics.ValueTypeMismatch, location, methodName, received, expected);
            }
        }

        IMethodSymbol? current = null;
        if (NamedArgument(attribute, "Current") is string currentName)
        {
            current = FindCurrentMethod(self, currentName, extra, valueType);
            if (current is null)
            {
                return GeneratorResult.Error(
                    Diagnostics.CurrentNotFound,
                    location,
                    self.Name,
                    currentName,
                    valueType.ToDisplayString()
                );
            }
        }

        // overloads need distinct file and listener names
        string suffix = OverloadSuffix(declaration);
        Subscription subscription = new(
            declaration,
            subscribe,
            BufferExpression(bufferType, bufferValue),
            current,
            NamedArgument(attribute, "ThrowWhenFinished") is true,
            conversion,
            suffix
        );
        string hintName = $"{declaration.ContainingType.ToDisplayString()}.{declaration.Name}{suffix}.g.cs";
        return new GeneratorResult(hintName, Render(subscription), null);
    }

    /// <summary>
    /// <c>static partial IAsyncEnumerable&lt;T&gt; Name(this Owner owner, extra..., CancellationToken cancellationToken)</c>
    /// in a top level, non generic class.
    /// </summary>
    private static bool IsValidDeclaration(IMethodSymbol declaration) =>
        declaration.IsPartialDefinition
        && declaration.IsExtensionMethod
        && !declaration.IsGenericMethod
        && declaration.ContainingType.ContainingType is null
        && !declaration.ContainingType.IsGenericType
        && declaration.Parameters.Length >= 2
        && declaration.Parameters.Last().Type.ToDisplayString() == "System.Threading.CancellationToken"
        && declaration.Parameters.All(p => p.RefKind == RefKind.None && !p.IsParams && p.Name != Writer)
        && declaration.ReturnType is INamedTypeSymbol { Arity: 1 } returnType
        && returnType.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.IAsyncEnumerable<T>";

    /// <summary>
    /// The <c>T</c> of the <c>IAsyncEnumerable&lt;T&gt;</c> returned by a valid declaration.
    /// </summary>
    private static ITypeSymbol GetValueType(IMethodSymbol declaration) =>
        ((INamedTypeSymbol)declaration.ReturnType).TypeArguments[0];

    /// <summary>
    /// The parameters of a valid declaration between the extended type and the cancellation token.
    /// </summary>
    private static ImmutableArray<IParameterSymbol> GetExtraParameters(IMethodSymbol declaration) =>
        declaration.Parameters.RemoveAt(0).RemoveAt(declaration.Parameters.Length - 2);

    /// <summary>
    /// The buffer as C# expression, the name of the enum member if there is one.
    /// </summary>
    private static string BufferExpression(INamedTypeSymbol bufferType, object bufferValue) =>
        bufferType
            .GetMembers()
            .OfType<IFieldSymbol>()
            .Where(field => field.HasConstantValue && Equals(field.ConstantValue, bufferValue))
            .Select(field => $"{bufferType.ToDisplayString(TypeFormat)}.{field.Name}")
            .DefaultIfEmpty($"({bufferType.ToDisplayString(TypeFormat)}){bufferValue}")
            .First();

    /// <summary>
    /// The instance method <paramref name="name"/> of <paramref name="type"/> that returns a <c>TaskHandle</c>, takes a
    /// listener and otherwise the <paramref name="extra"/> parameters, null if there is none.
    /// </summary>
    private static SubscribeMethod? FindSubscribeMethod(
        ITypeSymbol type,
        string name,
        ImmutableArray<IParameterSymbol> extra,
        CancellationToken cancellationToken
    )
    {
        foreach (IMethodSymbol candidate in type.GetMembers(name).OfType<IMethodSymbol>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AsSubscribeMethod(candidate) is { } subscribe && ParametersMatch(candidate, subscribe.Listener, extra))
            {
                return subscribe;
            }
        }
        return null;
    }

    /// <summary>
    /// <paramref name="method"/> with its listener if it is an instance method returning a <c>TaskHandle</c> that takes
    /// a listener, null otherwise.
    /// </summary>
    private static SubscribeMethod? AsSubscribeMethod(IMethodSymbol method)
    {
        if (method.IsStatic || !ReturnsTaskHandle(method.ReturnType))
        {
            return null;
        }
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (GetListenerMethod(parameter.Type) is { } listenerMethod)
            {
                return new SubscribeMethod(method, parameter, listenerMethod);
            }
        }
        return null;
    }

    /// <summary>
    /// The instance method <paramref name="name"/> of <paramref name="type"/> that takes the <paramref name="extra"/>
    /// parameters and returns the value type or a task of it, null if there is none.
    /// </summary>
    private static IMethodSymbol? FindCurrentMethod(
        ITypeSymbol type,
        string name,
        ImmutableArray<IParameterSymbol> extra,
        ITypeSymbol valueType
    ) =>
        type.GetMembers(name)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(candidate =>
                !candidate.IsStatic
                && ParametersMatch(candidate.Parameters.ToArray(), extra)
                && ReturnsValue(candidate.ReturnType, valueType)
            );

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
            subscription.Subscribe.Method.Parameters.Select(p =>
                SymbolEqualityComparer.Default.Equals(p, subscription.Subscribe.Listener)
                    ? $"new {listenerClass}({Writer})"
                    : Identifier(p)
            )
        );
        IMethodSymbol listenerMethod = subscription.Subscribe.ListenerMethod;
        string written =
            listenerMethod.Parameters.Length == 1
                ? Identifier(listenerMethod.Parameters[0])
                : "(" + string.Join(", ", listenerMethod.Parameters.Select(Identifier)) + ")";
        if (subscription.Conversion is not null)
        {
            written = $"global::System.Array.ConvertAll({written}, {ConvertMethod})";
        }

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
        code.Line(")");
        code.Open();
        // the enumeration is deferred, null arguments are reported right away like in a hand written method
        foreach (IParameterSymbol parameter in new[] { self }.Concat(subscription.Extra).Where(IsNonNullableReference))
        {
            code.Line(
                $"global::System.ArgumentNullException.ThrowIfNull({Identifier(parameter)}, nameof({Identifier(parameter)}));"
            );
        }
        code.Line($"return {Stream}.CreateAsync<{value}, global::{TaskHandle}>(");
        code.Indent();
        code.Line($"{Writer} => new global::System.Threading.Tasks.ValueTask<global::{TaskHandle}>(");
        code.Indented(new[] { $"{Identifier(self)}.{subscription.Subscribe.Method.Name}({arguments})" });
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
        code.Close();
        code.Line();

        code.Line($"private sealed class {listenerClass}(");
        code.Indented(new[] { $"global::Matrix.RustSdk.Subscriptions.SubscriptionWriter<{value}> {Writer}" });
        code.Line($") : {subscription.Subscribe.Listener.Type.ToDisplayString(TypeFormat)}");
        code.Open();
        code.Line(
            $"public void {listenerMethod.Name}({string.Join(", ", listenerMethod.Parameters.Select(Parameter))}) =>"
        );
        code.Indented(new[] { $"{Writer}.Write({written});" });
        if (subscription.Conversion is { } shape)
        {
            code.Line();
            code.Line(
                $"private static global::Matrix.RustSdk.VectorDiff<{shape.Item.ToDisplayString(TypeFormat)}> "
                    + $"{ConvertMethod}({shape.Diff.ToDisplayString(TypeFormat)} diff) =>"
            );
            code.Indent();
            foreach (string line in shape.RenderSwitch("diff", TypeFormat))
            {
                code.Line(line);
            }
            code.Outdent();
            // the switch expression ends the expression body
            code.AppendToLastLine(";");
        }
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

    /// <summary>
    /// The diff enum the listener receives an array of, if the declaration yields arrays of the matching
    /// <c>VectorDiff&lt;T&gt;</c>. Otherwise null, what the listener receives and the value type the declaration has to
    /// return.
    /// </summary>
    private static VectorDiffShape? DiffConversion(
        IMethodSymbol listenerMethod,
        ITypeSymbol valueType,
        out string received,
        out string expected
    )
    {
        ImmutableArray<IParameterSymbol> parameters = listenerMethod.Parameters;
        received =
            parameters.Length == 1
                ? parameters[0].Type.ToDisplayString()
                : "(" + string.Join(", ", parameters.Select(p => p.Type.ToDisplayString())) + ")";
        expected = received;
        if (parameters.Length != 1)
        {
            return null;
        }
        if (
            parameters[0].Type is not IArrayTypeSymbol { ElementType: { } element }
            || VectorDiffShape.Analyze(element, out _) is not { } shape
        )
        {
            return null;
        }
        // diffs are yielded as VectorDiff<T>, that's what the error recommends
        expected = $"Matrix.RustSdk.VectorDiff<{shape.Item.ToDisplayString()}>[]";
        return
            valueType is IArrayTypeSymbol { ElementType: { } yielded }
            && VectorDiffShape.VectorDiffItem(yielded) is { } item
            && SymbolEqualityComparer.IncludeNullability.Equals(item, shape.Item)
            ? shape
            : null;
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

    internal static bool IsNonNullableReference(IParameterSymbol parameter) =>
        parameter.Type.IsReferenceType && parameter.NullableAnnotation != NullableAnnotation.Annotated;

    // the bindings use keywords as parameter names (@event), escaping every identifier is always valid
    internal static string Identifier(IParameterSymbol parameter) => "@" + parameter.Name;

    internal static string Parameter(IParameterSymbol parameter) =>
        $"{parameter.Type.ToDisplayString(TypeFormat)} {Identifier(parameter)}";

    internal static string Accessibility(IMethodSymbol method) =>
        method.DeclaredAccessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => "public",
            Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
            _ => "private",
        };

    /// <summary>
    /// A subscription method of the bindings and its listener parameter.
    /// </summary>
    private sealed class SubscribeMethod(IMethodSymbol method, IParameterSymbol listener, IMethodSymbol listenerMethod)
    {
        public IMethodSymbol Method { get; } = method;

        public IParameterSymbol Listener { get; } = listener;

        /// <summary>
        /// The single method of the listener interface.
        /// </summary>
        public IMethodSymbol ListenerMethod { get; } = listenerMethod;
    }

    /// <summary>
    /// A valid declaration and the members of the bindings it uses.
    /// </summary>
    private sealed class Subscription(
        IMethodSymbol declaration,
        SubscribeMethod subscribe,
        string buffer,
        IMethodSymbol? current,
        bool throwWhenFinished,
        VectorDiffShape? conversion,
        string overloadSuffix
    )
    {
        public IMethodSymbol Declaration { get; } = declaration;

        /// <summary>
        /// The parameters between the extended type and the cancellation token, passed through by name.
        /// </summary>
        public ImmutableArray<IParameterSymbol> Extra { get; } = GetExtraParameters(declaration);

        public ITypeSymbol ValueType { get; } = GetValueType(declaration);

        /// <summary>
        /// The subscription method of the bindings.
        /// </summary>
        public SubscribeMethod Subscribe { get; } = subscribe;

        /// <summary>
        /// The buffer as C# expression.
        /// </summary>
        public string Buffer { get; } = buffer;

        /// <summary>
        /// The method returning the current value, if the declaration names one.
        /// </summary>
        public IMethodSymbol? Current { get; } = current;

        public bool ThrowWhenFinished { get; } = throwWhenFinished;

        /// <summary>
        /// The diff enum the listener receives, if the values are converted to <c>VectorDiff&lt;T&gt;</c>.
        /// </summary>
        public VectorDiffShape? Conversion { get; } = conversion;

        /// <summary>
        /// The name of the generated listener. It keeps the Async suffix of the method, without it AccountDataAsync
        /// would get a nested AccountDataListener hiding the listener interface of the bindings.
        /// </summary>
        public string ListenerClass { get; } = declaration.Name + "Listener" + overloadSuffix;
    }
}
