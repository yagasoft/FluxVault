# Native service-process replacement

Bounded continuation of the existing NEXT-002 lifecycle checks, based on
`c337ed0`. Product behaviour and the overall roadmap are unchanged. This checks
clean replacement after completed operations and an admitted unknown outcome; interrupted effects, reboot,
packaged identity and the full S07/G01/rollout gates remain separate.

The optional, default-off `-RunRestartTests` requires `-RunSingleVaultTests` and
cannot combine with the native-access or packaged-identity extensions. It uses
the existing disposable A/B accounts, group, SYSTEM scheduled task, private
SSPI cluster and contained jobs. No profiles, trust changes, normal service or
database installation is involved. The observed per-run resource count is 56, within
the existing journal limit of 64; failures preserve ownership and evidence.

The original server runs the existing creator recovery and ungranted-user
checks. The elevated native actor grants ReadHistory to only the owned group;
B is denied before membership, then a fresh B token receives history while
stronger commands and another actor's receipt remain denied. Creator A saves
the complete accepted configuration and retains its exact save/backup requests,
receipt envelopes, revision, version and independently computed source hash in
a bounded checkpoint. Only A reads that checkpoint; it supplies no privileged
runner or SYSTEM cleanup authority.

The runner joins the first scheduled invocation, native server process and
contained job before starting a new invocation. Protected SYSTEM process logs
and readiness are matched against the live process's PID, start time,
executable and native owner SID. The replacement opens the existing protected
bootstrap through the actual product service; it does not provision, adopt or
migrate data. The private PostgreSQL PID and start time must remain unchanged.

After replacement, A verifies complete configuration and revision, changes and
independently hashes the live source, and compares the original complete save
and backup receipts through status and exact request replay. History must not
change during replay. Original recovery must match the old hash; a fresh
backup must create a different version whose recovery matches the new hash.
A fresh B token verifies the persisted group grant, then the elevated actor
revokes it and a further fresh B token is denied. Both servers and every owned
temporary process must be absent after teardown. Normal service identities,
authentication files, configuration and unrelated primary edits must match.

The [native run](native/0ee82f86c1404967b35dcb2910454df4/result.json) passes all
48 added checks: creator A6 before/A14 after replacement, six elevated access
checks and B22 nonmember/member/persisted/revoked checks. The existing A132
creator workflow and nineteen initial ungranted-user command denials pass.
The actual native servers have distinct PID/start identities: 25392 at
02:27:09.7700885Z and 64584 at 02:27:29.9828027Z on 5 October 2026.

The [follow-on run](native/2b3ee620d8064a03a9537f36c1fb79af/result.json), based
on `0322d54`, adds five checks for an admitted unknown outcome. A's new-file
preview is refused because the destination already exists; its independently
hashed bytes remain unchanged. The original unknown receipt persists across
actual replacement. A removes only that fixed owned destination, making
duplicate execution observable: exact replay must still return the unknown
receipt and must not recreate the now-eligible output. All 53 checks pass
(A8/A17, elevated6, B22), together with the existing A132/B19 workflow. The
replacement identities are 12780 at 02:38:30.4029146Z and 14660 at
02:38:50.2422139Z. No fault hook or product runtime change was introduced; this
is not a forced crash or an interruption during an effect.

All 59 focused fixture/database tooling checks pass without skips. Both
PowerShell entry points parse, and the final complete Release solution build
has zero warnings/errors. No product runtime or dependency changed; the
previous product regression and forty-case integrity evidence remains
applicable to that unchanged implementation.

The [final census](process-census.json) covers both rehearsals: all four servers
and 58 captured process identities are absent, all 112 journal resources are
retired, and both private roots, jobs, accounts, group and task are gone. Each
private postmaster remained unchanged during replacement. Normal service identities, authentication/configuration
files and unrelated primary edits are unchanged. The independent Astra
reviewer accepted the corrected preflight, runtime and teardown without a
blocking finding. No package/profile/trust or normal installation was changed.

```powershell
./eng/test-windows-database-boundary.ps1 -FixtureId 2b3ee620d8064a03a9537f36c1fb79af `
  -RunCatalogueTests -RunMetadataTests -RunSingleVaultTests -RunRestartTests `
  -EvidenceDirectory ./docs/verification/2026-10-05-next002-service-restart/native/2b3ee620d8064a03a9537f36c1fb79af
```

Normal FluxVault installation rollout remains separately gated. Normal
PostgreSQL is not restarted.
