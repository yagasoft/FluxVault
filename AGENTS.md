# FluxVault Codex instructions

Use these instructions only for this workspace:

`D:\Drive\Work (1)\Code\FluxVault`

## Local code index

- Use the local SQL Server code index before broad code exploration:
  - `scripts\fluxvault-index.ps1 search "<query>"`
  - `scripts\fluxvault-index.ps1 search "<query>" --semantic-mode always` for broader natural-language searches that need vector ranking
  - `scripts\fluxvault-index.ps1 doctor`
  - `scripts\fluxvault-index.ps1 sync`
- The index database is `FluxVault_Codex` on `localhost`.
- Shared indexer tooling, the central CUDA PyTorch runtime, and the shared model/package cache live under `D:\Codex\code-indexer`.
- The central Python virtual environment is `D:\Codex\code-indexer\venv`.
- FluxVault project state, logs, watcher lock files, and watcher launchers live under `D:\Codex\FluxVault`.
- Do not use or modify `YsTrader_Codex`, `MoHESR_Codex`, `D:\Codex\YsTrader`, `D:\Codex\MOHESR`, or any companion workspace files from this repo.
- If the index is unavailable or incomplete, run `doctor` and `sync`; use `rg` as the fallback for urgent exact searches.
- Be patient with index searches. If a search is slow or times out, first optimise the index query instead of immediately reading broad code:
  - retry with a narrower identifier, file name, symbol, or domain term
  - use the default lexical search for exact code identifiers before forcing `--semantic-mode always`
  - run `doctor` to check watcher health, GPU/CUDA readiness, stale locks, and semantic backlog
  - use `sync --path "<file-or-folder>"` for a targeted refresh when freshness is the concern
  - fall back to `rg` only after the index path is clearly unavailable, incomplete, or unsuitable for the query
- Do not index generated, vendored, build, runtime, log, local secret, or credential files.
- The real-time watcher should be preferred over periodic scheduled maintenance:
  - `scripts\fluxvault-index.ps1 watch`
  - `scripts\fluxvault-index.ps1 install-startup-watcher`
- Keep shared package caches and model caches under `D:\Codex\code-indexer`; keep FluxVault-specific logs, watcher locks, and watcher launchers under `D:\Codex\FluxVault`.
- When an NVIDIA GPU is available, the semantic embedding runtime must use CUDA PyTorch; do not silently fall back to CPU. CPU is acceptable only on machines without supported GPU access.
- Keep the real-time index writable. Do not rebuild the SQL Server DiskANN vector index during normal watcher use; SQL Server rejects vector-table writes while that index exists. Exact `VECTOR_DISTANCE` search remains available without it.

## Configuration and Options

- Always parameterise appropriate and reasonable configurations through typed,
  defaulted configuration models rather than hard-coded operational constants.
- Add user-meaningful operational configuration to the FluxVault Options
  dialogue (the Options dialogue) in the same change that introduces or
  materially changes the behaviour.
- Preserve compatibility for existing configuration files by keeping new fields
  optional or defaultable.
- Add tests for configuration defaults, Options load/save behaviour, and any
  runtime policy effect.
- Document the setting in the relevant project docs and update
  `docs/roadmap-tracker.md` when it changes roadmap capability status.
- Do not expose dormant, internal, unsafe, or unimplemented fields in Options
  until they have a real runtime effect and clear user semantics.
