# Caller-authorised source inventory and capture

This bounded milestone supplies source inventory, metadata and live capture through the authenticated caller. It reuses the actual backup operations with a bound PostgreSQL repository. It does not complete S1 or NEXT-002: the vault executor, service/client composition, native vault permissions, scheduled/VSS authority and remaining security/isolation gates are still open. No normal installation was deployed or changed.

## Behaviour

`IProtectionSourceAccess` distinguishes present, confirmed missing and unavailable sources. The backup engine reports inaccessible metadata or interrupted enumeration as failures and retains history. A missing or disappearing protection root cannot tombstone its children or its own history. Selection matching uses configuration rather than `File.Exists`, which can conflate missing files and access failures. Confirmed missing children under an accessible root still record deletions.

The Windows adapter opens and pins every source component under the retained native caller. It reads metadata and bounded directory batches from those handles; SYSTEM never enumerates or reopens the source by path. Only validated missing-name/path native statuses establish absence. Directory records reject malformed offsets, names and UTF-16. Physical identity checks distinguish different case-sensitive roots while permitting ordinary Windows case variations. Cancellation disposes iterators, buffers and pins.

`WindowsCallerCaptureProvider` snapshots the accepted protection roots and returns caller-opened live streams. It has no privileged source or VSS fallback. Native file timestamps are converted to UTC before PostgreSQL persistence.

## Observed verification

| Check | Evidence |
| --- | --- |
| Backup/history regressions | Meaningful red cases in `baseline-red.log`, `status-red.log`, `root-red.log`, `root-self-red.log` and `drive-root-red.log`; 32 Core/source and performance checks pass in `core-review-green.log` |
| Native source/capture and parsing | Raw-status, malformed UTF-16, root-absence and UTC regressions fail before correction; 41 focused Windows checks pass in `utc-green.log` |
| Native A → SYSTEM bound PostgreSQL | Fresh UUID `095f1814ee5d4ebf97c150c855c89366`; `native-system-final/result.json` records 11 SYSTEM checks, A's 10 checks and no failure |
| Combined applicable suite | 865 passed, zero failed/skipped: Core 412, App 222, Windows 75, Integration 156; `combined.log` and four separate TRX files under `combined/` |
| Release build | `build.log`: zero warnings/errors; subsequent changed test-host build in `host-output-control.log`: zero warnings/errors |
| Teardown | Final `cleanup.json`: root removed, owned jobs joined and installation unchanged; supplemental cleanup result confirms removal of published outputs; fresh `process-census.json` |

The native flow uses actual caller tokens, actual operations, a real configuration store, SSPI metadata and manifests bound to a fresh VaultId. A denies metadata/listing after initial capture: targeted and full backups report failures without changing version identities. Restoring permission and deleting the file produces a genuine tombstone. Caller A independently reads and edits the standalone and nested recovered output and verifies the original hard-linked file remains unchanged. Existing staging mutation, cancellation, late-destination and small-file buffer checks remain included. B is refused by a fixture allowlist before file work; this is not proof of the pending production vault policy.

The first bound native rehearsal (`native-system/`) failed because a local-offset timestamp was rejected by PostgreSQL. Its owned teardown passed. The corrected second and final rehearsals passed with complete teardown. Earlier compilation errors, fixture ACL errors and the no-match `utf16-red.log` are retained diagnostic attempts, not behavioural red evidence; `utf16-red-built.log` contains the actual failing Unicode regression.

The installed PostgreSQL and FluxVault service identities, HBA/identity mapping and configuration match the before/after snapshots. Full PostgreSQL G01 suite execution remains pending. The [independent review](independent-review.md) approves this bounded helper milestone only.
