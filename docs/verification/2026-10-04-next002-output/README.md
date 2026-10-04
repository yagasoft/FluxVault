# Caller-authorised source and recovery target

This bounded helper milestone is implemented and independently reviewed. It does not complete S1 or NEXT-002. The authenticated vault executor, service/client composition, native vault-permission and isolation flows, scheduled/VSS authorisation and remaining acceptance gates are still open. No normal installation was changed or deployed.

## Behaviour and invariants

The Core repository can restore through an owned `IRepositoryRestoreTarget`. It validates the graph before preparation, verifies chunk bytes and lengths, flushes through the target, and publishes only after successful verification. The target is disposed on success and failure. Five real-repository tests cover verified target-only writes, corrupt content, invalid graphs, cancellation and published warnings; the logical test target is not a native permission proof.

The Windows target requires SYSTEM and an authenticated effective caller. That caller creates a fresh protected stage under a retained, non-following destination parent. The stage denies subsequent caller mutation, including implicit owner DAC rights. SYSTEM writes only within that exact protected stage. The caller performs handle-relative publication. SYSTEM then hands over permissions bottom-up, with the root last, using security-only handles. A post-publication permission failure is a warning and never causes published output to be removed. File replacement changes the directory entry rather than truncating an existing hard link; folder recovery requires a new destination. A late destination is refused when the destination was initially absent.

Native inspection disproved two assumptions: directory publication fails while nested handles deny delete sharing, and the caller's initial protected-root handle cannot change its DACL despite requesting DAC access. The retained tiny proofs record these results. Child handles therefore close while the stage remains protected; an independently approved SYSTEM security-only root handle provides the final permission handover.

Source ancestors above the selected root may allow traversal without attribute reads. Those handles reject reparse resolution in the native open. The selected root and descendants retain strict metadata checks. Differently cased requested prefixes are resolved under the caller against the same fixed NTFS volume and compared by file identity while both chains remain pinned. Distinct physical roots are denied before source bytes are returned; ordinary Windows case variations remain accepted.

Disposed file streams are pruned before the next creation. The target keeps active streams for cleanup and tracks single-file admission separately. This prevents a sequential small-file restore from retaining every disposed stream's 128 KiB buffer.

## Observed checks

| Check | Result and retained evidence |
| --- | --- |
| Core target regression baseline | Five expected failures in `target-red`; 51 focused green tests in `target-green` |
| Case-distinct source baseline | Two expected missing denials and one passing ordinary-case control in `case-red-corrected` |
| Source reader | 16 physical current-user regressions passed in `source-green-final` |
| Stream retention baseline | Actual native SYSTEM target retained 100/100 disposed streams in `native-fifth/caller-server-error.json` |
| Fresh native A/B → SYSTEM | Eight server checks passed in `native-sixth/result.json`; A reports seven refused mutation attempts, editable published content and preservation of the original hard link |
| Combined applicable suite | 823 passed: Core 395, App 222, Windows 50, Integration 156; zero failed or skipped. `combined.log` retains all per-project counts. The explicitly named TRX was overwritten between projects and contains only the final Integration result |
| Build and final census | Full fresh Release build: zero warnings/errors. Final census: no remaining task/fixture processes, roots, accounts or scheduled tasks. See `build.log` and `process-census.json` |

The fresh fixture UUID is `9f84865e81ea46249d903b59bcb4221c`. It uses actual native caller tokens and physical NTFS permissions. It exercises source capture, file recovery, nested-folder recovery and editability, parent DELETE_CHILD rename/open, staged data/attribute/DACL/add-child/reparse-write authority refusals, cancellation cleanup, late-destination preservation and a 100-small-file weak-reference buffer regression. B is refused by a fixture allowlist before file work. This is not a production vault-policy proof: the file pipeline uses a protected legacy fixture repository, not the bound PostgreSQL vault executor. Existing SSPI checks still run against both loopbacks in this fixture.

## Failed rehearsals and cleanup

All six native rehearsal results are retained. The first found insufficient parent DELETE_CHILD permission in the fixture. The second lacked detailed server diagnostics. The third exposed traversal-only ancestor admission. The fourth exposed root permission handover. The fifth reached the intended memory regression and then revealed that the administrator runner could not enumerate correctly protected published outputs. Those failures are not reported as overall passing runs.

Cleanup now completes the original resource journal, removes probe accounts, and uses a separately journalled, owned SYSTEM job/task to delete only `output-A` and `output-B`. It rejects reparses and changes no file ACL, so hard-linked names outside the subtree are unaffected. It retains runtime and both ledgers until the helper exits. The supplemental ledger uses flushed temporary writes and atomic replacement. Interrupted helper resources are recovered before a retry. SYSTEM independently verifies the root's physical owner is a direct local administrator before applying the existing path/ACL checks with that additional trusted SID. No output directories means no new helper is created after any pending helper recovery.

The fifth rehearsal's interrupted cleanup was recovered successfully; its `cleanup.json` records root removal, joined jobs and unchanged installation. The fresh sixth rehearsal records the same three results. Its original PostgreSQL and FluxVault service identities, HBA/identity mapping and app configuration match the preceding snapshot. `test-early-cleanup.ps1` reproduces the failure before runtime/output creation through the real cleanup script, with `early-cleanup.json` retaining its result.

[Independent review](independent-review.md) approved the bounded source/case-identity change, protected recovery target and fixture cleanup. These approvals do not cover full S1, normal rollout or the remaining S01–S08/G01 acceptance results.
