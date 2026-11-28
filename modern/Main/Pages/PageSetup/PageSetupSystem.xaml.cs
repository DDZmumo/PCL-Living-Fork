// SPDX-License-Identifier: MPL-2.0
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeloongCore;

namespace PCL;

public partial class PageSetupSystem : GlassWorkspacePage
{
    private bool _bindingMode;
    private bool _threadWarningQueued;

    public PageSetupSystem()
    {
        InitializeComponent();
        AttachScroll(PanBack);
        AttachPlaceholder(TextSystemCache, "默认");
        SettingService.BindNativeControls(this);
        BindHint(SliderDownloadThread, value => (value + 1).ToString());
        BindHint(SliderDownloadSpeed, SpeedLimitText);
        BindHint(SliderDebugAnim, value => value == 30 ? "关闭" : $"{(value + 1) / 10.0:0.#}x");
        SliderDownloadThread.ValueChanged += (_, _) => QueueThreadWarning();
        SliderDownloadThread.LostMouseCapture += (_, _) => QueueThreadWarning();
        CheckDebugMode.Checked += DebugModeChanged;
        CheckDebugMode.Unchecked += DebugModeChanged;
        CheckModpackMode.Checked += ModeChanged;
        CheckModpackMode.Unchecked += ModeChanged;
        BtnSystemSettingExp.Click += (_, _) => ModMain.Hint("该功能尚在开发中！", Log: false);
        BtnSystemSettingImp.Click += (_, _) => ModMain.Hint("该功能尚在开发中！", Log: false);
        RefreshBackupRetention();
        Loaded += (_, _) => {
            PanBack.ScrollToTop();
            Reload();
            RefreshBackupStorage();
        };
    }

    public void Reload()
    {
        ModAnimation.AniControlEnabled++;
        try
        {
            SettingService.RefreshSettings(this);
            RefreshModpackSwitch();
            RefreshBackupRetention();
        }
        finally { ModAnimation.AniControlEnabled--; }
    }

    internal void RefreshModpackSwitch()
    {
        _bindingMode = true;
        try
        {
            CheckModpackMode.IsChecked = ModModpackMode.Enabled;
            LabModpackMode.Visibility = CheckModpackMode.Visibility = ModModpackMode.CanEnable ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _bindingMode = false; }
    }

    private void ModeChanged(object sender, RoutedEventArgs args)
    {
        if (!IsLoaded || _bindingMode) return;
        _bindingMode = true;
        try { ModModpackMode.Toggle(CheckModpackMode); }
        finally { _bindingMode = false; }
    }

    public void Reset()
    {
        try
        {
            Settings.Reset("LaunchWorldBackupKeepAll");
            Settings.Reset("LaunchEnvironmentHistoryAutoCleanup");
            SettingService.ResetSettings(this);
            Logger.Info("已初始化启动器页设置");
            ModMain.Hint("已初始化启动器页设置！", ModMain.HintType.Green);
        }
        catch (Exception error) { Logger.Error(error, "初始化启动器页设置失败", LogBehavior.Alert); }
        Reload();
    }

    private static string SpeedLimitText(int value) => value switch {
        <= 14 => $"{(value + 1) * 0.1:0.#} M/s",
        <= 31 => $"{(value - 11) * 0.5:0.#} M/s",
        <= 41 => $"{value - 21} M/s",
        _ => "无限制"
    };

    private static void BindHint(Slider slider, Func<int, string> format)
    {
        var hint = new GlassPageResources.SliderDragHint(slider);
        hint.SetText(format((int)slider.Value));
        slider.ValueChanged += (_, _) => hint.SetText(format((int)slider.Value));
    }

    private void QueueThreadWarning()
    {
        if (_threadWarningQueued || !IsLoaded || ModAnimation.AniControlEnabled != 0 ||
            SliderDownloadThread.IsMouseCaptureWithin || SliderDownloadThread.Value < 100 ||
            Settings.Get<bool>("HintDownloadThread")) return;
        _threadWarningQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
            _threadWarningQueued = false;
            if (!IsLoaded || SliderDownloadThread.Value < 100 || Settings.Get<bool>("HintDownloadThread")) return;
            Settings.Set("HintDownloadThread", true);
            ModMain.MyMsgBox("如果设置过多的下载线程，可能会导致下载时出现严重的卡顿。\r\n一般设置 64 线程即可满足大多数下载需求。",
                "警告", "我知道了", IsWarn: true);
        }));
    }

    private void DebugModeChanged(object sender, RoutedEventArgs args)
    {
        if (IsLoaded && ModAnimation.AniControlEnabled == 0)
            ModMain.Hint("部分调试信息将在刷新或启动器重启后切换显示！");
    }
}
