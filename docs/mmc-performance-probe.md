# MMC owned-fixture performance probe

This opt-in executable references `modern/PCLCS/PCLCS.Modern.csproj` and its actual Core/Shared dependency graph. It does not source-link a substitute host. It measures an existing successful publication and never starts Main, Java, Minecraft, import, repair, or a download. Core retains MPL-2.0; this host tooling retains the repository LICENCE.

For the 2026-11-15 `e0b87974` packaged baseline, cancellation drain, synthetic private-payload import, and malformed-catalog paging records, see [MMC 启动准备性能与架构决策记录（2026-11-15）](mmc-performance-20261115.md). The 2026-11-08 baseline and withdrawn comparison sections below are kept intact as historical record.

Register the exact retained acceptance root with `scripts/New-MmcPerformanceManifest.ps1 -AcceptanceRoot <owned acceptance run> -OutputPath <checkout>/artifacts/mmc-performance/fixture.json`. The manifest freezes both ownership-record hashes, import-record hash, source-record hash, package SHA256, current instance ID, generation, primary SHA256 and paths. The original ZIP and the Java executable are not opened. The executable revalidates ownership, direct `game/versions` containment, link boundaries, primary identity, complete original-object existence, and no pending transaction/active session before measuring.

Build explicitly, outside a measurement and only when the shared heavy-test gate is available:

```powershell
$env:MSBUILDDISABLENODEREUSE='1'
dotnet build tests/PCLLF.Mmc.Performance.Probe/PCLLF.Mmc.Performance.Probe.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false
```

First run one explicit diagnostic (`--authorize-owned-fixture --manifest <file> --case java8 --output <new artifacts/report.json> --diagnostic-split`). It reports Backend.PrepareLaunch, low-frequency actual backend phase boundaries, a separate final ValidatePreparedLaunch under the real admission lease, and additional ReadPrimary / SavedSourceStore.Load (including parsing and resolution) / Core.Compile stages. Backend artifact-scope includes materialization, preserved-file validation, binding, final validation and both workspace disposals; its reported child phases overlap it and must not be added to the parent. Diagnostic stages run after prepare, are source-warm, and are not additive parts of prepare. The actual prepare still reads, compiles, materializes/verifies, binds and validates normally with `RepairIncomplete=false` and an exact expected primary digest. A failed/incomplete run never counts as a successful baseline.

Run `scripts/Measure-MmcPerformance.ps1 -Manifest <file>` for three fresh processes per case. Each process measures its first iteration as **process-cold with OS cache uncontrolled**, then a second iteration as **same-process hot**. The script runs one process at a time, does not build, evict OS caches, delete caches, reimport, or redownload the package, and emits raw reports plus medians. No claim is made about first-use SSD/OS-cache performance. Do not compare a failed old GTNH chain with this successful chain as if both perform the same work.

The backend creates and removes its own marked staging workspaces and uses its ordinary admission lock. No transaction is committed and no new generation is adopted. The complete `MmcPublishedArtifactLocks` original-object set, including derived asset objects, is opened read-only with `FileShare.Read` for the probe lifetime; missing objects fail before prepare, and pins block deletion/replacement while measuring. This adds a documented handle/pin cost to the harness outside measured prepare. Before/after reports include primary, compatibility projection, provider config and Setup.ini hashes without their contents. Preference changes are recorded separately and are not silently treated as unchanged structure. Retained source/object/runtime/local-library and owned shared-cache trees are inventoried by logical file length; their inventory signatures detect length/mtime/path changes, not same-size same-time content edits. Execution and source content hashes remain checked by the actual backend and final validation.

Windows GetProcessIoCounters values are **process logical transfer bytes**, including cached I/O/runtime activity; they are not physical disk reads/writes or unique MMC bytes. Reports include CPU, allocations, working set/private bytes and process lifetime working-set peak. Owned logical disk high-water is sampled from the retained-tree baseline plus staging once per second; short transient peaks can be missed, and this is not physical allocated/compressed disk usage. Copy/extraction progress counters and denied network/download requests are retained. Full reimport and import peak usage are outside this read-only preparation probe.

