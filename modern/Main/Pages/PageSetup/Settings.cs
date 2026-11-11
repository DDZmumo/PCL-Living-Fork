using System;
using System.Collections.Generic;
using MeloongCore;
using Microsoft.VisualBasic.CompilerServices;
using PCLCS;

namespace PCL
{

    // LF 的原名记录、枚举、回调与实际宿主边界；全部存储/转换/迁移业务在 C#。
    public partial class Settings
    {
        private static readonly LfRegistryStorage LfRegistry = new LfRegistryStorage(LegacySettingsRegistryStore.Open, ModSecret.RegFolder);
        private static readonly LegacySettingsEngine<ModMinecraft.McInstance, Setting, PclAccountMigrationPlan> Backend = new LegacySettingsEngine<ModMinecraft.McInstance, Setting, PclAccountMigrationPlan>(() => Entries, () => SharedAccountSync, () => new PclAccountMigrationPlan(), new LegacySettingsHost<ModMinecraft.McInstance>()
        {
            InstancePath = new Func<ModMinecraft.McInstance, string>(Instance => Instance.PathVersion),
            Identify = new Func<string>(() => ModBase.Identify),
            RegistryFolder = new Func<string>(() => ModSecret.RegFolder),
            ReadIni = new Func<string, string, string, string>((File, Key, Fallback) => ModBase.ReadIni(File, Key, Fallback)),
            WriteIni = new Action<string, string, string>((File, Key, Value) => ModBase.WriteIni(File, Key, Value)),
            DeleteIni = new Action<string, string>((File, Key) => ModBase.DeleteIniKey(File, Key)),
            HasIni = new Func<string, string, bool>((File, Key) => ModBase.HasIniKey(File, Key)),
            ReadRegistry = (Folder, Key, Fallback) => LfRegistry.ReadRegistry(Folder, Key, Fallback),
            WriteRegistry = LfRegistry.WriteRegistry,
            HasRegistry = LfRegistry.HasRegistry,
            OpenRegistry = LfRegistry.OpenRegistry,
            IdentifyCandidates = LegacySettingsRegistryStore.IdentifyCandidates,
            Log = LogSettings
        });

        private static void LogSettings(Exception ErrorValue, string Message, LegacySettingsLogLevel Level, bool Toast)
        {
            switch (Level)
            {
                case LegacySettingsLogLevel.Trace:
                    {
                        Logger.Trace(Message);
                        break;
                    }
                case LegacySettingsLogLevel.Info:
                    {
                        Logger.Info(Message);
                        break;
                    }
                case LegacySettingsLogLevel.Warning:
                    {
                        if (ErrorValue is null)
                        {
                            if (Toast)
                                Logger.Warn(Message, LogBehavior.Toast);
                            else
                                Logger.Warn(Message);
                        }
                        else if (Toast)
                            Logger.Warn(ErrorValue, Message, LogBehavior.Toast);
                        else
                            Logger.Warn(ErrorValue, Message);
                        break;
                    }
                case LegacySettingsLogLevel.Error:
                    {
                        if (ErrorValue is null)
                        {
                            if (Toast)
                                Logger.Error(Message, LogBehavior.Toast);
                            else
                                Logger.Error(Message);
                        }
                        else if (Toast)
                            Logger.Error(ErrorValue, Message, LogBehavior.Toast);
                        else
                            Logger.Error(ErrorValue, Message);
                        break;
                    }
            }
        }

        internal static bool IsPclAccountSetting(Setting Entry)
        {
            return LegacySettingsEngine<ModMinecraft.McInstance, Setting, PclAccountMigrationPlan>.IsAccountSetting(Entry);
        }
        internal static bool IsMicrosoftAccountSetting(Setting entry) => LegacySettingsEngine<ModMinecraft.McInstance, Setting, PclAccountMigrationPlan>.IsMicrosoftAccountSetting(entry);
        internal static string RegistryValueName(Setting entry) => LegacySettingsEngine<ModMinecraft.McInstance, Setting, PclAccountMigrationPlan>.RegistryValueName(entry);
        internal static Dictionary<string, string> ReadPclAccountRegistrySnapshot(Microsoft.Win32.RegistryKey store, bool legacy = false) =>
            Backend.ReadAccountRegistrySnapshot(store is null ? null : new LegacySettingsRegistryStore(store), legacy);

