using System;
using System.Collections.Generic;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL
{
    /// <summary>原 LF 资源版本类型，使用原枚举与列表的单份 C# 模型存储。</summary>
    public class ResourceVersion : ResourceVersionData<ResourceVersion.ReleaseTypes>
    {

        public ResourceVersion() : base(ResourceCatalogRuntime.VersionServices)
        {
        }
        public enum ReleaseTypes
        {
            Release = 1,
            Beta = 2,
            Alpha = 3
        }
        public enum DownloadReason
        {
            Standalone,
            Dependency,
            ModPack,
            Update
        }
        protected internal static Dictionary<string, List<ResourceVersion>> ProjectFilesCache = new Dictionary<string, List<ResourceVersion>>();

        public static ResourceVersion FromPlatformJson(JObject Data, ResourceTypes ExpectedResourceType)
        {
            return ResourceVersionRules.FromPlatformJson(Data, ExpectedResourceType, () => new ResourceVersion());
        }
        public static List<ResourceVersion> FromProjectId(string ProjectId, ResourcePlatforms Platform, bool LoadDependencies = true)
        {
            return ResourceCatalogRuntime.Pipeline.VersionsFromProjectId(ProjectId, Platform, LoadDependencies);
        }
        public static ResourceVersion FromCacheJson(JObject Data)
        {
            return ResourceVersionRules.FromCacheJson<ResourceVersion, ReleaseTypes>(Data, () => new ResourceVersion());
        }
        public ModNet.NetFile ToNetFile(string LocalFileOrFolder, DownloadReason? TrackDownloadReason = default, string TrackGameVersion = null, ModLoaders TrackLoader = ModLoaders.None)
        {
            var File = ToDownloadDescriptor(LocalFileOrFolder, TrackDownloadReason.HasValue ? TrackDownloadReason.Value.ToString() : null, TrackGameVersion, TrackLoader);
            return new ModNet.NetFile(File.Urls, File.Destination, new FileChecker() { Hash = File.Hash, ActualSize = File.Size }, SimulateBrowserHeaders: true);
        }
        public static IEnumerable<string> ParseModrinthTrackArguments(List<string> Urls, DownloadReason? TrackDownloadReason = default, string TrackGameVersion = null, ModLoaders TrackLoader = ModLoaders.None)
        {
            return ResourceVersionRules.ParseModrinthTrackArguments(Urls, TrackDownloadReason.HasValue ? TrackDownloadReason.Value.ToString() : null, TrackGameVersion, TrackLoader);
        }
        public static List<string> ParseCurseForgeDownloadUrls(string Url)
        {
            return ResourceVersionRules.ParseCurseForgeDownloadUrls(Url);
        }

        // 仅原懒列表项、实际 Click 委托与保存按钮的类型适配。

    }
}