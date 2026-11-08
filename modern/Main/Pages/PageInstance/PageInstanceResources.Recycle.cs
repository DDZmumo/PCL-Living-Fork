// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Windows;
using ResourceRow = PCLCS.IResourcePageRow<PCL.InstanceResourceEntry, PCL.InstanceResourceOnlineInfo>;
using ResourceTile = PCLCS.IResourcePageThumbnail<PCL.InstanceResourceEntry>;
using ResourceRowActions = PCLCS.ResourcePageRowActions<PCL.InstanceResourceEntry, PCL.InstanceResourceOnlineInfo>;

namespace PCL
{
    public partial class PageInstanceResources
    {
        internal bool IsRecycleMode
        {
            get
            {
                return Controller.IsRecycleMode;
            }

            set
            {
                Controller.IsRecycleMode = value;
            }
        }

        internal void SetRecycleMode(bool Value)
        {
            Controller.SetRecycleMode(Value);
        }

        private void ReturnToResources()
        {
            Controller.ReturnToResources();
        }

        private void RestoreDeleted()
        {
            Controller.SetRecycleMode(!Controller.IsRecycleMode);
        }

        private void RestoreRecycleSelection(IEnumerable<InstanceResourceEntry> Items = null)
        {
            Controller.RestoreRecycleSelection(Items);
        }

        private void RefreshResourceToolbar()
        {
            Controller?.RefreshResourceToolbar();
        }

        private sealed class RowAdapter : ResourceRow
        {
            private readonly MyLocalModItem Row;
            public RowAdapter(PageInstanceResources Page, ResourceRowActions Actions)
            {
                Row = new MyLocalModItem()
                {
                    SelectionBar = Page.CardSelect
                };
                Row.Changed += (Sender, e) =>
                {
                    if (Actions.SelectionChanged(this))
                        e.Handled = true;
                };
                Row.Click += (Sender, Args) => Actions.Click(this);
                Row.MouseRightButtonUp += (Sender, e) => Actions.Details(Row.ResourceEntry);
                Row.ResourceUpdateRequested = new Action(() => Actions.Update(Row.ResourceEntry));
                Row.ResourceDetailsRequested = new Action(() => Actions.Details(Row.ResourceEntry));
                Row.Buttons = NativeRowButtons(Page.Kind, Row, Actions);
                Row.MouseLeftButtonDown += (Sender, e) =>
                {
                    if (e.ClickCount == 2)
                        Actions.EnterFolder(Row.ResourceEntry);
                };
            }

            public FrameworkElement Element
            {
                get
                {
                    return Row;
                }
            }

            public InstanceResourceEntry Entry
            {
                get
                {
                    return Row.ResourceEntry;
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

            public void Bind(InstanceResourceEntry Value, InstanceResourceOnlineInfo Info)
            {
                Row.BindResource(Value, Info);
            }
        }

        private static List<MyIconButton> NativeRowButtons(InstanceResourceKind Kind, MyLocalModItem Row, ResourceRowActions Actions)
        {
            if (Actions.IsRecycle())
            {
                var Restore = new MyIconButton()
                {
                    Logo = ModBase.Logo.IconButtonRefresh,
                    ToolTip = "还原"
                };
                Restore.Click += (Sender, Args) => Actions.Restore(Row.ResourceEntry);
                var Purge = new MyIconButton()
                {
                    Logo = ModBase.Logo.IconButtonDelete,
                    ToolTip = "彻底删除"
                };
                Purge.Click += (Sender, Args) => Actions.Purge(Row.ResourceEntry);
                return new List<MyIconButton>()
                {
                    Restore,
                    Purge
                };
            }

            var Buttons = new List<MyIconButton>();
            if (Actions.CanPlay())
            {
                var Play = new MyIconButton()
                {
                    Logo = LocalResourceUi.PlayLogo,
                    LogoScale = 0.9d,
                    ToolTip = "启动此版本并直接进入存档"
                };
                Play.Click += (Sender, Args) => Actions.Play(Row.ResourceEntry);
                Buttons.Add(Play);
            }

            var Details = new MyIconButton()
            {
                Logo = ModBase.Logo.IconButtonInfo,
                ToolTip = Kind == InstanceResourceKind.Screenshots ? "预览截图" : "详情"
            };
            Details.Click += (Sender, Args) => Actions.Details(Row.ResourceEntry);
            Buttons.Add(Details);
            var Open = new MyIconButton()
            {
                Logo = ModBase.Logo.IconButtonOpen,
                LogoScale = 1.05d,
                ToolTip = "打开文件位置"
            };
            Open.Click += (Sender, Args) => Actions.Open(Row.ResourceEntry);
            Buttons.Add(Open);
            var Rename = new MyIconButton()
            {
                Logo = ModBase.Logo.IconButtonEdit,
                ToolTip = "重命名"
            };
            Rename.Click += (Sender, Args) => Actions.Rename(Row.ResourceEntry);
            Buttons.Add(Rename);
            var Delete = new MyIconButton()
            {
                Logo = ModBase.Logo.IconButtonDelete,
                ToolTip = "删除"
            };
            Delete.Click += (Sender, Args) => Actions.Delete(Row.ResourceEntry);
            Buttons.Add(Delete);
            return Buttons;
        }

        private sealed class ThumbnailAdapter : ResourceTile
        {
            private readonly MyScreenshotItem Tile;
            public ThumbnailAdapter(Action<ResourceTile> Changed, Action<InstanceResourceEntry, int> Clicked, FrameworkElement Viewport)
            {
                Tile = new MyScreenshotItem();
                Tile.Changed += (Sender) => Changed(this);
                Tile.ClickRequested += (Sender, Count) => Clicked(Sender.Entry, Count);
            }

            public FrameworkElement Element
            {
                get
                {
                    return Tile;
                }
            }

            public InstanceResourceEntry Entry
            {
                get
                {
                    return Tile.Entry;
                }
            }

            public bool Checked
            {
                get
                {
                    return Tile.Checked;
                }

                set
                {
                    Tile.Checked = value;
                }
            }

            public void Bind(InstanceResourceEntry Value)
            {
                Tile.Bind(Value);
            }

            public void UpdateViewport(FrameworkElement Viewport)
            {
                Tile.UpdateViewport(Viewport);
            }

            public void ReleaseThumbnail()
            {
                Tile.ReleaseThumbnail();
            }
        }
    }
}
