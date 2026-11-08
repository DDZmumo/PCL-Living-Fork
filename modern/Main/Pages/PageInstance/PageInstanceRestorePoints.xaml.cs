// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json.Linq;
using PCLCS;
using HistoryController = PCLCS.EnvironmentHistoryPageController<PCL.ModMinecraft.McInstance>;
using HistoryHost = PCLCS.EnvironmentHistoryPageHost<PCL.ModMinecraft.McInstance>;
using HistoryView = PCLCS.EnvironmentHistoryPageView<PCL.ModMinecraft.McInstance>;

namespace PCL
{
    public partial class PageInstanceRestorePoints : IRefreshable
    {
        private readonly HistoryController Controller;
        private readonly ModLoader.LoaderTask<string, List<JObject>> PointsLoader;
        private readonly EnvironmentHistoryUi.ChangedEventHandler HistoryChangedHandler;
        public PageInstanceRestorePoints()
        {
            __LfAttachOwnHandles();
            HistoryChangedHandler = Refresh;
            this.InitializeComponent();
            Controller = new HistoryController(CreateView(), CreateHost());
            PointsLoader = new ModLoader.LoaderTask<string, List<JObject>>("读取还原点", Task => Task.Output = EnvironmentHistory.Points(Task.Input));
            Init();
        }

        private HistoryView CreateView()
        {
            return new HistoryView()
            {
                Root = this,
                CardRetention = CardRetention,
                PanRetention = PanRetention,
                CardList = CardList,
                RestoreButton = BtnRestore,
                Entries = PanEntries,
                Empty = LabEmpty,
                Select = LabSelect,
                Search = new Func<string>(() => this.SearchBox.Text),
                SetSearch = new Action<string>(Value => this.SearchBox.Text = Value),
                RetentionText = new Func<string>(() => this.TextRetention.Text),
                SetRetentionText = new Action<string>(Value => this.TextRetention.Text = Value),
                SetAllText = new Action<string>(Value => this.FilterAll.Text = Value),
                SetHealthyText = new Action<string>(Value => this.FilterHealthy.Text = Value),
                AutoCleanup = new Func<bool>(() => this.CheckAutoCleanup.Checked),
                SetAutoCleanup = new Action<bool>(Value => this.CheckAutoCleanup.Checked = Value),
                RetentionHasFocus = new Func<bool>(() => this.TextRetention.IsKeyboardFocusWithin),
                SearchHasFocus = new Func<bool>(() => this.SearchBox.IsKeyboardFocusWithin),
                CheckAll = new Action(() => this.FilterAll.Checked = true),
                ForceRetentionResize = new Action(() => this.CardRetention.TriggerForceResize()),
                ScrollHome = new Action(() => this.PanBack.ScrollToHome()),
                CreateRow = new Func<IEnvironmentHistoryPageRow>(() => new RowAdapter(new MyLocalModItem() { SelectionBar = CardSelect })),
                AdaptRow = new Func<object, IEnvironmentHistoryPageRow>(Sender => new RowAdapter((MyLocalModItem)Sender)),
                CreateIcon = CreateIcon,
                RestoreLogo = ModBase.Logo.IconButtonRefresh,
                DetailsLogo = ModBase.Logo.IconButtonInfo,
                OpenLogo = ModBase.Logo.IconButtonOpen,
                DeleteLogo = ModBase.Logo.IconButtonDelete,
                RefreshSelectionBar = new Func<int, int, bool, int>((Count, Previous, Animate) =>
                {
                    LocalResourceUi.RefreshSelectionBar(this.CardSelect, this.TransSelect, Count, ref Previous, "Restore Point Selection " + this.PageUuid, Animate);
                    return Previous;
                })
            };
        }

