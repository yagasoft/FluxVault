# Independent bounded review

Reviewer: the existing independent Astra `service_security_review`, 4 October
2026. Scope: scoped PostgreSQL history keysets/generation and schema refusal,
authenticated page/snapshot routes, byte bounds, actual inventory UI and
configuration tests, owned native/integrity evidence and resource cleanup.
Whole NEXT-002/G01 and normal installation rollout are excluded.

The reviewer accepted the fresh-schema approach and private fixture preflights.
Required corrections were the drive-root entry key, exact scope/consistent
recorded-child fixture, cancellation ownership cleanup, atomic UI replacement,
case-distinct IDs, repeated-disposal join and malformed/overlapping page refusal.
Retained red/green evidence proves the affected runtime behaviours. The actual
WPF regression additionally caught Closing re-entry; queuing final Close after
the shared read join passed the real-window test.

Final review identified unavailable initial binding escaping the browser/startup
action and watched-folder event. Four actual public-command/startup red cases
reproduce that failure. The shared handled entry point now refuses paging,
explains unavailability and avoids an authoritative-empty window. All 17 focused
paging/WPF cases and the final 381 App tests pass.

The final reviewer accepted **1,209 ordinary tests, 40/40 integrity tests,
zero-warning Release build, four removed fixtures and 62 absent owned process
identities**, with normal installation unchanged. It found no further
implementation blocker. Its remaining documentation correction concerned
architecture.md's stale schema-v2/full-history statements; those now describe
schema v3, delivered recovery-history paging and remaining current-file paging.
This correction is documentation-only and needs no executable rerun.

The bounded implementation/evidence review is accepted. No self-approval of
high-risk source or installation action is substituted. Scale/accessibility,
remaining S2/S3 work, full-branch G01 and separately authorised normal rollout
remain open, with the original applicable gates unchanged.
