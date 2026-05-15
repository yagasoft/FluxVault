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
```

## Chunk records

Each chunk is immutable and addressed by BLAKE3 digest. The stored payload may be
raw, zstd, lz4, Brotli, or LZMA encoded. The manifest records the encoding,
logical length, stored length, and digest.

## Version manifests

A manifest records:

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

Manifests are written to a temporary file and atomically moved into place.
Older manifests that do not contain entry-kind or deletion fields load as live
file versions.

Folder manifests are metadata-only versions. When a file is committed with a
watched-root path, FluxVault writes folder versions for the containing folder
and each tracked ancestor up to the watched root. Each folder version stores
its immediate child entries and the child version ids that make up that
snapshot. Deletion tombstones are also manifests: they point at the deleted
version through `DeletedFromVersionId`, and parent folder manifests are
cascaded upward so browsers can show missing tracked entries as restorable
phantoms.

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

Retention deletes manifests first. A version disappears from `list` as soon as
its manifest is pruned. Folder manifests and deletion tombstones keep referenced
child versions alive while the folder/tombstone remains retained. Chunk and
metadata files are deleted only when no remaining manifest references the chunk
digest, so shared chunks survive older version pruning.

Cloud-folder mirrors use the same layout. Local pruning is authoritative; mirror
deletion is best-effort and any mirror cleanup warnings are surfaced in service
status and diagnostics.

## Maintenance state and health

Repository health results are service state, not repository history. The service
stores the last health snapshot, scrub report, and restore rehearsal report under
ProgramData state so restarting the service does not erase diagnostics evidence.
These files are outside the chunk/manifests repository and are additive runtime
metadata.

Scrub validates only manifests and chunks still referenced by remaining
manifests. Missing or corrupt primary artefacts can be repaired from a healthy
mirror copy, and missing or corrupt mirror artefacts can be repaired from a
healthy primary copy. If neither side has a healthy copy, the scrub report marks
the issue critical and unresolved. Scrub never repairs from live source files.

Restore rehearsal writes temporary restored output under FluxVault service state,
verifies the restored logical length, records pass/fail details, and deletes the
temporary files. It does not write restore hints and does not create manifests.

## CLI repository operations

The developer CLI writes into the same chunk and manifest layout as the service
will use. `list` reads manifest summaries, `inspect` reads one manifest and
reports stored/logical size, and `restore` reconstructs file manifests from
ordered chunks or folder manifests recursively from their child snapshot
entries.
