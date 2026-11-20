using System;
using System.Collections.Generic;
using System.Net.Http;

namespace PCLLF.Presentation.Accounts;

// LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 Pages/PageLaunch login/skin/dialog
// sources are the behavior base; account preservation uses the current C022 APIs.
// PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8 is upstream. CE fixed
// 1a7653127d578efdb2a9388999a3fdc67d9e49d5 PageLoginMs.xaml.cs,
// PageLoginOffline.xaml.cs, PageLoginAuth.xaml.cs, PageLoginProfileSkin.xaml.cs,
// MySkin.xaml.cs were reviewed (their Plain Craft Launcher 2/LICENCE applies). Its profile store,
// UUID/new validators/localization/provider policy is not substituted for LF.
// Repository LF LICENCE applies. These hosts close the original DTO and UI types;
// no credentials, task state, account store, cache or scheduler is mirrored.
public enum LegacyLoginHint { Default, Red, Green, Blue }
public enum LegacyLoginTaskState { Waiting, Loading, Finished, Failed, Canceled }
public sealed class LegacyLoginTaskPort {
    public Func<LegacyLoginTaskState> State = null!;
    public Func<double> Progress = null!;
    public Func<Exception?> Error = null!;
    public Func<object?> Input = null!;
    public Action<object?, bool> Start = null!, Wait = null!;
    public Action Cancel = null!, ResetInput = null!;
}
public sealed class LegacyLoginInput {
    public string? UserName, Password, RefreshToken, AccessToken, Uuid, ProfileJson, Token, BaseUrl, Description, SkinName;
    public int SkinType;
    public bool PopulateMsBasic, PopulateMsFull;
}
public sealed class LegacyLoginHost<TLegacy, TMs, TServer> {
    public Func<string, string> ReadString = null!, ReadInstanceString = null!;
    public Func<string, bool> ReadBoolean = null!;
    public Func<string, int> ReadInteger = null!;
    public Action<string, object?> WriteSetting = null!;
    public Func<string, Dictionary<string, object?>> AccountSnapshot = null!;
    public Action<Dictionary<string, object?>> SaveAccountGroup = null!;
    public Action<PCLCS.Applications.AccountProvider>? SignOut;
    public Func<LegacyLoginInput, TLegacy> CreateLegacy = null!;
    public Func<LegacyLoginInput, TMs> CreateMs = null!;
    public Func<LegacyLoginInput, TServer> CreateServer = null!;
    public Func<TLegacy, string> LegacyName = null!;
    public Func<TMs, string> MsRefreshToken = null!;
    public Func<TServer, string> ServerName = null!, ServerPassword = null!;
    public Action<TServer, bool> ForceReselectProfile = null!;
    public Func<object?, bool> IsReselectingServer = null!;
    public Func<bool> HasSelectedInstance = null!, SelectedVersionValid = null!;
    public Func<Version> SelectedVanillaVersion = null!;
    public Func<int> AnimationDisabled = null!;
    public Func<bool> HiddenForceShow = null!, HasLaunchPage = null!;
    public Action OpenLaunchSettings = null!;
    public Action<bool, bool> RefreshLaunchPage = null!;
    public Action<string> OpenWebsite = null!;
    public Action<string, LegacyLoginHint> Hint = null!;
    public Action<Action> RunInUi = null!, RunInUiWait = null!, RunInThread = null!;
    public Action<Action, string> RunInNewThread = null!;
    public LegacyLoginTaskPort MsLogin = null!, NideLogin = null!, AuthLogin = null!, MainLogin = null!, Launch = null!;
    public Func<string> MsAccessToken = null!, MsUuid = null!, MsProfileJson = null!;
    public Action<string> SetMsProfileJson = null!;
    public Func<string, HttpMethod, object?, string?, Dictionary<string, string>?, string> RequestRetry = null!;
}
public sealed class LegacyOfflineLoginView {
    public Func<bool> IsReloaded = null!;
    public Action<bool> SetReloaded = null!;
    public Func<string> Name = null!;
    public Action<string> SetName = null!;
    public Action<IEnumerable<string>?> SetNames = null!;
    public Action StartSkin = null!, RestartSkin = null!;
    public Action<bool> ShowChineseHint = null!;
    public Action InvalidateMeasureWhenLoaded = null!;
}
public sealed class LegacyServerLoginView {
    public Func<string> Name = null!, Password = null!;
    public Action<string> SetName = null!, SetPassword = null!;
    public Action<IEnumerable<string>?> SetNames = null!;
    public Action<bool> SetRemember = null!, ShowRegister = null!;
    public Func<object?> LinkContent = null!;
    public Action<string> SetLinkContent = null!;
}
public sealed class LegacyMicrosoftLoginView {
    public Func<int> SelectedIndex = null!;
    public Action<int> SelectIndex = null!;
    public Action ClearItems = null!, AddNewAccount = null!;
    public Action<string, Func<string>> AddAccount = null!;
    public Func<object?> SelectedItem = null!;
    public Func<bool> HasPresenter = null!;
    public Func<object?, bool> IsAccountItem = null!, IsNewAccountItem = null!;
    public Func<object?, string> AccountName = null!, AccountToken = null!;
    public Func<object?, object?> NewAccountContent = null!;
    public Action<object?> SetPresenterContent = null!;
    public Action<bool> EnableAccounts = null!, EnableLogin = null!;
    public Action<string> SetLoginText = null!;
}
