# Native user and group access verification

Bounded S01 verification based on `8946e65`, using the existing explicitly owned
Windows/SSPI fixture. No product authorisation policy or normal installation
change is proposed.

The optional `-RunNativeAccessTests` follows the native creator workflow and
initial ungranted-user denials. The real elevated runner user connects through
the authenticated pipe, grants only ReadHistory to B, replaces that grant with
only ReadHistory for the existing owned fixture group, and finally revokes it.
Fresh ordinary B logon tokens exercise each phase. Backup, maintenance,
retention, recovery and grant management must remain denied. B has no fixture
group membership during the direct-user phase. Group-only replacement must
deny B before membership is added, then permit its fresh member token; this
prevents a leftover direct grant from satisfying the group test. Known elevated
grant receipts must remain private to their originating actor. The controlled
SYSTEM fixture disposes and reopens the actual protected service composition,
then checks the persistent group grant again. This is service-object reopen,
not an operating-system reboot or process restart claim.

Only fixture-owned B membership in the already journalled fixture group changes.
Exact SID/description checks precede it; normal users/groups and database
authentication remain untouched. Existing finite deadlines, hidden contained
processes and ownership-based teardown apply. Failure preserves evidence and
the normal installation; all fixture state is disposable, with no existing-data
adoption or migration.

The TestHost Release build and PowerShell syntax checks pass with zero warnings
or errors. The retained `build-reviewed.log` compilation failure was a missing
System.IO import in the new test helper, corrected before native execution.

Independent Astra review accepted the corrected rehearsal and final bounded
runtime evidence. [Fixture `ccabb1b8008d492986e30a7fca83f2f8`](native/ccabb1b8008d492986e30a7fca83f2f8/result.json)
passes all forty added assertions: nine elevated checks, nine direct-user
checks, two group-nonmember denials, nine member checks, nine reopened-service
checks and two revocation denials. The existing 132 creator checks and nineteen
initial ungranted-user command denials pass; SYSTEM records 171 successful
product responses. Real configuration, history and independent SHA-256 recovery
are retained. Group access exposes neither another actor's real receipt nor
direct protected-state/runtime write access.

The [cleanup census](process-census.json) confirms all 27 exact captured process
identities, the private root and journalled resources are gone. Accounts, group
and task are removed; no task-attributed build/test/CLI workers remain. Normal
service identities, PostgreSQL authentication files, original configuration and
unrelated primary edits are unchanged. Product source and dependencies are
unchanged from `8946e65`; its 1,313 ordinary passes and forty integrity passes
remain the current product checks, rather than rerunning them for a test-only
extension.

Packaged identities, complete S01/S06/G01 and separately approved installation
rollout remain open; these checks do not substitute for those gates.
