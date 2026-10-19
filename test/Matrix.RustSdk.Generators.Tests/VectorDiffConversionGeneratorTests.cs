using System.Runtime.CompilerServices;

namespace Matrix.RustSdk.Generators.Tests;

/// <summary>
/// Snapshot tests of <see cref="VectorDiffConversionGenerator"/>. The snapshots are in <c>Snapshots/</c>, review and
/// accept changes of the generated code there.
/// </summary>
public class VectorDiffConversionGeneratorTests
{
    // the ids of the diagnostics in Diagnostics.cs
    private const string InvalidConversion = "MRSG005";
    private const string NotADiff = "MRSG006";

    private const string Usings = """
        using Matrix.RustSdk.Bindings;
        using Matrix.RustSdk.Subscriptions;

        namespace Matrix.RustSdk;

        """;

    [Test]
    public Task Conversion_ShouldBeGenerated() =>
        VerifyGeneratedAsync(
            """
            internal static partial class RoomListEntriesUpdateExtensions
            {
                [VectorDiffConversion]
                internal static partial VectorDiff<Room> ToVectorDiff(this RoomListEntriesUpdate diff);
            }
            """
        );

    [Test]
    public Task ConversionToAnotherItem_ShouldReportNotADiff() =>
        VerifyErrorAsync(
            NotADiff,
            """
            internal static partial class RoomListEntriesUpdateExtensions
            {
                [VectorDiffConversion]
                internal static partial VectorDiff<TimelineItem> ToVectorDiff(this RoomListEntriesUpdate diff);
            }
            """
        );

    [Test]
    public Task ConversionOfAnotherType_ShouldReportNotADiff() =>
        VerifyErrorAsync(
            NotADiff,
            """
            internal static partial class RoomInfoExtensions
            {
                [VectorDiffConversion]
                internal static partial VectorDiff<Room> ToVectorDiff(this RoomInfo info);
            }
            """
        );

    [Test]
    public Task ConversionWithTwoParameters_ShouldReportInvalidConversion() =>
        VerifyErrorAsync(
            InvalidConversion,
            """
            internal static partial class RoomListEntriesUpdateExtensions
            {
                [VectorDiffConversion]
                internal static partial VectorDiff<Room> ToVectorDiff(this RoomListEntriesUpdate diff, int offset);
            }
            """
        );

    private static Task VerifyGeneratedAsync(
        string declarations,
        [CallerFilePath] string testFile = "",
        [CallerMemberName] string test = ""
    ) => GeneratorRun.VerifyGeneratedAsync(Usings + declarations, testFile, test);

    private static Task VerifyErrorAsync(
        string id,
        string declarations,
        [CallerFilePath] string testFile = "",
        [CallerMemberName] string test = ""
    ) => GeneratorRun.VerifyErrorAsync(id, Usings + declarations, testFile, test);
}
