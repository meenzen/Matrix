using System.Reflection;
using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// Makes sure every subscription of the bindings has an <see cref="IAsyncEnumerable{T}"/> helper. When an update of
/// matrix-rust-sdk adds a subscription or a listener these tests fail until it is declared with
/// <see cref="SubscriptionAttribute"/> or listed with a reason, see AGENTS.md.
/// </summary>
public class SubscriptionCoverageTests
{
    /// <summary>
    /// Methods of the bindings returning a <see cref="TaskHandle"/> without a declaration, keyed by <c>Type.Method</c>.
    /// </summary>
    private static readonly Dictionary<string, string> NotDeclared = new()
    {
        ["RoomListEntriesWithDynamicAdaptersResult.EntriesStream"] =
            "the listener is passed to RoomList.EntriesWithDynamicAdapters, the room list needs a hand written helper",
        ["RoomListLoadingStateResult.StateStream"] =
            "the listener is passed to RoomList.LoadingState, the room list needs a hand written helper",
        ["Client.SetDelegate"] = "ClientDelegate has two methods, it isn't a stream",
    };

    private const string ProgressListener = "progress listener, candidate for an IProgress<T> overload";

    /// <summary>
    /// Methods of the bindings taking a listener that don't return a <see cref="TaskHandle"/>, keyed by
    /// <c>Type.Method</c>. They aren't subscriptions, but some deserve a helper of their own.
    /// </summary>
    private static readonly Dictionary<string, string> OtherListeners = new()
    {
        ["RoomList.EntriesWithDynamicAdapters"] = "room list, needs a hand written helper",
        ["RoomList.LoadingState"] = "room list, needs a hand written helper",
        ["Client.UploadMedia"] = ProgressListener,
        ["Encryption.EnableRecovery"] = ProgressListener,
        ["Encryption.WaitForBackupUploadSteadyState"] = ProgressListener,
        ["GrantLoginWithQrCodeHandler.Generate"] = ProgressListener,
        ["GrantLoginWithQrCodeHandler.Scan"] = ProgressListener,
        ["LoginWithQrCodeHandler.Generate"] = ProgressListener,
        ["LoginWithQrCodeHandler.Scan"] = ProgressListener,
        ["ClientBuilder.SetSessionDelegate"] = "delegate storing sessions, not a stream",
        ["Client.SetUtdDelegate"] = "delegate reporting decryption failures, registered for the lifetime of the client",
        ["NotificationSettings.SetDelegate"] = "delegate, not a stream",
        ["SessionVerificationController.SetDelegate"] = "delegate with several methods, not a stream",
        ["Client.RegisterNotificationHandler"] = "handler registered for the lifetime of the client",
        ["WidgetDriver.Run"] = "provider returning capabilities, not a stream",
    };

    [Test]
    public async Task EverySubscription_ShouldBeDeclared()
    {
        // Arrange
        HashSet<string> declared = [.. Declarations().Select(declaration => declaration.Subscription)];

        // Act
        string[] missing =
        [
            .. Subscriptions().Where(name => !declared.Contains(name) && !NotDeclared.ContainsKey(name)),
        ];

        // Assert
        await Assert
            .That(missing)
            .IsEmpty()
            .Because(
                "every subscription of the bindings needs a [Subscription] declaration in src/Matrix.RustSdk or an "
                    + "entry with a reason in NotDeclared"
            );
    }

    [Test]
    public async Task EveryOtherListener_ShouldBeListed()
    {
        // Act
        string[] missing = [.. MethodsTakingListeners().Where(name => !OtherListeners.ContainsKey(name))];

        // Assert
        await Assert
            .That(missing)
            .IsEmpty()
            .Because(
                "methods taking a listener aren't subscriptions if they don't return a TaskHandle, decide whether "
                    + "they need a helper and list them in OtherListeners"
            );
    }

