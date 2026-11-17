# Managed MMC ZIP updates

`MmcInstancePackageUpdateBackend<TNetFile>` implements explicit local-ZIP update
preparation, selection and transactional publication. It does not call ordinary
Forge installation or the legacy update transaction executor.

## Operation boundary

`PrepareAsync` fixes the current root/ID/generation/primary digest, reads the
selected local ZIP and really imports its author candidate under
`<MC root>/PCL/MmcPackageUpdates/<operation>/versions/candidate`. This is outside
the player's versions list and active instance. An exclusive nonce directory,
flushed owner record, sealed candidate primary and full candidate-file digest
inventory bind subsequent operations. The original ZIP is not required after
successful preparation. Layout changes between `minecraft` and `.minecraft`
require explicit migration and are refused by this update operation.

`SelectAsync` takes the fixed preview digest and normalized component/file
decisions. The preview reports new/removed author roots, dependency changes,
current user-override conflicts, effective runtime/private-input changes and
author-versus-personal file changes. Surviving nonconflicting user overrides
remain selected. A changed/removed root with an override needs an explicit
decision; a removed root cannot retain an orphaned override. Unverified final
mod dependencies require the existing explicit acknowledgement; definite
dependency errors remain blocking.

File planning uses the existing `PackUpdateEngine.Plan`, `DownloadChanges`,
`ReviewPreview`, `ValidateSnapshot`, `ValidateChoices` and `BuildNextManifest`
with schema-2 author ownership. A narrow internal partial returns the original
validated file-operation list. It does not bypass legacy guards or execute the
old `Commit`/`ApplyTransaction`. Author hashes come from measured old/new package
baselines; current observations cannot become author declarations. Personal
modifications, deletions, disabled states, additions and ambiguous associations
retain the existing three-way policy. Worlds, account files, map/runtime data
and the inactive game tree are excluded from update operations.

The immutable preview digest covers normalized choices, source objects,
metadata, local library inputs, author payloads, the complete relevant local
file namespace, candidate inventory and actual effective plan fields. Commit
re-evaluates the final dependency graph and recompiles the captured source
inputs instead of trusting a self-reported compiled preview.

## Immutable author/package state

`SourceBundle.PackageLocalScopeSha256` is optional. Null keeps historical JSON,
artifact keys and all fingerprints unchanged. New measured ZIP updates use the
new origin package SHA as a namespace only for `PackageLocal` component sources
whose library scope is `InstanceLocal`. The artifact's `SourceRevision` remains
the original patch SHA. Consequently a new ZIP may change local JAR bytes while
keeping JSON, coordinates and filename identical. Remote declarations and
retained user-patch local inputs do not gain new authority from that namespace.
Saved-source replay, clone and history preserve the scope explicitly.

`PackageOwnershipReference` is also optional. Its immutable schema-2 manifest is
stored by content digest under `MmcSource`; the payload must bind the matching
origin ZIP SHA. It is part of the bound definition, not the component profile.
The mutable `PCL/ModpackOrigin.json` is a mirror. New updates read the immutable
reference, and old definitions without one bootstrap only from their measured
author baseline. History undo therefore restores the old ownership policy as
well as its component source, even if the mutable mirror is newer. Current
personal additions are not recruited into the author baseline on later updates.

## Publication and recovery

Known old/new CAS inputs are verified into an owned external input pool, so
runtime preparation does not require a surviving global cache or silently
adopt unknown local paths. Network work finishes before current-instance
admission. Commit quiesces settings, takes sorted current/candidate admission
leases, rechecks session/pending/current identity and both file snapshots,
and captures a real complete undo point under the same owner lease. Its final
validated write/delete operation targets are declared in the managed-path scope
of a complete coverage-5 safe inventory, including absent targets. Core package
binding requires that every real mutation target belongs to the captured proof;
a fixed-scope point cannot authorize extra resource-pack/custom/root-file writes.
New ZIP undo points use complete safe inventory even for runtime-only changes,
so later direct restoration can prove future paths without guessing away
existing personal content.
Unknown private-library and mapped-resource destinations retain the existing
typed ownership guards.

A real Minecraft-version change with existing worlds requires the host's
existing `WorldBackupEngine.CaptureFolder` adapter to finish successfully.
The backend never starts Minecraft or opens a world. The new definition,
source/patch evidence, author baseline, runtime inputs, selected game-file
operations, immutable ownership, mirror, manifest and update receipt publish in
one MMC file transaction at N+1. Fresh current preference overlays survive.
Predecision cancellation rolls back; a durable decision recovers forward.

All candidate operations use a cross-process job mutex. `DiscardAsync` drains
providers before quiescence/admission and removes only the verified exclusive
nonce directory after a bounded no-link/no-session scan. Ordinary whole-root,
PCL-folder and old file-helper cleanup refuses external candidate stores,
including unknown residuals when the original active instance no longer exists.
The receipt store is retained management evidence, not execution authority.

## Validation status

Initial local verification passed single-node .NET 10 compilation with shared
compilation disabled, two scope regressions, the 34 existing definition/authority
regressions and one real-file continuous ZIP-update aggregate. That aggregate
covers identical-metadata/same-size local A-to-B replacement, original patch
revision preservation, ZIP/global-cache deletion followed by prepare/repair,
personal modification/deletion/disable/addition through B and C, immutable
ownership after undo to B/A and later planning, both worlds, map data and current
empty/unknown preference values.

The remaining new component/removal, refusal, transaction-interruption,
world-backup and cleanup aggregates have been authored and await their scheduled
focused run. No GUI, real game launch or host-process-kill acceptance is claimed.
This local-ZIP operation infers no platform project ID and does not carry the old
platform version ID into a new unverified origin.

The coverage-5 correction subsequently passed a zero-warning single-node build
and three targeted real-file aggregates: direct A-to-B-to-C restoration of A and
redo of C, both absent/personal future-path variants and unrelated personal data;
existing options/OptiFine/shader/Litematica runtime settings with legacy fixed
coverage; and conservative ordinary-author/mapped-asset role migration refusal.
An independent 43e71f2c production checkout reproduced the direct-old-point bug
with the same API: restoration reported success while A's resource still held B.
