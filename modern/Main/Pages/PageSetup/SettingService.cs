using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualBasic.CompilerServices;
using PCLCS;
using PCLLF.Wpf.Glass;

namespace PCL
{

    // 原附加属性 owner 与原接口身份；遍历/保存次序由 C# controller 承接。
    public class SettingService
    {
        [ThreadStatic] private static int _refreshing;
        public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached("Key", typeof(string), typeof(SettingService), new PropertyMetadata(null));
        public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached("Value", typeof(string), typeof(SettingService), new PropertyMetadata(null));

        public static string GetKey(DependencyObject obj)
        {
            return LegacySettingsBindingController.GetAttached(obj, KeyProperty);
        }
        public static void SetKey(DependencyObject obj, string value)
        {
            LegacySettingsBindingController.SetAttached(obj, KeyProperty, value);
        }
        public static string GetValue(DependencyObject obj)
        {
            return LegacySettingsBindingController.GetAttached(obj, ValueProperty);
        }
        public static void SetValue(DependencyObject obj, string value)
        {
            LegacySettingsBindingController.SetAttached(obj, ValueProperty, value);
        }
        public static void ResetSettings(DependencyObject Target)
        {
            LegacySettingsBindingController.ResetSettings(Target, KeyProperty, Key => Settings.ResetInstanceUserPreference(Key, Instance: LauncherInstanceContext.Instance));
        }
        public static void RefreshSettings(DependencyObject Target)
        {
            _refreshing++;
            try
            {
                LegacySettingsBindingController.RefreshSettings(Target, KeyProperty, Key => Settings.Get(Key, Instance: LauncherInstanceContext.Instance), RefreshControl);
            }
            finally { _refreshing--; }
        }
        private static void RefreshControl(DependencyObject Target, object NewValue)
        {
            if (Target is ISettingControl)
            {
                ((ISettingControl)Target).RefreshSetting(Conversions.ToString(NewValue));
                return;
            }
            switch (Target)
            {
                case ComboBox combo:
                    string value = Conversions.ToString(NewValue);
                    int match = FindChoice(combo, value);
                    if (match >= 0) combo.SelectedIndex = match;
                    break;
                case RadioButton radio:
                    radio.IsChecked = GetValue(radio) == Conversions.ToString(NewValue);
                    break;
                case CheckBox check:
                    check.IsChecked = Conversions.ToBoolean(NewValue);
                    break;
                case Slider slider:
                    slider.Value = Conversions.ToDouble(NewValue);
                    break;
                case TextBox text:
                    text.Text = Conversions.ToString(NewValue) ?? "";
                    break;
            }
        }

        private static int FindChoice(ComboBox combo, string value)
        {
            for (int index = 0; index < combo.Items.Count; index++)
                if (ChoiceValue(combo.Items[index], index) == value) return index;
            return -1;
        }

        private static string ChoiceValue(object item, int index) =>
            (item is DependencyObject option ? GetValue(option) : null) ?? index.ToString(CultureInfo.InvariantCulture);

        internal static void BindNativeControls(DependencyObject target, Func<bool> maySave = null)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(target).OfType<DependencyObject>())
                BindNativeControls(child, maySave);
            if (GetKey(target) is not { } key || target is ISettingControl) return;
            void Save(object value)
            {
                if (maySave?.Invoke() != false) SaveNative(key, value);
            }
            switch (target)
            {
                case ComboBox combo:
                    combo.SelectionChanged += (sender, args) =>
                    {
                        if (ReferenceEquals(args.OriginalSource, sender) && combo.SelectedIndex >= 0)
                            Save(ChoiceValue(combo.SelectedItem, combo.SelectedIndex));
                    };
                    break;
                case RadioButton radio:
                    radio.Checked += (_, _) => { if (radio.IsChecked == true) Save(GetValue(radio)); };
                    break;
                case CheckBox check:
                    check.Checked += (_, _) => Save(check.IsChecked == true);
                    check.Unchecked += (_, _) => Save(check.IsChecked == true);
                    break;
                case Slider slider:
                    slider.ValueChanged += (_, _) => Save((int)Math.Round(slider.Value));
                    break;
                case TextBox text:
                    text.TextChanged += (_, _) =>
                    {
                        if (text is not GlassValidatedTextBox field || field.IsValidated) Save(text.Text);
                    };
                    break;
            }
        }

        private static void SaveNative(string key, object value)
        {
            if (_refreshing == 0 && ModAnimation.AniControlEnabled == 0 && value is not null)
                Settings.SetInstanceUserPreference(key, value, Instance: LauncherInstanceContext.Instance);
        }
        public static void SaveSetting(object sender)
        {
            LegacySettingsBindingController.SaveSetting(sender, ModAnimation.AniControlEnabled, Control => Control is ISettingControl, KeyProperty, Control => ((ISettingControl)Control).GetCurrentSetting(), (Key, Value) =>
                Settings.SetInstanceUserPreference(Key, Value, Instance: LauncherInstanceContext.Instance));
        }
    }

    public interface ISettingControl
    {
        void RefreshSetting(string NewValue);
        string GetCurrentSetting();
    }
}
