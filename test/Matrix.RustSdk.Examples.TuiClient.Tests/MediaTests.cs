using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Examples.TuiClient.Chat;
using Matrix.RustSdk.Examples.TuiClient.Tests.Support;
using Matrix.RustSdk.Testing;
using Terminal.Gui.Input;

namespace Matrix.RustSdk.Examples.TuiClient.Tests;

/// <summary>
/// Attachments in the TUI client: uploading files, saving and opening the ones others sent.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class MediaTests(Homeserver homeserver)
{
    [Test]
    public async Task UploadAndDownload_ShouldTransferFiles()
    {
        // Arrange
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await using Peer peer = await Peer.StartAsync(homeserver);
        using TemporaryDirectory files = new();
        string name = $"Files {Guid.NewGuid().ToString("N")[..6]}";
        string roomId = await peer.CreateRoomAsync(name, invite: [tui.User.UserId]);
        LiveList<TimelineEntry> peerTimeline = await peer.WatchAsync(roomId);
        await RoomTests.AcceptInviteAsync(tui, name);
        string upload = Path.Join(files.Path, "notes.txt");
        await File.WriteAllTextAsync(upload, "notes from the terminal");

        // Act: upload
        await tui.NormalModeAsync();
        await tui.CommandAsync($"upload {upload}");

        // Assert
        await tui.WaitForTextAsync("[file] notes.txt (text/plain, 23 B)");
        TimelineEntry uploaded = await Peer.WaitForEntryAsync(
            peerTimeline,
            e => e is { Kind: EntryKind.Media, Media.Filename: "notes.txt" },
            "the uploaded file"
        );
        using (MediaSource source = MediaSource.FromJson(uploaded.Media!.SourceJson))
        {
            byte[] content = await peer.Client.GetMediaContent(source);
            await Assert.That(System.Text.Encoding.UTF8.GetString(content)).IsEqualTo("notes from the terminal");
        }

        // Arrange: the peer sends a file
        string sent = Path.Join(files.Path, "report.pdf");
        byte[] report = [0x25, 0x50, 0x44, 0x46, 1, 2, 3, 4];
        await File.WriteAllBytesAsync(sent, report);
        Bindings.Timeline timeline = await peer.TimelineAsync(roomId);
        using (
            SendAttachmentJoinHandle handle = timeline.SendFile(
                new UploadParameters(new UploadSource.File(sent), null, null, null, null),
                new Bindings.FileInfo("application/pdf", (ulong)report.Length, null, null)
            )
        )
        {
            await handle.Join();
        }
        await tui.WaitForTextAsync("[file] report.pdf (application/pdf, 8 B)");

        // Act: the newest message is selected in normal mode, :save stores it in the download directory
        await tui.PressAsync('l');
        await tui.PressAsync('G');
        await tui.CommandAsync("save");

        // Assert
        string saved = Path.Join(tui.DownloadDirectory, "report.pdf");
        await tui.WaitForTextAsync($"Saved {saved}.");
        await Assert.That(await File.ReadAllBytesAsync(saved)).IsEquivalentTo(report);

        // Act: o opens it with the default application
        await tui.PressAsync('o');

        // Assert
        await Poll.UntilAsync(() => Task.FromResult(tui.OpenedFiles.Count == 1), "the file to be opened");
        await Assert.That(await File.ReadAllBytesAsync(tui.OpenedFiles.Single())).IsEquivalentTo(report);
    }
}
