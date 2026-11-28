using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCL;

// MMC uses the existing pack page and rows, with its own measured-origin and ZIP transaction authority.
public partial class PageInstancePackUpdate {
    private bool mmcCaptured, mmcReady;
    private ModMinecraft.McInstance mmcOwner;
    private string mmcRoot, mmcVersion, mmcMinecraft, mmcStateMessage;
    private Guid mmcInstanceId;
    private MmcInstanceInspection mmcSource;
    private MmcPlatformOriginEvidence mmcVerified;
    private MmcPlatformOriginCatalog mmcCatalog;
    private readonly Dictionary<ResourceVersion, MmcPlatformReleaseDescriptor> mmcReleases = new();
    private CancellationTokenSource mmcRead;
    private Task mmcRefreshCompletion = Task.CompletedTask;
    internal Task MmcReadCompletion => mmcRefreshCompletion;
    internal Func<MmcPlatformOriginCatalog> ReadMmcCatalog = ModNet.CreateMmcPlatformOriginCatalog;
    internal Func<MmcPlatformReleaseDescriptor, int> ConfirmMmcVersion = null;

    public PageInstancePackUpdate(ModMinecraft.McInstance managedOwner, Guid expectedInstanceId) : this() => CaptureMmcOwner(managedOwner, expectedInstanceId);

