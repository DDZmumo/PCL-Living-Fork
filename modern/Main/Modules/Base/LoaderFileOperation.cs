using System;
using MeloongCore;
using PCLCS.Loaders;

namespace PCL
{

    // 文件事务独占范围由 C# 真实负载计数控制；旧调用入口保持不变。
    internal sealed class LoaderFileOperation
    {
        internal static Action CreateWorkload(ModLoader.LoaderCombo<string> Loader, string Input)
        {
            return CreateWorkloadWithProgress(Loader, Input, null);
        }
        internal static Action CreateWorkloadWithProgress(ModLoader.LoaderCombo<string> Loader, string Input, ProgressProvider Progress)
        {
            return LegacyLoaderFileOperation.CreateWorkload<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState>(Loader, Input, Progress);
        }
    }
}