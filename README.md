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
- Local immutable repository plus atomic writes into a user-selected cloud sync folder.
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
- Dashboard service availability warning and Start/Stop service control. If
  Windows denies service control, the app reports that elevated permissions are
  required instead of crashing.
- Windows Event Log provider wiring for service information, warning, and fatal
  failure events, with SCM recovery configured by the developer install script.
- Watched-folder backup using file-system notifications, USN journal catch-up,
  and periodic reconciliation fallback.
- Normal readable-file capture with writer-aware VSS requester fallback for
  locked files when the service has sufficient Windows privileges.
- Capture status reports VSS consistency evidence: normal reads are
  best-effort, covered writer-aware VSS captures are app-consistent, and VSS
  captures without matching writer coverage are crash-consistent.
- Version list, inspect, restore, diagnostics export, local repository, and
  optional cloud-folder mirror.
- Conservative automatic retention, with a WPF Options dialog for previewing
  and applying retention settings.
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
- Helpful wrapping tooltips across Options so each retention, cadence, and
  compression setting explains its operational impact without clipping.
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
  handler are active. Unpackaged developer runs still register the full menu and
  report compact registration as unavailable.

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

The install script registers `FluxVaultService`, creates the `FluxVaultService`
Windows Event Log source under the Application log, configures delayed automatic
start, and configures service recovery restart actions. The dashboard shows a
warning if the service is stopped or unavailable and provides Start/Stop service
controls.

Manual VSS smoke check for this slice: from an elevated session with the service
installed, lock a protected file with another process, run **Run backup now**,
and inspect the dashboard Activity/Capture detail. A writer-covered file should
show `AppConsistent`; a file without matching writer metadata should show
`CrashConsistent` and a no-writer-coverage explanation. This live elevated
check is not a CI gate.

In the dashboard, choose a repository folder and optionally choose a cloud-sync
mirror folder. Use **File browser** to select protected folders or files, review
the pending changes pane, save the configuration, run a backup, then restore a
selected version to an alternate path. Restore asks for a destination and, when
that file already exists, requires an explicit overwrite confirmation before the
service restore IPC call is sent.

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
show it selected in File Explorer.

Open **Options** > **Advanced** to register or unregister FluxVault Explorer
context-menu actions for the current Windows user. Explorer actions are
app-managed HKCU entries, not install/uninstall script switches. The full menu
gets Add, Show FluxVault versions, and Remove actions. Developer packaging also
publishes a sparse package manifest and `FluxVault.ExplorerCommand.dll`
scaffold for the Windows 11 compact menu. The compact menu is package-owned:
Options reports it as active only when FluxVault is running with package
identity and the shell-extension artefact is present.

UI/UX redesign directions are tracked in
[`docs/ui-concepts/mvp-021-ui-concepts.md`](docs/ui-concepts/mvp-021-ui-concepts.md).
Concept A, the operational cockpit, is the selected runtime direction. The
dashboard therefore keeps primary commands stable in the header, opens on the
first workspace tab, keeps navigation on the left, and keeps health tiles in the
footer.

Future restore lineage hardening will keep FluxVault history append-only.
Restoring an older version will create a new version event that records the
restored version as the fork origin, while newer versions stay available. This
is Git-like lineage on FluxVault manifests and chunks, not a normal `.git`
repository.

Future multi-PC sync will wait for each peer to confirm the same source path or
choose a per-PC path override before creating, hydrating, or patching a newly
selected folder or file on that peer. Sync will also record source version,
operation, origin device, and applied-version metadata so a remote hydration
write is not republished forever as a new local change.

Open **Options** to review retention. The MVP defaults keep every version for
24 hours, keep one version per hour for 30 days, keep one version per day for
180 days, and always keep at least the latest 20 versions per source file.
Retention is enabled by default and runs after successful service backups; the
dialog can preview reclaimable repository space and run retention immediately.

The **Advanced** options page controls capture cadence and compression. The
default watcher poll is 5 seconds, reconciliation is 10 minutes, and hot files
are forced after 30 seconds, 2 minutes, or 10 minutes for Fast, Balanced, and
Quiet profiles. The Capture tile shows live watcher/debounce work such as
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
