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

Manifests are written to a temporary file and atomically moved into place.

## Planned lineage metadata

V1 restore hardening adds Git-like per-file history metadata to manifests or a
closely related version event record:

- parent version id or ids
- restored-from version id
- fork origin version id
- device id
- operation type, such as capture, restore, sync hydrate, or conflict resolution
- optional conflict group id

Restoring an older version does not delete, overwrite, or hide newer manifests.
It creates a new version event that points back to the restored version as the
fork origin. This lets the restore browser show normal history, forks,
restored-from links, and conflicts without compromising chunk deduplication.

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
