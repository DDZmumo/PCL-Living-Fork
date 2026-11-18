using System.Windows;
using Microsoft.VisualBasic.CompilerServices;
using PCLCS;

namespace PCL
{

    // 原附加属性 owner 与原接口身份；遍历/保存次序由 C# controller 承接。
    public class SettingService
    {
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
            LegacySettingsBindingController.ResetSettings(Target, KeyProperty, Key => Settings.ResetInstanceUserPreference(Key, Instance: PageInstanceLeft.Instance));
        }
        public static void RefreshSettings(DependencyObject Target)
        {
            LegacySettingsBindingController.RefreshSettings(Target, KeyProperty, Key => Settings.Get(Key, Instance: PageInstanceLeft.Instance), RefreshControl);
        }
        private static void RefreshControl(DependencyObject Target, object NewValue)
        {
            if (Target is ISettingControl)
                ((ISettingControl)Target).RefreshSetting(Conversions.ToString(NewValue));
        }
        public static void SaveSetting(object sender)
        {
            LegacySettingsBindingController.SaveSetting(sender, ModAnimation.AniControlEnabled, Control => Control is ISettingControl, KeyProperty, Control => ((ISettingControl)Control).GetCurrentSetting(), (Key, Value) =>
                Settings.SetInstanceUserPreference(Key, Value, Instance: PageInstanceLeft.Instance));
        }
    }

    public interface ISettingControl
    {
        void RefreshSetting(string NewValue);
        string GetCurrentSetting();
    }
}
