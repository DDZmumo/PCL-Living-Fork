# Native LF host sources

The product starts from LF `f4aa0fae62c3ee8af24c81e61844c8fdd13c8983`, PCL foundation `5ef81e490ae46b1100d61e33d0183094c7a80ca8`, and LF migration source checkpoint `a1d2ee1ed215647ab7e38f11c9e45859fbb4fcf7`. LF remains the application base. Its original `LICENCE`, branding, WPF names, resources and user-data contracts remain authoritative.

The maintainer now licenses the independently owned PCLLF source files listed in [MPL-COVERED-FILES.txt](../../licenses/MPL-COVERED-FILES.txt) under [MPL-2.0](../../LICENSE-MPL-2.0.txt). This does not relicense inherited PCL files, mixed-source files, copied third-party implementations, or assets. Preserved historical source/behavior references below remain provenance records; current per-file license notices and the [license audit](../../docs/licensing-audit.md) identify the applicable scope and outstanding evidence.

CE stable `1a7653127d578efdb2a9388999a3fdc67d9e49d5` was reviewed for C# WPF/My/project migration experience; LF remains the product base. CE application paths use their directory `LICENCE`; Core paths use its root Apache license. The [fixed historical module map](https://github.com/DDZmumo/PCL-Living-Fork/blob/4d635f143287720ea37a97326e4752528685fece/docs/migration/MODULE_MAP.md) records per-module selection and attribution; original individual source notices remain in the implementation.

`modern/Presentation/NativeWpf` selectively adapts these actual fixed LF submodule sources at `MeloongCore` commit `c774ae4c85fd94e0eaa171db21e78a65870df19b`: `Wpf/Main.cs`, `Wpf/Controls/ModernTooltipService.cs`, `Wpf/General/Dialogs.cs`, `Wpf/Extensions/WpfExtensions.cs`, and `Wpf/Utils/WindowsUtils.cs`. The same fixed source's [`Wpf/General/ValueConverters.cs`](https://github.com/Meloong-Git/MeloongCore/blob/c774ae4c85fd94e0eaa171db21e78a65870df19b/Wpf/General/ValueConverters.cs) is adapted in [`presentation/Controls/NativeControlConverters.cs`](../../presentation/Controls/NativeControlConverters.cs). The four old CLR namespace converter aliases delegate to those Presentation converters, preserving one implementation. See [MeloongCore-Wpf/LICENSE.txt](../ThirdParty/MeloongCore-Wpf/LICENSE.txt), retained from the [fixed upstream Apache-2.0 license](https://github.com/Meloong-Git/MeloongCore/blob/c774ae4c85fd94e0eaa171db21e78a65870df19b/LICENSE.txt); original individual attributions are retained. The retired net48 WPF implementation DLL is not a native product reference or embedded payload.

The read-only native bundle identity reader adapts the actual .NET 10.0.14 bundle format at dotnet/runtime `6aa72daa24f66c01c3d7b6a32519e9d21396deec`; exact public paths, hashes and MIT license are in `modern/ThirdParty/dotnet-runtime-bundle`. The reader does not extract or execute candidates. LF managed identity/hash/rollback and old four-argument update handoff remain the baseline.