    private void CaptureMmcOwner(ModMinecraft.McInstance owner, Guid instanceId) {
        if (mmcCaptured) throw new InvalidOperationException("整合包更新页面已经绑定实例。");
        mmcOwner = owner ?? throw new ArgumentNullException(nameof(owner));
        mmcRoot = Path.GetFullPath(owner.PathVersion); mmcInstanceId = instanceId;
        mmcCaptured = true; displayed = owner;
        mmcReady = false;
        BtnOnline.IsEnabled = BtnLocal.IsEnabled = BtnBackups.IsEnabled = BtnOrigin.IsEnabled = false;
        BtnOrigin.Visibility = Visibility.Visible;
        BtnBackups.Content = "环境记录";
    }
    internal bool IsManagedOwner(ModMinecraft.McInstance owner, Guid instanceId) => mmcCaptured && ReferenceEquals(owner, mmcOwner)
        && instanceId == mmcInstanceId && MeloongCore.PathUtils.IsSame(owner.PathVersion, mmcRoot);
    private bool MmcLocationMatches() => ReferenceEquals(LauncherInstanceContext.Instance, mmcOwner)
        && MeloongCore.PathUtils.IsSame(mmcOwner.PathVersion, mmcRoot);
    private bool MmcOwnerCurrent() {
        if (!mmcReady || mmcSource is null || !MmcLocationMatches() || mmcInstanceId == Guid.Empty) return false;
        try { return MmcPackageUpdateUi.ReadCapturedSource(mmcOwner, MmcPackageUpdateUi.Selection(mmcSource)).Definition.InstanceId == mmcInstanceId; }
        catch { return false; }
    }
    private void CancelMmcRead() {
        var reading = Interlocked.Exchange(ref mmcRead, null);
        try { reading?.Cancel(); } catch (ObjectDisposedException) { }
    }
    private async Task RefreshMmcAsync(int current) {
        var cancellation = new CancellationTokenSource(); mmcRead = cancellation;
        mmcReady = false; mmcSource = null; mmcVerified = null; record = null;
        mmcReleases.Clear(); catalog.Clear(); catalogError = null; catalogLoading = true;
        BtnOnline.IsEnabled = BtnLocal.IsEnabled = BtnBackups.IsEnabled = BtnOrigin.IsEnabled = false;
        LabName.Text = mmcOwner.Name; LabVersion.Text = "正在读取…"; LabSource.Text = "MMC / Prism 整合包";
        mmcStateMessage = "正在核对当前 MMC 实例…"; RenderVersions();
        try {
            if (!MmcLocationMatches()) { ApplyMmcBlocked("当前页面对应的实例已变更，请重新进入实例设置。"); return; }
            LoadMmcLogo();
            var snapshot = await Task.Run(() => {
                cancellation.Token.ThrowIfCancellationRequested();
                var state = InstanceSidebarContext.Observe(mmcRoot, preserveMmcFamily: true);
                if (!state.IsConsistent || mmcInstanceId == Guid.Empty || state.InstanceId != mmcInstanceId)
                    return (State: state, Source: (MmcInstanceInspection)null, Verified: (MmcPlatformOriginEvidence)null);
                var source = MmcPackageUpdateUi.ReadManagedSource(mmcOwner);
                if (source.Definition.InstanceId != mmcInstanceId) throw new IOException("此目录中的实例已被替换，请重新选择实例。");
                return (State: state, Source: source, Verified: MmcInstancePlatformOriginBackend.ReadVerified(MmcPackageUpdateUi.Selection(source), cancellation.Token));
            }, cancellation.Token).ConfigureAwait(false);
            if (Dispatcher.HasShutdownStarted) return;
            Task listing = Task.CompletedTask;
            await Dispatcher.InvokeAsync(() => {
                if (current != revision || cancellation.IsCancellationRequested) return;
                if (!MmcLocationMatches()) { ApplyMmcBlocked("当前页面对应的实例已变更，请重新进入实例设置。"); return; }
                if (snapshot.State.InstanceId != mmcInstanceId || mmcInstanceId == Guid.Empty) {
                    ApplyMmcBlocked("实例身份无法匹配。当前页面不会接管此目录中的另一个实例，请重新选择实例。"); return;
                }
                if (snapshot.Source is null) {
                    string reason = snapshot.State.Family switch {
                        InstanceSidebarFamily.MmcRepairRequired => "实例配置校验未通过，请先到“组件与补丁”修复实例。",
                        InstanceSidebarFamily.MmcPendingTransaction => "实例有待恢复的操作，请完成恢复后再更新整合包。",
                        InstanceSidebarFamily.MmcUnsupported => "实例管理格式高于当前启动器支持范围，暂不能更新。",
                        _ => "无法确认实例管理状态，请重新检查实例。"
                    };
                    ApplyMmcBlocked(reason); return;
                }
                // The read may have completed after an external replacement or structural change.
                try { _ = MmcPackageUpdateUi.ReadCapturedSource(mmcOwner, MmcPackageUpdateUi.Selection(snapshot.Source)); }
                catch (Exception error) { ApplyMmcBlocked(error.Message); return; }
                mmcSource = snapshot.Source; mmcVerified = snapshot.Verified; mmcReady = true; catalogLoading = false;
                mmcVersion = mmcVerified?.Identity.VersionId ?? Path.GetFileNameWithoutExtension(mmcSource.Definition.Origin.OriginalName);
                mmcMinecraft = mmcSource.Manifest.Components.MinecraftVersion?.ToString() ?? "";
                LabVersion.Text = string.IsNullOrWhiteSpace(mmcVersion) ? "未记录整合包版本" : mmcVersion;
                LabSource.Text = mmcVerified is null ? "MMC / Prism 本地整合包 · 平台来源未核实" : mmcVerified.Identity.Platform + " · 项目 " + mmcVerified.Identity.ProjectId + " · 来源已核实";
                if (mmcMinecraft.Length > 0) LabSource.Text += " · Minecraft " + mmcMinecraft;
                BtnOrigin.Content = mmcVerified is null ? "核实平台来源" : "重新核实来源";
                BtnLocal.IsEnabled = BtnBackups.IsEnabled = BtnOrigin.IsEnabled = true;
                BtnOnline.IsEnabled = mmcVerified is not null;
                GlassDialogControls.UpdateNotice(HintState, mmcVerified is null
                    ? "选择最初导入的原 ZIP 核实平台来源后，可在此选择同一项目的在线版本；也可直接从新版 MMC / Prism ZIP 更新。"
                    : "在线文件将核对同一平台项目、下载内容和 MMC 容器；确认前可预览文件与组件变更，个人存档会受到保护。",
                    mmcVerified is null ? DialogTone.Warning : DialogTone.Information);
                RenderVersions();
                if (mmcVerified is not null) listing = LoadMmcVersionsAsync(current, cancellation.Token);
            });
            await listing.ConfigureAwait(false);
        } catch (OperationCanceledException) { }
        catch (Exception error) {
            if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() => {
                if (current == revision && !cancellation.IsCancellationRequested) ApplyMmcBlocked("读取 MMC 实例失败：" + error.Message);
            });
        } finally {
            Interlocked.CompareExchange(ref mmcRead, null, cancellation);
            cancellation.Dispose();
        }
    }
    private void LoadMmcLogo() {
        try {
            string logo = mmcOwner.Logo;
            if (!string.IsNullOrWhiteSpace(logo) && !Path.IsPathRooted(logo) && !Uri.TryCreate(logo, UriKind.Absolute, out _))
                logo = Path.GetFullPath(Path.Combine(mmcRoot, logo));
            ImgPack.Source = string.IsNullOrWhiteSpace(logo) ? null : (ImageSource)new MyBitmap(logo);
        } catch { ImgPack.Source = null; }
        if (ImgPack.Source is null) try { ImgPack.Source = (ImageSource)new MyBitmap(ModBase.PathImage + "Blocks/Grass.png"); } catch { }
    }
    private async Task LoadMmcVersionsAsync(int current, CancellationToken token) {
        catalogLoading = true; BtnOnline.IsEnabled = false; RenderVersions();
        try {
            mmcCatalog ??= ReadMmcCatalog();
            var service = mmcCatalog; var selection = MmcPackageUpdateUi.Selection(mmcSource);
            var releases = await new MmcInstancePlatformOriginBackend(service).GetAvailableReleasesAsync(selection, token).ConfigureAwait(false);
            if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() => {
                if (current != revision || !MmcOwnerCurrent()) return;
                catalog.Clear(); mmcReleases.Clear();
                foreach (var release in releases) {
                    var display = release.Display;
                    var version = new ResourceVersion { Id = release.VersionId,
                        Display = string.IsNullOrWhiteSpace(display?.Name) ? release.File.FileName : display.Name,
                        Version = string.IsNullOrWhiteSpace(display?.Version) ? release.VersionId : display.Version,
                        ReleaseTypeCode = display?.ReleaseTypeCode ?? 0, ReleaseDate = display?.Published ?? DateTime.MinValue,
                        GameVersions = display?.GameVersions.ToList() ?? new() };
                    catalog.Add(version); mmcReleases.Add(version, release);
                }
                var installed = catalog.FirstOrDefault(IsCurrentMmcRelease);
                if (installed is not null) LabVersion.Text = mmcVersion = installed.Version ?? installed.Display ?? mmcVersion;
                catalogError = null;
            });
        } catch (OperationCanceledException) { }
        catch (Exception) {
            if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() => {
                if (current == revision && MmcOwnerCurrent()) catalogError = "平台版本列表暂不可用，请重新检查更新。";
            });
        } finally {
            if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() => {
                if (current != revision) return;
                catalogLoading = false;
                if (!MmcOwnerCurrent()) { ApplyMmcBlocked("实例状态已变化，请刷新页面后重新选择。"); return; }
                BtnOnline.IsEnabled = true; RenderVersions();
            });
        }
    }
    private void ApplyMmcBlocked(string message) {
        mmcReady = false; mmcSource = null; mmcVerified = null; mmcStateMessage = message;
        catalogLoading = false; catalogError = null; catalog.Clear(); mmcReleases.Clear();
        BtnOnline.IsEnabled = BtnLocal.IsEnabled = BtnBackups.IsEnabled = BtnOrigin.IsEnabled = false;
        LabVersion.Text = "暂不可更新"; GlassDialogControls.UpdateNotice(HintState, message, DialogTone.Warning);
        RenderVersions();
    }
    private bool IsCurrentMmcRelease(ResourceVersion version) => mmcVerified is not null && mmcReleases.TryGetValue(version, out var release)
        && release.Platform.ToString() == mmcVerified.Identity.Platform && release.ProjectId == mmcVerified.Identity.ProjectId
        && release.VersionId == mmcVerified.Identity.VersionId && release.File == mmcVerified.SelectedFile;
    private string MmcReleaseSearchText(ResourceVersion version) => mmcReleases.TryGetValue(version, out var release) ? " " + release.VersionId + " " + release.File.FileName : "";
    private string MmcReleaseInfo(ResourceVersion version, bool current) {
        if (!mmcReleases.TryGetValue(version, out var release)) return "";
        string date = version.ReleaseDate == DateTime.MinValue ? "发布日期未提供" : version.ReleaseDate.ToString("yyyy-MM-dd");
        string games = string.Join("、", version.GameVersions ?? new());
        return (current ? "当前已安装版本" : date) + (games.Length == 0 ? "" : " · Minecraft " + games) + " · " + release.File.FileName + " · " + MmcPlatformReleaseSelectView.Size(release.File.SizeBytes);
    }
    private string MmcListStatus(string channel) => !mmcReady ? mmcStateMessage : catalogLoading ? "正在读取已核实项目的发布文件…"
        : catalogError ?? (mmcVerified is null ? "平台来源尚未核实，可核实最初导入的原 ZIP，或从新版 MMC / Prism ZIP 更新。"
            : "已获取 " + catalog.Count + " 个发布文件，" + channel + "显示 " + PanVersions.Children.Count + " 个；选择文件后将下载并预览更新。");
    private bool TryMmcSelection(out MmcPlatformInstanceSelection selection) {
        selection = null;
        if (!MmcOwnerCurrent()) { ApplyMmcBlocked("实例状态已变化，请刷新页面后重新选择。"); return false; }
        selection = MmcPackageUpdateUi.Selection(mmcSource); return true;
    }
    private void BindMmcOrigin() {
        if (!TryMmcSelection(out var selection)) return;
        try {
            mmcCatalog ??= ReadMmcCatalog();
            ObserveMmcOperation(MmcPlatformOriginUi.PromptBindingCaptured(mmcOwner, mmcCatalog, selection, MmcOwnerCurrent));
        } catch (Exception error) { ShowMmcActionError(error); }
    }
    private void UpdateMmcLocal() {
        if (!TryMmcSelection(out var selection)) return;
        try { ObserveMmcOperation(MmcPackageUpdateUi.PromptCaptured(mmcOwner, selection, MmcOwnerCurrent)); }
        catch (Exception error) { ShowMmcActionError(error); }
    }
    private void OpenMmcHistory() {
        if (!TryMmcSelection(out _)) return;
        try { MmcHistoryUi.Prompt(mmcOwner); }
        catch (Exception error) { ShowMmcActionError(error); }
    }
    private void ChooseMmcVersion(ResourceVersion version, int rowRevision) {
        if (rowRevision != revision || !TryMmcSelection(out var selection) || !mmcReleases.TryGetValue(version, out var release)) return;
        try {
            int decision = ConfirmMmcVersion is not null ? ConfirmMmcVersion(release) : ModMain.MyMsgBox(
                "版本：" + (version.Display ?? version.Version ?? release.VersionId) + "\r\n发布 ID：" + release.VersionId + "\r\n文件：" + release.File.FileName +
                "\r\n\r\n将核对发布文件和 MMC 整合包内容，下载后预览文件与组件变更。", "更新整合包", "下载并预览", "取消");
            if (decision == 1 && rowRevision == revision && MmcOwnerCurrent())
                ObserveMmcOperation(MmcPackageUpdateUi.StartOnlineSelected(mmcOwner, mmcCatalog, release, selection));
        } catch (Exception error) { ShowMmcActionError(error); }
    }
    private void ShowMmcActionError(Exception error) {
        GlassDialogControls.UpdateNotice(HintState, error.Message, DialogTone.Warning);
    }
    private async void ObserveMmcOperation(ModLoader.LoaderCombo<string> operation) {
        if (operation is null) return;
        int current = revision;
        try {
            while (operation.State is ModBase.LoadState.Loading or ModBase.LoadState.Waiting || MmcPackageUpdateUi.HasActiveOperation)
                await Task.Delay(100).ConfigureAwait(false);
            if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() => {
                if (current == revision && MmcLocationMatches()) Refresh();
            });
        } catch (OperationCanceledException) { }
    }
}