        internal static string GetPclSourceIdentify(string HardwareConfig, string Seed)
        {
            return LegacySettingsCrypto.SourceIdentify(HardwareConfig, Seed);
        }
        private static string DecodePclAccountValue(Setting Entry, string RawValue, string DeviceIdentify)
        {
            return Backend.DecodeAccountValue(Entry, RawValue, DeviceIdentify);
        }
        private static string RegistryFolderFor(Setting Entry)
        {
            return Backend.RegistryFolderFor(Entry);
        }
        internal static List<string> PclIdentifyCandidates(string Seed)
        {
            return LegacySettingsRegistryStore.IdentifyCandidates(Seed);
        }
        private static string DecodePclAccountWithKeys(Setting Entry, string RawValue, IEnumerable<string> Keys)
        {
            return Backend.DecodeAccountWithKeys(Entry, RawValue, Keys);
        }
        private static string AccountGroup(string Key)
        {
            return LegacySettingsEngine<ModMinecraft.McInstance, Setting, PclAccountMigrationPlan>.AccountGroup(Key);
        }
        internal class PclAccountMigrationPlan : LegacyAccountMigrationPlan
        {
        }
        internal static PclAccountMigrationPlan PlanPclAccountMigration(IDictionary<string, string> Official, IDictionary<string, string> Legacy, IEnumerable<string> OfficialKeys, IEnumerable<string> LegacyKeys, string CurrentIdentify)
        {
            return Backend.PlanAccountMigration(Official, Legacy, OfficialKeys, LegacyKeys, CurrentIdentify);
        }
        internal static void ApplyPclAccountMigration(PclAccountMigrationPlan Plan, Microsoft.Win32.RegistryKey Official, Microsoft.Win32.RegistryKey Legacy)
        {
            Backend.ApplyAccountMigration(Plan, new LegacySettingsRegistryStore(Official), Legacy is null ? null : new LegacySettingsRegistryStore(Legacy));
        }
        internal static void ImportCompatiblePclAccounts()
        {
            Backend.ImportCompatibleAccounts();
        }
        private static void ImportCompatiblePclAccountsCore()
        {
            Backend.ImportCompatibleAccountsCore();
        }
        internal static bool IsCompatiblePclSetting(Setting Entry)
        {
            return LegacySettingsEngine<ModMinecraft.McInstance, Setting, PclAccountMigrationPlan>.IsCompatibleSetting(Entry);
        }
        private static string ReadRegistrySetting(Setting Entry, string DefaultValue)
        {
            return Backend.ReadRegistrySetting(Entry, DefaultValue);
        }

        public static readonly Dictionary<string, Setting> Entries = LegacySettingsDefinitions.Create((Key, Value, Source, Encrypted, Changed) => new Setting(Key, Value, (Sources)Source, Encrypted, Changed), EntryHostDefault, EntryCallbacks());

