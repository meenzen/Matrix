# Echo bot

A Matrix bot built as a .NET worker service: it logs in, joins every room it is invited to and sends the text messages
of other users back into the room.

- `EchoBotHost.cs` builds the [Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host) and binds
  `EchoBotOptions` from the `EchoBot` configuration section, `Program.cs` sets up the SDK (`MatrixSdk.Initialize`, its
  logs go to `logs/` in the working directory) and runs the host.
- `EchoBotWorker.cs` is the `BackgroundService`: it logs in or restores its session with
  `StoredClient.LoginOrRestoreAsync`, runs a `/sync` loop with `Client.SyncOnceV2`, joins
  invites and sends the messages back.
- `RoomTimeline.cs` listens to the timeline of a joined room and picks out new text messages of other users.

## Running

The example references the bindings from source and copies the locally built native library, build it first with
`./scripts/build-debug.sh` (see the [README](../../README.md#development) in the repository root). Your own application
references the `Matrix.RustSdk.Bindings.Native.*` packages instead.

Create an account for the bot, then pass its credentials with environment variables or command line arguments instead
of writing the password into `appsettings.json`:

```bash
export EchoBot__Password='...'
dotnet run --project example/Matrix.RustSdk.Examples.EchoBot -- \
  --EchoBot:Homeserver=matrix.org \
  --EchoBot:Username=my-echo-bot
```

Invite the bot to a room and send a message, it answers with the same text. Stop it with `Ctrl+C`. The bot stores its
session and the stores of the SDK in `data/` in the working directory, later starts restore the session and don't need
the password.

| Setting                 | Description                                                 |
|-------------------------|-------------------------------------------------------------|
| `EchoBot:Homeserver`    | Server name (`matrix.org`) or homeserver URL                |
| `EchoBot:Username`      | Username of the bot                                         |
| `EchoBot:Password`      | Password of the bot, only needed for the first login        |
| `EchoBot:DeviceName`    | Name of the device created at login, defaults to `Echo Bot` |
| `EchoBot:DataDirectory` | Directory of the session and the stores, defaults to `data` |

## Limitations

The bot only answers messages that arrive while it is running. It doesn't set up end-to-end encryption: its device
isn't verified and has no key backup, so other users' clients may refuse to send it keys. A real bot would verify its
device or use `Encryption.Recover` after the first login (`StoredClient.IsRestored` is `false`).

## Tests

`test/Matrix.RustSdk.Examples.EchoBot.Tests` starts this host in-process against a tuwunel homeserver in docker and
chats with the bot as a second user.
