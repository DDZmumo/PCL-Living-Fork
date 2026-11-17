using System;
using System.IO;
using System.Threading.Tasks;
using PCLCS;

namespace PCL;

// Original LF integration; acquisition joins the existing ZIP update worker and review/undo flow.
internal static partial class MmcPackageUpdateUi {
    internal static ModLoader.LoaderCombo<string> StartOnline(ModMinecraft.McInstance instance, MmcPlatformOriginCatalog catalog) =>
        StartOnlineSelected(instance, catalog, null, null);
    internal static ModLoader.LoaderCombo<string> StartOnlineSelected(ModMinecraft.McInstance instance, MmcPlatformOriginCatalog catalog,
        MmcPlatformReleaseDescriptor selectedRelease, MmcPlatformInstanceSelection expected) {
        ArgumentNullException.ThrowIfNull(catalog); var source = ReadCapturedSource(instance, expected);
        if (ModMinecraft.McInstanceLock.IsLocked(instance)) throw new IOException("该实例正在被修改，请稍后再更新。");
        var services = ServicesFactory?.Invoke(instance) ?? CreateServices(source.Paths.InstanceRoot);
        return StartPrepared(instance, source, services, "读取已核实项目的官方发布文件", (backend, task, token) => {
            var selection = new MmcPlatformInstanceSelection(source.Paths.InstanceRoot, source.Definition.InstanceId, source.Definition.Generation, source.PrimarySha256);
            var selected = selectedRelease;
            if (selected is null) {
                var origin = new MmcInstancePlatformOriginBackend(catalog);
                var releases = origin.GetAvailableReleasesAsync(selection, token).GetAwaiter().GetResult();
                token.ThrowIfCancellationRequested(); task.Name = "等待选择 MMC 在线发布文件";
                selected = ModBase.RunInUiWait(() => {
                    token.ThrowIfCancellationRequested();
                    return MmcPlatformOriginUi.ChooseRelease(releases, "选择 MMC 在线更新文件", source.Definition.Origin.PlatformProjectIdentity?.VersionId);
                });
            }
            if (selected is null) { task.Cancel(); throw new OperationCanceledException(token); }
            token.ThrowIfCancellationRequested(); task.Name = "下载并核对所选 MMC 整合包";
            var online = new MmcInstancePlatformOnlineBackend<ModNet.NetFile>(catalog, backend, services.CacheRoots, MmcDownloads.BuildFile,
                (files, cancellation) => MmcDownloads.RunBatchAsync(files, cancellation, onLoaderCreated: services.DownloadCreated));
            return online.PrepareAsync(selection, selected, (archive, cancellation) => {
                task.Name = "读取新版 MMC 的组件和 Java 要求";
                return Task.FromResult(ResolveEnvironment(archive, services.Metadata, task, cancellation));
            }, token, new Progress(task, services.ProgressObserved)).GetAwaiter().GetResult();
        });
    }
}
