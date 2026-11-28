using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using MeloongCore;
using MeloongCore.Extensions;
using PCLCS;

namespace PCLLF.Presentation.Mods;

public sealed partial class LegacyModPage<TMod,TProject,TVersion,TItem,TInstance,TDownload,TButton,TImpact>
    where TMod : LocalResourceFileData<TProject,TVersion> where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData
    where TItem : FrameworkElement where TInstance : class where TDownload : class where TButton : FrameworkElement where TImpact : ModDependencyImpactItemData {
    public void UpdateMods(IEnumerable<TMod> mods, bool ModeConfirmed = false) {
        if (!AllowModWrite(host)) return;
        if (host.HistoryBusy()) { host.Hint("请等待环境还原操作完成。", 0, true); return; }
        TInstance? snapshot = host.Instance();
        if (snapshot is null) return;
        var selected = mods.ToList();
        string gameFolder = host.GameFolder(snapshot);
        string pathMods = Path.Combine(gameFolder, "mods") + Path.DirectorySeparatorChar;
        if (host.UpdatingFolders().Contains(pathMods, StringComparer.OrdinalIgnoreCase)) {
            host.Hint("此实例正在更新 Mod，请稍候。", 0, true);
            return;
        }
        if (host.ModpackMode()) {
            if (!ModeConfirmed && !host.ConfirmMode("更新 Mod")) return;
        } else if (!host.ReadBoolean("HintUpdateMod") || selected.Count >= 15) {
            if (host.Message("新版本 Mod 可能不兼容旧存档或者其他 Mod，这可能导致游戏崩溃，甚至永久损坏存档！\r\n如果你在游玩整合包，请千万不要自行更新 Mod！\r\n\r\n在更新前，请先备份存档，并检查 Mod 的更新日志。\r\n如果更新后出现问题，你也可以在回收站找回更新前的 Mod。", "Mod 更新警告", "我已了解风险，继续更新", "取消", true) != 1)
                return;
            host.WriteSetting("HintUpdateMod", true);
        }
        try {
            var plan = new LegacyLocalModFiles<TMod,TProject,TVersion,TDownload>(selected, host.TempPath, host.ToNetFile);
            if (plan.Files.Count == 0) return;
            LegacyModUpdateJob loader = host.CreateUpdateJob(() => "Mod 更新：" + host.InstanceName(snapshot), plan.Files, plan.Files.Count * 1.5,
                token => {
                    string stamp = DependencyStamp(gameFolder);
                    plan.Replace(() => {
                        if (plan.EnabledReplacements.Count == 0) return;
                        var problems = host.CheckUpdateDependencies(snapshot, plan.EnabledReplacements, token);
                        if (problems.Count == 0) return;
                        var confirmed = problems.Where(problem => problem.Confirmed).ToArray();
                        if (confirmed.Length > 0)
                            throw new ModUpdateDependencyException(
                                "本次更新会导致必需前置缺失或版本不符。已保留原有 Mod，请补齐兼容前置，或取消选择相关更新后再试。\r\n\r\n" +
                                DependencyDetails(confirmed));
                        token.ThrowIfCancellationRequested();
                        if (host.Message("部分模组声明未能读全。以下必需前置未在已识别的启用模组中找到，请核对后再更新。\r\n\r\n" +
                            DependencyDetails(problems), "核对新版必需前置", "取消更新", "仍然更新", true) != 2)
                            throw new OperationCanceledException();
                    }, () => {
                        host.CaptureHistory(snapshot, "更新模组前", token);
                        if (!PathUtils.IsSame(gameFolder, host.GameFolder(snapshot)) || DependencyStamp(gameFolder) != stamp)
                            throw new IOException("检查期间 Mod 文件或游戏目录已变化，请重新检查更新。");
                    }, token);
                });
            loader.SetOnStateChanged(Finished);
            Logger.Info($"开始更新 {plan.Files.Count} 个 Mod：{string.Join("，", selected.Select(mod => mod.File.Name))}");
            host.UpdatingFolders().Add(pathMods);
            try { loader.Start(); }
            catch { host.UpdatingFolders().Remove(pathMods); throw; }
            loader.AddTaskbar();
            host.RefreshDownloadButton();
            host.RibbleDownloadButton();
            ReloadModList(true);

            void Finished(LegacyModUpdateJob job) {
                int state = job.State();
                if (state is not (2 or 3 or 4)) return;
                Logger.Info($"已从正在进行 Mod 更新的文件夹列表移除：{pathMods}");
                host.UpdatingFolders().Remove(pathMods);
                host.Background(plan.Clean, "Clean Mod Update Cache", ThreadPriority.BelowNormal);
                switch (state) {
                    case 2 when plan.FinishedFileNames.Count == 1:
                        host.Hint($"已成功更新 {plan.FinishedFileNames[0]}！", 2, true);
                        break;
                    case 2 when plan.FinishedFileNames.Count > 1:
                        host.Hint($"已成功更新 {plan.FinishedFileNames.Count} 个 Mod！", 2, true);
                        break;
                    case 3:
                        Exception? error = job.Error();
                        if (error?.GetBaseException() is ModUpdateDependencyException dependency)
                            host.Message(dependency.Message, "Mod 更新需要前置", "知道了", "", true);
                        else
                            host.Hint("Mod 更新失败：" + error.GetDisplay(false), 1, true);
                        break;
                    case 4:
                        host.Hint("Mod 更新已中止！", 3, true);
                        break;
                }
            }
        } catch (Exception error) { Logger.Warn(error, "初始化 Mod 更新失败"); }
    }

    private static string DependencyDetails(IReadOnlyList<ModUpdateDependencyFinding> problems) {
        string details = string.Join("\r\n\r\n", problems.Take(8).Select(problem => problem.Message));
        return problems.Count > 8 ? details + $"\r\n\r\n还有 {problems.Count - 8} 项问题。" : details;
    }
    private void DeleteMods(IEnumerable<TMod> mods,bool ModeConfirmed=false){
        bool permanent=host.ShiftDown();
        var frozen=mods.GroupBy(mod=>mod.StorageKey,StringComparer.OrdinalIgnoreCase).Select(group=>(Mod:group.First(),Path:group.First().File.FullName)).ToList();
        if(!ModeConfirmed&&!host.ConfirmMode("删除 Mod"))return;
        BeginDependencyMutation(frozen.Select(item=>item.Path),"删除",()=>DeleteModsCore(frozen,permanent));
    }
    private void DeleteModsCore(IReadOnlyList<(TMod Mod,string Path)> selected,bool permanent){
        if(!AllowModWrite(host))return;
        try{
            bool success=true;var list=selected.Where(item=>FileUtils.Exists(item.Path)).ToList();
            foreach(var item in list){
                TMod mod=item.Mod;
                try{FileUtils.Delete(item.Path,!permanent);}catch(OperationCanceledException error){Logger.Warn(error,"删除 Mod 被主动取消");ReloadModList(true);return;}catch(Exception error){Logger.Error(error,$"删除 Mod 失败（{item.Path}）",LogBehavior.Alert);success=false;continue;}
                host.Selected().Remove(mod.StorageKey);host.Output()!.Remove(mod);searchResult?.Remove(mod);host.Items().TryRemove(mod.StorageKey,out _);
                int index=view.List()!.Items.IndexOf(view.List()!.Items.OfType<TItem>().FirstOrDefault(item=>host.Entry(item).Equals(mod)));if(index>=0)view.List()!.Items.RemoveAt(index);
            }
            RefreshBars();if(!success){host.Hint("由于文件被占用，Mod 删除失败，请尝试关闭正在运行的游戏后再试！",1,true);ReloadModList(true);}else if(view.List()!.Items.Count==0)ReloadModList(true);else RefreshBars();
            if(!success)return;if(list.IsSingle())host.Hint($"已删除 {Path.GetFileName(list.Single().Path)}！",2,true);else host.Hint($"已删除 {list.Count} 个文件！",2,true);
        }catch(OperationCanceledException error){Logger.Warn(error,"删除 Mod 被主动取消");ReloadModList(true);}catch(Exception error){Logger.Error(error,"删除 Mod 出现未知错误");ReloadModList(true);}host.LoaderRun(2);
    }
}
