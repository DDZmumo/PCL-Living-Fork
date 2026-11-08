// Behavior source: LF Pages/PageSetup and Pages/PageOther; repository LICENCE.
// LF lineage f4aa0fae62c3ee8af24c81e61844c8fdd13c8983; upstream PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8.
// Existing LF policies are preserved; no new native/performance/MMC behavior.
using System;
using System.Collections.Generic;
using PCLLF.Presentation.Controls;

namespace PCLCS;
public sealed class LegacyToolboxPageView
{
    public Func<MyTextBoxView> Url = null!, Folder = null!, Name = null!;
    public Func<MyButtonView> Start = null!, Open = null!, Clear = null!;
    public Func<bool> Loaded = null!;
    public Action<bool> SetLoaded = null!;
}

public sealed class LegacyToolboxPageHost<TFile, TDownload, TSave, TCombo>
    where TCombo : class
{
    public LegacySetupPageHost Common = null!;
    public Func<DateTime> LuckDate = () => DateTime.Now;
    public Func<string> LuckIdentify = null!;
    public Func<bool> LuckTimeChanged = () => false;
    public Func<LegacyToolboxPageController<TFile, TDownload, TSave, TCombo>?> CurrentPage = null!;
    public Func<bool> HasMain = null!, HasRunningGame = null!, LaunchLoading = null!, HasDownloadingTask = null!, HasGameFolders = null!, HasAdminRole = null!;
    public Func<MyExtraButtonView> ExtraDownload = null!;
    public Action StartFolderLoader = null!, WaitFolderLoader = null!, OptimizeNative = null!;
    public Func<IEnumerable<string>> GameFolders = null!;
    public Func<long> AvailableMemory = null!;
    public Func<string, int> RunElevated = null!;
    public Func<int> AcquireCleanup = null!, AcquireMemory = null!, ReadCleanup = null!;
    public Action ReleaseCleanup = null!, ReleaseMemory = null!;
    public Action<Action, string> NewThread = null!;
    public Func<ICollection<string>, string> RandomOne = null!;
    public Func<string, string, string?> SaveFile = null!;
    public Func<string?> SelectFolder = null!;
    public Action<string> StartProcess = null!;
    public Func<string, string> ValidateFolder = null!;
    public Action<MyTextBoxView, Func<string, string>> AddValidation = null!;
    public Action<MyTextBoxView> AddFolderValidation = null!;
    public Func<string, string, LegacyToolboxDownloadSession> CreateSession = null!;
    public Func<string, string, long, bool, TFile> NewFile = null!;
    public Action<TFile, bool> SetCredentialHeaders = null!;
    public Action<TFile, Action> SetMergeFinished = null!;
    public Func<string, List<TFile>, TDownload> NewDownload = null!;
    public Func<string, Action<TSave>, TSave> NewSave = null!;
    public Func<TSave, bool> SaveCanceled = null!;
    public Action<TSave, double> SaveWeight = null!;
    public Func<string, TDownload, TSave, TCombo> NewCombo = null!;
    public Func<TCombo, int> ComboState = null!;
    public Func<TCombo, string> ComboName = null!;
    public Func<TCombo, Exception> ComboError = null!;
    public Action<TCombo, Action<TCombo>> SetStateCallback = null!;
    public Action<TCombo> StartCombo = null!, AddTaskbar = null!;
    public Func<int> NewUuid = null!;
}
