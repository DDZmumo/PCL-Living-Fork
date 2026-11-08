// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Windows;
using System.Windows.Controls;
using PCLCS;

namespace PCL;

/// <summary>Native Markdown notice with a fixed footer outside the scrolling notes.</summary>
internal sealed class ReleaseNotesPromptView : Grid
{
    internal double PreferredWidth => 720;
    internal readonly ReleaseNotesView Notes;
    internal readonly MyCheckBox Suppress;

    internal ReleaseNotesPromptView(string markdown, bool suppressed, Func<bool, bool> persist, Action<string> openLink = null)
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Notes = new ReleaseNotesView(markdown, openLink)
        {
            // The native dialog supplies the finite body budget. Reserve the
            // footer first, then let only the Markdown viewport consume the rest.
            Height = double.NaN
        };
        Children.Add(Notes);
        Suppress = new MyCheckBox
        {
            Text = LegacyUpdateLogPrompt.CheckBoxText,
            Checked = suppressed,
            Margin = new Thickness(0, 16, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        SetRow(Suppress, 1);
        Children.Add(Suppress);
        bool saved = suppressed;
        Suppress.Change += (_, user) =>
        {
            if (!user) return;
            if (persist(Suppress.Checked)) saved = Suppress.Checked;
            else Suppress.Checked = saved;
        };
    }
}
