// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Collections.Generic;
using System.Windows;
using MeloongCore;
using PCLCS;

namespace PCL
{
    public partial class MyScreenshotItem
    {
        internal InstanceResourceEntry Entry;
        private readonly int AnimationId = ModBase.GetUuid();
        private readonly ResourceThumbnailController Controller;
        internal event ChangedEventHandler Changed;
        internal delegate void ChangedEventHandler(MyScreenshotItem Sender);
        internal event ClickRequestedEventHandler ClickRequested;
        internal delegate void ClickRequestedEventHandler(MyScreenshotItem Sender, int Count);
        public MyScreenshotItem()
        {
            this.InitializeComponent();
            var View = new ResourceThumbnailController.View(this, this.Thumbnail, this.LabName, this.LabInfo, this.SelectionBorder, this.Check, () => this.Check.Checked, Value => this.Check.Checked = Value, () => Entry, Value => Entry = (InstanceResourceEntry)Value);
            var Host = new ResourceThumbnailController.Host((FileName, Width) => InstanceResourceStore.LoadImage(FileName, Width), Work => ModBase.RunInUi(Work), (ErrorValue, Message) => Logger.Warn(ErrorValue, Message), (Back, Hovered, Selected, Pressed) =>
            {
                var Ani = new List<ModAnimation.AniData>();
                LocalResourceUi.AppendItemSelection(Ani, Back, Hovered, Selected, Pressed, false);
                ModAnimation.AniStart(Ani, "Screenshot Selection " + AnimationId);
            }, () => ModAnimation.AniControlEnabled == 0, () => ModAnimation.AniStop("Screenshot Selection " + AnimationId));
            Controller = new ResourceThumbnailController(View, Host);
            Controller.Changed += () => Changed?.Invoke(this);
            Controller.ClickRequested += Count => ClickRequested?.Invoke(this, Count);
        }

        public bool Checked
        {
            get
            {
                return Controller.Checked;
            }

            set
            {
                Controller.Checked = value;
            }
        }

        internal void Bind(InstanceResourceEntry Value)
        {
            Controller.Bind(Value);
        }

        private void CheckChanged()
        {
            Controller?.CheckChanged();
        }

        internal void UpdateViewport(FrameworkElement Viewport)
        {
            Controller.UpdateViewport(Viewport);
        }

        internal void ReleaseThumbnail()
        {
            Controller.ReleaseThumbnail();
        }
    }
}
