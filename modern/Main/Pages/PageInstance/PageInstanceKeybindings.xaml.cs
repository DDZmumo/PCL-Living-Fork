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
        public PageInstanceKeybindings()
        {
            __LfAttachOwnHandles();
            this.InitializeComponent();
            this.DisabledPageAnimControls.Add(this.CardCapture);
            this.BtnClear.Logo = global::PCL.ModBase.Logo.IconButtonDelete;
            this.BtnCancel.Logo = global::PCL.ModBase.Logo.IconButtonCross;
            this.BtnFilterAll.LabText.Margin = new global::System.Windows.Thickness((global::System.Double)(-(2)), 0d, 8d, 0d);
            this.BtnFilterConflicts.LabText.Margin = new global::System.Windows.Thickness((global::System.Double)(-(2)), 0d, 8d, 0d);
            this.Controller = new global::PCLCS.KeybindingPageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceKeybinding>(this.CreateView(), this.CreateHost());
            this.BindingsLoader = new global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Collections.Generic.List<global::PCL.InstanceKeybinding>>("读取版本按键", this.ReadBindings);
            this.PageLoaderInit(this.Load, this.PanLoad, this.PanBack, (global::System.Windows.FrameworkElement)null, this.BindingsLoader, (_) => this.BindingsLoaded(), AutoRun: false);
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
                Capture = LabCapture,
                Search = new Func<global::System.String>(() => this.SearchBox.Text),
                SetAllText = new Action<global::System.String>(Value => this.BtnFilterAll.Text = Value),
                SetConflictsText = new Action<global::System.String>(Value => this.BtnFilterConflicts.Text = Value),
                ScrollHome = new Action(() => this.PanBack.ScrollToHome()),
                CreateButton = global::PCL.PageInstanceKeybindings.CreateNativeButton,
                CreateGroup = global::PCL.PageInstanceKeybindings.CreateNativeGroup,
                RefreshCaptureBar = new Func<global::System.Int32, global::System.Int32, global::System.Boolean, global::System.Int32>((Count, Previous, Animate) =>
                {
                    global::PCL.LocalResourceUi.RefreshSelectionBar(this.CardCapture, this.TransCapture, Count, ref Previous, "Keybinding Capture " + this.PageUuid, Animate);
                    return Previous;
                })
            };
        }

        internal static global::PCLCS.KeybindingPageButton CreateNativeButton(global::PCL.InstanceKeybinding Entry, global::System.String Text, global::System.Boolean Conflict, global::System.String Tooltip, global::System.Action Clicked)
        {
            var Button = new global::PCL.MyButton()
            {
                Text = Text,
                Width = 132d,
                Height = 32d,
                Padding = new global::System.Windows.Thickness(10d, 0d, 10d, 0d),
                VerticalAlignment = global::System.Windows.VerticalAlignment.Center,
                ColorType = (Conflict) ? (global::PCL.MyButton.ColorState.Red) : (global::PCL.MyButton.ColorState.Normal),
                Tag = Entry,
                ToolTip = Tooltip
            };
            Button.Click += (Sender, Args) => Clicked();
            return new global::PCLCS.KeybindingPageButton(Button, Value => Button.Text = Value, Value => Button.ColorType = (Value == global::PCLCS.KeybindingPageButtonState.Capturing) ? (global::PCL.MyButton.ColorState.Highlight) : (((Value == global::PCLCS.KeybindingPageButtonState.Conflict) ? (global::PCL.MyButton.ColorState.Red) : (global::PCL.MyButton.ColorState.Normal))));
        }

        private static global::PCLCS.KeybindingPageGroup CreateNativeGroup(global::System.String Title, global::System.String Normalized)
        {
            var Group = new global::PCL.MyCard()
            {
                Title = Title,
                Margin = new global::System.Windows.Thickness(0d, 8d, 0d, 12d),
                Tag = Normalized
            };
            return new global::PCLCS.KeybindingPageGroup(Group, Rows =>
            {
                var Header = Group.MainTextBlock;
                Group.Children.Add(Rows);
            });
        }

        private global::PCLCS.KeybindingPageHost<global::PCL.ModMinecraft.McInstance> CreateHost()
        {
            global::PCLCS.KeybindingPageHost<global::PCL.ModMinecraft.McInstance> @init = new global::PCLCS.KeybindingPageHost<global::PCL.ModMinecraft.McInstance>();
            return (@init.CurrentInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.PageInstanceLeft.Instance), @init.GameFolder = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Value => Value.PathIndie), @init.VersionPath = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Value => Value.PathVersion), @init.AllowsInstance = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Value => global::PCL.ModModpackMode.AllowsInstance(Value.PathVersion)), @init.IsLocked = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Value => global::PCL.ModMinecraft.McInstanceLock.IsLocked(Value)), @init.VersionValid = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Value => Value.Version.Valid), @init.VersionMajor = new Func<global::PCL.ModMinecraft.McInstance, global::System.Int32>(Value => Value.Version.Vanilla.Major), @init.HasRunningGame = new Func<global::System.Boolean>(() => global::PCL.ModWatcher.HasRunningMinecraft), @init.IsCurrentPage = new Func<global::System.Boolean>(() => Object.ReferenceEquals((global::PCL.ModMain.FrmMain?.PageRight), this)), @init.AnimationEnabled = new Func<global::System.Boolean>(() => ((global::PCL.ModAnimation.AniControlEnabled) == (0))), @init.LoaderFailed = new Func<global::System.Boolean>(() => (this.BindingsLoader.State == global::PCL.ModBase.LoadState.Failed)), @init.StartLoader = new Action<global::System.String>(Folder => this.BindingsLoader.Start(Folder, IsForceRestart: true)), @init.CancelLoader = new Action(() => this.BindingsLoader.Cancel()), @init.BindExternalEvents = this.BindExternalEvents, @init.BindingForVersion = global::PCL.PersonalGameSettings.BindingForVersion, @init.Save = global::PCL.InstanceKeybindings.Save, @init.CaptureEditedBinding = global::PCL.PersonalGameSettings.CaptureEditedBinding, @init.Message = new Func<global::System.String, global::System.String, global::System.String, global::System.String, global::System.Boolean, global::System.Int32>((Text, Title, First, Second, Warning) => global::PCL.ModMain.MyMsgBox(Text, Title, First, Second, IsWarn: Warning)), @init.Hint = new Action<global::System.String, global::System.Boolean>((Text, Success) => global::PCL.ModMain.Hint(Text, (Success) ? (global::PCL.ModMain.HintType.Green) : (global::PCL.ModMain.HintType.Red))), @init.Warn = new Action<global::System.Exception, global::System.String>((ErrorValue, Message) => global::MeloongCore.Logger.Warn(ErrorValue, Message)), @init.Error = new Action<global::System.Exception, global::System.String>((ErrorValue, Message) => global::MeloongCore.Logger.Error(ErrorValue, Message, global::MeloongCore.LogBehavior.Toast)), @init.CapturePersonalOnEdit = false, @init.PersonalBindingAnnotations = global::PCL.PersonalGameSettings.BindingAnnotations, @init).@init;
        }

        private void BindExternalEvents(global::System.Boolean Attach, global::System.Windows.Input.KeyEventHandler KeyHandler, global::System.Windows.Input.MouseButtonEventHandler MouseHandler)
        {
            if (Attach)
            {
                if ((global::PCL.ModMain.FrmMain is not null && this.KeyboardOwner is null))
                {
                    this.KeyboardOwner = global::PCL.ModMain.FrmMain;
                    this.KeyboardKeyHandler = KeyHandler;
                    this.KeyboardMouseHandler = MouseHandler;
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
            this.Controller.PageLoaded();
        }

        private void PageUnloaded()
        {
            this.Controller.PageUnloaded();
        }

        public void RefreshBindings()
        {
            this.Controller.RefreshBindings();
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

        private void FilterChecked(global::PCL.MyRadioButton Sender, global::System.Boolean User)
        {
            this.Controller?.FilterChecked(Object.ReferenceEquals((Sender), this.BtnFilterConflicts));
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
