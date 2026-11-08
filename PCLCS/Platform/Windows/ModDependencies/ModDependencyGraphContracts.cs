// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Windows.Media;

namespace PCLCS;

// LF Controls/ModDependencyGraphView.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// blob 4a112a072c8cd30cfba21d272167f97c6bf5ddab, under the repository LICENCE.
// Absent from PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModLocalComp.cs/GetLogo was
// checked as C# reference (CE Plain Craft Launcher 2/LICENCE; MPL-2.0 for PCLLF-original code); there is no
// equivalent graph view. No CE implementation or graph policy is copied.
public class ModDependencyGraphNodeData {
    public string? Identity, Title, Condition, Details;
    public bool IsConflict, IsCandidate, IsPlatformRequirement, IsEmbedded, NeedsAttention, NeedsVersionReview;
    public string? FileName, IconPath;
    public double Height = 50;
}

/// <summary>Only the original LF bitmap resource and local JAR icon adapters cross this boundary.</summary>
public sealed class ModDependencyGraphHost {
    public Func<string, ImageSource> DefaultIcon = null!;
    public Func<string, string, ImageSource?> ReadIcon = null!;
}

// The LF ModTheme.ThemeBrush primitive, kept here to avoid a Core -> Presentation reference.
internal static class ModDependencyVisualTheme {
    internal static SolidColorBrush Brush(string key) => System.Windows.Application.Current?.TryFindResource(key) as SolidColorBrush ?? new SolidColorBrush(Colors.Gray);
}