[`LegacyPackMetadataNumberParser.cs`](../../PCLCS/Minecraft/LegacyPackMetadataNumberParser.cs) also contains an actual managed port of the CoreCLR number parser at `abbb8f685929c7aeaa087dae46fedc1bc2af4b17`, from `src/mscorlib/src/System/Number.cs` and `src/classlibnative/bcltype/number.cpp`. Its file header retains the .NET Foundation and Contributors attribution and the complete [upstream MIT license](https://github.com/dotnet/coreclr/blob/abbb8f685929c7aeaa087dae46fedc1bc2af4b17/LICENSE.TXT). Markdig 1.6.0 is an actual Main package dependency; its [BSD 2-Clause text](../ThirdParty/Markdig/LICENSE.txt), matching the [fixed upstream license](https://github.com/xoofx/markdig/blob/1.6.0/license.txt), is embedded for the About page. [NOTICE.md](../../NOTICE.md) links these existing notices; no additional implementation from the MMC mechanism-reference projects is introduced by this documentation update.

The complete typed `NativeHandles` sources and `modern/BuildEvents` preserve original VB named-part replacement/override/event ordering. WPF's original generated files are retained; only compilation inputs receive the declared-field projection. Original implicit XAML initialization and handler ordering are part of the migration contract, not permission to remove behavior.

The C#/.NET 10 product is integrated into `main`. Current development, build and validation instructions are in [BUILDING_MODERN.md](../../docs/BUILDING_MODERN.md), and public artifact policy is in [PUBLISHING.md](../../docs/PUBLISHING.md). The migration-era [handoff and validation ledger](https://github.com/DDZmumo/PCL-Living-Fork/blob/4d635f143287720ea37a97326e4752528685fece/docs/MIGRATION_STATUS.md#migration-handoff) is retained in Git history. Its paused goals and temporary branch restrictions describe that historical checkpoint, not current development authorization. Actual account, game and upgrade acceptance must still be reported separately for each tested commit and environment.

## Embedded runtime JARs

These are actual inherited runtime components, rather than MMC mechanism
references. The Java Wrapper and agent are explicit `PCLCS.csproj` embedded
resources; the existing native resource schema retains `Resources/transformer.jar`.
Their binaries are unchanged by this license/source delivery.

- `PCLCS/Launch/JavaWrapper.jar`: Java Launch Wrapper 1.4.4, MIT,
  Copyright (c) 2022 00ll00. SHA-256
  `88eb3ee58854e916437831697ef6a2fd493da9bc4ffb018f620cd88f090952aa`
  equals the official [v1.4.4 release asset digest](https://github.com/00ll00/java_launch_wrapper/releases/tag/v1.4.4).
  The tag resolves to `da6121dcf3c5d0f1f02be402b1fbbfdd9e07e095`.
  Its complete [MIT text](../ThirdParty/JavaLaunchWrapper/LICENSE.txt) and
  [artifact record](../ThirdParty/JavaLaunchWrapper/NOTICE.txt) are retained.
- `PCLCS/Launch/LwjglUnsafeAgent.jar`: LWJGL Unsafe Agent 2.0, Apache-2.0,
  Copyright 2026 Glavo. SHA-256
  `6c07d3508dc090a12076876bdc227a19ce195a41c9e2c6535c3f82213b1b2015`
  equals the official [2.0 release asset digest](https://github.com/HMCL-dev/lwjgl-unsafe-agent/releases/tag/2.0).
  The tag resolves to `6755ad42e444d472a5a849bbe85128e05b6442e5`.
  The [fixed attribution and source record](../ThirdParty/LwjglUnsafeAgent/NOTICE.txt)
  identifies the upstream README/source license header; this fixed tree has
  no separate LICENSE/NOTICE file. The full Apache-2.0 text is reused from
  [MeloongCore-Wpf/LICENSE.txt](../ThirdParty/MeloongCore-Wpf/LICENSE.txt),
  with this agent's own Glavo attribution displayed alongside it.
- `Plain Craft Launcher 2/Resources/transformer.jar`: HMCL Transformer
  Discovery Service 1.0, GPL-3.0-or-later with the upstream Section 7 terms,
  Copyright (C) 2020 huangyuhui and contributors. SHA-256
  `ea5756fec2e06d5aa5824b54f9c26ae46a7ab43ca9a57e747f1f63e1269f7dad`;
  its Git blob `3ea51be50926e0b72ac87e7c8e73086edbbd6f04` exactly matches
  [HMCL's historical JAR](https://github.com/HMCL-dev/HMCL/blob/7c510f9fb8655598a26b1b5937bcb0046d0960d4/HMCLCore/src/main/resources/assets/game/HMCLTransformerDiscoveryService-1.0.jar)
  at `7c510f9fb8655598a26b1b5937bcb0046d0960d4`. The same fixed tree supplies
  its Java source, service registration and original build scripts.
  [SOURCE.md](../ThirdParty/HMCLTransformerDiscoveryService/SOURCE.md) and
  the [source manifest](../ThirdParty/HMCLTransformerDiscoveryService/source-manifest.json)
  identify every retained source file, full GPL text and additional terms.
  The source set also preserves the actual compile-only ModLauncher 4.1.0
  interface from `5bfa59b715884c91a8b90aa53d270776d0a60186`, Copyright
  (C) 2017-2019 cpw, with its fixed LGPLv3 notice and GPL/LGPL texts.
  This API source is used to rebuild the component; it is not an added host
  runtime dependency. LF added an offline component build script and source
  packaging on 2026-11-08; the HMCL Java implementation remains unchanged.

The complete notices and licenses are embedded for offline About-page reading.
The entire Transformer source set is also embedded; the About-page source
button exports it as a machine-readable ZIP from the same resources. The
single-EXE package requires no loose source or license attachment for these
resources. A regenerated component JAR is not asserted to be byte-identical
to the historical artifact. These source/artifact identities were checked
against official upstream sources on 2026-11-08; they do not certify the
licensing of the whole product. No license is added for pure-reference MMC
implementations.

## LF 1.3.5-pre-3 synchronization

The additional LF source is pinned to `50b028da5fcad2226d74d901a41d8c43a249bfb2`; the initial LF/PCL/CE baselines above remain unchanged. The original LF `LICENCE` applies. This batch copies no CE product base or policy.

- `Modules/ModTheme.vb`, `ModSecret.vb`, `ModMain.vb`, the setup pages and actual control/XAML changes are adapted into the single existing C# Presentation theme, hint, control and page implementations. Light `0` and dark `1` are the LF modes; there is no system-follow mode. The original live color slots, tooltip service, background opacity and animation boundaries remain authoritative.
- `Pages/PageSetup/Settings*.vb` account/compatibility changes enter the existing C# settings engine. All 13 Microsoft logical entries use their `_LF` physical names. Unsuffixed shared MS credentials and private caches without client-origin proof are left untouched. Existing LF logout and encrypted transaction/rollback semantics take precedence; other account groups retain their original migration.
- `ModDependencyIndex/Inspector/Online/AutoFill.vb`, `ModLaunch.vb`, `LocalResourceLoaders.vb` and the original graph/mod/resource UI changes enter their existing C# engines and typed adapters. Index/class metadata/nested JAR rules are the new LF behavior; no new CE/MMC/Prism/Java policy is added.
- `FileTaskProgress.vb`, world/environment/clone/pack operations, locks and navigation enter the existing C# transactions and real worker adapters. Snapshot leases still block writes and launches; their read-only navigation exception uses `BlocksNavigation`, including custom-event and launch-page settings entry. Source byte progress, cancellation, journal rollback and exact lease ownership are retained.
- The LF CNB/GitHub source, stable/prerelease channel and version-selection changes enter the real Native C# updater. Native bundle identity/legacy four-argument bridge support remains a separate necessary runtime adaptation. Build packaging and Windows handoff are subject to their own validation.

Current feature guides are retained under [file-task-progress-and-navigation.md](../../docs/file-task-progress-and-navigation.md), [microsoft-account-storage.md](../../docs/microsoft-account-storage.md), [mod-loader-compatibility.md](../../docs/mod-loader-compatibility.md) and [theme-and-update-channels.md](../../docs/theme-and-update-channels.md). The product VB host and temporary VB comparison builder have been retired; independent historical test oracles remain. Cross-compilation is not evidence of Windows GUI, account or real game behavior.

## Independently implemented MMC foundations

`PCLLF.MMCC.Core` is new C#/.NET 10 implementation under its own MPL-2.0
[license](../../PCLLF.MMCC.Core/LICENSE). Its
[mechanism and implementation record](../../PCLLF.MMCC.Core/README.md) identifies
the fixed format references and supported parsing slice. Upstream mechanisms
inform behavior; Prism, HMCL and CE implementations are not copied, ported or
line-by-line translated into this Core. This records implementation provenance,
not a formal clean-room or whole-product legal audit.

The LF host adapters, archive audit tools and integration tests retain the
repository's applicable existing license. Root `LICENCE`, third-party notices
and the licenses of input packages are unchanged. A future binary distribution
must supply the corresponding MPL-covered Core sources and notices, including
when the assembly is bundled into a single executable. The managed MMC host
installation, launch and repair path is now connected; actual acceptance and
remaining capability boundaries are tracked in `docs/mmc-gtnh-acceptance.md`
and `docs/mmc-completion-ledger.md`. Core parsing alone still grants no host
filesystem or process authority.

## Native build inputs and preserved optional mode

The toolbox daily-luck calculation and result wording were reconstructed from the user-supplied PCL 2.99 executable (SHA256 `5b64d6d540c376390470cea3ab99d47661e8bca81be2ba647ea83e7d2e9428fa`, MVID `0b152d43-0403-4f95-b151-c673fa4af019`, `PCL.PageOtherTest.Jrrp`, token `0x06000aea`) using execution-free REA metadata/CIL inspection. `PCLCS/Toolbox/LegacyDailyLuck.cs` and the inherited toolbox runtime retain the original PCL [LICENCE](../../LICENCE); they are excluded from the LF-original MPL file list. The existing pinned MeloongCore stable hash matches the embedded dependency's CIL. LF captures the local date once, uses its existing device identity and restores the result/zero-score/changed-clock dialogs; it does not reconstruct the original theme-13 unlock interface or claim to unlock a theme.

The resource producer now reads `modern/BuildResources/LF.Resources.schema.json` and the actual Main project's explicit resource declarations. It reads only the five required data/native payloads from the inherited resource directory; managed dependencies use the normal .NET host graph; it does not read the old VB designer, `.vbproj` or `.resx`. The native application manifest is an exact copy at `modern/Main/app.manifest`. The resource regression compares actual Main bytes and real typed getters; its explicit Linux defer mode reports all Windows getter calls pending.

The existing `EnableLegacyLink` mode remains false by default. Its true branch now has actual C# conditional host/config/page routing, both original compiled pages, typed part/event semantics and the original per-instance declaration-point UDP initialization/failed-initialization behavior. On .NET 10 the original architecture decision uses the actual process architecture because CoreLib metadata no longer reports that value. This preserves the original branches; it adds no server configuration, network policy or supported service. Both graphs compile; service operation and Windows behavior require separate evidence.

On Windows, `scripts/Build-Release.ps1 -Slim` builds a framework-dependent development package using the .NET 10 SDK. Official identity additionally requires `-OfficialRelease`. The self-contained Native packaging and .NET Framework bridge paths remain available for internal CI and compatibility checks; they are not the public download policy. Public assets use the names and slim-payload requirements in [PUBLISHING.md](../../docs/PUBLISHING.md). Original bridge runtime/identity and four-argument handoff remain intentionally compatible with old clients.
