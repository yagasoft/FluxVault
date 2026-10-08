using System.Buffers.Binary;
using System.Text;
using FluxVault.Abstractions.Storage;
using FluxVault.Windows.Security;
using System.Runtime.Versioning;

namespace FluxVault.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsDirectoryBatchTests
{
    [Fact]
    public void Malformed_utf16_is_not_rewritten_as_a_replacement_character_sibling()
    {
        var malformed = Record("\uFFFD.txt", 0x80, last: false);
        BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(64), 0xD800);
        var sibling = Record("\uFFFD.txt", 0x80, last: true);
        Assert.Throws<IOException>(() => WindowsProtectionSourceAccess.ParseBatch([.. malformed, .. sibling]));
    }
    [Theory]
    [InlineData(0xC0000034u, true)]
    [InlineData(0xC000003Au, true)]
    [InlineData(0xC000000Eu, false)]
    [InlineData(0xC00000A3u, false)]
    [InlineData(0xC0000043u, false)]
    public void Native_absence_classification_preserves_status_instead_of_translated_error(uint status, bool missing)
    {
        var error = new WindowsCallerFileAccess.NativeOpenException(unchecked((int)status), 2);
        Assert.Equal(unchecked((int)status), error.NtStatus);
        Assert.Equal(missing, WindowsCallerFileAccess.IsConfirmedMissingOpen(error, validatedChain: true));
        Assert.False(WindowsCallerFileAccess.IsConfirmedMissingOpen(error, validatedChain: false));
    }
    [Fact]
    public void Complete_batch_preserves_names_and_skips_only_dot_entries()
    {
        var dot = Record(".", 0x10, last: false); var parent = Record("..", 0x10, last: false);
        var file = Record("drawing-图纸.txt", 0x80, last: false); var folder = Record("nested", 0x10, last: true);
        var entries = WindowsProtectionSourceAccess.ParseBatch([.. dot, .. parent, .. file, .. folder]);
        Assert.Collection(entries,
            item => { Assert.Equal("drawing-图纸.txt", item.Name); Assert.Equal(RepositoryEntryKind.File, item.Kind); },
            item => { Assert.Equal("nested", item.Name); Assert.Equal(RepositoryEntryKind.Folder, item.Kind); });
    }

    [Theory]
    [InlineData("header")]
    [InlineData("name")]
    [InlineData("odd-name")]
    [InlineData("offset")]
    [InlineData("alignment")]
    [InlineData("trailing-header")]
    [InlineData("component")]
    public void Truncated_or_invalid_record_is_a_failed_scan(string defect)
    {
        var bytes = Record("document.txt", 0x80, last: true);
        switch (defect)
        {
            case "header": bytes = bytes[..63]; break;
            case "name": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), 500); break;
            case "odd-name": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), 3); break;
            case "offset": BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length + 8); break;
            case "alignment": BinaryPrimitives.WriteUInt32LittleEndian(bytes, 65); break;
            case "trailing-header": BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length); bytes = [.. bytes, 0]; break;
            case "component": bytes = Record("../outside", 0x80, last: true); break;
        }
        Assert.Throws<IOException>(() => WindowsProtectionSourceAccess.ParseBatch(bytes));
    }

    private static byte[] Record(string name, uint attributes, bool last)
    {
        var text = Encoding.Unicode.GetBytes(name); var size = (64 + text.Length + 7) & ~7; var bytes = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, last ? 0u : (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), attributes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), (uint)text.Length);
        text.CopyTo(bytes, 64); return bytes;
    }
}
