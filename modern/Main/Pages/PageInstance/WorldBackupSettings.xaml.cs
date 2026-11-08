// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Windows.Input;
using MeloongCore;

namespace PCL
{
    public partial class WorldBackupSettings
    {
        private ModMinecraft.McInstance Owner;
        private bool BindingOwner;
        private bool RetentionDirty;
        public WorldBackupSettings()
        {
            __LfAttachOwnHandles();
            this.InitializeComponent();
        }

        private void Init()
        {
            this.CardSettings.SwapControl = this.PanSettings;
        }

        internal void BindOwner(ModMinecraft.McInstance Value)
        {
            bool PreserveInput = Value is not null && Owner is not null && PathUtils.IsSame(Value.PathVersion, Owner.PathVersion) && RetentionDirty && this.TextRetention.IsKeyboardFocusWithin;
            BindingOwner = true;
            try
            {
                Owner = Value;
                this.IsEnabled = Value is not null;
                if (Value is null)
                {
                    this.CheckAuto.Checked = false;
                    this.TextRetention.Text = "5";
                    RetentionDirty = false;
                    return;
                }

                this.CheckAuto.Checked = Settings.Get<bool>("VersionWorldBackupAutoEnabled", Instance: Value);
                if (!PreserveInput)
                {
                    this.TextRetention.Text = Settings.Get<int>("VersionWorldBackupRetention", Instance: Value).ToString();
                    RetentionDirty = false;
                }
            }
            finally
            {
                BindingOwner = false;
            }
        }

        private void AutoChanged(object Sender, bool User)
        {
            if (BindingOwner || !User || Owner is null)
                return;
            if (Settings.Get<bool>("VersionWorldBackupAutoEnabled", Instance: Owner) != this.CheckAuto.Checked)
                Settings.Set("VersionWorldBackupAutoEnabled", this.CheckAuto.Checked, Instance: Owner);
        }

        private void RetentionEdited()
        {
            if (!BindingOwner && Owner is not null && this.TextRetention.IsKeyboardFocusWithin)
                RetentionDirty = true;
        }

        private void RetentionLostFocus()
        {
            CommitPending();
        }

        private void RetentionKey(object Sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            CommitPending();
            e.Handled = true;
        }

        internal bool CommitPending()
        {
            if (BindingOwner || Owner is null || !RetentionDirty)
                return true;
            RetentionDirty = false;
            int Count;
            bool Valid = int.TryParse(this.TextRetention.Text, out Count) && Count >= 1 && Count <= 50;
            int Previous = Settings.Get<int>("VersionWorldBackupRetention", Instance: Owner);
            if (Valid && Count != Previous)
                Settings.Set("VersionWorldBackupRetention", Count, Instance: Owner);
            BindingOwner = true;
            try
            {
                this.TextRetention.Text = (Valid ? Count : Previous).ToString();
            }
            finally
            {
                BindingOwner = false;
            }

            if (!Valid)
                ModMain.Hint("每个世界保留的备份份数必须为 1 到 50。", ModMain.HintType.Red);
            return Valid;
        }
    }
}
