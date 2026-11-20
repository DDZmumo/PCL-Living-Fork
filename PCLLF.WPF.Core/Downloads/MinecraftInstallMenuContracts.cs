using System;
using System.Collections.Generic;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed class MinecraftInstallListItem {
    public string? Title, Info, Logo;
    public object? Tag;
}
public enum MinecraftInstallMenuIcon { Save, Info, Server, List }
public sealed class MinecraftInstallMenuButton {
    public MinecraftInstallMenuIcon Icon;
    public string ToolTip = null!;
    public double? LogoScale;
    public object? Tag;
    public Action<object, RoutedEventArgs> Click = null!;
}
public sealed class MinecraftInstallMenuHost<TView, TVirtual, TClick, TOptiFine, TForgeLike, TLite>
    where TView : FrameworkElement {
    public Func<Func<TView>, double, TVirtual> Virtualize = null!;
    public Func<MinecraftInstallListItem, TClick, Action<object, EventArgs>?, TView> CreateItem = null!;
    public Func<TVirtual, TView> Initialize = null!;
    public Func<TView, string> Title = null!, Info = null!;
    public Action<TView, string> SetInfo = null!;
    public Action<object, List<MinecraftInstallMenuButton>> SetButtons = null!;
    public Func<string> ImageRoot = null!;
    public Func<bool> DebugMode = null!;
    public Action<string> OpenWebsite = null!;
    public Action<TOptiFine> SaveOptiFine = null!;
    public Action<TForgeLike> SaveForgelike = null!;
    public Action<TLite> SaveLiteLoader = null!;
    public Action<Func<(string Title, JToken Json)>, bool> SaveMinecraft = null!;
}
