# Once-only installation provisioning contract

This is the missing installation prerequisite inside the agreed NEXT-002 slice,
not a roadmap extension or operational approval. Implementation stays in the
existing branch. Normal staging services/data are unchanged during preparation.
The existing independent Astra reviewer approves this bounded approach, with
the implementation requirements below. Completed implementation/runtime review
remains required before exposure or operational approval.

## Intended result and alternatives

A fresh, protected single vault can be provisioned through an authenticated
native creator request, then opened by the ordinary product service for
save → backup → history → verified recovery. Missing bootstrap still fails
before the normal pipe opens. Existing v1.0.4 storage remains independently
recoverable; neither its root ACL nor its metadata schema is adopted/converted.

Use the existing service executable in an explicit bounded setup mode, with a
distinct setup pipe and a SYSTEM-owned trusted ticket. Extract fresh provisioning
from the fixture into a product component, then invoke it through that setup
host. This reuses the authenticated transport, catalogue, metadata, marker and
storage checks without introducing a new service/project or product vault menu.

Direct administrator-written ownership would lose the native creator proof.
Automatic provisioning on normal startup would weaken missing-bootstrap refusal.
Both are excluded. No general installer framework or future multi-vault seam is
needed.

## Identity, storage and publication

- The setup host runs as non-impersonating LocalSystem. Normal service mode is
  unchanged; explicit setup mode never registers the normal product pipe.
- Acquire the fixed setup pipe's exclusive first-instance ownership for the
  host lifetime before any provisioning. Different ticket identities cannot
  permit concurrent setup hosts for the same Windows installation. Disposable
  fixtures retain their existing GUID pipe namespace for isolated verification.
- A strict bounded ticket is read through the existing protected-file/pinning
  guard. It names one installation/operation identity, protected binding/endpoint,
  intended creator SID, finite setup deadline and fresh initial configuration.
  The ticket is limited to 1 MiB/depth 32 and the exact serialized bootstrap to
  64 KiB/depth 16. Validate the bootstrap and its deterministic `.provisioning`
  file path, reject file/directory collisions, and recheck the normalised metadata
  binding before effects. Each directory parent must already exist or itself be
  an explicitly planned fresh root; do not create undeclared intermediate paths.
  It durably identifies every potential root, file and database namespace before
  creation, making interrupted owned resources attributable. The ticket's
  intended SID restricts admission; the native authenticated pipe
  caller supplies the owner recorded in the catalogue. No client owner SID or
  ordinary-user-selected repository/endpoint is trusted.
- The public service entry point publishes only the fixed default bootstrap.
  Component-level explicit paths are needed for the existing disposable fixture,
  as with ordinary product open; they do not become product lifecycle commands.
- Setup accepts only its exact correlation identity and handshake, from the
  intended native creator with permitted impersonation. Other actors, malformed
  handshakes and wrong correlations fail before storage/database mutation.
- One setup mutation gate serialises provisioning. Check the complete ticket,
  caller, configuration, every root, bootstrap absence and both database namespace
  absences before the first mutation. It requires no bootstrap,
  no existing catalogue/metadata namespace and absent private repository/state/
  mirror roots. Fresh roots have explicit SYSTEM ownership and inheritable
  SYSTEM/Administrators rights, with no untrusted create/write grant. Creation
  must be exclusive with the final security descriptor under retained verified
  parent handles; permissive creation followed by hardening is forbidden. Existing
  populated roots are never hardened/adopted. All accepted roots are pinned and
  verified using current storage guards before durable product publication.
- Provision the existing singleton catalogue, fresh v4 metadata and repository
  markers; initialise ownership from that authenticated caller. Verify endpoint,
  creator, binding, namespaces and markers through the normal product checks.
  Flush the complete bounded bootstrap, close it, and publish require-new as the
  final activation record under the retained protected parent. No normal runtime
  becomes available before success. After publication, cancellation/acknowledgement
  loss cannot be reported as proof that nothing activated; preserve that exact
  installation and reconcile it through ordinary authorised status.
