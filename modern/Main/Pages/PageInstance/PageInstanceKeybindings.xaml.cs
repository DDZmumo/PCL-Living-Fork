// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using MeloongCore.Wpf;
using MeloongCore.Wpf.Extensions;
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using KeybindingController = global::PCLCS.KeybindingPageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceKeybinding>;
using KeybindingHost = global::PCLCS.KeybindingPageHost<global::PCL.ModMinecraft.McInstance>;
using KeybindingView = global::PCLCS.KeybindingPageView<global::PCL.InstanceKeybinding>;
using ThrottleDebounce;

namespace PCL
{
    public partial class PageInstanceKeybindings : global::PCL.IRefreshable
    {
        private readonly global::PCLCS.KeybindingPageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceKeybinding> Controller;
        private readonly global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Collections.Generic.List<global::PCL.InstanceKeybinding>> BindingsLoader;

        private global::PCL.FormMain KeyboardOwner;
        private global::System.Windows.Input.KeyEventHandler KeyboardKeyHandler;
        private global::System.Windows.Input.MouseButtonEventHandler KeyboardMouseHandler;
        private readonly Button BtnClear, BtnCancel;

        public PageInstanceKeybindings()
        {
            InitializeComponent();
            CardCapture.SetBackdropSource(PanBack);
            (BtnClear, BtnCancel) = CardCapture.AddKeyCaptureCommands(this, "BtnClear", "BtnCancel");
            // One column header above the bindings table; it hides with the list when there is nothing to show.
            if (PanBindings.Parent is Panel table) {
                var header = ColumnHeader();
                header.Margin = new Thickness(0, 12, 0, 4);
                table.Children.Insert(table.Children.IndexOf(PanBindings), header);
                void SyncHeader() => header.Visibility = LabEmpty.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                System.ComponentModel.DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(TextBlock))
                    .AddValueChanged(LabEmpty, (_, _) => SyncHeader());
                SyncHeader();
            }
            AttachScroll(PanBack);
            var placeholder = Theme.Note("搜索按键 / 模组名 / 文件名", 13);
            placeholder.IsHitTestVisible = false;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            placeholder.Margin = new Thickness(12, 0, 12, 0);
            GlassPageResources.BindTextPlaceholder(SearchBox, placeholder);
            SearchHost.Children.Add(placeholder);
            Controller = new KeybindingController(CreateView(), CreateHost());
            BindingsLoader = new("读取版本按键", ReadBindings);
            BindingsLoader.OnStateChangedUi += (_, state, _) => {
                if (!IsLoaded || BindingsLoader.State != state) return;
                if (state == ModBase.LoadState.Finished) BindingsLoaded();
                UpdateLoading();
            };
            Loaded += (_, _) => PageLoaded();
            Unloaded += (_, _) => PageUnloaded();
            SearchBox.TextChanged += (_, _) => FilterChanged();
            BtnFilterAll.Checked += (_, _) => Controller.FilterChecked(false);
            BtnFilterConflicts.Checked += (_, _) => Controller.FilterChecked(true);
            BtnCancel.Click += (_, _) => CancelCapture();
            BtnClear.Click += (_, _) => ClearBinding();
            BtnRetry.Click += (_, _) => RetryLoad();
        }

        private global::PCLCS.KeybindingPageView<global::PCL.InstanceKeybinding> CreateView()
        {
            return new global::PCLCS.KeybindingPageView<global::PCL.InstanceKeybinding>()
            {
                Root = this,
                CardBindings = CardBindings,
                Bindings = PanBindings,
                CancelButton = BtnCancel,
                ClearButton = BtnClear,
                Empty = LabEmpty,
                Capture = CardCapture.Status,
                Search = new Func<global::System.String>(() => this.SearchBox.Text),
                SetAllText = new Action<global::System.String>(Value => this.BtnFilterAll.Content = Value),
                SetConflictsText = new Action<global::System.String>(Value => this.BtnFilterConflicts.Content = Value),
                ScrollHome = PanBack.ScrollToTop,
                PrepareRow = PrepareBindingRow,
                CreateButton = global::PCL.PageInstanceKeybindings.CreateNativeButton,
                CreateGroup = CreateNativeGroup,
                RefreshCaptureBar = new Func<global::System.Int32, global::System.Int32, global::System.Boolean, global::System.Int32>((Count, Previous, Animate) =>
                {
                    CardCapture.ShowSelection(Count, Animate);
                    return Count;
                })
            };
        }

