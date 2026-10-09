using System.Diagnostics;
using Matrix.RustSdk.Examples.TuiClient.Notifications;

namespace Matrix.RustSdk.Examples.TuiClient;

/// <summary>
/// How the client interacts with the desktop. Tests replace the notifier and the opener.
/// </summary>
public sealed record ClientSettings
{
    /// <summary>
    /// Shows desktop notifications for mentions and direct messages.
    /// </summary>
    public INotifier Notifier { get; init; } = new DesktopNotifier();

    /// <summary>
    /// Whether notifications are shown when the client starts, <c>:notifications</c> toggles it.
    /// </summary>
    public bool NotificationsEnabled { get; init; } = true;

    /// <summary>
    /// Opens a downloaded attachment with the default application, returns false if that isn't possible.
    /// </summary>
    public Func<string, bool> Opener { get; init; } = OpenWithDefaultApplication;

    /// <summary>
    /// Where <c>:save</c> puts attachments without a path: <c>~/Downloads</c>.
    /// </summary>
    public string DownloadDirectory { get; init; } =
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// The time zone of the timestamps, the local one by default.
    /// </summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    private static bool OpenWithDefaultApplication(string path)
    {
        string tool = "xdg-open";
        if (OperatingSystem.IsMacOS())
        {
            tool = "open";
        }
        else if (OperatingSystem.IsWindows())
        {
            tool = "explorer";
        }
        // the tool is looked up in the PATH like a shell does
#pragma warning disable S4036
        ProcessStartInfo start = new(tool)
#pragma warning restore S4036
        {
            ArgumentList = { path },
            UseShellExecute = false,
            // the UI owns the terminal, the application must not write into it
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            Process? process = Process.Start(start);
            if (process is null)
            {
                return false;
            }
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => process.Dispose();
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
