# Helpers

Design notes for `src/Matrix.RustSdk`, the helpers on top of the generated bindings. They should build on
matrix-rust-sdk, not reimplement it, and make common tasks idiomatic in C#: `IAsyncEnumerable`, `await using`,
cancellation, managed collections. Helpers 1 to 3 are implemented, the findings for the others are from
`matrix-sdk-ffi/20260909` and uniffi 0.31 and should be rechecked against the bindings before building on them.

## Pain points of the bindings

| Pain point                                                           | Scope                         |
| -------------------------------------------------------------------- | ----------------------------- |
| Reactive APIs take a listener interface and return a `TaskHandle`    | 38 methods                    |
| The same diff enum, once per collection                              | 8 types                       |
| Async methods have no `CancellationToken`                            | every async method            |
| A `TaskHandle` that isn't referenced ends the subscription on GC     | every subscription            |
| Listeners are called on SDK threads, exceptions must not escape      | every listener                |
| Bot logic (new remote messages only, each once) is easy to get wrong | every bot                     |
| Unix millisecond timestamps, verbose message content constructors    | everywhere                    |

Details:

- Subscriptions: `Timeline.AddListener`, `SyncService.State`, `Room.SubscribeToRoomInfoUpdates`,
  `Room.SubscribeToTypingNotifications`, `Encryption.VerificationStateListener`, `RoomListService.State`, ... Find them
  with `grep -nE 'public (async )?(Task<)?TaskHandle>? \w+\(' src/Matrix.RustSdk.Bindings/matrix_sdk_ffi.cs`. The
  listener interfaces have a single `void` method (`OnUpdate` or `Call`).
- Diffs: `TimelineDiff`, `RoomListEntriesUpdate`, `ThreadListUpdate`, `SpaceListUpdate`, `SpaceFilterUpdate`,
  `SearchServiceResultsUpdate`, `RoomDirectorySearchEntryUpdate` and `LiveLocationShareUpdate` are all eyeball's
  `VectorDiff` with the same variants (`Append`, `Clear`, `PushFront`, `PushBack`, `PopFront`, `PopBack`, `Insert`,
  `Set`, `Remove`, `Truncate`, `Reset`). The TUI client applies them with the same switch twice (`MatrixSession`,
  `RoomTimeline`).
- Cancellation: the generated `_UniFFIAsync.PollFuture` never calls the `rust_future_cancel_*` functions, `WaitAsync`
  only stops waiting while the Rust future keeps running.
- Lifetimes: generated objects have finalizers and Rust cancels the task when a `TaskHandle` is dropped
  (`bindings/matrix-sdk-ffi/src/task_handle.rs`). Forgetting the handle silently ends the subscription, and
  `Dispose()` alone is enough to cancel, `Cancel()` before it is redundant.
- Bots: the echo bot needs a set of seen event ids, has to skip the initial `Reset` (history), own events and events
  that weren't received by sync (`Origin != Sync`), and matches
  `TimelineItemContent.MsgLike { Content.Kind: MsgLikeKind.Message { Content.MsgType: MessageType.Text text } }`.

## Proposed helpers

In order of value.

### 1. Subscriptions as `IAsyncEnumerable<T>` (implemented)

```csharp
await syncService.WatchStateAsync(cancellationToken).FirstAsync(state => state == SyncServiceState.Running);
await foreach (TimelineDiff[] diffs in timeline.WatchItemDiffsAsync(cancellationToken)) { }
await foreach (string[] userIds in room.WatchTypingUsersAsync(cancellationToken)) { }
```

- All 37 subscriptions are wrapped, declared in `src/Matrix.RustSdk/*Extensions.Subscriptions.cs`, see AGENTS.md for
  the conventions. The room list (`RoomList.EntriesWithDynamicAdapters`, `RoomList.LoadingState`) passes its listener
  differently and has hand written helpers, see helper 2.
- The enumeration subscribes when it starts and disposes the `TaskHandle` when it ends (`break`, exception,
  cancellation). The SDK doesn't notify listeners when it ends a subscription on its own (owner disposed, lagging
  behind, sync errors), the runtime checks `TaskHandle.IsFinished()` once per second while idle and ends the
  enumeration, `SyncV2Async` throws instead.
- Two buffer policies, chosen per subscription from the Rust implementation: states keep only the latest unread value
  (`BoundedChannel(1, DropOldest)`), diffs and events keep everything.
- Values that weren't yielded are disposed, including the elements of arrays and tuples.
- `Room.WatchRoomInfoAsync` fetches the current info after subscribing, the SDK only delivers changes. It is skipped
  when the SDK delivered a value in the meantime.
