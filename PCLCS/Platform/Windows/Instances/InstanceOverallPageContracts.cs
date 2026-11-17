using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace PCLCS;

public sealed class InstanceOverallPageView<TItem> where TItem : Grid {
    public ComboBox DisplayType = null!, DisplayLogo = null!;
    public FrameworkElement CustomLogo = null!, Delete = null!, Star = null!, FolderMods = null!, Installed = null!;
    public Panel HeaderPanel = null!;
    public Action ScrollHome = null!, ResizeDisplay = null!;
    public Action<string> SetStarText = null!;
    public Func<TItem?> Header = null!;
    public Action<TItem> SetHeader = null!;
    public Func<TItem, string> HeaderInfo = null!;
    public Action<TItem, string> SetHeaderInfo = null!;
    public Func<bool> IsLoaded = null!;
    public Action<bool> SetLoaded = null!;
    public Func<string> OriginalHeaderInfo = null!;
    public Action<string> SetOriginalHeaderInfo = null!;
}
public sealed class InstanceOverallPageHost<TInstance, TItem, TCombo>
    where TInstance : class where TItem : Grid where TCombo : class {
    public Func<TInstance> Instance = null!;
    public Action<TInstance> SetInstance = null!;
    public Func<TInstance?> SelectedInstance = null!;
    public Func<string> MinecraftFolder = null!;
    public Func<bool> ModpackEnabled = null!, LaunchLoading = null!, Shift = null!, LegacyLogin = null!, HideHintSeen = null!;
    public Action MarkHideHint = null!, ForceListRefresh = null!;
    public Func<TInstance, bool> Locked = null!, IgnoreFileCheck = null!;
    public Func<TInstance, TItem> CreateHeader = null!;
    public Action RefreshInstalled = null!, RefreshModDisabled = null!, RefreshPageName = null!, RefreshInstanceList = null!;
    public Func<string, TInstance> LoadInstance = null!;
    public Func<TInstance, string?, TInstance> Rename = null!;
    public Action<TInstance>? BeginManagedRename;
    public Action<TInstance>? BeginManagedRepair;
    public Action<TInstance, bool, Action>? BeginManagedDelete;
    public Func<string, string, string, string> ReadIni = null!;
    public Action<string, string, string> WriteIni = null!;
    public Func<string, string> ReadLogo = null!, StorageLogo = null!;
    public Action<string, string> WriteLogo = null!;
    public Func<string?> SelectImage = null!;
    public Func<string, string?, string?> InputDescription = null!;
    public Func<string, string, string?> InputName = null!;
    public Func<string, string, string?> SaveScript = null!;
    public Func<string, string, bool, int> Confirm = null!;
    public Func<TInstance, string, bool> LaunchScript = null!;
    public Func<IEnumerable<string>> TaskNames = null!;
    public Func<TInstance, string, TCombo> CreateFixLoader = null!;
    public Action<TCombo, Action> HookState = null!;
    public Func<TCombo, int> LoaderState = null!;
    public int Finished, Failed, Canceled;
    public Func<TCombo, string> LoaderName = null!, LoaderError = null!;
    public Action<TCombo, string> StartLoader = null!;
    public Action<TCombo> TaskbarAdd = null!;
    public Action RefreshDownload = null!, RibbleDownload = null!, PageBack = null!;
    public Action<string> Hint = null!, HintRed = null!, HintGreen = null!, HintBlue = null!, Explorer = null!;
    public Action<Exception, string> Error = null!, ErrorAlert = null!, Warn = null!;
    public Action<TInstance, bool, Action> DeleteConfirmed = null!;
    public Func<bool> IsCurrentPage = () => true;
}
