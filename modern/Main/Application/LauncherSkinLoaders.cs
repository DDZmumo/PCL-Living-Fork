// SPDX-License-Identifier: MPL-2.0
using System.Collections.Generic;
using System.Threading;
using SkinLoader = PCL.ModLoader.LoaderTask<PCLLF.Presentation.Launch.LegacySkinRequest, string>;

namespace PCL;

internal static class LauncherSkinLoaders
{
    internal static SkinLoader SkinMs = new("Loader Skin Ms", task => LauncherSkinSessions.Skins.Official(task, "Ms"),
        () => LauncherSkinSessions.Skins.Input("Ms"), ThreadPriority.AboveNormal);
    internal static SkinLoader SkinLegacy = new("Loader Skin Legacy", task => LauncherSkinSessions.Skins.Offline(task),
        () => LauncherSkinSessions.Skins.Input("Legacy"), ThreadPriority.AboveNormal);
    internal static SkinLoader SkinNide = new("Loader Skin Nide", task => LauncherSkinSessions.Skins.Official(task, "Nide"),
        () => LauncherSkinSessions.Skins.Input("Nide"), ThreadPriority.AboveNormal);
    internal static SkinLoader SkinAuth = new("Loader Skin Auth", task => LauncherSkinSessions.Skins.Official(task, "Auth"),
        () => LauncherSkinSessions.Skins.Input("Auth"), ThreadPriority.AboveNormal);

    internal static List<SkinLoader> All = [SkinMs, SkinLegacy, SkinNide, SkinAuth];
}
