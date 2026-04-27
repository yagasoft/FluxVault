using System.ComponentModel;
using FluxVault.Abstractions.ChangeTracking;

namespace FluxVault.Windows.ChangeTracking;

public static class UsnJournalDiagnosticFormatter
{
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;

    public static DurableChangeDetail OpenVolumeFailed(
        string? watchedFolderId,
        string? path,
        string? volumeRoot,
        string volumePath,
        int win32ErrorCode)
    {
        return new DurableChangeDetail(
            watchedFolderId,
            path,
            volumeRoot,
            "Open volume",
            $"Unable to open volume {volumePath}: {Win32Message(win32ErrorCode)}",
            win32ErrorCode);
    }

    public static DurableChangeDetail DeviceIoControlFailed(
        string? watchedFolderId,
        string? path,
        string? volumeRoot,
        string operation,
        int win32ErrorCode)
    {
        var prefix = string.Equals(operation, "FSCTL_QUERY_USN_JOURNAL", StringComparison.Ordinal)
                     && IsUnsupportedVolumeError(win32ErrorCode)
            ? "Unsupported volume"
            : $"{operation} failed";
        return new DurableChangeDetail(
            watchedFolderId,
            path,
            volumeRoot,
            operation,
            $"{prefix} on {volumeRoot ?? "unknown volume"}: {Win32Message(win32ErrorCode)}",
            win32ErrorCode);
    }

    public static DurableChangeDetail FileIdPathResolutionFailed(
        string? watchedFolderId,
        string? path,
        string? volumeRoot,
        string fileReference,
        int win32ErrorCode)
    {
        return new DurableChangeDetail(
            watchedFolderId,
            path,
            volumeRoot,
            "Resolve file ID path",
            $"File-id path resolution failed for USN record {fileReference}: {Win32Message(win32ErrorCode)}",
            win32ErrorCode);
    }

    public static DurableChangeDetail UnexpectedFailure(
        string? watchedFolderId,
        string? path,
        string? volumeRoot,
        string operation,
        Exception exception)
    {
        return new DurableChangeDetail(
            watchedFolderId,
            path,
            volumeRoot,
            operation,
            $"{operation} failed: {exception.Message}",
            exception is Win32Exception win32 ? win32.NativeErrorCode : null);
    }

    private static bool IsUnsupportedVolumeError(int win32ErrorCode)
    {
        return win32ErrorCode is ErrorInvalidFunction or ErrorNotSupported or ErrorInvalidParameter;
    }

    private static string Win32Message(int win32ErrorCode)
    {
        return new Win32Exception(win32ErrorCode).Message.TrimEnd('.');
    }
}
