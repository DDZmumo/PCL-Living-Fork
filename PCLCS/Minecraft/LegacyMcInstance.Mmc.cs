using System;
using System.IO;
using MeloongCore;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
public partial class LegacyMcInstance<TSelf, TState, TCard>
    where TSelf : LegacyMcInstance<TSelf, TState, TCard>
    where TState : struct, Enum where TCard : struct, Enum {
    // Presentation snapshot from Load/Check only. Operations still inspect the
    // current durable management identity before choosing a writable backend.
    public bool UsesMmcBackend { get; private set; }

    private bool ApplyManagedView(MmcInstanceInspection snapshot) {
        UsesMmcBackend = !snapshot.IsLegacy;
        // A damaged launch projection does not invalidate verified identity-bound
        // presentation preferences. Read them before the repair-required return.
        if (snapshot.Management.State is MmcManagementState.ManagedMmc or MmcManagementState.ManagedMmcRepairRequired &&
            snapshot.Management.Envelope is { } envelope && Config.ContextStamp.Kind == MmcConfigurationContextKind.Managed &&
            Config.ContextStamp.InstanceId == envelope.InstanceId) {
            try {
                MmcIniConfiguration.WithInstanceContext(Config, () => {
                    IsStar = bool.TryParse(Ini("IsStar", "False"), out bool star) && star;
                    CardCode = int.TryParse(Ini("DisplayType", "0"), out int card) ? card : 0;
                });
            } catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException) {
                Logger.Warn(error, "读取 MMC 实例展示偏好失败");
            }
        }
        if (!snapshot.IsConsistent) {
            StateCode = 0;
            Info = snapshot.Error ?? "MMC 环境尚未通过校验，请恢复或修复实例。";
            Logo = host.ImagePath() + "Blocks/RedstoneBlock.png";
            return false;
        }
        try {
            JsonText = snapshot.ProjectionJson!;
            JsonObject = JObject.Parse(snapshot.ProjectionJson!);
            Version = new McVersion(snapshot.Manifest!.Components);
            StateCode = Version.ModLoaders.HasFlag(ModLoaders.Fabric) ? 9 :
                Version.ModLoaders.HasFlag(ModLoaders.Forge) ? 6 :
                Version.ModLoaders.HasFlag(ModLoaders.NeoForge) ? 7 : 1;
            Info = VersionDisplayName() + ", MMC";
            // Read preferences with the fixed instance identity; no ordinary
            // manifest refresh or isolation migration is allowed on this path.
            MmcIniConfiguration.WithInstanceContext(Config, () => {
                string? custom = Ini("CustomInfo");
                if (!string.IsNullOrEmpty(custom)) Info = custom;
                Logo = host.ReadVersionLogo(PathVersion + "PCL\\Setup.ini", "");
            });
            if (string.IsNullOrEmpty(Logo)) Logo = host.ImagePath() + (StateCode == 6 ? "Blocks/Anvil.png" : StateCode == 9 ? "Blocks/Fabric.png" : "Blocks/Grass.png");
            return true;
        } catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or Newtonsoft.Json.JsonException) {
            StateCode = 0; Info = "MMC 实例读取失败：" + error.Message;
            Logo = host.ImagePath() + "Blocks/RedstoneBlock.png";
            return false;
        }
    }
}
