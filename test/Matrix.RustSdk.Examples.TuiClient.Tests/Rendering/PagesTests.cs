using Matrix.RustSdk.Examples.TuiClient.Chat;
using Matrix.RustSdk.Examples.TuiClient.Rendering;
using Matrix.RustSdk.Examples.TuiClient.Security;
using Matrix.RustSdk.Testing;

namespace Matrix.RustSdk.Examples.TuiClient.Tests.Rendering;

public class PagesTests
{
    private static string Render(Page page, int width = 70) =>
        $"{page.Title}\n" + string.Join('\n', page.Layout(width).Select(r => r.Text.TrimEnd()));

    [Test]
    public async Task Help_ShouldListTheKeysAndCommands()
    {
        // Act
        string rendered = Render(Pages.Help(), 100);

        // Assert
        await Snapshot.VerifyAsync(rendered);
    }

    [Test]
    public async Task Members_ShouldShowRolesAndInvites()
    {
        // Arrange
        MemberSummary[] members =
        [
            new("@alice:example.org", "Alice", IsInvited: false, PowerLevel: 100, IsIgnored: false),
            new("@bob:example.org", "@bob:example.org", IsInvited: false, PowerLevel: 0, IsIgnored: true),
            new("@carol:example.org", "Carol", IsInvited: true, PowerLevel: 0, IsIgnored: false),
        ];

        // Act
        string rendered = Render(Pages.Members("Book club", members));

        // Assert
        await Snapshot.VerifyAsync(rendered);
    }

    [Test]
    public async Task Verification_ShouldShowTheEmojisToCompare()
    {
        // Arrange
        VerificationStatus status = new(VerificationStep.Comparing)
        {
            Partner = "Alice, Phone (ABCDEF)",
            Emojis = [("🐶", "Dog"), ("🔑", "Key"), ("🎸", "Guitar")],
        };

        // Act
        string rendered = Render(Pages.Verification(status));

        // Assert
        await Snapshot.VerifyAsync(rendered);
    }
}
