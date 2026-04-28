# MVP-021 UI concept comparison

These static concepts were used to choose the next dashboard redesign
direction. `MVP-030` selects and applies Concept A as the runtime dashboard
direction.

## Concept A: Operational cockpit (recommended)

Best when FluxVault is mainly a background protection tool that users open to
check health, resolve warnings, or restore a version.

```text
+-----------------------------------------------------------------------+
| FluxVault                          [Refresh] [Backup now] [Restore]   |
| Service: running - last refreshed 14:20                               |
+-----------+-----------------------------------------------------------+
| Protection|  File browser                                             |
| Browser   |  +----------------+  +----------------+  +--------------+ |
| Repository|  | Drives/folders |  | Files          |  | Pending      | |
| Activity  |  | [x] Work       |  | [ ] draft.docx |  | + Work       | |
| Options   |  | [-] Projects   |  | [x] model.rvt  |  | - Temp       | |
| Diagnostics| +----------------+  +----------------+  +--------------+ |
+-----------+-----------------------------------------------------------+
| USN active | Retention kept 20 | Mirror healthy | Capture idle        |
+-----------------------------------------------------------------------+
```

Strengths: stable command bar, clear footer health, balanced work surface, and
easy fit for icons, warnings, blocked files, and future sync state.

Tradeoff: less like Explorer than concept B.

## Concept B: Explorer-first

Best when users spend most of their time curating protected files and folders.

```text
+-----------------------------------------------------------------------+
| FluxVault Explorer                                    [Save] [Discard] |
+--------------------------+--------------------------+-----------------+
| Drives and folders       | Files                    | Version preview |
| [x] D:\Work              | [x] report.docx          | Latest versions |
| [ ] D:\Media             | [ ] cache.tmp            | Restore actions |
| [-] D:\Projects          | [x] model.rvt            | Warnings        |
+--------------------------+--------------------------+-----------------+
| Activity ticker and health footer                                      |
+-----------------------------------------------------------------------+
```

Strengths: direct mental model for selection and restore browsing.

Tradeoff: background service health and activity become secondary unless the
footer is very strong.

## Concept C: Activity-centred

Best when FluxVault behaves like a sync client where the primary question is
"what is happening right now?"

```text
+-----------------------------------------------------------------------+
| FluxVault activity                       [Pause] [Backup now] [Options]|
+-----------------------+-----------------------------------------------+
| Now                   | Work queue                                     |
| Capturing model.rvt   | Pending 3, blocked 1, mirrored 18             |
| Mirror healthy        |                                               |
| USN active            | Recent versions / restore shortcuts           |
+-----------------------+-----------------------------------------------+
| File browser and repository tabs remain available below or in nav       |
+-----------------------------------------------------------------------+
```

Strengths: excellent for warnings, blocked files, sync conflicts, and tray
alignment.

Tradeoff: file selection and restore browsing require another navigation step.

## Recommendation

Use **Concept A: Operational cockpit** as the runtime redesign direction. It
keeps FluxVault quiet by default, makes health always visible, and leaves enough
main-surface space for the File browser, Repository, Activity, and future Sync
views without making any one workflow dominate the app.

## Selected runtime direction

`MVP-030` implements Concept A as the runtime shell: stable commands in the
header, left navigation, File browser as the default workspace, and health tiles
in the footer.
