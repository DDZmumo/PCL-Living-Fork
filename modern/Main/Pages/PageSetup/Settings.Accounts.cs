using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace PCL
{
    public partial class Settings
    {
        private static readonly object SharedAccountSync = new object();

        internal static Dictionary<string, string> PlanLegacyMicrosoftUpgrade(IDictionary<string, string> Raw, string DeviceIdentify)
        {
            return Backend.PlanLegacyMicrosoftUpgrade(Raw, DeviceIdentify);
        }
        internal static void EnsureMicrosoftCacheUpgrade()
        {
            Backend.EnsureMicrosoftCacheUpgrade();
        }
        internal static Dictionary<string, object> AccountSnapshot(string Prefix)
        {
            return Backend.AccountSnapshot(Prefix);
        }
        private static void WriteAccountValues(IDictionary<string, object> Values)
        {
            Backend.WriteAccountValues(Values);
        }

        internal static void WriteSkinSiteAccounts(string value)
        {
            try { WriteAccountValues(new Dictionary<string, object> { ["LfSkinSites"] = value }); }
            catch { throw new InvalidOperationException("保存外置登录账号失败。"); }
        }
        public static JObject UpdateMicrosoftAccounts(Action<JObject> Change)
        {
            return Backend.UpdateMicrosoftAccounts(Change);
        }
        public static void SaveMicrosoftSession(IDictionary<string, object> Values, string PreviousName, Func<bool> Canceled = null)
        {
            Backend.SaveMicrosoftSession(Values, PreviousName, Canceled);
        }
        public static void SaveAccountGroup(IDictionary<string, object> Values, Func<bool> Canceled = null)
        {
            Backend.SaveAccountGroup(Values, Canceled);
        }
        public static List<string> UpdateLegacyNames(Action<List<string>> Change)
        {
            return Backend.UpdateLegacyNames(Change);
        }
        public static Dictionary<string, string> UpdateServerAccounts(string Token, string Name, string Password, bool Remove)
        {
            return Backend.UpdateServerAccounts(Token, Name, Password, Remove);
        }
    }
}
