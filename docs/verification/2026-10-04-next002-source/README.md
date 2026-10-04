# Caller-bound source handle evidence

This bounded S1 prerequisite opens each local NTFS component under an effective Windows caller token, without following reparse points or requesting backup intent. It retains parent and source handles through stream disposal, rejects ambiguous paths, hard links and physical-path mismatches, and never reopens a caller source as SYSTEM. Concurrent writers remain possible; this is BestEffort access, not a snapshot-consistency claim.

`physical-red/source-physical-red.trx` records ten failing regressions and two passing controls against compile-ready unsafe API scaffolding before implementation. Earlier red fixtures had two defects: unexpected successful streams were not disposed before assertions, and AppData paths were redirected by package virtualisation. The tests now dispose unexpected results and use physical user-profile paths. `initial-red-cleanup.json` and `final-cleanup.json` record removal of the owned leftovers. The strict final physical-path check was retained.

`final-green/source-final-green.trx` passes all twelve source cases with no failures or skips. These include ACL denial after parent admission, leaf replacement, an actual in-place junction mutation, hard links, retained-parent identity, cancellation and ambiguous paths. The first physical green attempts exposed native junction cleanup and typed-denial issues; both were corrected before the final passing run. `windows-full/windows-full.trx` passes all 46 Windows tests.

Each source test captures an actual token through a verified private native pipe. Client and server are the same Windows user. This does not prove A/B-to-SYSTEM source permissions, scheduled capture after logoff, VSS or runtime service composition.

Independent Astra `integrity_review` approved this bounded source-reader gate with no blocking corrections after observing the twelve passing tests. Recovery publication and the remaining NEXT-002 gates are excluded from that approval.

`combined.log` records 813 passing applicable non-PostgreSQL tests with no failures or skips: Core 390, App 222, Windows 46 and Integration 155. The earlier invocation used the obsolete solution filename and failed before running tests. `release-build.log` records a fresh full Release build with zero warnings or errors. `process-census.json` records no remaining owned test/build process, and the source-fixture parent is empty. The PostgreSQL integrity suite remains required at S3.
