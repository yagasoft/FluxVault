# Protection failure visibility and accessible names

The actual desktop WPF window exposed two focused presentation defects: the
Protection page showed the full save-failure explanation only through a trimmed
header/tooltip, and primary composite buttons plus the repository field lacked
useful native automation names. The existing failed-save logic already retained
the edit and prevented dependent backup. This correction changes presentation,
not command, ownership, binding or receipt contracts.

Protection now displays the complete save and local-draft messages below the
mirror summary, with wrapping and polite live-region metadata. Seven primary
actions have explicit semantic names; the service action remains bound to its
current label, and the repository field is named. File-browser messages and
existing controls remain in place. No broader navigation redesign, new setting,
scheduler or multi-vault concept is added.

Three rendered-control regressions failed before the XAML correction and pass
after it. They inspect actual WPF automation peers, drive the actual view-model
save-before-backup flow, reopen the real file configuration store and measure
the full message within the visible Protection viewport at widths 1080/1440.
They do not inspect source strings. The initial test diagnostic is retained:
record equality compared collection references, so the store invariant was
corrected to compare complete serialised values before obtaining the genuine
missing-message reds. The empty native control name was already a genuine red.

All 478 App tests pass without skips. The final rendered subset passes 3/3,
and the complete final Release build has zero warnings/errors. The actual WPF
renders [at minimum width](protect-failure-1080.png) and
[at wider width](protect-failure-1440.png) show the full failure and local
retention explanations. These are logical WPF renders, not changed monitor DPI
or proof of high-contrast/Narrator behaviour.

The default-off TestHost interactive mode extends the existing disposable
single-vault draft fixture. It uses the actual window/view-model and local
stores with an explicitly rejecting client; it never connects to a service or
database. Its eight-minute dispatcher lifetime, joined close/flush and bounded
parent deadline prevent unattended windows. Both native observations prove one
save, zero backups, a retained editable repository path and the matching local
draft, then exit with code zero and remove their exact owned scratch roots.
The initial run used keyboard selection/text entry and closure; the corrected
run verified values through the native accessibility control after injected
typing did not appear. This is not a claim of complete keyboard validation.

[Before](before-accessibility.txt) and [after](after-accessibility.txt) native
trees confirm the corrected primary names, named editable field and complete
Protection messages. The second desktop capture returned stale/blank pixels
and inconsistent fragments after resizing despite updated native controls.
[That capture](capture-inconsistent.png) is retained as diagnostic evidence,
not visual acceptance. Human observation was requested to distinguish a capture
limitation from an actual rendering fault; this ambiguity remains open unless
that observation resolves it. The fixture closed at its finite lifetime and
was joined; it is no longer left running for the question.

Both native cleanup records and final census confirm the two owned WPF process
identities and roots are absent. Installed service identities and PostgreSQL
authentication/configuration hashes match before/after. Build/test workers are
joined, and unrelated processes are preserved. Self-review covers the focused
presentation change, real controls/state flow and fixture isolation. Final
whole-branch independent G01 review remains required.

This supplies bounded implemented-screen evidence. Real-service successful
save/backup/history/verified recovery retains its separate native proof; the
rejecting UI client does not substitute for it. Complete D01/S3 keyboard,
Narrator, DPI, high contrast, larger inventory responsiveness, packaged identity,
remaining source/background checks, final G01 and normal rollout remain open.
