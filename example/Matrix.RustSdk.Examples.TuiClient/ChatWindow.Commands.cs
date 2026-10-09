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
        switch (command)
        {
            case Command.Quit:
                RequestStop();
                break;
            case Command.Logout:
                Ask(
                    "Log out? This session and its keys are deleted, set up recovery first to keep the history. (y/n)",
                    logout =>
                    {
                        if (logout)
                        {
                            IsLogoutRequested = true;
                            RequestStop();
                        }
                    }
                );
                break;
            case Command.Help:
                ShowPage(Pages.Help());
                break;
            case Command.Join join:
                Run(
                    $"Joining {join.RoomIdOrAlias}…",
                    async () => await OpenRoomAsync(await _session.JoinAsync(join.RoomIdOrAlias)),
                    failure: "Joining failed"
                );
                break;
            case Command.Leave:
                WithRoom(room =>
                    Ask(
                        $"Leave {room.Summary.Name}? (y/n)",
                        leave =>
                        {
                            if (leave)
                            {
                                Run(
                                    "Leaving…",
                                    async () =>
                                    {
                                        await room.LeaveAsync();
                                        await CloseRoomAsync();
                                    },
                                    "Left the room."
                                );
                            }
                        }
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
                            await _session.CreateRoomAsync(create.Name, create.IsEncrypted, create.IsPublic)
                        ),
                    failure: "Creating the room failed"
                );
                break;
            case Command.DirectMessage dm:
                Run(
                    $"Opening the chat with {dm.UserId}…",
                    async () => await OpenRoomAsync(await _session.GetOrCreateDirectMessageAsync(dm.UserId)),
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
            case Command.Open:
                OpenSelectedMedia();
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
                    reset =>
                    {
                        if (reset)
                        {
                            Run(
                                "Creating a new recovery key…",
                                async () =>
                                {
                                    _shownRecoveryKey = await _session.Encryption.ResetRecoveryKeyAsync();
                                    ShowEncryptionPage();
                                },
                                "Created a new recovery key, store it.",
                                "Resetting the recovery key failed"
                            );
                        }
                    }
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
            ClearPrompt();
            int generation = _roomGeneration;
            Run(
                $"Joining {room.Summary.Name}…",
                async () =>
                {
                    await room.AcceptInviteAsync();
                    if (generation == _roomGeneration)
                    {
                        ClosePage();
                        ShowTimeline();
                        SetMode(InputMode.Insert);
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
            ClearPrompt();
            Run(
                "Declining the invite…",
                async () =>
                {
                    await room.LeaveAsync();
                    await CloseRoomAsync();
                },
                "Declined the invite.",
                "Declining failed"
            );
        });

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
        Page page = Pages.Encryption(_session.Encryption, _session.UserId, _session.DeviceId, _shownRecoveryKey);
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

    private void OpenSelectedMedia()
    {
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
