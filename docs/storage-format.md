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
raw or zstd-compressed. The manifest records the encoding, logical length, stored
length, and digest.

## Version manifests

A manifest records:

- version id
- watched root id
- source path
- captured UTC timestamp
- consistency level
- logical file length
- ordered chunk list
- compression policy used

Manifests are written to a temporary file and atomically moved into place.

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
