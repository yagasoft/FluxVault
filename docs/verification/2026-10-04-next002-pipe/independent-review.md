# Independent transport review

Reviewer: existing independent Astra reviewer `next002_design_review`, 4 October 2026. Scope: the new Core pipe contracts/server/client/framing/limits, owned caller context, Windows native factories/security/token provider and focused native/client tests. No normal rollout or final NEXT-002 approval.

The first assessment required three corrections:

1. Broken accepted peers made `IsConnected` false, skipped anchor reset, and stopped subsequent acceptance. Reset every accepted connection while retaining the handle, and prove later requests succeed after EOF/truncated/no-receipt peers.
2. Receipt delivery failure replaced an already received service outcome. Bound housekeeping receipt delivery and preserve the authoritative complete response, with closure/cancellation/stall regressions through the actual client.
3. An unknown-member depth fixture could fail for the wrong reason. Use known members under a small configured depth, plus successful parsing of the same payload under the normal depth.

Final outcome: **approved for this bounded transport milestone**. All three blocking findings resolved. The reviewer inspected the current anchor reset, bounded best-effort receipt, mandatory Stream factory with verified Windows forwarding, and meaningful depth tests. Retained TRX confirms 17 native and seven Core focused passes, zero skips; the reviewed Release log records zero warnings/errors. No further blockers in the remediated scope.

The review explicitly leaves standard A/B-to-SYSTEM, extra-instance, packaged/remote and full S1–S3 gates open. Technical approval does not authorise deployment. The later combined 754-test run broadens regression evidence but does not satisfy those native/product gates.
