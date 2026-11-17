# Real process-crash import recovery acceptance

This explicit Windows test executable references the production `modern/PCLCS/PCLCS.Modern.csproj` graph. It does not link replacement host sources or modify the shared Migration/Native test selectors. It runs no JVM, Minecraft, UI, or external HTTP requests.

Run the built apphost, with the .NET 10 runtime available:

```powershell
$env:DOTNET_ROOT = 'C:\Users\16892\.codex\tools\dotnet10'
& "$env:DOTNET_ROOT\dotnet.exe" build tests/PCLLF.Mmc.RealCrash.Tests/PCLLF.Mmc.RealCrash.Tests.csproj -c Debug -m:1 -nodeReuse:false -p:UseSharedCompilation=false -warnaserror
& ./tests/PCLLF.Mmc.RealCrash.Tests/bin/Debug/net10.0-windows/PCLLF.Mmc.RealCrash.Tests.exe --run
```

`--run --case prepared|committed|foreign` selects one scenario. The full run executes all three sequentially:

- `prepared`: the actual import child flushes its readiness receipt after production writes `prepared.record` and `Pending.record`, before any mutation or commit decision. The parent proves active Use exclusion and recovery refusal, validates the exact child identity, then kills only that process. The OS releases `producer.use`; real recovery rolls the transaction back and retains author payload. Retry rejects the old instance name and a different SHA, then succeeds with a byte-identical renamed archive and a different instance name/fresh id while preserving the parked tree.
- `committed`: the actual child stops after production flushes `CommitDecided.record`. After the same verified kill, the parent preserves the original archive at an offline evidence pathname and calls real recovery. Publication must retain the original instance id, generation 1, every exact staged transaction byte sequence, and author payload. The operation becomes completed.
- `foreign`: after a decided child is actually killed, the parent adds its own foreign world/unknown file inside the GUID fixture. Real recovery must refuse these unknown-only bytes while retaining the exact author payload. A subsequent same-length author-file change must also be refused on two attempts, preserving all foreign bytes and staged structural targets without publication.

Every scenario also hashes protected neighboring instance/world data and sibling player data before and after recovery. Read-only snapshots share `ReadWrite | Delete` with the live production writer, but require identical SHA256 and length across two reads at the stable checkpoint; the writer's exclusive Use constraints remain intact. The Use file must retain its pre-kill SHA256 when the OS releases its handle. Every kill checks the held `Process.Start` object's PID, creation time, exact apphost image, actual parent, root nonce, and scenario nonce against the child's independently flushed startup/checkpoint receipts. No process-name search, process-tree kill, simulated interruption exception, or cancellation stands in for termination.

The executable prints `RETAINED real-crash evidence: ...`. That GUID directory retains `acceptance.jsonl`, `summary.json`, every scenario receipt, original/offline archives, actual staged expected-byte copies, child stdout/stderr, protected-data snapshots, parked rollback data, the recovered instance, and preserved foreign data. It does not recursively delete test evidence on success or failure. This proves process-crash recovery at these two checkpoints, not machine power-loss durability or real-game acceptance.
