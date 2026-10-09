using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Examples.TuiClient.Chat;

/// <summary>
/// Turns timeline items of the SDK into <see cref="TimelineEntry"/>s. The item is disposed by the caller right after,
/// only managed values are copied.
/// </summary>
public static class TimelineEntries
{
    public static TimelineEntry From(TimelineItem item, string ownUserId)
    {
        string key = item.UniqueId().Id;
        using EventTimelineItem? eventItem = item.AsEvent();
        if (eventItem is not null)
        {
            return FromEvent(key, eventItem, ownUserId);
        }
        return item.AsVirtual() switch
        {
            VirtualTimelineItem.DateDivider divider => new TimelineEntry(key, EntryKind.DayDivider, "")
            {
                Time = DateTimeOffset.FromUnixTimeMilliseconds((long)divider.Ts),
            },
            _ => new TimelineEntry(key, EntryKind.ReadMarker, "new messages"),
        };
    }

    private static TimelineEntry FromEvent(string key, EventTimelineItem item, string ownUserId)
    {
        (EntryKind kind, string body, MediaAttachment? media) = Describe(item.Content, item.SenderDisplayName);
        MsgLikeContent? msgLike = (item.Content as TimelineItemContent.MsgLike)?.Content;
        MessageContent? message = item.Message;

        return new TimelineEntry(key, kind, body)
        {
            Time = item.SentAt,
            SenderId = item.Sender,
            SenderName = item.SenderDisplayName,
            Id = item.EventOrTransactionId,
            EventId = item.EventId,
            IsOwn = item.IsOwn,
            IsEditable = item.IsEditable,
            IsEdited = message?.IsEdited ?? false,
            CanReply = item.CanBeRepliedTo && item.EventId is not null,
            MentionsMe =
                !item.IsOwn
                && message is not null
                && (
                    message.Mentions is { } mentions
                        ? mentions.Room || mentions.UserIds.Contains(ownUserId)
                        : MentionsByName(message.Body, ownUserId)
                ),
            Status = item.LocalSendState switch
            {
                EventSendState.NotSentYet => SendStatus.Sending,
                EventSendState.SendingFailed => SendStatus.Failed,
                _ => SendStatus.Sent,
            },
            ReplyTo = msgLike?.InReplyTo is { } inReplyTo ? Reply(inReplyTo) : null,
            Media = media,
            Reactions =
            [
                .. (msgLike?.Reactions ?? []).Select(r => new ReactionSummary(
                    r.Key,
                    r.Senders.Length,
                    r.Senders.Any(s => s.SenderId == ownUserId)
                )),
            ],
            ReadBy =
            [
                .. item.ReadReceipts.Keys.Where(u => u != ownUserId && u != item.Sender).Order(StringComparer.Ordinal),
            ],
        };
    }

