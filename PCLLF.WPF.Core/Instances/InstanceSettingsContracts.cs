using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;

namespace PCLCS;

public sealed class InstanceSettingsView<TItem,TButton> where TItem : FrameworkElement where TButton : FrameworkElement {
    public ComboBox Isolation = null!, Login = null!, JavaKind = null!, JavaSelect = null!;
    public TextBox ServerEnter = null!, AuthServer = null!, AuthRegister = null!, AuthName = null!, JavaRange = null!, AdvanceRun = null!;
    public FrameworkElement Slider = null!, JavaHint = null!, JavaSelectPanel = null!, JavaSearch = null!, AdvanceRunWait = null!;
    public TextBlock JavaLabel = null!;
    public FrameworkElement[] NideControls = null!, AuthControls = null!;
    public Action ScrollHome = null!, ResizeServer = null!;
    public Func<bool> LoadedFirst = null!, NideValidated = null!, AuthValidated = null!;
    public Action<bool> SetLoadedFirst = null!;
    public Func<int> LoginLast = null!;
    public Action<int> SetLoginLast = null!;
    public Func<string> JavaRangeError = null!;
    public Action<string,int> SetJavaHint = null!;
    public Action<Func<string,string>> SetJavaRangeValidator = null!;
    public Func<Java?,string,TItem> CreateJavaItem = null!;
    public Action<TItem,Action> OnJavaClick = null!;
    public Func<bool,TButton> CreateJavaButton = null!;
    public Action<TButton,Action> OnButtonClick = null!;
    public Action<TItem,List<TButton>> SetButtons = null!;
}
public sealed class InstanceSettingsHost<TInstance> where TInstance : class {
    public Func<TInstance> Instance = null!;
    public Action<TInstance> SetInstance = null!;
    public Func<string,TInstance,int> Integer = null!;
    public Func<string,TInstance,bool> Boolean = null!, HasSaved = null!;
    public Action<string,object,TInstance> Set = null!;
    public Action<string,TInstance> Reset = null!;
    public Action ApplyVisibility = null!, RefreshSettings = null!, ResetSettings = null!;
    public Func<int> Animation = null!;
    public Action<int> AddAnimation = null!;
    public Action<Action> RunInUi = null!;
    public Func<RedoableWorker<Java>> JavaWorker = null!;
    public Func<RedoableWorker> JavaRefresh = null!;
    public Func<TInstance,ValueRange<Version>> JavaRequirement = null!;
    public Action ReloadCurrentJava = null!, ImportJava = null!, SearchJava = null!;
    public Action<Java> RemoveJava = null!;
    public Action<TInstance> OpenInstanceFolder = null!;
    public Action<string> Explorer = null!, Info = null!;
    public Action<string,bool> HintGreen = null!;
    public Action<Exception,string> Error = null!, ErrorAlert = null!;
    public Func<string,string,string,string,bool,int> Confirm = null!;
    public Action ClearInstanceCache = null!, RefreshInstanceList = null!, SwitchGlobal = null!;
    public Func<string,TInstance> LoadInstance = null!;
    public string LeftQuote = "", RightQuote = "";
}
