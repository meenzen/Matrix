using Matrix.RustSdk.Examples.TuiClient.Chat;
using Matrix.RustSdk.Examples.TuiClient.Input;
using Matrix.RustSdk.Examples.TuiClient.Rendering;

namespace Matrix.RustSdk.Examples.TuiClient;

public sealed partial class ChatWindow
{
    // the recovery key created in this session, shown on the encryption page until the window closes
    private string? _shownRecoveryKey;

    private void Execute(Command command)
    {
        // rooms opened by a command start insert mode, unless the user pressed keys while waiting for the server
        long keyPresses = _keyPresses;
        switch (command)
        {
            case Command.Quit:
                RequestStop();
                break;
            case Command.Logout:
                Ask(
                    "Log out? This session and its keys are deleted, set up recovery first to keep the history. (y/n)",
                    () =>
                    {
                        IsLogoutRequested = true;
                        RequestStop();
                    }
                );
                break;
            case Command.Help:
                ShowPage(Pages.Help());
                break;
            case Command.Join join:
                Run(
                    $"Joining {join.RoomIdOrAlias}…",
                    async () =>
                        await OpenRoomAsync(
                            await _session.JoinAsync(join.RoomIdOrAlias),
                            startInsert: true,
                            keyPresses: keyPresses
                        ),
                    failure: "Joining failed"
                );
                break;
            case Command.Leave:
                WithRoom(room =>
                    Ask(
                        $"Leave {room.Summary.Name}? (y/n)",
                        () =>
                            Run(
                                "Leaving…",
                                async () =>
                                {
                                    await room.LeaveAsync();
                                    await CloseIfOpenAsync(room);
                                },
                                "Left the room."
                            )
                    )
                );
                break;
            case Command.Accept:
                AcceptInvite();
                break;
            case Command.Decline:
                DeclineInvite();
                break;
            case Command.Invite invite:
                WithJoinedRoom(room =>
                    Run(
                        $"Inviting {invite.UserId}…",
                        () => room.InviteAsync(invite.UserId),
                        $"Invited {invite.UserId}."
                    )
                );
                break;
            case Command.Kick kick:
                WithJoinedRoom(room =>
                    Run(
                        $"Removing {kick.UserId}…",
                        () => room.KickAsync(kick.UserId, kick.Reason),
                        $"Removed {kick.UserId}."
                    )
                );
                break;
            case Command.Ban ban:
                WithJoinedRoom(room =>
                    Run($"Banning {ban.UserId}…", () => room.BanAsync(ban.UserId, ban.Reason), $"Banned {ban.UserId}.")
                );
                break;
            case Command.Unban unban:
                WithJoinedRoom(room =>
                    Run($"Unbanning {unban.UserId}…", () => room.UnbanAsync(unban.UserId), $"Unbanned {unban.UserId}.")
                );
                break;
            case Command.CreateRoom create:
                Run(
                    $"Creating {create.Name}…",
                    async () =>
                        await OpenRoomAsync(
                            await _session.CreateRoomAsync(create.Name, create.IsEncrypted, create.IsPublic),
                            startInsert: true,
                            keyPresses: keyPresses
                        ),
                    failure: "Creating the room failed"
                );
                break;
            case Command.DirectMessage dm:
                Run(
                    $"Opening the chat with {dm.UserId}…",
                    async () =>
                        await OpenRoomAsync(
                            await _session.GetOrCreateDirectMessageAsync(dm.UserId),
                            startInsert: true,
                            keyPresses: keyPresses
                        ),
                    failure: "Opening the chat failed"
                );
                break;
            case Command.Members:
                ShowMembers();
                break;
            case Command.Topic { Text: null }:
                WithRoom(room =>
                    ShowMessage(room.Summary.Topic is { } topic ? $"Topic: {topic}" : "The room has no topic.")
                );
                break;
            case Command.Topic topic:
                WithJoinedRoom(room =>
                    Run("Changing the topic…", () => room.SetTopicAsync(topic.Text), "Changed the topic.")
                );
                break;
            case Command.RoomName name:
                WithJoinedRoom(room =>
                    Run("Renaming the room…", () => room.SetNameAsync(name.Name), "Renamed the room.")
                );
                break;
            case Command.Emote emote:
                WithJoinedRoom(room => Run(null, () => room.SendEmoteAsync(emote.Text), failure: "Sending failed"));
                break;
            case Command.React react:
                React(react.Key);
                break;
            case Command.Upload upload:
                WithJoinedRoom(room =>
                {
                    TimelineEntry? replyTo = _replyTo;
                    CancelReplyAndEdit();
                    Run(
                        $"Uploading {Path.GetFileName(upload.Path)}…",
                        () => room.UploadAsync(upload.Path, replyTo),
                        "Uploaded.",
                        "Uploading failed"
                    );
                });
                break;
            case Command.Open { Link: { } link }:
                OpenLink(link);
                break;
            case Command.Open:
                OpenSelectedMedia();
                break;
            case Command.Retry:
                if (
                    _pane == Pane.Timeline
                    && _timelineView.SelectedItem is int index
                    && index < _entries.Count
                    && _entries[index].Status == Chat.SendStatus.Failed
                )
                {
                    ResendSelected();
                }
                else
                {
                    Run("Sending the waiting messages…", _session.RetrySendingAsync, "Sending again.");
                }
                break;
            case Command.Save save:
                SaveSelectedMedia(save.Path);
                break;
            case Command.MarkRead:
                WithJoinedRoom(room => Run(null, room.MarkAsReadAsync, "Marked as read."));
                break;
            case Command.Ignore ignore:
                Run(
                    $"Ignoring {ignore.UserId}…",
                    () => _session.IgnoreAsync(ignore.UserId),
                    $"Ignoring {ignore.UserId}."
                );
                break;
            case Command.Unignore unignore:
                Run(null, () => _session.UnignoreAsync(unignore.UserId), $"No longer ignoring {unignore.UserId}.");
                break;
            case Command.Verify:
                ShowPage(Pages.Verification(_session.Verification.Status));
                Run(null, _session.Verification.RequestDeviceVerificationAsync, failure: "Verification failed");
                break;
            case Command.VerifyUser verify:
                ShowPage(Pages.Verification(_session.Verification.Status));
                Run(
                    null,
                    () => _session.Verification.RequestUserVerificationAsync(verify.UserId),
                    failure: "Verification failed"
                );
                break;
            case Command.RecoveryStatus:
                ShowEncryptionPage();
                break;
            case Command.EnableRecovery:
                ShowEncryptionPage();
                Run(
                    "Setting up recovery, backing up the room keys…",
                    async () =>
                    {
                        _shownRecoveryKey = await _session.Encryption.EnableRecoveryAsync();
                        ShowEncryptionPage();
                    },
                    "Recovery is set up, store the recovery key.",
                    "Setting up recovery failed"
                );
                break;
            case Command.ResetRecoveryKey:
                Ask(
                    "Replace the recovery key? The old one stops working. (y/n)",
                    () =>
                        Run(
                            "Creating a new recovery key…",
                            async () =>
                            {
                                _shownRecoveryKey = await _session.Encryption.ResetRecoveryKeyAsync();
                                ShowEncryptionPage();
                            },
                            "Created a new recovery key, store it.",
                            "Resetting the recovery key failed"
                        )
                );
                break;
            case Command.Recover recover:
                ShowEncryptionPage();
                Run(
                    "Recovering…",
                    () => _session.Encryption.RecoverAsync(recover.RecoveryKey),
                    "Recovered: this session is verified and the history can be decrypted.",
                    "Recovering failed"
                );
                break;
            case Command.Notifications notifications:
                _notificationsEnabled = notifications.Enabled ?? !_notificationsEnabled;
                ShowMessage(_notificationsEnabled ? "Notifications are on." : "Notifications are off.");
                break;
            case Command.Search search:
                _session.RoomFilter = search.Query ?? "";
                FocusPane(Pane.Rooms);
                break;
        }
    }

