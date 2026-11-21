using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL
{
    public class ResourceSearcher
    {
        public class SearchRequest : ResourceSearchRequestData<ResourceProject, SearchResult>
        {
            public SearchRequest(ResourceTypes Type, SearchResult Storage, int TargetResultCount) : base(Type, Storage, TargetResultCount)
            {
            }
            public override bool Equals(object Obj)
            {
                return base.Equals(Obj as SearchRequest);
            }
            public override int GetHashCode()
            {
                return base.GetHashCode();
            }
            public static bool operator ==(SearchRequest Left, SearchRequest Right)
            {
                return EqualityComparer<SearchRequest>.Default.Equals(Left, Right);
            }
            public static bool operator !=(SearchRequest Left, SearchRequest Right)
            {
                return !(Left == Right);
            }
        }
        public class SearchResult : ResourceSearchResultData<ResourceProject>
        {
        }
        private static readonly ResourceSearchEngine<ResourceProject, SearchResult> Engine = new ResourceSearchEngine<ResourceProject, SearchResult>(new ResourceSearchHost<ResourceProject>()
        {
            CreateProject = new Func<JObject, ResourceProject>(Data => new ResourceProject(Data)),
            Request = new Func<string, JToken>(Url => ModDownload.DlModRequest(Url)),
            StartThread = new Func<Action, string, Thread>((Work, Name) => ModBase.RunInNewThread(() => Work(), Name)),
            SimplifyChinese = SimplifyChinese,
            WikiEntries = new Func<List<WikiEntry>>(() => WikiEntry.All.Value),
            Info = new Action<string>(Message => Logger.Info(Message)),
            Warning = new Action<string>(Message => Logger.Warn(Message)),
            Warn = new Action<Exception, string>((ErrorValue, Message) => Logger.Warn(ErrorValue, Message))
        });
        // Keep LF's traditional-to-simplified search rule without depending on
        // VisualBasic's locale availability check under the modern ICU runtime.
        private static string SimplifyChinese(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            const uint simplifiedChinese = 0x02000000;
            int count = LCMapStringEx("zh-CN", simplifiedChinese, value, value.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (count == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法转换中文搜索关键词。");
            char[] output = new char[count];
            int written = LCMapStringEx("zh-CN", simplifiedChinese, value, value.Length, output, output.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (written == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法转换中文搜索关键词。");
            return new string(output, 0, written);
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern int LCMapStringEx(string localeName, uint flags, string source, int sourceCount,
            [Out] char[] destination, int destinationCount, IntPtr version, IntPtr reserved, IntPtr sortHandle);
        public static ModLoader.LoaderTask<SearchRequest, int> GetLoader(Func<SearchRequest> InputDelegate,
            Action<SearchRequest, IReadOnlyList<ResourceProject>> ShowAvailable = null)
        {
            return new ModLoader.LoaderTask<SearchRequest, int>("社区资源获取", task => Run(task, ShowAvailable), InputDelegate) { ReloadTimeout = 60 * 1000 };
        }
        private static void Run(ModLoader.LoaderTask<SearchRequest, int> Task, Action<SearchRequest, IReadOnlyList<ResourceProject>> ShowAvailable)
        {
            SearchRequest input = Task.Input;
            Thread worker = Thread.CurrentThread;
            bool Canceled() => Task.IsCanceledWithThread(worker);
            Engine.Run(input, new ResourceSearchTask()
            {
                IsCanceled = Canceled,
                Progress = new Func<double>(() => Task.Progress),
                SetProgress = value => { if (!Canceled()) Task.Progress = value; }
            }, ShowAvailable is not null && input.TargetResultCount == PageResource.PAGE_SIZE && ModMain.FrmMain?.GlassShellActive == true
                ? results => ShowAvailable(input, results) : null);
        }
    }
}
