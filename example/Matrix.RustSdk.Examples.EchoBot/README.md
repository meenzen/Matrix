# Echo bot

A Matrix bot built as a .NET worker service: it logs in, joins every room it is invited to and sends the text messages
of other users back into the room.

- `EchoBotHost.cs` builds the [Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host) and binds
  `EchoBotOptions` from the `EchoBot` configuration section, `Program.cs` runs it.
- `EchoBotWorker.cs` is the `BackgroundService`: it logs in, runs a `/sync` loop with `Client.SyncOnceV2`, joins
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

Invite the bot to a room and send a message, it answers with the same text. Stop it with `Ctrl+C`.

| Setting               | Description                                                       |
|-----------------------|-------------------------------------------------------------------|
| `EchoBot:Homeserver`  | Server name (`matrix.org`) or homeserver URL                      |
| `EchoBot:Username`    | Username of the bot                                               |
| `EchoBot:Password`    | Password of the bot                                               |
| `EchoBot:DeviceName`  | Name of the device created at login, defaults to `Echo Bot`       |

## Limitations

The bot keeps its state in memory: it logs in with a new device on every start and only answers messages that arrive
while it is running. It can't read end-to-end encrypted rooms, a real bot would use `ClientBuilder.SqliteStore`,
restore its session with `Client.RestoreSession` and verify its device.

## Tests

`test/Matrix.RustSdk.Examples.EchoBot.Tests` starts this host in-process against a tuwunel homeserver in docker and
chats with the bot as a second user.
