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
- operation type: `Capture`, `Restore`, or `InheritedCopy`
- parent version id or ids
- restored-from version id
- fork-origin version id
- inherited-from version id and source path
- deterministic content signature based on logical length and ordered chunk
  identity

Manifests are written to a temporary file and atomically moved into place.

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
its manifest is pruned. Chunk and metadata files are deleted only when no
remaining manifest references the chunk digest, so shared chunks survive older
version pruning.

Cloud-folder mirrors use the same layout. Local pruning is authoritative; mirror
deletion is best-effort and any mirror cleanup warnings are surfaced in service
status and diagnostics.

## CLI repository operations

The developer CLI writes into the same chunk and manifest layout as the service
will use. `list` reads manifest summaries, `inspect` reads one manifest and
reports stored/logical size, and `restore` reconstructs the file from ordered
manifest chunks.
