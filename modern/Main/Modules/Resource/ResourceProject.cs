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
        public MyVirtualizingElement<MyResourceItem> ToResourceItem(bool ShowMcVersionDesc, bool ShowLoaderDesc, ResourceTypes? ResourceTypeContext = null)
        {
            return new MyVirtualizingElement<MyResourceItem>(() => {
                var Context = ResourcePresentation.ProjectContext(this, ShowMcVersionDesc, ShowLoaderDesc);
                MyResourceItem Row = ResourcePresentation.ProjectRow(this, Context, CreateItemHost());
                Row.ResourceTypeContext = ResourceTypeContext;
                return Row;
            }) { Height = 64d };
        }
        public void ApplyLogoToMyImage(MyImage Img)
        {
            ResourcePresentation.ApplyLogo(this, Value => Img.Source = Value, Value => Img.FallbackSource = Value);
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

        // 仅映射原控件成员，分段、标题、图片源、筛选描述及计数格式均由 C# 决定。
        private static ResourceProjectListHost<MyResourceItem> CreateItemHost()
        {
            return new ResourceProjectListHost<MyResourceItem>()
            {
                Create = new Func<MyResourceItem>(() => new MyResourceItem()),
                SetTag = new Action<MyResourceItem, object>((Row, Value) => { Row.Tag = Value; Row.BindFavorite((ResourceProject)Value); }),
                SetLogoSource = new Action<MyResourceItem, string>((Row, Value) => Row.PathLogo.Source = Value),
                SetLogoFallback = new Action<MyResourceItem, string>((Row, Value) => Row.PathLogo.FallbackSource = Value),
                SetTitle = new Action<MyResourceItem, string>((Row, Value) => Row.Title = Value),
                SetSubtitle = new Action<MyResourceItem, string>((Row, Value) => Row.SubTitle = Value),
                RemoveSubtitle = new Action<MyResourceItem>(Row => ((StackPanel)Row.LabTitleRaw.Parent).Children.Remove(Row.LabTitleRaw)),
                SetContent = new Action<MyResourceItem, List<string>, string>((Row, Tags, Description) =>
                    {
                        Row.Tags = Tags;
                        Row.Description = Description;
                    }),
                HideVersion = HideVersion,
                SetVersion = new Action<MyResourceItem, string>((Row, Value) => Row.LabVersion.Text = Value),
                SetFooter = new Action<MyResourceItem, string, string, string>((Row, Source, Time, Downloads) =>
                    {
                        Row.LabSource.Text = Source;
                        Row.LabTime.Text = Time;
                        Row.LabDownload.Text = Downloads;
                    })
            };
        }
        private static void HideVersion(MyResourceItem Row)
        {
            ((Grid)Row.PathVersion.Parent).Children.Remove(Row.PathVersion);
            ((Grid)Row.LabVersion.Parent).Children.Remove(Row.LabVersion);
            Row.ColumnVersion1.Width = new GridLength(0d);
            Row.ColumnVersion2.MaxWidth = 0d;
            Row.ColumnVersion3.Width = new GridLength(0d);
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
