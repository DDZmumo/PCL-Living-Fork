// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeloongCore;
using SchematicKit;

namespace PCL
{
    /// <summary>
    ///     「更多 → 投影管理」：沿用 LF 的卡片/列表外观，汇总所有实例的投影文件，
    ///     点击行或行内按钮在该窗口的浮层里做 3D 预览。
    /// </summary>
    public partial class PageSchematicManager
    {
        private const string CubeLogo = "M12,2 L21.5,7.2 V16.8 L12,22 L2.5,16.8 V7.2 Z M12,4.2 L4.5,8.4 V15.6 L12,19.8 L19.5,15.6 V8.4 Z M12,11.4 L4.8,8.4 M12,11.4 L19.2,8.4 M12,11.4 V19.6";

        private SchematicSortOrder Order = SchematicSortOrder.ModifiedTime;
        private System.Windows.Threading.DispatcherTimer RetryTimer;
        private int RetryCount;

        public PageSchematicManager()
        {
            SchematicSupport.EnsureInitialized();
            InitializeComponent();
            BtnSort.Click += (Sender, Args) => ToggleSort();
            BtnRefresh.Click += (Sender, Args) => Render();
            BtnOpenFolder.Click += (Sender, Args) => OpenFolder();
            SearchBox.TextChanged += (Sender, Args) => Render();
            PageEnter += Render;
            // 切到别的页面时只收起预览、恢复页面内容，不要触发返回/导航。
            PageExit += () =>
            {
                SchematicPreviewOverlay.HideIn(PanPreview);
                PanPreview.Visibility = Visibility.Collapsed;
                PanBack.Visibility = Visibility.Visible;
            };
            Render();
        }

        /// <summary>版本列表是懒加载的，进得太早时每秒重试，直到扫出投影或超时。</summary>
        private void EnsureInstancesLoaded()
        {
            if (SchematicSupport.Service.Instances.Count > 0)
            {
                StopRetry();
                return;
            }
            RetryTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            if (RetryTimer.IsEnabled) return;
            RetryCount = 0;
            RetryTimer.Tick += RetryTick;
            RetryTimer.Start();
        }

        private void RetryTick(object Sender, EventArgs Args)
        {
            RetryCount++;
            Render();
            if (SchematicSupport.Service.Instances.Count > 0 || RetryCount >= 12) StopRetry();
        }

        private void StopRetry()
        {
            if (RetryTimer is null || !RetryTimer.IsEnabled) return;
            RetryTimer.Stop();
            RetryTimer.Tick -= RetryTick;
        }

        private void ToggleSort()
        {
            Order = Order == SchematicSortOrder.ModifiedTime ? SchematicSortOrder.FileName : SchematicSortOrder.ModifiedTime;
            Render();
        }

        private void ClosePreview() => SchematicPreviewOverlay.HideIn(PanPreview);

        private void Render()
        {
            try
            {
                BtnSort.Text = Order == SchematicSortOrder.ModifiedTime ? "排序：修改时间" : "排序：文件名";
                string Search = SearchBox.Text?.Trim() ?? "";
                SchematicInstance Current = SchematicSupport.Service.Current ?? SchematicSupport.Service.Instances.FirstOrDefault();
                LabInstName.Text = Current?.Name ?? "未选择实例";
                LabInstPath.Text = Current?.SchematicFolder ?? "";
                var Files = Current is null
                    ? new List<SchematicFileEntry>()
                    : SchematicSupport.Service.Scan(Current, Order, Search).ToList();

                PanList.Children.Clear();
                LabCount.Text = $"共 {Files.Count} 个投影文件";
                LabEmpty.Visibility = Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                LabEmpty.Text = string.IsNullOrEmpty(Search)
                    ? "还没有投影文件。把 .litematic / .schem / .nbt 放进实例的 schematics 文件夹后再刷新。"
                    : "没有匹配的投影文件。";

                foreach (SchematicFileEntry File in Files) PanList.Children.Add(CreateRow(Current, File));

                EnsureInstancesLoaded();
            }
            catch (Exception Error)
            {
                Logger.Error(Error, "刷新投影管理页失败");
            }
        }
        private FrameworkElement CreateRow(SchematicInstance Instance, SchematicFileEntry Entry)
        {
            var Row = new MyListItem
            {
                Logo = CubeLogo,
                Title = Entry.DisplayName,
                Info = $"{SchematicInfo.FormatSize(Entry.Size)} · {Entry.Modified:g} · {Entry.RelativePath}",
                Height = 46,
                Type = MyListItem.CheckType.Clickable,
                Margin = new Thickness(0, 1, 0, 1)
            };
            Row.MouseRightButtonUp += (Sender, Args) => ModBase.OpenExplorer(Entry.Path);
            Row.Click += (Sender, Args) => PreviewFile(Instance, Entry);

            // 玻璃界面只转发 LF 控件自身的点击；预览做成独立 MyButton，保证点得到。
            var Preview = new MyButton
            {
                Text = "预览",
                MinWidth = 70,
                Height = 32,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Preview.Click += (Sender, Args) => PreviewFile(Instance, Entry);

            var Line = new Grid();
            Line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Line.Children.Add(Row);
            Grid.SetColumn(Preview, 1);
            Line.Children.Add(Preview);
            return Line;
        }

        private void PreviewFile(SchematicInstance Instance, SchematicFileEntry Entry)
        {
            try
            {
                // 预览时把页面其它 UI 收起来，返回再恢复，避免预览压住列表/搜索框。
                PanBack.Visibility = Visibility.Collapsed;
                SchematicPreviewOverlay.ShowSeView(PanPreview, Entry.Path, SchematicSupport.ResolveJarPath(Instance), Entry.DisplayName,
                    () =>
                    {
                        PanPreview.Visibility = Visibility.Collapsed;
                        PanBack.Visibility = Visibility.Visible;
                        ModMain.FrmMain?.RefreshGlassShell();
                    });
                ModMain.FrmMain?.RefreshGlassShell();
            }
            catch (Exception Error)
            {
                Logger.Error(Error, "打开投影预览失败");
                ModMain.Hint("打开投影预览失败，详见日志。", ModMain.HintType.Red);
            }
        }

        private void OpenFolder()
        {
            SchematicInstance Instance = SchematicSupport.Service.Current
                ?? SchematicSupport.Service.Instances.FirstOrDefault();
            if (Instance is null) return;
            try
            {
                Directory.CreateDirectory(Instance.SchematicFolder);
                ModBase.OpenExplorer(Instance.SchematicFolder);
            }
            catch (Exception Error)
            {
                Logger.Error(Error, "打开投影文件夹失败");
            }
        }

        /// <summary>当前实例变化时刷新默认 JAR 推导，预览时按实例重新解析。</summary>
        internal void Reload() => Render();
    }
}
