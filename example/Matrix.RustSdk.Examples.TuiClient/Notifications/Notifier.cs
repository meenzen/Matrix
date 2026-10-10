using System.Diagnostics;

namespace Matrix.RustSdk.Examples.TuiClient.Notifications;

/// <summary>
/// Shows desktop notifications. Implementations must not block and never throw, a notification that can't be shown
/// is dropped.
/// </summary>
public interface INotifier
{
    void Notify(string title, string body);
}

/// <summary>
/// Shows notifications with <c>notify-send</c> on Linux and <c>osascript</c> on macOS, does nothing elsewhere or if
/// the tool is missing.
/// </summary>
public sealed class DesktopNotifier : INotifier
{
    private const int MaxBodyLength = 300;

    /// <summary>
    /// Reads the output of a started tool so it never blocks on a full pipe, and disposes the process when it exits.
    /// </summary>
    internal static void DrainAndDispose(Process process)
    {
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => process.Dispose();
    }

    public void Notify(string title, string body)
    {
        if (body.Length > MaxBodyLength)
        {
            body = body[..MaxBodyLength] + "…";
        }
        ProcessStartInfo? start = null;
        // the tools are looked up in the PATH like a shell does
#pragma warning disable S4036
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            start = new ProcessStartInfo("notify-send")
            {
                ArgumentList = { "--app-name=Matrix TUI", "--", title, body },
            };
        }
        else if (OperatingSystem.IsMacOS())
        {
            start = new ProcessStartInfo("osascript")
            {
                ArgumentList =
                {
                    "-e",
                    "on run argv\ndisplay notification (item 2 of argv) with title (item 1 of argv)\nend run",
                    title,
                    body,
                },
            };
        }
#pragma warning restore S4036
        if (start is null)
        {
            return;
        }
        start.UseShellExecute = false;
        // the UI owns the terminal, the tool must not write into it
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        try
        {
            Process? process = Process.Start(start);
            if (process is not null)
            {
                DrainAndDispose(process);
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // the tool isn't installed
        }
    }
}

/// <summary>
/// Decides which notifications are shown: mentions, direct messages and what the push rules make noisy, not the room the user is reading, not
/// messages older than the start of the client (the first sync delivers what was missed while the client wasn't
/// running).
/// </summary>
public static class NotificationPolicy
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    public static bool ShouldNotify(
        IncomingNotification notification,
        string? readingRoomId,
        DateTimeOffset startedAt
    ) =>
        (notification.HasMention || notification.IsDirect || notification.IsNoisy)
        && notification.RoomId != readingRoomId
        && notification.Time >= startedAt - ClockSkew;

    public static (string Title, string Body) Format(IncomingNotification notification) =>
        notification.IsDirect
            ? (notification.Sender, notification.Body)
            : ($"{notification.Sender} in {notification.RoomName}", notification.Body);
}
