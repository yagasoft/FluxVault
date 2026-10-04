# Bound metadata namespace evidence

This is a bounded S1 prerequisite, not completion of S1 or NEXT-002. The installed service is unchanged and this intermediate branch is not deployable.

The service-managed store takes an immutable `VaultBinding`, uses password-free Windows SSPI, qualifies every metadata query with its UUID-derived schema and derives a transaction lock from that UUID. Explicit provisioning refuses existing namespaces. Ordinary connections verify the database/role, acknowledgement durability, namespace access and persisted vault/root/endpoint binding before metadata access. Incoming and stored manifests must carry the same VaultId. Outbox exports reject another root before file I/O. Legacy direct fixtures retain their existing constructor; the authenticated service has no fallback to it.

## Reproduced regression and live checks

`namespace-red/result.json` records the expected descriptor conflict when two vaults in the same real database reused a version ID, source path and digest but acknowledged different lengths. The temporary constructor still used the global `fluxvault` schema at that point. The final implementation does not.

`namespace-first-green/result.json` records 20 metadata checks and 40 catalogue checks on each actual store host, `127.0.0.1` and `::1`. The metadata checks use real SQL, including folder/lineage collisions, current entries, per-vault mutation locks, cancellation, export targeting, deletion, chunk references, outbox status, absent/mismatched namespaces and rechecking a poisoned binding after initialisation. A snapshot of every second-vault table is identical after first-vault mutations. Synthetic manifests exercise metadata; no native user capture/recovery claim is made.

The same owned rehearsal includes 16 native authentication/ACL observations. Both retained runs have `cleanup.json` confirming joined owned jobs, removed fixture roots and unchanged normal installation. PostgreSQL log bytes are retained for refusal correlation. The post-run census found no fixture process; its only matching PowerShell process was the census command itself.

The existing independent Astra `integrity_review` reviewed the namespace source, query/DDL qualification, transaction locks, SSPI binding and probe. Its verdict was **approved for this bounded source milestone, no blocking correction**. It inspected the retained red evidence; green runtime evidence was still pending during its review. It did not approve source/output handles, full S1, the remaining command surface or deployment.

## Regression checks and limitations

`focused/namespace-focused.trx`: 36 manifest/metadata/ownership tests pass, no failures or skips, after the null-VaultId serialisation compatibility annotation.

`combined.log` and its four TRXs retain a mistaken class-name filter: Core 379, App 222 and Windows 34 passed; Integration reported 17 failures because PostgreSQL tests were included without the required owned-cluster fixture. This is not passing acceptance evidence. The corrected integration run uses `Category!=RequiresPostgreSql`: all 155 pass, no failures/skips, in `integration-corrected.log` and `combined-corrected/non-postgresql.trx`. Across the four applicable suites, 790 non-PostgreSQL tests pass. PostgreSQL integrity tests are not silently skipped and still require the safe owned full-suite runner.

Repository ownership markers and generated manifests, actual source/output handle access, client targeting, service composition and the complete acceptance matrix remain open. This metadata probe does not establish exactly-once filesystem effects, process-death reconciliation or normal-installation readiness.

`release-build.log`: fresh full Release build, zero warnings/errors. `process-census.json`: no remaining fixture, test host or build process. All tool sessions launched for these checks returned their final exit codes.
