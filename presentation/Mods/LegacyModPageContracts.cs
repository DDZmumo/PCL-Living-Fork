using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCLCS;

namespace PCLLF.Presentation.Mods;

public enum LegacyModFilter { All, Enabled, Disabled, CanUpdate, Duplicate }
public delegate void LegacyModSelectionBar(int count,ref int previous,bool animate);
public sealed class LegacyModPageView {
    public Func<FrameworkElement> Root = null!, Back = null!, Empty = null!, ListBack = null!;
    public Func<Panel?> List = null!;
    public Func<string> Search = null!;
    public Action<string> SetSearch = null!, SetSelectionText = null!;
    public Action ScrollHome = null!, FocusBack = null!;
    public Action<bool> EnableIndex = null!;
    public Action<string> IndexText = null!;
    public Func<ResourcePageFilter[]> Filters = null!;
    public Action<string> SetEmptyTitle = null!, SetEmptyHint = null!;
    public Action<bool> EnableDisable = null!, EnableEnable = null!, EnableUpdate = null!;
    public LegacyModSelectionBar SelectionBar = null!;
}
public sealed class LegacyModDependencyDetails {
    public string Caption = null!;
    public Func<bool> HasMod = null!;
    public Func<FrameworkElement> CreateView = null!;
}
// Read-only live access to the original C# report, not a second report/model.
public sealed class LegacyModDependencyIndexResult {
    public Func<bool> IndexReadFailed = null!, IndexPersisted = null!, OnlineIncomplete = null!;
}
public sealed class LegacyModUpdateJob {
    public Func<int> State = null!;
    public Func<Exception?> Error = null!;
    public Action Start = null!, AddTaskbar = null!;
    public Action<Action<LegacyModUpdateJob>> SetOnStateChanged = null!;
}
public sealed class LegacyModPageHost<TMod,TProject,TVersion,TItem,TInstance,TDownload,TButton,TImpact>
    where TMod : LocalResourceFileData<TProject,TVersion> where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData
    where TItem : FrameworkElement where TInstance : class where TDownload : class where TButton : FrameworkElement where TImpact : ModDependencyImpactItemData {
    public Func<ConcurrentDictionary<string,TItem>> Items = null!;
    public Func<List<string>> Selected = null!, UpdatingFolders = null!;
    public Action<List<string>> SetSelected = null!;
    public Func<List<TMod>?> Output = null!;
    public Func<TInstance?> Instance = null!, GlobalInstance = null!;
    public Func<TInstance,string> GameFolder = null!, InstanceName = null!;
    public Func<TInstance,bool> Modable = null!, Locked = null!;
    public Func<bool> AnyLocked = null!;
    public Action ResetIndexLookups = null!;
    public Func<TInstance,Action<int,int>,LegacyModDependencyIndexResult> RebuildIndex = null!;
    public Action<TItem,TInstance> RefreshLoaderWarning = null!;
    public Func<bool> LastWasDetails = null!, IsCurrentPage = null!, CtrlDown = null!, ShiftDown = null!, IsInstanceSetup = null!, IsInstanceSelect = null!, IsModPage = null!, ModpackMode = null!, HistoryBusy = null!;
    public Func<int> AnimationControl = null!;
    public Action<int> SetAnimationControl = null!, ChangeAnimationControl = null!;
    public Func<int,bool> LoaderRun = null!;
    public Action<string> ForceFolderLoader = null!;
    public Func<int> LoaderState = null!;
    public Action ApplyMode = null!, BindMainKey = null!, AdjustFilterMargins = null!, InitializeLoader = null!;
    public Action ClearProjectCache = null!, ClearVersionCache = null!, SelectModPage = null!;
    public Action<bool> ReloadCurrent = null!;
    public Func<string> TempPath = null!;
    public Action<string,int,bool> Hint = null!;
    public Func<string,bool> ConfirmMode = null!, ReadBoolean = null!;
    public Action<string,object> WriteSetting = null!;
    // Cached OrdinalIgnoreCase snapshot for the current instance. Rows and counts
    // only perform set lookups; a toggle returns the state that actually persisted.
    public Func<IEnumerable<string>> SelectInstallFiles = null!;
    public Action<string> Explorer = null!, Website = null!;
    public Func<string,string,string,string,bool,int> Message = null!;
    public Action<string,string,Func<FrameworkElement>> ContentMessage = null!;
    public Func<string,string,List<TImpact>,int> ImpactConfirm = null!;
    public Func<TInstance,string[],List<TImpact>> InspectImpacts = null!;
    public Func<TInstance,TMod,LegacyModDependencyDetails> InspectDetails = null!;
    public Action<Action> Dispatch = null!, DispatchWait = null!;
    public Action<Action,string,ThreadPriority> Background = null!;
    public Func<TMod,bool,TItem> NewItem = null!;
    public Action<TMod,TItem> BindUpdate = null!;
    public Func<TItem,TMod> Entry = null!;
    public Func<TItem,bool> Checked = null!;
    public Action<TItem,bool> SetChecked = null!;
    public Action<TItem> BindRowEvents = null!;
    public Func<double,string,string,TItem?,Action<object>,TButton> CreateButton = null!;
    public Action<TItem,IEnumerable<TButton>> SetButtons = null!;
    public Func<string> OpenLogo = null!, InfoLogo = null!, DeleteLogo = null!, StopLogo = null!, CheckLogo = null!, ListLogo = null!;
    public Func<object,TItem> ResolveItem = null!;
    public Func<string,TMod> NewMod = null!;
    public Action<TInstance,string> NavigateDownload = null!;
    public Action<TProject> NavigateDetails = null!;
    public Func<TVersion,string,TDownload> ToNetFile = null!;
    public Func<Func<string>,List<TDownload>,double,Action<CancellationToken>,LegacyModUpdateJob> CreateUpdateJob = null!;
    public Func<TInstance,IReadOnlyDictionary<string,string>,CancellationToken,IReadOnlyList<ModUpdateDependencyFinding>> CheckUpdateDependencies = null!;
    public Action<TInstance,string,CancellationToken> CaptureHistory = null!;
    public Action RefreshDownloadButton = null!, RibbleDownloadButton = null!;
    public Func<long,string> FormatSize = null!;
}