        private HistoryHost CreateHost()
        {
            return new HistoryHost()
            {
                CurrentInstance = new Func<ModMinecraft.McInstance>(() => PageInstanceLeft.Instance),
                VersionPath = new Func<ModMinecraft.McInstance, string>(Instance => Instance.PathVersion),
                AutoCleanup = new Func<ModMinecraft.McInstance, bool>(Instance => Settings.Get<bool>("VersionEnvironmentHistoryAutoCleanup", Instance: Instance)),
                Retention = new Func<ModMinecraft.McInstance, int>(Instance => Settings.Get<int>("VersionEnvironmentHistoryRetention", Instance: Instance)),
                SetAutoCleanup = new Action<ModMinecraft.McInstance, bool>((Instance, Value) => Settings.Set("VersionEnvironmentHistoryAutoCleanup", Value, Instance: Instance)),
                SetRetention = new Action<ModMinecraft.McInstance, int>((Instance, Value) => Settings.Set("VersionEnvironmentHistoryRetention", Value, Instance: Instance)),
                StartLoader = new Action<string>(VersionFolder => PointsLoader.Start(VersionFolder, IsForceRestart: true)),
                CancelLoader = new Action(() => PointsLoader.Cancel()),
                LoaderFailed = new Func<bool>(() => PointsLoader.State == ModBase.LoadState.Failed),
                AnimationEnabled = new Func<bool>(() => ModAnimation.AniControlEnabled == 0),
                BindChanged = BindChanged,
                Start = new Action<ModMinecraft.McInstance, bool, string, IEnumerable<string>>((Instance, Restore, PointId, DeleteIds) => EnvironmentHistoryUi.Start(Instance, Restore, PointId, DeleteIds)),
                HintError = new Action<string>(Message => ModMain.Hint(Message, ModMain.HintType.Red)),
                OpenFolder = new Action<string>(VersionFolder => EnvironmentHistory.OpenFolder(VersionFolder)),
                ShowDetails = new Action<string, string>((Text, Title) => ModMain.MyMsgBox(Text, Title))
            };
        }

        private void BindChanged(bool Attach)
        {
            if (Attach)
            {
                EnvironmentHistoryUi.Changed += HistoryChangedHandler;
            }
            else
            {
                EnvironmentHistoryUi.Changed -= HistoryChangedHandler;
            }
        }

        private static EnvironmentHistoryPageIcon CreateIcon(string Logo, double? Scale, string Tip)
        {
            var Button = new MyIconButton()
            {
                Logo = Logo
            };
            if (Scale.HasValue)
                Button.LogoScale = Scale.Value;
            Button.ToolTip = Tip;
            return new EnvironmentHistoryPageIcon(Button, Clicked => Button.Click += (Sender, Args) => Clicked());
        }

        private sealed class RowAdapter : IEnvironmentHistoryPageRow
        {
            private readonly MyLocalModItem Row;
            public RowAdapter(MyLocalModItem Row)
            {
                this.Row = Row;
            }

            public FrameworkElement Element
            {
                get
                {
                    return Row;
                }
            }

            public JObject Point
            {
                get
                {
                    return Row.RestorePoint;
                }
            }

            public bool Checked
            {
                get
                {
                    return Row.Checked;
                }

                set
                {
                    Row.Checked = value;
                }
            }

            public void Bind(JObject Point)
            {
                Row.BindRestorePoint(Point);
            }

            public void SetButtons(List<EnvironmentHistoryPageIcon> Buttons)
            {
                Row.Buttons = Buttons.Select(Button => (MyIconButton)Button.Element).ToList();
            }

            public void Subscribe(Action Click, Action<object> Changed)
            {
                Row.Click += (Sender, Args) => Click();
                Row.Changed += (Sender, e) => Changed(Sender);
            }
        }

        // 同名状态入口仅转发到控制器，原条目与选择集合没有页面副本。
        private List<JObject> Entries
        {
            get
            {
                return Controller.Entries;
            }

            set
            {
                Controller.Entries = value;
            }
        }

        private HashSet<string> SelectedIds
        {
            get
            {
                return Controller.SelectedIds;
            }
        }

        private bool Rendering
        {
            get
            {
                return Controller.Rendering;
            }

            set
            {
                Controller.Rendering = value;
            }
        }

        private int BarCount
        {
            get
            {
                return Controller.BarCount;
            }

            set
            {
                Controller.BarCount = value;
            }
        }

