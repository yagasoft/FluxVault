# Independent implementation review

Reviewer: the independent Astra integrity reviewer, `/root/integrity_review`, on 1 October 2026. This records the returned assessment, not a new test run by the reviewer.

Outcome: **implementation/source gate approved**. No concrete blockers remained in the reviewed source scope. Earlier integrity, SQL concurrency and setup-preservation findings were resolved. The reviewer inspected the retained full-run log: 653 passed, zero failed, zero skipped. The ASCII authentication-file guard and successful disposable provisioning/preservation rehearsal were also checked.

The setup approach is technically approved. At the time of review, runtime acceptance still required:

- The user's authentication-policy choice before normal-cluster mutation.
- Native folder recovery success and existing-destination refusal evidence.
- Final published-binary performance and provenance evidence.
- Installation and installed service/UI validation against the checksum-bound candidate.

The six published-binary trials subsequently completed with independent hashes and bounded memory growth; their record and binary provenance are linked from the [acceptance matrix](README.md). Authentication, native folder and installation gates remain open.

This assessment does not claim installation, deployment or unrestricted real-file readiness. The reviewer made no mutations and ran no additional tests during the final review. Earlier complete-diff review and focused correction reviews supplied the source assessment; the main owner supplied the observed verification evidence.

## Subsequent live-validation corrections

The same independent Astra reviewer assessed the actual managed-HBA-block correction and required the strengthened setup rehearsal to use ordinary SCRAM rules. That actual rehearsal passed; normal access was repaired with a scoped runtime block and reload only. No administrator rule remained.

The installed folder discrepancy received an independent design gate and source gate. The reviewer approved `NextFolderSnapshotTime`, including folder deletion tombstones, propagated updated-child timestamps, SQL microsecond separation and overflow refusal under the existing lease. File capture times remain unchanged. Deletion-version recovery retains its existing predecessor-following semantics. Nine core regressions failed before the fix and then passed; eight real PostgreSQL regressions also failed before the fix and passed in the full run. The reviewer was supplied fresh 670-pass, zero-failure/skip output. This is source approval; installed acceptance is evidenced separately.

The release script's stale incremental MSI was caught before installation. Rebuild for both WiX projects and extraction/hash comparison produced an actual 1.0.3.0 MSI with all 204 expected payloads. The installed service/CLI recovery passed. Installed desktop recovery remains pending after physical Escape stopped Computer Use; no review approval substitutes for that runtime evidence.

## Final cumulative source/evidence gate

The independent Astra reviewer subsequently inspected the final 672-pass, zero-failure/skip full-run output, zero-warning Release build, corrected release safeguards, actual 204-file MSI/published/installed hash evidence and installed file/two-file-folder recovery results. The reviewer approved the reviewed source and recorded evidence scope with no concrete blocker or unsupported completion claim. The later UI test expansion explicitly proves access, busy and integrity errors preserve selection, make one request and publish no output.

Installed desktop acceptance remains separate and incomplete after the user's Escape stop. The reviewer requires its actual UI/hash evidence and refreshed cleanup/installed-state records before closeout. This technical approval does not authorise desktop resumption or establish unrestricted professional-file readiness. No edits or unchanged test reruns were made by the reviewer.

## Installed inventory correction gate

On 2 October the independent Astra reviewer assessed the [inventory correction](inventory-correction.md) discovered during resumed installed validation. Required corrections covered unavailable roots, cache reads crossing invalidation, and shared profile-response handling for both recent versions and file-browser tracked entries. Meaningful failing regressions preceded each correction. The reviewer inspected 15 passing Core status cases and 65 passing App refresh cases and returned: “Approved for this bounded source gate. All identified inventory blockers are resolved.” Full-solution, packaging and installed desktop acceptance remain separate runtime gates.

The same reviewer approved the supporting preview directory-age correction. [Red evidence](preview-cleanup-red.log) proves cleanup removed a fresh empty directory, and [four passing preview cases](preview-cleanup-green.log) prove fresh-directory preservation and expired-directory removal. The original intermittent preview failure was not conclusively attributed to this defect; the diagnostic rerun passed. No assertion was weakened. The roadmap consistency test now expects the actual partially implemented PostgreSQL bootstrap/staging status. Broader runtime checks remain independent gates.

## Final v1.0.4 staging closeout gate

After installed desktop validation, the independent Astra reviewer inspected the current full-scope audit and retained evidence: 687 passing tests with zero failures/skips, zero-warning Release build, all 204 installed payload hashes, configuration/history preservation, installed file/folder/refusal results, native UI recovery and independent hash, inventory preservation after invalidation, temporary-process exits, empty watches and unchanged normal PostgreSQL/scoped authentication. The returned assessment was: “Approved for closeout of the bounded verified-recovery staging slice. No concrete blocker or unsupported completion claim found.” No tests or live operations were rerun by the reviewer.

The owner also verified [344 source fingerprints and 181 local document targets](closeout-checks-v104.json) and clean whitespace. This closes the approved slice, not the broader roadmap, unrestricted professional-file readiness, full disaster recovery or service authorisation/vault isolation. Source remains uncommitted/unmerged; the existing task worktree is retained.
