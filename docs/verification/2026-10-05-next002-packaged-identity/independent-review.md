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
