# Authoritative vault catalogue and command admission

Bounded S1 prerequisite, 4 October 2026. This is not completion of the authorised capture/restore round trip, NEXT-002 or deployment. The isolated branch still lacks the protected runtime executor, vault namespace/path integration and installed-service composition. Do not deploy this intermediate build.

## Implemented contracts

- Validated UUID vault identity, explicit command target/revision/operation fields and a total permission table. Creator ownership derives from the supplied verified caller context; grants are explicit. Ordinary owners cannot manage access.
- SSPI-only authoritative PostgreSQL catalogue, independent protected bootstrap identity/endpoint, fixed `fv_control` schema, logged service-owned tables with no other role/PUBLIC grants, and checked WAL durability settings. Opens never provision or adopt a missing catalogue.
- Transactional admission orders against revocation and uses coherent bindings/revisions. Atomic save/access transactions retain all accepted configuration and commit their receipt before success. Protected infrastructure cannot be changed by ordinary configuration commands.
- Durable operation receipts retain caller/vault/command/fingerprint and effective permissions. Conflicting IDs are rejected, interrupted admitted work is not replayed, and receipt envelopes are authorised before response decoding. Unknown commit/response outcomes remain explicit.
- Discovery returns bounded pages of authorised summaries without constructing repositories or returning roots/configurations. The dispatcher requires an authorised executor and has no legacy profile-handler fallback. Runtime lifecycle provisioning remains unavailable until protected physical resources exist.

## Observed verification

- Policy/identity regressions have retained red/green TRXs. The dispatcher scaffold failed all eight initial command-flow tests; its implementation passed them. Three additional phase regressions failed before correction. The final focused run has **36 passes, no skips**.
- `catalogue-red`: actual SYSTEM SSPI fixture reached the unimplemented provisioning method and failed as expected. Teardown joined owned jobs, removed the root and preserved the installation.
- `catalogue-first-green`: **27 real-store contracts**, repeated after successful native SSPI probes on both loopbacks.
- `catalogue-final-green`: **40 real-store contracts** per repetition: full configuration round trip, revision CAS, exact replay/conflicts, explicit grants/revocation/restart, protected binding changes, two-vault preservation, malformed inaccessible receipts, partial purge permission revocation, complete paged discovery, real-store dispatcher denial before executor access and target preservation, and acknowledgement loss immediately after an actual database commit followed by receipt reconciliation.
- The catalogue itself uses IPv4 in these repetitions; the enclosing native authentication probes separately exercise IPv4 and IPv6. Catalogue policy actors A/B are deliberate doubles; SYSTEM is the actual process identity. This is not proof of A/B caller tokens through a SYSTEM pipe or native source/output access.
- The acknowledgement-loss hook throws after the real transaction commits. This demonstrates real-store reconciliation, not actual process death, power loss or exactly-once repository/filesystem effects.
- Both successful rehearsals retain 16 native authentication/ACL observations and teardown evidence. Fixture resources and normal installation snapshots are preserved in their respective folders.
- `combined.log` and four distinct TRXs: **790 non-PostgreSQL tests pass**, no failures/skips: Core 379, App 222, Windows 34, Integration 155. PostgreSQL-tagged full integrity cases are explicitly excluded and remain required later.
- `release-build.log`: zero-warning/error Release solution build. `process-census.json` records post-run fixture-process absence.

[Independent review](independent-review.md) approves this bounded source milestone. The [accepted plan](../../superpowers/plans/2026-10-03-next002-with-protection-save.md), all S01–S08/G01 gates and the overall roadmap remain unchanged. Remaining immediate work is bound same-database metadata namespaces and protected caller file access for the first real authorised two-vault round trip.
