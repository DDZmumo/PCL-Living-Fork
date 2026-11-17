// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
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
                    ComboMode.SelectedIndex = 0;
                    ComboRetentionPolicy.SelectedIndex = 1;
                    PanRetentionCount.Visibility = Visibility.Visible;
                    PanIndependent.Visibility = Visibility.Collapsed;
                    this.CheckAuto.Checked = false;
                    this.TextRetention.Text = "5";
                    RetentionDirty = false;
                    return;
                }
                bool independent = BackupPreferences.WorldMode(Value) == 1;
                ComboMode.SelectedIndex = independent ? 1 : 0;
                PanIndependent.Visibility = independent ? Visibility.Visible : Visibility.Collapsed;
                this.CheckAuto.Checked = Settings.Get<bool>("VersionWorldBackupAutoEnabled", Instance: Value);
                bool keepAll = Settings.Get<bool>("VersionWorldBackupKeepAll", Value);
                ComboRetentionPolicy.SelectedIndex = keepAll ? 0 : 1;
                PanRetentionCount.Visibility = keepAll ? Visibility.Collapsed : Visibility.Visible;
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
            if (BindingOwner || !User || Owner is null || ComboMode.SelectedIndex != 1)
                return;
            if (Settings.Get<bool>("VersionWorldBackupAutoEnabled", Instance: Owner) != this.CheckAuto.Checked)
                Settings.Set("VersionWorldBackupAutoEnabled", this.CheckAuto.Checked, Instance: Owner);
        }

        private void RetentionEdited()
        {
            if (!BindingOwner && Owner is not null && ComboMode.SelectedIndex == 1 && ComboRetentionPolicy.SelectedIndex == 1 && this.TextRetention.IsKeyboardFocusWithin)
                RetentionDirty = true;
        }
        private void ModeChanged(object sender, SelectionChangedEventArgs args) {
            if (BindingOwner || Owner is null || ComboMode.SelectedIndex < 0) return;
            if (!CommitPending()) { BindOwner(Owner); return; }
            BackupPreferences.SetWorldMode(Owner, ComboMode.SelectedIndex == 1 ? 1 : 0);
            BindOwner(Owner);
            CardSettings.TriggerForceResize();
        }
        private void RetentionPolicyChanged(object sender, SelectionChangedEventArgs args) {
            if (BindingOwner || Owner is null || ComboMode.SelectedIndex != 1 || ComboRetentionPolicy.SelectedIndex < 0) return;
            RetentionDirty = false;
            Settings.Set("VersionWorldBackupKeepAll", ComboRetentionPolicy.SelectedIndex == 0, Owner);
            BindOwner(Owner); CardSettings.TriggerForceResize();
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
            if (BackupPreferences.WorldMode(Owner) != 1) { RetentionDirty = false; BindOwner(Owner); return true; }
            if (Settings.Get<bool>("VersionWorldBackupKeepAll", Owner)) { RetentionDirty = false; BindOwner(Owner); return true; }
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
