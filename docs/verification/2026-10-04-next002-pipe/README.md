# Authenticated and bounded pipe transport

Status: bounded transport milestone implemented and independently approved, 4 October 2026. This clears an S1 prerequisite; the authorised two-vault round trip and S1–S3 acceptance remain open. No installation, migration, service restart, deployment, push or merge occurred.

The Windows server factory acquires the first pipe instance once, rejects remote clients, and gives ordinary desktop/packaged identities individual read/write rights without instance creation. The served anchor remains open, including between connections. Clients verify the connected handle's owner and DACL before sending any request. Production requires LocalSystem ownership; explicit private fixtures require a GUID name and fixed expected owner. There is no name-only client constructor or unauthenticated server overload.

After bounded request receipt, the Windows provider impersonates the actual writer and duplicates the effective token. Authorisation attributes exclude disabled/deny-only groups; OS access checks retain the actual token's restrictions. Tokens remain owned through asynchronous handling and response receipt. The server bounds frames, JSON depth, admission and read/write waits, and joins accepted work on shutdown. A complete response remains authoritative if its bounded housekeeping receipt fails. This does not implement mutation idempotency or unknown-outcome reconciliation.

These mechanisms follow Microsoft's [pipe security documentation](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights), [first-instance and remote-client flags](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipew) and [impersonation contract](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-impersonatenamedpipeclient). The references describe platform capability; the following results describe the observed tests.

## Observed evidence

- `red-corrected`: nine expected native failures against compile-ready missing-behaviour scaffolding, followed by nine passes in `first-green`. The initial `red` trial had three fixture ordering timeouts; concurrent reads corrected the fixture before implementation, without weakening assertions.
- `server-red`: all four cases fail against the original real server: oversized/deep/forged identity payloads reach the handler, and shutdown returns before accepted work ends.
- `receipt-red`: caller lifetime ends before response receipt; the new contract keeps it alive through bounded receipt handling.
- `broken-anchor-red`: EOF and truncated frames reproduce server termination. `no-receipt` was already a passing control. All three pass after resetting every accepted anchor connection, including broken streams.
- `receipt-failure-red`: actual client flow loses a fully received success when receipt fails, cancels or stalls. All three pass with bounded best-effort receipt handling.
- `corrections-green`: 17 native Windows tests and seven Core transport/frame cases pass, zero skips. The corrected depth case uses known members at depth four; the same payload succeeds at depth 32.
- `combined.log` and four distinct TRX files under `combined`: **754 non-PostgreSQL tests pass**, zero failures/skips: Core 343, App 222, Windows 34, Integration 155. PostgreSQL-tagged cases were explicitly excluded and are required later; this is not G01 or whole-suite PostgreSQL evidence.
- `release-build.log`: current Release solution build with zero warnings/errors.
- `process-census.json`: launched test/fixture processes absent after completion. No temporary accounts, service tasks or PostgreSQL clusters were created in this milestone.

[Independent review](independent-review.md) found three blocking issues during implementation; all were reproduced/corrected and approved. Existing integrity fixtures now require their verified same-owner private pipe. Their full PostgreSQL executions remain pending; no assertion was weakened to compensate.

## Remaining product and native gates

The installed service composition is intentionally unfinished in this isolated branch: the new transport requires an authenticated dispatcher, and that vault dispatcher is the next S1 batch. Do not deploy this intermediate build. The legacy profile/operation handlers are internal application contracts and cannot be passed to the public pipe-server constructor.

Standard A/B clients must still prove connected-handle verification against SYSTEM, refusal of extra instances, enabled/restricted group semantics, packaged/remote client behaviour and token/path access. Vault ownership/grants/catalogue, explicit target/revision/operation binding, same-database namespaces, source/output handles, background access and interruption semantics remain unimplemented. The [accepted plan](../../superpowers/plans/2026-10-03-next002-with-protection-save.md) and its full gates are unchanged. The [disposable SSPI milestone](../2026-10-03-next002-windows-fixture/README.md) remains prerequisite evidence only.
