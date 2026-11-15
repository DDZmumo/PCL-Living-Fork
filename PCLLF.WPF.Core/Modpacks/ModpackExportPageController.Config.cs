using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using Microsoft.VisualBasic.CompilerServices;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLCS;

public sealed partial class ModpackExportPageController<TInstance, TOption, TCheck, TLocal, TProject, TVersion, TCombo>
    where TInstance : class where TOption : ModpackExportOptionData where TCheck : FrameworkElement
    where TLocal : LocalResourceFileData<TProject, TVersion> where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData where TCombo : class {
    public List<string>? RulesOverrides {
        get => state.Rules();
        set {
            state.SetRules(value);
            if (value is null) {
                view.OverrideCancel.Visibility = Visibility.Collapsed; view.Options.Visibility = Visibility.Visible;
                view.CardInlines().Clear(); view.CardInlines().Add(new Run("导出内容列表") { FontWeight = FontWeights.Bold });
            } else {
                view.OverrideCancel.Visibility = Visibility.Visible; view.Options.Visibility = Visibility.Collapsed;
                view.CardInlines().Clear(); view.CardInlines().Add(new Run("导出内容列表:    ") { FontWeight = FontWeights.Bold });
                view.CardInlines().Add(new Run("从配置文件中读取") { FontWeight = FontWeights.Normal });
            }
        }
    }
    public IEnumerable<string> Rules() {
        if (RulesOverrides is not null) foreach (string rule in RulesOverrides) yield return rule;
        else {
            yield return ""; yield return "# 修改下方的规则以控制需要导出的内容。";
            yield return "# 以 ! 开头以反选。可以使用 *、?、[] 通配符。靠后的行覆盖靠前的。"; yield return "";
            foreach (TCheck check in AllOptions(false)) {
                if (!view.Checked(check)) continue;
                TOption option = GetOption(check);
                List<string> rules = option.Rules?.Split('|').ToList() ?? new List<string>();
                if (ReferenceEquals(check, view.ShaderSettings)) foreach (TCheck shader in view.Shaders.Children.OfType<TCheck>()) {
                    if (!view.Checked(shader)) continue;
                    string file = $"shaderpacks/{GetOption(shader).Title}.txt";
                    if (!FileUtils.Exists(Path.Combine(host.GameRoot(host.Instance()), file))) continue;
                    rules.Add(StringUtils.LikePatternEscape(file));
                }
                if (!rules.Any()) continue;
                yield return $"# {option.Title}";
                foreach (string rule in rules) yield return rule;
                yield return "";
            }
            yield return "# 排除的文件"; yield return "!*.log"; yield return "!*.dat_old"; yield return "!*.BakaCoreInfo";
            yield return "!hmclversion.cfg"; yield return "!log4j2.xml"; yield return "";
        }
    }
    public IEnumerable<string> ExtraFiles() {
        if (state.Extra() is not null) foreach (string file in state.Extra()!) yield return file;
        else { yield return ""; yield return "# 如果想将额外的文件自动放到压缩包根目录中，可以将它们的路径写在下方。"; yield return "# 必须是完整路径。每行中，若以 \\ 结尾则代表是文件夹，不以 \\ 结尾则代表是文件。"; yield return ""; }
    }
    public void ResetConfigOverrides() { RulesOverrides = null; state.SetPackPath(null); state.SetExtra(null); view.ScrollHome(); }
    public void OptionsMouseDown() { if (RulesOverrides is null) return; ResetConfigOverrides(); }
    public void ExportConfig() {
        try {
            string? path = host.SaveFile("选择文件位置", "export_config.txt", host.ReadConfigPath(), new() { ("txt", "整合包导出配置") });
            if (path is null) return;
            host.WriteConfigPath(path);
            List<string> lines = new() { "Name:" + view.Name.Text, "Version:" + view.Version.Text, "",
                $"# 当版本设置中选择了 {host.QuoteLeft()}使用版本文件夹中的 Java{host.QuoteRight()} 时，是否打包版本文件夹中的 Java。",
                "IncludeJava:" + view.Checked(view.Java), "", "# 是否打包当前 PCL Living Fork，以便没有启动器的玩家安装整合包。",
                "IncludeLauncher:" + view.Checked(view.Launcher), "", "# 是否打包 PCL 个性化内容，例如功能隐藏设置、主页、背景音乐和图片等。",
                "IncludeLauncherCustom:" + view.Checked(view.Custom), "", "# 是否默认开启 PCL Living Fork 整合包模式，仅在 IncludeLauncher 为 True 时生效。",
                "LivingModpackMode:" + view.Checked(view.ModpackMode), "", "# 导入后是否开启该版本的版本隔离，不修改全局默认设置。",
                "VersionIsolation:" + view.Checked(view.Isolation), "", "# 是否将 Mod、资源包、光影包的文件直接放入整合包中，这样在导入时就无需联网下载它们。",
                "# 建议仅在无法稳定连接 CurseForge 或 Modrinth 时才考虑启用。", "# 二次分发可能违反使用协议，请尽量不要公开发布包含资源文件的整合包！",
                "DontCheckHostedAssets:" + view.Checked(view.IncludeAssets), "", "# 若资源未在 Modrinth 上托管，则将其原始文件直接放入整合包中。",
                "# 如果你想将整合包上传到 Modrinth，这能帮助你分辨哪些文件未在 Modrinth 上托管。", "# 此选项与 DontCheckHostedAssets 冲突。",
                "ModrinthUploadMode:" + view.Checked(view.Modrinth), "", "# 导出的文件的存放位置。",
                "# 若设置了此项，在导出时会直接将文件放到此路径，不会弹窗要求选择。", "# 若 IncludeLauncher 为 True，应以 .zip 结尾；若为 False，应以 .mrpack 结尾。",
                "PackPath:" + (state.PackPath() ?? ""), "", Separator };
            lines.AddRange(Rules()); lines.Add(Separator); lines.AddRange(ExtraFiles());
            FileUtils.Write(path, string.Join("\r\n", lines)); host.HintGreen("已保存配置文件：" + path); host.Explorer(path);
        } catch (Exception ex) { host.ErrorAlert(ex, "保存配置失败"); }
    }
    public void ImportConfig() {
        try {
            string? path = host.SelectConfig("选择配置文件", host.ReadConfigPath());
            if (string.IsNullOrEmpty(path)) return;
            host.WriteConfigPath(path!);
            string[] segments = FileUtils.ReadAsString(path!).Split(Separator, false);
            Dictionary<string, string> ini = new();
            foreach (string raw in segments[0].SplitLines(true)) {
                string line = raw.Trim();
                if (line == "" || line.StartsWithF("#") || line.StartsWithF("=")) continue;
                int index = line.IndexOfF(":");
                if (index > 0) ini[line.Substring(0, index)] = line.Substring(index + 1);
            }
            string? Get(string name, string? fallback) => ini.TryGetValue(name, out string? result) ? result : fallback;
            bool Bool(string name, bool fallback) => Conversions.ToBoolean(Get(name, fallback.ToString()));
            view.Name.Text = Get("Name", ""); view.Version.Text = Get("Version", "");
            view.SetChecked(view.Java, Bool("IncludeJava", true)); view.SetChecked(view.Launcher, Bool("IncludeLauncher", true)); view.SetChecked(view.Custom, Bool("IncludeLauncherCustom", true));
            view.SetChecked(view.ModpackMode, Bool("LivingModpackMode", true)); view.SetChecked(view.Isolation, Bool("VersionIsolation", true));
            view.SetChecked(view.IncludeAssets, Bool("DontCheckHostedAssets", false));
            if (!view.Checked(view.IncludeAssets)) view.SetChecked(view.Modrinth, Bool("ModrinthUploadMode", false));
            state.SetPackPath(Get("PackPath", null)); RulesOverrides = segments[1].SplitLines(true).ToList();
            state.SetExtra(segments.Length > 2 ? segments[2].SplitLines(true).ToList() : null);
            host.HintGreen("已读取配置文件：" + path);
        } catch (Exception ex) { host.ErrorAlert(ex, "读取配置失败"); }
    }
    public void StartExport() {
        if (host.ModpackEnabled()) return;
        string name = string.IsNullOrEmpty(view.Name.Text) ? view.NameHint() : view.Name.Text;
        string version = string.IsNullOrEmpty(view.Version.Text) ? "1.0.0" : view.Version.Text;
        string loaderName = "导出整合包：" + name;
        foreach (string ongoing in host.TaskNames()) if (ongoing == loaderName) { host.ShowTasks(); return; }
        string? path = null, configured = state.PackPath();
        if (!string.IsNullOrWhiteSpace(configured) && !configured!.EndsWithF("\\") && !configured.EndsWithF("/")) {
            try { DirectoryUtils.Create(PathUtils.RemoveLastPart(configured!)); path = configured; host.Info($"使用配置文件中指定的导出路径：{configured}"); }
            catch (Exception ex) {
                host.Warn(ex, $"无法使用配置文件中指定的导出路径（{configured}）");
                if (host.Confirm($"指定的路径：{configured}\r\n\r\n{host.ErrorDisplay(ex)}", "无法使用配置文件中指定的导出路径", "确定", "取消") == 2) return;
            }
        }
        if (path is null) {
            List<(string Extension, string Display)> extensions = new();
            if (!view.Checked(view.Launcher) && view.Checked(view.Modrinth)) extensions.Add(("mrpack", "Modrinth 整合包文件"));
            extensions.Add(("zip", "压缩文件"));
            if (!view.Checked(view.Launcher) && !view.Checked(view.Modrinth)) extensions.Add(("mrpack", "Modrinth 整合包文件"));
            path = host.SaveFile("选择导出位置", name + (string.IsNullOrEmpty(view.Version.Text) ? "" : " " + view.Version.Text), null, extensions);
            host.Info($"手动指定的导出路径：{path}");
        }
        if (path is null) return;
        string cacheFolder = host.TempFolder(); TInstance instance = host.Instance();
        bool includeJava = RulesOverrides is null && view.Java.Visibility == Visibility.Visible && view.Checked(view.Java) || RulesOverrides is not null && view.Checked(view.Java) && host.JavaType(instance) == 2;
        string gameRoot = host.GameRoot(instance);
        bool checkHosted = !view.Checked(view.IncludeAssets), uploadMode = view.Checked(view.Modrinth), launcher = view.Checked(view.Launcher);
        bool custom = launcher && view.Checked(view.Custom), modpackMode = launcher && view.Checked(view.ModpackMode), isolation = view.Checked(view.Isolation);
        var request = new ModpackExportRequest<TInstance> { Name = name, Version = version, LoaderName = loaderName, PackPath = path, CacheFolder = cacheFolder, OverridesFolder = cacheFolder + @"modpack\overrides\", Instance = instance,
            IncludeJava = includeJava,
            GameRoot = gameRoot, CheckHostedAssets = checkHosted, ModrinthUploadMode = uploadMode, IncludeLauncher = launcher,
            IncludeCustom = custom, IncludeModpackMode = modpackMode, VersionIsolation = isolation,
            Rules = ModpackExportPipeline<TInstance, TLocal, TProject, TVersion>.StandardizeLines(Rules(), true).ToList(), ExtraFiles = ModpackExportPipeline<TInstance, TLocal, TProject, TVersion>.StandardizeLines(ExtraFiles(), false).ToList() };
        host.Info($"准备导出整合包，共有 {request.Rules.Count} 条规则，{request.ExtraFiles.Count} 条追加内容行");
        TCombo loader = host.CreateCombo(request); host.StartCombo(loader); host.AddTaskbar(loader); host.RefreshDownload(); host.RibbleDownload(); host.ShowTasks();
    }
}
