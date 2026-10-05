# Prepared packaged desktop identity rehearsal

Status: source independently reviewed and package construction verified; native
registration remains unexecuted and unaccepted. Based on `1c76d9b`.
This closes the remaining packaged full-trust desktop portion of S01 only after
real native results and teardown. It does not change product authorisation,
Windows/WPF/.NET/PostgreSQL architecture, single-vault scope or the roadmap.

## Exact proposed resource extension

Use `eng/test-windows-database-boundary.ps1 -RunCatalogueTests -RunMetadataTests
-RunSingleVaultTests -RunPackagedIdentityTests` with one fresh GUID fixture root
under `C:\ProgramData\FluxVault.Tests\NEXT002`. The existing explicitly owned
accounts A/B, group, jobs, task and private SSPI cluster remain the targets.
No normal installation rollout, staging data adoption or PostgreSQL restart.

Additional resources are limited to newly created profiles for those exact A/B
SIDs and one GUID-scoped identity package registered separately for them. Its
name is `FVGate.Package.<fixture UUID>`, publisher is
`CN=FluxVault Fixture <fixture UUID>`, and version is `1.0.0.0`. The external
cloned TestHost apphost and DLLs remain within the protected fixture runtime.
The package has one hidden full-trust desktop application, with no shell,
protocol, file-association, startup or service extensions.

One ephemeral RSA signing key creates a short-lived code-signing certificate.
SignTool imports persistent current-user Software KSP keys from the private PFX.
A flushed private baseline and public-key fingerprint precede each signing
invocation. After the contained tool exits, native recovery selects only new
matching keys, records their exact identities, deletes through the verified
native object and verifies absence. Enumeration is silent and provider errors
preserve the private recovery record/root. Unrelated keys are preserved. The
transient PFX is removed and the memory key disposed; neither private material
nor unrelated baseline names are copied into evidence. No certificate trust is
added to the normal runner's stores. Only the public certificate is trusted in
`CurrentUser\TrustedPeople` for A and B; no machine-wide trust, Developer Mode
or sideloading-policy change is permitted. Registration failure stops the
approach rather than weakening these bounds.

The installed SDK is under `E:\Windows Kits\10\bin\10.0.28000.0\x64`, beneath
a standard-user-writable ancestor. It is never executed there. Ten explicit
EXE/DLL files are copied into the private root; valid Windows Authenticode,
pinned Microsoft signing certificates and signed tool identities are checked
on the protected copied bytes. Fixed generated SxS manifests reference only
those local authenticated DLLs. Absolute executable paths, a protected working
directory and trusted-only PATH prevent fallback into the original SDK tree.
Neither the original SDK ACLs nor registry COM registration is changed.
Microsoft's [external-location identity guidance](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)
describes matching executable/package manifests and per-user public-certificate
trust. The test uses the apphost directly and requires the
[native package identity API](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getcurrentpackagefullname)
to equal the exact registered full name. Executing `dotnet TestHost.dll` does
not count as packaged identity.

## Acceptance and recovery

The same package identity must retain A's original creator access and verified
file recovery while B remains denied status, identity and history. Actual
ordinary Windows SIDs and non-administrator tokens must match the owned actors.
No package claim is added to the request or accepted as vault authority.

Profile/package intents precede logon, trust and registration. They bind the
exact fixture UUID, already recorded account SID, expected profile path,
publisher and public certificate thumbprint. Real reopened-journal regressions
reject wrong fixture identity, actor and profile paths. Per-user ownership
records are flushed before publication; partial registration retains evidence.

Each packaged child is contained, has a finite deadline, is joined before
unregistration and publishes exact process identity. Cleanup unregisters only
the exact per-user identity and removes only its public certificate trust.
The parent checks each user's package absence after that user's cleanup, then
checks global/staged absence only after both users. Interrupted B registration
cannot stop recovery at already-clean A. The parent then removes the
owned unloaded profiles and accounts. An interrupted parent may reset only the
verified disposable account's password to run its cleanup; no credential is
journalled. Missing/changed profile identity, an unexpected package or loaded
profile causes preservation and a visible failure, never broad deletion. CIM,
the exact profile directory and its ProfileList key must all be absent before
profile retirement is recorded. Ordinary actors return public ownership and
process diagnostics; SYSTEM cleanup does not read user-controlled files.
Signing-key intent/state stays in the protected private root; joined contained
tools and native key recovery handle interruption after import but before
result publication. Cleanup failure prevents private-root retirement.

