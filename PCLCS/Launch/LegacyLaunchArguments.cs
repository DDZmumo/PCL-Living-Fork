#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json.Linq;

namespace PCLCS;
public sealed partial class LegacyLaunchEngine<TInstance, TState, TCard, TOptions, TData, TServer, TMicrosoft, TOffline, TWatcher>
    where TInstance : LegacyMcInstance<TInstance, TState, TCard>
    where TState : struct, Enum where TCard : struct, Enum where TOptions : class, ILegacyLaunchOptions<TInstance>, new()
    where TData : class, ILegacyLaunchLoginData where TServer : TData, ILegacyLaunchServerData
    where TMicrosoft : TData, ILegacyLaunchMicrosoftData where TOffline : TData, ILegacyLaunchOfflineData where TWatcher : class {
public void McLaunchJava(LegacyLaunchTask<int, int> Task)
    {
        McLaunchJavaSelected = host.SelectJava(McInstanceSelected, true, Task.CreateCancellationToken(), Task.CreateSyncProgressProvider());
        if (Task.IsCanceled)
            return;
        if (McLaunchJavaSelected is null)
            throw new OperationCanceledException(); // SelectOrDownloadJava 已经会给予适当的提示
        if (host.Debug())
        {
            McLaunchLog($"预期 Java 版本范围：{host.JavaRequirement(McInstanceSelected)}");
            McLaunchLog($"Java 版本设置类别：{Setting<int>("VersionArgumentJavaV2", McInstanceSelected)}");
        }
        McLaunchLog("选择的 Java：" + McLaunchJavaSelected.ToString());
    }


    private int? launchRamMiB;

    public void McLaunchArgumentMain(LegacyLaunchTask<string, int> Loader)
    {
        launchRamMiB = null;
        // 获取基础参数
        var Arguments = new List<string>();
        McLaunchLog("参数获取开始");
        Arguments.AddRange(McLaunchArgumentsJVM()); // JVM 参数
        Arguments.Add((string)McInstanceSelected.JsonObject["mainClass"]); // mainClass
        Arguments.AddRange(McLaunchArgumentsGame()); // 游戏参数
        // 替换
        McLaunchLog("=================== 启动参数开始 ===================");
        var Replacements = McLaunchArgumentsReplace(Arguments).ToList();
        string Argument = "";
        foreach (var OriginalArg in Arguments)
        {
            var Arg = OriginalArg;
            // 应用替换标记
            foreach (var Replacement in Replacements)
                Arg = Arg.Replace(Replacement.Key, Replacement.Value);
            // 为带特殊字符的参数添加双引号
            if (Arg.IndexOfAny(new[] { '&', '|', '<', '>', '^', ' ' }) >= 0 && !Arg.StartsWithF("\"") && !Arg.EndsWithF("\""))
            {
                Arg = $"\"{Arg.Replace("\"", @"\""")}\""; // 转义在原本参数中间的双引号
            }
            // 输出
            McLaunchLog(Arg);
            Argument += Arg + " ";
        }
        McLaunchLog("=================== 启动参数结束 ===================");
        // 输出
        McLaunchArgument = Argument.TrimEnd();
    }


public List<string> McLaunchArgumentsJVM()
    {
        var Args = new List<string>();

        // Minecraft 参数
        if (McInstanceSelected.JsonObject["arguments"] is not null && McInstanceSelected.JsonObject["arguments"]["jvm"] is not null)
        {
            // 新版 MC：从版本 JSON 获取参数
            var CurrentInstance = McInstanceSelected;
        NextInstance:
            ;

            McLaunchLog($"新版 JVM 参数：从版本 JSON 获取（{CurrentInstance.Name}）");
            if (CurrentInstance.JsonObject["arguments"] is not null && CurrentInstance.JsonObject["arguments"]["jvm"] is not null)
            {
                foreach (JToken SubJson in CurrentInstance.JsonObject["arguments"]["jvm"])
                {
                    switch (SubJson.Type)
                    {
                        case JTokenType.String:
                            {
                                Args.Add(SubJson.ToString().Trim());
                                break;
                            }
                        case JTokenType.Object:
                            {
                                JObject Argument = (JObject)SubJson;
                                if (Argument.ContainsKey("rules") && !host.JsonRuleCheck(Argument["rules"]))
                                    continue; // 不满足准则
                                if (!Argument.ContainsKey("value"))
                                    continue; // 没有 value 字段
                                if (Argument["value"].Type == JTokenType.String)
                                {
                                    Args.Add(Argument["value"].ToString().Trim());
                                }
                                else
                                {
                                    foreach (JToken value in Argument["value"])
                                        Args.Add(value.ToString().Trim());
                                }

                                break;
                            }
                    }
                }
            }
            if (!string.IsNullOrEmpty(CurrentInstance.InheritName))
            {
                CurrentInstance = host.CreateInstance(CurrentInstance.InheritName);
                goto NextInstance;
            }
            // 1.17.1 中有个 "-Dos.name=Windows 10"，不立即加双引号会导致 10 被隔开
            Args = Args.Select(a => a.Contains(" ") && !a.StartsWithF("\"") && !a.EndsWithF("\"") ? $"\"{a.Replace("\"", @"\""")}\"" : a).ToList();
        }
        else
        {
            // 旧版 MC：直接添加参数
            McLaunchLog($"旧版 JVM 参数");
            Args.Add("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump");
            Args.Add("-Djava.library.path=${natives_directory}");
            Args.Add("-cp");
            Args.Add("${classpath}");
        }

        // 设置里自定义的参数
        string CustomArg = Setting<string>("VersionAdvanceJvm", Instance: McInstanceSelected);
        if (string.IsNullOrEmpty(CustomArg))
            CustomArg = Setting<string>("LaunchAdvanceJvm");
        if (!string.IsNullOrEmpty(CustomArg))
            CustomArg = ArgumentReplace(CustomArg);

        // 重新分割字符串并去重
        Args = DeduplicateJavaArguments(SplitJavaArguments(Args.Join(' ').Replace("McEmu= ", "McEmu=") + " " + CustomArg).ToList(), true);

        // 统一通行证
        if (McLoginLoader.Output.Type == "Nide")
        {
            Args.Add(@"-javaagent:""${pure_directory}\nide8auth.jar""=""" + Setting<string>("VersionServerNide", Instance: McInstanceSelected) + "\"");
        }
        // Authlib-Injector
        if (McLoginLoader.Output.Type == "Auth")
        {
            if (McLaunchJavaSelected.Version.Major >= 6)
                Args.Add("-Djavax.net.ssl.trustStoreType=WINDOWS-ROOT"); // 信任系统根证书（5252#）
            if (McLoginLoader.Input is not ILegacyLaunchServerData { BaseUrl: { } endpoint } ||
                !endpoint.EndsWith(AuthlibEndpoint.AuthPath, StringComparison.Ordinal) || !AuthlibEndpoint.TryParse(endpoint, out _))
                throw new InvalidOperationException("缺少此次登录的 Authlib-Injector 认证地址，请重新登录后启动。");
            string Server = endpoint[..^AuthlibEndpoint.AuthPath.Length];
            try
            {
                McLaunchLog($"开始 Authlib Injector Prefetch");
                string Response = SendRequest(Server, Encoding: Encoding.UTF8);
                Args.Add(@"-javaagent:""${pure_directory}\authlib-injector.jar""=""" + Server + "\"");
                Args.Add("-Dauthlibinjector.side=client");
                Args.Add("-Dauthlibinjector.yggdrasil.prefetched=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Response)));
            }
            catch (Exception ex)
            {
                throw new Exception("无法连接到第三方登录服务器（" + (Server ?? null) + "）", ex);
            }
        }

        // JLW
        // 非 GBK 编码下命令行参数乱码的问题（JDK-8272352）
        bool UseJLW = !Setting<bool>("LaunchAdvanceDisableJLW") && !Setting<bool>("VersionAdvanceDisableJLW", McInstanceSelected) && !host.IsGbkEncoding(); // 即使游戏文件夹名称不含非 ASCII 字符，但玩家名等也可能出现中文，因此依然需要 JLW（#8739）
        if (UseJLW && CustomArg.Contains("-javaagent"))
        {
            UseJLW = false;
            McLaunchLog("由于使用了自定义 javaagent，已禁用 JLW，这可能导致游戏崩溃！", LogLevel.Warn);
        }
        if (UseJLW)
        {
            if (McLaunchJavaSelected.Version.Major >= 9)
            {
                Args.Add("--add-exports");
                Args.Add("cpw.mods.bootstraplauncher/cpw.mods.bootstraplauncher=ALL-UNNAMED");
            }
            Args.Add("-Doolloo.jlw.tmpdir=${pure_directory}"); // 这里需要不以 \ 结尾
        }
        McLaunchLog("使用 JLW：" + UseJLW);

        // LUA
        bool UseLUA = !Setting<bool>("LaunchAdvanceDisableLUA") && !Setting<bool>("VersionAdvanceDisableLUA", McInstanceSelected) && host.Libraries(McInstanceSelected, false).Select(e => e.OriginalName).Contains("org.lwjgl:lwjgl:3.4.1");
        if (UseLUA)
            Args.Add($"-javaagent:\"{LaunchUtils.ExtractPatch(@"Launch\LwjglUnsafeAgent.jar", host.PureFolder())}\"");
        McLaunchLog("使用 LUA：" + UseLUA);

        var gameVersion = McInstanceSelected.Version;
        if (CurrentLaunchOptions?.SaveBatch is null && gameVersion.Reliable &&
            gameVersion.ModLoaderVersions.Any(loader =>
                ModKeybindPatch.Applies(loader.Key, gameVersion.VanillaName, loader.Value, McLaunchJavaSelected.Version.Major)))
        {
            try
            {
                string agent = ModKeybindPatch.StageLegacy(host.PureFolder());
                Args.Add($"\"{ModKeybindPatch.Argument(agent, McInstanceSelected.PathIndie)}\"");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Logger.Warn(error, "未能应用模组键位保护");
            }
        }

        #region 内存管理

        McLaunchLog("当前剩余内存：" + Math.Round(host.AvailablePhysicalMemory() / 1024d / 1024d / 1024d * 10d) / 10 + "G");
        int TargetRam = Conversions.ToInteger(Math.Floor(host.RamGb(McInstanceSelected) * 1024d));
        launchRamMiB = TargetRam;
        Args.Add("-Xmx" + TargetRam + "m");

        // 获取设置的管理方式
        // 0：在 Java 21+ 使用分代 ZGC，20~15 使用非分代的 ZGC，14- 使用 G1GC
        // 1：在 Java 21+ 使用分代 ZGC，20- 使用 G1GC
        // 2：仅 G1GC
        // 3：不指定，玩家自定义
        int SetupType = Setting<int>("LaunchAdvanceGC");
        if (McInstanceSelected is not null && Setting<int>("VersionAdvanceGC", McInstanceSelected) > 0)
            SetupType = Setting<int>("VersionAdvanceGC", McInstanceSelected) - 1; // 去掉默认
        if (SetupType != 3)
        {
            // 确定是使用 G1GC 还是 ZGC
            bool UseG1GC = false;
            if (SetupType == 0 && McLaunchJavaSelected.Version.Major < 15 || SetupType == 1 && McLaunchJavaSelected.Version.Major < 21 || SetupType == 2 || SetupType == 4)
                UseG1GC = true; // 检查 Java 版本，ZGC 在 Java 15+ 可用，分代 ZGC 在 Java 21+ 可用
            if (Environment.OSVersion.Version.Major < 10 || Environment.OSVersion.Version.Build < 17763)
                UseG1GC = true; // 检查系统兼容性，ZGC 需要 Windows 10 1809+
            McLaunchLog($"GC 设置：{SetupType}，选取 {(UseG1GC ? "G1GC" : "ZGC")}，Java 版本：{McLaunchJavaSelected.Version.Major}");
            // 移除已有的 GC 参数
            Args = Args.Where(a => !a.RegexCheck(@"( )*-XX:[+-]?(Use\w+GC|ZGenerational|UseCompactObjectHeaders|G1\w+Percent|G1\w+Size|(Max|Min)(GCPauseMillis|HeapFreeRatio))")).ToList();
            // 添加 GC 参数
            Args.Add("-XX:+UnlockExperimentalVMOptions");
            if (McLaunchJavaSelected.Version.Major >= 24)
                Args.Add("-XX:+UseCompactObjectHeaders");
            if (UseG1GC && SetupType == 4)
            {
                // 优化的 G1GC
                Args.Add("-XX:+UseG1GC");
                Args.Add("-XX:G1NewSizePercent=20");
                Args.Add("-XX:G1ReservePercent=20");
                Args.Add("-XX:G1HeapRegionSize=32M");
                Args.Add("-XX:MaxGCPauseMillis=50");
                if (!host.Debug())
                    Args.Add("-XX:+PerfDisableSharedMem");
                if (McLaunchJavaSelected.Version.Major <= 7)
                    Args.Add("-XX:MaxPermSize=512m"); // #8286
                if (McLaunchJavaSelected.Version.Major == 8)
                    Args.Add("-XX:+ParallelRefProcEnabled");
                if (McLaunchJavaSelected.Version.Major >= 12)
                    Args.Add("-XX:MinHeapFreeRatio=25"); // 需要比 G1NewSizePercent 大一点
                if (McLaunchJavaSelected.Version.Major >= 12)
                    Args.Add("-XX:MaxHeapFreeRatio=40"); // 需要比 MinHeapFreeRatio 大一些
            }
            else if (UseG1GC)
            {
                // Mojang G1GC
                Args.Add("-XX:+UseG1GC");
                Args.Add("-XX:G1NewSizePercent=20");
                Args.Add("-XX:G1ReservePercent=20");
                Args.Add("-XX:G1HeapRegionSize=32M");
                Args.Add("-XX:MaxGCPauseMillis=50");
                if (McLaunchJavaSelected.Version.Major <= 7)
                    Args.Add("-XX:MaxPermSize=512m"); // #8286
            }
            else
            {
                // ZGC
                Args.Add("-XX:+UseZGC");
                if (McLaunchJavaSelected.Version.Major == 21 || McLaunchJavaSelected.Version.Major == 22)
                    Args.Add("-XX:+ZGenerational");
            } // Java 23 起默认启用分代 ZGC，不需要再添加参数
        }

        Args.Remove("-XX:MaxDirectMemorySize=256M"); // #3511 的清理

        #endregion

        // Log4j 漏洞防御参数
        Args.Add("-Dlog4j2.formatMsgNoLookups=true");

        // 编码（#4700、#5892、#5909、#6934、#8950、#9241）
        bool UseCompatEncoding = McLaunchJavaSelected.Version.Major >= 18;
        string EncodingName = UseCompatEncoding ? McLaunchJavaSelected.NativeEncodingName : McLaunchJavaSelected.FileEncodingName; // Java 18+ 会添加 COMPAT，因此 Java 会回退到 native.encoding
                                                                                                                                   // Java 17- 直接使用 file.encoding
        try
        {
            McLaunchEncoding = LegacyEncoding(EncodingName.Equals("GBK", StringComparison.OrdinalIgnoreCase) ? "GB18030" : EncodingName);
        }
        catch (Exception)
        {
            var FallbackEncoding = LegacyEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage); // 老版本 Java 的编码选择行为
            McLaunchLog($"无法识别 Java 编码 {EncodingName ?? "Nothing"}，已回退为 {FallbackEncoding.WebName}", LogLevel.Warn);
            McLaunchEncoding = FallbackEncoding;
        }
        McLaunchLog($"使用编码：{McLaunchEncoding.WebName}");
        string LaunchEncodingName = McLaunchEncoding.CodePage == 54936 ? "GBK" : McLaunchEncoding.WebName;
        string StdoutArgName = McLaunchJavaSelected.Version.Major < 19 ? "sun.stdout.encoding" : "stdout.encoding"; // 老版本 Java 应该设置 sun.std
        string StderrArgName = McLaunchJavaSelected.Version.Major < 19 ? "sun.stderr.encoding" : "stderr.encoding";
        if (!Args.Any(a => a.StartsWithF($"-D{StdoutArgName}=")))
            Args.Add($"-D{StdoutArgName}={LaunchEncodingName}");
        if (!Args.Any(a => a.StartsWithF($"-D{StderrArgName}=")))
            Args.Add($"-D{StderrArgName}={LaunchEncodingName}");
        if (UseCompatEncoding && !Args.Any(a => a.StartsWithF("-Dfile.encoding=")))
            Args.Add("-Dfile.encoding=COMPAT"); // 防止老版本 MC 与部分 Mod 不兼容 UTF-8

        // 为 JLW 添加 -jar，它必须放在最后
        if (UseJLW)
        {
            Args.Add("-jar");
            Args.Add(LaunchUtils.ExtractPatch(@"Launch\JavaWrapper.jar", host.PureFolder()));
        }

        // 再次去重并输出
        return DeduplicateJavaArguments(Args, true);
    }


public List<string> McLaunchArgumentsGame()
    {
        string Arg = "";

        // Minecraft 参数
        // 旧版 MC：直接添加参数
        if (McInstanceSelected.JsonObject["minecraftArguments"]?.ToString().Any() == true)
        {
            string BasicString = McInstanceSelected.JsonObject["minecraftArguments"].ToString();
            McLaunchLog($"旧版游戏参数：" + BasicString);
            BasicString += " --height ${resolution_height} --width ${resolution_width}"; // 总是添加宽高选项，之后去重的时候会覆盖 MC 自带的
            Arg += " " + BasicString;
        }
        // 新版 MC：从版本 JSON 获取参数
        if (McInstanceSelected.JsonObject["arguments"] is not null && McInstanceSelected.JsonObject["arguments"]["game"] is not null)
        {
            var CurrentInstance = McInstanceSelected;
            void AppendArg(string a) => Arg += " " + (a.Contains(" ") && !a.StartsWithF("\"") && !a.EndsWithF("\"") ? $"\"{a.Replace("\"", @"\""")}\"" : a);
        NextInstance:
            ;

            McLaunchLog($"新版游戏参数：从版本 JSON 获取（{CurrentInstance.Name}）");
            if (CurrentInstance.JsonObject["arguments"] is not null && CurrentInstance.JsonObject["arguments"]["game"] is not null)
            {
                foreach (JToken SubJson in CurrentInstance.JsonObject["arguments"]["game"])
                {
                    switch (SubJson.Type)
                    {
                        case JTokenType.String:
                            {
                                AppendArg(SubJson.ToString());
                                break;
                            }
                        case JTokenType.Object:
                            {
                                JObject Argument = (JObject)SubJson;
                                if (Argument.ContainsKey("rules") && !host.JsonRuleCheck(Argument["rules"]))
                                    continue; // 不满足准则
                                if (!Argument.ContainsKey("value"))
                                    continue; // 没有 value 字段
                                if (Argument["value"].Type == JTokenType.String)
                                {
                                    AppendArg(Argument["value"].ToString());
                                }
                                else
                                {
                                    foreach (JToken value in Argument["value"])
                                        AppendArg(value.ToString());
                                }

                                break;
                            }
                    }
                }
            }
            if (!string.IsNullOrEmpty(CurrentInstance.InheritName))
            {
                CurrentInstance = host.CreateInstance(CurrentInstance.InheritName);
                goto NextInstance;
            }
        }

        // 设置里自定义的参数
        string CustomArg = Setting<string>("VersionAdvanceGame", Instance: McInstanceSelected);
        if (string.IsNullOrEmpty(CustomArg))
            CustomArg = Setting<string>("LaunchAdvanceGame");
        if (!string.IsNullOrEmpty(CustomArg))
            CustomArg = ArgumentReplace(CustomArg);
        Arg += " " + CustomArg;

        // 把 OptiFineForgeTweaker 放在参数的末尾
        if ((McInstanceSelected.Version.ModLoaders.HasFlagF(ModLoaders.Forge) || McInstanceSelected.Version.ModLoaders.HasFlagF(ModLoaders.LiteLoader)) && McInstanceSelected.Version.HasOptiFine && !McInstanceSelected.Version.OptiFineAsMod)
        {
            if (Arg.Contains("--tweakClass optifine.OptiFineForgeTweaker"))
            {
                McLaunchLog("发现正确的 OptiFineForge TweakClass，目前的游戏参数：" + Arg);
                Arg = Arg.Replace(" --tweakClass optifine.OptiFineForgeTweaker", "") + " --tweakClass optifine.OptiFineForgeTweaker";
            }
            if (Arg.Contains("--tweakClass optifine.OptiFineTweaker"))
            {
                McLaunchLog("发现错误的 OptiFineForge TweakClass，目前的游戏参数：" + Arg);
                Arg = Arg.Replace(" --tweakClass optifine.OptiFineTweaker", "") + " --tweakClass optifine.OptiFineForgeTweaker";
                try
                {
                    FileUtils.Write(McInstanceSelected.GetJsonPath(), FileUtils.ReadAsString(McInstanceSelected.GetJsonPath()).Replace("optifine.OptiFineTweaker", "optifine.OptiFineForgeTweaker"));
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "替换 OptiFineForge TweakClass 失败");
                }
            }
        }

        // 重新分割字符串并去重
        var Args = DeduplicateJavaArguments(SplitJavaArguments(Arg).ToList(), false);

        // 额外传入的参数
        Args.AddRange(CurrentLaunchOptions.ExtraGameArgs);

        // 全屏
        if (Setting<int>("LaunchArgumentWindowType") == 0)
            Args.Add("--fullscreen");

        // 自动进入服务器
        string Server = CurrentLaunchOptions.QuickPlayWorld is not null ? "" : string.IsNullOrEmpty(CurrentLaunchOptions.ServerIp) ? Setting<string>("VersionServerEnter", McInstanceSelected) : CurrentLaunchOptions.ServerIp;
        if (!string.IsNullOrEmpty(Server))
        {
            if (McInstanceSelected.ReleaseTime > new DateTime(2023, 4, 4))
            {
                // QuickPlay
                Args.Add($"--quickPlayMultiplayer");
                Args.Add(Server);
            }
            else if (LegacyServerAddress.TryParse(Server, out string hostName, out int port))
            {
                Args.Add("--server");
                Args.Add(hostName);
                Args.Add("--port");
                Args.Add(port.ToString(CultureInfo.InvariantCulture));
                if (McInstanceSelected.Version.HasOptiFine)
                    Hint("OptiFine 与自动进入服务器可能不兼容，有概率导致材质丢失甚至游戏崩溃！", LegacyLaunchHint.Red);
            }
            else Hint("自动进入服务器的地址无效，已跳过自动进服。", LegacyLaunchHint.Red);
        }

        CurrentLaunchOptions.QuickPlay = host.PrepareQuickPlay(McInstanceSelected, CurrentLaunchOptions.QuickPlayWorld, Args, CurrentLaunchOptions.SaveBatch is not null);
        // 再次去重并输出
        return DeduplicateJavaArguments(Args, false);
    }


