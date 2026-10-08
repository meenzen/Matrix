using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Matrix.RustSdk.Generators.Tests;

/// <summary>
/// Snapshot tests of <see cref="SubscriptionGenerator"/>, one per shape of subscription in the bindings. The snapshots
/// are in <c>Snapshots/</c>, review and accept changes of the generated code there.
/// </summary>
public class SubscriptionGeneratorTests
{
    private const string Usings = """
        using Matrix.RustSdk.Bindings;
        using Matrix.RustSdk.Subscriptions;

        namespace Matrix.RustSdk;

        """;

    [Test]
    public Task SyncSubscription_ShouldBeGenerated() =>
        VerifyGeneratedAsync(
            """
            public static partial class RoomExtensions
            {
                [Subscription(nameof(Room.SubscribeToTypingNotifications), SubscriptionBuffer.Latest)]
                public static partial IAsyncEnumerable<string[]> TypingUsersAsync(
                    this Room room,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task AsyncSubscription_ShouldBeGenerated() =>
        VerifyGeneratedAsync(
            """
            public static partial class TimelineExtensions
            {
                [Subscription(nameof(Timeline.AddListener), SubscriptionBuffer.All)]
                public static partial IAsyncEnumerable<TimelineDiff[]> UpdatesAsync(
                    this Timeline timeline,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task ExtraParameters_ShouldBePassedByName() =>
        VerifyGeneratedAsync(
            """
            public static partial class RoomListServiceExtensions
            {
                [Subscription(nameof(RoomListService.SyncIndicator), SubscriptionBuffer.Latest)]
                public static partial IAsyncEnumerable<RoomListServiceSyncIndicator> SyncIndicatorAsync(
                    this RoomListService roomListService,
                    uint delayBeforeShowingInMs,
                    uint delayBeforeHidingInMs,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task ListenerWithTwoValues_ShouldYieldTuples() =>
        VerifyGeneratedAsync(
            """
            public static partial class ClientExtensions
            {
                [Subscription(nameof(Client.SubscribeToSendQueueUpdates), SubscriptionBuffer.All)]
                public static partial IAsyncEnumerable<(string RoomId, RoomSendQueueUpdate Update)> SendQueueUpdatesAsync(
                    this Client client,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task KeywordParameterNames_ShouldBeEscaped() =>
        VerifyGeneratedAsync(
            """
            public static partial class ClientExtensions
            {
                [Subscription(nameof(Client.ObserveAccountDataEvent), SubscriptionBuffer.Latest)]
                public static partial IAsyncEnumerable<AccountDataEvent> AccountDataAsync(
                    this Client client,
                    AccountDataEventType eventType,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task NullableValue_ShouldBeGenerated() =>
        VerifyGeneratedAsync(
            """
            public static partial class ClientExtensions
            {
                [Subscription(nameof(Client.SubscribeToMediaPreviewConfig), SubscriptionBuffer.Latest)]
                public static partial IAsyncEnumerable<MediaPreviewConfig?> MediaPreviewConfigAsync(
                    this Client client,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task Overloads_ShouldHaveDistinctNames() =>
        VerifyGeneratedAsync(
            """
            public static partial class Extensions
            {
                [Subscription(nameof(Room.SubscribeToRoomInfoUpdates), SubscriptionBuffer.Latest)]
                public static partial IAsyncEnumerable<RoomInfo> RoomInfoAsync(
                    this Room room,
                    CancellationToken cancellationToken = default
                );

                [Subscription(nameof(Client.SubscribeToRoomInfo), SubscriptionBuffer.Latest)]
                public static partial IAsyncEnumerable<RoomInfo> RoomInfoAsync(
                    this Client client,
                    string roomId,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task InternalDeclarationInGlobalNamespace_ShouldBeGenerated() =>
        VerifyGeneratedAsync(
            """
            using Matrix.RustSdk.Bindings;
            using Matrix.RustSdk.Subscriptions;

            internal static partial class Extensions
            {
                [Subscription(nameof(SyncService.State), SubscriptionBuffer.Latest)]
                internal static partial IAsyncEnumerable<SyncServiceState> StateAsync(
                    this SyncService syncService,
                    CancellationToken cancellationToken
                );
            }
            """,
            usings: false
        );

    [Test]
    public Task Current_ShouldBeFetchedAfterSubscribing() =>
        VerifyGeneratedAsync(
            """
            public static partial class RoomExtensions
            {
                [Subscription(
                    nameof(Room.SubscribeToRoomInfoUpdates),
                    SubscriptionBuffer.Latest,
                    Current = nameof(Room.RoomInfo)
                )]
                public static partial IAsyncEnumerable<RoomInfo> WatchRoomInfoAsync(
                    this Room room,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task ThrowWhenFinished_ShouldBePassedToTheStream() =>
        VerifyGeneratedAsync(
            """
            public static partial class ClientExtensions
            {
                [Subscription(nameof(Client.SyncV2), SubscriptionBuffer.All, ThrowWhenFinished = true)]
                public static partial IAsyncEnumerable<SyncResponseV2> SyncV2Async(
                    this Client client,
                    SyncSettingsV2 settings,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task UnknownCurrent_ShouldReportCurrentNotFound() =>
        VerifyErrorAsync(
            "MRSG004",
            """
            public static partial class RoomExtensions
            {
                [Subscription(nameof(Room.SubscribeToRoomInfoUpdates), SubscriptionBuffer.Latest, Current = "Info")]
                public static partial IAsyncEnumerable<RoomInfo> WatchRoomInfoAsync(
                    this Room room,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task CurrentOfAnotherType_ShouldReportCurrentNotFound() =>
        VerifyErrorAsync(
            "MRSG004",
            """
            public static partial class RoomExtensions
            {
                [Subscription(
                    nameof(Room.SubscribeToTypingNotifications),
                    SubscriptionBuffer.Latest,
                    Current = nameof(Room.Id)
                )]
                public static partial IAsyncEnumerable<string[]> WatchTypingUsersAsync(
                    this Room room,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task ReservedParameterName_ShouldReportInvalidDeclaration() =>
        VerifyErrorAsync(
            "MRSG001",
            """
            public static partial class RoomExtensions
            {
                [Subscription(nameof(Room.SubscribeToTypingNotifications), SubscriptionBuffer.Latest)]
                public static partial IAsyncEnumerable<string[]> WatchTypingUsersAsync(
                    this Room __writer,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task NotPartial_ShouldReportInvalidDeclaration() =>
        VerifyErrorAsync(
            "MRSG001",
            """
            public static partial class RoomExtensions
            {
                [Subscription(nameof(Room.SubscribeToTypingNotifications), SubscriptionBuffer.Latest)]
                public static IAsyncEnumerable<string[]> TypingUsersAsync(
                    this Room room,
                    CancellationToken cancellationToken = default
                ) => throw new NotImplementedException();
            }
            """
        );

    [Test]
    public Task MissingCancellationToken_ShouldReportInvalidDeclaration() =>
        VerifyErrorAsync(
            "MRSG001",
            """
            public static partial class RoomExtensions
            {
                [Subscription(nameof(Room.SubscribeToTypingNotifications), SubscriptionBuffer.Latest)]
                public static partial IAsyncEnumerable<string[]> TypingUsersAsync(this Room room);
            }
            """
        );

    [Test]
    public Task WrongReturnType_ShouldReportInvalidDeclaration() =>
        VerifyErrorAsync(
            "MRSG001",
            """
            public static partial class RoomExtensions
            {
                [Subscription(nameof(Room.SubscribeToTypingNotifications), SubscriptionBuffer.Latest)]
                public static partial Task<string[]> TypingUsersAsync(
                    this Room room,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task NotASubscription_ShouldReportMethodNotFound() =>
        VerifyErrorAsync(
            "MRSG002",
            """
            public static partial class RoomExtensions
            {
                [Subscription(nameof(Room.Join), SubscriptionBuffer.All)]
                public static partial IAsyncEnumerable<string> JoinAsync(
                    this Room room,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task ListenerWithSeveralMethods_ShouldReportMethodNotFound() =>
        VerifyErrorAsync(
            "MRSG002",
            """
            public static partial class ClientExtensions
            {
                [Subscription(nameof(Client.SetDelegate), SubscriptionBuffer.All)]
                public static partial IAsyncEnumerable<bool> AuthErrorsAsync(
                    this Client client,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task WrongParameterName_ShouldReportMethodNotFound() =>
        VerifyErrorAsync(
            "MRSG002",
            """
            public static partial class ClientExtensions
            {
                [Subscription(nameof(Client.SubscribeToRoomInfo), SubscriptionBuffer.Latest)]
                public static partial IAsyncEnumerable<RoomInfo> RoomInfoAsync(
                    this Client client,
                    string id,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task WrongParameterType_ShouldReportMethodNotFound() =>
        VerifyErrorAsync(
            "MRSG002",
            """
            public static partial class ClientExtensions
            {
                [Subscription(nameof(Client.SubscribeToRoomInfo), SubscriptionBuffer.Latest)]
                public static partial IAsyncEnumerable<RoomInfo> RoomInfoAsync(
                    this Client client,
                    string? roomId,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task WrongValueType_ShouldReportMismatch() =>
        VerifyErrorAsync(
            "MRSG003",
            """
            public static partial class RoomExtensions
            {
                [Subscription(nameof(Room.SubscribeToTypingNotifications), SubscriptionBuffer.Latest)]
                public static partial IAsyncEnumerable<string> TypingUsersAsync(
                    this Room room,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task MissingNullability_ShouldReportMismatch() =>
        VerifyErrorAsync(
            "MRSG003",
            """
            public static partial class ClientExtensions
            {
                [Subscription(nameof(Client.SubscribeToMediaPreviewConfig), SubscriptionBuffer.Latest)]
                public static partial IAsyncEnumerable<MediaPreviewConfig> MediaPreviewConfigAsync(
                    this Client client,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public Task TupleForSingleValue_ShouldReportMismatch() =>
        VerifyErrorAsync(
            "MRSG003",
            """
            public static partial class ClientExtensions
            {
                [Subscription(nameof(Client.SubscribeToSendQueueUpdates), SubscriptionBuffer.All)]
                public static partial IAsyncEnumerable<RoomSendQueueUpdate> SendQueueUpdatesAsync(
                    this Client client,
                    CancellationToken cancellationToken = default
                );
            }
            """
        );

    [Test]
    public async Task UnrelatedChange_ShouldNotRegenerate()
    {
        // Arrange
        Microsoft.CodeAnalysis.CSharp.CSharpCompilation compilation = GeneratorRun.CreateCompilation(
            Usings
                + """
                public static partial class RoomExtensions
                {
                    [Subscription(nameof(Room.SubscribeToTypingNotifications), SubscriptionBuffer.Latest)]
                    public static partial IAsyncEnumerable<string[]> TypingUsersAsync(
                        this Room room,
                        CancellationToken cancellationToken = default
                    );
                }
                """
        );
        GeneratorRun first = GeneratorRun.Run(compilation);

        // Act
        GeneratorRun second = GeneratorRun.Run(
            compilation.AddSyntaxTrees(
                Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("public static class Unrelated { }")
            ),
            first.Driver
        );

        // Assert
        IncrementalStepRunReason[] reasons =
        [
            .. second
                .Driver.GetRunResult()
                .Results.Single()
                .TrackedOutputSteps.SelectMany(step => step.Value)
                .SelectMany(step => step.Outputs)
                .Select(output => output.Reason),
        ];
        await Assert.That(reasons).IsNotEmpty();
        await Assert.That(reasons).All().Satisfy(reason => reason.IsEqualTo(IncrementalStepRunReason.Cached));
    }

    /// <summary>
    /// Snapshots the generated source and checks that it compiles.
    /// </summary>
    private static async Task VerifyGeneratedAsync(
        string declarations,
        bool usings = true,
        [CallerFilePath] string testFile = "",
        [CallerMemberName] string test = ""
    )
    {
        GeneratorRun run = GeneratorRun.Run(usings ? Usings + declarations : declarations);

        await Assert.That(run.GeneratorDiagnostics).IsEmpty();
        await Assert.That(run.CompilationErrors).IsEmpty();
        await Snapshot.VerifyAsync(run, testFile, test);
    }

    /// <summary>
    /// Snapshots the diagnostics and checks that the generator reported <paramref name="id"/> and generated nothing.
    /// </summary>
    private static async Task VerifyErrorAsync(
        string id,
        string declarations,
        [CallerFilePath] string testFile = "",
        [CallerMemberName] string test = ""
    )
    {
        GeneratorRun run = GeneratorRun.Run(Usings + declarations);

        await Assert.That(run.GeneratorDiagnostics.Select(diagnostic => diagnostic.Id)).IsEquivalentTo([id]);
        await Assert.That(run.Driver.GetRunResult().GeneratedTrees).IsEmpty();
        await Snapshot.VerifyAsync(run, testFile, test);
    }
}