    /// <summary>
    /// Messages without explicit mentions (older clients) mention the user if they contain the localpart of the user
    /// id as a word.
    /// </summary>
    private static bool MentionsByName(string body, string ownUserId)
    {
        string localpart = Localpart(ownUserId);
        int index = body.IndexOf(localpart, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            bool startsWord = index == 0 || !char.IsLetterOrDigit(body[index - 1]);
            int end = index + localpart.Length;
            bool endsWord = end == body.Length || !char.IsLetterOrDigit(body[end]);
            if (startsWord && endsWord)
            {
                return true;
            }
            index = body.IndexOf(localpart, index + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// <c>alice</c> of <c>@alice:example.org</c>.
    /// </summary>
    public static string Localpart(string userId)
    {
        int colon = userId.IndexOf(':', StringComparison.Ordinal);
        return userId.TrimStart('@')[
            ..Math.Max(0, (colon < 0 ? userId.Length : colon) - (userId.StartsWith('@') ? 1 : 0))
        ];
    }

    private static ReplyPreview Reply(InReplyToDetails inReplyTo)
    {
        string eventId = inReplyTo.EventId();
        using EmbeddedEventDetails details = inReplyTo.Event();
        return details switch
        {
            EmbeddedEventDetails.Ready ready => new ReplyPreview(
                eventId,
                ready.SenderProfile is ProfileDetails.Ready { DisplayName: { Length: > 0 } name } ? name : ready.Sender,
                Describe(ready.Content, ready.Sender).Body
            ),
            EmbeddedEventDetails.Error => new ReplyPreview(eventId, null, "(the message can't be loaded)"),
            _ => new ReplyPreview(eventId, null, null),
        };
    }

    /// <summary>
    /// The kind and text of the content. <paramref name="sender"/> is the name used in sentences like "alice joined".
    /// </summary>
    public static (EntryKind Kind, string Body, MediaAttachment? Media) Describe(
        TimelineItemContent content,
        string sender
    ) =>
        content switch
        {
            TimelineItemContent.MsgLike { Content.Kind: var kind } => kind switch
            {
                MsgLikeKind.Message { Content: var message } => DescribeMessage(message),
                MsgLikeKind.Sticker sticker => (EntryKind.Media, $"sticker: {sticker.Body}", null),
                MsgLikeKind.Poll poll => (
                    EntryKind.Message,
                    $"poll: {poll.Question}\n"
                        + string.Join(
                            '\n',
                            poll.Answers.Select(a =>
                                $"  {(poll.Votes.TryGetValue(a.Id, out string[]? votes) ? votes.Length : 0)} × {a.Text}"
                            )
                        ),
                    null
                ),
                MsgLikeKind.Redacted => (EntryKind.Redacted, "(deleted)", null),
                MsgLikeKind.UnableToDecrypt => (EntryKind.Encrypted, "(unable to decrypt, waiting for the keys)", null),
                _ => (EntryKind.Event, "(unsupported message)", null),
            },
            TimelineItemContent.RoomMembership membership => (
                EntryKind.Event,
                DescribeMembership(
                    membership.UserDisplayName ?? membership.UserId,
                    membership.Change,
                    membership.Reason
                ),
                null
            ),
            TimelineItemContent.ProfileChange profile => (EntryKind.Event, DescribeProfile(sender, profile), null),
            TimelineItemContent.State state => (EntryKind.Event, DescribeState(sender, state.Content), null),
            TimelineItemContent.CallInvite or TimelineItemContent.RtcNotification => (
                EntryKind.Event,
                $"{sender} started a call",
                null
            ),
            TimelineItemContent.FailedToParseMessageLike failed => (
                EntryKind.Event,
                $"(a {failed.EventType} event that can't be shown)",
                null
            ),
            _ => (EntryKind.Event, "(an event that can't be shown)", null),
        };

    private static (EntryKind, string, MediaAttachment?) DescribeMessage(MessageContent message) =>
        message.MsgType switch
        {
            MessageType.Emote => (EntryKind.Emote, message.Body, null),
            MessageType.Notice => (EntryKind.Notice, message.Body, null),
            MessageType.Image { Content: var c } => Media(
                "image",
                c.Filename,
                c.Caption,
                c.Info?.Mimetype,
                c.Info?.Size,
                c.Source
            ),
            MessageType.File { Content: var c } => Media(
                "file",
                c.Filename,
                c.Caption,
                c.Info?.Mimetype,
                c.Info?.Size,
                c.Source
            ),
            MessageType.Video { Content: var c } => Media(
                "video",
                c.Filename,
                c.Caption,
                c.Info?.Mimetype,
                c.Info?.Size,
                c.Source
            ),
            MessageType.Audio { Content: var c } => Media(
                "audio",
                c.Filename,
                c.Caption,
                c.Info?.Mimetype,
                c.Info?.Size,
                c.Source
            ),
            MessageType.Location { Content: var c } => (EntryKind.Message, $"location: {c.Body} ({c.GeoUri})", null),
            _ => (EntryKind.Message, message.Body, null),
        };

    private static (EntryKind, string, MediaAttachment?) Media(
        string kind,
        string filename,
        string? caption,
        string? mimeType,
        ulong? size,
        MediaSource source
    ) => (EntryKind.Media, caption ?? "", new MediaAttachment(kind, filename, mimeType, size, source.ToJson()));

    public static string DescribeMembership(string user, MembershipChange? change, string? reason)
    {
        string text = change switch
        {
            MembershipChange.Joined => $"{user} joined",
            MembershipChange.Left => $"{user} left",
            MembershipChange.Banned or MembershipChange.KickedAndBanned => $"{user} was banned",
            MembershipChange.Unbanned => $"{user} was unbanned",
            MembershipChange.Kicked => $"{user} was removed",
            MembershipChange.Invited => $"{user} was invited",
            MembershipChange.InvitationAccepted => $"{user} accepted the invite",
            MembershipChange.InvitationRejected => $"{user} declined the invite",
            MembershipChange.InvitationRevoked => $"the invite of {user} was revoked",
            MembershipChange.Knocked => $"{user} asked to join",
            MembershipChange.KnockAccepted => $"{user} was let in",
            MembershipChange.KnockRetracted => $"{user} no longer asks to join",
            MembershipChange.KnockDenied => $"{user} was not let in",
            _ => $"{user} changed their membership",
        };
        return string.IsNullOrWhiteSpace(reason) ? text : $"{text}: {reason}";
    }

    private static string DescribeProfile(string sender, TimelineItemContent.ProfileChange profile)
    {
        if (profile.DisplayName == profile.PrevDisplayName)
        {
            return $"{sender} changed their avatar";
        }
        if (profile.DisplayName is null)
        {
            return $"{profile.PrevDisplayName} removed their display name";
        }
        return profile.PrevDisplayName is null
            ? $"{sender} set their display name to {profile.DisplayName}"
            : $"{profile.PrevDisplayName} is now {profile.DisplayName}";
    }

    private static string DescribeState(string sender, OtherState state) =>
        state switch
        {
            OtherState.RoomName { Name: { } name } => $"{sender} renamed the room to {name}",
            OtherState.RoomName => $"{sender} removed the room name",
            OtherState.RoomTopic { Topic: { } topic } => $"{sender} changed the topic to: {topic}",
            OtherState.RoomTopic => $"{sender} removed the topic",
            OtherState.RoomCreate => $"{sender} created the room",
            OtherState.RoomEncryption => $"{sender} enabled encryption",
            OtherState.RoomAvatar => $"{sender} changed the room avatar",
            OtherState.RoomCanonicalAlias => $"{sender} changed the room address",
            OtherState.RoomJoinRules { JoinRule: { } rule } => $"{sender} changed who can join to {Describe(rule)}",
            OtherState.RoomHistoryVisibility => $"{sender} changed who can read the history",
            OtherState.RoomPowerLevels => $"{sender} changed the power levels",
            OtherState.RoomPinnedEvents => $"{sender} changed the pinned messages",
            OtherState.RoomTombstone => $"{sender} upgraded the room",
            OtherState.RoomGuestAccess => $"{sender} changed the guest access",
            OtherState.RoomServerAcl => $"{sender} changed the server access",
            OtherState.RoomThirdPartyInvite { DisplayName: var name } => $"{sender} invited {name ?? "someone"}",
            OtherState.SpaceChild or OtherState.SpaceParent => $"{sender} changed the space",
            OtherState.Custom custom => $"{sender} changed {custom.EventType}",
            _ => $"{sender} changed the room settings",
        };

    private static string Describe(JoinRule rule) =>
        rule switch
        {
            JoinRule.Public => "anyone",
            JoinRule.Invite => "invited users",
            JoinRule.Knock => "users who ask",
            JoinRule.Restricted or JoinRule.KnockRestricted => "members of other rooms",
            _ => "something custom",
        };
}
