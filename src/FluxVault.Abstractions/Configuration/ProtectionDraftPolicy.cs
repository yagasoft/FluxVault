namespace FluxVault.Abstractions.Configuration;

public sealed record ProtectionDraftPolicy(int SaveDelayMilliseconds = 500)
{
    public ProtectionDraftPolicy Normalise() => this with { SaveDelayMilliseconds = Math.Clamp(SaveDelayMilliseconds,100,5000) };
}
