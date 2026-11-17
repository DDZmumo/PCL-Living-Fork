# Fixed Prism native reference subset

This opt-in developer tool compiles original Prism C++ function bodies from
`899d2f3957215a09fe398a8a8cbb8a8a9c619968`. It has no product reference and is
not packaged with PCLLF. The existing Prism 12 portable artifact is not used.

The measured path is `OneSixVersionFormat` → `VersionFile::applyTo` →
`LaunchProfile`, including `Library`, `Rule`, `RuntimeContext`, `GradleSpecifier`
and `Version`. The original pre-binding game tokenization/tweaker statements
are extracted from `MinecraftInstance::processMinecraftArgs` and compiled.
No C# or Python port supplies the reference output.

`PackProfile` and `MinecraftInstance` sources are fixed for inspection, but
the complete application, `ComponentUpdateTask` dependency resolver, settings,
network download executor, account/macro binding, Java selection, wrapper,
materialization and JVM launch are not run. Explicit ordered input components
are supplied by the adapter. This is actual upstream native parsing/planning
evidence for that subset, not a full Prism application or game acceptance.

## Reproduce on Windows

Use a fresh ignored `artifacts` root. Existing source/metadata evidence and the
original ZIPs are read only. No system installation, PATH persistence, registry
change or account is needed. Downloaded compiler/Qt packages are extracted only
below the chosen artifact root; a compatible GCC 15 runtime is pinned for the
prebuilt Qt/ICU DLLs. The GCC 16 compiler is self-contained. Keep the extracted
notices and license files with these development dependencies.

```powershell
python -X utf8 scripts/mmc/prism-reference/BuildReference.py --root artifacts/prism-native --fetch --download-tools --build
python -X utf8 scripts/mmc/prism-reference/PrepareCases.py --root artifacts/prism-native --adopted-metadata E:/Project/PCL2-MMC-Compatibility/artifacts/mmc-goal/metadata
C:/Users/16892/.codex/tools/dotnet10/dotnet.exe build tests/PCLLF.Mmc.Prism.Differential/PCLLF.Mmc.Prism.Differential.csproj --nologo -v minimal -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false
python -X utf8 scripts/mmc/prism-reference/Compare.py --root artifacts/prism-native --dotnet C:/Users/16892/.codex/tools/dotnet10/dotnet.exe --core-adapter tests/PCLLF.Mmc.Prism.Differential/bin/Debug/net10.0/PCLLF.Mmc.Prism.Differential.dll --output artifacts/prism-native/comparison.json
```

The C++ build is serial. `--resume` preserves already compiled unchanged
translation units after an interrupted build. There is no automatic retry or
SDK installation. `--fetch` verifies every source against `source-lock.json`;
the tooling package hashes are in `toolchain-lock.json`. Offline builds use
the same verified cache without `--fetch` or `--download-tools`.

`PrepareCases.py` only accepts the two authorized GTNH 2.9.0 archive hashes.
It reads their metadata and original entry facts, never extracts game payloads
or overwrites the ZIPs. Java 8 metadata is accepted only if its three historical
adopted SHA256 values match; no live request or current template can replace it.
Four original tiny boundary fixtures cover ordered tokens, duplicates, local
paths, Maven versions, download-only libraries, rules/native variants, and a
null `mainJar` grammar difference.

`Compare.py` preserves each implementation's stdout/stderr and exit code.
Comparison only replaces the explicit global/instance library root prefixes
and stable-deduplicates the appended Prism Java-major list to compare LF's
documented finite set. All argument and path arrays remain ordered. A
rejection or field difference returns nonzero and stays visible in evidence;
it is not converted to an equivalence pass. Output report paths must be new.

## Retained native payload check

A retained LF acceptance fixture can also be checked without extracting files:

```powershell
python -X utf8 scripts/mmc/prism-reference/VerifyNativePayload.py --import-receipt 'PATH-TO-OWNED-IMPORT-RESULT.json' --case artifacts/prism-native/cases/gtnh-java21/case.json --comparison-evidence artifacts/prism-native/comparison-cases/gtnh-java21 --output artifacts/prism-native/native-payload-verified.json
```

This verifies actual selected JAR digests, entry hashes and materialized LF
native files. It is a concrete byte inventory, not a Prism extractor/port or a
replacement for the native reference output. The recorded modern GTNH fixture
has three selected native JARs and ten disjoint DLL entries; its existing LF
native files match every payload SHA256. See the [result and scope notes](../../../docs/mmc-prism-differential.md).

## Dependency trimming and license boundary

`transformations.json` records generated-file hashes and every trim/adapter:

- Remove unused `PackProfile` includes, use the actual `LaunchProfile` declaration.
- Remove `Library::getDownloads` and network-only includes/declarations. All
  measured active/native/path methods are unchanged.
- Explicitly supply the OS and selected Java architecture/bitness. Omit system
  discovery and the settings updater; original classifier functions are intact.
- Extract unchanged format/requirements parser functions, path functions and
  invalid-character helpers. Preserve the original path declaration/default.
- Supply only the build display name used in an unsupported-launcher warning.
- Unused JSON file IO overloads fail closed. Harness input uses `QFile` directly.
- Extract original game-token prefix statements before auth/server/macro binding.

Downloaded originals retain their full author headers, [Prism's fixed source
notice](https://github.com/PrismLauncher/PrismLauncher/blob/899d2f3957215a09fe398a8a8cbb8a8a9c619968/COPYING.md)
and license text. Generated excerpts preserve the applicable original header.
The native adapter [Reference.cpp](Reference.cpp) is GPL-3.0-only and its text is
[included here](LICENSE.GPL-3.0-only.txt). Python orchestration and the separate
Core adapter are original LF development tools under the repository license;
they do not contain an upstream rules implementation. Core remains original
MPL-2.0 code and does not link or copy any of this reference source.

If redistributing a reference binary or toolchain, satisfy the corresponding
source/notice/license obligations; this workflow keeps those binaries in ignored
local artifacts and does not add them to the product deliverable. A compiled
reference comparison is behavioral evidence, not a code-ownership certification.
