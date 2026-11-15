using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;

namespace PCLCS;

public sealed class DownloadNameValidator {
    public Func<string?, string> Validate = null!;
    public Func<string?, string?> Prompt = null!;
}
public sealed class ResourceDetailPageView {
    public Panel Filter = null!, Results = null!, Intro = null!;
    public FrameworkElement FilterCard = null!, Alternative = null!, Wiki = null!;
    public Action<string> AlternativeText = null!, WebText = null!;
    public Action ScrollHome = null!;
}
public sealed class ResourceDetailPageHost<TProject, TVersion, TCard, TResourceItem>
    where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData
    where TCard : FrameworkElement where TResourceItem : FrameworkElement {
    public Func<ResourceDetailPageView> View = null!;
    public Func<int, object?> Argument = null!;
    public Func<object[]?> Arguments = null!;
    public Action<int, object> SetArgument = null!;
    public Func<bool> HasArguments = null!, Failed = null!;
    public Func<Exception?> Error = null!;
    public Func<List<TVersion>> Files = null!;
    public Func<string?, ResourcePlatforms, List<TVersion>> ProjectFiles = null!;
    public Action Initialize = null!;
    public Action<bool> Restart = null!;
    public Func<string, Action<string>, FrameworkElement> FilterButton = null!;
    public Func<FrameworkElement, string> FilterText = null!;
    public Action<FrameworkElement, bool> FilterChecked = null!;
    public Func<double> SwappedHeight = null!;
    public Func<string, StackPanel, TCard> Card = null!;
    public Action<TCard, Action<StackPanel>> SetBuilder = null!;
    public Func<TCard, string> CardTitle = null!;
    public Action<TCard> BuildCard = null!;
    public Action<TCard, bool> SwapCard = null!;
    public Func<string, bool> HasProject = null!;
    public Func<string, TProject> CachedProject = null!;
    public Func<TProject, FrameworkElement> ProjectRow = null!;
    public Func<string, Thickness, FrameworkElement> YellowHint = null!;
    public Func<FrameworkElement, bool> IsVersionRow = null!;
    public Func<TVersion, Action<FrameworkElement, EventArgs>, Action<object, EventArgs>?, bool, FrameworkElement> VersionRow = null!;
    public Func<TProject, TResourceItem> Header = null!;
    public Action<TResourceItem, bool> HeaderInteractive = null!;
    public Func<TResourceItem, string> HeaderLogo = null!;
    public Func<TResourceItem, (string Title, string Raw)> HeaderName = null!;
    public Action<string> Website = null!, Clipboard = null!;
}
public sealed class ResourceDetailDownloadHost<TLoader, TNetFile, TVersion, TInstance>
    where TLoader : class where TNetFile : class where TVersion : ResourceVersionModelData where TInstance : class {
    public IMinecraftInstallLoaders<TLoader, TNetFile> Loaders = null!;
    public MinecraftInstallStates States = null!;
    public Dictionary<ResourceTypes, string> CachedFolders = null!;
    public Func<string> MinecraftFolder = null!;
    public Func<TInstance?> SelectedInstance = null!;
    public Func<IEnumerable<TInstance>> Instances = null!;
    public Func<bool> InstanceListFinished = null!;
    public Action LoadInstanceList = null!, RefreshDownload = null!, RibbleDownload = null!, TaskManager = null!, RefreshCurrentResources = null!;
    public Func<int> TranslateSetting = null!;
    public Func<string, DownloadNameValidator> NameValidator = null!;
    public Func<TVersion, string, ResourceDownloadReason, string?, ModLoaders, TNetFile> NetFile = null!;
    public Action<string, string, string?, Newtonsoft.Json.Linq.JObject> InstallPack = null!;
    public Func<string, string> MapDownloadFolder = null!;
    public Func<string, string, CancellationToken, string> InstallMap = null!;
    public Action<TLoader, bool> InterruptOnCancel = null!;
    public Action<TLoader> StateHint = null!, ClearFailedFolder = null!;
    public Action<string, int> Hint = null!;
    public Action<ResourceProjectModelData, TVersion, string, ResourceDownloadReason, TInstance?>? InstallMod;
    public Func<string?, string?, string, string?, string?> SaveFile = null!;
    public Action<string> Explorer = null!;
    public Action<Action, string> RunInNewThread = null!;
    public Action<Action> RunInUi = null!;
}
