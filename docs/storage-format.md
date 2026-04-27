# Storage format

## Repository layout

```text
repository/
  chunks/
    blake3-prefix/
      digest.chunk
  manifests/
    yyyy/
      mm/
        version-id.json
  refs/
    latest.json
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

## CLI repository operations

The developer CLI writes into the same chunk and manifest layout as the service
will use. `list` reads manifest summaries, `inspect` reads one manifest and
reports stored/logical size, and `restore` reconstructs the file from ordered
manifest chunks.