- The component records the exact result for same-lifetime replay. The setup
  transport admits one request at a time and terminates only after the authorised
  attempt's response/receipt handling and native caller cleanup finish. A lost
  receipt has a finite deadline. Reconnection after shutdown uses ordinary
  bootstrap/status reconciliation. Denied/malformed requests do not end setup.
  Reopened setup does not create another vault or
  repair/adopt partial state. A lost acknowledgement is reconciled against the
  protected bootstrap and ordinary authorised status, never by blind provisioning.

## Failure and lifetime

Provisioning is not one cross-filesystem/database transaction. A failure before
bootstrap publication preserves partial owned state, returns a clear unavailable
result and stops the affected setup attempt. Ordinary startup remains blocked.
There is no automatic deletion, resume or alternate owner/path. An interrupted
fresh attempt is handled by explicit operator recovery of its exact journalled
resources or a newly reviewed target; existing user data is never cleanup input.
Latch the attempt after effects begin, including failure: queued handshakes
cannot restart partial provisioning in the same host.

The setup host/transport have a finite lifetime, stop accepting work and join all
accepted requests, native tokens, database pools and handles. The orchestrator
records exact PID/start/executable/job identities before launch and joins owned
children. It records a terminal setup result before proceeding to installation
or live probes. A timeout/uncertain result is not successful provisioning.

## First executable milestone and checks

Inside the existing disposable Windows/SSPI fixture:

1. Prove wrong actor/correlation and existing/partial target refusal before
   any new effect. Add meaningful regressions before changed product behaviour.
2. Native A handshake → fresh real catalogue/metadata/repository → protected
   bootstrap → joined setup host → ordinary product open → actual save/backup/
   history/independently verified file and folder recovery. Native B remains denied.
3. Repeated successful request preserves the original identity and ownership;
   cancelled/failed publication cannot activate a partial installation or replace
   an existing bootstrap. Check real stores/files, not source strings.
4. Verify zero warnings, affected combined tests, original service/PostgreSQL and
   unrelated-file preservation, plus a fresh exact-resource teardown census.

That milestone is the effort checkpoint before packaging/cutover work. Independent
review evaluates the complete consequential change and its recovery evidence.

## Concrete staging target to prepare

Proposed once-only installation identity: `7871ff7f8d1b404db20771f2e742364f`.
Creator restriction: native SID `S-1-5-21-136112424-624261118-1239521417-1001`,
observed from the current Windows operator. Actual catalogue ownership still
comes from the matching authenticated creator request.

Prepare a fresh `fluxvault_single` database and `fluxvault_service` role on the
installed PostgreSQL 18 instance, using exact loopback SSPI mapping to SYSTEM
and scoped rejection for other principals. Refuse name collisions. Preserve
unrelated databases/authentication lines. Temporary provisioning-administrator
access must be removed immediately; reload only, never PostgreSQL restart.
Exact SQL/HBA/ident changes and normal-instance checks must be prepared/reviewed
before their separately approved execution.

The current `C:\ProgramData\FluxVault` root is inherited, administrator-owned
legacy staging storage. It must be preserved under a verified rollback target
before a fresh SYSTEM-owned root can occupy the default path. Preserve legacy
ACLs/data without claiming that a new parent ACL alone secures old children.
Current v1.0.4 database/role remain preserved; remove its specific temporary
loopback trust block only as an explicitly approved cutover change, retaining
the exact restoration data. Do not expose old binaries to fresh v4 metadata.

The retained v1.0.4 setup exists at
`E:\Drive\Work (1)\Code\FluxVault\artifacts\release-integrity-v104\Yagasoft-FluxVault-v1.0.4-win-x64-Setup.exe`;
fresh SHA-256 observation matches
`DD4C47DC6B20C0B348691289481965002466ACDF373CEA26E5D823AB9AF84F61`.
Prepare checked payload/installer changes so the service is not automatically
started against a missing bootstrap. Preserve current service registration,
payload hashes, configuration, data and authentication rollback before stopping
the old service. Live clients must be quiescent; do not kill an unowned client.

The final operational request must include the exact candidate payload/hash,
database/role/authentication delta, preserved legacy locations, service stop/
start sequence, verified rollback and native owner/ungranted-user/live recovery
probes. Independent final G01/deployment review and the user's separate rollout
approval remain required. This document authorises no normal-machine change.
