using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCLCS;
using PCLLF.Presentation.Controls;

namespace PCL
{
    // Only LF original XAML/type/Handles/model identity adapters; all workflows are C#.
    public partial class PageOtherAbout
    {
        private LegacyAuxiliaryBasicController _controller;
        internal LegacyAuxiliaryBasicController Controller
        {
            get
            {
                if (_controller is null)
                    _controller = new LegacyAuxiliaryBasicController(AuxiliaryPageRuntime.Basic, new LegacyAuxiliaryBasicView() { Page = new Func<DependencyObject>(() => this), Scroll = new Func<ScrollViewer>(() => this.PanBack), About = new Func<MyListItemView>(() => this.ItemAboutPcl) });
                return _controller;
            }
        }

        public PageOtherAbout()
        {
            __LfAttachOwnHandles();
            this.InitializeComponent();
            LabMarkdigLicense.Text = ReadLicense("Markdig.txt");
            LabThirdPartyLicenses.Text = ReadLicense("ThirdParty.txt");
            LabMeloongCoreLicense.Text = ReadLicense("MeloongCore.txt");
            LabDotNetLicense.Text = ReadLicense("dotnet-runtime.txt");
            LabJavaWrapperLicense.Text = ReadLicense("JavaLaunchWrapper/NOTICE.txt") + "\n" + ReadLicense("JavaLaunchWrapper/LICENSE.txt");
            LabLwjglUnsafeLicense.Text = ReadLicense("LwjglUnsafeAgent/NOTICE.txt") + "\n" + LabMeloongCoreLicense.Text;
            LabTransformerLicense.Text = ReadLicense("HMCLTransformerDiscoveryService/NOTICE.txt") + "\n"
                + ReadLicense("HMCLTransformerDiscoveryService/LICENSE") + "\n"
                + ReadLicense("HMCLTransformerDiscoveryService/ADDITIONAL_TERMS.md");
        }

        private static string ReadLicense(string name)
        {
            using var license = OpenLicense(name);
            using var reader = new System.IO.StreamReader(license);
            return reader.ReadToEnd();
        }

        private static System.IO.Stream OpenLicense(string name)
        {
            return System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/LivingPCL;component/Licenses/" + name))?.Stream
                ?? throw new InvalidOperationException("Missing embedded license/source resource: " + name);
        }

        // The same resource set ships inside the single EXE; no loose source sidecar is needed.
        internal static void WriteTransformerSource(System.IO.Stream output)
        {
            const string prefix = "HMCLTransformerDiscoveryService/";
            using var archive = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true);
            using var manifest = System.Text.Json.JsonDocument.Parse(ReadLicense(prefix + "source-manifest.json"));
            AddFile("source-manifest.json");
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
                AddFile(file.GetProperty("path").GetString());

            void AddFile(string path)
            {
                using var source = OpenLicense(prefix + path);
                using var destination = archive.CreateEntry(path).Open();
                source.CopyTo(destination);
            }
        }

        private void BtnTransformerSource_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                using var source = new System.IO.MemoryStream();
                WriteTransformerSource(source);
                var path = MeloongCore.Wpf.Dialogs.SaveFile("保存组件对应源码", "HMCLTransformerDiscoveryService-1.0-source.zip",
                    filter: new[] { ("zip", "ZIP 压缩文件") });
                if (path is null) return;
                using var destination = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None);
                source.Position = 0;
                source.CopyTo(destination);
                ModMain.Hint("已保存 Transformer Discovery Service 对应源码。", ModMain.HintType.Green);
            }
            catch (Exception ex)
            {
                ModMain.Hint("保存组件对应源码失败：" + ex.Message, ModMain.HintType.Red);
            }
        }

        private void PageOtherAbout_Loaded(object sender, RoutedEventArgs e)
        {
            Controller.AboutLoaded();
        }

        private void BtnAboutUpdate_Click(object sender, MouseButtonEventArgs e)
        {
            Controller.CheckUpdate();
        }
    }
}
