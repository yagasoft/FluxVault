# Independent bounded review

Reviewed on 4 October 2026 using the existing independent security, integrity and Windows fixture reviewers. No remaining blocking finding for this helper milestone.

## Security review

`service_security_review` approved the SYSTEM security-only reopen of the fresh protected recovery root. The original caller-created private root remains pinned; the reopen requests only attributes, read-control, DAC and synchronise rights. Caller-token publication precedes root-last permission handover. No privileged source-data or existing user-destination reopen was introduced.

The reviewer separately approved final blind-ancestor traversal and case-prefix identity handling after inspecting the corrected two-failure baseline and sixteen passing physical regressions. Both differently cased root chains use the same fixed NTFS volume, remain pinned and are compared by native file identity. Source descendants are opened from the selected root. Failed queries or unequal identities deny access.

## Integrity and performance review

`integrity_review` identified retained disposed stream buffers as a blocker. The native baseline demonstrated 100/100 retained streams. After pruning disposed references, retaining active ownership and preserving independent single-file admission, the fresh native regression passed. The reviewer approved the final target/Core/test diff, nested bottom-up handover, publication-before-warning semantics and pre-publication cleanup. The fresh fixture records eight passing checks and complete cleanup; the combined log records 823 passing applicable tests.

## Fixture review

`windows_fixture_review` required an output-only SYSTEM cleanup helper after probe teardown. It rejected ACL normalisation because hard links can share their ACL with names outside the output subtree. It required an atomic supplemental ledger, recovery before quiescence, scheduler instance draining and an early-failure path that admits no unnecessary helper.

The reviewer approved the final helper after those corrections, then approved the direct administrator-owner check following a failed native rehearsal. SYSTEM independently validates the physical root owner against local Administrators and retains the existing path/ACL checks before reading the supplemental ledger. The final review inspected native-fifth recovery, confirmed zero pending resources and matching original journal hash, and independently compared the installation snapshots as unchanged. The fresh sixth run subsequently passed with the same cleanup results; the early-failure script also passed.

## Approval boundary

These reviews cover caller-bound source/recovery helpers and the owned disposable fixture. They do not approve the unimplemented vault executor, complete native vault permission/isolation flow, S1/S2/S3, remaining S01–S08/G01 gates, or normal-installation deployment. The roadmap, architecture and acceptance requirements remain unchanged.
