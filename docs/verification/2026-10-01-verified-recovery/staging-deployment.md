# Staging deployment and rollback

Status on 2 October 2026: **v1.0.4 installed on the authorised Windows staging PC; service/CLI/UI live recovery passed**. The owned desktop process exited. Original empty-watch configuration remains restored without purging history. No commit, push, merge or public release occurred.

## Installed candidate and evidence

- Unsigned bundle: `artifacts/release-integrity-v104/Yagasoft-FluxVault-v1.0.4-win-x64-Setup.exe`.
- SHA-256: `dd4c47dc6b20c0b348691289481965002466acdf373cea26e5d823ab9af84f61`.
- Embedded MSI version: 1.0.4.0. All 204 app/service/CLI files match published and installed payloads.
- [Candidate](deployment-candidate.json), [zero-warning package build](release-package-v104.log), [fresh solution build](release-build-v104-final.log), [687-test run](full-tests-v104-final.log), [installed hashes](installed-payload-v104.json).
- [Installed upgrade](installed-upgrade-v104.json) and [actual installer log](installed-release-v104-smoke.log) show exit 0, no restart and preserved configuration bytes. [Smoke and policy](installed-smoke-v104.json) checked delayed-auto LocalSystem service, restart/restart/none recovery policy, Event Log registration and installed CLI hash recovery. [Current live results](installed-live-v104-results.json) prove ten retained versions, file/folder hashes, destination refusal and inventory availability.
- [Native installed UI](installed-native-ui-v104.json) records real selection, automatic refresh after cache invalidation, native destination dialogue and visible verified recovery. [Independent native hash](installed-live-results.json) matches the generated 74-byte source.
- [Fresh installed state](installed-state-final.json) verifies all payload hashes, healthy metadata/no error and zero watched folders. [Temporary process audit](process-cleanup-v104.json) and [UI exit](installed-ui-cleanup.json) identify final process state.
- The source is uncommitted in `codex/product-review-roadmap-20261001` at `b930ed5`. Native fixture and installed desktop gates passed within the bounded generated-content scope.

## PostgreSQL target and authorisation

The user explicitly approved automatic creation of FluxVault's database/account, retained password-free local access restricted to that database/account, immediate removal of temporary administrator access, preservation of unrelated databases, and no PostgreSQL restart. PostgreSQL 18.6 uses `D:\Program Files\PostgreSQL\18\bin`, existing service `postgresql-x64-18` and port 5432. New database `fluxvault_metadata` is owned by least-privilege login `fluxvault` (no superuser/create-database/create-role).

Retained rules are `host fluxvault_metadata fluxvault 127.0.0.1/32 trust` and `host fluxvault_metadata fluxvault ::1/128 trust`. Credential-free probes succeed for this tuple and fail for the administrator and an unrelated database on both addresses. Original unrelated non-empty authentication lines remain identical; the exact original HBA backup hash is `0c8dc6e6e57399790417a6e13b3a8e1b5e27aa19708a2122148fbfe3bdcecd42`. [Setup record](installed-postgresql-setup.json) identifies the retained backup. Normal postmaster PID 50372 has retained its original start time, `2026-10-01T13:38:16.2557791Z`. Only configuration reload was used.

The first setup exposed nested managed blocks removing runtime trust with temporary admin trust. The corrected helper inserts outside managed blocks and verifies runtime access after admin cleanup before success. Actual disposable setup under SCRAM passed; the normal runtime block was repaired by reload alone without re-enabling admin access or reprovisioning the database.

## Deployment sequence and corrected failures

v1.0.2 installed and its file recovery passed, but nested-folder recovery exposed timestamp ordering that selected an incomplete folder as latest. The staging service was stopped after restoring its empty-watch configuration. An independently reviewed monotonic folder publication rule and 17 red/green regressions were added; the 670-test checkpoint passed. Historical versions were preserved. [v1.0.2 rollback](uninstall-v102.json) removed its service/payloads while retaining ProgramData/database/staging history.

The first v1.0.3 build accidentally reused the previous MSI. Offline extraction refused the version/hash mismatch before installation. Both WiX builds now use Rebuild. The rejected attempt remains under `artifacts/rejected-stale-v103`; the corrected v1.0.3 candidate was extracted and checked before execution.

v1.0.3 was installed through the checksum-bound smoke helper in a fresh generated smoke directory with existing ProgramData preservation. Normal service validation watched only a newly generated GUID staging folder: two files captured; both folder files and standalone file matched SHA-256 after recovery; repeating the folder restore failed and preserved the sentinel. The original configuration is restored and the service remains healthy/idle. No normal repository fault injection, real working-file protection, Explorer registration or professional application opening was performed.

## Completed desktop resumption and inventory correction

Earlier resumed attempts encountered Windows desktop access denial and exited without claiming recovery. Once access returned, live validation exposed the service reporting an invalidated cache as an empty inventory. The [reviewed correction](inventory-correction.md) distinguishes unavailable snapshots, protects cache publication and preserves same-profile version/file-browser state. A separate fresh-directory preview cleanup regression also passed before packaging.

The clean-only smoke helper refused an existing service before mutation. The inspected v1.0.4 bundle's authored major-upgrade path was then executed against the verified v1.0.3 staging installation. Installer exit was 0 with no restart; configuration bytes and all ten generated history versions were preserved. Installed CLI/policy checks and 204-file payload comparison passed independently.

Owned UI PID 45820 selected the generated sample version, retained all ten rows and enabled recovery after service cache invalidation, and saved `native-file.txt` through the actual native dialogue. Visible service status reported “Restored and verified 1 file(s)”. Independent SHA-256 and length match the generated source. The app exited, no temporary process remains, original empty-watch configuration is restored and normal PostgreSQL retains its original postmaster.

## Rollback

For a future failed staging validation, stop only this installed FluxVault service and invoke the checksum-bound v1.0.4 bundle with `-uninstall -quiet -norestart`; wait for the launcher and installer processes to exit, then verify service/payload removal. Keep ProgramData, captured generated content, the new database/role and evidence. Never drop a database, reset a repository or remove unrelated data. Do not restart/uninstall PostgreSQL. Restore exact authentication backup bytes and reload only when explicitly required and after checking for intervening changes; otherwise obtain a merge decision.

Full service authorisation/vault isolation, configuration/lifecycle correctness, portable disaster recovery, professional performance and UI redesign remain roadmap work. Passing this bounded slice does not establish unrestricted professional-file readiness.
