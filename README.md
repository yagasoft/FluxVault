# FluxVault

FluxVault is a Windows 11 backup utility for frequent, versioned protection of
large local files that may be open while users work. It combines a WPF dashboard,
a tray app, and a per-machine Windows service.

## MVP direction

- Watched local folders with recursive include/exclude policies.
- USN journal catch-up plus directory notifications for low-latency detection.
- Writer-aware VSS requester reads for locked-file capture and app-consistency
  where VSS writer metadata covers the source path.
- FastCDC-style chunking with BLAKE3 chunk fingerprints.
- Configurable capture cadence with bounded hot-file snapshots.
- Adaptive compression policy with zstd by default, plus lz4, Brotli, LZMA,
  and off modes for explicit profiles.
- Local immutable repository plus atomic writes into enabled mirror nodes such
  as cloud sync folders or removable storage, using full-copy,
  capacity-balanced, or redundant placement.
- Restore to alternate paths with explicit overwrite confirmation before the
  service is asked to write.
- File browser selection UX with drives, folders, files, and a pending change
  summary before Save.
- Local diagnostics only; no hidden telemetry.

## Repository status

FluxVault now has a developer-usable MVP loop:

- WPF dashboard connected to the service over local named-pipe IPC.
- Per-machine Worker Service host with persisted configuration under
  `C:\ProgramData\FluxVault\config.json`.
- The service IPC pipe grants explicit local desktop and packaged-app access,
  so sparse-packaged FluxVault can connect without weakening service-control or
  ProgramData permissions.
- Dashboard service availability warning and Start/Stop service control. If
  Windows denies service control, the app reports that elevated permissions are
  required instead of crashing.
- Windows Event Log provider wiring for service information, warning, and fatal
  failure events, with SCM recovery configured by the developer script and the
  WiX production installer.
- Watched-folder backup using file-system notifications, USN journal catch-up,
  and periodic reconciliation fallback.
- Normal readable-file capture with writer-aware VSS requester fallback for
  locked files when the service has sufficient Windows privileges.
- Capture status reports VSS consistency evidence: normal reads are
  best-effort, covered writer-aware VSS captures are app-consistent, and VSS
  captures without matching writer coverage are crash-consistent.
- Version list, inspect, restore, diagnostics export, local repository, and a
  `MirrorSet` for optional mirrors.
- Append-only version lineage in manifests. Same-path captures record parent
  versions, restores leave a pending lineage hint for the next capture, and
  identical copied files become visible inherited versions without re-uploading
  chunks.
- Repository health dashboard with manual and scheduled scrub/rehearsal. Scrub
  validates referenced manifests and chunks, repairs only from a healthy
  primary/mirror counterpart, and restore rehearsal verifies recent versions in
  FluxVault-owned temporary output.
- Conservative automatic retention and scheduled repository maintenance, with a
  WPF Options dialog for previewing retention, running retention, editing
  maintenance cadence, and controlling mirror repair/rehearsal defaults.
- Dedicated Mirrors workspace for editing mirror node label, path, enabled
  state, placement profile, minimum mirror copies, capacity budget, and
  priority. Existing legacy `mirrorPath` configuration is migrated into one
  enabled full-copy mirror node, and mirror write failures are reported as
  warnings without failing the primary backup.
- Mirror repair preview and repair actions report per-node mirror health.
  Diagnostics can preview or repair all enabled mirrors; the Mirrors workspace
  can preview or repair the selected node from the healthy primary repository.
- Mirror placement preview and apply actions report and execute safe mirror
  copy/delete work for existing mirror artefacts. The Mirrors workspace can
  preview and run selected-node drain, copying required artefacts elsewhere
  before removing drained chunk/metadata copies and disabling the node.
- Conservative workload policy presets for general, Office, CAD/BIM,
  Adobe/video, developer, and generic-large-file selections. Options controls
  the default preset for new selections, and the File browser can change the
  preset for each protected folder or file before saving.
- A FluxVault app icon, left-navigation dashboard, activity view, tray activity
  pane that stays inside the active monitor working area, and blocked-file
  reporting.
