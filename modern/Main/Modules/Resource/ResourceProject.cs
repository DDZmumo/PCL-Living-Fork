using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL
{

    /// <summary>原 LF 项目类型；完整模型、平台解析、缓存与呈现决策在 C#。</summary>
    public class ResourceProject : ResourceProjectModelData
    {

        protected internal static Dictionary<string, ResourceProject> Cache = new Dictionary<string, ResourceProject>();

        public ResourceProject(JObject Data) : base(Data, ResourceCatalogRuntime.ProjectServices)
        {
        }
        public static ResourceProject FromProjectId(string ProjectId, ResourcePlatforms Platform)
        {
            return ResourceCatalogRuntime.Pipeline.FromProjectId(ProjectId, Platform);
        }
        public static IEnumerable<ResourceProject> FromProjectIds(IEnumerable<string> ProjectIds, ResourcePlatforms Platform)
        {
            return ResourceCatalogRuntime.Pipeline.FromProjectIds(ProjectIds, Platform);
        }
        public bool IsLike(ResourceProject Project)
        {
            return base.IsLike(Project);
        }
        public override bool Equals(object Obj)
        {
            return base.Equals(Obj as ResourceProject);
        }
        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
        public static bool operator ==(ResourceProject Left, ResourceProject Right)
        {
            return EqualityComparer<ResourceProject>.Default.Equals(Left, Right);
        }
        public static bool operator !=(ResourceProject Left, ResourceProject Right)
        {
            return !(Left == Right);
        }

    }

    // 原可替换缓存字典与真实网络／镜像／日志服务；不建立第二份生产缓存。
    internal sealed class ResourceCatalogRuntime
    {
        internal static readonly ResourceVersionServices VersionServices = new ResourceVersionServices()
        {
            MirrorUrl = ModDownload.DlSourceModGet,
            DebugMode = new Func<bool>(() => ModBase.ModeDebug),
            ImageRoot = new Func<string>(() => ModBase.PathImage)
        };
        internal static readonly ResourceProjectServices ProjectServices = new ResourceProjectServices()
        {
            ReadVersion = new Func<JObject, ResourceTypes, ResourceVersionModelData>((Data, Types) => ResourceVersion.FromPlatformJson(Data, Types)),
            StoreCache = new Action<ResourceProjectModelData>(Project => ResourceProject.Cache[ResourceProjectKey.Get(Project.Platform, Project.Id)] = (ResourceProject)Project),
            MirrorUrl = ModDownload.DlSourceModGet,
            AllDrops = new Func<List<int>>(() => ModDownload.AllDrops),
            ImageRoot = new Func<string>(() => ModBase.PathImage),
            Info = new Action<string>(Message => Logger.Info(Message))
        };
        internal static readonly ResourceCatalogPipeline<ResourceProject, ResourceVersion> Pipeline = new ResourceCatalogPipeline<ResourceProject, ResourceVersion>(new ResourceCatalogHost<ResourceProject, ResourceVersion>()
        {
            ProjectCache = new Func<Dictionary<string, ResourceProject>>(() => ResourceProject.Cache),
            VersionCache = new Func<Dictionary<string, List<ResourceVersion>>>(() => ResourceVersion.ProjectFilesCache),
            CreateProject = new Func<JObject, ResourceProject>(Data => new ResourceProject(Data)),
            CreateVersion = new Func<ResourceVersion>(() => new ResourceVersion()),
            Request = new Func<string, HttpMethod, string, string, JToken>((Url, Method, Content, ContentType) => ModDownload.DlModRequest(Url, Method, Content, ContentType)),
            Info = new Action<string>(Message => Logger.Info(Message)),
            Warn = new Action<string>(Message => Logger.Warn(Message))
        });
    }
}
