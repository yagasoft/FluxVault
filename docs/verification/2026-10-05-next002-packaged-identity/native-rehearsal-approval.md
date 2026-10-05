# Bounded native package identity rehearsal

Technical review: approved preparation; execution approval pending.

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