- The selected operational cockpit dashboard direction, with a stable command
  bar, left navigation, first-tab startup, and footer health tiles.
- Yagasoft branding in the About window, including the project GitHub link,
  Yagasoft website link, application description, and brief roadmap milestones.
- A three-pane File browser tab for selecting recursive folders, immediate
  folder files, or individual files before saving service configuration.
- Compact checkbox-style File browser indicators, equal-width scrollable panes,
  first-render column sizing, mouse-wheel tree scrolling, and preserved unsaved
  selection changes until Save or Discard.
- Scoped include/exclude regex rules on selected File browser folders/files.
  Recursive folder rules apply recursively, immediate folder rules apply only to
  direct files, and child rules are additive with inherited parent rules.
- Helpful wrapping tooltips across Options so each retention, maintenance,
  cadence, and compression setting explains its operational impact without
  clipping.
- Safer restore workflow: the dashboard picks a destination before IPC,
  requires confirmation before overwriting an existing file, keeps the selected
  version on cancellation/failure, and reports IPC, locked-file, and
  access-denied failures without closing the app.
- Options-managed per-user Explorer file/folder actions. Registration writes
  HKCU full-menu commands for `Add to FluxVault`, `Show FluxVault versions`,
  and `Remove from FluxVault` in that order. `Show FluxVault versions` and the
  legacy `--restore-path` route forward a version hint to the running dashboard
  when one exists; they never auto-restore or overwrite. Add/Remove requests
  save configuration immediately. Windows 11 compact-menu support now has a
  sparse package manifest and native `IExplorerCommand` handler scaffold; the
  same Options buttons continue to report whether package identity and the
  handler are active. Unpackaged developer runs and the unsigned consumer
  installer still register the full menu and report compact registration as
  unavailable.

The detailed implementation status is tracked in
[`docs/roadmap-tracker.md`](docs/roadmap-tracker.md). Every future roadmap or
implementation PR should update that tracker with the affected item ids,
status, and evidence.

VSS captures are app-consistent only when writer coordination completes and
writer metadata covers the captured source path. Successful VSS snapshots
without matching writer coverage are reported as crash-consistent with an
explicit detail message in service status, dashboard activity, diagnostics, and
newly written IPC responses.

FluxVault opens source files read-only with `FileShare.ReadWrite |
FileShare.Delete`; VSS captures read from the shadow path. Repository writes
should be kept outside watched source folders unless explicitly configured.

## Build

```powershell
dotnet restore
dotnet build
dotnet test
```

## Developer app and service quickstart

```powershell
.\eng\package.ps1

# Elevated PowerShell
.\artifacts\publish\install-service.ps1

# Normal desktop session
.\artifacts\publish\app\FluxVault.App.exe
```

The base developer package needs the .NET SDK. Building the Windows 11 compact
Explorer-menu DLL additionally requires Visual Studio 2022 Build Tools with
**Desktop development with C++**, MSVC v143 x64/x86 tools, and the Windows SDK.
When those native C++ prerequisites are missing, `eng\package.ps1` skips the
compact-menu DLL build, still publishes the app/service/CLI and sparse package
files, and records the missing prerequisite in `compact-menu-status.txt`.

The install script registers `FluxVaultService`, creates the `FluxVaultService`
Windows Event Log source under the Application log, configures delayed automatic
start, and configures service recovery restart actions. The dashboard shows a
warning if the service is stopped or unavailable and provides Start/Stop service
controls.

Consumer packaging uses WiX. `eng\release-package.ps1` first publishes the
developer artefacts, then builds an unsigned Burn bootstrapper named
`Yagasoft-FluxVault-v1.0.1-win-x64-Setup.exe` under `artifacts\release`
by default, plus matching checksum, release-notes, and status files. Pass
`-ReleaseVersion vX.Y.Z` to build a different release version. The MSI inside that
bootstrapper installs the app, service, and CLI per-machine, preserves
`C:\ProgramData\FluxVault` across major upgrades and uninstall, and configures
service delayed auto-start, SCM recovery, and the Event Log source. This
consumer profile does not require signing secrets and does not install the
sparse MSIX/package-identity leg, so Windows may show Unknown publisher and
SmartScreen warnings.