    [Test]
    public async Task Lists_ShouldOnlyContainExistingMethods()
    {
        // Arrange
        HashSet<string> subscriptions = [.. Subscriptions()];
        HashSet<string> declared = [.. Declarations().Select(declaration => declaration.Subscription)];
        HashSet<string> listeners = [.. MethodsTakingListeners()];

        // Act
        string[] stale =
        [
            .. NotDeclared.Keys.Where(name => !subscriptions.Contains(name) || declared.Contains(name)),
            .. OtherListeners.Keys.Where(name => !listeners.Contains(name)),
        ];

        // Assert
        await Assert
            .That(stale)
            .IsEmpty()
            .Because("entries have to be removed when the method is gone or got a declaration");
    }

    [Test]
    public async Task Declarations_ShouldFollowTheConventions()
    {
        // Act
        string[] misnamed =
        [
            .. Declarations()
                .Where(declaration =>
                    !declaration.Method.Name.EndsWith("Async", StringComparison.Ordinal)
                    || declaration.Method.DeclaringType!.Name
                        != $"{declaration.Method.GetParameters()[0].ParameterType.Name}Extensions"
                    || declaration.Method.DeclaringType.Namespace
                        != declaration
                            .Method.GetParameters()[0]
                            .ParameterType.Namespace?.Split('.')
                            .Take(3)
                            .Aggregate((a, b) => $"{a}.{b}")
                )
                .Select(declaration => $"{declaration.Method.DeclaringType!.Name}.{declaration.Method.Name}"),
        ];

        // Assert
        await Assert
            .That(misnamed)
            .IsEmpty()
            .Because(
                "subscriptions end with Async and are declared in the <ExtendedType>Extensions class in the "
                    + "Matrix.RustSdk.Bindings namespace"
            );
    }

    [Test]
    public async Task EveryDiff_ShouldBeConvertedToVectorDiff()
    {
        // Arrange
        HashSet<Type> converted =
        [
            .. Declarations().Select(declaration => ListenerValue(declaration.Method)?.GetElementType()).OfType<Type>(),
            .. Conversions().Select(conversion => conversion.GetParameters()[0].ParameterType),
        ];

        // Act
        string[] missing = [.. DiffTypes().Where(diff => !converted.Contains(diff)).Select(diff => diff.Name)];

        // Assert
        await Assert
            .That(missing)
            .IsEmpty()
            .Because(
                "every diff enum of the bindings needs a subscription yielding VectorDiff<T>[] or a "
                    + "[VectorDiffConversion] declaration for its hand written listener"
            );
    }

    [Test]
    public async Task DiffSubscriptions_ShouldYieldVectorDiffs()
    {
        // Arrange
        HashSet<Type> diffs = [.. DiffTypes()];

        // Act
        string[] raw =
        [
            .. Declarations()
                .Where(declaration =>
                    ListenerValue(declaration.Method)?.GetElementType() is { } element
                    && diffs.Contains(element)
                    && (
                        declaration.Method.ReturnType.GetGenericArguments()[0] is not { IsArray: true } yielded
                        || !yielded.GetElementType()!.IsGenericType
                        || yielded.GetElementType()!.GetGenericTypeDefinition() != typeof(VectorDiff<>)
                        || !declaration.Method.Name.EndsWith("DiffsAsync", StringComparison.Ordinal)
                    )
                )
                .Select(declaration => $"{declaration.Method.DeclaringType!.Name}.{declaration.Method.Name}"),
        ];

        // Assert
        await Assert
            .That(raw)
            .IsEmpty()
            .Because("diff subscriptions are named Watch…DiffsAsync and yield VectorDiff<T>[], see AGENTS.md");
    }

    private static IEnumerable<Type> BindingClasses() =>
        // the generated interfaces (IClient, ...) repeat the methods of the classes
        typeof(TaskHandle).Assembly.GetExportedTypes().Where(type => type.IsClass);

