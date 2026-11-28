// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.Threading;
using MeloongCore;

namespace PCL;

internal static class LauncherAppearanceSettings
{
    private static string MusicFolder => Paths.Base + @"PCL\Musics\";
    private static string CustomFile => Paths.Base + @"PCL\Custom.xaml";

    internal static bool HiddenForceShow { get; set; }
    internal static void HiddenRefresh() => ModMain.FrmMain?.RefreshGlassShell();
    internal static void BackgroundRefresh(bool hint, bool refresh) => ModMain.FrmMain?.UpdateBackgroundAndTitleBar();

    internal static void OpenMusic() => ModBase.OpenExplorer(MusicFolder);
    internal static void RefreshMusic() => ModMusic.MusicRefreshPlay(true);
    internal static void ClearMusic()
    {
        if (ModMain.MyMsgBox("即将删除背景音乐文件夹中的所有文件。确定继续吗？", "清空背景音乐", "删除", "取消", IsWarn: true) != 1) return;
        ModBase.RunInThread(() => {
            ModMain.Hint("正在删除背景音乐……");
            ModMusic.MusicNAudio = null;
            ModMusic.MusicWaitingList = new List<string>();
            ModMusic.MusicAllList = new List<string>();
            Thread.Sleep(200);
            try {
                DirectoryUtils.Delete(MusicFolder);
                ModMain.Hint("背景音乐已删除！", ModMain.HintType.Green);
            }
            catch (Exception error) { Logger.Error(error, "删除背景音乐失败", LogBehavior.Alert); }
            try {
                DirectoryUtils.Create(MusicFolder);
                ModBase.RunInUi(() => ModMusic.MusicRefreshPlay(false));
            }
            catch (Exception error) { Logger.Error(error, "重建背景音乐文件夹失败", LogBehavior.Alert); }
        });
    }

    internal static void CreateCustomFile()
    {
        try {
            if (FileUtils.Exists(CustomFile) && ModMain.MyMsgBox("生成教学文件会覆盖现有主页文件。确定继续吗？", "覆盖确认", "继续", "取消", IsWarn: true) != 1) return;
            ModBase.ExtractResources(CustomFile, "Custom");
            ModMain.Hint("教学文件已生成！", ModMain.HintType.Green);
            ModBase.OpenExplorer(CustomFile);
        }
        catch (Exception error) { Logger.Error(error, "生成教学文件失败"); }
    }

    internal static void RefreshCustom()
    {
        FormMain.RefreshHomepage();
        ModMain.Hint("已刷新主页！", ModMain.HintType.Green);
    }

    internal static void CustomTutorial() => ModMain.MyMsgBox(
        "点击「生成教学文件」创建 Custom.xaml。用文本编辑器修改并保存，再点击「刷新主页」查看效果。\r\n\r\n也可以直接将主页文件拖入启动器窗口。",
        "主页自定义教程");

    internal static void OpenCustomFolder() => ModBase.OpenExplorer(FileUtils.Exists(CustomFile) ? CustomFile : Paths.Base + @"PCL\");
}