To smoke-validate the unsigned consumer installer on a clean elevated Windows
session, run:

```powershell
.\eng\smoke-release-install.ps1 -ReleaseVersion v1.0.1 -UninstallAfter
```

The smoke harness verifies the setup checksum, refuses to run over an existing
`FluxVaultService` or existing `C:\ProgramData\FluxVault` by default, installs
the bundle, checks service state, full Program Files payloads, ProgramData
creation, Event Log source registration, and an installed CLI backup/list/restore
round trip, then uninstalls and confirms ProgramData is preserved. Because it
mutates Program Files, Windows service state, HKLM Event Log registration, and
`C:\ProgramData\FluxVault`, run it only on a disposable or otherwise clean
validation machine/session. Use `-AllowExistingProgramData` only when you
intentionally want non-clean evidence against preserved existing machine state.

The writer-aware VSS requester has deterministic CI coverage for requester call
order, writer metadata coverage, writer status failures, snapshot cleanup, and
the rule that failed writer/requester captures do not commit a version. This
proves FluxVault's VSS behaviour without requiring elevated live app-writer
tests in CI; it does not certify every third-party VSS writer workload.

In the dashboard, choose a repository folder, then use **Mirrors** if you want
one or more mirror nodes. The Mirrors workspace can keep all nodes as full
copies, balance new chunk/metadata writes by node capacity and priority, or
write a redundant minimum number of mirror copies. Use **File browser** to
select protected folders or files, review the pending changes pane, save the
configuration, run a backup, then restore a selected version to an alternate
path. Restore asks for a destination and, when that file already exists,
requires an explicit overwrite confirmation before the service restore IPC call
is sent.

