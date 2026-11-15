using System;
using System.Linq;
using System.Windows;

namespace PCLCS;

/// <summary>LF's child-first attached-settings traversal and control-save ordering.</summary>
public static class LegacySettingsBindingController {
    public static string? GetAttached(DependencyObject? target, DependencyProperty property) => target is null ? null : (string?)target.GetValue(property);
    public static void SetAttached(DependencyObject target, DependencyProperty property, string? value) => target.SetValue(property, value);

    public static void ResetSettings(DependencyObject target, DependencyProperty keyProperty, Action<string> reset) {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(target).OfType<DependencyObject>()) ResetSettings(child, keyProperty, reset);
        string? key = GetAttached(target, keyProperty);
        if (key is null) return;
        reset(key);
    }
    public static void RefreshSettings(DependencyObject target, DependencyProperty keyProperty, Func<string, object?> read, Action<DependencyObject, object?> refreshIfControl) {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(target).OfType<DependencyObject>()) RefreshSettings(child, keyProperty, read, refreshIfControl);
        string? key = GetAttached(target, keyProperty);
        if (key is null) return;
        object? newValue = read(key);
        refreshIfControl(target, newValue);
    }
    public static void SaveSetting(object sender, int animationControlEnabled, Func<object, bool> isSettingControl,
        DependencyProperty keyProperty, Func<object, string?> currentSetting, Action<string, string> save) {
        if (animationControlEnabled != 0) return;
        if (!isSettingControl(sender)) return;
        // LF narrows only after the original interface check. A foreign class
        // implementing that interface still retains its invalid-cast failure.
        string? key = GetAttached((DependencyObject)sender, keyProperty);
        if (key is null) return;
        string? value = currentSetting(sender);
        if (value is null) return;
        save(key, value);
    }
}
