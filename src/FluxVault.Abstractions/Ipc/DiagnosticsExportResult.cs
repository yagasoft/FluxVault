using FluxVault.Abstractions.Security;

namespace FluxVault.Abstractions.Ipc;

public sealed record DiagnosticsExportResult(string OutputPath, IReadOnlyList<string> Warnings);

public sealed record DiagnosticsExportDocument(int FormatVersion, DateTimeOffset CapturedUtc,
    VaultId VaultId, long ConfigurationRevision, FluxVaultServiceStatus Status);
