# Storage format

## Repository layout

```text
repository/
  chunks/
    digest-prefix/
      digest.chunk
      digest.json
  manifests/
    version-id.json
  lineage/
    restore-hints/
  metadata-journal/
    device-id/
      sequence.fvop
```

PostgreSQL is the primary metadata store for normal service/app runtime.
Chunk payloads remain content-addressed files under the repository. The legacy
JSON manifest layout remains importable and available through explicit
developer diagnostics, but normal large-vault query paths read PostgreSQL
tables instead of scanning `manifests/*.json`.

Developer machines can run `eng/setup-fluxvault-postgresql.ps1` to install and
prepare the local PostgreSQL metadata store with FluxVault defaults.

The metadata database stores paths, versions, chunk references, lineage, folder
entries, current-entry projections, mirror placement state, sync records,
conflicts, capture queue rows, and metadata outbox rows. The replayable
`metadata-journal` stream is an export/recovery artefact, not the primary
runtime query source.

## Chunk records

Each chunk is immutable and addressed by BLAKE3 digest. The stored payload may be
raw, zstd, lz4, Brotli, or LZMA encoded. The manifest records the encoding,
logical length, stored length, and digest.

## Version manifests

For repositories created before the PostgreSQL metadata transition, or when
explicit legacy diagnostics are requested, a manifest records:

- version id
- watched root id
- source path
- captured UTC timestamp
- consistency level
- logical file length
- ordered chunk list
- chunk encoding used for each chunk
- operation type: `Capture`, `Restore`, `InheritedCopy`, `RemoteSync`, or
  `Delete`
- parent version id or ids
- restored-from version id
- fork-origin version id
- inherited-from version id and source path
- deterministic content signature based on logical length and ordered chunk
  identity
- entry kind: `File` or `Folder`
- deletion state and deleted-from version id for tombstones
- immediate folder child entries for folder manifests

Normal PostgreSQL-backed commits store the exact `FileVersionManifest` payload
in `versions.manifest_json` and normalized rows for indexed queries. Legacy
manifest files are written only in legacy file-manifest mode. Older manifests
that do not contain entry-kind or deletion fields load as live file versions.

Folder versions are metadata-only versions. When a file is committed with a
watched-root path, FluxVault records folder versions for the containing folder
and each tracked ancestor up to the watched root. Each folder version stores
its immediate child entries and the child version ids that make up that
snapshot. Deletion tombstones point at the deleted version through
`DeletedFromVersionId`, and parent folder versions are cascaded upward so
browsers can show missing tracked entries as restorable phantoms.

## Lineage metadata

V1 restore hardening adds Git-like per-file history metadata to manifests.
Existing manifests without lineage fields are treated as normal `Capture`
versions with no parent ids.

Same-path captures record the latest previous same-path version as the parent.
Restoring a version writes the requested bytes and stores a small
repository-local pending restore hint under the lineage metadata area. The next
capture of that destination consumes the hint and writes a `Restore` manifest
with restored-from and fork-origin version ids.

When a protected file is copied from existing FluxVault content and the copied
bytes still match an existing content signature, FluxVault writes a visible
`InheritedCopy` manifest for the new path. It reuses the existing chunks,
records the inherited source version/path, and gives later edits a parent/fork
origin without republishing duplicate content.

FluxVault should keep its own content-addressed chunk repository as the live
storage engine. Actual Git or libgit2 may be evaluated later for export or
interoperability, but it is not the default runtime store because FluxVault
needs streaming large-file capture, VSS/open-file handling, retention, mirror
placement, and a clean repository layout without side `.git` working trees.

## Retention and garbage collection

Retention deletes PostgreSQL version rows first. A version disappears from
`list` as soon as its DB row is pruned. Folder versions and deletion tombstones
keep referenced child versions alive while the folder/tombstone remains
retained. Chunk and metadata files are deleted only when no remaining DB chunk
reference points at the digest, so shared chunks survive older version pruning.

Cloud-folder mirrors use the same layout. Local pruning is authoritative; mirror
deletion is best-effort and any mirror cleanup warnings are surfaced in service
status and diagnostics.

## Maintenance state and health

Repository health results are service state, not repository history. The service
stores the last health snapshot, scrub report, and restore rehearsal report under
ProgramData state so restarting the service does not erase diagnostics evidence.
These files are outside the chunk/manifests repository and are additive runtime
metadata.

Scrub validates only DB-referenced manifests and chunks. Missing or corrupt
primary artefacts can be repaired from a healthy
mirror copy, and missing or corrupt mirror artefacts can be repaired from a
healthy primary copy. If neither side has a healthy copy, the scrub report marks
the issue critical and unresolved. Scrub never repairs from live source files.

Restore rehearsal writes temporary restored output under FluxVault service state,
verifies the restored logical length, records pass/fail details, and deletes the
temporary files. It does not write restore hints and does not create manifests.

## CLI repository operations

The CLI loads the normal ProgramData/profile configuration and uses the
PostgreSQL-backed repository by default. The legacy JSON manifest repository is
still available through `--legacy-file-manifest` for developer diagnostics and
test harnesses. `list`, `inspect`, and `restore` use the same metadata source as
the selected mode.
