# Prepared packaged desktop identity rehearsal

Status: the approved native rehearsal reached registration but Windows rejected
per-user certificate trust with `0x800B0109`. All owned resources were retired.
S01 remains unaccepted; a separately scoped machine-trust fallback is prepared
below. Earlier preparation evidence is retained, based on `1c76d9b`.
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
added to the normal runner's stores. Under the original approval only the public
certificate is trusted in `CurrentUser\TrustedPeople` for A and B; no machine-wide
trust, Developer Mode or sideloading-policy change was permitted. Registration
failure stopped that approach. The extension below requires separate approval.

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
ten private roots, named jobs, 134 captured process identities and all captured
fixture signing keys absent through native/backing-file checks. All 38
database intents are retired; accounts, group, task, profiles and package
registrations are absent. Normal services/configuration/certificate stores and
unrelated primary-checkout edits remain unchanged.
The third native attempt added and then removed A's public per-user trust;
registration failed before any packaged caller proof. The separately approved
integrity refresh did not use the package extension. Native packaged token
results are not inferred from successful signing or cleanup.

Independent source review accepted the bounded preparation after corrections;
see [review record](independent-review.md). The user approved the exact A/B
package, per-user public-certificate trust and Windows profiles on 5 October.
That scope has been exercised; it does not include machine trust. Normal FluxVault installation rollout approval remains
separately required. S01 packaged identity and full G01 remain open.

## Native rehearsal and smallest remaining correction

Three attempts used the exact approved fixture `bf4645f9aaba450abb0b822dea68a6c7`:

- [First attempt](native/bf4645f9aaba450abb0b822dea68a6c7-first-attempt/result.json)
  found a collision with the cloned runtime's Assets directory. The package now
  owns its logo under its fresh packaging directory. An actual module regression
  proves package placement and preservation of the existing runtime bytes/ACL.
- [Second attempt](native/bf4645f9aaba450abb0b822dea68a6c7-second-attempt/result.json)
  found that an ordinary actor cannot inspect traversal-only private ancestors.
  The privileged parent retains those checks. The ordinary launcher retains
  canonical UUID/root, native SID and exact payload hashes, matching the existing
  ordinary launcher. Independently reviewed targeted cleanup retired the
  preserved root and its owned profile/accounts without widening permissions.
- [Third attempt](native/bf4645f9aaba450abb0b822dea68a6c7/result.json)
  passed those guards and reached `Add-AppxPackage`, which returned certificate
  trust error `0x800B0109`. Its [cleanup](native/bf4645f9aaba450abb0b822dea68a6c7/cleanup.json)
  proves joined owned jobs, root removal and unchanged normal installation.