For a separate cancellation/drain observation, pass `--cancel-after-ms 1000`. Cancellation is followed through the actual awaited backend and workspace cleanup before after-state checks/reporting. It never kills another process. The cancelled run has `Success=false` and is excluded from medians even when the probe returns zero to acknowledge a correctly drained cancellation.

Design section 13 thresholds remain fixed: successful prepare median regression needs analysis when both >10% and >200ms; import median >15% needs analysis. Hot parsing may not use network or reextract the full ZIP; one trusted artifact may not be redownloaded. Any accepted correctness cost must be recorded explicitly. No measured optimization result is claimed until raw before/after runs exist.

## Frozen successful baseline, 2026-11-08

This baseline is the unoptimized algorithm at `ba5ceded916229856bd8d83c85973de4fd65bd84`, with phase-only instrumentation `55d951bb` and the probe at `178e546f`. Measurement ran on Windows build 26200, x64, .NET 10.0.14 from 14:12:00 to 14:40:23 UTC. The heavy-test gate was exclusive. There was no concurrent build/test/JVM workload started by the agents. OS page cache and unrelated existing user applications were not manipulated. Each of three fresh processes ran one process-cold iteration and then one same-process hot iteration. The first diagnostic observations are excluded from these medians. No compiler or artifact algorithm was changed between the twelve samples.

| Sample / temperature | Round 1 Prepare ms | Round 2 Prepare ms | Round 3 Prepare ms | Median Prepare ms | Median internal final validation ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Java 8 process-cold | 65791.587 | 62811.663 | 80358.995 | 65791.587 | 14221.834 |
| Java 8 same-process hot | 70249.362 | 81767.458 | 80659.998 | 80659.998 | 17734.834 |
| Java 21 process-cold | 86122.461 | 89389.155 | 91199.069 | 89389.155 | 19690.474 |
| Java 21 same-process hot | 80485.758 | 85644.802 | 92380.049 | 85644.802 | 18658.955 |

The substantial round-to-round and cold/hot variation is retained, not explained away as an optimization. Process-cold does not mean OS-cache cold, and hot is not assumed to be faster. The initial diagnostic Prepare observations (Java 8: 83459.997 ms; Java 21: 83476.645 ms) are separate successful observations and are not an old/new algorithm comparison.

All twelve samples succeeded without repair, new generation, metadata network attempts, download requests, author/source/runtime inventory changes or provider preference hash changes. Primary/projection hashes remained fixed and both owned workspaces drained before return. Per Prepare, process logical read transfers were exactly 1,105,896,508 bytes for Java 8 and 1,297,884,325 bytes for Java 21 in every round. Logical writes were 506 and 510 bytes respectively, corresponding to the two owned workspace markers; copy/native-extract progress was absent. This does not measure physical disk transfers or prove all possible missing-artifact deduplication paths.

The bound snapshots contained 1,464 input paths / 367,466,426 logical input bytes (Java 8) and 1,550 paths / 433,440,216 bytes (Java 21). Sampled retained-input/cache logical disk peaks were 446,529,373 / 512,552,683 bytes, with sampled staging peaks 506 / 510 bytes. These figures cover the explicitly listed source/original/runtime/library/shared-cache trees; **game payload, mods, worlds and the original ZIP are outside this disk scope**. They are not whole-instance or import disk peaks. Maximum process-lifetime working sets observed in the baseline were 292,306,944 bytes for Java 8 and 222,244,864 bytes for Java 21, including harness inventory work.

| Fixed fixture | Current instance ID | Generation | Primary SHA256 | Previously measured package SHA256 |
| --- | --- | ---: | --- | --- |
| Java 8 | `a18034b3-8a04-435d-bc23-49cc45ad1457` | 2 | `46a1dac3850a06002255ba1e9aa7d088b4676a809f48ee24f4a3e1aa8801ed34` | `ae94a01a5367065f71ac9b5150966182a5b55762c841749fcb3d603ab5ed78af` |
| Java 21 | `08dd1f16-fe24-4eb9-94f9-74aa00cf8312` | 2 | `a22224d16a2481320dfb2bd0730ce00c1a043ef00e5df221037a1be058d214f1` | `6d0ebe7e6be18fb74b4fc920916cc5710e142a3f885072d324da05244d398b16` |