        private static Dictionary<string, Action<object>> EntryCallbacks()
        {
            return new Dictionary<string, Action<object>>() { { "SystemDebugMode", (_) => Logger.Instance.MinLevel = ModBase.ModeDebug ? LogLevel.Trace : LogLevel.Info }, { "LivingModpackMode", ModModpackMode.OnChanged }, { "LaunchSkinID", (_) => PageLaunchLeft.SkinLegacy.Start() }, { "LaunchSkinType", (_) => PageSetupLaunch.UpdateSkinType() }, { "LaunchRecentWorlds", QuickPlayWorlds.OnEnabledChanged }, { "LaunchModDependencyCheck", (_) => LaunchDependencyCheck.SettingsChanged() }, { "LaunchModDependencyAutoFill", (_) => LaunchDependencyCheck.SettingsChanged() }, { "LaunchRamType", (_) => PageSetupLaunch.UpdateRamType() }, { "ToolDownloadSpeed", Value => ModNet.UpdateNetTaskSpeedLimitHigh(Microsoft.VisualBasic.CompilerServices.Conversions.ToInteger(Value)) }, { "ToolDownloadCert", Value => ModNet.ShouldValidateSslCertificateOnLogin(Microsoft.VisualBasic.CompilerServices.Conversions.ToBoolean(Value)) }, { "UiLauncherTransparent", Value => { int ConvertedValue = Microsoft.VisualBasic.CompilerServices.Conversions.ToInteger(Value); if (ModMain.FrmMain is not null) ModMain.FrmMain.Opacity = ConvertedValue / 1000d + 0.4d; } }, { "UiLauncherColorMode", _ => ModSecret.ThemeRefresh() }, { "UiPanelOpacity", _ => ModTheme.RefreshPanelOpacity() }, { "UiLauncherTheme", Value => ModSecret.ThemeRefresh(Microsoft.VisualBasic.CompilerServices.Conversions.ToInteger(Value)) }, { "UiBackgroundMode", (_) => PageSetupUI.BackgroundRefresh(false, true) }, { "UiBackgroundColorful", (_) => ModSecret.ThemeRefresh() }, { "UiBackgroundOpacity", FormMain.UpdateBackgroundAndTitleBar }, { "UiBackgroundBlur", FormMain.UpdateBackgroundAndTitleBar }, { "UiBackgroundSuit", FormMain.UpdateBackgroundAndTitleBar }, { "UiCustomType", (_) => PageSetupUI.OnMainPageTypeChanged() }, { "UiLogoType", FormMain.UpdateBackgroundAndTitleBar }, { "UiLogoText", FormMain.UpdateBackgroundAndTitleBar }, { "UiLogoLeft", FormMain.UpdateBackgroundAndTitleBar }, { "UiHiddenPageDownload", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenPageLink", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenPageSetup", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenPageOther", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenFunctionSelect", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenFunctionModUpdate", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenFunctionInstanceModify", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenFunctionHidden", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenSetupLaunch", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenSetupUi", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenSetupLink", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenSetupSystem", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenOtherHelp", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenOtherFeedback", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenOtherVote", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenOtherAbout", (_) => PageSetupUI.HiddenRefresh() }, { "UiHiddenOtherTest", (_) => PageSetupUI.HiddenRefresh() }, { "VersionRamType", Value => PageInstanceSetup.OnVersionRamTypeChanged(Microsoft.VisualBasic.CompilerServices.Conversions.ToInteger(Value)) }, { "VersionServerLogin", Value => PageInstanceSetup.OnVersionServerLoginChanged(Microsoft.VisualBasic.CompilerServices.Conversions.ToInteger(Value)) } };
        }

        private static object EntryHostDefault(string Key)
        {
            switch (Key ?? "")
            {
                case "LoginType":
                    {
                        return ModLaunch.McLoginType.Legacy;
                    }
                case "UiLauncherHue":
                    {
                        return ModSecret.DefaultThemeHue;
                    }
                case "UiLauncherSat":
                    {
                        return ModSecret.DefaultThemeSat;
                    }
                case "UiLauncherDelta":
                    {
                        return ModSecret.DefaultThemeDelta;
                    }
                case "UiLauncherLight":
                    {
                        return ModSecret.DefaultThemeLight;
                    }
                case "UiBackgroundOpacity":
                    {
                        return ModSecret.DefaultBackgroundOpacity;
                    }

                default:
                    {
                        throw new KeyNotFoundException(Key);
                    }
            }
        }

        public enum Sources
        {
            Normal,
            Registry,
            Instance
        }
        public class Setting : LegacySettingData<ModMinecraft.McInstance>
        {
            public Sources Source;
            public Setting(string Key, object Value, Sources Source = Sources.Normal, bool Encrypted = false, Action<object> OnChanged = null) : base(Key, Value, Encrypted, OnChanged, (ErrorValue, Message) => Logger.Error(ErrorValue, Message))
            {
                this.Source = Source;
            }
            public override LegacySettingSource GetSourceCode()
            {
                return (LegacySettingSource)Source;
            }
            protected override string GetInstancePath(ModMinecraft.McInstance Instance)
            {
                return Instance.PathVersion;
            }
            public override void Save(ModMinecraft.McInstance Instance = null)
            {
                Backend.SaveEntry(this, Instance);
            }
        }

        public static void Set(string Key, object Value, ModMinecraft.McInstance Instance = null)
        {
            Backend.Set(Key, Value, Instance);
        }
        private static void SetCore(string Key, object Value, ModMinecraft.McInstance Instance)
        {
            Backend.SetCore(Key, Value, Instance);
        }
        public static void SetSafe(string Key, object Value, ModMinecraft.McInstance Instance = null)
        {
            Backend.SetSafe(Key, Value, Instance);
        }
        public static object Get(string Key, ModMinecraft.McInstance Instance = null)
        {
            return Backend.Get(Key, Instance);
        }
        private static object GetCore(string Key, ModMinecraft.McInstance Instance, Func<Setting, string> ReadRaw = null, string DeviceIdentify = null)
        {
            return Backend.GetCore(Key, Instance, ReadRaw, DeviceIdentify);
        }
        public static T Get<T>(string Key, ModMinecraft.McInstance Instance = null)
        {
            return Conversions.ToGenericParameter<T>(Get(Key, Instance));
        }
        public static object GetSafe(string Key, ModMinecraft.McInstance Instance = null)
        {
            return Backend.GetSafe(Key, Instance);
        }
        public static void Reset(string Key, ModMinecraft.McInstance Instance = null)
        {
            Backend.Reset(Key, Instance);
        }
        private static void ResetCore(string Key, ModMinecraft.McInstance Instance)
        {
            Backend.ResetCore(Key, Instance);
        }
        public static string GetDefault(string Key)
        {
            return Backend.GetDefault(Key);
        }
        public static bool HasSaved(string Key, ModMinecraft.McInstance Instance = null)
        {
            return Backend.HasSaved(Key, Instance);
        }
        private static string DesEncrypt(string sourceString, string key = null)
        {
            return LegacySettingsCrypto.Encrypt(sourceString, key);
        }
        private static string DesDecrypt(string encryptedString, string key = null)
        {
            return LegacySettingsCrypto.Decrypt(encryptedString, key);
        }
    }
}
