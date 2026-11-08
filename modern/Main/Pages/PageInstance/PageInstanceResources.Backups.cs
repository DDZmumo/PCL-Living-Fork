// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCLCS;
using ResourceBackupGroup = PCLCS.IResourcePageBackupGroup<PCL.ModMinecraft.McInstance>;

namespace PCL
{
    public partial class PageInstanceResources
    {
        private void BackupAll()
        {
            Controller.BackupAll();
        }

        private void RestoreWorldBackup()
        {
            Controller.RestoreWorldBackup();
        }

        private sealed class BackupGroupAdapter : ResourceBackupGroup
        {
            private readonly WorldBackupGroup Group;
            public BackupGroupAdapter(FrameworkElement SelectionBar, Action Changed, Func<bool> BeforeCreate)
            {
                Group = new WorldBackupGroup()
                {
                    SelectionBar = SelectionBar,
                    Margin = new Thickness(39d, 5d, 0d, 10d),
                    BeforeCreate = BeforeCreate
                };
                Group.SelectionChanged += (Sender) => Changed();
            }

            public FrameworkElement Element
            {
                get
                {
                    return Group;
                }
            }

            public string FolderName
            {
                get
                {
                    return Group.FolderName;
                }
            }

            public IEnumerable<string> SelectedIds
            {
                get
                {
                    return Group.SelectedIds;
                }
            }

            public void ClearSelection(bool Animate)
            {
                Group.ClearSelection(Animate);
            }

            public void Bind(ModMinecraft.McInstance Owner, string Name, List<JObject> Points)
            {
                Group.Bind(Owner, Name, Points);
            }

            public void PrefixTitle(string Name)
            {
                MyCard Card = (MyCard)Group.FindName("CardBackups");
                Card.Title = Name + " · " + Card.Title;
            }
        }

        private sealed class CardAdapter : IResourcePageCard
        {
            private readonly MyCard Card;
            private readonly Panel Content;
            public CardAdapter(MyCard Card, Panel Content = null)
            {
                this.Card = Card;
                this.Content = Content ?? Card;
            }

            public FrameworkElement Element
            {
                get
                {
                    return Card;
                }
            }

            public Panel Children
            {
                get
                {
                    return Content;
                }
            }

            public void InitializeHeader()
            {
                var Header = Card.MainTextBlock;
            }

            public string Title
            {
                get
                {
                    return Card.Title;
                }

                set
                {
                    Card.Title = value;
                }
            }
        }

        private static IResourcePageCard CreateBackupCard(Panel Content)
        {
            var Card = new MyCard()
            {
                Margin = new Thickness(0d, 10d, 0d, 10d),
                SwapControl = Content,
                IsSwapped = true
            };
            Card.Children.Add(Content);
            return new CardAdapter(Card, Content);
        }
    }
}
