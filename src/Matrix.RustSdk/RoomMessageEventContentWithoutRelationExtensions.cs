namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="RoomMessageEventContentWithoutRelation"/>, the content <see cref="Timeline.Send"/> and
/// <see cref="Timeline.SendReply"/> take.
/// </summary>
/// <remarks>
/// The contents are native objects and belong to the caller. Sending copies them, dispose them once
/// <see cref="Timeline.Send"/> returned. <see cref="TimelineExtensions.SendTextAsync"/> and the other send helpers
/// create and dispose them for you.
/// </remarks>
public static class RoomMessageEventContentWithoutRelationExtensions
{
    extension(RoomMessageEventContentWithoutRelation)
    {
        /// <summary>
        /// Creates a text message (<c>m.text</c>), the kind of message people send.
        /// </summary>
        /// <param name="body">The plain text body.</param>
        /// <param name="html">
        /// The HTML version of the body, for clients that show formatted messages. <paramref name="body"/> should
        /// contain the same text without formatting.
        /// </param>
        public static RoomMessageEventContentWithoutRelation Text(string body, string? html = null)
        {
            ArgumentNullException.ThrowIfNull(body);
            return MatrixSdkFfiMethods.MessageEventContentNew(
                new MessageType.Text(new TextMessageContent(body, Html(html)))
            );
        }

        /// <summary>
        /// Creates a text message (<c>m.text</c>) from markdown. The markdown is the plain text body, the HTML it
        /// converts to is the formatted body if the markdown contains any formatting.
        /// </summary>
        /// <param name="markdown">The markdown.</param>
        public static RoomMessageEventContentWithoutRelation Markdown(string markdown)
        {
            ArgumentNullException.ThrowIfNull(markdown);
            return MatrixSdkFfiMethods.MessageEventContentFromMarkdown(markdown);
        }

        /// <summary>
        /// Creates a notice (<c>m.notice</c>), the kind of message bots send. By convention bots don't answer notices,
        /// which keeps bots from answering each other in a loop.
        /// </summary>
        /// <param name="body">The plain text body.</param>
        /// <param name="html">
        /// The HTML version of the body, for clients that show formatted messages. <paramref name="body"/> should
        /// contain the same text without formatting.
        /// </param>
        public static RoomMessageEventContentWithoutRelation Notice(string body, string? html = null)
        {
            ArgumentNullException.ThrowIfNull(body);
            return MatrixSdkFfiMethods.MessageEventContentNew(
                new MessageType.Notice(new NoticeMessageContent(body, Html(html)))
            );
        }
    }

    private static FormattedBody? Html(string? html) =>
        html is null ? null : new FormattedBody(new MessageFormat.Html(), html);
}
