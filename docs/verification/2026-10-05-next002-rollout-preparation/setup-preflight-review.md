# Independent setup preflight

The existing Astra service/security reviewer approves the bounded native run
`6eb5ebbe54e04cbaabf12fd52ac73ec6`. No blocking preflight finding remains.

Review corrections were reproduced before fixing them: activation-file equality
or ancestor collisions with directory targets, pending-file collision, oversized
serialized bootstrap, invalid pending filename and metadata normalisation that
would change the immutable binding. Ticket validation now rejects all of these
before effects. Its nineteen focused cases pass within Core 105/105.

The serial terminal transport is accepted: one listener/request, retained
first-instance ownership, response/receipt deadline, native caller disposal,
disconnect, then terminal predicate. Native tests prove held/missing receipts,
denied/malformed admission and subsequent pipe ownership. Ordinary service
defaults remain unchanged. All 154 Windows checks pass.

The reviewer inspected protected native exclusive creation, both-namespace
freshness checks, native creator ownership, failed-attempt latch, require-new
activation and joined lifetime. The same disposable fixture root/database/
accounts/jobs contain the new failure cases. Nine protected tickets are exported
under a bounded allowlist before teardown; no package/profile/trust or normal
installation operation is added. Tooling 63/63 passes and the final complete
Release build has zero warnings/errors.

Native execution and teardown acceptance remain pending. The checkpoint runs
actual provisioning components over the private fixture pipe; it must not claim
execution of the installed fixed-path Service.exe setup entry. This assessment
does not approve G01 or normal installation rollout.