    /// <summary>
    /// The methods of the bindings that return a <see cref="TaskHandle"/>, synchronously or as a task, as
    /// <c>Type.Method</c>. Properties like <c>RoomListLoadingStateResult.StateStream</c> by their name.
    /// </summary>
    private static IEnumerable<string> Subscriptions() =>
        BindingClasses()
            .SelectMany(type =>
                type.GetMethods(Members)
                    .Where(method =>
                        method.ReturnType == typeof(TaskHandle) || method.ReturnType == typeof(Task<TaskHandle>)
                    )
                    .Select(method =>
                        $"{type.Name}.{(method.IsSpecialName ? method.Name["get_".Length..] : method.Name)}"
                    )
            )
            .Distinct()
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// The methods and constructors of the bindings that take a callback interface (an interface no class of the
    /// bindings implements), also in arrays and collections, and don't return a <see cref="TaskHandle"/>, as
    /// <c>Type.Method</c>.
    /// </summary>
    private static IEnumerable<string> MethodsTakingListeners()
    {
        Type[] classes = [.. BindingClasses()];
        HashSet<Type> callbacks =
        [
            .. typeof(TaskHandle)
                .Assembly.GetExportedTypes()
                .Where(type => type.IsInterface && !classes.Any(type.IsAssignableFrom)),
        ];
        bool IsListener(Type type) =>
            callbacks.Contains(type)
            || type.HasElementType && IsListener(type.GetElementType()!)
            || type.IsGenericType && type.GetGenericArguments().Any(IsListener);

        return classes
            .SelectMany(type =>
                type.GetMethods(Members)
                    .Where(method =>
                        method.ReturnType != typeof(TaskHandle) && method.ReturnType != typeof(Task<TaskHandle>)
                    )
                    .Cast<MethodBase>()
                    .Concat(type.GetConstructors())
                    .Where(method => method.GetParameters().Any(parameter => IsListener(parameter.ParameterType)))
                    .Select(method => $"{type.Name}.{method.Name}")
            )
            .Distinct()
            .Order(StringComparer.Ordinal);
    }

    // instance methods and functions (static methods like those of MatrixSdkFfiMethods)
    private const BindingFlags Members =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>
    /// The diff enums of the bindings, records with the nested variants of <see cref="VectorDiff{T}"/>.
    /// </summary>
    private static IEnumerable<Type> DiffTypes() =>
        BindingClasses()
            .Where(type =>
                typeof(VectorDiff<>)
                    .GetNestedTypes()
                    .All(variant => type.GetNestedType(variant.Name) is { } nested && nested.BaseType == type)
            );

    /// <summary>
    /// The type the listener of the subscription wrapped by <paramref name="declaration"/> receives, null if it
    /// receives several values.
    /// </summary>
    private static Type? ListenerValue(MethodInfo declaration)
    {
        string name = declaration.GetCustomAttribute<SubscriptionAttribute>()!.Method;
        return declaration
            .GetParameters()[0]
            .ParameterType.GetMember(name, MemberTypes.Method, Members)
            .Cast<MethodInfo>()
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Where(type => type.IsInterface && type.GetMethods().Length == 1)
            .Select(listener => listener.GetMethods()[0].GetParameters())
            .Where(parameters => parameters.Length == 1)
            .Select(parameters => parameters[0].ParameterType)
            .FirstOrDefault();
    }

    /// <summary>
    /// The <see cref="VectorDiffConversionAttribute"/> declarations in Matrix.RustSdk.
    /// </summary>
    private static IEnumerable<MethodInfo> Conversions() =>
        typeof(ClientExtensions)
            .Assembly.GetTypes()
#pragma warning disable S3011
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
#pragma warning restore S3011
            .Where(method => method.IsDefined(typeof(VectorDiffConversionAttribute)));

    /// <summary>
    /// The <see cref="SubscriptionAttribute"/> declarations in Matrix.RustSdk, including internal ones, with the
    /// subscription they wrap as <c>Type.Method</c>.
    /// </summary>
    private static IEnumerable<(MethodInfo Method, string Subscription)> Declarations() =>
        typeof(ClientExtensions)
            .Assembly.GetTypes()
            // the hand written wrappers call internal declarations
#pragma warning disable S3011
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
#pragma warning restore S3011
            .SelectMany(method =>
                method
                    .GetCustomAttributes<SubscriptionAttribute>()
                    .Select(attribute => (method, $"{method.GetParameters()[0].ParameterType.Name}.{attribute.Method}"))
            );
}