Microsoft's [external-location guide](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)
describes per-user trust, whereas its [troubleshooting entry for this error](https://learn.microsoft.com/en-us/windows/msix/msix-troubleshooting-guide#certificate-not-trusted-0x800b0109)
specifies Local Computer TrustedPeople. The evidence supports a bounded fallback;
it does not prove machine trust is the only solution. Repeating unchanged
per-user registration would not close S01. The next attempt independently
reopens A/B's CurrentUser store before registration and records native SID,
exact public bytes/hash/thumbprint and `HasPrivateKey=false`.

The existing package module and runner now prepare a default-off
`-PermitMachinePackageTrust` extension. It permits only one short-lived public
code-signing leaf in LocalMachine TrustedPeople, after the signing PFX and
matching owned native keys are absent. Durable protected intent and exact
certificate identity precede import; existing cleanup removes only that leaf
and verifies absence on success, failure or interrupted recovery. A collision,
changed identity or reappearance after retirement is preserved for review.
No Root store, private-key store, normal-user trust or Windows policy is changed.
The [approval record](native-rehearsal-approval.md) specifies this additional
scope and its executable recovery command. Machine trust had not been executed
at that preparation checkpoint; the 7 October result is recorded below.

Meaningful retirement-owner/fingerprint and reappearance regressions failed
before their guards; the native-key case likewise fails before the existence
check. The focused fallback suite passes 10/10. These tests reopen actual
protected ownership files and use real public certificates and native key
presence, substituting only the machine X509Store boundary. They are logic
evidence, not real machine-store or packaged-token acceptance.

The combined affected tooling suite passes 115/115 without skips or new build
warnings. All test parents/children are joined, the real test-owned CNG key is
absent, and no tooling scratch root or worktree test/build worker remains.
The refreshed existing census confirms all ten private roots and 134 captured
native identities absent, signing imports retired, and normal services,
authentication/configuration, trust and unrelated primary edits unchanged.
The frozen normal candidate's MSI and Setup hashes still match their manifest.

Independent review identified one pre-profile interruption gap: cleanup without
the original command switches could omit package trust/policy verification if
the runner stopped immediately after machine publication. The actual snapshot
function now retains this requirement from reopened `before.json`. The real
baseline/journal/lost-ack regression fails before that correction and passes
after it, also detecting a subsequent trust change. Native state/store boundaries
are substituted in this focused check; it does not claim native interrupted
machine-store proof. Existing ownership/cleanup mechanisms remain unchanged.

The final combined affected suite, after that correction, passes 116/116 with
zero skips and no new build warnings. The independent reviewer accepts the
complete bounded preparation with no blocking finding. The separate machine
resource approval, actual packaged native result/teardown, D01 observations and
normal rollout/live validation remain open; NEXT-002 is not complete.

## Authorised packaged result on 7 October

The user approved the exact fixed fixture's bounded machine-leaf scope. The
[first machine attempt](native/bf4645f9aaba450abb0b822dea68a6c7-machine-trust-first-attempt/result.json)
stopped at the launcher's combined package/certificate collision guard. Cleanup
retired its leaf and every owned resource. Windows CurrentUser TrustedPeople
inherits machine trust; the original guard did not distinguish that inherited
leaf from a per-user import. The correction binds `PerUser`/`MachineParent` to
protected runtime/package metadata, journal identity and actor ownership. Machine
mode opens the effective user store read-only, verifies exact public bytes and
native SID before registration, and never imports or removes user trust.
Unconditional package collisions still fail before publication. Cleanup checks
owned receipts before package removal and requires fresh effective absence after
parent retirement, including when the package remains registered.

The full launcher regressions use real certificates/files/receipts and substitute
only native trust/package boundaries. They reproduce the inherited collision
before correction and cover wrong/missing trust, mismatched mode/ownership,
package/per-user collisions and cleanup before/after parent retirement. Independent
review caught a strict test-host configuration mismatch before retry; the existing
reader now accepts a validated defaulted mode while rejecting null/unknown modes
and unknown JSON fields. Its actual owned-file reader regressions failed before
correction. The combined affected [suite](machine-parent-final.trx) passes 151/151
without skips or new build warnings; its parent exits zero and joins.

The corrected [native run](native/bf4645f9aaba450abb0b822dea68a6c7-machine-trust/result.json)
exits zero with no failure. Actual packaged creator A passes all five checks,
including history and independently verified caller-authorised recovery.
Packaged B passes all four denial checks: package identity cannot grant an
ungranted Windows user repository identity or history. A/B independently reopen
the same inherited public leaf, without private material. Parent retirement is
recorded for thumbprint `B756220F7B9D643EFD96867569C1838064B775FA`, CER SHA-256
`CA8CEB7D7879DB756931AB2F51073B474E1DB74C74C302E76A55644395B0F8BC`.
Post-parent effective absence is supported by enforced cleanup checks and the
completed journal; no separate post-cleanup A/B owner files were retained.

The same run also passes creator A's 143 workflow checks, SYSTEM's 166 checks and
36 setup regressions, with ungranted B denied. Its [teardown](native/bf4645f9aaba450abb0b822dea68a6c7-machine-trust/cleanup.json)
records joined jobs, root removal and unchanged normal installation; all 59
journal resources are Removed. The refreshed existing census verifies twelve
private roots and 208 exact captured identities absent, imported keys retired,
and no package/profile/account/group/task remaining. Normal trust, configuration,
authentication, actual PostgreSQL postmaster and unrelated primary edits match.
Historical census/baseline records are preserved: services restarted outside
these runs on 6 October, so the current census uses the freshly checked lifetimes
and separately checks each retained before/after pair.

Independent Astra review accepts this bounded S01 packaged proof, correction and
cleanup with no remaining blocker. Existing elevated/user/group/reopen evidence
remains applicable. D01 actual desktop observations, final G01 consolidation and
separately authorised normal rollout/live validation remain open. The frozen
normal product candidate is unchanged; this is not NEXT-002 completion.

## Approved integrity refresh

The owned Windows/PostgreSQL fixture `9502277264a74301b76818204cc7db85` passed
all forty integrity checks without skips. Its [TRX](integrity-refresh/9502277264a74301b76818204cc7db85/results/PostgreSql.trx)
and [teardown](integrity-refresh/9502277264a74301b76818204cc7db85/cleanup.json)
record root removal, joined jobs and unchanged normal installation. This is
integrity refresh evidence, not packaged identity or final G01 acceptance.
