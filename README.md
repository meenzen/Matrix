[![GitHub](https://img.shields.io/github/license/meenzen/Matrix.svg)](https://github.com/meenzen/Matrix/blob/main/LICENSE)
[![codecov](https://codecov.io/gh/meenzen/Matrix/graph/badge.svg?token=OTzMAH3dRO)](https://codecov.io/gh/meenzen/Matrix)
[![NuGet](https://img.shields.io/nuget/vpre/Matrix.RustSdk)](https://www.nuget.org/packages/Matrix.RustSdk)
[![NuGet](https://img.shields.io/nuget/dt/Matrix.RustSdk.svg)](https://www.nuget.org/packages/Matrix.RustSdk)
[![Matrix](https://img.shields.io/badge/matrix-%23dotnet%3Amnzn.dev-blue?logo=matrix)](https://matrix.to/#/#dotnet:mnzn.dev)

# Matrix.RustSdk

An unofficial C# wrapper for [matrix-rust-sdk](https://github.com/matrix-org/matrix-rust-sdk), the Rust SDK for the
[Matrix](https://matrix.org) protocol. This project is community-maintained and not affiliated with The Matrix.org
Foundation.

Questions or feedback about the SDK? Join [#dotnet:mnzn.dev](https://matrix.to/#/#dotnet:mnzn.dev) on Matrix.

## Installation

`Matrix.RustSdk` contains idiomatic helpers on top of the generated bindings (`Matrix.RustSdk.Bindings`), the native
libraries are shipped in separate packages, so applications only download the platforms they need. Reference
`Matrix.RustSdk` and the native packages for the platforms the application runs on, all of the same version:

```xml
<PackageReference Include="Matrix.RustSdk" Version="0.1.0" />
<PackageReference Include="Matrix.RustSdk.Bindings.Native.Linux" Version="0.1.0" />
<PackageReference Include="Matrix.RustSdk.Bindings.Native.Windows" Version="0.1.0" />
```

Applications that only use the bindings leave out `Matrix.RustSdk`, the native packages bring the matching
`Matrix.RustSdk.Bindings` package.

| Package                                  | Contents                                                                              |
|------------------------------------------|---------------------------------------------------------------------------------------|
| `Matrix.RustSdk`                         | helpers: subscriptions as `IAsyncEnumerable<T>`, live lists, messages, sessions       |
| `Matrix.RustSdk.Bindings`                | the generated bindings                                                                |
| `Matrix.RustSdk.Bindings.Native.Linux`   | native libraries for `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64` |
| `Matrix.RustSdk.Bindings.Native.Windows` | native libraries for `win-x64`, `win-arm64`                                           |
| `Matrix.RustSdk.Bindings.Native.MacOS`   | native libraries for `osx-x64`, `osx-arm64`                                           |
| `Matrix.RustSdk.Bindings.Native.All`     | all native packages                                                                   |

The native libraries only work with the bindings of the exact same version, and the helpers are compiled against them,
so `Matrix.RustSdk` and the native packages depend on exactly that version of `Matrix.RustSdk.Bindings`. The glibc
libraries require glibc 2.35 or newer (Ubuntu 22.04, Debian 12, RHEL 10), the musl libraries `libgcc` (installed with
.NET on Alpine).

The packages contain matrix-rust-sdk and the Rust crates it depends on, their licenses and copyright notices are in
`THIRD-PARTY-NOTICES.txt` in every package.

The bindings are generated from [matrix-rust-sdk](https://github.com/matrix-org/matrix-rust-sdk), the release notes of
each package name the `matrix-sdk-ffi` release it was built from. The API follows the upstream SDK, which changes with
almost every release, so minor versions of the packages contain breaking changes until 1.0.

## Usage

The bindings mirror the API of `matrix-sdk-ffi`, `Matrix.RustSdk` adds extension methods to their types (in the
`Matrix.RustSdk.Bindings` namespace, so they show up without another `using`) and a few types of its own:

- subscriptions as `IAsyncEnumerable<T>` (`room.WatchTypingUsersAsync()`) instead of listener interfaces and task
  handles, list diffs as `VectorDiff<T>` and `LiveList<T>`, an observable list that applies them
- sending and reading messages (`timeline.SendTextAsync`, `timeline.WatchIncomingMessagesAsync`, `item.TryGetText`)
- `StoredClient`: stores the session and restores it on the next start
- `MatrixSdk.Initialize`: logs and a multi-threaded runtime for the SDK
- `IProgress<T>` overloads for uploads and key backups

A minimal bot that answers every text message in a room with the same text:

```csharp
using Matrix.RustSdk;
using Matrix.RustSdk.Bindings;

// once per process, before anything else calls into the SDK: a multi-threaded runtime and logs
MatrixSdk.Initialize(new MatrixSdkOptions { LogDirectory = "logs" });

// stops the bot on Ctrl+C
using CancellationTokenSource stopping = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

// restores the session stored in the data directory, or logs in and stores the new session there
await using StoredClient stored = await StoredClient.LoginOrRestoreAsync(
    new ClientStoreOptions { DataDirectory = "data" },
    new PasswordCredentials
    {
        Homeserver = "matrix.org",
        Username = "username",
        Password = "password",
    }
);

// sliding sync only returns the latest event of each room by default, a bot wants all new ones
using SyncService sync = await stored.Client.SyncService().WithRoomListTimelineLimit(20).Finish();
await sync.Start();
try
{
    // answers every new text message of other users in the room with the same text
    using Room room = await stored.Client.JoinRoomByIdOrAlias("#my-room:matrix.org", serverNames: []);
    using Timeline timeline = await room.Timeline();
    await foreach (EventTimelineItem message in timeline.WatchIncomingMessagesAsync(stopping.Token))
    {
        using (message)
        {
            if (message.TryGetText(out string? text))
            {
                await timeline.SendTextAsync(text, inReplyTo: message.EventId);
            }
        }
    }
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
    // Ctrl+C
}
finally
{
    // the sync has to stop before the StoredClient closes the stores
    await sync.Stop();
}
```

The bindings work on their own too. A minimal example that logs in, creates a room and lists the rooms of the account:

```csharp
using Matrix.RustSdk.Bindings;

using Client client = await new ClientBuilder()
    .ServerNameOrHomeserverUrl("matrix.org")
    .InMemoryStore()
    .Build();

await client.Login("username", "password", initialDeviceName: "My App", deviceId: null);

string roomId = await client.CreateRoom(
    new CreateRoomParameters(
        Name: "My Room",
        IsEncrypted: true,
        Visibility: new RoomVisibility.Private(),
        Preset: RoomPreset.PrivateChat
    )
);

foreach (Room room in client.Rooms())
{
    Console.WriteLine($"{room.Id()}: {room.DisplayName()}");
}
```

Complete example apps, built on the helpers, are in [`example/`](example):

- [Echo bot](example/Matrix.RustSdk.Examples.EchoBot): a worker service that joins rooms it's invited to and echoes
  messages
- [TUI client](example/Matrix.RustSdk.Examples.TuiClient): a terminal client with vim keys: rooms, invites, direct
  chats, replies, edits, reactions, attachments, notifications, emoji verification and key backup

For everything else, the [matrix-rust-sdk documentation](https://github.com/matrix-org/matrix-rust-sdk) and the
`matrix-sdk-ffi` sources are the best reference, the C# methods are the PascalCase versions of the Rust ones. The helpers are documented in their XML docs, the design
notes are in [HELPERS.md](HELPERS.md).

## Development

The development environment is managed with [devenv](https://devenv.sh), `devenv shell` provides Rust, .NET and
[uniffi-bindgen-cs](https://github.com/NordSecurity/uniffi-bindgen-cs).

```bash
git submodule update --init
devenv shell
./scripts/build-debug.sh  # builds matrix-sdk-ffi for the host and regenerates the C# bindings
dotnet test               # runs the tests against a tuwunel homeserver, requires docker
```

The uniffi version used by `external/matrix-rust-sdk` has to match the one uniffi-bindgen-cs is built for
(`UNIFFI_RS_VERSION` in `devenv.nix`). When updating the submodule, pick a `matrix-sdk-ffi/*` tag whose `uniffi`
version matches the latest uniffi-bindgen-cs release.

## Releases

See [RELEASING.md](RELEASING.md) for versioning and the release process.

The [Packages](.github/workflows/packages.yml) workflow builds the native libraries on native GitHub runners for every
platform, tests them and packs the NuGet packages, the [Publish NuGet package](.github/workflows/nuget.yml) workflow
publishes them. Pull requests run the same builds once the basic build and tests passed.

Each library is tested on its runner before it is packed. The Linux runners run all tests, the musl libraries are
built and tested in Alpine containers. The Windows and macOS runners can't run the Linux homeserver container and only
run the smoke tests that don't need a homeserver.
