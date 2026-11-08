#!/usr/bin/env python3
"""Select existing LF checks from changed paths; unknown dependencies fail closed to full.

No arguments read the GitHub event. Use --path repeatedly for local inspection,
--full for a complete checkpoint. Only fixed group/script names become outputs.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys

MAIN_BRANCH = "main"
MIGRATION_BRANCH = "migration/csharp-modern-dotnet"
DOCUMENT_FILES = {
    "README.md", "NOTICE.md", "CONTRIBUTING.md", "SECURITY.md", "CODE_OF_CONDUCT.md",
    "Plain Craft Launcher 2/Images/Branding/DESIGN.md",
    "modern/Main/SOURCE_PROVENANCE.md", "modern/BuildEvents/tests/README.md",
    "modern/BuildResources/README.md", "modern/BuildResources/tests/README.md",
    "tests/PCLLF.Native.Facade.Tests/README.md",
}


def is_documentation_path(name: str) -> bool:
    # Only repository-relative, unambiguous paths enter the fast path. Files
    # under docs/ are not automatically docs: fixtures and generators are code.
    if not isinstance(name, str) or "\\" in name or any(ord(char) < 32 for char in name):
        return False
    if any(part in {"", ".", ".."} for part in name.split("/")):
        return False
    if name.rsplit("/", 1)[-1].casefold() in {"agents.md", "skill.md"}:
        return False
    if name in DOCUMENT_FILES:
        return True
    suffix = Path(name).suffix.lower()
    return (name.startswith("docs/") and suffix in {".md", ".mdx", ".rst", ".adoc"}) or (
        name.startswith("docs/assets/") and suffix in {".svg", ".png", ".jpg", ".jpeg", ".webp", ".gif"})


def documentation_only(paths: list[str]) -> bool:
    return bool(paths) and all(is_documentation_path(name) for name in paths)

GROUPS = ("world-metadata", "local-data", "personal-options", "keybindings", "quick-play",
          "resource-entry", "resource-paths", "resource-files", "resource-metadata", "world-backups", "toolbox-surprise", "environment-history", "instance-clone", "pack-update", "mod-dependencies", "mod-dependency-online", "settings", "mod-favorites", "validation-loaders", "legacy-java", "foundation")
# Resources is a fixed feature alias, not every group appended after the first five.
RESOURCE_GROUPS = ("resource-entry", "resource-paths", "resource-files", "resource-metadata")
# Release-tooling-only paths have isolated, synthetic Python checks. They do
# not change launcher code or require a VB/WPF build; other workflow edits keep
# the conservative full fallback below.
CNB_CHECK_PATHS = {
    ".github/workflows/cnb-checks.yml", ".github/workflows/cnb-release.yml",
    "scripts/ReleaseSyncState.py", "scripts/Sync-CnbRelease.py",
    "scripts/Test-CnbMirror.py", "scripts/Test-CnbTrigger.py", "scripts/Trigger-CnbSync.py",
}
# These files belong only to the real C#/.NET10 launcher/tooling. They have
# no legacy VB build inputs. The native workflow builds, packages and runs its
# own cold-entry/facade checks; unknown future shared paths retain full fallback.
NATIVE_ONLY_PREFIXES = (
    "modern/Main/", "modern/BuildEvents/", "modern/BuildResources/",
    "modern/Presentation/NativeWpf/", "modern/ThirdParty/",
    "tests/PCLLF.Native.Facade.Tests/", "scripts/native-smoke/",
    "scripts/native-update-inspector/", "scripts/tests/native-credentials/",
)
NATIVE_ONLY_PATHS = {
    "PCL-Living-Fork.Modern.slnx",
    ".github/workflows/native-main.yml",
    "scripts/Build-NativeRelease.ps1", "scripts/Build-ModernRelease.ps1",
    "scripts/Test-NativeLauncherSmoke.ps1",
    "scripts/tests/Test-NativeUpgradeQualification.py",
    "scripts/tests/Test-CSharpCredentialFactories.ps1", "scripts/tests/Test-NativeBuildCredentials.ps1",
}

NATIVE_ONLY_PATHS.update({"scripts/Test-NativeUpdateBridge.ps1", "scripts/tests/Test-BridgePayloadIdentity.ps1"})
NATIVE_ONLY_PREFIXES += ("scripts/native-upgrade/",)
SELECTION_CHECK_PATHS = {"scripts/tests/Test-MigrationSelection.py", "scripts/tests/MigrationSelection.golden.json"}
# Exact existing scripts; workflows keep their original static invocations/options.
SCRIPTS = (
    "Test-Release", "Test-VersionListCancellation", "Test-AuditFixes", "Test-CustomEventAndSecret", "Test-LauncherActivation", "Test-MigratedControls",
    "Test-Toolbox", "Test-ToolboxMemory", "Test-InstanceResources", "Test-MapInstallAndRecycle",
    "Test-BackgroundModes", "Test-ResourceUpdates", "Test-LocalGameFeatures", "Test-GameFeatureRegressions",
    "Test-ModpackUpdate", "Test-ModpackUpdateUx", "Test-ReviewFixes", "Test-EnvironmentHistory",
    "Test-BackupRetention", "Test-RecentWorldCard", "Test-LaunchDependencies", "Test-DependencyActivation",
    "Test-DependencyToggles", "Test-WorldBackups", "Test-WorldBackupGroups", "Test-SaveBackupIntegration",
    "Test-ModDependencies", "Test-ModDependencyOnline", "Test-ModDependencyGraph", "Test-InstanceClone",
    "Test-InstanceCloneGroups", "Test-RecentEnvironmentChanges", "Test-EnvironmentCoverage", "Test-KeybindingGroups",
    "Test-MapDetails", "Test-ResourceEntryPoints", "Test-WorldDetails", "Test-ScreenshotGallery",
    "Test-ToolboxDownload", "Test-ModpackMode", "Test-ModpackFolderScope", "Test-IconCompatibility",
    "Test-InstalledComponentLoading", "Test-ExportVersionIsolation",
)
WORLD_CHECKS = {"Test-WorldDetails", "Test-MapDetails", "Test-InstanceResources", "Test-WorldBackups",
                "Test-WorldBackupGroups", "Test-SaveBackupIntegration", "Test-LocalGameFeatures",
                "Test-GameFeatureRegressions", "Test-RecentWorldCard", "Test-EnvironmentHistory", "Test-BackupRetention"}
LOCAL_CHECKS = {"Test-LocalGameFeatures", "Test-GameFeatureRegressions", "Test-KeybindingGroups",
                "Test-RecentWorldCard", "Test-ModpackUpdate", "Test-ModpackUpdateUx", "Test-EnvironmentHistory",
                "Test-BackupRetention", "Test-WorldBackups", "Test-WorldBackupGroups", "Test-SaveBackupIntegration"}
RESOURCE_CHECKS = {"Test-InstanceResources", "Test-MapInstallAndRecycle", "Test-BackgroundModes",
                   "Test-ResourceUpdates", "Test-MapDetails", "Test-ResourceEntryPoints", "Test-ScreenshotGallery"}
# These UI files serve the resource page and its existing offscreen consumers.
# Exact paths keep unrelated/future platform contracts on the full fallback.
RESOURCE_PAGE_PATHS = {
    "PCLCS/Platform/Windows/Resources/ResourcePageContracts.cs",
    "PCLCS/Platform/Windows/Resources/ResourcePageController.cs",
    "PCLCS/Platform/Windows/Resources/ResourcePageController.Recycle.cs",
    "PCLCS/Platform/Windows/Resources/ResourcePageController.Backups.cs",
    "Plain Craft Launcher 2/Pages/PageInstance/PageInstanceResources.xaml.vb",
    "Plain Craft Launcher 2/Pages/PageInstance/PageInstanceResources.Recycle.vb",
    "Plain Craft Launcher 2/Pages/PageInstance/PageInstanceResources.Backups.vb",
    "Plain Craft Launcher 2/Pages/PageInstance/PageInstanceResources.xaml",
}
RESOURCE_IMAGE_PATHS = {
    "PCLCS/Platform/Windows/Resources/ResourceThumbnailController.cs",
    "PCLCS/Platform/Windows/Resources/InstanceResourceImages.cs",
    "Plain Craft Launcher 2/Pages/PageInstance/MyScreenshotItem.xaml.vb",
    "Plain Craft Launcher 2/Pages/PageInstance/MyScreenshotItem.xaml",
}
RESOURCE_PAGE_CHECKS = RESOURCE_CHECKS | {"Test-SaveBackupIntegration", "Test-InstanceClone"}
# The shared decoder also supplies recent-world images through PageLaunchRight.
RESOURCE_IMAGE_CHECKS = RESOURCE_CHECKS | {"Test-RecentWorldCard"}
# The update facade's hash helper is shared with launch/dependency activation.
RESOURCE_ONLINE_CHECKS = RESOURCE_CHECKS | {"Test-LaunchDependencies", "Test-DependencyActivation", "Test-DependencyToggles"}
# Backup engine/workflow callers: grouped saves, clone guards and pre-upgrade flow.
# Launch recovery/process-stop behavior is exercised directly by Test-WorldBackups.
WORLD_BACKUP_PATHS = {
    "PCLCS/Minecraft/WorldBackupContracts.cs",
    "PCLCS/Minecraft/WorldBackupEngine.cs",
    "PCLCS/Minecraft/WorldBackupEngine.Restore.cs",
    "PCLCS/Minecraft/WorldBackupWorkflow.cs",
    "Plain Craft Launcher 2/Modules/Minecraft/WorldBackups.vb",
}
WORLD_BACKUP_CHECKS = {
    "Test-WorldBackups", "Test-WorldBackupGroups", "Test-BackupRetention", "Test-SaveBackupIntegration",
    "Test-InstanceResources", "Test-MapInstallAndRecycle", "Test-InstanceClone", "Test-InstanceCloneGroups",
    "Test-ModpackUpdate", "Test-ModpackUpdateUx",
}
# History owns these exact sources; future shared contracts still fail closed.
# Its callers include modpack updates, clone migration and launch preparation;
# the retained VB transaction and real instance adapters run in Windows checks.
ENVIRONMENT_HISTORY_PATHS = {
    "PCLCS/Minecraft/EnvironmentHistoryContracts.cs",
    "PCLCS/Minecraft/EnvironmentHistoryEngine.cs",
    "PCLCS/Minecraft/EnvironmentHistoryEngine.Restore.cs",
    "PCLCS/Minecraft/EnvironmentHistoryWorkflow.cs",
    "PCLCS/Minecraft/RecentEnvironmentChanges.cs",
    "Plain Craft Launcher 2/Modules/Minecraft/EnvironmentHistory.vb",
    "Plain Craft Launcher 2/Modules/Minecraft/RecentEnvironmentChanges.vb",
}
ENVIRONMENT_HISTORY_CHECKS = {
    "Test-EnvironmentHistory", "Test-RecentEnvironmentChanges", "Test-EnvironmentCoverage", "Test-BackupRetention",
    "Test-ModpackUpdate", "Test-ModpackUpdateUx", "Test-InstanceClone", "Test-InstanceCloneGroups", "Test-LaunchDependencies",
}
# Clone's exact backend/adapters also provide the path/link/hash/isolation
# primitives used by the existing history and recent-change Windows hosts.
# The clone check runs the retained real transaction, including rollback;
# unrelated resource/dependency suites are not required by these exact paths.
INSTANCE_CLONE_PATHS = {
    "PCLCS/Minecraft/InstanceCloneContracts.cs",
    "PCLCS/Minecraft/InstanceCloneEngine.cs",
    "PCLCS/Minecraft/InstanceCloneEngine.Migration.cs",
    "PCLCS/Minecraft/InstanceCloneGroupsEngine.cs",
    "PCLCS/Minecraft/InstanceCloneExportIsolation.cs",
    "PCLCS/Minecraft/InstanceCloneWorkflow.cs",
    "PCLCS/Platform/Windows/Instances/InstanceClonePageController.cs",
    "PCLCS/Platform/Windows/Instances/InstanceCloneSelectionPresenter.cs",
    "Plain Craft Launcher 2/Modules/Minecraft/InstanceCloner.vb",
    "Plain Craft Launcher 2/Modules/Minecraft/InstanceCloneGroups.vb",
    "Plain Craft Launcher 2/Modules/Minecraft/ExportedVersionIsolation.vb",
    "Plain Craft Launcher 2/Pages/PageInstance/PageInstanceClone.xaml.vb",
}
INSTANCE_CLONE_CHECKS = {
    "Test-InstanceClone", "Test-InstanceCloneGroups", "Test-ExportVersionIsolation",
    "Test-EnvironmentHistory", "Test-RecentEnvironmentChanges", "Test-EnvironmentCoverage", "Test-BackupRetention",
}
# Author ownership/rename/transaction fixtures execute the real pack engine.
# Its metadata adapter consumes the shared inspector; parser changes must also
# select the pack fixtures so dependency IDs cannot regress into mod identities.
# Exact paths keep future, unclassified shared contracts on the full fallback.
PACK_UPDATE_PATHS = {
    "PCLCS/Minecraft/PackUpdateContracts.cs",
    "PCLCS/Minecraft/PackUpdateEngine.Package.cs",
    "PCLCS/Minecraft/PackUpdateEngine.Baseline.cs",
    "PCLCS/Minecraft/PackUpdateEngine.Plan.cs",
    "PCLCS/Minecraft/PackUpdateEngine.Transactions.cs",
    "PCLCS/Minecraft/PackUpdateFlowContracts.cs",
    "PCLCS/Minecraft/PackUpdateFlowEngine.cs",
    "PCLCS/Minecraft/PackUpdateFlowEngine.Execute.cs",
    "PCLCS/Platform/Windows/Modpacks/ModpackUpdatePreviewController.cs",
}
PACK_UPDATE_METADATA_PATH = "PCLCS/Minecraft/PackUpdateModInspection.cs"
PACK_UPDATE_CHECKS = {"Test-ModpackUpdate", "Test-ModpackUpdateUx"}
# History and clone use the same digest/atomic transaction contracts. Their
# existing consumers stay selected when these shared implementations change.
PACK_UPDATE_SHARED_PATHS = {
    "PCLCS/Minecraft/PackUpdateContracts.cs",
    "PCLCS/Minecraft/PackUpdateEngine.Package.cs",
    "PCLCS/Minecraft/PackUpdateEngine.Transactions.cs",
}
# Exact local-inspector sources; the online/graph and launch repair/activation
# checks consume its real report, path, version and provider contracts.
# Toggle layout and world/modpack backup suites do not execute the inspector.
MOD_DEPENDENCY_PATHS = {
    "PCLCS/Minecraft/ModDependencyInspection.Contracts.cs",
    "PCLCS/Minecraft/ModDependencyInspection.Archives.cs",
    "PCLCS/Minecraft/ModDependencyInspection.Resolution.cs",
    "PCLCS/Minecraft/ModDependencyVersions.cs",
    "Plain Craft Launcher 2/Modules/Minecraft/ModDependencyInspector.vb",
}
MOD_DEPENDENCY_CHECKS = {
    "Test-ModDependencies", "Test-ModDependencyOnline", "Test-ModDependencyGraph",
    "Test-LaunchDependencies", "Test-DependencyActivation",
}
# Online enrichment supplies exact-content DTOs to details/graph and the real
# launch repair plan. Activation's handcrafted local report does not call it.
MOD_DEPENDENCY_ONLINE_PATHS = {
    "PCLCS/Minecraft/ModDependencyOnlinePipeline.cs",
    "PCLCS/Minecraft/ModDependencyOnlinePipeline.Files.cs",
    "PCLCS/Minecraft/ModDependencyOnlinePipeline.Matches.cs",
    "PCLCS/Minecraft/ModDependencyTransport.cs",
    "Plain Craft Launcher 2/Modules/Minecraft/ModDependencyOnline.vb",
}
MOD_DEPENDENCY_ONLINE_CHECKS = {
    "Test-ModDependencyOnline", "Test-ModDependencyGraph", "Test-LaunchDependencies",
}
# Store is the shared path/transaction facade consumed by these existing chains.
STORE_CHECKS = RESOURCE_CHECKS | WORLD_CHECKS | LOCAL_CHECKS | {
    "Test-ReviewFixes", "Test-InstanceClone", "Test-InstanceCloneGroups", "Test-RecentEnvironmentChanges",
    "Test-EnvironmentCoverage", "Test-LaunchDependencies", "Test-DependencyActivation", "Test-DependencyToggles",
    "Test-ModDependencies", "Test-ModDependencyOnline", "Test-ModDependencyGraph", "Test-ExportVersionIsolation"}
BULK_CHECKS = {"Test-WorldBackups", "Test-WorldBackupGroups", "Test-SaveBackupIntegration",
               "Test-ModDependencies", "Test-ModDependencyOnline", "Test-ModDependencyGraph", "Test-InstanceClone",
               "Test-InstanceCloneGroups", "Test-RecentEnvironmentChanges", "Test-EnvironmentCoverage",
               "Test-KeybindingGroups", "Test-MapDetails", "Test-ResourceEntryPoints", "Test-WorldDetails", "Test-ScreenshotGallery"}


def choose(paths: list[str], full: bool = False, reason: str = "changed paths") -> dict:
    groups: set[str] = set()
    scripts: set[str] = set()
    oracle = inventory = product = False
    modern_test_change = False
    native = native_only_change = False
    cnb_tooling_change = False
    reasons = [reason]
    unknown = []
    docs_only = not full and documentation_only(paths)
    if docs_only:
        reasons.append("documentation-only whitelist: lightweight document validation, no product execution")

    def include(selected, checks, *, io_oracle=False, build=True):
        nonlocal oracle, product
        groups.update(selected)
        scripts.update(checks)
        oracle |= io_oracle
        product |= build

    for name in paths:
        if docs_only:
            continue
        # Product directories are considered before documentation extensions.
        leaf = name.rsplit("/", 1)[-1]
        # Common source changes affect the modern product as well as whatever
        # precise legacy groups/scripts are already selected below.
        if name.startswith(("PCLCS/", "presentation/", "MeloongCore/")) or name in {"MeloongCore", ".gitmodules"}:
            native = True
        if name in SELECTION_CHECK_PATHS:
            # The native selection job always runs these real CLI regressions.
            # Changes to its test inputs alone do not require a product build.
            continue
        elif name == "docs/migration/source-inventory.baseline.json" or name == "scripts/Inventory-Migration.py":
            inventory = True
        elif name in NATIVE_ONLY_PATHS or name.startswith(NATIVE_ONLY_PREFIXES):
            native = native_only_change = True
        elif name in CNB_CHECK_PATHS:
            cnb_tooling_change = True
        elif name.startswith("tests/PCLLF.Modern.Wpf.Tests/") or name == ".github/workflows/modern-wpf.yml":
            # These sources execute in their own real NET10 WPF workflow and do
            # not reference the VB product or require its unrelated regressions.
            modern_test_change = True
        elif name == "tests/PCLLF.Windows.Tests/ResourceThumbnailControllerChecks.cs":
            include((), {"Test-ScreenshotGallery"})
        elif name == "tests/PCLLF.Windows.Tests/KeybindingControllerChecks.cs":
            include((), {"Test-KeybindingGroups"})
        elif name == "scripts/Select-MigrationChecks.py" or name.startswith(".github/workflows/"):
            full = True
            reasons.append("check routing/workflow changed")
        elif name.startswith("tests/PCLLF.PathOracle/") or name == "scripts/Test-MigrationResourcePaths.ps1":
            include(RESOURCE_GROUPS[1:], (), io_oracle=True, build=False)
        elif name in {"tests/PCLLF.Migration.Tests/Program.cs", "tests/PCLLF.Migration.Tests/PCLLF.Migration.Tests.csproj"}:
            include(GROUPS, (), build=False)
        elif name.startswith("tests/PCLLF.Migration.Tests/"):
            stem = leaf.removesuffix("Tests.cs")
            if stem.startswith("WorldMetadata"):
                include(("world-metadata",), (), build=False)
            elif stem == "LocalGameData":
                include(("local-data",), (), build=False)
            elif stem == "FileTaskProgress":
                include(("world-backups", "environment-history", "instance-clone"), (), build=False)
            elif stem == "WorldBackupPipeline":
                include(("world-backups",), (), build=False)
            elif stem == "EnvironmentHistoryPipeline":
                include(("environment-history",), (), build=False)
            elif stem == "InstanceClonePipeline":
                include(("instance-clone",), (), build=False)
            elif stem in {"PackUpdateThreeWay", "PackUpdateMetadata"}:
                include(("pack-update",), (), build=False)
            elif stem == "ModDependencyInspectorPipeline":
                include(("mod-dependencies",), (), build=False)
            elif stem == "ModDependencyOnlinePipeline":
                include(("mod-dependency-online",), (), build=False)
            elif stem in {"LegacySettings", "SettingsMicrosoftIsolation", "UpdateLogPrompt"}:
                include(("settings",), (), build=False)
            elif stem in {"LegacyModFavorites", "ResourceFavorite"}:
                include(("mod-favorites",), (), build=False)
            elif stem == "LegacyValidationLoader":
                include(("validation-loaders",), (), build=False)
            elif stem in {"LegacyFoundation", "LegacyMainTermination"}:
                include(("foundation",), (), build=False)
            elif stem == "LegacyJava":
                include(("legacy-java",), (), build=False)
            elif stem.startswith("PersonalOptions"):
                include(("personal-options",), (), build=False)
            elif stem.startswith("InstanceKeybinding"):
                include(("keybindings",), (), build=False)
            elif stem.startswith("QuickPlay"):
                include(("quick-play",), (), build=False)
            elif stem.startswith("InstanceResourceEntry"):
                include(("resource-entry",), (), build=False)
            elif stem.startswith("InstanceResourcePath"):
                include(("resource-paths",), (), build=False)
            elif stem.startswith(("InstanceResourceDetails", "InstanceResourcePackMetadata", "InstanceResourceMetadata")):
                include(("resource-metadata",), (), build=False)
            elif stem in {"ResourceRecyclePipeline", "InstanceResourceOnline"}:
                include(("resource-files",), (), build=False)
            elif stem in {"InstanceResourceKindRules", "InstanceResourceNameRules", "InstanceResourceFileRules", "InstanceResourcePackageRules", "InstanceResourcePipeline"}:
                include(("resource-files",), (), build=False)
            else:
                unknown.append(name)
        elif name.startswith("scripts/Test-") and leaf.removesuffix(".ps1") in SCRIPTS:
            include((), (leaf.removesuffix(".ps1"),))
        elif name in {"PCLCS/Resources/LegacyModFavorites.cs", "PCLCS/Resources/ResourceFavorites.cs", "PCLCS/Resources/ResourceProjectKey.cs"}:
            include(("mod-favorites",), (), build=False)
        elif name == "PCLCS/Settings/LegacyUpdateLogPrompt.cs":
            include(("settings",), (), build=False)
        elif name in WORLD_BACKUP_PATHS:
            include(("world-backups",), WORLD_BACKUP_CHECKS)
        elif name in ENVIRONMENT_HISTORY_PATHS:
            include(("environment-history",), ENVIRONMENT_HISTORY_CHECKS)
        elif name in INSTANCE_CLONE_PATHS:
            include(("instance-clone",), INSTANCE_CLONE_CHECKS)
        elif name in PACK_UPDATE_SHARED_PATHS:
            include(("pack-update", "environment-history", "instance-clone"), PACK_UPDATE_CHECKS | ENVIRONMENT_HISTORY_CHECKS | INSTANCE_CLONE_CHECKS)
        elif name in PACK_UPDATE_PATHS:
            include(("pack-update",), PACK_UPDATE_CHECKS)
        elif name == PACK_UPDATE_METADATA_PATH:
            include(("pack-update", "mod-dependencies"), PACK_UPDATE_CHECKS | MOD_DEPENDENCY_CHECKS)
        elif name in MOD_DEPENDENCY_PATHS:
            include(("pack-update", "mod-dependencies"), MOD_DEPENDENCY_CHECKS | PACK_UPDATE_CHECKS)
        elif name in MOD_DEPENDENCY_ONLINE_PATHS:
            include(("mod-dependency-online",), MOD_DEPENDENCY_ONLINE_CHECKS)
        elif name == "PCLCS/Platform/Windows/Instances/KeybindingPageController.cs":
            include(("keybindings",), {"Test-LocalGameFeatures", "Test-GameFeatureRegressions", "Test-KeybindingGroups"})
        elif name in {"PCLCS/Platform/Windows/Instances/RecentWorldPageController.cs", "PCLCS/Platform/Windows/Instances/RecentWorldQuickPlay.cs"}:
            # These exact controllers own only the recent-world card chain.
            # PageLaunchRight remains a shared account/launch VB page and keeps
            # the conservative full fallback for future edits outside that chain.
            include(("quick-play",), {"Test-LocalGameFeatures", "Test-GameFeatureRegressions", "Test-RecentWorldCard"})
        elif name in RESOURCE_PAGE_PATHS:
            include(RESOURCE_GROUPS, RESOURCE_PAGE_CHECKS)
        elif name in RESOURCE_IMAGE_PATHS:
            include(RESOURCE_GROUPS, RESOURCE_IMAGE_CHECKS)
        elif name.startswith("PCLCS/Minecraft/") and leaf.endswith(".cs"):
            if leaf.startswith("InstanceResourceOnline"):
                include(RESOURCE_GROUPS, RESOURCE_ONLINE_CHECKS)
            elif leaf in {"ResourceRecycleFiles.cs", "MapDownloadFiles.cs"}:
                include(RESOURCE_GROUPS, RESOURCE_CHECKS)
            elif leaf.startswith("WorldMetadata"):
                include(("world-metadata", "quick-play", "resource-entry", "resource-files"), WORLD_CHECKS)
            elif leaf == "LocalGameDataStore.cs":
                include(("local-data", "personal-options", "keybindings", "quick-play", "world-backups", "environment-history"), LOCAL_CHECKS)
            elif leaf.startswith("PersonalOptions"):
                include(("personal-options", "keybindings"), LOCAL_CHECKS)
            elif leaf.startswith("InstanceKeybinding"):
                include(("keybindings",), {"Test-LocalGameFeatures", "Test-GameFeatureRegressions", "Test-KeybindingGroups"})
            elif leaf.startswith("QuickPlay"):
                include(("quick-play",), {"Test-LocalGameFeatures", "Test-GameFeatureRegressions", "Test-RecentWorldCard"})
            elif leaf.startswith(("InstanceResourceDetails", "InstanceResourcePackMetadata", "LegacyPackMetadataNumber")):
                include(("resource-metadata",), {"Test-InstanceResources", "Test-MapDetails"}, io_oracle=True)
            elif leaf.startswith("InstanceResourceEntry"):
                include(RESOURCE_GROUPS, RESOURCE_CHECKS)
            elif leaf.startswith("InstanceResourcePath") or leaf == "LegacyPathCharacters.cs":
                include((*RESOURCE_GROUPS, "quick-play"), STORE_CHECKS, io_oracle=True)
            elif leaf.startswith("InstanceResource"):
                include(RESOURCE_GROUPS, STORE_CHECKS, io_oracle=True)
            else:
                unknown.append(name)
        elif name == "Plain Craft Launcher 2/Modules/Minecraft/InstanceResourceUpdates.vb":
            include(RESOURCE_GROUPS, RESOURCE_ONLINE_CHECKS)
        elif name in {"Plain Craft Launcher 2/Modules/Minecraft/ResourceRecycleStore.vb", "Plain Craft Launcher 2/Modules/Minecraft/MapDownloadInstaller.vb"}:
            include(RESOURCE_GROUPS, RESOURCE_CHECKS)
        elif name == "Plain Craft Launcher 2/Modules/Minecraft/WorldMetadata.vb":
            include(("world-metadata", "quick-play", "resource-entry", "resource-files"), WORLD_CHECKS)
        elif name == "Plain Craft Launcher 2/Modules/Minecraft/LocalGameData.vb":
            include(("local-data", "personal-options", "keybindings", "quick-play", "world-backups", "environment-history"), LOCAL_CHECKS)
        elif name == "Plain Craft Launcher 2/Modules/Minecraft/PersonalGameSettings.vb":
            include(("personal-options", "keybindings"), LOCAL_CHECKS)
        elif name in {"Plain Craft Launcher 2/Modules/Minecraft/InstanceKeybindings.vb",
                       "Plain Craft Launcher 2/Pages/PageInstance/PageInstanceKeybindings.xaml.vb"}:
            include(("keybindings",), {"Test-LocalGameFeatures", "Test-GameFeatureRegressions", "Test-KeybindingGroups"})
        elif name == "Plain Craft Launcher 2/Modules/Minecraft/QuickPlayWorlds.vb":
            include(("quick-play",), {"Test-LocalGameFeatures", "Test-GameFeatureRegressions", "Test-RecentWorldCard"})
        elif name == "Plain Craft Launcher 2/Modules/Minecraft/InstanceResourceStore.vb":
            include((*RESOURCE_GROUPS, "quick-play", "local-data", "personal-options", "keybindings", "world-metadata"), STORE_CHECKS, io_oracle=True)
        elif name.startswith(("PCLCS/", "Plain Craft Launcher 2/", "MeloongCore")) or name == ".gitmodules":
            unknown.append(name)
        elif is_documentation_path(name) or name in {"LICENCE", "LICENSE", ".gitignore"}:
            pass
        else:
            unknown.append(name)
    if native_only_change:
        reasons.append("native launcher/tooling changes run in native-main.yml without a legacy VB rebuild")
    if cnb_tooling_change:
        reasons.append("CNB release tooling changes run in cnb-checks.yml without network publication")
    if modern_test_change:
        reasons.append("modern WPF test/workflow changes run in modern-wpf.yml")
    if unknown:
        full = True
        reasons.append("unclassified/shared change: " + ", ".join(unknown))
    if full:
        groups, scripts = set(GROUPS), set(SCRIPTS)
        oracle = inventory = product = native = True
    # Any product build retains credential, legacy-update build/handoff and artifact gates in build.yml.
    selected_scripts = [name for name in SCRIPTS if name in scripts]
    return {"full": full, "docs_only": docs_only, "native_run": native, "product_build": product, "migration_run": bool(groups or oracle or inventory),
            "groups": [name for name in GROUPS if name in groups], "windows_scripts": selected_scripts,
            "world_checks": bool(scripts & BULK_CHECKS), "engine_only": "Test-WorldBackups" in scripts,
            "oracle": oracle, "inventory": inventory or full, "reasons": reasons, "changed_paths": paths}


def event_selection(event_file: str) -> tuple[list[str], bool, str]:
    event = json.loads(Path(event_file).read_text(encoding="utf-8"))
    if not isinstance(event, dict):
        raise ValueError("GitHub event must be an object")
    event_name = os.environ.get("GITHUB_EVENT_NAME", "")
    pr = event.get("pull_request")
    if event_name == "workflow_dispatch":
        return [], True, "manual complete checkpoint"
    if event_name not in {"push", "pull_request", "merge_group"}:
        return [], True, "unknown event: complete checkpoint"
    if event_name == "merge_group":
        group = event.get("merge_group")
        if not isinstance(group, dict) or group.get("base_ref") != "refs/heads/main":
            return [], True, "malformed merge queue context: complete checkpoint"
        branch = MAIN_BRANCH
        base, head, separator = group.get("base_sha"), group.get("head_sha"), ".."
    elif event_name == "pull_request":
        if not isinstance(pr, dict) or not isinstance(pr.get("base"), dict) or not isinstance(pr.get("head"), dict):
            return [], True, "malformed PR context: complete checkpoint"
        branch = pr["base"].get("ref")
    else:
        branch = os.environ.get("GITHUB_REF", event.get("ref", "")).removeprefix("refs/heads/")
    if branch not in {MAIN_BRANCH, MIGRATION_BRANCH}:
        return [], True, "unrecognized integration context: complete checkpoint"
    if event_name == "pull_request":
        base, head = pr.get("base", {}).get("sha"), pr.get("head", {}).get("sha")
        # PR comparison includes only the proposed delta, not unrelated base commits.
        separator = "..."
    elif event_name == "push":
        base, head, separator = event.get("before"), event.get("after"), ".."
    if not all(isinstance(value, str) and re.fullmatch(r"[0-9a-fA-F]{40}", value) and set(value) != {"0"} for value in (base, head)):
        return [], True, "missing/initial diff base: complete checkpoint"
    process = subprocess.run(["git", "diff", "--name-only", "--no-renames", "-z", base + separator + head],
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    if process.returncode:
        return [], True, "diff unavailable: complete checkpoint"
    paths = [value.decode("utf-8") for value in process.stdout.split(b"\0") if value]
    if documentation_only(paths):
        return paths, False, "verified documentation-only git diff"
    if branch == MAIN_BRANCH:
        return [], True, "main code, mixed or empty diff: complete product and migration checks"
    return paths, False, "changed paths"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--path", action="append", default=[], help="Changed Git path; repeat for local inspection")
    parser.add_argument("--event-file", help="GitHub event JSON; defaults to GITHUB_EVENT_PATH")
    parser.add_argument("--full", action="store_true", help="Explicit complete checkpoint")
    parser.add_argument("--github-output", help="Append fixed-name GitHub job outputs")
    parser.add_argument("--report", help="Write selected/unselected scope as JSON")
    args = parser.parse_args()
    if args.path:
        paths, full, reason = args.path, args.full, "explicit changed paths"
    elif args.full:
        paths, full, reason = [], True, "explicit complete checkpoint"
    else:
        try:
            paths, full, reason = event_selection(args.event_file or os.environ["GITHUB_EVENT_PATH"])
        except (KeyError, OSError, ValueError, UnicodeError):
            paths, full, reason = [], True, "event unavailable: complete checkpoint"
    plan = choose(paths, full, reason)
    serialized = json.dumps(plan, ensure_ascii=True, indent=2) + "\n"
    if args.report:
        report = Path(args.report)
        report.parent.mkdir(parents=True, exist_ok=True)
        report.write_text(serialized, encoding="utf-8")
    if args.github_output:
        with Path(args.github_output).open("a", encoding="utf-8", newline="\n") as output:
            for name in ("full", "docs_only", "native_run", "product_build", "migration_run", "groups", "windows_scripts", "world_checks", "engine_only", "oracle", "inventory"):
                output.write(name + "=" + json.dumps(plan[name], separators=(",", ":")) + "\n")
    print(serialized, end="")
    return 0


if __name__ == "__main__":
    sys.exit(main())