        private bool HealthyOnly
        {
            get
            {
                return Controller.HealthyOnly;
            }

            set
            {
                Controller.HealthyOnly = value;
            }
        }

        private ModMinecraft.McInstance Owner
        {
            get
            {
                return Controller.Owner;
            }

            set
            {
                Controller.Owner = value;
            }
        }

        private string PendingFocusId
        {
            get
            {
                return Controller.PendingFocusId;
            }

            set
            {
                Controller.PendingFocusId = value;
            }
        }

        private bool BindingRetention
        {
            get
            {
                return Controller.BindingRetention;
            }

            set
            {
                Controller.BindingRetention = value;
            }
        }

        private bool RetentionDirty
        {
            get
            {
                return Controller.RetentionDirty;
            }

            set
            {
                Controller.RetentionDirty = value;
            }
        }

        internal void FocusPoint(string Id)
        {
            Controller.FocusPoint(Id);
        }

        private void Init()
        {
            this.DisabledPageAnimControls.Add(this.CardSelect);
            LocalResourceUi.ApplySelectionIcons(this.BtnRestore, this.BtnDelete, this.BtnDeselect);
            this.BtnRestore.Logo = ModBase.Logo.IconButtonRefresh;
            this.FilterAll.LabText.Margin = new Thickness(-2, 0d, 8d, 0d);
            this.FilterHealthy.LabText.Margin = new Thickness(-2, 0d, 8d, 0d);
            this.PageLoaderInit(this.Load, this.PanLoad, this.PanBack, (FrameworkElement)null, PointsLoader, (_) => LoadedPoints(), AutoRun: false);
        }

        private void PageLoaded()
        {
            Controller.PageLoaded();
        }

        private void PageUnloaded()
        {
            Controller.PageUnloaded();
        }

        public void Refresh()
        {
            Controller.Refresh();
        }

        private void BindRetention()
        {
            Controller.BindRetention();
        }

        private void AutoCleanupChanged(object Sender, bool User)
        {
            Controller?.AutoCleanupChanged(User);
        }

        private void RetentionEdited()
        {
            Controller?.RetentionEdited();
        }

        private void RetentionLostFocus()
        {
            Controller.CommitRetention();
        }

        private void RetentionKey(object Sender, KeyEventArgs e)
        {
            Controller.RetentionKey(Sender, e);
        }

        internal bool CommitRetention()
        {
            return Controller.CommitRetention();
        }

        private void LoadedPoints()
        {
            Controller.LoadedPoints(() => PointsLoader.Output);
        }

        private void SearchChanged()
        {
            Controller?.RenderList();
        }

        private void FilterChanged(MyRadioButton Sender, bool User)
        {
            Controller?.FilterChanged(object.ReferenceEquals(Sender, this.FilterHealthy));
        }

        internal void RenderList()
        {
            Controller.RenderList();
        }

        private List<MyIconButton> PointButtons(JObject Point)
        {
            return Controller.PointButtons(Point).Select(Button => (MyIconButton)Button.Element).ToList();
        }

        private void SelectionChanged(object Sender, ModBase.RouteEventArgs e)
        {
            Controller.SelectionChanged(Sender);
        }

        private void RefreshSelection()
        {
            Controller.RefreshSelection();
        }

        internal void ChangeAllSelected(bool Value)
        {
            Controller.ChangeAllSelected(Value);
        }

        private void SelectAll()
        {
            Controller.SelectAll();
        }

        private void Deselect()
        {
            Controller.Deselect();
        }

        private void SelectionKeys(object Sender, KeyEventArgs e)
        {
            Controller.SelectionKeys(Sender, e);
        }

        private void DeleteSelected()
        {
            Controller.DeleteSelected();
        }

        private void CreatePoint()
        {
            Controller.CreatePoint();
        }

        private void RestorePoint()
        {
            Controller.RestorePoint();
        }

        private void OpenFolder()
        {
            Controller.OpenFolder();
        }

        private void Retry()
        {
            Controller.Retry();
        }
    }
}