- Follow-ups: one shared timer for the finished checks instead of one per idle enumeration, a callback form
  (`Subscription : IDisposable`) or `IObservable<T>` if needed. Upstream issues worth reporting: the duplicate key and
  send queue subscriptions spin when the client is dropped while they run, `SubscribeToSendQueueStatus` doesn't send
  the initial status it documents, and cancelling `SubscribeToKnockRequests` leaks a cleanup task.

### 2. `VectorDiff<T>` and `LiveList<T>` (implemented)

```csharp
RoomListQuery query = new(pageSize: 50);
await using LiveList<RoomItem> rooms = roomList
    .WatchRoomDiffsAsync(query, cancellationToken)
    .ToLiveList(room => new RoomItem(room.Id(), room.DisplayName()), (item, room) => item.Update(room));
query.Filter = new RoomListEntriesDynamicFilterKind.Favourite();
```

- `VectorDiff<T>` replaces the 8 diff enums, which have the same variants: the `Watch…DiffsAsync` subscriptions yield
  `VectorDiff<T>[]` (the generator converts in the listener), `ApplyTo(IList<T>, removed)` applies a diff.
- `ToLiveList` keeps a `LiveList<T>` up to date from a diff stream: `IList`, `INotifyCollectionChanged` with single item
  events (WPF throws for ranges), snapshots, `Changed` per batch, `Initialized` and `Completion`. It runs on the
  current `SynchronizationContext` (like `Progress<T>`), so UI apps bind to it directly and console apps get the thread
  pool.
- Ownership, decided: the list owns what it stores and disposes items when they leave it. With a projection (sync or
  async) the native value is disposed right after projecting, an optional `update` keeps view models on `Set` instead of
  replacing them. The SDK replaces a room on every notable change, so lists of native rooms are only for short lived
  use, UIs project.
