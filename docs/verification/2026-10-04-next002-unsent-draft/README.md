# Unsent protection draft verification

Bounded S2 interruption work on `codex/next002-planning-20261003`, based on
`9a26ee2`. One vault, creator access, repository binding and the existing
configuration preservation and save-dependent-backup gates remain unchanged.
No installed service, database adoption, migration or PostgreSQL restart occurs.

## Behaviour and local recovery

Protect edits now have a separate per-user draft at
`%LOCALAPPDATA%\FluxVault\protection-draft.json`. It contains a complete editable
snapshot, repository identity, exact baseline revision/fingerprint, stable
editing identity and fresh publication identity. Strict bounded JSON and an
exclusive file gate protect read/CAS/write/discard; flushed atomic replacement
supports open snapshot readers and cleans owned failed/cancelled temporary files.
Incomplete repository/mirror text and regex patterns remain editable; malformed
collection members are refused before partial UI application.

One coalesced asynchronous writer uses the typed/defaulted Options setting
`ProtectionDraft.SaveDelayMilliseconds` (500; constrained to 100–5000). Continuous
typing cannot indefinitely debounce every write. Local status distinguishes
pending, retained, unreadable and failed retention. Edits since the latest
completed write can still be lost in a crash; this is explicitly described in
Options. A local write never dispatches a service save or backup.

Save retains the exact submitted draft, reserves the full receipt and durably
associates its operation before dispatch. Definite local preparation failures
send nothing, preserve any partial record and permit retry when no receipt is
reserved. Restart inspects both records before applying a submitted snapshot;
newer edits survive without replay. Original and exact acknowledged baseline
phases remain uncertain until the original authoritative receipt is checked.
Confirmation retires the submitted draft or retains all newer edits at the
confirmed baseline before conditionally clearing that receipt. Editing identity
survives interrupted receipt retirement; a completed editing session or explicit
discard does not donate its identity to a new session.

Discard fences the writer, rechecks the reviewed draft/receipt/edit generation
after status and conditionally retires the record before applying settings.
Edits arriving during retirement are retained. Corrupt unsent bytes may be
quarantined only after bounded length/SHA-256 revalidation and a non-overwriting
rename; oversized/unreadable files and unresolved dispatched receipts remain
gated. Exit fences the visible editor, refuses outstanding save/receipt work,
force-flushes and joins the writer before joining reads. A failed flush keeps
the window usable and explains the failure.

## Repeatable tests and visual evidence

Tests use the actual `FileProtectionDraftStore`, reopened real configuration and
receipt stores, and public view-model commands. No source-string assertion
stands in for these contracts. Retained reds establish store/CAS corruption,
never-dispatched restart, coalescing policy, draft/receipt preparation ordering,
discard cross-session refusal, structural collection refusal, late retirement
edits, original correlation after retirement, receipt-check Exit lifetime and
local preparation failure classification. Controlled repeated-publication and
receipt-cleanup interruption checks prove the reviewer corrections.

Final ordinary suites pass **1,313 tests**, with zero failures or skips: Core
536, App 473, Windows 141 and ordinary Integration 163. The existing C01/C02/C03
full-record real-store/actual-VM regressions remain. The [Release build](build-final.log)
has zero warnings or errors. Core/Windows/ordinary Integration checks are reused
after the final App-only local failure classification change; their checked
implementation and dependencies are unchanged.

The finite [WPF renderer](run-ui-render.ps1) uses disposable local stores and a
controlled service refusal. It verifies separate non-overlapping
[save failure and local retention status](ui/protect-draft.png), zero dependent
backup and the [loaded delay within the Options viewport](ui/options-draft.png).
Both actual images were inspected. The initial `ui-initial` image exposed a
missing layout/viewport check in the helper; its `OptionsDelayVisible` flag
alone is not evidence of visibility. The corrected final helper checks geometry
after layout and scrolling. Both renderer processes joined with exit zero.
These images cover this small UI addition at normal DPI; broader keyboard,
Narrator, high-contrast, DPI and redesigned-flow acceptance remain open.

## Native evidence and remaining gates

The native probe uses only creator-owned fixture paths outside protected
office/cad sources. It reopens retained edits without IPC mutation, verifies
exact durable association before the actual authenticated save, dispatches one
dependent backup and checks real history/recovery with independent SHA-256.
The runner requires `LocalProtectionDraftVerified=true`.

Retain the failed native runs: `836d62d1acf64ed8ac2aafec0019ea23` had an opaque
sequencing assertion; `d9cf9e8cede947789c13f9650a11d0a7` revealed the probe had
attempted an immutable mirror infrastructure edit. The actual UI correctly
retained the draft and dispatched zero backups. `c2d865c818bc4ac0a96ecc38034b9bbc`
assumed the legacy wildcard watcher setup supplied a browser selection; it did
not. The corrected probe creates a valid explicit Office selection with scoped
`.docx` regex through the actual browser VM, preserving infrastructure gates.
The successful `05370cfb4b694d7f96b08529f545291e` run passes 132 creator checks,
19 ungranted-user denials and 156 SYSTEM product responses, with the draft marker
true, no failure and all journalled resources removed. The final
`2eca1d6b517147af834c074a89167d0d` run repeats those results after the App-only
local preparation correction. The fresh integrity fixture
`d7158ef3f1fe4b958963b2dafbb22286` passes all forty PostgreSQL cases without
failures, skips or aborts.

The [complete ownership census](process-census.json) confirms all six private
fixture roots and journalled resources are removed, 111 exact captured process
identities (including both WPF renderers) are absent, all 38 database intents
are removed and no task-attributed build/test/CLI worker remains. Accounts,
group and scheduled task are removed. Installed service identities, PostgreSQL
authentication files, original configuration and unrelated primary-checkout
edits are unchanged.

Independent Astra review accepted the bounded implementation and final runtime
evidence, including the meaningful publication/correlation/discard/Exit
corrections described above. See [review outcome](independent-review.md).
Broader lifecycle/concurrency/performance,
client inventory accumulation/rendering, background/VSS, full G01 and separately
approved normal installation rollout remain open. Retain original v1.0.4
installation/database/storage assets for existing-data recovery. Options unsent
drafts are outside this bounded Protect batch; dispatched Options receipt
recovery remains intact. Rollback is to preserve the local records and use the
matching pre-rollout application/service assets; never adopt another repository
or erase an unresolved receipt to resume work.
