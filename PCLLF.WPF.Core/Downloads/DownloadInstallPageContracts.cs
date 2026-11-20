using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public enum DownloadInstallComponent { OptiFine, LiteLoader, Forge, NeoForge, Fabric, FabricApi, OptiFabric }
/// <summary>Direct access to original public page fields, including the shared waiting selection. No state copies.</summary>
public abstract class DownloadInstallSelection<TOptiFine, TForge, TNeo, TLite, TResource>
    where TOptiFine : class where TForge : class where TNeo : class where TLite : class where TResource : class {
    public abstract bool IsInSelectPage { get; set; }
    public abstract string? VanillaName { get; set; }
    public abstract JObject? VanillaData { get; set; }
    public abstract string? VanillaIcon { get; set; }
    public abstract TOptiFine? SelectedOptiFine { get; set; }
    public abstract TLite? SelectedLiteLoader { get; set; }
    public abstract TForge? SelectedForge { get; set; }
    public abstract TNeo? SelectedNeoForge { get; set; }
    public abstract string? SelectedFabric { get; set; }
    public abstract TResource? SelectedFabricApi { get; set; }
    public abstract TResource? SelectedOptiFabric { get; set; }
    public abstract bool IsSelectNameEdited { get; set; }
    public abstract JArray? MinecraftVersions { get; set; }
    public abstract string? VersionWaitingSelect { get; set; }
    public abstract bool AutoSelectedFabricApi { get; set; }
    public abstract bool AutoSelectedOptiFabric { get; set; }
}
public sealed class DownloadInstallLoadingView {
    public Func<bool> IsLoader = null!;
    public Func<int> LoadingState = null!;
    public Func<Exception> Error = null!;
}
public sealed class DownloadInstallSource<TOutput> where TOutput : class {
    public Func<bool> Finished = null!;
    public Func<TOutput?> Output = null!;
    public Action Start = null!;
}
public sealed class DownloadInstallForgeSource<TForge> {
    public Func<string?> Input = null!;
    public Func<List<TForge>> Output = null!;
    public Func<bool> Finished = null!;
}
public sealed class DownloadInstallComponentView {
    public FrameworkElement Card = null!, Clear = null!, Image = null!;
    public Func<FrameworkElement> MainSwap = null!;
    public Grid Info = null!;
    public Panel Versions = null!;
    public TextBlock Label = null!;
    public Func<bool> IsSwapped = null!;
    public Action<bool> SetSwapped = null!;
}
public sealed class DownloadInstallPageView {
    public FrameworkElement Inner = null!, Back = null!, Start = null!, NameHint = null!;
    public Panel Select = null!, Minecraft = null!;
    public TextBox Name = null!;
    public TextBlock MinecraftVersion = null!;
    public FrameworkElement HintFabricApi = null!, HintOptiFabric = null!, HintOptiFabricOld = null!, HintModOptiFine = null!;
    public Action<string?> Logo = null!, MinecraftLogo = null!, NameHintText = null!;
    public Func<bool> NameValidated = null!;
    public Func<string?> NameValidationResult = null!;
    public Action ValidateName = null!, ScrollHome = null!;
}
public sealed class DownloadInstallPageHost<TOptiFine, TForge, TNeo, TLite, TResource, TRequest>
    where TOptiFine : class where TForge : class where TNeo : class where TLite : class where TResource : class {
    public DownloadInstallSelection<TOptiFine, TForge, TNeo, TLite, TResource> State = null!;
    public Func<DownloadInstallPageView> View = null!;
    public Func<DownloadInstallComponent, DownloadInstallComponentView> Component = null!;
    public Func<DownloadInstallComponent, DownloadInstallLoadingView?> Loading = null!;
    public int LoadingRun, LoadingError, LoadingUnloaded;
    public DownloadInstallSource<List<TOptiFine>> OptiFine = null!;
    public DownloadInstallSource<List<TLite>> LiteLoader = null!;
    public DownloadInstallSource<List<TNeo>> NeoForge = null!;
    public DownloadInstallSource<JObject> Fabric = null!;
    public DownloadInstallSource<List<TResource>> FabricApi = null!, OptiFabric = null!;
    public Func<DownloadInstallForgeSource<TForge>> Forge = null!;
    public Action BindLoaders = null!, RefreshRecommended = null!, InitializeLoader = null!, InitializeNameValidation = null!;
    public Action<bool> SuppressStartAnimation = null!, StartShow = null!;
    public Func<FrameworkElement, IEnumerable<FrameworkElement>> AnimationControls = null!;
    public Action<string> StartForge = null!;
    public Func<bool> ClientFinished = null!;
    public Func<JObject> ClientList = null!;
    public Func<JToken, string> FoolName = null!;
    public Action ReloadSelected = null!, ShowMinecraftList = null!;
    public Func<string?> SelectLogo = null!;
    public Action<FrameworkElement, MouseButtonEventArgs?> SelectMinecraft = null!;
    public Func<JObject, Action<FrameworkElement, MouseButtonEventArgs>?, FrameworkElement> MinecraftRow = null!;
    public Func<TOptiFine, Action<FrameworkElement, EventArgs>, FrameworkElement> OptiFineRow = null!;
    public Func<TLite, Action<FrameworkElement, EventArgs>, FrameworkElement> LiteLoaderRow = null!;
    public Func<TForge, Action<FrameworkElement, EventArgs>, FrameworkElement> ForgeRow = null!;
    public Func<TNeo, Action<FrameworkElement, EventArgs>, FrameworkElement> NeoForgeRow = null!;
    public Func<JObject, Action<FrameworkElement, EventArgs>, FrameworkElement> FabricRow = null!;
    public Func<TResource, Action<FrameworkElement, EventArgs>, FrameworkElement> FabricApiRow = null!, OptiFabricRow = null!;
    public Action<Panel, List<TForge>, Action<FrameworkElement, EventArgs>> ForgePreload = null!;
    public Action<Panel, List<TNeo>, Action<FrameworkElement, EventArgs>> NeoForgePreload = null!;
    public Action<IEnumerable<JObject>, Action<FrameworkElement, EventArgs>> FabricBuilder = null!;
    public Action BuildFabric = null!;
    public Func<FrameworkElement, FrameworkElement> Materialize = null!;
    public Func<FrameworkElement, string?> RowTitle = null!, RowLogo = null!;
    public Func<string, Panel, List<JObject>, Action<FrameworkElement, MouseButtonEventArgs>, bool, FrameworkElement> MinecraftCard = null!;
    public Func<Brush> Gray1 = null!, Gray4 = null!;
    public Func<string> MinecraftFolder = null!;
    public Func<int> IsolationSetting = null!;
    public Func<ModpackMessage, int> Message = null!;
    public Action<TRequest> StartRequested = null!;
    public Func<TRequest, bool> Install = null!;
}
