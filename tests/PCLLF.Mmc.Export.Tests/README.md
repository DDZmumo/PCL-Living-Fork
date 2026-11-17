# Managed MMC portable export checks

Run with the explicit .NET 10 SDK:

```powershell
$env:MSBUILDDISABLENODEREUSE = '1'
dotnet build tests/PCLLF.Mmc.Export.Tests/PCLLF.Mmc.Export.Tests.csproj -m:1 -p:UseSharedCompilation=false -p:BuildInParallel=false
dotnet run --project tests/PCLLF.Mmc.Export.Tests/PCLLF.Mmc.Export.Tests.csproj --no-build
```

Twelve small aggregate groups reference the genuine modern/PCLCS project. `--case <name fragment>` selects one group. They use owned GUID roots, self-authored valid Java8 class/JAR fixtures, controlled metadata/artifact callbacks, real managed import and source/transaction/export ZIP/production read-back code. No internet, GTNH, Minecraft or JVM is executed. Roots and failed partials remain tagged/retained for audit.

Coverage: Java8/21 metadata import/export/reimport and fresh identity, current files/private/game libraries, adopted remote snapshots pinned and frozen locally, published user overrides, empty local JVM scope, player data/options/dual-root/unknown data, output collisions/hardlink originals/cross-source-volume publication, stale source/preferences/CAS, pending/uncertain sessions, held ZIP writer cancellation/drain, raw tweaks + extra/fullscreen exact argv with shared class requirements, literal auth rejection/version query URLs, and late observer cancellation after the atomic commit boundary.

[Backend contract and portable field scope](../../docs/mmc-managed-export.md). Main UI/CI wiring belongs to the integration root. Native/source extraction policy is unchanged.

Astra review regressions additionally cover one shared account/binding flag policy over both extra and final compiled argv (bare and equals forms), declared macro preservation, generic custom arguments, default server equals-form refusal, and package auto-join false/stale address versus true/fallback versus explicitly empty/nonempty instance overrides. Negative pending markers are removed only by the owned fixture after asserting export preserved them, permitting queued preferences to drain during teardown. A case filter matching no group fails instead of silently passing.
