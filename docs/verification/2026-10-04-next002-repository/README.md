# Bound repository round-trip evidence

This bounded S1 prerequisite binds the content repository to its PostgreSQL namespace. It does not complete S1, native caller file access or NEXT-002. The normal installation remains unchanged; service composition is still incomplete.

The public service constructor requires the exact bound PostgreSQL store. Ordinary access verifies that store before repository I/O, checks a version-2 primary/mirror marker carrying VaultId and a root/state/endpoint fingerprint, and never creates or adopts a missing root. Explicit provisioning requires fresh empty roots and refuses existing markers. File, folder and deletion manifests carry the bound VaultId; reads reject foreign or missing identities before recovery publication. Legacy direct fixtures preserve their version-1 contract and cannot open bound markers.

`red/repository-binding-red.trx` and `red.log`: all 11 new regression cases failed against compile-ready API scaffolding before the behaviour was implemented. An earlier test compilation mistake used a nonexistent default `ChunkingOptions` constructor; it was corrected before retaining the executable red result.

`focused/repository-focused.trx` and `focused.log`: 83 pass, no failures/skips. This includes all 11 new binding tests plus existing repository, metadata-primary, mirror and folder-recovery integrity cases. The first focused green attempt exposed a missing-mirror check incorrectly applied during fresh provisioning; the implementation now distinguishes provisioning from ordinary required-mirror access.

`live-first/result.json`: each real SYSTEM SSPI store host (`127.0.0.1`, `::1`) passes 40 catalogue, 20 metadata and 13 repository contracts. Actual repository operations capture shared source/digest bytes, reopen the bound store, return verified file/folder bytes and purge only the first vault. Every second-vault row and storage byte is unchanged by that purge; a subsequent recovery succeeds. A poisoned metadata marker rejects capture before chunk publication and preserves both roots. Policy actors are doubles; source input is a generated stream and output is inside the protected owned fixture. `NativeCallerFileAccess=false` is explicit in both results.

`live-first/cleanup.json`: all owned jobs joined, root removed, normal installation unchanged. The enclosing fixture also retains its native authentication/ACL observations and PostgreSQL refusal log correlation.

Existing independent Astra `integrity_review` approved the bounded repository-binding source gate with no required corrections. It observed the 83-case focused result; the real repository probe remained pending at review. This is source approval only, not acceptance of native source/output handles or deployment.

The reviewer requires caller-token component opens, retained handles, non-following handle-relative operations where path resolution could race, final type/volume/path checks, caller-authorised staging/publication/cleanup, non-overwriting publication for initially absent destinations and directory-entry replacement for hard-linked destinations. Native barrier tests must cover replacement, in-place reparse changes, late destination appearance, hard links and cancellation. These conditions guide the next implementation; no native helper is claimed here.

Combined `Category!=RequiresPostgreSql` checks pass 801 cases, no failures/skips: Core 390, App 222, Windows 34, Integration 155. `release-build.log` records a fresh full Release build with zero warnings/errors. `process-census.json` records no remaining owned test/fixture process. The full owned PostgreSQL integrity suite remains required at S3.
