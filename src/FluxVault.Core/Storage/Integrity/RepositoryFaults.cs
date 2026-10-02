namespace FluxVault.Core.Storage.Integrity;

// Constructor-injected, test-only barriers. Production never supplies a callback.
internal enum RepositoryFaultPoint
{
    ObjectPayloadPublished,
    AfterMetadataRecorded,
    BeforeRestorePublication,
    AfterRestorePublication,
    BeforeRestoreHint,
    DepartingPayloadDeleted
}

internal sealed record RepositoryFaults(Action<RepositoryFaultPoint, string> Hit);
