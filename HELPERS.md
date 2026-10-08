# Helpers

Design notes for `src/Matrix.RustSdk`, the helpers on top of the generated bindings. They should build on
matrix-rust-sdk, not reimplement it, and make common tasks idiomatic in C#: `IAsyncEnumerable`, `await using`,
cancellation, managed collections. Nothing here is implemented yet, the findings are from `matrix-sdk-ffi/20260909` and
uniffi 0.31 and should be rechecked against the bindings before building on them.

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

### 1. Subscriptions as `IAsyncEnumerable<T>`

```csharp
await foreach (SyncServiceState state in syncService.StateChanges(cancellationToken)) { }
await foreach (TimelineDiff[] diffs in timeline.Updates(cancellationToken)) { }
await foreach (string[] userIds in room.TypingUsers(cancellationToken)) { }
```

- An internal listener writes the callback values to a `Channel<T>`, the enumerator subscribes on the first
  `MoveNextAsync` and disposes the `TaskHandle` in `finally`, so `break` or cancellation ends the subscription and the
  enumerator keeps the handle alive.
- Two backpressure policies: state streams (sync state, room info, verification and recovery state, typing) use
  `BoundedChannel(1, DropOldest)` because only the latest value matters, diff streams are unbounded because dropping a
  diff corrupts the list.
- The listener catches all exceptions, nothing escapes into the SDK. Undelivered `IDisposable` values are disposed when
  the enumeration ends.
- A `Subscription : IDisposable` for callback style use, `IObservable<T>` can be added later without depending on
  System.Reactive.
- Write the most used ones (sync state, timeline, room list, room info, typing) by hand. Once the pattern is settled,
  generate the remaining ones with a Roslyn source generator: every method `TaskHandle M(..., TListener listener)` (or
  `Task<TaskHandle>`) where `TListener` has a single `void` method.

### 2. `VectorDiff<T>` and `LiveList<T>`

- A generic `VectorDiff<T>` with a conversion from each of the 8 diff enums and one `ApplyTo(IList<T>)`.
- `LiveList<TNative, TView>` keeps a managed list up to date from a diff stream with a projection
  (`Func<TimelineItem, TView>`), exposes snapshots and `INotifyCollectionChanged` with an optional
  `SynchronizationContext` (Avalonia, WPF, MAUI).
- Open question, ownership: either project to managed values and dispose the native items right away (what the TUI
  client does), or let the list own the native items and dispose them on `Remove`, `Set`, `Truncate`, `Clear` and
  `Reset`.

### 3. Messages and timeline items

- `MessageContent.Text(body)`, `MessageContent.Markdown(markdown)`, `timeline.SendTextAsync(text)` instead of
  `MatrixSdkFfiMethods.MessageEventContentNew(new MessageType.Text(new TextMessageContent(body, Formatted: null)))`.
- On `EventTimelineItem`: `TryGetText(out string body)`, `EventId` (unwraps `EventOrTransactionId.EventId`),
  `SenderDisplayName` (falls back to `Sender`), timestamps as `DateTimeOffset`.
- `timeline.IncomingMessages(cancellationToken)`: only new events of other users received by sync, each once. This
  replaces most of the echo bot's `RoomTimeline`.

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

## Out of reach for helpers

- Real cancellation needs uniffi-bindgen-cs to generate `CancellationToken` overloads that call
  `rust_future_cancel_*`, like the Kotlin and Swift bindings do. Worth an issue or PR upstream, until then helpers can
  only offer `WaitAsync`.
- matrix-sdk-ffi only logs to stdout and files (`TracingConfiguration`), `LogEvent` goes from C# into the Rust logs.
  Forwarding Rust logs to `ILogger` needs a callback in matrix-sdk-ffi.

## First step

Build helpers 1 to 3 for sync state, timeline, room list, room info and typing, then rewrite the echo bot and the TUI
client with them. Their tests run the real apps against the homeserver and cover the helpers end to end, the
`VectorDiff` application gets unit tests in `test/Matrix.RustSdk.Tests`.
