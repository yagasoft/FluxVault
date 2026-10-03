# Independent Batch C review

3 October 2026. Independent GPT-6 Astra, high reasoning; bounded read-only assessment of Batch C source, requirements and observed evidence in `codex/next002-planning-20261003`, baseline `afdfe1f`. No live operations or code edits by the reviewer.

## Initial findings and correction evidence

| Blocking finding | Correction and meaningful red/green proof |
| --- | --- |
| Acknowledged save with newer edits left the old selection baseline | Acknowledge only the dispatched selection baseline while preserving current rules. Second-save tests verify both newly removed scope and no repeated successful purge. |
| Ambiguous destructive-save guard inspected the current draft | Capture dispatched removal scopes before await. Held timeout/cancellation tests re-add then remove the same selection and prove no destructive replay. |
| Both Options entries/handler allowed conflicting saves | Bind both controls to a loaded, clean, idle configuration and guard the handler. Actual WPF controls and raised Click prove it, including forwarded Explorer/profile changes while modal Options is open. |
| Explorer Show versions forcibly reloaded configuration | Use normal draft-preserving refresh. Actual startup-request tests after rejection and during held save retain edits/result. |
| Discard cleared result before reload succeeded | Return whether configuration applied. Failed/skipped reload and concurrent-edit tests preserve draft and explanation. |

The bounded follow-up resolved all five and identified one related blocker: Options closing re-enabled protection saves before reconciliation with its newly saved settings. Actual modal tests reproduced delayed/failed reloads through real stores. Closing now keeps protection mutation unavailable until a fresh baseline applies, retains intervening drafts, permits deliberate discard after an unsuccessful reload, and prevents the dialogue closing during its own save. A successful later reload clears the warning and restores access.

## Final assessment

**Resolved. No remaining blocker found in the bounded Batch C review.** The reviewer confirmed the new modal tests preserve the actual persisted Options setting and reviewed the supporting lifetime/reconciliation guards. Observed focused result: 117/117; combined result: 722/722, zero skips; Release solution build: zero warnings/errors; `git diff --check` passed.

This approves Batch C and its corrections only. It does not approve NEXT-002 security, installed-system changes, rollout or the broader UI. No optional improvement was added as a release gate.