public IEnumerable<(string Key, string Value)> McLaunchArgumentsReplace(List<string> Arguments)
    {
        // 基础参数
        yield return ("${classpath_separator}", ";");
        yield return ("${natives_directory}", PathUtils.RemoveSlashSuffix(GetNativesFolder()));
        yield return ("${library_directory}", PathUtils.ToShortPath(host.SelectedFolder() + "libraries"));
        yield return ("${libraries_directory}", PathUtils.ToShortPath(host.SelectedFolder() + "libraries"));
        yield return ("${pure_directory}", PathUtils.RemoveSlashSuffix(host.PureFolder())); // 由 PCL 添加，这会允许在分割参数并去重后再替换路径，防止路径中的特殊字符影响参数分割和去重
        yield return ("${launcher_name}", "PCL");
        yield return ("${launcher_version}", host.VersionCode());
        yield return ("${version_name}", McInstanceSelected.Name);
        yield return ("${game_directory}", PathUtils.ToShortPath(Strings.Left(McInstanceSelected.PathIndie, McInstanceSelected.PathIndie.Count() - 1)));
        yield return ("${assets_root}", PathUtils.ToShortPath(host.SelectedFolder() + "assets"));
        yield return ("${user_properties}", "{}");
        yield return ("${auth_player_name}", McLoginLoader.Output.Name);
        yield return ("${auth_uuid}", McLoginLoader.Output.Uuid);
        yield return ("${auth_access_token}", McLoginLoader.Output.AccessToken);
        yield return ("${access_token}", McLoginLoader.Output.AccessToken);
        yield return ("${auth_session}", McLoginLoader.Output.AccessToken);
        yield return ("${user_type}", "msa"); // #1221
        yield return ("${primary_jar}", PathUtils.ToShortPath(McInstanceSelected.PathVersion + McInstanceSelected.Name + ".jar")); // #6942
        yield return ("${game_assets}", PathUtils.ToShortPath(host.SelectedFolder() + @"assets\virtual\legacy")); // 1.5.2 的 pre-1.6 资源索引应与 legacy 合并
        yield return ("${assets_index_name}", host.AssetsIndexName(McInstanceSelected));

        // 自定义信息
        string ArgumentInfo;
        ArgumentInfo = Setting<string>("VersionArgumentInfo", Instance: McInstanceSelected);
        if (string.IsNullOrEmpty(ArgumentInfo))
            ArgumentInfo = Setting<string>("LaunchArgumentInfo");
        if (string.IsNullOrEmpty(ArgumentInfo)) // 若自定义信息为空，则必须从参数中去掉该部分，不然 MC 会显示 ${version_type} 或者一个空字符串
        {
            if (Arguments.Contains("--versionType"))
            {
                int Index = Arguments.IndexOf("--versionType");
                Arguments.RemoveAt(Index);
                if (Index < Arguments.Count)
                    Arguments.RemoveAt(Index);
            }
            yield return ("${version_type}", "\"\"");
        }
        else
        {
            yield return ("${version_type}", ArgumentReplace(ArgumentInfo));
        }

        // 窗口尺寸
        LegacyLaunchSize TargetSize;
        switch (Setting<int>("LaunchArgumentWindowType"))
        {
            case 2: // 与启动器尺寸一致
                {
                    var Result = default(LegacyLaunchSize);
                    host.DispatchWait(() => Result = host.LauncherSize());
                    TargetSize = Result;
                    TargetSize.Height -= 29.5d * host.Dpi() / 96d; // 标题栏高度
                    break;
                }
            case 3: // 自定义
                {
                    TargetSize = new LegacyLaunchSize(Math.Max(100, Setting<int>("LaunchArgumentWindowWidth")), Math.Max(100, Setting<int>("LaunchArgumentWindowHeight")));
                    break;
                }

            default:
                {
                    TargetSize = new LegacyLaunchSize(854, 480);
                    break;
                }
        }
        if (McInstanceSelected.Version.Vanilla.Major <= 12 && McLaunchJavaSelected.Version >= new Version(8, 0, 200) && McLaunchJavaSelected.Version <= new Version(8, 0, 321) && !McInstanceSelected.Version.HasOptiFine && !McInstanceSelected.Version.ModLoaders.HasFlagF(ModLoaders.Forge)) // 修复 1.12.2- 下 JRE 8u200~321 下窗口大小为设置大小的 DPI% 倍（#3463）
        {
            McLaunchLog($"已应用窗口大小过大修复（Java 版本：{McLaunchJavaSelected.Version}）");
            TargetSize.Width /= host.Dpi() / 96d;
            TargetSize.Height /= host.Dpi() / 96d;
        }
        yield return ("${resolution_width}", Conversions.ToString(Math.Round(TargetSize.Width)));
        yield return ("${resolution_height}", Conversions.ToString(Math.Round(TargetSize.Height)));

        // 支持库
        var Cps = new List<string>();
        string OptiFineCp = null;
        foreach (LegacyMinecraftLibrary Library in host.Libraries(McInstanceSelected, true))
        {
            if (Library.IsNatives)
                continue;
            if (Library.Name is not null && Library.Name == "optifine:OptiFine")
            {
                OptiFineCp = Library.LocalPath;
            }
            else
            {
                Cps.Add(Library.LocalPath);
            }
        }
        if (OptiFineCp is not null)
            Cps.Insert(Cps.Count - 2, OptiFineCp); // OptiFine 的总是需要放到倒数第二位
        yield return ("${classpath}", Cps.Select(c => PathUtils.ToShortPath(c)).Join(';'));
    }


