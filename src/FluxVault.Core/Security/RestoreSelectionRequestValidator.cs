using FluxVault.Abstractions.Ipc;

namespace FluxVault.Core.Security;

public static class RestoreSelectionRequestValidator
{
    public static (string Source, string Destination, RestoreSelectionDestinationMode Mode) Validate(FluxVaultIpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Command is not (FluxVaultIpcCommand.PreviewRestoreSelection or FluxVaultIpcCommand.RunRestoreSelection))
            throw new ArgumentException("A selection recovery command is required.", nameof(request));
        var source = WindowsLocalPath.Validate(request.SourcePath ?? string.Empty);
        var mode = request.DestinationMode ?? RestoreSelectionDestinationMode.Elsewhere;
        if (!Enum.IsDefined(mode)) throw new ArgumentException("A supported recovery destination mode is required.", nameof(request));
        var destination = mode == RestoreSelectionDestinationMode.Original ? source : WindowsLocalPath.Validate(request.DestinationPath ?? string.Empty);
        if (mode == RestoreSelectionDestinationMode.Original && request.DestinationPath is not null &&
            !string.Equals(WindowsLocalPath.Validate(request.DestinationPath), source, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Original recovery must target the selected source path.", nameof(request));
        return (source, destination, mode);
    }
}
