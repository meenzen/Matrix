using System.Collections.Concurrent;
using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Examples.TuiClient.Rooms;
using Timeline = Matrix.RustSdk.Bindings.Timeline;

namespace Matrix.RustSdk.Examples.TuiClient.Chat;

/// <summary>
/// The room the user opened: its live timeline as <see cref="TimelineEntry"/>s, who is typing and what the user can do
/// in the room. An invite has no timeline until it is accepted (<see cref="AcceptInviteAsync"/>). Like
/// <see cref="MatrixSession"/> this class doesn't know anything about the UI, the events are raised on thread pool
/// threads.
/// </summary>
public sealed class OpenedRoom : IAsyncDisposable
{
    private const ushort PageEvents = 50;

    private readonly Client _client;
    private readonly Room _room;
    private readonly string _ownUserId;
    private readonly CancellationTokenSource _closing = new();
    private readonly List<Task> _watchers = [];

    // the replies whose details were requested, so they are only requested once
    private readonly ConcurrentDictionary<string, bool> _fetchedReplies = new();
    private readonly ConcurrentDictionary<string, string> _names = new();

    // the send handles of messages that failed to send, by the key of their entry, to send them again
    private readonly ConcurrentDictionary<string, SendHandle> _failedSends = new();

    // protects the start of the timeline against closing the room at the same time
    private readonly Lock _lock = new();
    private bool _closed;
    private Timeline? _timeline;
    private LiveList<TimelineEntry>? _entries;
    private volatile string[] _typing = [];
    private int _paginating;

    private OpenedRoom(Client client, Room room, string ownUserId, RoomSummary summary)
    {
        _client = client;
        _room = room;
        _ownUserId = ownUserId;
        RoomId = room.Id();
        Summary = summary;
    }

    public string RoomId { get; }

    /// <summary>
    /// The latest state of the room: name, topic, membership.
    /// </summary>
    public RoomSummary Summary { get; private set; }

    public bool IsInvite => Summary.IsInvite;

    /// <summary>
    /// The timeline, oldest first. Empty for invites.
    /// </summary>
    public IReadOnlyList<TimelineEntry> Entries => _entries?.ToArray() ?? [];

    /// <summary>
    /// The names of the users who are typing, without the own user.
    /// </summary>
    public IReadOnlyList<string> TypingUsers => [.. _typing.Select(NameOf)];

    /// <summary>
    /// Whether the timeline reached the start of the room, there is nothing left to load.
    /// </summary>
    public bool ReachedStart { get; private set; }

    /// <summary>
    /// Whether older messages are being loaded.
    /// </summary>
    public bool IsPaginating => Volatile.Read(ref _paginating) == 1;

    /// <summary>
    /// The user ids and names of the members and senders seen so far, for completion.
    /// </summary>
    public IReadOnlyDictionary<string, string> KnownUsers => _names;

    public event EventHandler? EntriesChanged;

    public event EventHandler? TypingChanged;

    public event EventHandler? SummaryChanged;

