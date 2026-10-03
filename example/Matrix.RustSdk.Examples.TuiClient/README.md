# TUI client

A basic Matrix client for the terminal, built with [Terminal.Gui](https://github.com/tui-cs/Terminal.Gui) on top of
the bindings. It logs in with a password, shows the room list, the live timeline of the opened room and sends text
messages.

The parts of the SDK it uses:

- [`MatrixSession.cs`](MatrixSession.cs): `ClientBuilder` and `Client.Login`, the `SyncService` and its
  `RoomListService`, the room list with `RoomList.EntriesWithDynamicAdapters`
- [`RoomTimeline.cs`](RoomTimeline.cs): `Room.Join`, `Room.Timeline`, `Timeline.AddListener` with its diffs,
  `Timeline.PaginateBackwards` and `Timeline.Send`

The SDK calls the listeners on its own threads, [`ChatWindow.cs`](ChatWindow.cs) moves the updates to the main loop of
Terminal.Gui with `IApplication.Invoke`.

To keep it short, the client uses an in-memory store: every start is a new login with a new device (removed again when
quitting), so messages in encrypted rooms sent before the start can't be decrypted. Real clients use
`ClientBuilder.SqliteStore` and restore the session instead. Only text messages and membership changes are shown.

## Running

Build the native library first (`./scripts/build-debug.sh` in the devenv shell, see the [README](../../README.md)),
then:

```bash
dotnet run --project example/Matrix.RustSdk.Examples.TuiClient -- --homeserver matrix.org --username alice
```

The login form asks for everything that's missing. `--homeserver` and `--username` can also be set with the
`MATRIX_HOMESERVER` and `MATRIX_USERNAME` environment variables, the password only with `MATRIX_PASSWORD`. If all three
are known, the client logs in right away. The homeserver has to support simplified sliding sync (MSC4186), the
`SyncService` is built on it.

## Keys

| Key       | Action                                                                   |
| --------- | ------------------------------------------------------------------------ |
| Tab       | Next field or pane (Shift+Tab for the previous one)                      |
| Up / Down | Select a room, scroll the timeline                                       |
| Enter     | Log in (login form), open the selected room (room list), send (composer) |
| Esc       | Quit                                                                     |

Opening a room you're invited to joins it.

## Tests

[`test/Matrix.RustSdk.Examples.TuiClient.Tests`](../../test/Matrix.RustSdk.Examples.TuiClient.Tests) runs the real
client in-process against a tuwunel homeserver: Terminal.Gui's ANSI driver runs without a terminal, the tests inject
key presses and read the rendered screen, so they work headless in CI.
