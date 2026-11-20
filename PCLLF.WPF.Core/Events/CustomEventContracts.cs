using System;
using System.Diagnostics;

namespace PCLCS;

public sealed class CustomEventHost<TPage,TSubPage,TInstance,THint>
    where TPage : struct,Enum where TSubPage : struct,Enum where TInstance : class where THint : struct,Enum {
    public Action<Action> RunInThread = null!, RunInUi = null!, RunInUiWait = null!;
    public Func<bool> IsUiThread = null!;
    public Func<string,string,string,string,string,int> Message = null!;
    public Action<string,THint> Hint = null!;
    public THint Blue,Green,Red;
    public Action<string> Info = null!, OpenWebsite = null!, Clipboard = null!, EnterHelp = null!;
    public Action<Exception,string> ErrorAlert = null!, WarnToast = null!;
    public Action<ProcessStartInfo> StartProcess = null!;
    public Func<TInstance?> SelectedInstance = null!;
    public Func<TInstance,string> InstanceName = null!;
    public Func<string,TInstance> CreateInstance = null!;
    public Func<TInstance,string?,bool> Launch = null!;
    public Action RefreshHome = null!, RefreshCurrent = null!, RefreshHelp = null!, Jrrp = null!, MemoryOptimize = null!, RubbishClear = null!, InstallModpack = null!, CheckUpdates = null!;
    public Func<bool> CanRefreshCurrent = null!, HintCustomCommand = null!;
    public Action RememberCustomCommand = null!;
    public Func<TInstance,bool> Locked = null!;
    public Action<TInstance> SetEditedInstance = null!;
    public TPage InstanceSetup;
    public TSubPage DefaultSubPage;
    public Action<TPage,TSubPage> ChangePage = null!;
    public Action<string,string,string?> Download = null!;
    public Action<string,string,TInstance?> SetSetting = null!;
    public Func<string> RegistryFolder = null!, BaseFolder = null!, CacheFolder = null!, RequestTempFolder = null!;
    public Action<string,string,string> WriteRegistry = null!;
    public Action ExtractHelp = null!;
    public Func<string,bool> FileExists = null!;
    public Action<string,string> DownloadFile = null!;
    public bool LegacyLink;
    public Action? JoinDefault;
    public Action<string>? Join;
}
