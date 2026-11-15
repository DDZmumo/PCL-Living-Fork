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
    public void UpdateMods(IEnumerable<TMod> mods,bool ModeConfirmed=false){
        if(!AllowModWrite(host))return;
        if(host.HistoryBusy()){host.Hint("请等待环境还原操作完成。",0,true);return;}
        TInstance? snapshot=host.Instance();
        if(host.ModpackMode()){if(!ModeConfirmed&&!host.ConfirmMode("更新 Mod"))return;}
        else if(!host.ReadBoolean("HintUpdateMod")||mods.Count()>=15){
            if(host.Message("新版本 Mod 可能不兼容旧存档或者其他 Mod，这可能导致游戏崩溃，甚至永久损坏存档！\r\n如果你在游玩整合包，请千万不要自行更新 Mod！\r\n\r\n在更新前，请先备份存档，并检查 Mod 的更新日志。\r\n如果更新后出现问题，你也可以在回收站找回更新前的 Mod。","Mod 更新警告","我已了解风险，继续更新","取消",true)==1)host.WriteSetting("HintUpdateMod",true);else return;
        }
        try{
            mods=mods.ToList();var plan=new LegacyLocalModFiles<TMod,TProject,TVersion,TDownload>(mods,host.TempPath,host.ToNetFile);
            LegacyModUpdateJob loader=host.CreateUpdateJob(()=>"Mod 更新："+host.InstanceName(host.Instance()!),plan.Files,mods.Count()*1.5,
                ()=>plan.Replace(()=>host.CaptureHistory(snapshot!,"更新模组前",CancellationToken.None)));
            string pathMods=host.GameFolder(host.Instance()!)+@"mods\";
            loader.SetOnStateChanged(Finished);
            Logger.Info($"开始更新 {mods.Count()} 个 Mod：{string.Join("，",mods.Select(mod=>mod.File.Name))}");host.UpdatingFolders().Add(pathMods);loader.Start();loader.AddTaskbar();host.RefreshDownloadButton();host.RibbleDownloadButton();ReloadModList(true);
            void Finished(LegacyModUpdateJob job){
                switch(job.State()){
                    case 2: switch(plan.FinishedFileNames.Count){case 0:Logger.Info("没有 Mod 被成功更新");break;case 1:host.Hint($"已成功更新 {plan.FinishedFileNames.Single()}！",2,true);break;default:host.Hint($"已成功更新 {plan.FinishedFileNames.Count} 个 Mod！",2,true);break;}break;
                    case 3:host.Hint("Mod 更新失败："+job.Error()!.GetDisplay(false),1,true);break;
                    case 4:host.Hint("Mod 更新已中止！",3,true);break;
                    default:return;
                }
                Logger.Info($"已从正在进行 Mod 更新的文件夹列表移除：{pathMods}");host.UpdatingFolders().Remove(pathMods);
                host.Background(plan.Clean,"Clean Mod Update Cache",ThreadPriority.BelowNormal);
            }
        }catch(Exception error){Logger.Warn(error,"初始化 Mod 更新失败");}
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
                int index=view.List()!.Children.IndexOf(view.List()!.Children.OfType<TItem>().FirstOrDefault(item=>host.Entry(item).Equals(mod)));if(index>=0)view.List()!.Children.RemoveAt(index);
            }
            RefreshBars();if(!success){host.Hint("由于文件被占用，Mod 删除失败，请尝试关闭正在运行的游戏后再试！",1,true);ReloadModList(true);}else if(view.List()!.Children.Count==0)ReloadModList(true);else RefreshBars();
            if(!success)return;if(list.IsSingle())host.Hint($"已删除 {Path.GetFileName(list.Single().Path)}！",2,true);else host.Hint($"已删除 {list.Count} 个文件！",2,true);
        }catch(OperationCanceledException error){Logger.Warn(error,"删除 Mod 被主动取消");ReloadModList(true);}catch(Exception error){Logger.Error(error,"删除 Mod 出现未知错误");ReloadModList(true);}host.LoaderRun(2);
    }
}
