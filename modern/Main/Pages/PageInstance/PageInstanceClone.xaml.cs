// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using PCLCS;
using ClonePageController = PCLCS.InstanceClonePageController<PCL.ModMinecraft.McInstance, PCL.InstanceCloneOptions>;
using ClonePageHost = PCLCS.InstanceClonePageHost<PCL.ModMinecraft.McInstance, PCL.InstanceCloneOptions>;

namespace PCL
{
    public partial class PageInstanceClone : IRefreshable
    {
        private readonly ClonePageController Controller;
        private readonly InstanceClonerUi.ChangedEventHandler ChangedHandler;
        public PageInstanceClone()
        {
            __LfAttachOwnHandles();
            ChangedHandler = Refresh;
            this.InitializeComponent();
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
                ClearValidationRules = new Action(() => this.TextName.ValidateRules.Clear()),
                AddFolderRule = new Action<string>(Folder => this.TextName.ValidateRules.Add(new ValidateFolderName(Folder))),
                ValidateNativeName = new Func<string, string>(Value => ModValidate.Validate(Value, (IEnumerable<Validate>)this.TextName.ValidateRules)),
                CopySaves = new Func<bool>(() => this.CheckSaves.Checked),
                CopyScreenshots = new Func<bool>(() => this.CheckScreenshots.Checked),
                CopyServers = new Func<bool>(() => this.CheckServers.Checked),
                ScrollHome = new Action(() => this.PanBack.ScrollToHome())
            };
            var Host = new ClonePageHost()
            {
                CurrentInstance = new Func<ModMinecraft.McInstance>(() => PageInstanceLeft.Instance),
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
