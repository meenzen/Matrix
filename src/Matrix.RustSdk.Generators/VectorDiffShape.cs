using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Matrix.RustSdk.Generators;

/// <summary>
/// A diff enum of the bindings (<c>TimelineDiff</c>, <c>RoomListEntriesUpdate</c>, ...): a record with exactly the
/// nested variants of eyeball's <c>VectorDiff</c>, which map one to one to <c>Matrix.RustSdk.VectorDiff&lt;T&gt;</c>.
/// </summary>
internal sealed class VectorDiffShape
{
    public const string VectorDiff = "Matrix.RustSdk.VectorDiff<T>";

    private const string Values = "Values";
    private const string Value = "Value";
    private const string Index = "Index";
    private const string Length = "Length";

    /// <summary>
    /// The variants with their positional properties, in the order of eyeball.
    /// </summary>
    private static readonly (string Name, string[] Properties)[] Variants =
    {
        ("Append", new[] { Values }),
        ("Clear", new string[0]),
        ("PushFront", new[] { Value }),
        ("PushBack", new[] { Value }),
        ("PopFront", new string[0]),
        ("PopBack", new string[0]),
        ("Insert", new[] { Index, Value }),
        ("Set", new[] { Index, Value }),
        ("Remove", new[] { Index }),
        ("Truncate", new[] { Length }),
        ("Reset", new[] { Values }),
    };

    private VectorDiffShape(INamedTypeSymbol diff, ITypeSymbol item)
    {
        Diff = diff;
        Item = item;
    }

    /// <summary>
    /// The diff enum of the bindings.
    /// </summary>
    public INamedTypeSymbol Diff { get; }

    /// <summary>
    /// The type of the items, the <c>T</c> of <c>VectorDiff&lt;T&gt;</c>.
    /// </summary>
    public ITypeSymbol Item { get; }

    /// <summary>
    /// The shape of <paramref name="type"/>, or null and the reason why it isn't a diff enum.
    /// </summary>
    public static VectorDiffShape? Analyze(ITypeSymbol type, out string? error)
    {
        error = null;
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Class } diff)
        {
            error = "it isn't a class";
            return null;
        }

        INamedTypeSymbol[] nested = diff.GetTypeMembers()
            .Where(t => SymbolEqualityComparer.Default.Equals(t.BaseType, diff))
            .ToArray();
        string[] unknown = nested.Select(t => t.Name).Except(Variants.Select(v => v.Name)).ToArray();
        string[] missing = Variants.Select(v => v.Name).Except(nested.Select(t => t.Name)).ToArray();
        if (unknown.Length > 0 || missing.Length > 0)
        {
            error =
                missing.Length > 0
                    ? "it has no variant " + string.Join(", ", missing)
                    : "it has the unknown variant " + string.Join(", ", unknown);
            return null;
        }

        // the item type is the element type of Append.Values, the other variants have to agree with it
        ITypeSymbol? item = (Property(nested, "Append", Values)?.Type as IArrayTypeSymbol)?.ElementType;
        if (item is null)
        {
            error = "Append has no array property Values";
            return null;
        }

        foreach ((string name, string[] properties) in Variants)
        {
            INamedTypeSymbol variant = nested.First(t => t.Name == name);
            bool matches = variant.InstanceConstructors.Any(constructor =>
                constructor.DeclaredAccessibility == Accessibility.Public
                && constructor.Parameters.Length == properties.Length
                && constructor.Parameters.Zip(properties, (p, e) => Matches(p.Type, e, item)).All(m => m)
                && properties.All(p => Property(nested, name, p) is not null)
            );
            if (!matches)
            {
                string parameters = string.Join(", ", properties.Select(p => $"{Describe(p, item)} {p}"));
                error = $"its variant {name} isn't {name}({parameters})";
                return null;
            }
        }

        return new VectorDiffShape(diff, item);
    }

    /// <summary>
    /// The <c>T</c> of <c>VectorDiff&lt;T&gt;</c>, null if <paramref name="type"/> isn't one.
    /// </summary>
    public static ITypeSymbol? VectorDiffItem(ITypeSymbol type) =>
        type is INamedTypeSymbol { Arity: 1 } named && named.ConstructedFrom.ToDisplayString() == VectorDiff
            ? named.TypeArguments[0]
            : null;

    /// <summary>
    /// A switch expression converting <paramref name="value"/>, a C# expression of the diff type, to a
    /// <c>VectorDiff&lt;T&gt;</c>, one line per variant.
    /// </summary>
    public IEnumerable<string> RenderSwitch(string value, SymbolDisplayFormat format)
    {
        string diff = Diff.ToDisplayString(format);
        string target = $"global::Matrix.RustSdk.VectorDiff<{Item.ToDisplayString(format)}>";
        // the name of the variant in the switch arms, reserved like the writer of the subscriptions
        const string variant = "__variant";

        yield return $"{value} switch";
        yield return "{";
        foreach ((string name, string[] properties) in Variants)
        {
            string pattern = properties.Length == 0 ? $"{diff}.{name}" : $"{diff}.{name} {variant}";
            string arguments = string.Join(
                ", ",
                properties.Select(p => p is Index or Length ? $"checked((int){variant}.{p})" : $"{variant}.{p}")
            );
            yield return $"    {pattern} => new {target}.{name}({arguments}),";
        }
        yield return $"    _ => throw new global::System.ArgumentOutOfRangeException(nameof({value}), {value}, \"Unknown diff.\"),";
        yield return "}";
    }

    private static IPropertySymbol? Property(INamedTypeSymbol[] variants, string variant, string name) =>
        variants
            .First(t => t.Name == variant)
            .GetMembers(name)
            .OfType<IPropertySymbol>()
            .FirstOrDefault(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic);

    private static bool Matches(ITypeSymbol type, string property, ITypeSymbol item) =>
        property switch
        {
            Values => type is IArrayTypeSymbol array
                && SymbolEqualityComparer.IncludeNullability.Equals(array.ElementType, item),
            Value => SymbolEqualityComparer.IncludeNullability.Equals(type, item),
            _ => type.SpecialType == SpecialType.System_UInt32,
        };

    private static string Describe(string property, ITypeSymbol item) =>
        property switch
        {
            Values => item.ToDisplayString() + "[]",
            Value => item.ToDisplayString(),
            _ => "uint",
        };
}
