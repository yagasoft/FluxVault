namespace FluxVault.Core.Ipc;

public sealed record FluxVaultIpcLimits
{
    public int MaximumRequestBytes { get; init; } = 1024 * 1024;
    public int MaximumResponseBytes { get; init; } = 8 * 1024 * 1024;
    public int MaximumJsonDepth { get; init; } = 32;
    public int MaximumConcurrentRequests { get; init; } = 16;
    public int PendingListeners { get; init; } = 8;
    public TimeSpan FrameReadTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan FrameWriteTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public void Validate()
    {
        if (MaximumRequestBytes is < 1024 or > 16 * 1024 * 1024 ||
            MaximumResponseBytes is < 1024 or > 64 * 1024 * 1024 ||
            MaximumJsonDepth is < 4 or > 64 || MaximumConcurrentRequests is < 1 or > 24 ||
            PendingListeners is < 1 or > 8 || PendingListeners > MaximumConcurrentRequests ||
            FrameReadTimeout < TimeSpan.FromMilliseconds(10) || FrameReadTimeout > TimeSpan.FromMinutes(1) ||
            FrameWriteTimeout < TimeSpan.FromMilliseconds(10) || FrameWriteTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(FluxVaultIpcLimits), "IPC limits must be finite and within supported bounds.");
    }
}
