using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using PCLLF.Presentation.Controls;

namespace PCLCS;
// Whole LF PageSetupLeft/Link and PageOther pages; original repository LICENCE.
// CE stable 1a7653127d578efdb2a9388999a3fdc67d9e49d5's SetupLeft,
// HelpDetail and ToolsTest were reviewed. LF page IDs/hidden/sponsor/link/tool
// policies and all actual old-name data remain the base; no CE profile is imported.
public sealed class LegacyAuxiliaryPageSlot
{
    public Func<MyPageRightView?> Read = null!;
    public Action<MyPageRightView> Write = null!, Reset = null!;
    public Func<MyPageRightView> Create = null!;
    public MyPageRightView Get()
    {
        if (Read()is null)
            Write(Create());
        return Read()!;
    }
}

public sealed class LegacyAuxiliaryNavigationHost
{
    public LegacySetupPageHost Common = null!;
    public Func<bool> HiddenForceShow = null!, ModpackEnabled = null!, LegacyLink = null!;
    public Action HiddenRefresh = null!, ApplyModpackVisibility = null!, RefreshHelp = null!;
    public Func<int, int> NormalizeSetup = null!;
    public Func<MyPageRightView> LauncherSettingsPage = null!;
    public Func<int> PageId = null!;
    public Action<int> SetPageId = null!;
    public Func<object, int> ParseTag = null!;
    public Func<int, string> FormatNumber = null!, FormatEnum = null!;
    public Func<MyPageRightView> Target = null!;
    public Action<MyPageRightView> SetTarget = null!;
    public Func<Border> MainRight = null!;
    public LegacyAuxiliaryPageSlot Launch = null!, UI = null!, Link = null!, System = null!, Update = null!, Personal = null!, Help = null!, About = null!, Test = null!;
}

public sealed class LegacyAuxiliaryNavigationView
{
    public Func<MyListItemView> Launch = null!, UI = null!, Link = null!, System = null!, Help = null!, About = null!, Test = null!;
}

public sealed class LegacyAuxiliaryBasicView
{
    public Func<DependencyObject> Page = null!;
    public Func<ScrollViewer> Scroll = null!;
    public Func<MyListItemView> About = null!;
}

public sealed class LegacyAuxiliaryBasicHost
{
    public LegacySetupPageHost Common = null!;
    public Func<string> LauncherVersion = null!;
    public Action CheckUpdate = null!;
}
