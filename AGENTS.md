# FluxVault Codex instructions

## Configuration and Options

- Always parameterise appropriate and reasonable configurations through typed,
  defaulted configuration models rather than hard-coded operational constants.
- Add user-meaningful operational configuration to the FluxVault Options
  dialogue (the Options dialogue) in the same change that introduces or
  materially changes the behaviour.
- Preserve compatibility for existing configuration files by keeping new fields
  optional or defaultable.
- Add tests for configuration defaults, Options load/save behaviour, and any
  runtime policy effect.
- Document the setting in the relevant project docs and update
  `docs/roadmap-tracker.md` when it changes roadmap capability status.
- Do not expose dormant, internal, unsafe, or unimplemented fields in Options
  until they have a real runtime effect and clear user semantics.