Package identity is rechecked against the digest-bound retained import/source records, not by reopening the original ZIP. The manifest also freezes the exact owner/source/import record hashes. Java 8/21 here identify the published `MmcRuntimeEnvironment` supplied to the backend; no installed Java binary or game was executed by this performance probe.

| Frozen measurement input | SHA256 |
| --- | --- |
| Manifest | `9d228b522d80b63b915ee38b6121a2d1cc30a2b5eaeab684c3b4b5e10a91abd0` |
| Probe apphost EXE | `ebdb34f6cf48b6ae98c1f631a651d0e81701b7c3783633f447370ab93a2f20c6` |
| Probe DLL | `21fbcfe14763a151b6512837f69112aa4633f8ff27a9f3bd29208d983f2c5e81` |
| Actual PCLCS host DLL | `9618878523f7b044c1b48384bb3bb82015ca201319f1f781e5f1ab21a0d9f65c` |
| Core DLL | `eb468dae95ccb54e74c2611de2c48ba0b848d91d84ea063593012b6361368bd3` |

Local ignored evidence: `artifacts/mmc-performance/20261108-141200-0f640da3ed304219a6f4925431fb2a2a/{java8,java21}-round-{1,2,3}.json` and `summary.json`; complete baseline output preserved at `artifacts/mmc-performance/baseline-178e546f-bin`. The two initial split reports are `java8-initial-diagnostic.json` / `java21-initial-diagnostic.json` in the same performance artifacts directory. Raw reports and binaries are not committed.

The initial split shows source reading/parsing/resolution around one second and compilation below 70 ms, while artifact preparation, binding/launch-file construction and final validation dominate. In the baseline `ValidatePreparedLaunch` loop, `ResolveRelative` performs a complete `EnsureNoLinks` ancestor walk, immediately followed by a second explicit `EnsureNoLinks` for the same returned path. There is no intervening filesystem action or await. From the measured snapshot input counts and this source loop, the two direct per-file guard invocations number 2,928 / 3,100 per validation; this is a static call-count derivation, not an ETW count of attribute syscalls. A proposed narrow change removes only the second adjacent guard and retains `ResolveRelative`'s full path boundary, per-file cancellation, size and SHA256 checks, admission/source/definition checks and resource drain. Optimization timing and acceptance remain pending until the same twelve-point matrix and focused boundary regressions are complete.

This is a successful preparation baseline, not Main's full account/JavaWrapper/launch latency, a game/world acceptance run, an import comparison, an OS-cache-cold benchmark, or proof of persistent compiler/plan caching. Those unmeasured dimensions are not assigned fabricated times, disk peaks or benefits.

## Host-only comparison setup

The adjacent duplicate removal is `9dc33420`. Its focused `mmc-backend` regression group passed 9/9, including stable complete preparation, missing-input recovery, stale identity/variant/support-input refusal and cancellation/publication boundaries. Probe and regression projects both built with zero warnings/errors, single-node MSBuild, node reuse disabled and shared compilation disabled. The path-role/junction rules also passed in Root's 81-case profile run. The synthetic 2,003-file import exercised by the existing backend group is a separate single controlled regression, not a GTNH import performance baseline.

The SDK embeds the current Git revision in `AssemblyInformationalVersion`: the first build used `55d951bb41e2fbe0a2436d78b170c21962b3f387`, while the optimized build used `9dc334206ee83198761fd020435ad2fbd71c309e`. Consequently even unchanged Core/probe sources initially produced different MVIDs and binary digests. `git diff 55d951bb..9dc33420 -- PCLLF.MMCC.Core` is empty. The complete SDK output is retained separately as `artifacts/mmc-performance/optimized-9dc33420-sdk-bin`; its successful split diagnostics (`java8-optimized-diagnostic.json` / `java21-optimized-diagnostic.json`) are excluded from the formal matrix.

