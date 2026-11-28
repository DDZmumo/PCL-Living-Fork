// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Windows;
using PCLCS;
using ClonePageController = PCLCS.InstanceClonePageController<PCL.ModMinecraft.McInstance, PCL.InstanceCloneOptions>;
using ClonePageHost = PCLCS.InstanceClonePageHost<PCL.ModMinecraft.McInstance, PCL.InstanceCloneOptions>;

namespace PCL
{
    public partial class PageInstanceClone : IRefreshable
    {
        private readonly ClonePageController Controller;
        private readonly List<Validate> nameRules = new();
        private readonly InstanceClonerUi.ChangedEventHandler ChangedHandler;
        public PageInstanceClone()
        {
            ChangedHandler = Refresh;
            InitializeComponent();
            AttachScroll(PanBack);
            var placeholder = Theme.Note("输入新的版本名称", 13);
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            placeholder.IsHitTestVisible = false;
            placeholder.Margin = new Thickness(12, 0, 12, 0);
            GlassPageResources.BindTextPlaceholder(TextName, placeholder);
            NameEditor.Children.Add(placeholder);
            var View = new InstanceClonePageView()
            {
                NameControl = TextName,
                CloneButton = BtnClone,
                MigrateButton = BtnMigrate,
                OriginCard = CardOrigin,
                Source = LabSource,
                Origin = LabOrigin,
                Validation = LabValidation,
                NameText = new Func<string>(() => this.TextName.Text),
                SetNameText = new Action<string>(Value => this.TextName.Text = Value),
                ClearValidationRules = new Action(() => nameRules.Clear()),
                AddFolderRule = new Action<string>(Folder => nameRules.Add(new ValidateFolderName(Folder))),
                ValidateNativeName = new Func<string, string>(Value => ModValidate.Validate(Value, nameRules)),
                CopySaves = new Func<bool>(() => this.CheckSaves.IsChecked == true),
                CopyScreenshots = new Func<bool>(() => this.CheckScreenshots.IsChecked == true),
                CopyServers = new Func<bool>(() => this.CheckServers.IsChecked == true),
                ScrollHome = PanBack.ScrollToTop
            };
            var Host = new ClonePageHost()
            {
                CurrentInstance = new Func<ModMinecraft.McInstance>(() => LauncherInstanceContext.Instance),
                Name = new Func<ModMinecraft.McInstance, string>(Instance => Instance.Name),
                VersionPath = new Func<ModMinecraft.McInstance, string>(Instance => Instance.PathVersion),
                SourceVersionFolder = InstanceCloner.SourceVersionFolder,
                SafeName = InstanceResourceStore.SafeName,
                IsBusy = new Func<bool>(() => InstanceClonerUi.IsBusy),
                BindChanged = BindChanged,
                CreateOptions = new Func<InstanceCloneOptions>(() => new InstanceCloneOptions()),
                Start = InstanceClonerUi.Start,
                Migrate = InstanceClonerUi.Migrate
            };
            Controller = new ClonePageController(View, Host);
            Loaded += (_, _) => PageLoaded();
            Unloaded += (_, _) => PageUnloaded();
            TextName.TextChanged += (_, _) => NameChanged();
            BtnClone.Click += (_, _) => CloneVersion();
            BtnMigrate.Click += (_, _) => MigrateVersion();
        }

        private void BindChanged(bool Attach)
        {
            if (Attach)
            {
                InstanceClonerUi.Changed += ChangedHandler;
            }
            else
            {
                InstanceClonerUi.Changed -= ChangedHandler;
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

        private void NameChanged()
        {
            Controller?.ValidateName();
        }

        private void ValidateName()
        {
            Controller.ValidateName();
        }

        private void CloneVersion()
        {
            Controller.CloneVersion();
        }

        private void MigrateVersion()
        {
            Controller.MigrateVersion();
        }

        private void UpdateTaskState()
        {
            Controller.UpdateTaskState();
        }
    }
}
