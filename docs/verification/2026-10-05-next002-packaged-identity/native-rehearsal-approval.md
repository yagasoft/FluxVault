# Bounded native package identity rehearsal

Technical review: approved preparation. Execution authorised by the user's
5 October 2026 approval of the pending S01/D01 prompts. This authorises the exact
scoped rehearsal below and the guided disposable desktop session; normal
installation rollout remains separately gated.

Fresh native fixture ID: `bf4645f9aaba450abb0b822dea68a6c7`.
Root: `C:\ProgramData\FluxVault.Tests\NEXT002\bf4645f9aaba450abb0b822dea68a6c7`.
Package: `FVGate.Package.bf4645f9aaba450abb0b822dea68a6c7`, version `1.0.0.0`, x64.
Publisher: `CN=FluxVault Fixture bf4645f9aaba450abb0b822dea68a6c7`.

Run the prepared Windows fixture with catalogue, metadata, single-vault and
packaged identity switches. Its existing disposable resources remain the
owned A/B accounts, group, private SSPI cluster, contained jobs and SYSTEM task.
No normal installation or database is targeted.

Additional Windows changes are exactly:

- Create/load the Windows profiles of newly created `FVGateA_261003` and
  `FVGateB_261003`, binding their recorded SIDs and exact expected profile paths.
- Trust only the generated short-lived public signing certificate in those
  two accounts' `CurrentUser\TrustedPeople` stores.
- Register the one GUID-named full-trust desktop identity package separately
  for those two accounts, pointing to the protected cloned TestHost runtime.

No machine certificate trust, normal-user trust, Developer Mode, sideloading
policy, shell association, startup extension or service registration is changed.
Signing uses the authenticated SDK snapshot and a transient private PFX, which
is deleted before registration. SignTool's persistent current-user imports are
also retired through the independently reviewed native recovery hook before
registration. The public thumbprint and payload hashes
are fixed in protected ownership metadata before either per-user side effect.

Acceptance: A/B execute the apphost directly; native package full name and
ordinary Windows SID/token must match. A retains creator history and independently
hashed verified file recovery; B remains denied status, repository identity and
history despite identical package identity. The existing save → backup → history
→ verified recovery assertions also run. No simulated token counts as acceptance.

Rollback/teardown: contain and join all actors/tools, unregister that exact
per-user package and remove only its public certificate trust, verify global
absence after both users, then remove the owned unloaded profiles and accounts.
Unknown identity, loaded/partial profile or unexpected package is preserved
with an error. Protected ownership enables interrupted recovery; no broad
package/profile deletion is used. Final census verifies exact process identities
and fixture resources absent, with normal services, data/configuration,
developer/sideload policy and certificate stores unchanged.

```powershell
./eng/test-windows-database-boundary.ps1 -FixtureId bf4645f9aaba450abb0b822dea68a6c7 `
  -RunCatalogueTests -RunMetadataTests -RunSingleVaultTests -RunPackagedIdentityTests `
  -EvidenceDirectory ./docs/verification/2026-10-05-next002-packaged-identity/native/bf4645f9aaba450abb0b822dea68a6c7
```

Normal FluxVault rollout remains separately gated. PostgreSQL is not restarted.

## Approved bounded machine-trust extension

Technical preparation is independently accepted after the reopened-boundary
correction; final affected checks pass 116/116 with no skips or new warnings.
The user approved this exact additional scope on 7 October 2026. Execution now
uses the prepared command below; normal installation rollout remains separately
gated. Current Windows service lifetimes differ from the 5 October evidence;
the new run captures their current identities and requires before/after equality.

The approved per-user attempt reached registration and failed with `0x800B0109`.
Its exact root, accounts/profiles, package trust and imported signing keys were
removed. No successful packaged caller proof is claimed.

Retain the exact fixture ID, root, A/B account names, package/version/publisher
above. Add only one newly generated short-lived public code-signing leaf to
`LocalMachine\TrustedPeople` for one bounded rehearsal. Its certificate has no
private key, is not a CA, permits only digital signature/code signing, and has
a lifetime under four hours. The old certificate/package were deleted by
teardown; this approval targets the fixed fixture publisher and bounded
generation procedure, not reuse of the old certificate bytes.

Before import, the privileged runner verifies the protected public CER hash,
thumbprint, publisher and leaf constraints against package metadata, verifies
matching signing-key retirement and fresh native absence, and refuses existing
machine trust. It durably records the exact new hash/thumbprint, fixture/root,
store, publisher and owner SID before adding that leaf. A/B independently reopen
their per-user store before registration and record exact bytes, SID and absence
of private material. No Root store, private-key store, normal runner certificate
trust, Developer Mode or sideloading-policy change is included.

Rollback uses the existing cleanup path after all actors/tools have joined.
It removes only the exact recorded public leaf, verifies fresh store absence,
then records retirement. This runs on success or failure, before later profile
cleanup can fail. The same protected intent supports interrupted recovery;
changed identity or a certificate reappearing after recorded retirement is
preserved with an error. Final census must show unrelated normal/machine trust,
services/data/authentication and PostgreSQL postmaster unchanged.
Interrupted cleanup keeps the package trust/policy snapshot requirement from
the loaded `before.json`, including a stop after import but before profile intents.

Machine trust temporarily affects all Windows users. The 7 October approval
extends the earlier per-user scope only as specified here, for this command:

```powershell
./eng/test-windows-database-boundary.ps1 -FixtureId bf4645f9aaba450abb0b822dea68a6c7 `
  -RunCatalogueTests -RunMetadataTests -RunSingleVaultTests -RunPackagedIdentityTests `
  -PermitMachinePackageTrust `
  -EvidenceDirectory ./docs/verification/2026-10-05-next002-packaged-identity/native/bf4645f9aaba450abb0b822dea68a6c7-machine-trust
```

If interrupted, recover through the same existing runner and exact fixture:

```powershell
./eng/test-windows-database-boundary.ps1 -Mode Cleanup `
  -FixtureId bf4645f9aaba450abb0b822dea68a6c7 `
  -EvidenceDirectory ./docs/verification/2026-10-05-next002-packaged-identity/native/bf4645f9aaba450abb0b822dea68a6c7-machine-trust
```

Recovery removes an already owned leaf; it never creates or adopts trust.
Normal installation rollout is neither requested nor authorised by this extension.
