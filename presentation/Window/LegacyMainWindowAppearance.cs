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
        if (host.LegacyLink && host.LinkTryExit(!sendWarning, true)) return;
        host.UiWait(() => {
            if (host.HasBlockingFileOperation()) { Hint("版本文件处理尚未完成，请稍候再退出。", LegacyMainHint.Red); return; }
            Window.IsHitTestVisible = false;
            if (view.PanBack().RenderTransform is null) {
                TranslateTransform position = new(0, 0); RotateTransform rotation = new(0); ScaleTransform scale = new(1, 1);
                view.PanBack().RenderTransform = new TransformGroup { Children = new TransformCollection { rotation, position, scale } };
                AniStart([
                    AaOpacity(Window, -Window.Opacity, 140, 40, Out(AnimationEasePower.Weak)),
                    AaDouble(i => { scale.ScaleX += i; scale.ScaleY += i; }, 0.88 - scale.ScaleX, 180),
                    AaDouble(i => position.Y += i, 20 - position.Y, 180, 0, Out(AnimationEasePower.Weak)),
                    AaDouble(i => rotation.Angle += i, 0.6 - rotation.Angle, 180, 0, Inout(AnimationEasePower.Weak)),
                    AaCode(() => { Window.IsHitTestVisible = false; Window.Top = -10000; Window.ShowInTaskbar = false; }, 210), AaCode(host.ForceExit, 230)
                ], "Form Close");
            } else host.ForceExit();
            Logger.Info("收到关闭指令");
        });
    }
    public void DragMove(object sender) {
        ProjectData.ClearProjectError();
        if (LegacyMainTermination.ResumeNextCondition(() => ((UIElement)sender).IsMouseDirectlyOver)) {
            try { Window.DragMove(); } catch (Exception ex) { ProjectData.SetProjectError(ex); }
        }
    }
    public void SizeChanged() {
        if (host.SizeSaveable()) { host.WriteSetting("WindowHeight", Window.Height); host.WriteSetting("WindowWidth", Window.Width); }
        view.RectForm().Rect = new Rect(0, 0, view.BorderForm().ActualWidth, view.BorderForm().ActualHeight);
        view.PanForm().Width = view.BorderForm().ActualWidth + 0.001; view.PanForm().Height = view.BorderForm().ActualHeight + 0.001;
        view.PanMain().Width = view.PanForm().Width; view.PanMain().Height = Math.Max(0, view.PanForm().Height - view.PanTitle().ActualHeight);
        if (Window.WindowState == WindowState.Maximized) Window.WindowState = WindowState.Normal;
    }
    public void Minimize() => Window.WindowState = WindowState.Minimized;
    public void UpdateBackgroundAndTitleBar() {
        Logger.Info("从设置更新背景图片与标题栏样式");
        view.ImgBack().Opacity = host.ReadInteger("UiBackgroundOpacity") / 1000d * PCLLF.Presentation.Foundation.LegacyThemePalette.BackgroundThemeOpacityFactor(view.ImgBack());
        double radius = checked(host.ReadInteger("UiBackgroundBlur") + 1);
        if (radius == 1 || view.UsesGlassShell?.Invoke() == true) view.ImgBack().Effect = null; else view.ImgBack().Effect = new BlurEffect { Radius = radius };
        view.ImgBack().Margin = new Thickness(-radius / 1.8);
        int type = host.ReadInteger("UiBackgroundSuit");
        if (view.ImgBack().Background is not null) {
            ImageBrush brush = (ImageBrush)view.ImgBack().Background;
            if (type == 0) type = brush.ImageSource.Width < view.PanMain().ActualWidth / 2 && brush.ImageSource.Height < view.PanMain().ActualHeight / 2 ? 4 : 2;
            brush.Stretch = Stretch.UniformToFill; brush.TileMode = TileMode.None; brush.Viewport = new Rect(0, 0, 1, 1); brush.ViewportUnits = BrushMappingMode.RelativeToBoundingBox; brush.AlignmentX = AlignmentX.Center; brush.AlignmentY = AlignmentY.Center;
            switch (type) {
                case 1: brush.Stretch = Stretch.None; break;
                case 3: brush.Stretch = Stretch.Fill; break;
                case 4: brush.Stretch = Stretch.None; brush.TileMode = TileMode.Tile; brush.Viewport = new Rect(0, 0, brush.ImageSource.Width, brush.ImageSource.Height); brush.ViewportUnits = BrushMappingMode.Absolute; break;
                case 5: brush.AlignmentX = AlignmentX.Left; brush.AlignmentY = AlignmentY.Top; break;
                case 6: brush.AlignmentX = AlignmentX.Right; brush.AlignmentY = AlignmentY.Top; break;
                case 7: brush.AlignmentX = AlignmentX.Left; brush.AlignmentY = AlignmentY.Bottom; break;
                case 8: brush.AlignmentX = AlignmentX.Right; brush.AlignmentY = AlignmentY.Bottom; break;
                case 9: brush.AlignmentX = AlignmentX.Left; brush.AlignmentY = AlignmentY.Center; break;
                case 10: brush.AlignmentX = AlignmentX.Right; brush.AlignmentY = AlignmentY.Center; break;
                case 11: brush.AlignmentX = AlignmentX.Center; brush.AlignmentY = AlignmentY.Top; break;
                case 12: brush.AlignmentX = AlignmentX.Center; brush.AlignmentY = AlignmentY.Bottom; break;
            }
        }
        UpdateTitleBar();
    }
    private void SetLogoSettings(Visibility check, Visibility text, Visibility change) {
        if (host.LogoSettings() is null) return;
        host.LogoSettings()!.CheckLeft.Visibility = check; host.LogoSettings()!.Text.Visibility = text; host.LogoSettings()!.Change.Visibility = change;
    }
    public void UpdateTitleBar() {
        int logoType = host.ReadInteger("UiLogoType"); if (host.PackEnabled() && logoType != 2) logoType = 1;
        switch (logoType) {
            case 0:
                view.ShapeTitleLogo().Visibility = Visibility.Collapsed; view.LabTitleLogo().Visibility = Visibility.Collapsed; view.ImageTitleLogo().Visibility = Visibility.Collapsed;
                SetLogoSettings(Visibility.Visible, Visibility.Collapsed, Visibility.Collapsed); break;
            case 1:
                view.ShapeTitleLogo().Visibility = Visibility.Collapsed; view.LabTitleLogo().Visibility = Visibility.Visible; view.ImageTitleLogo().Visibility = Visibility.Collapsed;
                SetLogoSettings(Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed); break;
            case 2:
                view.ShapeTitleLogo().Visibility = Visibility.Collapsed; view.LabTitleLogo().Visibility = Visibility.Visible; view.ImageTitleLogo().Visibility = Visibility.Collapsed;
                SetLogoSettings(Visibility.Collapsed, Visibility.Visible, Visibility.Collapsed); break;
            case 3:
                view.ShapeTitleLogo().Visibility = Visibility.Collapsed; view.LabTitleLogo().Visibility = Visibility.Collapsed; view.ImageTitleLogo().Visibility = Visibility.Visible;
                SetLogoSettings(Visibility.Collapsed, Visibility.Collapsed, Visibility.Visible);
                try { view.ImageTitleLogo().Source = Paths.Base + @"PCL\Logo.png"; }
                catch (Exception ex) { view.ImageTitleLogo().Source = null; Logger.Error(ex, "显示标题栏图片失败", LogBehavior.Alert); }
                break;
        }
        host.LogoSettings()?.ForceResize();
        view.LabTitleLogo().Text = host.DisplayPackTitle(logoType == 1 ? "PCL-LF" : host.ReadString("UiLogoText"));
        view.PanTitleMain().ColumnDefinitions[0].Width = new GridLength(host.ReadBoolean("UiLogoLeft") && logoType == 0 ? 0 : 1, GridUnitType.Star);
    }
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
