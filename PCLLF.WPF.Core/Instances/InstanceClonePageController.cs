// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PCLCS;

public sealed class InstanceClonePageView {
    public FrameworkElement NameControl = null!, CloneButton = null!, MigrateButton = null!, OriginCard = null!;
    public TextBlock Source = null!, Origin = null!, Validation = null!;
    public Func<string> NameText = null!;
    public Action<string> SetNameText = null!;
    public Action ClearValidationRules = null!;
    public Action<string> AddFolderRule = null!;
    public Func<string, string?> ValidateNativeName = null!;
    public Func<bool> CopySaves = null!, CopyScreenshots = null!, CopyServers = null!;
    public Action ScrollHome = null!;
}
public sealed class InstanceClonePageHost<TInstance, TOptions> where TInstance : class where TOptions : InstanceCloneOptionsData {
    public Func<TInstance?> CurrentInstance = null!;
    public Func<TInstance, string> Name = null!, VersionPath = null!;
    public Func<string, string> SourceVersionFolder = null!, SafeName = null!;
    public Func<bool> IsBusy = null!;
    public Action<bool> BindChanged = null!;
    public Func<TOptions> CreateOptions = null!;
    public Action<TInstance?, string, TOptions> Start = null!;
    public Action<TInstance?> Migrate = null!;
}

/// <summary>LF 测试副本页面的来源、名称验证、选择性克隆和迁回按钮生命周期。</summary>
// LF PageInstanceClone.xaml.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// unchanged through 202aa3b4e7703b61865c875c9e6d98d993e7f18c. No CE code reused.
public sealed class InstanceClonePageController<TInstance, TOptions> where TInstance : class where TOptions : InstanceCloneOptionsData {
    private readonly InstanceClonePageView view;
    private readonly InstanceClonePageHost<TInstance, TOptions> host;
    public TInstance? Owner;
    public InstanceClonePageController(InstanceClonePageView view, InstanceClonePageHost<TInstance, TOptions> host) { this.view = view; this.host = host; }
    private static bool SameText(string? first, string? second) => string.Equals(first ?? "", second ?? "", StringComparison.Ordinal);
    public void PageLoaded() { view.ScrollHome(); host.BindChanged(true); Refresh(); }
    public void PageUnloaded() => host.BindChanged(false);
    public void Refresh() {
        TInstance? previous = Owner;
        Owner = host.CurrentInstance();
        if (Owner is null) return;
        view.Source.Text = "当前版本：" + host.Name(Owner);
        view.ClearValidationRules();
        view.AddFolderRule(Path.GetDirectoryName(host.VersionPath(Owner).TrimEnd('\\'))!);
        if (!ReferenceEquals(previous, Owner) || string.IsNullOrWhiteSpace(view.NameText())) view.SetNameText(host.Name(Owner).Substring(0, Math.Min(host.Name(Owner).Length, 85)) + " - 测试");
        view.OriginCard.Visibility = Visibility.Collapsed;
        try {
            string source = host.SourceVersionFolder(host.VersionPath(Owner));
            view.Origin.Text = "来源版本：" + Path.GetFileName(source) + "\r\n迁移会先展示模组、配置和游戏组件的变化，并保存原环境还原点。原存档、截图和服务器列表保持当前内容。";
            view.OriginCard.Visibility = Visibility.Visible;
        } catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception) {
            view.Origin.Text = "副本来源记录不可用，无法自动迁移。请在资源管理器中检查版本目录。";
            view.OriginCard.Visibility = Visibility.Visible;
        }
        view.MigrateButton.IsEnabled = !host.IsBusy() && view.Origin.Text.StartsWith("来源版本：", StringComparison.Ordinal);
        ValidateName();
    }
    public void ValidateName() {
        if (view.NameControl is null || view.CloneButton is null || Owner is null) return;
        string? errorText = null;
        try {
            host.SafeName(view.NameText());
            string? nativeError = view.ValidateNativeName(view.NameText());
            if (!SameText(nativeError, "")) errorText = nativeError;
            string address = Path.Combine(Path.GetDirectoryName(host.VersionPath(Owner).TrimEnd('\\'))!, view.NameText());
            if (Directory.Exists(address) || File.Exists(address)) errorText = "同名版本已存在，请使用新的测试版本名称。";
        } catch (IOException error) { errorText = error.Message; }
        view.Validation.Text = errorText ?? "";
        view.Validation.Visibility = errorText is null ? Visibility.Collapsed : Visibility.Visible;
        view.CloneButton.IsEnabled = errorText is null && !host.IsBusy();
    }
    public void CloneVersion() {
        ValidateName();
        if (!view.CloneButton.IsEnabled || !ReferenceEquals(Owner, host.CurrentInstance())) return;
        TInstance? owner = Owner;
        string name = view.NameText();
        TOptions options = host.CreateOptions();
        options.CopySaves = view.CopySaves(); options.CopyScreenshots = view.CopyScreenshots(); options.CopyServers = view.CopyServers();
        host.Start(owner, name, options);
        UpdateTaskState();
    }
    public void MigrateVersion() {
        if (Owner is null || !ReferenceEquals(Owner, host.CurrentInstance())) return;
        host.Migrate(Owner);
        UpdateTaskState();
    }
    public void UpdateTaskState() {
        if (host.IsBusy()) { view.CloneButton.IsEnabled = false; view.MigrateButton.IsEnabled = false; }
        else Refresh();
    }
}
