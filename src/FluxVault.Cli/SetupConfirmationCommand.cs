using System.Globalization;
using System.Text.Json;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Ipc;
using FluxVault.Core.Security;

namespace FluxVault.Cli;

/// <summary>Creator proof for a prepared installation; no lifecycle or storage selection.</summary>
internal static class SetupConfirmationCommand
{
    internal static async Task<int> RunAsync(IReadOnlyList<string> arguments, TextWriter output, TextWriter error,
        Func<IFluxVaultServiceClient> clientFactory, CancellationToken cancellationToken)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var option = arguments[index];
            if (option is not ("--instance" or "--timeout-seconds") || index + 1 >= arguments.Count ||
                !options.TryAdd(option, arguments[index + 1]))
                throw new ArgumentException("Use setup-confirm --instance <canonical UUID N> [--timeout-seconds <1..600>]; duplicate or unknown options are refused.");
        }
        if (!options.TryGetValue("--instance", out var instanceText) ||
            !Guid.TryParseExact(instanceText, "N", out var instance) || instance == Guid.Empty || instanceText != instance.ToString("N"))
            throw new ArgumentException("Setup confirmation requires one canonical nonempty installation UUID in N format.");
        var seconds = FluxVaultProvisioningTicket.DefaultTimeoutSeconds;
        if (options.TryGetValue("--timeout-seconds", out var timeoutText) &&
            (!int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ||
             seconds is < 1 or > FluxVaultProvisioningTicket.MaximumTimeoutSeconds))
            throw new ArgumentException("Setup confirmation timeout must be an integer from 1 to 600 seconds.");

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
            deadline.Token.ThrowIfCancellationRequested();
            var response = await clientFactory().SendAsync(FluxVaultIpcRequest.GetStatus() with { OperationId = instance },
                deadline.Token).ConfigureAwait(false);
            if (!response.Success || response.ErrorCode is not null || response.ErrorMessage is not null ||
                response.OperationId != instance || response.VaultId is not { IsValid: true } || response.VaultRevision != 1)
                return await UnconfirmedAsync(error).ConfigureAwait(false);
            // A complete response is authoritative even if cancellation follows publication.
            await output.WriteLineAsync($"Provisioning confirmed. Installation: {instance:N}. Vault: {response.VaultId.Value}. Revision: 1.").ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or OperationCanceledException or TimeoutException or JsonException or PlatformNotSupportedException)
        {
            return await UnconfirmedAsync(error).ConfigureAwait(false);
        }
    }

    private static async Task<int> UnconfirmedAsync(TextWriter error)
    {
        await error.WriteLineAsync("Provisioning is not confirmed. Owned state may exist. Reconcile the protected bootstrap and ordinary authorised status before operator recovery. Do not retry setup automatically.").ConfigureAwait(false);
        return 3;
    }
}
