using FluxVault.Windows.ChangeTracking;

namespace FluxVault.Windows.Tests;

public sealed class UsnJournalDiagnosticFormatterTests
{
    [Fact]
    public void Open_volume_failure_names_volume_and_win32_error()
    {
        var detail = UsnJournalDiagnosticFormatter.OpenVolumeFailed(
            "docs",
            @"D:\Work",
            @"D:\",
            @"\\.\D:",
            5);

        Assert.Equal("Open volume", detail.Operation);
        Assert.Equal(@"D:\", detail.VolumeRoot);
        Assert.Equal(5, detail.Win32ErrorCode);
        Assert.Contains("Unable to open volume", detail.Reason);
        Assert.Contains(@"\\.\D:", detail.Reason);
    }

    [Fact]
    public void Query_journal_invalid_function_is_reported_as_unsupported_volume()
    {
        var detail = UsnJournalDiagnosticFormatter.DeviceIoControlFailed(
            "docs",
            @"D:\Work",
            @"D:\",
            "FSCTL_QUERY_USN_JOURNAL",
            1);

        Assert.Equal("FSCTL_QUERY_USN_JOURNAL", detail.Operation);
        Assert.Contains("Unsupported volume", detail.Reason);
        Assert.Equal(1, detail.Win32ErrorCode);
    }

    [Fact]
    public void File_id_path_resolution_failure_names_record_and_error()
    {
        var detail = UsnJournalDiagnosticFormatter.FileIdPathResolutionFailed(
            "docs",
            @"D:\Work",
            @"D:\",
            "0x0000000000000042",
            2);

        Assert.Equal("Resolve file ID path", detail.Operation);
        Assert.Contains("File-id path resolution failed", detail.Reason);
        Assert.Contains("0x0000000000000042", detail.Reason);
        Assert.Equal(2, detail.Win32ErrorCode);
    }
}
