# Saving protection changes

File browser selections are pending edits until **Save selections** succeeds. Saving them preserves the loaded profile's other settings, including disabled protection, PostgreSQL, maintenance, synchronisation and diagnostics settings. A saved selection is not a completed backup.

**Run backup** first saves the current draft. It requests backup once, for that same profile, only after the service acknowledges success. A rejected, cancelled or unconfirmed save keeps the edits and explains why this action's backup did not start. Scheduled service activity is separate.

The save result stays beside the pending changes through refresh and Explorer **Show versions**. Retry saves the retained draft. **Discard changes** reloads saved settings; if reload fails or newer edits arrive, the draft stays visible and the app explains that discard did not complete.

A lost acknowledgement may mean the service already saved the settings. Refresh checks service status before retry; the app does not claim rollback. If the request included destructive history removal, or the service saved settings but reported purge failure, review the saved settings and explicitly reload them using **Discard changes** before another save. This prevents blind repetition of uncertain destructive work.

Edits made during a save remain pending against the newly acknowledged baseline. They must be saved before backup. Profile changes are blocked while saving, editing Options or holding unsaved changes. Both Options entry points require loaded settings and a saved or discarded draft. Options remains open until an in-flight save finishes. Protection saving stays unavailable after closing Options until saved settings reload successfully. If newer selection edits arrive during that reload, they stay pending; review them before explicitly discarding to reload. Explorer protection requests received while Options is open or reconciliation is incomplete explain that they must be retried afterwards.

This repair covers the current WPF workflow. Cross-client revision conflict handling, pause/resume and separate history deletion remain in the agreed roadmap. See the [verification record](verification/2026-10-03-protection-save/README.md).
