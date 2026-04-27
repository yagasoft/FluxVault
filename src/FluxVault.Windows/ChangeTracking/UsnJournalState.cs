namespace FluxVault.Windows.ChangeTracking;

public sealed record UsnJournalState(
    string VolumeRoot,
    ulong JournalId,
    long FirstUsn,
    long NextUsn);