- Room list: `RoomList.WatchRoomDiffsAsync(RoomListQuery)` with a mutable query (filter, `AddOnePage`,
  `ResetToOnePage`) bound to one running enumeration, and `RoomList.WatchLoadingStateAsync`. Like Element X Android
  (`RoomListFilterMapper`, `RustDynamicRoomList`) the query always applies a base filter (rooms that weren't left and
  aren't spaces, space invites, `DeduplicateVersions`), `Filter` narrows it, and changing it goes back to one page.
- Follow-ups: an opt-out of disposing projections (`disposeItems: false`) if view models get cached across lists,
  `GeneratorResult` keeps the `Diagnostic` (and its syntax tree) of failed declarations, so those don't cache.

### 3. Messages and timeline items (implemented)

```csharp
await foreach (EventTimelineItem message in timeline.WatchIncomingMessagesAsync(cancellationToken))
{
    using (message)
    {
        if (message.TryGetText(out string? body))
        {
            await timeline.SendTextAsync(body, inReplyTo: message.EventId);
        }
    }
}
```

- Reading, C# 14 extension properties on `EventTimelineItem`: `EventId`, `SenderDisplayName` (disambiguated like
  Element when another member uses the same name: `Alice (@alice:example.org)`), `SentAt` (`DateTimeOffset`, UTC),
  `Message` (the `MessageContent` of any msgtype, like Rust's `as_message()`), `ThreadRootEventId`,
  `InReplyToEventId`, and `TryGetText(out body)` for `m.text` only, so bots don't answer the notices of other bots.
- Sending: static extensions `RoomMessageEventContentWithoutRelation.Text(body, html?)`, `Notice(body, html?)` and
  `Markdown(markdown)` (a static class `MessageContent` would clash with the record of the bindings), and
  `timeline.SendTextAsync`/`SendNoticeAsync`/`SendMarkdownAsync` with an optional `inReplyTo`. Replies use
  `Timeline.SendReply`, which keeps the thread of the replied-to event. The helpers return once the message is queued
  and dispose the `SendHandle`, disposing it doesn't abort sending.
- `WatchIncomingMessagesAsync`: messages of other users that arrived by sync since the timeline was created, each once.
  The timeline reports events again on every change and re-adds them after a gappy sync (`Clear`, then origin `Sync`
  again), so the filter (`IncomingMessageFilter`) remembers the event ids it saw (bounded, FIFO) and decides by origin
  alone: `init_focus` loads the cached events with origin `Cache`, so the first `Reset` needs no special case and
  messages arriving before the subscription started aren't lost. The position doesn't matter, eyeball's `Skip` shows
  a new subscriber only the latest items and adds hidden ones, live ones too, with `PushFront` later. Events that
  couldn't be decrypted aren't remembered, the decrypted `Set` keeps the origin. The sync of the own join (sliding
  sync) contains the history before it with origin `Sync`, in the same batch: everything before the own join is
  history.
- Limitations, documented: events lost in a gappy sync are only loaded by pagination and aren't yielded. Sliding sync
  syncs one event per room by default, bots raise it with `SyncServiceBuilder.WithRoomListTimelineLimit`. Events that
  arrive while the timeline reloads from the cache (client fell behind) have origin `Cache` and are missed too. The
  event cache handles a sync in the background, a timeline created right after the first sync can get its events as
  new ones (the messages that arrived while a bot with a persistent store was offline, or recent history with an
  in-memory store): bots compare `SentAt` with their start if they don't want those.
- Follow-ups: filling gaps (paginate after a `Clear` and yield the events newer than the last seen one), which would
  also make the default sliding sync configuration safe for bots.

### 4. Pagination

- `room.MembersAsync()` as `IAsyncEnumerable<RoomMember>` over `RoomMembersIterator.NextChunk`.
- `timeline.PaginateBackwardsUntilAsync(...)` with a count or predicate, `PaginateBackwards` only returns whether the
  start was reached.
- Room directory search (`NextPage` and the `Results` listener) as an async enumerable.

### 5. Client setup and sessions

- `LoginOrRestoreAsync`: `SqliteStore`/`SessionPaths`, `RestoreSession` if a session is stored, otherwise a password
  login.
- `ISessionStore` on top of `ClientSessionDelegate` with a JSON file implementation. Check that `Session` round-trips
  with System.Text.Json.
- `MatrixSdk.Initialize(...)`: `InitPlatform` may only be called once per process, map Microsoft.Extensions.Logging
  levels to `TracingConfiguration`.

### 6. Hosting

A separate package (`Matrix.RustSdk.Hosting`) with `services.AddMatrixClient(...)` and a hosted `SyncService`, so the
core package stays free of dependencies.

## Source generation

Helper 1 is implemented with an incremental source generator (`src/Matrix.RustSdk.Generators`), the evaluation below
was done with a spike first. Declarations pick the name, the buffer policy and the docs, the generator writes a
private listener class writing to the channel and an implementation calling `SubscriptionStream.CreateAsync`, the hand
written runtime (channel, buffer policy, `TaskHandle` disposal, disposing unread values). AGENTS.md shows a
declaration.

### What fits

| Pattern                                                               | Count | Generated helper              |
| --------------------------------------------------------------------- | ----- | ----------------------------- |
| `TaskHandle` subscription with a single method listener               | 37    | `IAsyncEnumerable<T>`         |
| Progress listener of an async method (recovery, QR login, ...)        | 7     | overload with `IProgress<T>`  |
| `VectorDiff`-shaped enum                                              | 8     | conversion to `VectorDiff<T>` |
| Single method listener without a `TaskHandle` (`SetUtdDelegate`, ...) | 4     | none, hand written            |
| Several methods or a return value (`ClientSessionDelegate`, ...)      | 4     | none, hand written            |
| Async methods without `CancellationToken`                             | 315   | none, see below               |

- All 37 listener interfaces of the subscriptions have exactly one `void` method. 34 pass one value, 3 pass two
  (`ObserveRoomAccountDataEvent`, `SubscribeToSendQueueStatus`, `Client.SubscribeToSendQueueUpdates`), they map to a
  named tuple (`IAsyncEnumerable<(string RoomId, RoomSendQueueUpdate Update)>`). 13 subscribe asynchronously
  (`Task<TaskHandle>`), 6 take extra parameters, the generator passes them by name.
- The room list is the exception: the listener is passed to `RoomList.EntriesWithDynamicAdapters` and
  `EntriesStream()` returns the `TaskHandle`, it needs a hand written helper anyway because of the filter controller.
- The progress listeners (`EnableRecoveryProgressListener`, `BackupSteadyStateListener`, the four QR login listeners,
  `ProgressWatcher` of `UploadMedia`) have the same shape, the same technique generates `IProgress<T>` overloads.
- `CancellationToken` overloads for all async methods could be generated too, but they would only call `WaitAsync`,
  pretend to cancel and double the API. Fix it upstream instead.

### Options

1. **Hand written**: ~15 lines per subscription with a shared runtime core, no tooling. Nothing notices when the SDK
   adds subscriptions.
2. **Generator, driven by declarations** (the spike): ~7 lines per subscription, the generator is ~280 lines.
3. **Generator, fully automatic** for every subscription in the referenced bindings: no declarations, but names and
   buffer policies can't be derived. The SDK names are inconsistent (`State`, `AddListener`, `Results`,
   `BackupStateListener`, `SubscribeToX`), and states (latest value wins) can't be told apart from events that must
   never be dropped (`SyncV2` responses, dehydrated device events, send queue errors). It also has to scan the
   referenced assembly through `CompilationProvider`, which reruns on every edit.
