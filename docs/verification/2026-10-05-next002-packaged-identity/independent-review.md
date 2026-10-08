# Independent packaged identity preparation review

Reviewer: existing independent GPT-6 Astra security reviewer, high effort.
Scope: complete prepared runner/TestHost/journal/SDK/package-helper changes on
`1c76d9b`, focused regressions and artifact-only construction evidence.

Required corrections were completed:

- Per-user package cleanup precedes a single global/staged absence check.
  Actual cleanup-function regressions reproduce surviving B registration and
  reopen the ownership journal after both actors are processed.
- Profile retirement checks CIM record, exact directory and ProfileList key
  absence. A leftover-directory regression preserves `Created` state.
- The writable installed SDK is copied without executing it. Protected copied
  EXE/DLL bytes require valid Authenticode, expected Microsoft signer and tool
  identity. Fixed SxS manifests name authenticated local DLLs only.
- Removed a proposed SYSTEM diagnostic-file copy because user-controlled
  ancestors/hard links could create privileged file reading. Ordinary actors
  return the bounded public diagnostics instead.

Initial review outcome: technically ready to present the bounded A/B registration
rehearsal for resource approval. Four successful SDK invocations, matching
MSIX/CER hashes, PFX-file removal and joined job/root cleanup were inspected.
Subsequent native key enumeration showed that this evidence did not establish
removal of SignTool's imported Software KSP keys. Native registration has not
run; technical readiness is pending the correction below.

This is technical assessment, not execution approval. Actual packaged Windows
tokens, creator recovery/ungranted-user denial, registration/trust/profile
teardown and normal-installation preservation still require the authorised
native run. This does not approve normal installation rollout or satisfy full G01.

## Persistent signing-key correction

The reviewer approved exact native-object retirement of the two prior imports
after provider, current-user namespace, unique name and retained-certificate
RSA fingerprint checks. Normal executable trust-path controls and ACLs were
not weakened. Both keys are absent through native and backing-file checks.

The concrete future recovery implementation was reviewed independently:
private baseline before signing, public fingerprint, exact identities flushed
before same-object deletion, absence verification, joined tools and preservation
of the private root on failure. Public evidence excludes unrelated key names.
The review required explicit `NCRYPT_SILENT_FLAG` on enumeration and correction
of inaccurate ephemeral-only claims. Both source corrections are applied.

Final assessment: technically ready for the bounded A/B package rehearsal,
subject to separate resource approval; no remaining blocking correction.
The reviewer inspected actual final signing (four successful SDK invocations,
two retired imports), 56/56 focused checks, 40/40 integrity checks, the
zero-warning/error Release build and the census. All six fixture signing keys,
seven roots and 59 captured processes are absent; 38 database intents are
retired. Normal installation and trust remain unchanged. Actual native package
registration, packaged-token behaviour and teardown are still unverified.

## Approved per-user attempt and bounded machine-trust preparation

The same independent Astra reviewer assessed the 5 October actual attempts,
complete fixture correction/fallback diff and public evidence. The package logo
correction preserves cloned runtime Assets. The ordinary launcher retains fixed
canonical UUID/root, native SID and payload hashes while the privileged parent
retains ancestor/ACL checks. The third attempt reaches registration and fails
with `0x800B0109`; all three cleanup guards pass and the captured fixture
resources/signing imports are absent. This is not S01 acceptance.

Conflicting Microsoft guidance and that native failure support preparation of
a separate machine-trust resource request without another unchanged per-user
rerun. They do not prove machine trust is the only solution. The prepared
CurrentUser diagnostic independently reopens the store and checks exact public
bytes/hash/thumbprint, native SID and `HasPrivateKey=false` before registration.

The bounded default-off fallback uses only one public code-signing leaf in
LocalMachine TrustedPeople. Exact baseline absence, durable intent and
certificate identity precede publication. Signing PFX and owned native keys
must be retired with matching owner/provider/RSA fingerprint and fresh silent
native absence. Existing cleanup removes the exact owned leaf even if key
recovery reports failure, then verifies absence. Changed identity, collision
or reappearance after retirement is preserved. Lost-ack recovery reopens the
actual protected record; unrelated trust and policies are preserved.

The reviewer required one correction: retain the original package trust/policy
snapshot requirement from reopened `before.json` when interruption precedes
PackageUser intents. The actual snapshot/reopened-journal regression failed
before correction and passes afterwards, including subsequent trust-change
detection. Final assessment: approved technical preparation for the separate
machine-trust resource decision, with no remaining blocking finding in this
bounded diff. The reviewer inspected 10/10 focused, 115/115 combined and the
added 1/1 interruption correction. The final combined run was pending at that
inspection; its observed result is recorded separately in the preparation README.
The subsequent final combined run passes 116/116, without skips or new build
warnings, and its parent exits zero and joins. No source changed after the
reviewed interruption correction.

This assessment grants no machine-store mutation, packaged S01 acceptance,
normal installation rollout or full G01 completion. The original user approval
covered only A/B per-user trust/package/profiles and guided disposable D01.

## 7 October consolidated acceptance

The user separately approved the fixed machine-leaf rehearsal. Independent Astra
review accepts the inherited-trust correction and current commissioning lifetime
repin. One blocker was found before native execution: the .NET fixture reader
would reject the new trust-mode field. Test-first correction adds only a validated
defaulted field to that existing strict configuration; unknown JSON fields remain
refused. The reviewer accepts that correction after checking the actual reader.

The affected suite passes 151/151 with no skips/new build warnings and joined exit
zero. The real corrected rehearsal passes packaged A5/B4, verifies exact inherited
public trust and records parent retirement. All 59 journal resources are Removed;
cleanup's three guards and the twelve-root/208-identity census pass. Review accepts
bounded S01 packaged-caller proof and cleanup, with no technical blocker.
Post-parent effective absence is proved by the enforced cleanup checks and the
completed journal; separate post-cleanup A/B owner records were not retained.

The reviewer also accepts the preserved historical normal baseline and refreshed
fixed lifetimes: read-only Prepare matches all 208 candidate/204 legacy files,
pins the current actual postmaster and reports `RolloutApproved=false`. No normal
authentication, ACL, installation or service mutation occurred. D01, final G01
consolidation and separate normal rollout/live acceptance remain open.