        internal static KeybindingPageButton CreateNativeButton(InstanceKeybinding entry, string text, bool conflict, string tooltip, Action clicked)
        {
            var button = GlassDialogControls.Button(text);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Height = 30;
            button.MinHeight = 30;
            button.FontSize = 13;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Tag = entry;
            button.ToolTip = tooltip;
            button.Click += (_, _) => clicked();
            void UpdateState(KeybindingPageButtonState state) {
                button.Style = GlassDialogControls.Style(state == KeybindingPageButtonState.Capturing ? "GlassPresentationAccentButton" : "GlassPresentationButton");
                if (state == KeybindingPageButtonState.Conflict)
                    button.SetResourceReference(Control.ForegroundProperty, "GlassPresentationWarnText");
                else button.ClearValue(Control.ForegroundProperty);
            }
            UpdateState(conflict ? KeybindingPageButtonState.Conflict : KeybindingPageButtonState.Normal);
            return new(button, value => button.Content = value, UpdateState);
        }

        private KeybindingPageGroup CreateNativeGroup(string title, string normalized)
        {
            // A group is a caption over a table of rows inside the bindings card, not a card of its own.
            var group = new StackPanel { Tag = normalized };
            var heading = Theme.Text(title);
            heading.FontWeight = FontWeights.SemiBold;
            heading.TextWrapping = TextWrapping.NoWrap;
            heading.TextTrimming = TextTrimming.CharacterEllipsis;
            heading.Margin = new Thickness(0, 12, 0, 4);
            heading.Visibility = string.IsNullOrWhiteSpace(title) ? Visibility.Collapsed : Visibility.Visible;
            group.Children.Add(heading);
            return new(group, rows => {
                rows.Margin = new Thickness();
                group.Children.Add(rows);
            });
        }

        private static Grid Columns(Grid grid) {
            while (grid.ColumnDefinitions.Count < 4) grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[1].Width = new GridLength(100);
            grid.ColumnDefinitions[2].Width = new GridLength(76);
            grid.ColumnDefinitions[3].Width = new GridLength(96);
            return grid;
        }

        private Grid ColumnHeader() {
            var header = Columns(new Grid { Margin = new Thickness(0, 12, 0, 4) });
            string[] titles = ["动作", "个人方案", "同步状态", "当前按键"];
            for (int index = 0; index < titles.Length; index++) {
                var title = Theme.Note(titles[index], 12);
                title.TextWrapping = TextWrapping.NoWrap;
                title.VerticalAlignment = VerticalAlignment.Center;
                if (index == 3) title.TextAlignment = TextAlignment.Center;
                Grid.SetColumn(title, index);
                header.Children.Add(title);
            }
            return header;
        }

