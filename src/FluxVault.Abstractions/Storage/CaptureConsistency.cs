namespace FluxVault.Abstractions.Storage;

public enum CaptureConsistency
{
    Failed = 0,
    BestEffort = 1,
    CrashConsistent = 2,
    AppConsistent = 3
}