    /// <summary>
    /// Opens <paramref name="room"/>, which is owned by the opened room from now on.
    /// </summary>
    public static async Task<OpenedRoom> OpenAsync(Client client, Room room, string ownUserId)
    {
        await ThreadPoolHop.Yield();
        OpenedRoom opened;
        try
        {
            using RoomInfo info = await room.RoomInfo();
            opened = new OpenedRoom(client, room, ownUserId, RoomSummary.From(info));
            if (info.Inviter is { } inviter)
            {
                opened.Summary = opened.Summary with { Inviter = inviter.DisplayName ?? inviter.UserId };
            }
        }
        catch
        {
            room.Dispose();
            throw;
        }

        try
        {
            opened._watchers.Add(opened.WatchSummaryAsync());
            if (!opened.IsInvite)
            {
                await opened.OpenTimelineAsync();
            }
            return opened;
        }
        catch
        {
            await opened.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Joins the room the user is invited to and opens its timeline.
    /// </summary>
    public async Task AcceptInviteAsync()
    {
        await ThreadPoolHop.Yield();
        await _room.Join();
        Summary = Summary with { Membership = Membership.Joined };
        try
        {
            await OpenTimelineAsync();
        }
        catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException && _closed)
        {
            // another room was opened while joining, the join worked
            return;
        }
        SummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Declines the invite or leaves the room.
    /// </summary>
    public async Task LeaveAsync()
    {
        await ThreadPoolHop.Yield();
        await _room.Leave();
    }

    /// <summary>
    /// Sends a text message, markdown is converted to HTML. A reply refers to <paramref name="replyTo"/>. The message
    /// shows up in the timeline right away as a local echo and is updated once the server confirms it.
    /// </summary>
    public async Task SendAsync(string text, TimelineEntry? replyTo = null)
    {
        await ThreadPoolHop.Yield();
        await Timeline.SendMarkdownAsync(text, replyTo?.EventId);
    }

    public async Task SendEmoteAsync(string text)
    {
        await ThreadPoolHop.Yield();
        using RoomMessageEventContentWithoutRelation content =
            MatrixSdkFfiMethods.MessageEventContentFromMarkdownAsEmote(text);
        using SendHandle handle = await Timeline.Send(content);
    }

    /// <summary>
    /// Replaces the text of an own message.
    /// </summary>
    public async Task EditAsync(TimelineEntry entry, string text)
    {
        await ThreadPoolHop.Yield();
        if (!entry.IsEditable || entry.Id is null)
        {
            throw new InvalidOperationException("Only your own text messages can be edited.");
        }
        using RoomMessageEventContentWithoutRelation content = MatrixSdkFfiMethods.MessageEventContentFromMarkdown(
            text
        );
        await Timeline.Edit(entry.Id, new EditedContent.RoomMessage(content));
    }

    /// <summary>
    /// Deletes a message (redacts it), a message that wasn't sent yet is cancelled.
    /// </summary>
    public async Task RedactAsync(TimelineEntry entry, string? reason = null)
    {
        await ThreadPoolHop.Yield();
        await Timeline.RedactEvent(
            entry.Id ?? throw new InvalidOperationException("This entry can't be deleted."),
            reason
        );
    }

    /// <summary>
    /// Adds the reaction, or removes it if the user reacted with it already. Returns whether it was added.
    /// </summary>
    public async Task<bool> ToggleReactionAsync(TimelineEntry entry, string key)
    {
        await ThreadPoolHop.Yield();
        return await Timeline.ToggleReaction(
            entry.Id ?? throw new InvalidOperationException("You can't react to this."),
            key
        );
    }

    /// <summary>
    /// Loads older messages. Does nothing while loading or when the start of the room was reached.
    /// </summary>
    public async Task PaginateBackwardsAsync()
    {
        await ThreadPoolHop.Yield();
        if (_timeline is not { } timeline || ReachedStart || Interlocked.Exchange(ref _paginating, 1) == 1)
        {
            return;
        }
        // the timeline shows that older messages are loading
        EntriesChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            ReachedStart = await timeline.PaginateBackwards(PageEvents);
        }
        finally
        {
            Volatile.Write(ref _paginating, 0);
            EntriesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Sends a message that failed to send again.
    /// </summary>
    public async Task ResendAsync(TimelineEntry entry)
    {
        await ThreadPoolHop.Yield();
        if (!_failedSends.TryGetValue(entry.Key, out SendHandle? handle))
        {
            throw new InvalidOperationException("Only messages that failed to send can be sent again.");
        }
        // the send queue of the room stops after an error
        _room.EnableSendQueue(true);
        await handle.TryResend();
    }

    /// <summary>
    /// Sends a read receipt for the latest message and clears the unread mark of the room.
    /// </summary>
    public async Task MarkAsReadAsync()
    {
        await ThreadPoolHop.Yield();
        if (_timeline is null)
        {
            return;
        }
        await _timeline.MarkAsRead(ReceiptType.Read);
        if (Summary.IsMarkedUnread)
        {
            await _room.SetUnreadFlag(false);
        }
    }

    /// <summary>
    /// Tells the others that the user is typing. The SDK throttles the notices, this can be called for every key.
    /// </summary>
    public async Task SetTypingAsync(bool isTyping)
    {
        await ThreadPoolHop.Yield();
        if (!IsInvite)
        {
            await _room.TypingNotice(isTyping);
        }
    }

    /// <summary>
    /// Sends a file, images, videos and audio files are sent as such so other clients show them inline.
    /// </summary>
    public async Task UploadAsync(string path, TimelineEntry? replyTo = null)
    {
        await ThreadPoolHop.Yield();
        System.IO.FileInfo file = new(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"{path} doesn't exist.", path);
        }
        string mimeType = MimeTypes.ForPath(path);
        ulong size = (ulong)file.Length;
        UploadParameters parameters = new(
            new UploadSource.File(file.FullName),
            Caption: null,
            FormattedCaption: null,
            Mentions: null,
            InReplyTo: replyTo?.EventId
        );
        using SendAttachmentJoinHandle handle = MimeTypes.Kind(mimeType) switch
        {
            "image" => Timeline.SendImage(
                parameters,
                null,
                new ImageInfo(null, null, mimeType, size, null, null, null, null)
            ),
            "video" => Timeline.SendVideo(
                parameters,
                null,
                new VideoInfo(null, null, null, mimeType, size, null, null, null)
            ),
            "audio" => Timeline.SendAudio(parameters, new AudioInfo(null, size, mimeType)),
            _ => Timeline.SendFile(parameters, new Bindings.FileInfo(mimeType, size, null, null)),
        };
        await handle.Join();
    }

    /// <summary>
    /// Downloads an attachment (decrypting it in encrypted rooms) to <paramref name="path"/>, or a file named like the
    /// attachment in <paramref name="directory"/>. Returns the path.
    /// </summary>
    public async Task<string> DownloadAsync(MediaAttachment media, string? path, string directory)
    {
        await ThreadPoolHop.Yield();
        string target = path ?? UniquePath(directory, media.Filename);
        if (Directory.Exists(target))
        {
            target = UniquePath(target, media.Filename);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        using MediaSource source = MediaSource.FromJson(media.SourceJson);
        byte[] content = await _client.GetMediaContent(source);
        await File.WriteAllBytesAsync(target, content, CancellationToken.None);
        return target;
    }

    public async Task<IReadOnlyList<MemberSummary>> GetMembersAsync()
    {
        await ThreadPoolHop.Yield();
        RoomMember[] members = await _room.GetMembersAsync();
        IReadOnlyList<MemberSummary> summaries = MemberSummary.From(members);
        foreach (MemberSummary member in summaries)
        {
            _names[member.UserId] = member.Name;
        }
        return summaries;
    }

    public async Task InviteAsync(string userId)
    {
        await ThreadPoolHop.Yield();
        await _room.InviteUserById(userId);
    }

    public async Task KickAsync(string userId, string? reason)
    {
        await ThreadPoolHop.Yield();
        await _room.KickUser(userId, reason);
    }

    public async Task BanAsync(string userId, string? reason)
    {
        await ThreadPoolHop.Yield();
        await _room.BanUser(userId, reason);
    }

    public async Task UnbanAsync(string userId)
    {
        await ThreadPoolHop.Yield();
        await _room.UnbanUser(userId, null);
    }

    public async Task SetTopicAsync(string topic)
    {
        await ThreadPoolHop.Yield();
        await _room.SetTopic(topic);
    }

    public async Task SetNameAsync(string name)
    {
        await ThreadPoolHop.Yield();
        await _room.SetName(name);
    }

    public async ValueTask DisposeAsync()
    {
        await ThreadPoolHop.Yield();
        Task[] watchers;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            watchers = [.. _watchers];
        }
        await _closing.CancelAsync();
#pragma warning disable VSTHRD003 // started by this class, they end with the cancellation
        await Task.WhenAll(watchers).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
#pragma warning restore VSTHRD003
        if (_entries is not null)
        {
            await _entries.DisposeAsync();
        }
        foreach (SendHandle handle in _failedSends.Values)
        {
            handle.Dispose();
        }
        _timeline?.Dispose();
        _room.Dispose();
        _closing.Dispose();
    }

    private Timeline Timeline => _timeline ?? throw new InvalidOperationException("Accept the invite first (:accept).");

    private async Task OpenTimelineAsync()
    {
        Timeline timeline = await _room.Timeline();
        lock (_lock)
        {
            if (_closed)
            {
                timeline.Dispose();
                throw new ObjectDisposedException(nameof(OpenedRoom));
            }
            _timeline = timeline;
            // the diffs start with the current items, every timeline item is converted when it arrives and disposed
            // right after
            _entries = timeline
                .WatchItemDiffsAsync(_closing.Token)
                .ToLiveList(ToEntry, synchronizationContext: MatrixSession.ThreadPool);
            _entries.Changed += (_, _) => EntriesChanged?.Invoke(this, EventArgs.Empty);
            _watchers.Add(WatchTypingAsync());
            // the room shows right away, older messages and the members follow
            _watchers.Add(LoadHistoryAndMembersAsync(timeline, _closing.Token));
        }
        // the list may have changed before the handler was attached
        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The timeline only contains what the sync loaded so far: loads some history, and the members for the display
    /// names of the senders and completion. Closing the room stops waiting, the SDK calls can't be cancelled.
    /// </summary>
    private async Task LoadHistoryAndMembersAsync(Timeline timeline, CancellationToken cancellationToken)
    {
        try
        {
            await PaginateBackwardsAsync().WaitAsync(cancellationToken);
            await timeline.FetchMembers().WaitAsync(cancellationToken);
            await GetMembersAsync().WaitAsync(cancellationToken);
        }
        catch (Exception e) when (e is OperationCanceledException or ClientException or ObjectDisposedException)
        {
            // closed, or the next pagination tries again; completion falls back to the senders
        }
    }

    /// <summary>
    /// Converts a timeline item. It must not throw, that would stop the timeline.
    /// </summary>
    private TimelineEntry ToEntry(TimelineItem item)
    {
        TimelineEntry entry;
        try
        {
            entry = TimelineEntries.From(item, _ownUserId);
        }
#pragma warning disable CA1031 // whatever an event contains, the timeline keeps going
        catch (Exception)
#pragma warning restore CA1031
        {
            return new TimelineEntry(item.UniqueId().Id, EntryKind.Event, "(an event that can't be shown)");
        }
        TrackSendHandle(item, entry);
        if (entry.SenderId is { } senderId && entry.SenderName is { } name)
        {
            _names[senderId] = name;
        }
        // replies to events that aren't in the timeline are loaded on demand, the item is updated afterwards
        if (entry is { ReplyTo.Body: null, EventId: { } eventId } && _fetchedReplies.TryAdd(eventId, true))
        {
            _ = FetchReplyAsync(eventId);
        }
        return entry;
    }

    /// <summary>
    /// Keeps the send handle of a message that failed to send, so it can be sent again.
    /// </summary>
    private void TrackSendHandle(TimelineItem item, TimelineEntry entry)
    {
        if (entry.Status != SendStatus.Failed)
        {
            if (_failedSends.TryRemove(entry.Key, out SendHandle? sent))
            {
                sent.Dispose();
            }
            return;
        }
        using EventTimelineItem? eventItem = item.AsEvent();
        if (eventItem?.LazyProvider.GetSendHandle() is { } handle)
        {
            _failedSends.AddOrUpdate(
                entry.Key,
                handle,
                (_, previous) =>
                {
                    previous.Dispose();
                    return handle;
                }
            );
        }
    }

    private async Task FetchReplyAsync(string eventId)
    {
        try
        {
            await (_timeline?.FetchDetailsForEvent(eventId) ?? Task.CompletedTask);
        }
        catch (Exception e) when (e is ClientException or ObjectDisposedException)
        {
            // the reply stays "loading", the room may have been closed in the meantime
        }
    }

    private string NameOf(string userId) =>
        _names.TryGetValue(userId, out string? name) ? name : TimelineEntries.Localpart(userId);

    private async Task WatchTypingAsync()
    {
        try
        {
            await foreach (string[] users in _room.WatchTypingUsersAsync(_closing.Token))
            {
                _typing = users;
                TypingChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ClientException or ObjectDisposedException)
        {
            // closed, or the subscription failed
        }
    }

    private async Task WatchSummaryAsync()
    {
        try
        {
            await foreach (RoomInfo info in _room.WatchRoomInfoAsync(_closing.Token))
            {
                using (info)
                {
                    Summary = RoomSummary.From(info) with { Inviter = Summary.Inviter };
                }
                SummaryChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ClientException or ObjectDisposedException)
        {
            // closed, or the subscription failed
        }
    }

    private static string UniquePath(string directory, string filename)
    {
        string name = Path.GetFileName(filename);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "attachment";
        }
        string path = Path.Join(directory, name);
        int copy = 1;
        while (File.Exists(path))
        {
            path = Path.Join(directory, $"{Path.GetFileNameWithoutExtension(name)} ({copy}){Path.GetExtension(name)}");
            copy++;
        }
        return path;
    }
}
