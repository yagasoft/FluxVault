# Contributing

FluxVault is a GPL-3.0-only project. By contributing, you agree that your
contribution is provided under the same licence.

## Development rules

- Keep changes small and covered by tests.
- Prefer public contracts in `FluxVault.Abstractions` and implementation detail
  in `FluxVault.Core` or platform-specific projects.
- Do not claim app-consistent backup unless the implementation has VSS writer
  evidence for that capture.
- Do not add telemetry or cloud calls without an explicit design update.

## Local checks

```powershell
dotnet restore
dotnet build
dotnet test
```
