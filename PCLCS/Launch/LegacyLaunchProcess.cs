using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLCS;

public sealed partial class LegacyLaunchEngine<TInstance, TState, TCard, TOptions, TData, TServer, TMicrosoft, TOffline, TWatcher>
    where TInstance : LegacyMcInstance<TInstance, TState, TCard>
    where TState : struct, Enum where TCard : struct, Enum where TOptions : class, ILegacyLaunchOptions<TInstance>, new()
    where TData : class, ILegacyLaunchLoginData where TServer : TData, ILegacyLaunchServerData
    where TMicrosoft : TData, ILegacyLaunchMicrosoftData where TOffline : TData, ILegacyLaunchOfflineData where TWatcher : class {
    public void McLaunchCustom(LegacyLaunchTask<int, int> loader) {
        string global = Setting<string>("LaunchAdvanceRun");
        if (!string.IsNullOrEmpty(global)) global = ArgumentReplace(global);
        string version = Setting<string>("VersionAdvanceRun", McInstanceSelected);
        if (!string.IsNullOrEmpty(version)) version = ArgumentReplace(version);
        try {
            string command = $"chcp {(McLaunchEncoding.CodePage == 54936 && host.IsGbkEncoding() ? 936 : McLaunchEncoding.CodePage)}>nul\r\n" +
                "@echo off\r\n" + $"title 启动 - {McInstanceSelected.Name}\r\n" + "echo 游戏正在启动，请稍候。\r\n" +
                $"cd /D \"{PathUtils.ToShortPath(McInstanceSelected.PathIndie)}\"\r\n" + global + "\r\n" + version + "\r\n" +
                $"\"{McLaunchJavaSelected.JavaExePath}\" {McLaunchArgument}\r\n" + "echo 游戏已退出。\r\n" + "pause";
            FileUtils.Write(CurrentLaunchOptions.SaveBatch ?? Paths.Base + "PCL\\LatestLaunch.bat",
                host.FilterAccessToken(command, 'F').Replace("%", "%%"), encoding: McLaunchEncoding);
            if (CurrentLaunchOptions.SaveBatch != null) {
                McLaunchLog("导出启动脚本完成，强制结束启动过程"); CanceledHint = "导出启动脚本成功！";
                host.OpenExplorer(CurrentLaunchOptions.SaveBatch); loader.CancelParent(); return;
            }
        } catch (Exception ex) { Logger.Warn(ex, "输出启动脚本失败"); if (CurrentLaunchOptions.SaveBatch != null) throw; }
        if (!string.IsNullOrEmpty(global)) RunCustom(global, false, loader);
        if (!string.IsNullOrEmpty(version)) RunCustom(version, true, loader);
    }
    private void RunCustom(string command, bool version, LegacyLaunchTask<int, int> loader) {
        McLaunchLog((version ? "正在执行版本自定义命令：" : "正在执行全局自定义命令：") + command);
        Process? process = null;
        try {
            process = host.StartProcess(new ProcessStartInfo {
                FileName = "cmd.exe", Arguments = $"/c \"{command}\"", WorkingDirectory = host.SelectedFolder(),
                UseShellExecute = false, CreateNoWindow = true
            });
            bool wait = version ? Setting<bool>("VersionAdvanceRunWait", McInstanceSelected) : Setting<bool>("LaunchAdvanceRunWait");
            if (wait) while (!process.HasExited && !loader.IsCanceled) Thread.Sleep(10);
        } catch (Exception ex) { Logger.Error(ex, version ? "执行版本自定义命令失败" : "执行全局自定义命令失败", LogBehavior.Toast); }
        finally {
            if (process != null && !process.HasExited && loader.IsCanceled) {
                McLaunchLog("由于取消启动，已强制结束自定义命令 CMD 进程"); process.Kill();
            }
        }
    }

    public void McLaunchRun(LegacyLaunchTask<int, Process> loader) {
        CurrentLaunchOptions.EnvironmentPoint = null!;
        if (!host.HasRunningMinecraft()) {
            try { CurrentLaunchOptions.EnvironmentPoint = host.CaptureEnvironment(McInstanceSelected, "游戏启动前", loader.CreateCancellationToken()); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Logger.Warn(ex, "创建环境还原点失败，本次启动不标记为可恢复环境"); }
        }
        ProcessStartInfo start = new(McLaunchJavaSelected.JavaExePath);
        string? pathEnv = start.EnvironmentVariables["Path"];
        List<string> paths = [];
        if (!string.IsNullOrEmpty(pathEnv)) paths.AddRange(pathEnv!.Split(';'));
        paths.Add(PathUtils.ToShortPath(McLaunchJavaSelected.Folder));
        start.EnvironmentVariables["Path"] = paths.Distinct().Join(';');
        start.EnvironmentVariables["appdata"] = PathUtils.ToShortPath(host.SelectedFolder());
        start.WorkingDirectory = McInstanceSelected.PathIndie; start.UseShellExecute = false;
        start.StandardOutputEncoding = McLaunchEncoding; start.StandardErrorEncoding = McLaunchEncoding;
        start.RedirectStandardOutput = true; start.RedirectStandardError = true; start.CreateNoWindow = true; start.Arguments = McLaunchArgument;
        if (host.WorldBackupsBusy() || host.ClonerUiBusy() || host.HistoryUiBusy()) throw new OperationCanceledException("正在备份或修改游戏环境，请稍后再启动。");
        CancellationToken token = loader.CreateCancellationToken();
        host.RecoverWorlds(McInstanceSelected.PathIndie, token);
        using var localSkin = host.PrepareLocalSkin?.Invoke(McLoginLoader.Output, McLaunchJavaSelected, start, token);
        LegacyJavaLaunchIdentity? sessionIdentity = LegacyJavaLaunchIdentity.Prepare(start);
        token.ThrowIfCancellationRequested();
        Process game = host.StartProcess(start); McLaunchLog("已启动游戏进程：" + McLaunchJavaSelected.JavaExePath);
        sessionIdentity?.Bind(game);
        if (loader.IsCanceled) { McLaunchLog("由于取消启动，已强制结束游戏进程"); game.Kill(); return; }
        try { localSkin?.Attach(game, token); }
        catch {
            try { if (!game.HasExited) game.Kill(); }
            finally { game.Dispose(); }
            throw;
        }
        loader.Output = game; host.StoreProcess(game);
        try {
            game.PriorityBoostEnabled = true;
            switch (Setting<int>("LaunchArgumentPriority")) {
                case 0: game.PriorityClass = ProcessPriorityClass.AboveNormal; break;
                case 2: game.PriorityClass = ProcessPriorityClass.BelowNormal; break;
            }
        } catch (Exception ex) { Logger.Error(ex, "设置进程优先级失败"); }
    }

    public void McLaunchWait(LegacyLaunchTask<Process, int> loader) {
        string title = Setting<string>("VersionArgumentTitle", McInstanceSelected);
        if (string.IsNullOrEmpty(title)) title = Setting<string>("LaunchArgumentTitle"); title = ArgumentReplace(title, ReplaceTime: false);
        PersonalOptionsSessionData personal = CurrentLaunchOptions.PersonalOptions;
        QuickPlaySessionData quick = CurrentLaunchOptions.QuickPlay; TInstance launchInstance = McInstanceSelected;
        TWatcher watcher = host.CreateWatcher(loader, McInstanceSelected, title, game => {
            try { host.TrackWorlds(launchInstance, game); } catch (Exception ex) { Logger.Warn(ex, "记录存档备份会话失败"); }
            try { host.TrackPersonal(personal, game); } catch (Exception ex) { Logger.Warn(ex, "记录个人设置同步会话失败"); }
            try { host.TrackQuickPlay(quick, game); } catch (Exception ex) { Logger.Warn(ex, "记录 Quick Play 会话失败"); }
        }, () => {
            try { host.FinishPersonal(personal); } catch (Exception ex) { Logger.Warn(ex, "保存个人游戏设置失败"); }
            try { host.FinishQuickPlay(quick); } catch (Exception ex) { Logger.Warn(ex, "保存最近存档记录失败"); }
            host.Dispatch(host.RefreshRecentWorlds); host.Dispatch(host.RefreshKeybindings);
        }, CurrentLaunchOptions.EnvironmentPoint);
        host.StoreWatcher(watcher);
        // Attach stdout and begin probing before gathering the diagnostic header.
        McLaunchLog(""); McLaunchLog("~ 基础参数 ~");
        McLaunchLog("PCL 版本：" + host.VersionDisplay() + " (" + host.VersionCode() + ")");
        McLaunchLog($"游戏版本：{McInstanceSelected.VersionDisplayName()}（{McInstanceSelected.Version.Vanilla}，Drop {McInstanceSelected.Version.Drop}{(McInstanceSelected.Version.Reliable ? "" : "，无法完全确定")}）");
        McLaunchLog("资源版本：" + host.AssetsIndexName(McInstanceSelected));
        McLaunchLog("版本继承：" + (McInstanceSelected.InheritName == "" ? "无" : McInstanceSelected.InheritName));
        double ramMiB = launchRamMiB ?? Math.Round(host.RamGb(McInstanceSelected) * 1024);
        McLaunchLog($"分配的内存：{ramMiB / 1024:0.##} GB（{ramMiB:0} MB）");
        McLaunchLog("MC 文件夹：" + host.SelectedFolder()); McLaunchLog("版本文件夹：" + McInstanceSelected.PathVersion);
        McLaunchLog("版本隔离：" + (McInstanceSelected.PathIndie == McInstanceSelected.PathVersion)); McLaunchLog("HMCL 格式：" + McInstanceSelected.IsHmclFormatJson);
        McLaunchLog("Java 信息：" + (McLaunchJavaSelected != null ? McLaunchJavaSelected.ToString() : "无可用 Java"));
        McLaunchLog("Natives 文件夹：" + GetNativesFolder()); McLaunchLog(""); McLaunchLog("~ 登录参数 ~");
        McLaunchLog("玩家用户名：" + McLoginLoader.Output.Name); McLaunchLog("AccessToken：" + McLoginLoader.Output.AccessToken);
        McLaunchLog("ClientToken：" + McLoginLoader.Output.ClientToken); McLaunchLog("UUID：" + McLoginLoader.Output.Uuid);
        McLaunchLog("登录方式：" + McLoginLoader.Output.Type); McLaunchLog("");
        int state = host.WatcherState(watcher);
        while (state == 0) {
            if (host.WaitForWatcherStartup is { } wait) state = wait(watcher, 100);
            else {
                Thread.Sleep(100);
                state = host.WatcherState(watcher);
            }
        }
        if (state is 2 or 4) throw new OperationCanceledException();
    }

    public void McLaunchEnd() {
        McLaunchLog("开始启动结束处理");
        if (Setting<bool>("UiMusicStop")) host.Dispatch(() => { if (host.MusicPause()) Logger.Info("已根据设置，在启动后暂停音乐播放"); });
        else if (Setting<bool>("UiMusicStart")) host.Dispatch(() => { if (host.MusicResume()) Logger.Info("已根据设置，在启动后开始音乐播放"); });
        McLaunchLog("启动器可见性：" + Setting<int>("LaunchArgumentVisible"));
        switch (Setting<int>("LaunchArgumentVisible")) {
            case 0: McLaunchLog("已根据设置，在启动后关闭启动器"); host.Dispatch(host.EndProgram); break;
            case 2: case 3: McLaunchLog("已根据设置，在启动后隐藏启动器"); host.Dispatch(host.HideLauncher); break;
            case 4: McLaunchLog("已根据设置，在启动后最小化启动器"); host.Dispatch(host.MinimizeLauncher); break;
        }
        host.WriteSetting("SystemLaunchCount", checked(Setting<int>("SystemLaunchCount") + 1));
    }
}