    private void WithRoom(Action<OpenedRoom> action)
    {
        if (_room is { } room)
        {
            action(room);
        }
        else
        {
            ShowError("Open a room first.");
        }
    }

    private void WithJoinedRoom(Action<OpenedRoom> action) =>
        WithRoom(room =>
        {
            if (room.IsInvite)
            {
                ShowError("Accept the invite first (:accept).");
            }
            else
            {
                action(room);
            }
        });

    private void AcceptInvite() =>
        WithRoom(room =>
        {
            if (!room.IsInvite)
            {
                ShowError("There is no invite to accept.");
                return;
            }
            ClearPrompts(InviteTag);
            int generation = _roomGeneration;
            long keyPresses = _keyPresses;
            Run(
                $"Joining {room.Summary.Name}…",
                async () =>
                {
                    await room.AcceptInviteAsync();
                    if (generation == _roomGeneration)
                    {
                        ClosePage();
                        ShowTimeline();
                        StartInsertAfter(keyPresses);
                    }
                },
                $"Joined {room.Summary.Name}.",
                "Joining failed"
            );
        });

    private void DeclineInvite() =>
        WithRoom(room =>
        {
            if (!room.IsInvite)
            {
                ShowError("There is no invite to decline.");
                return;
            }
            ClearPrompts(InviteTag);
            Run(
                "Declining the invite…",
                async () =>
                {
                    await room.LeaveAsync();
                    await CloseIfOpenAsync(room);
                },
                "Declined the invite.",
                "Declining failed"
            );
        });