Final evidence must prove both native package identities, authorised/denied
results, independent SHA-256 recovery, all recorded process identities absent,
package/trust/profile/account removal and unchanged normal services,
PostgreSQL authentication/configuration, developer settings and normal-user
and machine certificate stores. Fault/recovery and full G01 remain separate
until their actual cases are run; no skipped case counts as acceptance.

## Current preparation evidence

The full Release build has zero warnings/errors. Meaningful ownership and
interrupted-cleanup tests failed before their controls and passed after them.
The final focused suite passes 56 cases without skips. It executes actual
runner cleanup functions against stateful Windows API doubles and reopens the
real ownership store; native registration is still needed. The earlier `tooling-final`
run also selected two PostgreSQL-dependent cases without an owned cluster;
those failed as intended by the strict prerequisite guard, and are not counted
as accepted integrity evidence.

Artifact-only preparation `02c4a688f1354b6ca4ad5a342e957db9` used the actual
owned supervisor and completed manifest embedding, package construction and
both signatures. All four SDK invocations exited successfully; public
MSIX/CER hashes and PFX-file `Removed` state are retained. Earlier failed
preparations remain as evidence: copied-file identity initially differed from
localised MUI metadata, then the build exposed a missing signed MIDL dependency
and SxS manifests. Those failures were corrected without relaxing authentication
or using the writable SDK at runtime.

That first successful preparation left two imported Software KSP keys despite
PFX removal. The original cleanup claim was incomplete. Both were independently
identified by the retained certificate's RSA fingerprint and deleted through
their exact native objects; [cleanup evidence](imported-key-cleanup.json) records
their absence. The actual interrupted-import regression also preserves a new
unrelated key created after the baseline. Fresh SignTool construction
`a90e934089bd45e88f07f2b7b60473de` successfully removed both imports through the
new hook; its public [native cleanup record](artifacts/a90e934089bd45e88f07f2b7b60473de/native-key-cleanup.json)
contains only fixture keys. The reviewer subsequently required explicit silent
enumeration. Final construction `0e356aa7ec694ecfa4ff4cfce4c1e0d8` succeeds with
that correction: all four tools exit zero, both native imports are retired and
the private root is removed. Its public [cleanup record](artifacts/0e356aa7ec694ecfa4ff4cfce4c1e0d8/native-key-cleanup.json)
and [root teardown](artifacts/0e356aa7ec694ecfa4ff4cfce4c1e0d8/cleanup.json)
are retained. The final Release build has zero warnings/errors.

The fresh [artifact and integrity census](artifact-process-census.json) proves
seven private roots, named jobs, 59 captured process identities and all six
fixture signing keys absent through native/backing-file checks. All 38
database intents are retired; accounts, group, task, profiles and package
registrations are absent. Normal services/configuration/certificate stores and
unrelated primary-checkout edits remain unchanged.
No package registration or certificate trust has occurred. The separately
approved integrity refresh created and removed its disposable accounts and
database cluster; it did not use the package extension. Native token results
and installation teardown are not inferred from successful signing.

Independent source review accepted the bounded preparation after corrections;
see [review record](independent-review.md). Approval for temporary A/B package,
public-certificate trust and Windows profiles is still required before the
optional native switch. Normal FluxVault installation rollout approval remains
separately required. S01 packaged identity and full G01 remain open.

## Approved integrity refresh

The owned Windows/PostgreSQL fixture `9502277264a74301b76818204cc7db85` passed
all forty integrity checks without skips. Its [TRX](integrity-refresh/9502277264a74301b76818204cc7db85/results/PostgreSql.trx)
and [teardown](integrity-refresh/9502277264a74301b76818204cc7db85/cleanup.json)
record root removal, joined jobs and unchanged normal installation. This is
integrity refresh evidence, not packaged identity or final G01 acceptance.
