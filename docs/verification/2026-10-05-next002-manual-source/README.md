# Manual source authority and startup without an owner request

Bounded S04 verification of the authorised single-vault manual workflow, based
on `91238c4`. This adds tests to the existing native workflow and clean process
replacement fixture. No production path, command, scheduler, VSS fallback,
package/profile/trust change or normal installation operation is introduced.

The actual authenticated SYSTEM executor receives a manual backup while native
creator A holds an exclusively locked working file. A changes both bytes and
length before locking, preventing unchanged-file optimisation from explaining
the result. The terminal backup fails with zero captures/deletions and every
history identity unchanged. After releasing the handle, original-ID replay and
receipt lookup retain the exact failure without capturing. Only a new operation
captures the pending change; recovery independently matches its SHA-256/length
and preserves the working source.

The focused Windows test confirms the native handle distinction: Windows allows
attribute-only metadata inspection despite the exclusive data lock, so the file
remains present; data capture fails. Once released, capture succeeds and all
pins release before the directory moves. The initial metadata expectation was
incorrect, rather than a product defect: its [diagnostic](attribute-observation.trx)
is retained and the assertion now verifies presence, exact length, failed data
capture and subsequent released handles. No product assertion was weakened to
allow a deletion or successful locked capture.

Before process replacement, A changes an already protected nested working file,
then the existing supervisor joins A and the original SYSTEM process. The
replacement opens the actual protected bootstrap/service and serves its native
pipe before publishing readiness. With protection enabled and no owner requests,
one coherent SSPI SELECT compares the complete vault/configuration, every
operation receipt, history generation, manifests and current pointers before
and after the bounded observation. The [accepted native run](native/72370762bcfb46f79b3e5f5f9e83d5bf/result.json)
observes 549 ms and equal durable snapshots. An authorised status request then
reports unavailable automatic protection and no backup activity. Explicit manual
backup captures the pending note and independently verified recovery matches it.

This runtime result combines with the reviewed current composition: product
`Program.cs` registers only its pipe worker, product open creates no scheduler,
and caller capture never falls back to VSS or SYSTEM source reads. It does not
prove or deliver logged-off scheduling, VSS capture or reboot support. Previously
executed [native denied-file/subtree checks](../2026-10-04-next002-source-inventory/README.md)
retain actual caller refusal and real-store history preservation. Existing output
ACL, reparse, race and cancellation evidence remains applicable.

The native creator workflow passes 141 checks, ungranted B is denied nineteen
commands, and creator process-replacement phases pass 9/19. The private postmaster
identity remains unchanged. The runner requires the new locked-source and
enabled no-owner observation results; independent preflight caught and corrected
its obsolete exact phase counts before execution. No weakened assertions or
additional resource types/deadlines are used.

All 148 Windows tests and 63 focused fixture/configuration tests pass without
skips. The final complete Release build has zero warnings/errors. The read-only
[census](resource-census.json) verifies 55 captured process identities absent,
all 56 journal resources removed, and the exact root/accounts/group/SYSTEM task
gone. Installed service identities, PostgreSQL authentication/configuration and
unrelated primary-checkout edits remain unchanged. All owned build/test sessions
have exited; unrelated processes are preserved.

The existing independent Astra reviewer accepts the completed test change,
native evidence and teardown with no blocking findings; [scope](independent-review.md).
This closes the bounded manual-only S04 proof, subject to final whole-branch G01
composition review. Packaged identity, complete implemented-screen accessibility/
scale, rollout preparation and the separately approved installation/live rollout
remain open. The overall roadmap and architecture are unchanged.
