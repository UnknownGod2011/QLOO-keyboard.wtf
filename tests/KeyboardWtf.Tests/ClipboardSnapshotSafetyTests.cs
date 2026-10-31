namespace KeyboardWtf.Tests;

using KeyboardWtf.Helpers;
using Xunit;

// Regression: restoring a clipboard snapshot taken while Word (OLE) content was on the clipboard
// crashed QLOO in ole32.dll after "analyze the selected text". Snapshots must hold only detached
// managed copies, never apartment-bound COM objects or OLE link/embedding formats.
public sealed class ClipboardSnapshotSafetyTests
{
    [Fact]
    public void Only_detached_managed_values_are_kept_for_restore()
    {
        Assert.Equal("text", ClipboardHelper.ToRestorableValue("text"));

        var bytes = new byte[] { 1, 2, 3 };
        var copiedBytes = Assert.IsType<byte[]>(ClipboardHelper.ToRestorableValue(bytes));
        Assert.NotSame(bytes, copiedBytes);
        Assert.Equal(bytes, copiedBytes);

        using var stream = new MemoryStream(new byte[] { 4, 5 });
        using var copiedStream = Assert.IsType<MemoryStream>(ClipboardHelper.ToRestorableValue(stream));
        Assert.NotSame(stream, copiedStream);
        Assert.Equal(new byte[] { 4, 5 }, copiedStream.ToArray());

        var files = new[] { @"C:\a.txt" };
        var copiedFiles = Assert.IsType<string[]>(ClipboardHelper.ToRestorableValue(files));
        Assert.NotSame(files, copiedFiles);

        Assert.Null(ClipboardHelper.ToRestorableValue(new object()));
        Assert.Null(ClipboardHelper.ToRestorableValue(null));
    }

    [Theory]
    [InlineData("Embed Source")]
    [InlineData("Link Source")]
    [InlineData("Link Source Descriptor")]
    [InlineData("Object Descriptor")]
    [InlineData("Ole Private Data")]
    [InlineData("embed source")]
    [InlineData("EnhancedMetafile")]
    [InlineData("FileContents")]
    public void Ole_object_formats_are_never_restored(string format) =>
        Assert.Contains(format, ClipboardHelper.NonRestorableFormats);
}