        private void PrepareBindingRow(KeybindingPageRow row) {
            var names = (StackPanel)row.Children[0];
            var caption = names.Children.OfType<TextBlock>().Skip(1).FirstOrDefault();
            if (caption is not null) {
                caption.Style = Theme.Style("GlassPresentationTextStyle");
                caption.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
                caption.Margin = new Thickness();
                caption.TextWrapping = TextWrapping.NoWrap;
            }
            if (row.Personal is not null && caption is not null) {
                if (string.IsNullOrEmpty(row.Detail)) names.Children.Remove(caption);
                else caption.Text = row.Detail;
            }
            // Apply the original row padding and separator to the whole grid so WPF rounds them once at each DPI.
            row.Children.Clear();
            row.ColumnDefinitions.Clear();
            row.Height = double.NaN;
            row.Margin = new Thickness();
            var columns = Columns(new Grid());
            GlassPageLayout.PreserveRowSize(columns);
            var frame = new Border { Child = columns, MinHeight = 40, Padding = new Thickness(0, 3, 0, 3),
                BorderThickness = new Thickness(0, 0, 0, 1) };
            frame.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationSeparator");
            row.Children.Add(frame);
            if (names.Children.Count > 0 && names.Children[0] is TextBlock name) {
                name.Style = Theme.Style("GlassPresentationTextStyle");
                name.FontSize = 14;
                name.TextWrapping = TextWrapping.NoWrap;
                name.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
            }
            names.Margin = new Thickness(0, 0, 12, 0);
            names.VerticalAlignment = VerticalAlignment.Center;
            columns.Children.Add(names);
            Grid.SetColumn(row.KeyButton, 3);
            columns.Children.Add(row.KeyButton);
            if (row.Personal is not { } personal) return;
            var binding = personal.DisplayKey is null ? Theme.Note("未保存", 12) : Theme.Text(personal.DisplayKey);
            binding.TextWrapping = TextWrapping.NoWrap;
            binding.Margin = new Thickness(0, 0, 10, 0);
            binding.TextTrimming = TextTrimming.CharacterEllipsis;
            binding.VerticalAlignment = VerticalAlignment.Center;
            binding.ToolTip = personal.DisplayKey ?? "个人方案尚未保存此键位";
            Grid.SetColumn(binding, 1);
            columns.Children.Add(binding);
            if (personal.DisplayKey is null) return;
            var badge = new Border {
                Child = personal.Participates ? Theme.Text("参与") : Theme.Note("未参与", 12), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 1, 7, 1), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = personal.Participates ? "参与同步" : "未参与同步"
            };
            badge.SetResourceReference(Border.BackgroundProperty, personal.Participates ? "GlassPresentationSelected" : "GlassPresentationField");
            Grid.SetColumn(badge, 2);
            columns.Children.Add(badge);
        }

        private global::PCLCS.KeybindingPageHost<global::PCL.ModMinecraft.McInstance> CreateHost()
        {
            global::PCLCS.KeybindingPageHost<global::PCL.ModMinecraft.McInstance> @init = new global::PCLCS.KeybindingPageHost<global::PCL.ModMinecraft.McInstance>();
            return (@init.CurrentInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.LauncherInstanceContext.Instance), @init.GameFolder = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Value => Value.PathIndie), @init.VersionPath = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Value => Value.PathVersion), @init.AllowsInstance = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Value => global::PCL.ModModpackMode.AllowsInstance(Value.PathVersion)), @init.IsLocked = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Value => global::PCL.ModMinecraft.McInstanceLock.IsLocked(Value)), @init.VersionValid = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Value => Value.Version.Valid), @init.VersionMajor = new Func<global::PCL.ModMinecraft.McInstance, global::System.Int32>(Value => Value.Version.Vanilla.Major), @init.HasRunningGame = new Func<global::System.Boolean>(() => global::PCL.ModWatcher.HasRunningMinecraft), @init.IsCurrentPage = new Func<global::System.Boolean>(() => ModMain.FrmMain is { } current && (ReferenceEquals(current.PageRight, this) || current.IsCurrentGlassPage(this))), @init.AnimationEnabled = new Func<global::System.Boolean>(() => ((global::PCL.ModAnimation.AniControlEnabled) == (0))), @init.LoaderFailed = new Func<global::System.Boolean>(() => (this.BindingsLoader.State == global::PCL.ModBase.LoadState.Failed)), @init.StartLoader = new Action<global::System.String>(Folder => this.BindingsLoader.Start(Folder, IsForceRestart: true)), @init.CancelLoader = new Action(() => this.BindingsLoader.Cancel()), @init.BindExternalEvents = this.BindExternalEvents, @init.BindingForVersion = global::PCL.PersonalGameSettings.BindingForVersion, @init.Save = global::PCL.InstanceKeybindings.Save, @init.CaptureEditedBinding = global::PCL.PersonalGameSettings.CaptureEditedBinding, @init.Message = new Func<global::System.String, global::System.String, global::System.String, global::System.String, global::System.Boolean, global::System.Int32>((Text, Title, First, Second, Warning) => global::PCL.ModMain.MyMsgBox(Text, Title, First, Second, IsWarn: Warning)), @init.Hint = new Action<global::System.String, global::System.Boolean>((Text, Success) => global::PCL.ModMain.Hint(Text, (Success) ? (global::PCL.ModMain.HintType.Green) : (global::PCL.ModMain.HintType.Red))), @init.Warn = new Action<global::System.Exception, global::System.String>((ErrorValue, Message) => global::MeloongCore.Logger.Warn(ErrorValue, Message)), @init.Error = new Action<global::System.Exception, global::System.String>((ErrorValue, Message) => global::MeloongCore.Logger.Error(ErrorValue, Message, global::MeloongCore.LogBehavior.Toast)), @init.CapturePersonalOnEdit = false, @init.PersonalBindingAnnotations = global::PCL.PersonalGameSettings.BindingAnnotations, @init).@init;
        }

        private void BindExternalEvents(global::System.Boolean Attach, global::System.Windows.Input.KeyEventHandler KeyHandler, global::System.Windows.Input.MouseButtonEventHandler MouseHandler)
        {
            if (Attach)
            {
                if ((global::PCL.ModMain.FrmMain is not null && this.KeyboardOwner is null))
                {
                    this.KeyboardOwner = global::PCL.ModMain.FrmMain;
                    this.KeyboardKeyHandler = (sender, args) => { if (KeyboardOwner?.HasOpenGlassDialog != true) KeyHandler(sender, args); };
                    this.KeyboardMouseHandler = (sender, args) => { if (KeyboardOwner?.HasOpenGlassDialog != true) MouseHandler(sender, args); };
                    this.KeyboardOwner.PreviewKeyDown += this.KeyboardKeyHandler;
                    this.KeyboardOwner.PreviewMouseDown += this.KeyboardMouseHandler;
                }
            }
            else if (this.KeyboardOwner is not null)
            {
                this.KeyboardOwner.PreviewKeyDown -= this.KeyboardKeyHandler;
                this.KeyboardOwner.PreviewMouseDown -= this.KeyboardMouseHandler;
                this.KeyboardOwner = (global::PCL.FormMain)null;
                this.KeyboardKeyHandler = (global::System.Windows.Input.KeyEventHandler)null;
                this.KeyboardMouseHandler = (global::System.Windows.Input.MouseButtonEventHandler)null;
            }
        }

        // 原测试反射入口直接访问控制器状态，不维护页面状态副本。
        private global::System.Collections.Generic.List<global::PCL.InstanceKeybinding> Entries
        {
            get
            {
                return this.Controller.Entries;
            }

            set
            {
                this.Controller.Entries = value;
            }
        }

        private global::System.Collections.Generic.Dictionary<global::System.String, global::PCLCS.KeybindingPageButton> KeyButtons
        {
            get
            {
                return this.Controller.KeyButtons;
            }
        }

        private global::PCL.ModMinecraft.McInstance Owner
        {
            get
            {
                return this.Controller.Owner;
            }

            set
            {
                this.Controller.Owner = value;
            }
        }

        private string GameFolder
        {
            get
            {
                return this.Controller.GameFolder;
            }

            set
            {
                this.Controller.GameFolder = value;
            }
        }

        private global::PCL.InstanceKeybinding Captured
        {
            get
            {
                return this.Controller.Captured;
            }

            set
            {
                this.Controller.Captured = value;
            }
        }

        private bool ConflictsOnly
        {
            get
            {
                return this.Controller.ConflictsOnly;
            }

            set
            {
                this.Controller.ConflictsOnly = value;
            }
        }

        private void PageLoaded()
        {
            Controller.PageLoaded();
            UpdateLoading();
        }

        private void PageUnloaded()
        {
            Controller.PageUnloaded();
            Load.IsActive = false;
        }

        public void RefreshBindings()
        {
            Controller.RefreshBindings();
            UpdateLoading();
        }

        void global::PCL.IRefreshable.Refresh() => this.RefreshBindings();
        private void ReadBindings(global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Collections.Generic.List<global::PCL.InstanceKeybinding>> Loader)
        {
            Loader.Output = global::PCL.InstanceKeybindings.ReadBindings(Loader.Input, Loader.CreateCancellationToken());
        }

        private void BindingsLoaded()
        {
            this.Controller.BindingsLoaded(this.BindingsLoader.Output);
        }

        private void FilterChanged()
        {
            this.Controller?.RenderList();
        }

        private void UpdateLoading()
        {
            bool reading = BindingsLoader.State == ModBase.LoadState.Loading;
            bool failed = BindingsLoader.State == ModBase.LoadState.Failed;
            PanBack.Visibility = reading || failed ? Visibility.Collapsed : Visibility.Visible;
            PanLoad.Visibility = reading || failed ? Visibility.Visible : Visibility.Collapsed;
            Load.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
            Load.IsActive = reading && IsLoaded;
            BtnRetry.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
            LabLoad.Text = failed ? "读取按键失败，请重试。" : "正在读取按键和本地模组信息…";
        }

        private void RenderList()
        {
            this.Controller.RenderList();
        }

        private global::System.Windows.Controls.Grid CreateBindingRow(global::PCL.InstanceKeybinding Entry)
        {
            return this.Controller.CreateBindingRow(Entry);
        }

        private global::System.String DisplayKey(global::System.String Value)
        {
            return global::PCLCS.KeybindingPageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceKeybinding>.DisplayKey(Value);
        }

        private void BeginCapture(global::PCL.InstanceKeybinding Entry)
        {
            this.Controller.BeginCapture(Entry);
        }

        private void CancelCapture()
        {
            this.Controller.CancelCapture();
        }

        private void UpdateCaptureUi()
        {
            this.Controller.UpdateCaptureUi();
        }

        private void ClearBinding()
        {
            this.Controller.ClearBinding();
        }

        private void CaptureKey(global::System.Object Sender, global::System.Windows.Input.KeyEventArgs e)
        {
            this.Controller.CaptureKey(Sender, e);
        }

        private void HandleCapturedMouse(global::System.Object Sender, global::System.Windows.Input.MouseButtonEventArgs e)
        {
            this.Controller.HandleCapturedMouse(Sender, e);
        }

        private void SaveBinding(global::System.String Value)
        {
            this.Controller.SaveBinding(Value);
        }

        private global::System.String KeyboardName(global::System.Windows.Input.Key Value)
        {
            return global::PCLCS.KeybindingPageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceKeybinding>.KeyboardName(Value);
        }

        private void RetryLoad()
        {
            this.Controller.RetryLoad();
        }
    }
}
