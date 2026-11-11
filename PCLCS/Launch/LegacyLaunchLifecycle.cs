using System;
using System.Collections.Generic;
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
    public bool McLaunchStart(TOptions? options = null) {
        CurrentLaunchOptions = options ?? new TOptions();
        if (!host.OnUiThread()) throw new Exception("McLaunchStart 必须在 UI 线程调用！");
        if (host.PackEnabled() && !host.PackAllowsInstance((CurrentLaunchOptions.Instance ?? McInstanceSelected)?.PathVersion!)) {
            Hint("请在版本选择中选择当前整合包目录里的游戏版本。", LegacyLaunchHint.Red); return false;
        }
        if (host.CleaningFiles()) { Hint("正在清理游戏文件，请等待清理结束后再启动。", LegacyLaunchHint.Red); return false; }
        if (McLaunchLoader.State == LegacyLaunchState.Loading) { Hint("已有游戏正在启动中！", LegacyLaunchHint.Red); return false; }
        if (CurrentLaunchOptions.Instance != null && (McInstanceSelected == null || !PathUtils.IsSame(McInstanceSelected.PathVersion, CurrentLaunchOptions.Instance.PathVersion))) {
            McLaunchLog("在启动前切换到版本 " + CurrentLaunchOptions.Instance.Name);
            try { CurrentLaunchOptions.Instance.GetJsonPath(); }
            catch { Hint($"无法启动 {CurrentLaunchOptions.Instance.Name}：当前 Minecraft 文件夹中尚未安装该版本！", LegacyLaunchHint.Red); return false; }
            CurrentLaunchOptions.Instance.Load();
            if (Convert.ToInt32(CurrentLaunchOptions.Instance.State) == 0) {
                Hint($"无法启动 {CurrentLaunchOptions.Instance.Name}：{CurrentLaunchOptions.Instance.Info}", LegacyLaunchHint.Red); return false;
            }
            McInstanceSelected = CurrentLaunchOptions.Instance;
            host.RefreshButtons(); host.RefreshPageAfterSwitch();
        }
        string? blocked = GetLaunchBlockReason();
        if (blocked is not null) {
            McLaunchLog("启动被阻止：" + blocked);
            Hint(blocked, LegacyLaunchHint.Red); return false;
        }
        if (McInstanceSelected != null && host.PendingChanges(McInstanceSelected.PathVersion) > 0) {
            Hint("环境修改曾被中断，请在版本设置的还原点或修改页中恢复后再启动。", LegacyLaunchHint.Red); return false;
        }
        host.AprilGiveup(); host.RemoveInstanceSelectionPage(); McLaunchLoader.Start(options!, isForceRestart: true); return true;
    }

    public string? GetLaunchBlockReason() {
        if (host.WorldBackupsUiBusy()) return "存档备份任务尚未结束，请在任务管理中查看进度或取消任务。";
        if (host.WorldBackupsBusy()) return "正在备份或恢复存档，请等待存档任务结束后再启动。";
        if (host.ClonerUiBusy()) return "测试副本任务尚未结束，请在任务管理中查看进度。";
        if (host.HistoryUiBusy()) return "环境还原点任务尚未结束，请在任务管理中查看进度。";
        if (host.ModpackUpdating()) return "整合包正在更新，请等待更新任务结束后再启动。";
        return McInstanceSelected is not null && host.InstanceLocked(McInstanceSelected)
            ? $"版本 {McInstanceSelected.Name} 正在被文件任务修改，请等待任务结束后再启动！" : null;
    }

    public void McLaunchState(LegacyLaunchTask<TOptions, object> loader) {
        host.Dispatch(host.RefreshRecentVisibility);
        switch (McLaunchLoader.State) {
            case LegacyLaunchState.Finished: case LegacyLaunchState.Failed: case LegacyLaunchState.Waiting: case LegacyLaunchState.Canceled:
                host.LoginPage(); break;
            case LegacyLaunchState.Loading: host.ClearLaunchLog(); break;
        }
    }

    public void McLaunchStart(LegacyLaunchTask<TOptions, object> loader) {
        host.DispatchWait(host.LaunchingPage);
        try { McLaunchPrecheck(); McLaunchLog("预检测已通过"); }
        catch (Exception ex) { if (!ex.IsCanceled()) Hint(ex.Message, LegacyLaunchHint.Red); throw; }
        bool isSavingBatch = CurrentLaunchOptions?.SaveBatch != null;
        try {
            LegacyLaunchStep clientFix = host.ClientFix(McInstanceSelected); clientFix.ProgressWeight = 15; clientFix.Show = false;
            List<LegacyLaunchStep> loaders = [
                new LegacyLaunchIntStep { Name = "获取 Java", Work = McLaunchJava, ProgressWeight = 4, Block = false },
                new LegacyLaunchExistingStep { Task = McLoginLoader },
                clientFix,
                new LegacyLaunchIntStep { Name = "恢复未完成的存档操作", Work = task => host.RecoverWorlds(McInstanceSelected.PathIndie, task.CreateCancellationToken()), ProgressWeight = 1 },
                new LegacyLaunchIntStep { Name = "检查模组依赖", Work = McLaunchDependencyCheck, ProgressWeight = 1, InterruptOnCancel = false },
                new LegacyLaunchStringStep { Name = "获取启动参数", Work = McLaunchArgumentMain, ProgressWeight = 2 },
                new LegacyLaunchIntStep { Name = "解压文件", Work = McLaunchNatives, ProgressWeight = 2 },
                new LegacyLaunchIntStep { Name = "预启动处理", Work = _ => McLaunchPrerun(), ProgressWeight = 1 },
                new LegacyLaunchIntStep { Name = "执行自定义命令", Work = McLaunchCustom, ProgressWeight = 1 },
                new LegacyLaunchProcessStep { Name = "启动进程", Work = McLaunchRun, ProgressWeight = 2 },
                new LegacyLaunchWaitStep { Name = "等待游戏窗口出现", Work = McLaunchWait, ProgressWeight = 1 },
                new LegacyLaunchIntStep { Name = "结束处理", Work = _ => McLaunchEnd(), ProgressWeight = 1 }
            ];
            int optimize = Setting<int>("VersionRamOptimize", McInstanceSelected);
            if ((optimize == 0 && Setting<bool>("LaunchArgumentRam")) || optimize == 1) {
                loaders[2].Block = false;
                loaders.Insert(3, new LegacyLaunchIntStep { Name = "内存优化", Work = McLaunchMemoryOptimize, ProgressWeight = 30 });
            }
            LegacyLaunchOperation launch = host.CreateCombo("Minecraft 启动", loaders);
            if (McLoginLoader.State == LegacyLaunchState.Finished) McLoginLoader.State = LegacyLaunchState.Waiting;
            host.StoreLaunchOperation(launch); CanceledHint = null!; launch.Start(); host.AddTaskbar(launch);
            while (launch.State == LegacyLaunchState.Loading) { host.RefreshLaunching(); Thread.Sleep(100); }
            host.RefreshLaunching();
            switch (launch.State) {
                case LegacyLaunchState.Finished: Hint(McInstanceSelected.Name + " 启动成功！", LegacyLaunchHint.Green); break;
                case LegacyLaunchState.Canceled:
                    if (CanceledHint == null) Hint(isSavingBatch ? "已取消导出启动脚本！" : "已取消启动！", LegacyLaunchHint.Blue);
                    else Hint(CanceledHint, LegacyLaunchHint.Green);
                    break;
                case LegacyLaunchState.Failed: throw launch.Error;
                default: throw new Exception("错误的状态改变：" + launch.State);
            }
        } catch (Exception ex) {
            ex.ThrowIfCanceled();
            Exception current = ex;
            while (true) {
                if (current.Message.StartsWithF("$")) {
                    MyMsgBox(current.Message.TrimStart('$'), isSavingBatch ? "导出启动脚本失败" : "启动失败"); throw;
                }
                if (current.InnerException != null) { current = current.InnerException; continue; }
                McLaunchLog("错误：" + ex.GetDisplay(true));
                Logger.Error(ex, isSavingBatch ? "导出启动脚本失败" : "Minecraft 启动失败", LogBehavior.Alert); throw;
            }
        }
    }

    public void McLaunchMemoryOptimize(LegacyLaunchTask<int, int> loader) {
        McLaunchLog("内存优化开始"); bool finished = false;
        host.NewThread(() => { try { host.MemoryOptimize(); } finally { Volatile.Write(ref finished, true); } }, "Launch Memory Optimize");
        while (!Volatile.Read(ref finished) && !loader.IsCanceled) {
            if (loader.Progress < 0.7) loader.Progress += 0.007; else loader.Progress += (0.95 - loader.Progress) * 0.02;
            Thread.Sleep(100);
        }
    }

    public void McLaunchPrecheck() {
        if (Setting<bool>("SystemDebugDelay")) Thread.Sleep(host.RandomInteger(100, 2000));
        if (McInstanceSelected.PathIndie.Contains("!") || McInstanceSelected.PathIndie.Contains(";")) throw new Exception("游戏路径中不可包含 ! 或 ;（" + McInstanceSelected.PathIndie + "）");
        if (McInstanceSelected.PathVersion.Contains("!") || McInstanceSelected.PathVersion.Contains(";")) throw new Exception("游戏路径中不可包含 ! 或 ;（" + McInstanceSelected.PathVersion + "）");
        if (McInstanceSelected == null) throw new Exception("未选择 Minecraft 版本！");
        McInstanceSelected.Load();
        if (Convert.ToInt32(McInstanceSelected.State) == 0) throw new Exception("Minecraft 存在问题：" + McInstanceSelected.Info);
        string result = ""; host.DispatchWait(() => result = McLoginAble(McLoginInput()));
        if (!string.IsNullOrEmpty(result)) throw new ArgumentException(result);
        if (host.ReleaseBuild() && CurrentLaunchOptions?.SaveBatch == null) host.NewThread(() => {
            if (new[] { 10, 20, 40, 60, 80, 100, 120, 150, 200, 250, 300, 350, 400, 500, 600, 700, 800, 900, 1000, 1200, 1400, 1600, 1800, 2000 }.Contains(Setting<int>("SystemLaunchCount"))) {
                if (MyMsgBox("PCL 已经为你启动了 " + Setting<int>("SystemLaunchCount") + " 次游戏啦！\r\n如果 PCL 还算好用的话，能不能考虑赞助一下 PCL……\r\n如果没有大家的支持，PCL 很难在免费、无任何广告的情况下维持数年的更新（磕头）……！",
                    Setting<int>("SystemLaunchCount") + " 次启动！", "支持 PCL！", "但是我拒绝") == 1) host.OpenWebsite("https://meloong.com/afd/a/LTCat");
            }
        }, "Donate");
        if (CurrentLaunchOptions?.SaveBatch == null && !Setting<bool>("HintBuy") && Setting<LegacyLaunchLoginType>("LoginType") != LegacyLaunchLoginType.Ms) {
            if (System.Globalization.CultureInfo.CurrentCulture.Name.StartsWithF("zh") || System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWithF("zh")) host.NewThread(() => {
                if (new[] { 3, 8, 15, 30, 50, 70, 90, 110, 130, 180, 220, 280, 330, 380, 450, 550, 660, 750, 880, 950, 1100, 1300, 1500, 1700, 1900 }.Contains(Setting<int>("SystemLaunchCount"))) {
                    if (MyMsgBox("你已经启动了 " + Setting<int>("SystemLaunchCount") + " 次 Minecraft 啦！\r\n如果觉得 Minecraft 还不错，可以购买正版支持一下，毕竟开发游戏也真的很不容易……不要一直白嫖啦。\r\n\r\n在登录一次正版账号后，就不会再出现这个提示了！",
                        "考虑一下正版？", "支持正版游戏！", "下次一定") == 1) host.OpenWebsite("https://www.xbox.com/zh-cn/games/store/minecraft-java-bedrock-edition-for-pc/9nxp44l49shj");
                }
            }, "Buy Minecraft");
            else if (Setting<LegacyLaunchLoginType>("LoginType") == LegacyLaunchLoginType.Legacy) {
                switch (MyMsgBox("你必须先登录正版账号，才能进行离线登录！", "正版验证", "购买正版", "试玩", "返回",
                    Button1Action: () => host.OpenWebsite("https://www.xbox.com/zh-cn/games/store/minecraft-java-bedrock-edition-for-pc/9nxp44l49shj"))) {
                    case 2: Hint("游戏将以试玩模式启动！", LegacyLaunchHint.Red); CurrentLaunchOptions!.ExtraGameArgs.Add("--demo"); break;
                    case 3: throw new OperationCanceledException();
                }
            }
        }
    }
}