public IEnumerable<string> SplitJavaArguments(string Arg)
    {
        Arg = Arg.ReplaceLineEndings(" ", mergeMultiple: true).Trim(); // 预处理换行符
        if (string.IsNullOrWhiteSpace(Arg))
            yield break;
        var Current = new StringBuilder();
        bool InQuotes = false;
        int i = 0;
        while (i < Arg.Length)
        {
            char c = Arg[i];
            if (c == '\\' && i + 1 < Arg.Length && Arg[i + 1] == '"') // 检测 \"
            {
                // 转义的双引号：保留反斜杠和引号，不切换 InQuotes
                Current.Append(@"\""");
                i += 1; // 额外跳过下一个字符（双引号）
            }
            else if (c == '"')
            {
                // 未转义的双引号：切换 InQuotes
                InQuotes = !InQuotes;
                Current.Append(c);
            }
            else if (c == ' ' && !InQuotes)
            {
                // 不在双引号内的空格：一个参数结束
                if (Current.Length > 0)
                {
                    yield return Current.ToString();
                    Current.Clear();
                }
            }
            else
            {
                // 普通字符，直接添加
                Current.Append(c);
            }
            i += 1;
        }
        if (Current.Length > 0)
            yield return Current.ToString(); // 添加剩余的
    }


public List<string> DeduplicateJavaArguments(List<string> Args, bool IsJVMArgs)
    {
        var Result = new List<string>();
        int i = 0;
        while (i < Args.Count)
        {
            string Key = Args[i];
            if (!Key.StartsWithF("-") || i + 1 >= Args.Count || Args[i + 1].StartsWithF("-") && Conversion.Val(Args[i + 1].AfterFirst("-")) == 0d) // 避免 -xPos 23 -xPos -50 中后面的 -50 被识别为参数
            {
                // 单参数
                i += 1;
                if (Result.Contains(Key))
                {
                    McLaunchLog($"已去重单个{(IsJVMArgs ? " JVM " : "游戏")}参数：{Key}");
                    continue;
                }
                Result.Add(Key);
            }
            else
            {
                // 以空格间隔的键值对（例如 --width 233）
                string Value = Args[i + 1];
                i += 2;
                bool continueWhile = false;
                for (int j = 0, loopTo = Result.Count - 2; j <= loopTo; j++) // 向前寻找相同的键
                {
                    if ((Result[j] ?? "") != (Key ?? ""))
                        continue;
                    // 仅当是游戏参数且不是 tweakClass 时，用新的值覆盖旧的值
                    // 据测试，如果包含多个相同 --width 或 --uuid 等参数，两个都会直接失效，如果这发生在关键参数上会导致崩溃
                    if (!IsJVMArgs && Key != "--tweakClass")
                    {
                        McLaunchLog($"已覆盖重复的{(IsJVMArgs ? " JVM " : "游戏")}参数：{Key} {Result[j + 1]} → {Key} {Value}");
                        Result[j + 1] = Value;
                        continueWhile = true;
                        break;
                    }
                    // 键和值完全相同，直接抛弃
                    if ((Result[j + 1] ?? "") == (Value ?? ""))
                    {
                        McLaunchLog($"已去重{(IsJVMArgs ? " JVM " : "游戏")}参数对：{Key} {Value}");
                        continueWhile = true;
                        break;
                    }
                }

                if (continueWhile)
                {
                    continue;
                }
                Result.Add(Key);
                Result.Add(Value);
            }
        }
        return Result;
    }

}