To compare only the host change, `artifacts/mmc-performance/optimized-9dc33420-matched-bin` was copied from the frozen baseline output and only `PCLCS.dll` / `PCLCS.pdb` were replaced with the same-ABI optimized build, after every diagnostic process exited. All fourteen runtime files were hashed; only those two files differ. The formal matrix therefore uses the exact baseline probe apphost, probe DLL, Core DLL, deps/runtimeconfig files and other dependencies. The optimized host SHA256 is `d60c511ebeb86e1bc24df01599bee9a9a32fda01f12c344ce72de63b2fea654e` (MVID `4dda2376-a72a-4c11-bac0-6d79a4688d1b`), and its PDB SHA256 is `8043c678b84b00ed2dc1a4022876dc35fcb93d20e832b86040bd332519f5af39`. Full file-by-file evidence is `matched-binary-hashes.json` in the performance artifacts directory.

Published target/original/derived path reconstruction matches the measured snapshot's 1,464 / 1,550 paths. Counting each path's complete ancestor chain gives 28,655 / 30,416 attribute-lookup positions per full per-file pass. The original adjacent two passes therefore imply 57,310 / 60,832 lookup positions per `ValidatePreparedLaunch`, versus 28,655 / 30,416 after this change. These are source-derived counts for the fixed deep worktree paths, not measured ETW syscall counts; path depth affects this cost. No ancestor is cached or omitted from the retained pass. The full static derivation is retained as `baseline-path-walk-static-counts.json`.

## Comparison deferred at the user's priority change

The user explicitly prioritized making MMC packages compatible and launchable, and deferred performance optimization to later engineering work. New optimization rounds, old-host control runs and GTNH cancellation timing were stopped. The current probe process was allowed to finish both of its existing iterations, write its results and drain naturally. Only its verified task-owned PowerShell coordinator was suspended to prevent another process from starting, then terminated after the probe had exited. The process identity/exit evidence is retained in `artifacts/mmc-performance/priority-stop.json`. No Java/game process was started, stopped or otherwise controlled.

The formal matched-binary matrix is **incomplete: 10/12 points**. Java 8 completed three cold/hot pairs; Java 21 completed two pairs, and its third pair never started. No complete `summary.json` was generated for this comparison. The existing points are preserved in `artifacts/mmc-performance/20261108-150640-98204955658549c0a64c5ab1166f8748`, including an explicitly incomplete `partial-summary-stopped-for-priority.json`.

| Candidate sample / temperature | Round 1 Prepare ms | Round 2 Prepare ms | Round 3 Prepare ms | Three-sample median ms |
| --- | ---: | ---: | ---: | ---: |
| Java 8 process-cold | 82724.254 | 77661.100 | 70437.912 | 77661.100 |
| Java 8 same-process hot | 76389.091 | 75276.883 | 81733.025 | 76389.091 |
| Java 21 process-cold | 79026.772 | 77880.115 | Not run | Not available |
| Java 21 same-process hot | 77385.973 | 83850.199 | Not run | Not available |

All ten completed points succeeded without repair/new generation, changed primary/projection/provider hashes, retained-tree changes, metadata network attempts or download requests. Each recorded successful workspace drain. The complete owner/source/import/current-definition manifest was remeasured after stopping and remained byte-identical at SHA256 `9d228b522d80b63b915ee38b6121a2d1cc30a2b5eaeab684c3b4b5e10a91abd0` (`fixture-after-priority-stop-gen2.json`). The original twelve-point successful baseline remains intact.

**No optimization acceptance or P4 pass is claimed.** The candidate's complete Java 8 cold median is higher than the frozen cold baseline and triggers the pre-registered total-prepare regression threshold. Its target-phase improvement cannot override that gate. The planned same-directory old-host control investigation was not performed following the priority change, so that regression has not been closed. Root withdrew the candidate from the integrated compatibility branch with revert `9c2dd45a`; candidate source/binaries/diagnostics remain on the isolated performance branch for later investigation. The measurement tools, phase instrumentation and frozen successful baseline are retained. This record neither turns the incomplete comparison into a successful benchmark nor blocks ongoing MMC functional compatibility work.
