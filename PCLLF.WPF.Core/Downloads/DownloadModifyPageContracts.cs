using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace PCLCS;

/// <summary>Direct access to the existing Modify page fields and its original dictionary. No mirrored selection.</summary>
public abstract class DownloadModifyPageState<TLoader, TLoading> where TLoader : class where TLoading : class {
    public abstract TLoader? ForgeVersionLoader { get; set; }
    public abstract McComponents? InitialTarget { get; set; }
    public abstract bool IsVanillaVersionReliable { get; set; }
    public abstract string? OriginalName { get; set; }
    public abstract string? CustomLogo { get; set; }
    public abstract Dictionary<TLoading, string> PendingComponents { get; }
    public abstract bool IsRestoringComponents { get; set; }
}
public sealed class DownloadModifyTask<TInstance> {
    public Func<TInstance> Input = null!;
    public Func<object?> Output = null!;
    public Action<object?> SetOutput = null!;
    public Func<bool> Canceled = null!, ForceRestart = null!;
    public Action<double> Progress = null!;
}
public sealed class DownloadModifyComparison {
    public FrameworkElement Card = null!, Restore = null!, Current = null!;
    public Func<FrameworkElement?> MainSwap = null!;
    public FrameworkElement? Clear;
    public Grid Compare = null!;
    public TextBlock Label = null!, OldLabel = null!;
    public Action<bool> SetSwapped = null!;
}
public sealed class DownloadModifyPageView {
    public Panel Select = null!;
    public FrameworkElement SelectNameCard = null!, AllBack = null!, Back = null!, Minecraft = null!, BackButton = null!, Logo = null!, ChangeMinecraft = null!;
    public FrameworkElement Start = null!, NameRestore = null!;
    public TextBox Name = null!;
    public TextBlock NameLabel = null!, MinecraftVersion = null!;
    public Action ScrollHome = null!;
    public Action<string?> MinecraftOldLogo = null!, MinecraftNewLogo = null!;
    public Func<bool> NameValidated = null!;
    public Action<string> ValidateName = null!, StartText = null!;
    public Action SetStartLogo = null!;
}
public sealed class DownloadModifyAnimation<TAnimation> {
    public DownloadPageAnimationHost<TAnimation> Native = null!;
    public Func<Action<double>, double, double, int, DownloadPageEase, TAnimation> Double = null!;
}
public sealed class DownloadModifyPageHost<TOptiFine, TForge, TNeo, TLite, TResource, TRequest, TInstance, TLoader, TLoading>
    where TOptiFine : class where TForge : class where TNeo : class where TLite : class where TResource : class
    where TInstance : class where TLoader : class where TLoading : class {
    public DownloadInstallSelection<TOptiFine, TForge, TNeo, TLite, TResource> Selection = null!;
    public DownloadModifyPageState<TLoader, TLoading> State = null!;
    public Func<DownloadModifyPageView> View = null!;
    public Func<int, DownloadModifyComparison> Comparison = null!;
    public Func<Grid, DownloadModifyComparison> ComparisonCard = null!;
    public Func<WrapPanel?> PackButtons = null!;
    public Func<TextBlock?> PackName = null!, PackInfo = null!;
    public Action<WrapPanel> SetPackButtons = null!;
    public Action<TextBlock> SetPackName = null!, SetPackInfo = null!;
    public Func<string, Thickness, Panel> Card = null!;
    public Func<string, Action, FrameworkElement> UpdateButton = null!;
    public Action<bool> StartPackUpdate = null!;
    public Action OpenPackBackups = null!;
    public Action BaseControlInit = null!, InitializeLoader = null!, Restart = null!, Cancel = null!;
    public Action BaseClearSelected = null!, BaseReloadSelected = null!, BaseShowMinecraftList = null!, BasePrepareSelection = null!, BaseReloadLists = null!;
    public Func<string?> BaseLogo = null!, SelectName = null!;
    public Func<McComponents> Components = null!;
    public Action<JObject> SelectMinecraft = null!;
    public Func<JObject, FrameworkElement> MinecraftRow = null!;
    public Func<FrameworkElement, string?> RowTitle = null!, RowLogo = null!;
    public Func<TInstance> InitInput = null!;
    public Action<Action> UiWait = null!;
    public Func<string, JObject?> DetectPack = null!;
    public Func<string, TInstance> NewInstance = null!;
    public Func<Dictionary<string, TLoader>> CatalogLoaders = null!;
    public Func<string, TLoader> CreateForgeLoader = null!;
    public MinecraftInstallStates States = null!;
    public Action<TLoader, object?, bool> StartLoader = null!;
    public Func<TLoader, int> LoaderState = null!;
    public Func<TLoader, Exception> LoaderError = null!;
    public Func<TLoader, double> LoaderProgress = null!;
    public Func<TLoader, string?> ForgeInput = null!;
    public Func<TLoader, List<TForge>?> ForgeOutput = null!;
    public Action<TLoader> BindForge = null!;
    public Action<FrameworkElement, bool> SwapRestoreParent = null!;
    public Func<DownloadInstallComponent, TLoading> LoadingControl = null!;
    public Func<TLoading, bool> IsLoader = null!;
    public Func<TLoading, TLoader> ReadLoader = null!;
    public Func<DownloadInstallComponent, string?> ComponentError = null!;
    public Func<JObject> ClientOutput = null!, FabricOutput = null!, OptiFineHashes = null!;
    public Func<List<TOptiFine>> OptiFineOutput = null!;
    public Func<List<TNeo>> NeoForgeOutput = null!;
    public Func<List<TLite>> LiteLoaderOutput = null!;
    public Func<List<TResource>> FabricApiOutput = null!, OptiFabricOutput = null!;
    public Func<bool> OptiFineMirrorFinished = null!;
    public Action WaitOptiFineMirror = null!, CopyOptiFineMirror = null!;
    public Func<FileInfo, string> LocalSha1 = null!;
    public Func<IEnumerable<string>, string, int?> ChooseMod = null!;
    public Func<string, string, bool> ReadIniBoolean = null!;
    public Func<bool> ModpackEnabled = null!;
    public Func<string?, string?, bool> CanChangeMinecraft = null!;
    public Func<string> MinecraftFolder = null!;
    public Func<Brush> Gray4 = null!;
    public Action<string, SelectionPageHint> Hint = null!;
    public Func<SelectionPageMessage, int> Message = null!;
    public Func<TRequest, TInstance, bool> Modify = null!;
    public Func<TInstance?> InstancePage = null!, SelectedInstance = null!;
    public Action<TInstance> SetInstancePage = null!, SetSelectedInstance = null!;
    public Action ReloadJava = null!, RefreshInstances = null!, PageBack = null!;
}
