# TUI client

A basic Matrix client for the terminal, built with [Terminal.Gui](https://github.com/tui-cs/Terminal.Gui) on top of
the bindings and the helpers of `Matrix.RustSdk`. It logs in with a password, shows the room list, the live timeline of
the opened room and sends text messages. The session is stored, later starts restore it without asking for the
password.

The parts of the SDK it uses:

- [`MatrixSession.cs`](MatrixSession.cs): `StoredClient.TryRestoreAsync`, `LoginAsync` and `LogoutAsync`, the
  `SyncService` (`WatchStateAsync`)
  and its `RoomListService`, the room list with `RoomList.WatchRoomDiffsAsync` and a `RoomListQuery`, kept as a
  `LiveList` of room summaries with `ToLiveList`
- [`RoomTimeline.cs`](RoomTimeline.cs): `Room.Join`, `Room.Timeline`, `Timeline.WatchItemDiffsAsync` kept as a
  `LiveList` of lines, `Timeline.PaginateBackwards` and `Timeline.Send`

The live lists project the native rooms and timeline items to managed values right away, the native objects are
disposed after projecting. They are updated on thread pool threads, [`ChatWindow.cs`](ChatWindow.cs) moves the updates
to the main loop of Terminal.Gui with `IApplication.Invoke`.

The session and the stores of the SDK are in `~/.local/share/Matrix.RustSdk.TuiClient` (the local app data on Windows
and macOS), `--data-directory` or `MATRIX_DATA_DIRECTORY` selects another directory, one per account. Logging out
(Ctrl+L) removes the device and deletes the stored session. To keep it short, the client doesn't set up encryption: the
device isn't verified and has no key backup, so messages in encrypted rooms sent before the first login can't be
decrypted. Only text messages and membership changes are shown.

## Running

Build the native library first (`./scripts/build-debug.sh` in the devenv shell, see the [README](../../README.md)),
then:

```bash
dotnet run --project example/Matrix.RustSdk.Examples.TuiClient -- --homeserver matrix.org --username alice
```

Without a stored session the login form asks for everything that's missing. `--homeserver` and `--username` can also be set with the
`MATRIX_HOMESERVER` and `MATRIX_USERNAME` environment variables, the password only with `MATRIX_PASSWORD`. If all three
are known, the client logs in right away. The homeserver has to support simplified sliding sync (MSC4186), the
`SyncService` is built on it.

## Keys

| Key       | Action                                                                   |
| --------- | ------------------------------------------------------------------------ |
| Tab       | Next field or pane (Shift+Tab for the previous one)                      |
| Up / Down | Select a room, scroll the timeline                                       |
| Enter     | Log in (login form), open the selected room (room list), send (composer) |
| Ctrl+L    | Log out and go back to the login form                                    |
| Esc       | Quit                                                                     |

Opening a room you're invited to joins it.

## Tests

[`test/Matrix.RustSdk.Examples.TuiClient.Tests`](../../test/Matrix.RustSdk.Examples.TuiClient.Tests) runs the real
client in-process against a tuwunel homeserver: Terminal.Gui's ANSI driver runs without a terminal, the tests inject
key presses and read the rendered screen, so they work headless in CI.
