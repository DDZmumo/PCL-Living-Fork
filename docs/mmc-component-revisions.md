# MMC component definition revisions

`MmcInstanceDefinitionChangeBackend<TNetFile>` provides a dedicated managed
preview/commit path. It does not call the ordinary Minecraft/Forge installer.
Existing managed launch and repair reconstruct the newly published source
bundle through `MmcSavedSourceStore`.

`PreviewAsync` fixes the current root, instance ID, generation and primary SHA,
then applies explicit enable/disable, exact-version, user-patch or reset edits
to existing author component UIDs. Required dependencies are resolved through
the existing immutable metadata store. Exact-version replacement explicitly
adopts that exact metadata as a user-selected patch, retaining its remote
snapshot and the original package patch independently. User patches cannot
pretend to change the author manifest or measured author-file baseline.
Unknown UIDs, unsupported active semantics, inconsistent enable/version
requests, incompatible Java and no-op edits return a clear refusal.

The preview exposes effective component/source changes, main class/JAR,
JVM/game arguments, tweakers, classpath, native inputs, Java policy and runtime
environment, plus explicit user-override differences. Its immutable candidate
digest covers the selection, normalized overrides, complete metadata bytes,
new source records, object digests, original-copy references and measured local
input paths/hashes. Local replacements are explicit file sources with a fixed
SHA256; commit never discovers replacement bytes by scanning unknown paths.

`CommitAsync` accepts the selected preview and its fixed digest. It recomputes
that digest, reloads the current saved package, resolves/recompiles the captured
inputs, verifies local source bytes and reuses the existing batched prefetch and
materializer. Only an exact declared digest can reuse an original across a new
metadata identity. Network work occurs before structural admission.

After configuration quiescence and admission, commit revalidates the current
publication, pending/session barriers and private destinations, then actually
captures a complete coverage-3 undo point under that same owner lease. It does
not recursively call a public history task while holding the gate. The fresh
configuration scope retains current preference overlays and unknown keys.

`MmcPlanBinder.BindDefinitionRevision` is a separate explicit Core operation.
It validates the old publication, exact retained-history fact, unchanged author
metadata/baseline and fresh candidate source. Unchanged artifact/asset keys
cannot change adopted bytes. The new current adopted set is freshly measured
and published at N+1. Replaced authority is retained in the complete undo point,
so two revisions of one flat private library can have different bytes without
claiming that both byte sequences currently occupy the same filename. Repair
continues to require the current adopted digests and cannot invoke this policy.

One file transaction publishes the new source/patch objects, compatibility
projection, definition, owned private/derived inputs, fresh manifest and a
`PCL/MmcDefinitionChanges/<operation>.json` provenance receipt. The receipt binds
old/new primary hashes, candidate digest and the actual undo point reference;
it is not an alternate source of execution authority. The host management probe
must retain this directory as evidence if the primary is lost. Old source bundles and CAS objects
are not deleted. New targets cannot overwrite unknown same-name files; removed
known targets must still match their old adopted bytes. Final validation also
covers reused owned inputs, not only files that required mutation.

Mapped asset resources are also player-directory targets. Before capturing undo,
commit reads the bounded verified index and prefetched object bytes to fix every
mapped target's SHA256/size. An existing different target requires an active
current `AssetMappedResource` record with matching old bytes; unknown resources
and edited old resources refuse without becoming authorized by an undo capture.
Missing targets and exact new-byte reuse remain safe. The materializer's actual
mapped-output inventory must match this guarded snapshot, and the ownership
check runs again after the final caller checkpoint before the transaction.

The minimal regression group exercises custom main/JVM/library component
disable/enable/reset, fresh empty/unknown preference overlays, ordinary managed
repair after modification, exact remote-version/dependency/native resolution,
same-name A-to-B local replacement and complete history undo/redo, offline
preparation after ZIP/global-cache deletion, stale or altered candidates,
unknown target/input rejection, pending/session barriers, cancellation and
controlled transaction interruption before/after the decision. These are
backend/real-file tests; they do not claim GUI, Minecraft or process-kill
acceptance. Root-component creation/removal outside the immutable author
manifest is not an edit operation; new/removable dependency references are
resolved from the edited component declarations.
The mapped-resource case additionally checks new unknown targets, edited owned
targets and a late edit before transaction; all retain current primary bytes,
generation and player bytes. Known A-to-B mapping, history undo/redo, missing
targets and exact-byte reuse succeed.

The version-management page now has a component/patch editor using LF controls,
the genuine Loader and the existing backend. Three focused actual-Main groups
cover enable/disable/reset, exact-version/local-JAR adoption, and selected JSON
patch publication. A further pair covers cancellation after final validation
and after the durable decision. These are isolated fixtures, not game runs.

The cancellation fence is entered only after final world/source validation and
the last selected-input checks. It must not be entered by the earlier observer
checkpoint: large-world hashing remains cancellable. The final platform-evidence
check also precedes this boundary. A cancelled validation cannot create a new
CommitDecided record or publish a new generation; cancellation after the durable
decision still completes the committed result.

The remaining focused actual-Main groups also passed separately: unstarted and
preview cancellation, input mutations, unsupported semantics, stale selection,
unknown session/target/root, successful/locked/changed-world backup, genuine
NetFile HTTP cancellation/drain, and an explicitly probed installed Java 21.
Together with the earlier five groups these make nineteen groups, each retaining
the original fresh-process budget. No Minecraft process was launched.

The Java fixture initially assumed that an extra component's Java declaration
removed Java 8 already declared by its Minecraft component. The fixture now
checks its real combined candidate policy: the initial selection stays at Java 8,
then the user explicitly selects the measured Java 21 executable. Production
compiler semantics were not changed to satisfy the incorrect assertion.
