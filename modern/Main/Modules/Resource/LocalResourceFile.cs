using System;
using System.IO;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL
{
    public class LocalResourceFile : LocalResourceFileData<ResourceProject, ResourceVersion>
    {

        private static readonly LocalResourceFileServices<ResourceProject, ResourceVersion> Services = new LocalResourceFileServices<ResourceProject, ResourceVersion>()
        {
            ReadProject = new Func<JObject, ResourceProject>(Data => new ResourceProject(Data)),
            ReadVersion = ResourceVersion.FromCacheJson,
            HideUpdates = new Func<bool>(() => Settings.Get<bool>("UiHiddenFunctionModUpdate")),
            DisableInstanceUpdates = () => LauncherInstanceContext.Instance is { } instance &&
                Settings.Get<bool>("VersionAdvanceDisableModUpdate", Instance: instance),
            ReadHashCache = new Func<string, string>(Key => ModBase.ReadIni(ModBase.PathTemp + @"Cache\ModHash.ini", Key)),
            WriteHashCache = new Action<string, string>((Key, Value) => ModBase.WriteIni(ModBase.PathTemp + @"Cache\ModHash.ini", Key, Value)),
            ReadBytes = new Func<string, byte[]>(Path => FileUtils.ReadAsBytes(Path)),
            Sha1 = new Func<string, string>(Path => CryptographyUtils.ComputeFileHash(Path, CryptographyUtils.HashMethod.Sha1))
        };

        public LocalResourceFile(FileInfo FileInfo) : base(FileInfo, Services)
        {
        }
        public LocalResourceFile(string FullName) : this(FileUtils.GetInfo(FullName))
        {
        }
        public event OnUpdateEventHandler OnUpdate;

        public delegate void OnUpdateEventHandler(LocalResourceFile sender);
        protected override void NotifyUpdated()
        {
            OnUpdate?.Invoke(this);
        }
        public override bool Equals(object Obj)
        {
            return base.Equals(Obj as LocalResourceFile);
        }
        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
    }
}
