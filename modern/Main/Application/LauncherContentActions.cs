// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EventType = PCL.CustomEvent.EventType;

namespace PCL;

internal static class LauncherContentActions
{
    internal static bool Allows(EventType type, string argument)
    {
        if (argument.Length > 16384) return false;
        string[] parts = argument.Split('|', 3);
        return type switch {
            EventType.打开网页 => Uri.TryCreate(argument, UriKind.Absolute, out var web) &&
                web is { Scheme: "https" or "http", Host.Length: > 0, UserInfo.Length: 0 },
            EventType.打开帮助 => RemoteHelpAddress(argument) is not null || LocalHelpName(argument) is not null,
            EventType.启动游戏 => parts.Length == 1 && (argument == @"\current" || IsFileName(argument)),
            EventType.切换页面 => parts.Length <= 2 && Enum.TryParse(parts[0], out FormMain.PageType page) &&
                page is FormMain.PageType.Launch or FormMain.PageType.Download or FormMain.PageType.Link or FormMain.PageType.Setup or
                    FormMain.PageType.Other or FormMain.PageType.InstanceSelect or FormMain.PageType.TaskManager or FormMain.PageType.Favorites &&
                (parts.Length == 1 || Enum.TryParse(parts[1], out FormMain.PageSubType subpage) && Enum.IsDefined(subpage)),
            EventType.修改设置 or EventType.写入设置 => parts.Length >= 2 && int.TryParse(parts[1], out int value) &&
                (parts[0] == "UiLauncherColorMode" && value is 0 or 1 ||
                    parts[0] is "UiPanelOpacity" or "UiLauncherTransparent" or "UiBackgroundOpacity" && value is >= 0 and <= 1000),
            EventType.修改变量 or EventType.写入变量 => parts.Length >= 2 && Regex.IsMatch(parts[0], @"\A[\w-]{1,64}\z", RegexOptions.CultureInvariant) && parts[1].Length <= 1024,
            EventType.复制文本 or EventType.刷新主页 or EventType.刷新页面 or EventType.刷新帮助 or
            EventType.今日人品 or EventType.弹出窗口 or EventType.弹出提示 or EventType.检查更新 => true,
            _ => false
        };
    }

    internal static Uri? RemoteHelpAddress(string argument) =>
        Uri.TryCreate(argument, UriKind.Absolute, out var uri) && LauncherContentDownload.IsHttps(uri) &&
        uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? uri : null;

    internal static string? LocalHelpName(string argument)
    {
        if (Path.IsPathRooted(argument) || !argument.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
        string[] parts = argument.Split(['/', '\\']);
        return parts.All(IsFileName) ? string.Join(Path.DirectorySeparatorChar, parts) : null;
    }

    private static bool IsFileName(string name) => name.Length is > 0 and <= 255 && name is not ("." or "..") &&
        !name.EndsWith('.') && !name.EndsWith(' ') && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
