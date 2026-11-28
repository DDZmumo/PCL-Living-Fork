// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeloongCore;
using PCLCS;
using Entry = PCL.ModMain.HelpEntry;

namespace PCL;

internal static class LauncherHelpCatalog
{
    internal static bool IsVisible(Entry entry) =>
        ModBase.BuildType == ModBase.BuildTypes.Release ? entry.ShowInPublic : entry.ShowInSnapshot;

    internal static void Open(Entry entry)
    {
        try
        {
            if (entry.IsEvent) CustomEvent.Raise(entry.EventType, entry.EventData);
            else ModMain.FrmMain?.PageChange(new FormMain.PageStackData { Page = FormMain.PageType.HelpDetail, Additional = new object[] { entry } });
        }
        catch (Exception error) { Logger.Error(error, "处理帮助项目点击时发生意外错误"); }
    }

    internal static void Open(string location) => ModBase.RunInThread(() =>
    {
        try {
            Entry entry;
            if (LauncherContentActions.RemoteHelpAddress(location) is { } address) {
                var markup = new UriBuilder(address) { Path = address.AbsolutePath[..^5] + ".xaml" };
                entry = new Entry(address.AbsoluteUri,
                    () => LauncherContentDownload.FetchTextAsync(address.AbsoluteUri, LauncherContentDownload.HelpMetadataBytes).GetAwaiter().GetResult(),
                    () => LauncherContentDownload.FetchTextAsync(markup.Uri.AbsoluteUri, LauncherContentDownload.MarkupBytes).GetAwaiter().GetResult());
                if (entry.IsEvent) throw new InvalidDataException("联网帮助链接需要指向一个帮助页面。");
            } else {
                string name = LauncherContentActions.LocalHelpName(location) ?? throw new InvalidDataException("帮助地址无效。");
                if (ModMain.HelpLoader.State != ModBase.LoadState.Finished) ModMain.HelpLoader.WaitForExit(ModBase.GetUuid());
                entry = ModMain.HelpLoader.Output.FirstOrDefault(item => MatchesHelpName(item, name)) ??
                    throw new FileNotFoundException("没有找到已加载的帮助条目。");
            }
            if (!entry.IsEvent || entry.EventType != CustomEvent.EventType.打开帮助) ModBase.RunInUi(() => Open(entry));
        } catch (Exception error) { Logger.Warn(error, "无法打开自定义帮助条目", LogBehavior.Toast); }
    });

    private static bool MatchesHelpName(Entry entry, string name) => new[] {
        Path.Combine(Paths.Base, "PCL", "Help"), Path.Combine(ModBase.PathTemp, "Help")
    }.Any(root => string.Equals(Path.GetRelativePath(root, entry.RawPath), name, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<Match> Find(IEnumerable<Entry> entries, string query)
    {
        var input = entries.Where(entry => IsVisible(entry) && entry.ShowInSearch).Select(entry => new Match {
            Item = entry, Sources = new() { new(entry.Title, 1), new(entry.Desc, .5), new(entry.Search, 1.5) }
        }).ToList();
        return LegacyBaseSearch.Search<Match, Token>(input, query, 5, .08);
    }

    internal sealed class Match : ILegacyBaseSearchEntry<Token>
    {
        internal Entry Item = null!;
        public List<Token> Sources { get; set; } = new();
        public bool AbsoluteRight { get; set; }
        public double Similarity { get; set; }
    }

    internal sealed class Token(string? text, double weight) : ILegacyBaseSearchSource
    {
        public string[] Aliases { get; } = [text ?? ""];
        public double Weight { get; } = weight;
    }
}
