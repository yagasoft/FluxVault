# Storage format

## Repository layout

```text
repository/
  .fluxvault-storage.json
  .fluxvault.lock
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

The isolated single-vault NEXT-002 staging implementation creates fresh metadata
schema version 4. Exact UTC capture ticks and ordinal version IDs provide stable
history ordering; a transactional history generation invalidates a cursor after
capture or pruning. History responses project bounded headers before transfer,
while folder contents are read separately from the exact immutable manifest.
Current entries use the existing canonical path/kind identity as their primary
key. The winning version supplies display casing and deletion state; pruning
rebuilds affected pointers from retained history, including tombstones, within
the deletion transaction. Reads refuse mismatched pointers, paths and ticks.
Existing unsupported schemas are refused without automatic upgrade or adoption.
Retain the v1.0.4 installation and its original database/storage assets for
existing-data recovery; do not point old binaries at new staging metadata.

Developer machines can run `eng/setup-fluxvault-postgresql.ps1` to install and
prepare the local PostgreSQL metadata store with FluxVault defaults.

The metadata database stores paths, versions, chunk references, lineage, folder
entries, current-entry projections, mirror placement state, sync records,
conflicts, capture queue rows, and metadata outbox rows. The replayable
`metadata-journal` stream is an export/recovery artefact, not the primary
runtime query source.

## Verified recovery support and ownership

The October 2026 integrity implementation is unmerged and incomplete. Its
[evidence record](verification/2026-10-01-verified-recovery/README.md) distinguishes
legacy/in-memory proof from outstanding PostgreSQL and native checks. It does
not certify unrestricted use with real files.

Verified content operations require fresh owned roots on local Windows NTFS,
with distinct non-overlapping primary/mirror paths and no reparse components.
UNC/device paths, participating mapped-network volumes and other filesystems
are rejected. An unavailable optional mirror can warn during primary capture;
restore accesses the primary alone. Destructive mirror maintenance requires
all participating roots to be available and leased. Cloud synchronisation races,
hostile path replacement and physical power failure are outside this proof.

The ownership marker contains `formatVersion: 1`, a 32-hex `storageId`, and
`role: primary|mirror`. A mirror must belong to its primary. An unmarked non-empty
root or foreign/malformed marker is refused without resetting its data. Existing
staging repositories therefore need a separate deliberate transition; do not
remove files to bypass this guard.

A held `.fluxvault.lock` file with `FileShare.None` is the cooperating-process
lease. File existence is not ownership: the file remains after release and OS
handles are released after process death. Never delete it to unlock a repository.
Content-sensitive capture, restore, preview, rehearsal and maintenance share the
protocol; it is independent of future caller authorisation and metadata vault
namespaces.

## Verification and publication

The verified reader checks descriptor/sidecar agreement, bounded stored bytes,
exact decoded length and the BLAKE3 digest of the bytes actually consumed.
Existing acknowledged digest/length/stored-length/encoding tuples are immutable;
changing compression does not rewrite them. Partial objects and contradictory
references fail closed. New payload/sidecar pairs are staged, flushed and
verified before references are recorded. An interrupted pair can leave an
unreferenced partial object, which is preserved and refused on reuse.

Manifests require 32-hex version IDs, 64-hex digests, exact ordered chunk coverage,
consistent present file/folder signatures, safe Windows child names and an
acyclic bounded recovery graph. Capture validates the pending cascade before
acknowledgement. Scrub and repair validate semantics before declaring health or
copying a manifest; a malformed primary cannot overwrite a valid mirror.

Default internal support ceilings are 16 MiB decoded/stored chunk and decoder
window, 4 KiB sidecar, 64 MiB manifest, depth 128, 100,000 distinct graph nodes,
100,000 expanded entries, 1,000,000 expanded chunk references and 128 MiB
cumulative metadata plus UTF-16 destination paths. Repeated DAG expansions
consume the budget. These are safety limits, not Options tuning controls.
Admission measures the original source path; a longer recovery destination can
still exceed the restore path budget and be refused before publication.

File restore verifies a sibling staged file before publication. A file that
existed when the call began can be replaced under the caller's overwrite policy;
a file appearing later is preserved. A single folder version publishes a complete
staged tree into a new destination only, with no merge into an existing folder.
Bulk selection remains per-item work and can return partial success.

`RepositoryRestoreResult` reports verified logical bytes, actual file count and
warnings. A lineage-hint failure after publication is a warning on verified
success. Cancellation or IO/integrity failure before publication preserves the
existing destination or leaves a new one absent. A lost response is an unknown
acknowledgement outcome; never delete potentially committed objects as compensation.
Previews always reconstruct verified content; rehearsal owns only its GUID child
under the temporary root. No ACL/ADS/application-consistency certification is implied.

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
verifies chunk bytes and restored logical length, records pass/fail details, and deletes the
temporary files. It does not write restore hints and does not create manifests.

## CLI repository operations

The CLI loads the normal ProgramData/profile configuration and uses the
PostgreSQL-backed repository by default. The legacy JSON manifest repository is
still available through `--legacy-file-manifest` for developer diagnostics and
test harnesses. `list`, `inspect`, and `restore` use the same metadata source as
the selected mode.