Use the **About** button for the FluxVault version, Yagasoft copyright,
[`https://github.com/yagasoft/FluxVault`](https://github.com/yagasoft/FluxVault),
[`https://yagasoft.com/`](https://yagasoft.com/), and the short roadmap summary.
The FluxVault app icon remains the executable, taskbar, and tray icon.

The File browser replaces the older watched-folder browse/add flow with a
three-pane selector. Folder selection cycles between recursive selected,
immediate files only, and not selected. Individual file selections are compiled
into service watched-folder rules, and unsaved changes are shown before Save
applies them to the service. The tree pane uses its own scrollbars so mouse-wheel
scrolling works while the cursor is over the tree, and the file/pending-change
columns auto-size on first render before falling back to horizontal scrolling for
long paths.

Use the **File browser** scoped regex strip to add include/exclude regex rules
to the selected protected folder or file. A folder with local regex rules shows
an `R` indicator in the tree. Folder tree context menus can show the folder in
File Explorer; file-grid context menus can open a file in its default app or
show it selected in File Explorer. The same strip exposes the selected
folder/file workload preset, which resolves cadence, compression, compression
thresholds, and common generated/cache exclusions.

Open **Options** > **Advanced** to register or unregister FluxVault Explorer
context-menu actions for the current Windows user. Explorer actions are
app-managed HKCU entries, not install/uninstall script switches. The full menu
gets Add, Show FluxVault versions, and Remove actions. Developer packaging also
publishes a sparse package manifest and `FluxVault.ExplorerCommand.dll`
scaffold for the Windows 11 compact menu. The compact menu is package-owned:
Options reports it as active only when FluxVault is running with package
identity and the shell-extension artefact is present.

The unsigned consumer WiX Burn bundle wraps only the MSI. It intentionally does
not install `FluxVault.SparsePackage.msix` or the native compact-menu handler,
so the Windows 11 compact menu is unavailable in that package profile. The
classic full Explorer menu under **Show more options** remains available from
Options.

UI/UX redesign directions are tracked in
[`docs/ui-concepts/mvp-021-ui-concepts.md`](docs/ui-concepts/mvp-021-ui-concepts.md).
Concept A, the operational cockpit, is the selected runtime direction. The
dashboard therefore keeps primary commands stable in the header, opens on the
first workspace tab, keeps navigation on the left, and keeps health tiles in the
footer.

Restore lineage keeps FluxVault history append-only. Restoring an older version
does not create a manifest immediately; it writes a repository-local pending
lineage hint, and the next capture of that destination records the restored
version and fork origin while newer versions stay available. If a protected
file is copied from existing FluxVault content, the copy appears as a visible
inherited version that reuses existing chunks instead of uploading duplicate
content. This is Git-like lineage on FluxVault manifests and chunks, not a
normal `.git` repository.

The **Diagnostics** workspace is now the repository health dashboard. It shows
overall repository health, repository integrity, mirror state, USN state,
blocked-file status, last scrub, and last restore rehearsal. **Run scrub**
checks only artefacts still referenced by remaining manifests, repairs primary
chunks/manifests from a healthy mirror, repairs mirror artefacts from a healthy
primary copy, and reports unresolved corruption without reading live source
files. **Run restore rehearsal** restores the newest configured versions to a
FluxVault temporary state folder, verifies logical length, records pass/fail
results, then removes the temporary output without creating repository versions
or restore-lineage hints.

Mirror writes are secondary to the primary repository commit. FluxVault commits
chunks, metadata, and manifests to the primary repository first, then mirrors
new artefacts according to the active placement profile. `FullCopy` writes
chunks and metadata to every enabled node. `CapacityBalanced` uses deterministic
weighted rendezvous selection so new chunk/metadata pairs go only to the
selected capacity/priority target. `Redundant` writes the configured minimum
number of mirror copies, capped by enabled node availability, and reports when
the request is under-satisfied. Manifests continue to be mirrored to enabled
nodes so every mirror has version metadata. If a mirror folder is offline or
unavailable, the backup remains successful and the node is reported through
mirror warnings in status/health.

**Preview mirror placement** is a read-only check for existing repository
artefacts. It reports missing required chunk/metadata copies, extra non-target
copies, offline nodes, estimated movement, and per-node placement summaries
without writing files or leaving temporary artefacts. **Apply mirror placement**
copies missing required artefacts from the healthy primary repository first,
then deletes extra non-target mirror chunk/metadata artefacts only when required
target copies are satisfied. Unavailable mirrors or unhealthy primary artefacts
remain warnings for repair rather than failed backup or destructive cleanup.

**Preview mirror drain** is a selected-node safety check in the Mirrors
workspace. **Drain selected** copies required chunk/metadata artefacts from the
healthy primary repository to remaining target mirrors first, then deletes the
selected mirror's chunk/metadata copies only when those required copies are
satisfied. A successful drain disables the selected mirror node in
configuration. Ordinary **Remove from configuration** remains a separate
unsaved configuration edit and does not move or clean existing mirror data.

Mirror repair is an explicit manual operation, not a scheduled placement or
rebalance policy. **Preview mirror repair** reports repairable primary and
mirror drift without writing artefacts. **Run mirror repair** repairs primary
artefacts from any healthy enabled mirror and repairs enabled mirrors from the
healthy primary. The Mirrors workspace also provides selected-node preview and
repair; selected-node repair only repairs that mirror from the primary and does
not repair the primary.

FluxVault now creates a stable local device identity and local trusted-device
record as the first R3 sync foundation. Repository-owned sync metadata can also
store immutable per-peer operation records, compact peer heads, local
per-peer cursors, and mapping confirmation records; Diagnostics shows the local
device id, trusted-device count, peer-head count, cursor count, and pending
mapping count. A newly selected remote folder or file remains blocked from
hydration until this PC confirms the same source path or records a per-PC path
override. Remote-applied versions can now carry source version, source
operation, origin device, and applied-version metadata in the manifest and sync
status so later chunk hydration can avoid republishing FluxVault's own remote
write as a fresh local change. The local sync hydrator can copy only missing
chunk and metadata artefacts from a peer repository, apply a confirmed remote
version when the target is safe, leave locked targets blocked, and record
non-destructive conflicts with keep-local, keep-remote, restore-as-copy, and
mark-resolved actions.

FluxVault now has the first R4 WinFsp performance-workspace foundation. The
configuration model and Diagnostics status can describe a prepared WinFsp
workspace path, cache budget, and mount name, and the repository includes
static `eng/winfsp` setup manifests/scripts for review. Those scripts are not
run by tests or normal app flows; WinFsp driver installation and OS-level
registration still require explicit future approval.

FluxVault also has the first R5 Cloud Files API / ProjFS shell-integration
foundation. The typed configuration and Diagnostics status can describe a
prepared sync-root path, display name, hydration policy, and placeholder state
path, and the repository includes static `eng/shell-integration`
manifests/scripts for review. Those scripts do not register providers, create
placeholders, register sync roots, or mutate machine state during tests or
normal app flows.

FluxVault now has the first R6 direct-cloud adapter foundation for Azure Blob,
S3-compatible storage, Dropbox, Google Drive, and OneDrive. The configuration
stores provider metadata and credential references only, service status reports
SDK/provider readiness with live validation deferred, and fake-client tests
exercise the provider-neutral object adapter contract. No credentials are used
and no live cloud account calls run in tests or normal app flows.

FluxVault now has safe foundations for later client-side encryption and
enterprise/fleet policy. Configuration can store encryption key references
without inline key material, security/fleet status appears in Diagnostics, and
pure contract tests cover encryption planning and local fleet policy
evaluation. Repository artefacts remain plain until a future encryption
execution slice.

Open **Options** to review retention, scheduled maintenance, and the default
workload preset assigned to newly protected folders/files. The MVP
retention defaults keep every version for 24 hours, keep one version per hour
for 30 days, keep one version per day for 180 days, and always keep at least
the latest 20 versions per source file. Retention is enabled by default and
runs after successful service backups; the dialog can preview reclaimable
repository space and run retention immediately. Maintenance is enabled by
default, runs every 24 hours, repairs from a healthy mirror when possible, and
rehearses the newest three versions unless changed in Options.

The **Advanced** options page controls capture cadence and compression. It also
edits the skip-extension list used to avoid compressing formats such as archives
or media that are already compressed. Workload presets use this skip list as the
final no-compression guardrail. The default watcher poll is 5 seconds,
reconciliation is 10 minutes, and hot files are forced after 30 seconds, 2
minutes, or 10 minutes for Fast, Balanced, and Quiet profiles. The Capture tile
shows live watcher/debounce work such as
pending files; the USN tile shows the latest durable catch-up check and its
last checked time. Mature watcher changes trigger a USN catch-up before
FluxVault falls back to the watcher path, so the two tiles stay understandable
without duplicating captures. The dashboard header keeps service status short
so command buttons remain usable; durable-change details live in the USN
health tile, tooltip, and diagnostics export. When durable USN catch-up is
unavailable, the tile shows a concise reason such as unable to open a volume,
unable to query the change journal, unsupported volume, journal wrap, or
file-id path resolution failure.

PRs wait for `build-test` only; CodeQL runs on `main` push, manual dispatch, or
the scheduled scan, and is not a PR gate unless explicitly requested.

To remove the unsigned developer service:

```powershell
.\artifacts\publish\uninstall-service.ps1
```

The uninstall script preserves `C:\ProgramData\FluxVault` and the Event Log
source by default. Use `-RemoveProgramData` and `-RemoveEventLogSource` only
when you intentionally want to remove local machine state and the event source.
The production MSI similarly preserves ProgramData by default through permanent
state components.

## Local backup harness

The MVP command-line harness backs up normal readable files into a local
FluxVault repository. It does not use VSS or USN.

```powershell
dotnet run --project .\src\FluxVault.Cli -- backup --source .\README.md --repository .\.tmp\vault --mirror .\.tmp\cloud
dotnet run --project .\src\FluxVault.Cli -- list --repository .\.tmp\vault
dotnet run --project .\src\FluxVault.Cli -- inspect --repository .\.tmp\vault --version <version-id>
dotnet run --project .\src\FluxVault.Cli -- restore --repository .\.tmp\vault --version <version-id> --output .\.tmp\README.restored.md
```

Exit codes are `0` for success, `1` for invalid arguments, `2` for not found,
and `3` for backup/restore failures.
