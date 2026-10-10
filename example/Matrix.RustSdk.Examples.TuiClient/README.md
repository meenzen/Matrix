# Matrix TUI client

A terminal Matrix client built on the bindings and the `Matrix.RustSdk` helpers, with
[Terminal.Gui](https://github.com/gui-cs/Terminal.Gui). It is controlled like vim and covers what a daily client needs:
rooms and invites, direct chats, replies, edits, reactions, attachments, desktop notifications, emoji verification and
the key backup.

```bash
dotnet run --project example/Matrix.RustSdk.Examples.TuiClient -- --homeserver matrix.org --username alice
```

Build the native library first (in the devenv shell, see the [README](../../README.md)). Without a stored session the
login form asks for everything that's missing. `--homeserver` and `--username` can also be set with the
`MATRIX_HOMESERVER` and `MATRIX_USERNAME` environment variables, the password only with `MATRIX_PASSWORD`; if all
three are known, the client logs in right away. The homeserver has to support simplified sliding sync (MSC4186), the
`SyncService` is built on it.

The session, the encryption keys and the SDK logs are stored in `--data-directory` or `MATRIX_DATA_DIRECTORY`
(`~/.local/share/Matrix.RustSdk.TuiClient` on Linux, the local app data on Windows and macOS), one directory per
account. The next start restores the session. `--help` lists the options.

## Keys

The client starts in normal mode, where every key is a command. `?` shows all keys and commands.

| Keys                    | What they do                                                    |
| ----------------------- | --------------------------------------------------------------- |
| `j` `k`, `gg` `G`       | move down / up, to the first / last item (`5j`, `5G` work)      |
| `gu`                    | jump to the first unread message                                |
| `C-d` `C-u` `C-f` `C-b` | half a page / a page down and up                                |
| `h` `l`, `Tab`          | focus the room list / the timeline                              |
| `Enter`                 | open the room, the attachment or link, or a page's selection    |
| `J` `K`, `U`            | next / previous room, next unread room                          |
| `i`                     | write a message, `Enter` sends, `Alt+Enter` adds a line break   |
| `Tab`, `C-w`, `C-u`     | while writing: complete a name, delete a word / the line        |
| `Esc`, `C-c`            | back to normal mode, closes pages, cancels a reply or an edit   |
| `r` `e` `dd` `+` `yy`   | reply to, edit, delete, react to, copy the selected message     |
| `o`, `R`                | open the attachment or link, send a failed message again        |
| `y` `n`                 | answer the question in the status line, `Esc` dismisses it      |
| `m`                     | members of the room, `Enter` on a member opens the direct chat  |
| `/`                     | jump to a room by name, `Enter` opens the best match            |
| `:`                     | command line, `Tab` completes, `↑` `↓` go through the history   |
| `ZZ`, `:q`, `C-q`       | quit                                                            |

The mouse works too: the wheel scrolls, a click selects, a double click opens.

Opening a room starts at the first unread message, the read receipt is sent once you reach the end. What you type
stays with its room when you switch. Questions (an invite, a verification request) never take over while you write,
they wait in the status line until you are back in normal mode.

## Commands

| Command                                     | What it does                                                    |
| ------------------------------------------- | --------------------------------------------------------------- |
| `:join #room:example.org`                   | join a room by its address or id                                |
| `:create <name> [--public] [--unencrypted]` | create a room, encrypted and private by default                 |
| `:dm @user:example.org`                     | open the direct chat with a user, creating it if needed         |
| `:accept`, `:decline`                       | accept or decline the invite to the open room (`y`, `n`)        |
| `:leave`                                    | leave the open room                                             |
| `:invite`, `:kick`, `:ban`, `:unban`        | manage the members, kicks and bans take a reason                |
| `:topic [text]`, `:name <name>`             | show or change the topic, rename the room                       |
| `:me <text>`, `:react <emoji>`              | send an emote, react (shortcodes: `:react +1`, `:react tada`)   |
| `:upload <path>`, `:save [path]`, `:open`   | send a file, save or open the attachment of the selected message |
| `:open <n>`                                 | open the n-th link of the selected message                      |
| `:retry`                                    | send the messages that failed again                             |
| `:ignore`, `:unignore`                      | ignore a user                                                   |
| `:verify [@user]`                           | verify this session with another one of yours, or another user  |
| `:recovery`                                 | the state of the key backup and recovery                        |
| `:recovery enable`, `:recovery reset`       | set up recovery and show the recovery key, or replace the key   |
| `:recovery <key>`                           | verify this session and restore the history with the key        |
| `:notifications [on\|off]`                  | toggle desktop notifications                                    |
| `:logout`                                   | log out and delete the session                                  |

## Encryption

The first session of an account sets up cross-signing and a key backup on its own. `:recovery enable` stores the keys
on the server, protected by a recovery key: keep it, a new session needs it (or a verification with another session,
`:verify`) to read the history of encrypted rooms. Sessions that ask to be verified show up as a question, the emojis
to compare are shown in the timeline area.

`Tab` completes commands, user ids and file paths on the command line, `↑` and `↓` go through the history, which is
kept in the data directory.

## Connection

When the homeserver can't be reached the client waits for it to come back (`sync: offline` in the status line) and
sends the messages that piled up meanwhile. A message the server rejects shows "failed to send", `R` tries again.

## Notifications

Mentions, direct messages and whatever else the push rules of the account make noisy show a desktop notification
(`notify-send` on Linux, `osascript` on macOS), except for the room you are reading.

## How it uses the SDK

- [`MatrixSession.cs`](MatrixSession.cs): `StoredClient` (restore, login, logout) with cross-signing and backups
  enabled in `ClientStoreOptions.ConfigureClient`, the `SyncService`, the room list with `RoomList.WatchRoomDiffsAsync`
  and a `RoomListQuery` whose filter searches the names, kept as a `LiveList` of `RoomSummary` records,
  `Client.RegisterNotificationHandler` for notifications, joining, creating rooms and direct chats
- [`Chat/OpenedRoom.cs`](Chat/OpenedRoom.cs): the timeline as a `LiveList` of `TimelineEntry` records
  (`WatchItemDiffsAsync`), sending, replies, edits, redactions, reactions, pagination, read receipts, typing notices
  (`WatchTypingUsersAsync`), attachments, members and moderation
- [`Security/`](Security): `Encryption` (`WatchVerificationStateAsync`, `WatchBackupStateAsync`,
  `WatchRecoveryStateAsync`, `EnableRecoveryAsync`, `Recover`) and the `SessionVerificationController` for emoji
  verification

The live lists project the native rooms and timeline items to managed records right away, the native objects are
disposed after projecting. They are updated on thread pool threads, [`ChatWindow.cs`](ChatWindow.cs) moves the updates
to the main loop of Terminal.Gui with `IApplication.Invoke`.

## Code

The client is split into layers that don't know about the UI, so they can be tested without a terminal:

- `Input/`: the vim keymap and the command parser
- `Chat/`, `Rooms/`: the open room and its timeline as plain records, rendered to rows of styled text
- `Security/`: the encryption state and the verification flow
- `MatrixSession.cs`: the client, the sync and the room list
- `ChatWindow*.cs`: the window, key handling and commands, `Rendering/` the views

The session classes move every SDK call to the thread pool (`ThreadPoolHop`): the SDK can complete one future while
another is polled, and Terminal.Gui runs continuations posted from its main loop right away, which would call back into
the SDK from within the first call and deadlock.

## Tests

[`test/Matrix.RustSdk.Examples.TuiClient.Tests`](../../test/Matrix.RustSdk.Examples.TuiClient.Tests) runs the real
client in-process against a tuwunel homeserver for every feature: Terminal.Gui's ANSI driver runs without a terminal,
the tests inject key presses and read the rendered screen, another user on the SDK checks the results. Unit tests cover
the keymap, the command parser and the layout.
