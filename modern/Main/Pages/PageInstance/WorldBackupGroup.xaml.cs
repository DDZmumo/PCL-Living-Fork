// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using MeloongCore;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PCL
{
    // 嵌入存档行下方；只显示父页传入的记录，选择由父页的统一操作栏处理。
    public partial class WorldBackupGroup
    {
        private static readonly Dictionary<string, bool> ExpandedStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private ModMinecraft.McInstance Owner;
        private string Identifier;
        private string StateKey;
        private List<JObject> Entries;
        private readonly HashSet<string> Selection;
        private bool Rendering;
        private FrameworkElement SharedBar;
        internal event SelectionChangedEventHandler SelectionChanged;
        internal delegate void SelectionChangedEventHandler(WorldBackupGroup Sender);
        internal Func<bool> BeforeCreate { get; set; }

        internal IReadOnlyCollection<string> SelectedIds
        {
            get
            {
                return new ReadOnlyCollection<string>(Selection.ToList());
            }
        }

        internal string FolderName
        {
            get
            {
                return Identifier;
            }
        }

        internal bool IsExpanded
        {
            get
            {
                return !this.CardBackups.IsSwapped;
            }
        }

        internal FrameworkElement SelectionBar
        {
            get
            {
                return SharedBar;
            }

            set
            {
                SharedBar = value;
                foreach (MyLocalModItem Row in this.PanEntries.Children)
                    Row.SelectionBar = value;
            }
        }

        public WorldBackupGroup()
        {
            __LfAttachOwnHandles();
            this.Entries = new List<JObject>();
            this.Selection = new HashSet<string>(StringComparer.Ordinal);
            this.InitializeComponent();
        }

        private void Init()
        {
            this.CardBackups.SwapControl = this.PanContent;
            this.BtnCreate.Logo = ModBase.Logo.IconButtonSave;
        }

        internal void Bind(ModMinecraft.McInstance Value, string WorldFolderName, List<JObject> Points)
        {
            if (Value is null)
                throw new ArgumentNullException(nameof(Value));
            if (string.IsNullOrWhiteSpace(WorldFolderName) || (Path.GetFileName(WorldFolderName) ?? "") != (WorldFolderName ?? "") || WorldFolderName == "." || WorldFolderName == ".." || WorldFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("存档文件夹名称无效。", nameof(WorldFolderName));
            string GameFolder = Value.PathIndie;
            string Key = Path.GetFullPath(PathUtils.RemoveExtendedPrefix(GameFolder)).TrimEnd('\\', '/') + '\0' + WorldFolderName;
            bool SameWorld = ReferenceEquals(Owner, Value) && string.Equals(StateKey, Key, StringComparison.OrdinalIgnoreCase);
            List<JObject> PreviousEntries = Entries;
            var PreviousSelection = Selection.ToList();
            if (!string.Equals(StateKey, Key, StringComparison.OrdinalIgnoreCase))
                Selection.Clear();
            Owner = Value;
            Identifier = WorldFolderName;
            StateKey = Key;
            // 使用实体文件夹标识归组，两个世界的显示名称相同也不会混入记录。
            Entries = (Points ?? new List<JObject>()).Where(Point => Point is not null && string.Equals(Point["world"]?.ToString(), Identifier, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch((Point["id"]?.ToString()) ?? "", @"\A[a-f0-9]{32}\z")).GroupBy(Point => Point["id"].ToString(), StringComparer.Ordinal).Select(Batch => Batch.First()).OrderByDescending(Point => Point["created"].ToObject<DateTime>()).ToList();
            Selection.IntersectWith(Entries.Select(Point => Point["id"].ToString()));
            bool Available;
            try
            {
                QuickPlayWorlds.WorldPath(GameFolder, Identifier);
                Available = true;
            }
            catch (Exception ex)when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Available = false;
            }

            this.BtnCreate.IsEnabled = Available;
            this.BtnCreate.ToolTip = Available ? "立即备份此存档；备份完成并校验后才会清理它的旧备份。" : "原存档已不存在，仍可恢复已有备份。";
            this.LabState.Text = Available ? "按备份时间从新到旧排列。" : "原存档已不存在，可从以下备份恢复。";
            this.LabEmpty.Text = Available ? "暂无备份，可先备份此存档。" : "此存档当前没有可用备份。";
            this.LabEmpty.Visibility = Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            this.CardBackups.Title = "备份（" + Entries.Count + "）";
            if (SameWorld && PreviousEntries.Count == Entries.Count && PreviousEntries.Zip(Entries, (Left, Right) => JToken.DeepEquals(Left, Right)).All(Same => Same))
                return;
            Rendering = true;
            this.PanEntries.Children.Clear();
            Rendering = false;
            this.CardBackups.SetContentBuilder(Panel => BuildRows());
            bool Expanded;
            ExpandedStates.TryGetValue(StateKey, out Expanded);
            this.CardBackups.IsSwapped = !Expanded;
            if (Expanded)
                this.CardBackups.BuildContent();
            if (!new HashSet<string>(PreviousSelection, StringComparer.Ordinal).SetEquals(Selection))
                SelectionChanged?.Invoke(this);
        }

        private void BuildRows()
        {
            Rendering = true;
            try
            {
                this.PanEntries.Children.Clear();
                foreach (var Point in Entries)
                {
                    var Row = new MyLocalModItem()
                    {
                        SelectionBar = SharedBar
                    };
                    Row.BindRestorePoint(Point);
                    Row.Checked = Selection.Contains(Point["id"].ToString());
                    Row.Buttons = BackupButtons(Point);
                    Row.Click += (Sender, Args) => Row.Checked = !Row.Checked;
                    Row.Changed += RowSelectionChanged;
                    this.PanEntries.Children.Add(Row);
                }
            }
            finally
            {
                Rendering = false;
            }
        }

        private void RememberExpansion()
        {
            if (StateKey is not null)
                ExpandedStates[StateKey] = !this.CardBackups.IsSwapped;
        }

        private void RowSelectionChanged(object Sender, ModBase.RouteEventArgs e)
        {
            if (Rendering)
                return;
            MyLocalModItem Row = (MyLocalModItem)Sender;
            string Id = Row.RestorePoint["id"].ToString();
            if (Row.Checked)
                Selection.Add(Id);
            else
                Selection.Remove(Id);
            SelectionChanged?.Invoke(this);
        }

        internal void ClearSelection(bool Notify = true)
        {
            bool HadSelection = Selection.Count > 0;
            Rendering = true;
            try
            {
                Selection.Clear();
                foreach (MyLocalModItem Row in this.PanEntries.Children)
                    Row.Checked = false;
            }
            finally
            {
                Rendering = false;
            }

            if (Notify && HadSelection)
                SelectionChanged?.Invoke(this);
        }

        private List<MyIconButton> BackupButtons(JObject Point)
        {
            var Restore = new MyIconButton()
            {
                Logo = ModBase.Logo.IconButtonRefresh,
                LogoScale = 0.85d,
                ToolTip = "恢复此世界"
            };
            var Details = new MyIconButton()
            {
                Logo = ModBase.Logo.IconButtonInfo,
                ToolTip = "备份详情"
            };
            var Open = new MyIconButton()
            {
                Logo = ModBase.Logo.IconButtonOpen,
                LogoScale = 1.05d,
                ToolTip = "打开备份文件夹"
            };
            var Delete = new MyIconButton()
            {
                Logo = ModBase.Logo.IconButtonDelete,
                ToolTip = "移除此备份"
            };
            Restore.Click += (Sender, Args) => WorldBackupsUi.Start(Owner, RestoreId: Point["id"].ToString());
            Details.Click += (Sender, Args) => ShowDetails(Point);
            Open.Click += (Sender, Args) => OpenBackup(Point["id"].ToString());
            Delete.Click += (Sender, Args) => WorldBackupsUi.Start(Owner, DeleteIds: new[] { Point["id"].ToString() });
            return new List<MyIconButton>()
            {
                Restore,
                Details,
                Open,
                Delete
            };
        }

        private void ShowDetails(JObject Point)
        {
            int FileCount = Point["fileCount"]?.ToObject<int>() ?? ((JObject)Point["files"]).Count;
            ModMain.MyMsgBox("世界：" + Point["label"].ToString() + Constants.vbCrLf + "文件夹：" + Identifier + Constants.vbCrLf + "备份时间：" + Point["created"].ToObject<DateTime>().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + Constants.vbCrLf + "备份原因：" + Point["reason"].ToString() + Constants.vbCrLf + "存档大小：" + StringUtils.FormatByteSize(Point["bytes"].ToObject<long>()) + Constants.vbCrLf + "压缩大小：" + StringUtils.FormatByteSize(Point["archiveBytes"].ToObject<long>()) + Constants.vbCrLf + "文件数量：" + FileCount + Constants.vbCrLf + Constants.vbCrLf + "恢复时会校验完整备份，并先自动保存当前世界。", "存档备份详情");
        }

        private void OpenBackup(string Id)
        {
            try
            {
                if (Owner is null || !Regex.IsMatch(Id, @"\A[a-f0-9]{32}\z"))
                    return;
                string Root = WorldBackups.Store(Owner.PathIndie);
                string Folder = Path.Combine(Root, "points", Id);
                InstanceResourceStore.EnsureWithin(Root, Folder);
                if (Directory.Exists(Folder))
                    ModBase.OpenExplorer(Folder);
                else
                    ModMain.Hint("此备份已移动或删除，请刷新存档列表。");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "打开存档备份目录失败");
                ModMain.Hint(ex.Message, ModMain.HintType.Red);
            }
        }

        private void CreateBackup()
        {
            if (BeforeCreate is not null && !BeforeCreate.Invoke())
                return;
            if (Owner is not null && this.BtnCreate.IsEnabled)
                WorldBackupsUi.Start(Owner, WorldIdentifier: Identifier);
        }
    }
}