4. **Generated, committed files** from `scripts/generate-bindings.sh`: visible in diffs like the bindings, but needs a
   second code generator outside the build and has the naming problem of option 3.

**Recommendation: option 2.** The decisions that need judgment (name, buffer policy, docs) stay in reviewable C#, the
boilerplate is generated, and the generator checks every declaration against the bindings: when an SDK update renames
or changes a subscription the build fails (`MRSG002` method not found, `MRSG003` value type mismatch) instead of the
helper silently breaking. The generator is only a build time dependency of `Matrix.RustSdk` and isn't shipped. Pair it
with a test that reflects over the bindings and fails for subscription methods that have neither a declaration nor an
entry in an explicit ignore list, so new SDK subscriptions get noticed.

### Spike findings

- The generator sees the bindings' symbols through the normal project reference, nothing special is needed:
  `ProjectReference` with `OutputItemType="Analyzer"` and `ReferenceOutputAssembly="false"`.
- Generated code (`// <auto-generated/>`) builds warning free with `TreatWarningsAsErrors`, Sonar, Roslynator and the
  VS threading analyzers. Two adjustments were needed: `RS2008` (analyzer release tracking) is suppressed because the
  generator isn't published, and VSTHRD200 requires the `Async` suffix for methods returning `IAsyncEnumerable`, which
  matches the BCL (`ChannelReader.ReadAllAsync`).
- netstandard2.0 has no `IsExternalInit`, the pipeline model is a class with value equality instead of a record. Without
  the equality the incremental pipeline regenerates on every edit.
- The bindings use keywords as parameter names (`@event`), the generator prefixes every identifier with `@`.
- `new ValueTask<TaskHandle>(...)` accepts both `TaskHandle` and `Task<TaskHandle>`, one template covers sync and async
  subscriptions.
- `[EnumeratorCancellation]` lives on the hand written `SubscriptionStream.CreateAsync`, so both the parameter and
  `WithCancellation` cancel. Cancellation surfaces as `OperationCanceledException`, `break` ends the subscription.
- The implementation added snapshot tests (a small helper in the test project, Verify requires a license declaration
  since September 2026), unique names for overloads, the coverage test, `Current` and `ThrowWhenFinished`. Nested or
  generic containing classes are still rejected with `MRSG001`.

## Out of reach for helpers

- Real cancellation needs uniffi-bindgen-cs to generate `CancellationToken` overloads that call
  `rust_future_cancel_*`, like the Kotlin and Swift bindings do. Worth an issue or PR upstream, until then helpers can
  only offer `WaitAsync`.
- matrix-sdk-ffi only logs to stdout and files (`TracingConfiguration`), `LogEvent` goes from C# into the Rust logs.
  Forwarding Rust logs to `ILogger` needs a callback in matrix-sdk-ffi.

## Next steps

One pull request per step, each usable on its own. Public APIs get a design review by a fresh agent before they are
merged, like the subscriptions did.

1. ~~Generator and all subscriptions (helper 1)~~: done in #143.
2. ~~`VectorDiff<T>`, `LiveList<T>` and the room list (helper 2)~~: done in #148.
3. ~~Message and timeline helpers, the echo bot and the TUI client on top of them (helper 3)~~: done.
4. **Remaining helpers (4 and 5).** `IProgress<T>` overloads for the 7 progress listeners (generated, they are listed
   in `SubscriptionCoverageTests`), pagination as async enumerables, `LoginOrRestoreAsync` with a session store, and a
   run-once `MatrixSdk.Initialize`.
5. **First release of `Matrix.RustSdk`.** Set `IsPackable`, check the packaged XML docs, see RELEASING.md.

Smaller follow-ups, whenever convenient:

- One shared timer for the finished checks of idle subscriptions instead of one per enumeration.
- Upstream: an issue on uniffi-bindgen-cs for real cancellation (`rust_future_cancel_*`). Reports for matrix-rust-sdk:
  the duplicate key and send queue subscriptions spin when the client is dropped while they run,
  `SubscribeToSendQueueStatus` doesn't send the initial status it documents, and cancelling `SubscribeToKnockRequests`
  leaks a cleanup task. `SyncService.Stop()` sometimes never returns when it is called right after the state became
  `Running` (`SubscriptionTests.SyncServiceStateChanges_ShouldReportRunning` hangs in about one of five full test runs,
  on main too), needs a reproduction first.
- Filling gaps in `WatchIncomingMessagesAsync`, see helper 3.
- The hosting package (helper 6) once the rest is published.
