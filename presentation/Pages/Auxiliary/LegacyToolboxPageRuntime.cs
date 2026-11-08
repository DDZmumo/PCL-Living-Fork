// Behavior source: LF Pages/PageSetup and Pages/PageOther; repository LICENCE.
// LF lineage f4aa0fae62c3ee8af24c81e61844c8fdd13c8983; upstream PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8.
// Existing LF policies are preserved; no new native/performance/MMC behavior.
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using MeloongCore;

namespace PCLCS;
public sealed class LegacyToolboxPageRuntime<TFile, TDownload, TSave, TCombo>
    where TCombo : class
{
    public readonly LegacyToolboxPageHost<TFile, TDownload, TSave, TCombo> Host;
    private LegacySetupPageHost Common => Host.Common;

    public LegacyToolboxPageRuntime(LegacyToolboxPageHost<TFile, TDownload, TSave, TCombo> host) => Host = host;
    public void Jrrp() {
        DateTime date = Host.LuckDate();
        int score = LegacyDailyLuck.Score(date, Host.LuckIdentify());
        if (score == 0 && Common.Message("在查看结果前，请先同意以下附加使用条款：\r\n\r\n1. 我知晓并了解 PCL 的今日人品功能完全没有出 Bug。\r\n2. PCL 不对使用本软件所间接造成的一切财产损失（如砸电脑等）等负责。", "今日人品 - 附加使用条款", "同意并查看结果", "再见", warning: true) == 2) return;
        bool changed = Host.LuckTimeChanged();
        string text = (changed ? "在这个时候，你的人品值会是：" : "你今天的人品值是：") + score + LegacyDailyLuck.Description(score);
        Common.Message(text, (changed ? "今日人品? - " : "今日人品 - ") + date.ToString("yyyy'/'M'/'d"), "我知道了", warning: score < 20);
    }
    public string GetRandomCave() => GetRandomPresetHint();
    public string GetRandomHint() => GetRandomPresetHint();
    public string GetRandomPresetHint() => Host.RandomOne(new[] { "喜欢 PCL 的使用体验？欢迎前往原作者的官方渠道获取 PCL，支持龙腾猫跃持续开发。", "想体验 PCL 官方快照版和更多主题？可以前往龙腾猫跃的爱发电主页，了解赞助与获取方式。", "购买正版 Minecraft，支持 Mojang 继续创造新的冒险！请通过 Minecraft 官网购买并使用自己的微软账号登录。", "PCL Living Fork 是基于 PCL 原项目的第三方修改版本。欢迎了解并支持原作者开发的官方 PCL。", "已经拥有正版 Minecraft？使用购买游戏的微软账号登录，即可使用自己的正版身份开始冒险。" });
    public bool IsCleaningGameFiles => Host.ReadCleanup() != 0;

    public void StartCustomDownload(string url, string name, string? folder = null) => TryStartCustomDownload(url, name, folder);
    public bool TryStartCustomDownload(string url, string name, string? folder)
    {
        LegacyToolboxDownloadSession? session = null;
        try
        {
            if (IsCleaningGameFiles)
                throw new InvalidOperationException("正在清理游戏文件，请稍后再下载。");
            Uri address = LegacyToolboxDownloadSession.ParseUrl(url);
            if (string.IsNullOrWhiteSpace(folder))
            {
                // Original SaveFile selector is synchronous on the actual UI thread.
                string? chosen = Host.SaveFile("选择文件保存位置", name);
                if (chosen is null)
                    return false;
                name = Path.GetFileName(chosen);
                folder = Path.GetDirectoryName(chosen);
            }

            session = Host.CreateSession(folder!, name);
            bool overwrite = File.Exists(session.TargetPath);
            if (overwrite && Common.Message("目标文件已存在，下载成功后是否替换？\r\n\r\n" + session.TargetPath, "覆盖确认", "替换", "取消", warning: true) != 1)
            {
                session.Complete(false);
                return false;
            }

            LegacyToolboxDownloadSession active = session;
            TFile file = Host.NewFile(address.AbsoluteUri, session.StagePath, 0, true);
            Host.SetCredentialHeaders(file, false);
            Host.SetMergeFinished(file, active.OnCanceledMergeFinished);
            TDownload download = Host.NewDownload("下载文件：" + name, new List<TFile> { file });
            TCombo? combo = null;
            TSave save = Host.NewSave("保存下载文件", task => active.Commit(overwrite, () => Host.SaveCanceled(task) || Host.ComboState(combo!) == 4));
            Host.SaveWeight(save, 0.1);
            combo = Host.NewCombo("自定义下载：" + name + " (" + Host.NewUuid() + ")", download, save);
            Host.SetStateCallback(combo, loader => DownloadState(loader, active));
            Host.StartCombo(combo);
            Host.AddTaskbar(combo);
            Common.InUi(() =>
            {
                if (Host.HasMain())
                    Host.ExtraDownload().ShowRefresh();
                if (Host.HasMain())
                    Host.ExtraDownload().Ribble();
            });
            return true;
        }
        catch (Exception error)
        {
            session?.Complete(false);
            Logger.Error(error, "开始自定义下载失败", LogBehavior.Toast);
            return false;
        }
    }

    public void DownloadState(TCombo loader, LegacyToolboxDownloadSession session)
    {
        // Original closed LoadState: Finished=2, Failed=3, Canceled=4.
        switch (Host.ComboState(loader))
        {
            case 2:
                session.Complete(true);
                Common.Hint(Host.ComboName(loader) + "完成！", LegacySetupHint.Green, true);
                break;
            case 4:
                session.Complete(false);
                if (session.RecoveryPath is not null)
                    Common.Hint("下载已取消，原文件备份保留在：" + session.RecoveryPath, LegacySetupHint.Red, true);
                else if (session.UnrevertedPath is not null)
                    Common.Hint("下载已取消，但保存的文件因占用或权限问题未能撤回：" + session.UnrevertedPath, LegacySetupHint.Red, true);
                else
                    Common.Hint("自定义下载已取消。", LegacySetupHint.Blue, true);
                break;
            case 3:
                session.Complete(false);
                Logger.Error(Host.ComboError(loader), session.RecoveryPath is not null ? "自定义下载失败，原文件备份保留在：" + session.RecoveryPath : session.UnrevertedPath is not null ? "自定义下载失败，保存的文件未能撤回：" + session.UnrevertedPath : "自定义下载失败，原文件已保留。", LogBehavior.Toast);
                break;
        }
    }

    public bool GameFilesBusy() => Host.HasRunningGame() || Host.LaunchLoading() || Host.HasDownloadingTask();
    public void RubbishClear()
    {
        if (Host.AcquireCleanup() != 0)
            return;
        Host.NewThread(() =>
        {
            Common.InUi(() =>
            {
                if (Host.CurrentPage()is not null)
                    Host.CurrentPage()!.View.Clear().IsEnabled = false;
            });
            try
            {
                if (GameFilesBusy())
                {
                    Common.Hint("请先关闭游戏，并等待启动和下载任务结束。", LegacySetupHint.Blue, true);
                    return;
                }

                if (!Host.HasGameFolders())
                    Host.StartFolderLoader();
                Host.WaitFolderLoader();
                string[] folders = Host.GameFolders().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (Common.Message("清理已添加游戏目录中的日志、崩溃报告和可重建临时文件。\r\n存档、模组、配置、截图及自定义缓存目录将保留。", "清理游戏垃圾", "清理", "取消", warning: true) != 1)
                    return;
                if (GameFilesBusy())
                {
                    Common.Hint("游戏或下载状态已改变，本次清理已停止。", LegacySetupHint.Blue, true);
                    return;
                }

                LegacyGameGarbageCleanupResult result = LegacyGameGarbageCleaner.Clean(folders, () => new LegacyGameGarbageCleanupResult(), GameFilesBusy);
                string message = $"已清理 {result.DeletedFiles} 个文件，释放 {StringUtils.FormatByteSize(result.DeletedBytes)}。";
                if (result.SkippedItems > 0)
                    Common.Hint(message + $"{result.SkippedItems} 项因链接、占用或权限问题未清理。", LegacySetupHint.Blue, true);
                else
                    Common.Hint(message, LegacySetupHint.Green, true);
            }
            catch (OperationCanceledException)
            {
                Common.Hint("检测到游戏或下载任务，已停止继续清理。", LegacySetupHint.Blue, true);
            }
            catch (Exception error)
            {
                Logger.Error(error, "清理游戏垃圾失败", LogBehavior.Toast);
            }
            finally
            {
                Host.ReleaseCleanup();
                Common.InUi(() =>
                {
                    if (Host.CurrentPage()is not null)
                        Host.CurrentPage()!.View.Clear().IsEnabled = true;
                });
            }
        }, "Game Rubbish Clear");
    }

    public void MemoryOptimize(bool showHint)
    {
        if (Host.AcquireMemory() != 0)
        {
            if (showHint)
                Common.Hint("内存优化尚未结束，请稍等！", LegacySetupHint.Blue, true);
            return;
        }

        try
        {
            if (showHint)
                Common.Hint("正在进行内存优化……", LegacySetupHint.Blue, true);
            long released = LegacyMemoryOptimizationOperation.Execute(Host.HasAdminRole(), Host.AvailableMemory, () => MemoryOptimizeInternal(showHint), () => Host.RunElevated("--memory"));
            if (showHint)
                Common.Hint($"内存优化完成，目前可用 {StringUtils.FormatByteSize(Host.AvailableMemory())}，增加约 {StringUtils.FormatByteSize(released)}。", LegacySetupHint.Green, true);
        }
        catch (OperationCanceledException)
        {
            if (showHint)
                Common.Hint("已取消内存优化。", LegacySetupHint.Blue, true);
            Logger.Info("用户取消了内存优化的管理员授权。");
        }
        catch (Exception error)
        {
            Logger.Warn(error, "内存优化未完成", showHint ? LogBehavior.Toast : LogBehavior.None);
        }
        finally
        {
            Host.ReleaseMemory();
        }
    }

    public void MemoryOptimizeInternal(bool showHint) => Host.OptimizeNative();
}
