# Review evidence

Evidence collected on 1 October 2026 from source revision b930ed5. Source code was unchanged during these checks. Documentation and screenshot additions followed. Machine/user-specific path prefixes in retained logs were replaced with `<checkout>` and `<scratch>`; test-result filenames were anonymised. Result values, exception types, stack frames and measured timings were preserved.

## Build and automated tests

SDK: .NET 10.0.300; runtime observed in the UI crash: .NET 10.0.12. Release build: zero warnings and errors. Full suite: 484 passed, zero failed/skipped (194 Core, 17 Windows, 96 Integration, 177 App). Logs: [build](build-release.txt), [tests](test-release.txt). The [build command summary](build-release-summary.txt) and [test command summary](test-release-summary.txt) retain process exit codes and wall times of 64.484 s and 4.156 s. The build log's MSBuild elapsed time is a distinct internal measurement.

Observed commands, with the temporary output root expressed as a variable:

```powershell
$reviewRoot = Join-Path $env:TEMP 'FluxVault-review-20261001'
dotnet build FluxVault.slnx -c Release --artifacts-path "$reviewRoot\artifacts" --no-incremental
dotnet test FluxVault.slnx -c Release --no-build --artifacts-path "$reviewRoot\artifacts" --results-directory "$reviewRoot\test-results" --logger trx
```

For that external output location, a tracked-source snapshot was placed above the binaries because source-inspection tests locate the repository by walking upwards from AppContext.BaseDirectory. Ordinary in-checkout `dotnet build ... -c Release` followed by `dotnet test ... -c Release --no-build` avoids that special layout when reproducing the baseline. Native installer, service registration, cloud provider and live PostgreSQL tests were not executed. The existing suite has no live PostgreSQL test, so it did not report one as skipped.

## Focused integrity probes

[Source snapshot](integrity-probe.cs.txt), [observed output](integrity-results.jsonl). A disposable .NET 10 console harness called the compiled repository API. All paths were beneath a new GUID-named temporary directory. It used explicit file-manifest repositories, raw chunks and synthetic text, avoiding any database/service/real repository. These are shared repository methods used by normal metadata-backed operation, but this probe does not exercise its database transaction boundaries.

The final harness execution exited 0 after printing all three defect scenarios. **Exit 0 means the probe completed, not that product integrity passed.** To repeat, create a disposable net10.0 console project referencing `src/FluxVault.Core/FluxVault.Core.csproj`, use the source snapshot as Program.cs and run in Release. A project reference supplies dependencies; the original binary-reference harness required the native Blake3 library copied to its temporary output directory after an initial harness-only loader error.

| Probe | Observed result |
| --- | --- |
| Same-length corruption | Restore returned successfully; bytes differed; rehearsal Healthy/zero failures; scrub Critical. |
| Missing payload with surviving sidecar | Recommit returned successfully; chunk absent; restore FileNotFoundException. |
| Sole good mirror drained | Drain Healthy; healthy payload deleted; subsequent scrub Critical. |

The next implementation should turn these scenarios into assertions of the intended invariants, not checks that merely reproduce today's bad output.

## UI and configuration probes

[Source snapshot](ui-probe.cs.txt), [observed output](ui-results.jsonl). A disposable net10.0-windows console with UseWPF and a project reference to `src/FluxVault.App/FluxVault.App.csproj` used fake clients and the real configuration file store under a temporary directory. It did not connect to IPC or write normal ProgramData.

The dashboard omitted MetadataStore, RepositoryMaintenancePolicy, Sync and DiagnosticsPolicy from its save payload. On store round trip, the configured database changed from `review_specific_database` to `fluxvault_metadata`, maintenance interval from 7 to 24 hours, and IsEnabled from false to true. Both old/new automatic-maintenance flags happened to be false; that part of the output is not evidence of a flag change. Backup was requested even when the fake client explicitly rejected configuration save. An Options initialisation timeout escaped to its caller.

Separately, native UI navigation reproduced the real Options crash with the service absent. [Windows .NET Runtime event text](options-crash.txt) records event 1026's unhandled TimeoutException path. Window enumeration then returned no FluxVault window, and no FluxVault.App process remained. No installed service was started/stopped to obtain this result.

## Bounded performance measurements

[Measurements](performance-measurements.jsonl), [source/restore hashes](performance-hashes.jsonl), [measurement script](cli-perf.ps1.txt). Windows 11 Enterprise 10.0.26200, Ryzen 9 5900X, approximately 80 GiB RAM, local E: NTFS storage, zstd and legacy file-manifest CLI. Each of two synthetic 256 MiB files was captured, recaptured unchanged, edited in the middle by 64 KiB, appended by 1 MiB and restored. The final 257 MiB restores matched source SHA-256.

There was one sequence per input type, with uncontrolled cache effects. Measurements include process startup, with working/private memory sampled every 10 ms. No real PostgreSQL, VSS, mirrors, professional application writes or UI were in this benchmark. There were no physical IO counters or percentile estimates. The script creates new disposable directories beneath the supplied ReviewRoot and expects the CLI at `artifacts\bin\FluxVault.Cli\release\FluxVault.Cli.exe`.

Do not infer whole-app throughput, disk durability, an application-consistent restore or 100 GB performance from these results. Their role is a bounded baseline demonstrating healthy streaming/dedup behaviour while identifying where a complete benchmark programme is missing.

## Visual evidence

The [UX report](../ux-review.md) embeds nine saved native captures in observation order. The first eight workspaces were inspected in an unavailable-service state; the tenth workflow step, opening the Options dialog, failed before a stable dialog could be audited. Screenshot notes distinguish visible failures from source-derived risks. No visual redesign mock-ups are presented as implemented UI.

## Checks not performed

Live privilege exploitation; real PostgreSQL round trips/multi-profile isolation; process/power-loss fault campaigns; clean installer/update/uninstall; real writer-aware VSS; representative Office/CAD/Adobe reopen; large-scale/performance percentiles; Narrator/full keyboard/DPI/high-contrast matrix; real cloud/sync/driver/fleet execution; current dependency vulnerability/licence audit. These are explicit roadmap gates, not silently assumed successes.

## Review and documentation checks

An independent integrity reviewer accepted the proposed rebuild boundary and security, concurrency and recovery gates without blocking corrections. The review also clarified that the first verified round trip remains disposable-only until Phase 2 passes. A separate delivery reviewer checked test counts, all ten retained performance records, hashes, memory figures, capability claims and measurement limits against the original evidence. Its size correction and request to retain command-time summaries are incorporated. These assessments approve the roadmap approach, not the current application's safety.

After updating the roadmap and tracker, the focused `ProjectDirectiveTests` passed: 6 passed, zero failed/skipped. The [retained test log](docs-validation-tests.txt) records this documentation check. The full 484-test baseline was not rerun for prose-only changes.
