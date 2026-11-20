using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MeloongCore;

namespace PCLCS;

public sealed class ModpackExportPageView<TCheck> where TCheck : FrameworkElement {
    public TextBox Name = null!, Version = null!;
    public Func<string> NameHint = null!;
    public Action<string> SetNameHint = null!, SetVersionHint = null!;
    public TCheck Basic = null!, Java = null!, Launcher = null!, Custom = null!, ModpackMode = null!, Isolation = null!, IncludeAssets = null!, Modrinth = null!, ShaderSettings = null!;
    public StackPanel Options = null!, ResourcePacks = null!, Saves = null!, Shaders = null!;
    public FrameworkElement OverrideCancel = null!, CardOptions = null!, HintOptiFine = null!, HintJava = null!, HintModrinth = null!, Export = null!;
    public Func<InlineCollection> CardInlines = null!;
    public Action ScrollHome = null!;
    public Func<TCheck, bool> Checked = null!;
    public Action<TCheck, bool> SetChecked = null!;
    public Func<TCheck, InlineCollection> Inlines = null!;
    public Func<ModpackExportOptionData, TCheck> CreateCheck = null!;
    public Func<Brush> Gray = null!;
    public Action<string, bool> SetJavaHint = null!;
}
public sealed class ModpackExportPageState {
    public Func<string> CurrentInstance = null!;
    public Action<string> SetCurrentInstance = null!;
    public Func<string[]> BlackList = null!;
    public Func<List<string>?> Rules = null!, Extra = null!;
    public Action<List<string>?> SetRules = null!, SetExtra = null!;
    public Func<string?> PackPath = null!;
    public Action<string?> SetPackPath = null!;
}
public sealed class ModpackExportPageHost<TInstance, TOption, TCombo> where TInstance : class where TOption : ModpackExportOptionData where TCombo : class {
    public Func<TInstance> Instance = null!;
    public Func<TInstance, string> Name = null!, VersionFolder = null!, GameRoot = null!, VersionDescription = null!;
    public Func<TInstance, McVersion> Version = null!;
    public Func<TInstance, bool> Modable = null!;
    public Func<TInstance, int> JavaType = null!;
    public Func<RedoableWorker<Java>> JavaWorker = null!;
    public Action<Action> Ui = null!;
    public Action<int> AdjustAnimation = null!;
    public Func<int> Animation = null!;
    public Func<bool> ReleaseBuild = null!, ModpackEnabled = null!;
    public Action<string> SetHelpEvent = null!;
    public Func<TOption> CreateOption = null!;
    public Func<IEnumerable<string>> TaskNames = null!;
    public Action ShowTasks = null!;
    public Func<string> TempFolder = null!, ReadConfigPath = null!;
    public Action<string> WriteConfigPath = null!;
    public Func<string, string, string?, List<(string Extension, string Display)>, string?> SaveFile = null!;
    public Func<string, string?, string?> SelectConfig = null!;
    public Func<string, string, string, string, int> Confirm = null!;
    public Func<Exception, string> ErrorDisplay = null!;
    public Action<string> Info = null!, HintGreen = null!, Explorer = null!;
    public Action<Exception, string> Warn = null!, ErrorAlert = null!, ErrorToast = null!;
    public Func<ModpackExportRequest<TInstance>, TCombo> CreateCombo = null!;
    public Action<TCombo> StartCombo = null!, AddTaskbar = null!;
    public Action RefreshDownload = null!, RibbleDownload = null!;
    public Func<char> QuoteLeft = null!, QuoteRight = null!;
}
