using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLCS;

public sealed class InstanceSettingsController<TInstance,TState,TCard,TItem,TButton>
    where TInstance : LegacyMcInstance<TInstance,TState,TCard> where TState : struct,Enum where TCard : struct,Enum where TItem : FrameworkElement where TButton : FrameworkElement {
    private readonly InstanceSettingsView<TItem,TButton> view; private readonly InstanceSettingsHost<TInstance> host; private readonly InstanceRamDisplayController ram;
    private bool isReverting;
    public InstanceSettingsController(InstanceSettingsView<TItem,TButton> view, InstanceSettingsHost<TInstance> host, InstanceRamDisplayController ram) { this.view=view; this.host=host; this.ram=ram; }
    public void Loaded() {
        host.ApplyVisibility(); view.ScrollHome(); ram.Refresh(false); host.AddAnimation(1); Reload(); host.AddAnimation(-1);
        if (view.LoadedFirst()) return; view.SetLoadedFirst(true);
        host.JavaWorker().Started += UpdateJavaRelatedUi; host.JavaWorker().Stopped += UpdateJavaRelatedUi;
        view.SetJavaRangeValidator(text => InstanceJavaRangeValidation.Validate(text,host.LeftQuote,host.RightQuote));
        DispatcherTimer timer = new() { Interval = new TimeSpan(0,0,0,1) }; timer.Tick += (_,_) => ram.Refresh(true); timer.Start();
    }
    public void Reload() {
        try {
            _ = host.Instance().PathIndie;
            view.Isolation.SelectedIndex = host.Boolean("VersionArgumentIndieV2",host.Instance()) ? 0 : 1; UpdateJavaList();
            view.Login.SelectedIndex = host.Integer("VersionServerLogin",host.Instance()); view.SetLoginLast(view.Login.SelectedIndex); UpdateServerLoginUi();
            if (host.Integer("VersionAdvanceAssets",host.Instance()) == 2) { host.Info("已迁移老版本的关闭文件校验设置"); host.Reset("VersionAdvanceAssets",host.Instance()); host.Set("VersionAdvanceAssetsV2",true,host.Instance()); }
            host.RefreshSettings(); RamType(host.Integer("VersionRamType",host.Instance()));
        } catch (Exception ex) { host.Error(ex,"重载版本独立设置时出错"); }
    }
    public void Reset() {
        try {
            host.ResetSettings(); host.Reset("VersionServerLogin",host.Instance()); host.Reset("VersionArgumentIndieV2",host.Instance()); host.Reset("VersionAdvanceAssets",host.Instance()); Configs.JavaForced.Reset(host.Instance().Config); host.JavaRefresh().Start();
            host.Info("已初始化版本独立设置"); host.HintGreen("已初始化版本独立设置！",false);
        } catch (Exception ex) { host.ErrorAlert(ex,"初始化版本独立设置失败"); }
        Reload();
    }
    public void RamType(int type) { if (view.Slider is null) return; view.Slider.IsEnabled = type == 1; }
    public void NormalizeServerPunctuation() { string text = view.ServerEnter.Text.Replace("：",":").Replace("。","."); if (text == view.ServerEnter.Text) return; int position=view.ServerEnter.SelectionStart; view.ServerEnter.Text=text; view.ServerEnter.SelectionStart=position; }
    public void ServerLoginChanged() {
        if (host.Animation()!=0) return; UpdateServerLoginUi();
        if (view.Login.SelectedIndex==3 && !view.NideValidated() || view.Login.SelectedIndex==4 && !view.AuthValidated()) return;
        if (view.LoginLast()==view.Login.SelectedIndex) return; view.SetLoginLast(view.Login.SelectedIndex); host.Set("VersionServerLogin",view.Login.SelectedIndex,host.Instance());
    }
    public void UpdateServerLoginUi() {
        if (view.NideControls[0] is null) return;
        foreach (FrameworkElement item in view.NideControls) item.Visibility=view.Login.SelectedIndex==3 ? Visibility.Visible : Visibility.Collapsed;
        foreach (FrameworkElement item in view.AuthControls) item.Visibility=view.Login.SelectedIndex==4 ? Visibility.Visible : Visibility.Collapsed;
        view.ResizeServer();
    }
    public void SetLittleSkin() {
        if (view.AuthServer.Text != "" && view.AuthServer.Text != "https://littleskin.cn/api/yggdrasil" && host.Confirm("即将把第三方登录设置覆盖为 LittleSkin 登录。\r\n除非你是服主，或者服主要求你这样做，否则请不要继续。\r\n\r\n是否确实需要覆盖当前设置？","设置覆盖确认","继续","取消",false)==2) return;
        view.AuthServer.Text="https://littleskin.cn/api/yggdrasil"; view.AuthRegister.Text="https://littleskin.cn/auth/register"; view.AuthName.Text="LittleSkin 登录";
    }
    public void ReloadCurrentJava() => host.ReloadCurrentJava();
    public void UpdateJavaRelatedUi() => host.RunInUi(UpdateJavaUi);
    private void Hint(string text,int theme) => view.SetJavaHint(text,theme);
    public void UpdateJavaUi() {
        RedoableWorker<Java> worker=host.JavaWorker();
        switch (view.JavaKind.SelectedIndex) {
            case 0: case 2:
                view.JavaHint.Visibility=Visibility.Visible; view.JavaRange.Visibility=Visibility.Collapsed; view.JavaSelectPanel.Visibility=Visibility.Collapsed;
                if (!worker.LastSucceeded || worker.Running) { if (!worker.Running) worker.Start(); Hint("正在查找 Java……",0); }
                else if (worker.LastResult is null) { if (view.JavaKind.SelectedIndex==0) Hint("你的电脑上没有可供该版本使用的 Java，PCL 会在启动游戏时自动下载。",2); else Hint("该版本的版本文件夹中没有发现任何 Java！\r\n请点击此处打开版本文件夹，然后将 Java 文件夹复制进去。",1); }
                else Hint($"将会使用：{worker.LastResult}",0);
                break;
            case 1:
                view.JavaHint.Visibility=Visibility.Visible; view.JavaRange.Visibility=Visibility.Visible; view.JavaSelectPanel.Visibility=Visibility.Collapsed;
                if (!host.HasSaved("VersionArgumentJavaRange",host.Instance())) { string range=host.JavaRequirement(host.Instance()).ToString().Replace("-∞","").Replace("+∞",""); host.Set("VersionArgumentJavaRange",range,host.Instance()); view.JavaRange.Text=range; }
                string error=view.JavaRangeError(); if (!string.IsNullOrEmpty(error)) { Hint("Java 版本范围错误："+error,1); return; }
                if (!worker.LastSucceeded || worker.Running) { if (!worker.Running) worker.Start(); Hint("正在查找 Java……",0); }
                else if (worker.LastResult is null) Hint("你的电脑上没有任何 Java 符合该版本范围！\r\n如果 Mojang 提供了对应版本的 Java，PCL 会在启动游戏时自动下载。",2);
                else Hint($"将会使用：{worker.LastResult}",0);
                break;
            case 3: view.JavaHint.Visibility=Visibility.Collapsed; view.JavaRange.Visibility=Visibility.Collapsed; view.JavaSelectPanel.Visibility=Visibility.Visible; break;
        }
    }
    public void JavaHintClick() { if (view.JavaKind.SelectedIndex==2) host.OpenInstanceFolder(host.Instance()); }
    public void UpdateJavaList() {
        if (view.JavaSelect is null || view.JavaLabel is null || !view.JavaSelect.IsLoaded) return;
        view.JavaSelect.Items.Clear();
        if (host.JavaRefresh().Running) { view.JavaSearch.IsEnabled=false; view.JavaSelect.IsEnabled=false; view.JavaLabel.Text="搜索中 …"; return; }
        view.JavaSearch.IsEnabled=true; view.JavaSelect.IsEnabled=true;
        Java? selected=Configs.JavaForced.Get(host.Instance().Config);
        view.JavaLabel.Text=!Configs.JavaList.Get()!.Any() ? "未找到 Java，点击以导入已有的 Java" : selected is null ? "点击选择 Java …" : selected.ToString();
        try {
            foreach (Java entry in Configs.JavaList.Get()!) {
                TItem item=view.CreateJavaItem(entry,entry.ToString()); item.MouseLeftButtonUp+=(_,e)=>e.Handled=true;
                view.OnJavaClick(item,()=> { Configs.JavaForced.Set(entry,host.Instance().Config); UpdateJavaList(); view.JavaSelect.IsDropDownOpen=false; }); view.JavaSelect.Items.Add(item);
                bool official=PathUtils.IsParentOf($@"{Paths.AppData}.minecraft\runtime\",entry.Folder);
                TButton delete=view.CreateJavaButton(false); delete.IsEnabled=!official; delete.ToolTip=delete.IsEnabled ? "从列表中移除" : "无法移除官方 Java"; view.OnButtonClick(delete,()=>host.RemoveJava(entry)); ToolTipService.SetShowOnDisabled(delete,true);
                TButton open=view.CreateJavaButton(true); open.ToolTip="打开文件夹"; view.OnButtonClick(open,()=>host.Explorer(entry.JavaExePath)); view.SetButtons(item,new List<TButton>{delete,open});
            }
            TItem import=view.CreateJavaItem(null," 导入电脑中已有的 Java…"); import.MouseLeftButtonUp+=(_,e)=> { e.Handled=true; host.ImportJava(); }; view.JavaSelect.Items.Add(import);
        } catch (Exception ex) { host.Error(ex,"更新 Java 下拉框失败"); }
    }
    public void SearchJava() => host.SearchJava();
    public void IsolationChanged(ComboBox sender,SelectionChangedEventArgs e) {
        if (host.Animation()!=0 || isReverting) return;
        if (host.Confirm("调整版本隔离设置后，你需要游戏存档、Mod 等文件手动迁移到新的游戏文件夹中。\r\n如果发现存档消失，把这项设置改回来就能恢复。\r\n如果你不会迁移存档，不建议修改这项设置！","警告","我知道我在做什么","取消",true)==2) { isReverting=true; if (e.RemovedItems.Count>0) view.Isolation.SelectedItem=e.RemovedItems[0]; isReverting=false; }
        host.Set("VersionArgumentIndieV2",sender.SelectedIndex==0,host.Instance());
    }
    public void AdvanceRunChanged() => view.AdvanceRunWait.Visibility=view.AdvanceRun.Text!="" ? Visibility.Visible : Visibility.Collapsed;
    public void SwitchGlobal() => host.SwitchGlobal();
    public static void ReplaceEnter(TextBox sender) { string text=sender.Text.ReplaceLineEndings(" ",true); if (text==sender.Text) return; int caret=sender.CaretIndex; sender.Text=text; sender.CaretIndex=Math.Max(0,caret-1); }
}
