using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MeloongCore;
using Microsoft.VisualBasic.CompilerServices;
using PCLLF.Presentation.Animation;

using PCLCS.Startup;

namespace PCLLF.Presentation.Windowing;

public sealed partial class LegacyMainWindowController<TStack, TInstance, TAnimation>
    where TStack : class, ILegacyMainPageStack, new() where TInstance : class where TAnimation : struct {
    public void Closing(CancelEventArgs args) { EndProgram(true); args.Cancel = true; }
    public void EndProgram(bool sendWarning) {
        if (host.HasBlockingFileOperation()) { Hint("版本文件处理尚未完成，请稍候再退出。", LegacyMainHint.Red); return; }
        if (host.HasDownloadingTask()) {
            if (sendWarning && Message("还有下载任务尚未完成，是否确定退出？", second: "取消") == 2) return;
            RunThread(() => { Logger.Info("正在强行停止任务"); foreach (Action cancel in host.TaskbarCancels().ToList()) cancel(); }, "强行停止下载任务");
        }
        host.UiWait(() => {
            if (host.HasBlockingFileOperation()) { Hint("版本文件处理尚未完成，请稍候再退出。", LegacyMainHint.Red); return; }
            if (!Window.IsHitTestVisible) return;
            Window.IsHitTestVisible = false;
            if (view.PlayClose?.Invoke(host.ForceExit) != true) host.ForceExit();
            Logger.Info("收到关闭指令");
        });
    }
    public void SizeChanged() {
        if (host.SizeSaveable()) { host.WriteSetting("WindowHeight", Window.Height); host.WriteSetting("WindowWidth", Window.Width); }
        if (Window.WindowState == WindowState.Maximized) Window.WindowState = WindowState.Normal;
    }
    public void Minimize() => Window.WindowState = WindowState.Minimized;
    public IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg == 30) {
            DateTime date = DateTime.Now;
            if (date.Date == host.ApplicationOpenTime().Date) { Logger.Info($"系统时间微调为：{date.ToLongDateString()} {date.ToLongTimeString()}"); host.WriteSystemTimeChanged(false); }
            else { Logger.Info($"系统时间修改为：{date.ToLongDateString()} {date.ToLongTimeString()}"); host.WriteSystemTimeChanged(true); }
        } else if (msg == 6402) {
            Logger.Info($"收到置顶信息：{hwnd.ToInt64()}");
            if (!host.Loaded()) { Logger.Info("窗口尚未加载完成，忽略置顶请求"); return IntPtr.Zero; }
            ShowWindowToTop(); handled = true;
        }
        return IntPtr.Zero;
    }
    public void SetHidden(bool value) {
        if (host.Hidden() == value) return;
        host.WriteHidden(value);
        if (value) { Window.Left -= 10000; Window.ShowInTaskbar = false; Window.Visibility = Visibility.Hidden; Logger.Info($"窗口已隐藏，位置：({Window.Left},{Window.Top})"); }
        else { if (Window.Left < -2000) Window.Left += 10000; ShowWindowToTop(); }
    }
    public void ShowWindowToTop() => host.Ui(() => {
        Window.Visibility = Visibility.Visible; Window.ShowInTaskbar = true; Window.WindowState = WindowState.Normal; SetHidden(false); Window.Topmost = true; Window.Topmost = false;
        host.SetForeground(host.Handle()); Window.Focus(); Logger.Info($"窗口已置顶，位置：({Window.Left}, {Window.Top}), {Window.Width} x {Window.Height}");
    });
}