    /// <summary>
    /// Closes <paramref name="room"/> if it is still the open one, the user may have moved on while leaving it.
    /// </summary>
    private async Task CloseIfOpenAsync(OpenedRoom room)
    {
        if (_room == room)
        {
            await CloseRoomAsync();
        }
    }

    private void ShowMembers()
    {
        WithRoom(room =>
        {
            int generation = _roomGeneration;
            Run(
                "Loading the members…",
                async () =>
                {
                    IReadOnlyList<MemberSummary> members = await room.GetMembersAsync();
                    if (generation == _roomGeneration)
                    {
                        ShowPage(
                            Pages.Members(room.Summary.Name, members) with
                            {
                                // Enter opens the direct chat with the member
                                Open = item =>
                                {
                                    string userId = members[item].UserId;
                                    if (userId != _session.UserId)
                                    {
                                        Execute(new Command.DirectMessage(userId));
                                    }
                                    return Task.CompletedTask;
                                },
                            }
                        );
                    }
                }
            );
        });
    }

    private void ShowEncryptionPage()
    {
        Page page = Pages.Encryption(
            _session.Encryption,
            _session.UserId,
            _session.DeviceId,
            _shownRecoveryKey,
            _session.Verification.IsReady
        );
        if (_page?.Kind == PageKind.Encryption)
        {
            RefreshPage(page);
        }
        else
        {
            ShowPage(page);
        }
    }

    private void React(string key)
    {
        if (SelectedEntry("react to") is not { } entry || _room is not { } room)
        {
            return;
        }
        Run(
            null,
            async () =>
                ShowMessage(await room.ToggleReactionAsync(entry, key) ? $"Reacted with {key}." : $"Removed {key}."),
            failure: "Reacting failed"
        );
    }

    private MediaAttachment? SelectedMedia()
    {
        if (SelectedEntry("open") is not { } entry)
        {
            return null;
        }
        if (entry.Media is null)
        {
            ShowError("The selected message has no attachment.");
        }
        return entry.Media;
    }

    private void OpenLink(int number)
    {
        if (SelectedEntry("open") is not { } entry)
        {
            return;
        }
        IReadOnlyList<string> links = TextEditing.Links(entry.Body);
        if (number > links.Count)
        {
            ShowError(
                links.Count == 0
                    ? "The selected message has no links."
                    : $"The selected message has {links.Count} links."
            );
            return;
        }
        string link = links[number - 1];
        if (_settings.Opener(link))
        {
            ShowMessage($"Opened {link}.");
        }
        else
        {
            ShowError($"There is no application to open {link}.");
        }
    }

    private void OpenSelectedMedia()
    {
        // o on a message without an attachment opens its link
        if (
            _pane == Pane.Timeline
            && _timelineView.SelectedItem is int index
            && index < _entries.Count
            && _entries[index] is { Media: null } entry
            && TextEditing.Links(entry.Body).Count > 0
        )
        {
            if (TextEditing.Links(entry.Body).Count > 1)
            {
                ShowMessage(
                    $"The message has {TextEditing.Links(entry.Body).Count} links, :open <n> opens another one."
                );
            }
            OpenLink(1);
            return;
        }
        if (SelectedMedia() is not { } media || _room is not { } room)
        {
            return;
        }
        string directory = Path.Join(Path.GetTempPath(), "matrix-tui", Environment.UserName);
        Run(
            $"Downloading {media.Filename}…",
            async () =>
            {
                string path = await room.DownloadAsync(media, null, directory);
                if (_settings.Opener(path))
                {
                    ShowMessage($"Opened {path}.");
                }
                else
                {
                    ShowError($"Downloaded to {path}, but there is no application to open it.");
                }
            },
            failure: "Downloading failed"
        );
    }

    private void SaveSelectedMedia(string? path)
    {
        if (SelectedMedia() is not { } media || _room is not { } room)
        {
            return;
        }
        Run(
            $"Downloading {media.Filename}…",
            async () => ShowMessage($"Saved {await room.DownloadAsync(media, path, _settings.DownloadDirectory)}."),
            failure: "Saving failed"
        );
    }
}
