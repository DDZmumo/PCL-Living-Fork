# HMCL Transformer Discovery Service 1.0 source

The shipped `Plain Craft Launcher 2/Resources/transformer.jar` is byte-for-byte
the JAR committed at [HMCL `7c510f9fb8655598a26b1b5937bcb0046d0960d4`](https://github.com/HMCL-dev/HMCL/commit/7c510f9fb8655598a26b1b5937bcb0046d0960d4)
as `HMCLCore/src/main/resources/assets/game/HMCLTransformerDiscoveryService-1.0.jar`.
Both have Git blob ID `3ea51be50926e0b72ac87e7c8e73086edbbd6f04`; the LF
SHA-256 is `ea5756fec2e06d5aa5824b54f9c26ae46a7ab43ca9a57e747f1f63e1269f7dad`.
The LF copy is unchanged since PCL's initial public commit
`b0b02291cc6e6aa4e3c8aaadf61a2dcf9028f76d`.

The same fixed HMCL tree contains the retained preferred-form Java source
(blob `64d4b3dbd41dd04e3486ad4c93667d33f19712ac`), service registration and
Gradle build scripts. [LICENSE](LICENSE) is its full GPLv3 text; the Java header
permits version 3 or later. [ADDITIONAL_TERMS.md](ADDITIONAL_TERMS.md) preserves
the complete License section from that tree's README, including both Section 7
terms and its Chinese translation. These are retained component terms; the
launcher does not relicense this source under MPL.

## Files and build

[Build.ps1](Build.ps1) is an LF-added, offline component build script. With a
JDK 9 or later (tested with JDK 21), from this directory run:

```powershell
pwsh -NoProfile -File ./Build.ps1
```

It compiles the actual [ModLauncher 4.1.0 API definition](compile-api/ITransformerDiscoveryService.java)
into a separate compile-only directory, then compiles the unmodified
[HMCL source](src/main/java/org/jackhuang/hmcl/HMCLTransformerDiscoveryService.java)
for Java 8. The resulting `build/HMCLTransformerDiscoveryService-1.0.jar`
contains only the HMCL class, its service registration and manifest.
It never runs the JAR, and the build needs no network or downloaded dependency.
Use `-OutputDirectory <path>` for a different output directory.

The interface is copied unchanged from
[ModLauncher `5bfa59b715884c91a8b90aa53d270776d0a60186` (tag 4.1.0)](https://github.com/McModLauncher/modlauncher/blob/5bfa59b715884c91a8b90aa53d270776d0a60186/src/api/java/cpw/mods/modlauncher/serviceapi/ITransformerDiscoveryService.java).
Its LGPLv3 copyright header and complete [license notice](compile-api/LICENSE),
[GPL base text](compile-api/COPYING) and [LGPL text](compile-api/COPYING.LGPL)
are retained. No interface implementation or ModLauncher runtime is added to
the launcher by this source delivery.

`upstream-build/` retains the original HMCL root `build.gradle`,
`settings.gradle`, `license-header.txt` and component `build.gradle` unchanged.
They document the original multi-project build and its compile-only
`lib/modlauncher-4.1.0.jar` input. They are preserved build context; the standalone
script supplies that exact API from source and avoids unrelated HMCL modules
and obsolete remote build repositories. General-purpose JDK/Gradle binaries
are not included.

The component JAR is regenerated, not promised to be byte-identical to the
historical JAR: its compiler debug metadata, manifest, timestamps and archive
encoding can differ. The provenance claim rests on the exact upstream
historical binary blob match, rather than on a new build being the old artifact.

## Binary and source delivery

Every file listed in [source-manifest.json](source-manifest.json), plus the
manifest itself, is embedded as a WPF resource in the launcher EXE. The About
page displays the complete GPL and additional terms offline, and its source
export button writes the complete machine-readable set to a user-selected ZIP.
No loose source or notice sidecar is required by the single-EXE package.

A future release must retain those embedded notices and source files together
with the shipped component. This records this component's identity and source
delivery; it does not certify the licensing or legal treatment of the complete
launcher and game runtime.

All source locations and upstream binary identity were verified on 2026-11-08.
