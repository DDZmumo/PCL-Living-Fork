using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeloongCore;
using MeloongCore.Extensions;
using PCLCS;

namespace PCLLF.Presentation.Mods;

// LF PageInstanceMod.xaml.vb f4aa0fae62c3ee8af24c81e61844c8fdd13c8983;
// PCL baseline 5ef81e490ae46b1100d61e33d0183094c7a80ca8; LF LICENCE.
// Fixed CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 Pages/PageInstance/
// PageInstanceCompResource.xaml.cs reviewed (Apache LICENSE / original PCL LICENCE).
// Its expanded component store, dependency policy/localization are not imported.
public sealed partial class LegacyModPage<TMod,TProject,TVersion,TItem,TInstance,TDownload,TButton,TImpact>
    where TMod : LocalResourceFileData<TProject,TVersion> where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData
    where TItem : FrameworkElement where TInstance : class where TDownload : class where TButton : FrameworkElement where TImpact : ModDependencyImpactItemData {
    private readonly LegacyModPageView view;
    private readonly LegacyModPageHost<TMod,TProject,TVersion,TItem,TInstance,TDownload,TButton,TImpact> host;
    private bool loaded;
    private int dependencyChecking,bottomCount;
    private LegacyModFilter filter;
    private LocalResourceSortOrder sortOrder;
    private List<TMod>? searchResult;
    private const string EmptyTitleDefault = "尚未安装 Mod";
    private const string EmptyHintDefault = "你可以下载新的 Mod，或者从已经下载好的文件安装 Mod。\r\n如果你已经安装了 Mod，可能是版本隔离设置有误，请在设置中调整版本隔离选项。";
    // Test-visible rebuild counter: only a successful ReloadModList or a complete
    // LoadUI rebuild increments it; favorite toggles must leave it unchanged.
    public int ListRebuildSerial {get;private set;}
    public LegacyModPage(LegacyModPageView view,LegacyModPageHost<TMod,TProject,TVersion,TItem,TInstance,TDownload,TButton,TImpact> host){this.view=view;this.host=host;}
    public void Loaded(){host.ApplyMode();if(!host.LastWasDetails())view.ScrollHome();host.ChangeAnimationControl(1);host.Selected().Clear();ReloadModList();ApplyLoaderWarnings();ChangeAllSelected(false);host.ChangeAnimationControl(-1);if(loaded)return;loaded=true;host.BindMainKey();host.AdjustFilterMargins();}
    public void ReloadModList(bool ForceReload=false){if(!host.LoaderRun(ForceReload?1:0))return;ListRebuildSerial++;Logger.Info("已刷新 Mod 列表");Filter=LegacyModFilter.All;view.ScrollHome();view.SetSearch("");}
    public static void Refresh(LegacyModPageHost<TMod,TProject,TVersion,TItem,TInstance,TDownload,TButton,TImpact> host){
        try{host.ClearProjectCache();host.ClearVersionCache();FileUtils.Delete(host.TempPath()+@"Cache\LocalMod.json");Logger.Info("由于点击刷新按钮，清理本地 Mod 信息缓存");}catch(Exception error){Logger.Warn(error,"强制刷新时清理本地 Mod 信息缓存失败");}
        host.ReloadCurrent(true);host.SelectModPage();host.Hint("正在刷新……",0,false);
    }
    public void Initialize()=>host.InitializeLoader();
    public void LoadClick(){if(host.LoaderState()==3)host.LoaderRun(1);}
    public void LoadUI(){
        try{
            if(host.Output()!.Any()){view.Back().Visibility=Visibility.Visible;view.Empty().Visibility=Visibility.Collapsed;}
            else{view.Empty().Visibility=Visibility.Visible;view.Back().Visibility=Visibility.Collapsed;view.SetEmptyTitle(EmptyTitleDefault);view.SetEmptyHint(EmptyHintDefault);return;}
            host.Items().Clear();foreach(TMod entry in host.Output()!)host.Items()[entry.EnabledName]=MakeItem(entry);
            Filter=LegacyModFilter.All;view.SetSearch("");RefreshUI();ListRebuildSerial++;
        }catch(Exception error){Logger.Error(error,"加载 Mod 列表 UI 失败");}
    }
    private TItem MakeItem(TMod entry){host.ChangeAnimationControl(1);TItem item=host.NewItem(entry,host.Selected().Contains(entry.EnabledName));host.BindUpdate(entry,item);TInstance? target=host.Instance();if(target is not null)host.RefreshLoaderWarning(item,target);host.ChangeAnimationControl(-1);return item;}
    public void ApplyLoaderWarnings(){TInstance? target=host.Instance();if(target is null)return;foreach(TItem item in host.Items().Values)host.RefreshLoaderWarning(item,target);}
    public void RebuildDependencyIndex(){
        TInstance? target=host.Instance();if(target is null)return;
        if(Interlocked.CompareExchange(ref dependencyChecking,1,0)!=0){host.Hint("正在检查模组依赖，请稍候。",0,true);return;}
        view.EnableIndex(false);view.IndexText("正在更新索引");host.Hint("正在更新依赖索引并获取模组依赖声明……",0,true);
        host.Background(()=>{
            try{
                host.ResetIndexLookups();
                LegacyModDependencyIndexResult report=host.RebuildIndex(target,(done,total)=>{
                    if(done%10!=0&&done!=total)return;
                    host.Dispatch(()=>view.IndexText(done==total?"正在获取声明":"读取声明 "+done+"/"+total));
                });
                host.Dispatch(()=>{
                    if(ReferenceEquals(host.Instance(),target))ApplyLoaderWarnings();
                    if(report.IndexReadFailed())host.Hint("部分模组暂时无法读取，请解除文件占用后重新更新索引。",1,true);
                    else if(!report.IndexPersisted())host.Hint("依赖索引已更新，但未能保存到磁盘。",1,true);
                    else if(report.OnlineIncomplete())host.Hint("依赖索引已更新，部分平台声明未能获取；已保留本地声明。",0,true);
                    else host.Hint("模组依赖索引与声明已更新。",2,true);
                });
            }catch(Exception error){Logger.Error(error,"更新模组依赖树索引失败",LogBehavior.Toast);}
            finally{host.Dispatch(()=>{Interlocked.Exchange(ref dependencyChecking,0);view.EnableIndex(true);view.IndexText("更新依赖索引");});}
        },"Rebuild Mod Dependency Index",ThreadPriority.BelowNormal);
    }
    private static bool AllowModWrite(LegacyModPageHost<TMod,TProject,TVersion,TItem,TInstance,TDownload,TButton,TImpact> host){if(!host.AnyLocked())return true;host.Hint("文件任务进行中，请稍后再修改 Mod。",1,true);return false;}
    private static string DependencyStamp(string folder){folder=Path.Combine(folder,"mods");if(!Directory.Exists(folder))return "";return string.Join("|",new DirectoryInfo(folder).GetFiles().OrderBy(item=>item.Name,StringComparer.OrdinalIgnoreCase).Select(item=>item.Name+":"+item.Length+":"+item.LastWriteTimeUtc.Ticks));}
    private void BeginDependencyMutation(IEnumerable<string> paths,string actionName,Action continuation){
        if(!AllowModWrite(host))return;
        TInstance? target=host.Instance();string[] candidates=paths.ToArray();if(target is null||candidates.Length==0){continuation();return;}
        if(Interlocked.CompareExchange(ref dependencyChecking,1,0)!=0){host.Hint("正在检查模组依赖，请稍候。",0,true);return;}
        host.Hint("正在检查本地模组依赖影响……",0,true);
        host.Background(()=>{
            string? caption=null,stamp=null;Exception? failure=null;List<TImpact>? impacts=null;
            try{stamp=DependencyStamp(host.GameFolder(target));impacts=host.InspectImpacts(target,candidates);caption=impacts.Count==0?null:string.Join("\r\n",impacts.Select(item=>item.Name+"："+string.Join("；",item.Reasons)));}catch(Exception error){failure=error;}
            host.Dispatch(()=>{try{
                if(failure is not null){Logger.Error(failure,"检查模组依赖失败，本次操作已停止",LogBehavior.Toast);return;}
                if(!ReferenceEquals(host.Instance(),target)||host.Locked(target))return;
                ApplyLoaderWarnings();
                if(!string.IsNullOrEmpty(caption)&&host.ImpactConfirm(caption!,actionName,impacts!)!=1)return;
                if(DependencyStamp(host.GameFolder(target))!=stamp){host.Hint("Mod 列表已变化，请重新执行操作以检查依赖。",1,true);return;}
                continuation();
            }finally{Interlocked.Exchange(ref dependencyChecking,0);}});
        },"Mod Dependency Impact",ThreadPriority.Normal);
    }
    public void ShowDependencyDetails(TMod? entry){
        TInstance? target=host.Instance();if(target is null||entry is null)return;host.Hint("正在读取模组依赖关系……",0,true);
        host.Background(()=>{try{LegacyModDependencyDetails result=host.InspectDetails(target,entry);host.Dispatch(()=>{if(!ReferenceEquals(host.Instance(),target))return;ApplyLoaderWarnings();if(result.HasMod())host.ContentMessage(result.Caption,"模组依赖关系",result.CreateView);else host.Message(result.Caption,"模组依赖关系","确定","",false);});}catch(Exception error){Logger.Error(error,"读取模组依赖关系失败",LogBehavior.Toast);}},"Mod Dependency Details",ThreadPriority.Normal);
    }
    public void ItemContent(TItem item){
        host.BindRowEvents(item);
        TButton open=host.CreateButton(1.05,host.OpenLogo(),"打开文件位置",item,sender=>OpenSender(sender));
        TButton info=host.CreateButton(1,host.InfoLogo(),"详情",item,sender=>Info(sender));
        TButton delete=host.CreateButton(1,host.DeleteLogo(),"删除",item,sender=>DeleteItem(host.ResolveItem(sender)));
        TButton toggle=host.CreateButton(1,host.Entry(item).IsEnabled?host.StopLogo():host.CheckLogo(),host.Entry(item).IsEnabled?"禁用":"启用",item,sender=>ToggleItem(host.ResolveItem(sender)));
        TButton dependencies=host.CreateButton(1,host.ListLogo(),"查看模组依赖关系",null,_=>ShowDependencyDetails(host.Entry(item)));
        host.SetButtons(item,new[]{info,dependencies,open,toggle,delete});
    }
    public bool IsSearching=>!string.IsNullOrWhiteSpace(view.Search());
    private IEnumerable<TMod> Current=>IsSearching?(searchResult??new List<TMod>()):(host.Output()??new List<TMod>());
    public LocalResourceSortOrder SortOrder {
        get=>sortOrder;
        set {
            if(!Enum.IsDefined(typeof(LocalResourceSortOrder),value))throw new ArgumentOutOfRangeException(nameof(value));
            if(sortOrder==value)return;
            sortOrder=value;RefreshUI();
        }
    }
    public void SortChanged(int value){if(value>=0)SortOrder=(LocalResourceSortOrder)value;}
    public void RefreshUI(){
        if(view.List() is null)return;IReadOnlyList<TMod> showing=LocalResourceSort.Order(Current.Where(mod=>CanPassFilter(mod)),sortOrder,mod=>mod.File);host.ChangeAnimationControl(1);
        if(showing.Any()){view.List()!.Visibility=Visibility.Visible;view.List()!.Children.Clear();foreach(TMod mod in showing){if(!host.Items().TryGetValue(mod.EnabledName,out TItem? item))continue;host.SetChecked(item,host.Selected().Contains(mod.EnabledName));view.List()!.Children.Add(item);}view.Empty().Visibility=Visibility.Collapsed;}
        else{view.List()!.Visibility=Visibility.Collapsed;if(!(host.Output()?.Any()??false)){view.Empty().Visibility=Visibility.Visible;view.SetEmptyTitle(EmptyTitleDefault);view.SetEmptyHint(EmptyHintDefault);}else view.Empty().Visibility=Visibility.Collapsed;}
        host.ChangeAnimationControl(-1);host.SetSelected(host.Selected().Where(name=>showing.Any(mod=>Same(mod.EnabledName,name))).ToList());RefreshBars();
    }
    public void RefreshBars(){
        int any=0,enabled=0,updates=0;foreach(TMod mod in Current){any++;if(mod.HasUpdate)updates++;if(mod.IsEnabled)enabled++;}int disabled=any-enabled;
        ResourcePageFilter[] filters=view.Filters();filters[0].Text=(IsSearching?"搜索结果":"全部")+$" ({any})";filters[3].Text=$"可更新 ({updates})";filters[3].Visibility=Filter==LegacyModFilter.CanUpdate||updates>0?Visibility.Visible:Visibility.Collapsed;
        filters[1].Text=$"启用 ({enabled})";filters[1].Visibility=Filter==LegacyModFilter.Enabled||(enabled>0&&enabled<any)?Visibility.Visible:Visibility.Collapsed;filters[2].Text=$"禁用 ({disabled})";filters[2].Visibility=Filter==LegacyModFilter.Disabled||disabled>0?Visibility.Visible:Visibility.Collapsed;
        int count=host.Selected().Count;bool selected=count>0;if(selected)view.SetSelectionText($"已选择 {count} 个文件");
        if(selected){bool hasUpdate=false,hasEnabled=false,hasDisabled=false;foreach(TMod mod in host.Output()!){if(!host.Selected().Contains(mod.EnabledName))continue;if(mod.HasUpdate)hasUpdate=true;if(mod.IsEnabled)hasEnabled=true;if(!mod.IsEnabled)hasDisabled=true;}view.EnableDisable(hasEnabled);view.EnableEnable(hasDisabled);view.EnableUpdate(hasUpdate);}
        view.ListBack().Margin=new Thickness(0,0,0,selected?95:15);view.SelectionBar(count,ref bottomCount,view.Root().IsLoaded&&host.AnimationControl()==0);
    }
    public void OpenFolder(){try{DirectoryUtils.Create(host.GameFolder(host.Instance()!)+@"mods\");host.Explorer(host.GameFolder(host.Instance()!)+@"mods\");}catch(Exception error){Logger.Error(error,"打开 Mods 文件夹失败",LogBehavior.Alert);}}
    public void SelectAll()=>ChangeAllSelected(host.Selected().Count<view.List()!.Children.Count);
    public void InstallClick(){if(!host.ConfirmMode("从文件安装 Mod"))return;IEnumerable<string> files=host.SelectInstallFiles();if(!files.Any())return;Install(host,files,true);}
    public static bool Install(LegacyModPageHost<TMod,TProject,TVersion,TItem,TInstance,TDownload,TButton,TImpact> host,IEnumerable<string> files,bool modeConfirmed){
        string extension=PathUtils.GetExtension(files.First());if(!new[]{"jar","litemod","disabled","old"}.Any(t=>t==extension))return false;
        if(!AllowModWrite(host))return true;
        if(!modeConfirmed&&!host.ConfirmMode("安装 Mod"))return true;Logger.Info("文件为 jar/litemod 格式，尝试作为 Mod 安装");
        if(files.First().Contains(@":\$RECYCLE.BIN\")){host.Hint("请先将文件从回收站还原，再尝试安装！",1,true);return true;}
        TInstance? instance=host.GlobalInstance();if(host.IsInstanceSetup())instance=host.Instance();
        if(host.IsInstanceSelect()||instance is null||!host.Modable(instance))host.Hint("若要安装 Mod，请先选择一个可以安装 Mod 的版本！",0,true);
        else if(!host.IsModPage()){if(host.Message($"是否要将这{(files.IsSingle()?"个":"些")}文件作为 Mod 安装到 {host.InstanceName(instance)}？","Mod 安装确认","确定","取消",false)==1)InstallNow();}
        else InstallNow();
        return true;
        void InstallNow(){try{
            foreach(string file in files){string target=host.NewMod(file).EnabledName;if(!target.Contains("."))target+=".jar";target=host.GameFolder(instance!)+@"mods\"+target;if(PathUtils.IsSame(file,target))continue;FileUtils.Delete(target,true);FileUtils.Copy(file,target);}
            if(files.IsSingle())host.Hint($"已安装 {host.NewMod(files.First()).EnabledName}！",2,true);else host.Hint($"已安装 {files.Count()} 个 Mod！",2,true);
            if(host.IsModPage())host.ForceFolderLoader(host.GameFolder(instance!)+@"mods\");
        }catch(Exception error){Logger.Error(error,"复制 Mod 文件失败",LogBehavior.Alert);}}
    }
    public void Download(){if(host.ModpackMode())return;host.NavigateDownload(host.Instance()!,view.Search());}
    public void CheckChanged(TItem item){if(host.AnimationControl()!=0)return;string key=host.Entry(item).EnabledName;if(host.Checked(item)){if(!host.Selected().Contains(key))host.Selected().Add(key);}else host.Selected().Remove(key);RefreshBars();}
    private void ChangeAllSelected(bool value){host.ChangeAnimationControl(1);host.Selected().Clear();foreach(TItem item in host.Items().Values){bool selected=value&&view.List()!.Children.Contains(item);host.SetChecked(item,selected);if(selected)host.Selected().Add(host.Entry(item).EnabledName);}host.ChangeAnimationControl(-1);RefreshBars();}
    public void UnselectAnimated(){int old=host.AnimationControl();host.SetAnimationControl(0);ChangeAllSelected(false);host.ChangeAnimationControl(old);}
    public void MainKey(KeyEventArgs e){if(!host.IsCurrentPage())return;if(host.CtrlDown()&&e.Key==Key.A)ChangeAllSelected(true);}
    public void SearchKey(KeyEventArgs e){if(view.Search().Any())return;if(host.CtrlDown()&&e.Key==Key.A)view.FocusBack();}
    public LegacyModFilter Filter {get=>filter;set{if(filter==value)return;if(!Enum.IsDefined(typeof(LegacyModFilter),value))return;filter=value;view.Filters()[(int)value].Checked=true;RefreshUI();}}
    private bool CanPassFilter(TMod mod)=>Filter switch{LegacyModFilter.All=>true,LegacyModFilter.Enabled=>mod.IsEnabled,LegacyModFilter.Disabled=>!mod.IsEnabled,LegacyModFilter.CanUpdate=>mod.HasUpdate,_=>false};
    public void FilterChanged(int value){Filter=Enum.IsDefined(typeof(LegacyModFilter),value)?(LegacyModFilter)value:LegacyModFilter.All;}
    public void SelectED(bool enable){EDMods(host.Output()!.Where(mod=>host.Selected().Contains(mod.EnabledName)),enable);ChangeAllSelected(false);}
    private void EDMods(IEnumerable<TMod> mods,bool enable){List<TMod> frozen=mods.ToList();if(enable)ApplyModState(frozen,true);else BeginDependencyMutation(frozen.Where(mod=>mod.IsEnabled).Select(mod=>mod.File.FullName!),"禁用",()=>ApplyModState(frozen,false));}
    private void ApplyModState(IEnumerable<TMod> mods,bool enable){
        if(!AllowModWrite(host))return;
        bool success=true;foreach(TMod mod in mods.ToList()){
            string target;if(mod.IsEnabled&&!enable)target=PathUtils.RemoveExtendedPrefix(mod.File.FullName!)+(FileUtils.Exists(mod.File.FullName+".old")?".old":".disabled");else if(!mod.IsEnabled&&enable)target=mod.EnabledFullName;else continue;
            try{
                if(FileUtils.Exists(target)){if(FileUtils.Exists(mod.File.FullName!)){if(CryptographyUtils.ComputeFileHash(mod.File.FullName!)!=CryptographyUtils.ComputeFileHash(target)){host.Message($"目前同时存在启用和禁用的两个 Mod 文件：\r\n - {PathUtils.GetLastPart(target)}\r\n - {mod.File.Name}\r\n\r\n注意，这两个文件的内容并不相同。\r\n在手动删除或重命名其中一个文件后，才能继续操作。","存在文件冲突","确定","",false);continue;}}else{Logger.Warn("Mod 的状态已被切换");continue;}}
                FileUtils.Move(mod.File.FullName!,target);
            }catch(FileNotFoundException error){Logger.Error(error,$"未找到需要重命名的 Mod（{mod.File.FullName??"null"}）");ReloadModList(true);return;}catch(Exception error){Logger.Warn(error,$"重命名 Mod 失败（{mod.File.FullName??"null"}）");success=false;}
            TMod replacement=host.NewMod(target);replacement.FromJson(mod.ToJson());
            if(host.Output()!.Contains(mod)){int index=host.Output()!.IndexOf(mod);host.Output()!.RemoveAt(index);host.Output()!.Insert(index,replacement);}
            if(searchResult is not null&&searchResult.Contains(mod)){int index=searchResult.IndexOf(mod);searchResult.Remove(mod);searchResult.Insert(index,replacement);}
            TItem newItem=MakeItem(replacement);host.Items()[mod.EnabledName]=newItem;
            int ui=view.List()!.Children.IndexOf(view.List()!.Children.OfType<TItem>().FirstOrDefault(item=>ReferenceEquals(host.Entry(item),mod)));if(ui==-1)continue;view.List()!.Children.RemoveAt(ui);view.List()!.Children.Insert(ui,newItem);newItem.UpdateLayout();
        }
        if(success)RefreshBars();else{host.Hint("由于文件被占用，Mod 的状态切换失败，请尝试关闭正在运行的游戏后再试！",1,true);ReloadModList(true);}host.LoaderRun(2);
    }
    public void UpdateSelected(){List<TMod> updates=host.Output()!.Where(mod=>host.Selected().Contains(mod.EnabledName)&&mod.HasUpdate).ToList();if(!updates.Any())return;if(!host.ConfirmMode("更新 Mod"))return;UpdateMods(updates,true);ChangeAllSelected(false);}
    public void DeleteSelected(){DeleteMods(host.Output()!.Where(mod=>host.Selected().Contains(mod.EnabledName)));ChangeAllSelected(false);}
    public void CancelSelection()=>ChangeAllSelected(false);
    public void Info(object sender){try{
        TMod mod=host.Entry(host.ResolveItem(sender));if(mod.Project is not null)host.NavigateDetails(mod.Project);
        else{List<string> lines=new();if(mod.Description is not null)lines.Add(mod.Description+"\r\n");lines.Add("文件："+mod.File.Name+"（"+host.FormatSize(mod.File.Length)+"）");if(mod.Version is not null)lines.Add("版本："+mod.Version);List<string> debug=new();if(debug.Any()){lines.Add("");lines.AddRange(debug);}
            string original=mod.Display.Replace(" ","+"),search=original.Substring(0,1);for(int i=1;i<=original.Count()-1;i++){bool previousLower=original[i-1].ToString().Lower().Equals(original[i-1].ToString()),currentLower=original[i].ToString().Lower().Equals(original[i].ToString());if(previousLower&&!currentLower)search+="+";search+=original[i];}search=search.Replace("++","+").Replace("pti+Fine","ptiFine");
            if(host.Message(string.Join("\r\n",lines),mod.Display,"百科搜索","返回",false)==1)host.Website("https://www.mcmod.cn/s?key="+search+"&site=all&filter=0");}
    }catch(Exception error){Logger.Error(error,"获取 Mod 详情失败");}}
    public bool RunLoader(int type)=>host.LoaderRun(type);
    public void OpenSender(object sender){try{host.Explorer(host.Entry(host.ResolveItem(sender)).File.FullName);}catch(Exception error){Logger.Error(error,"打开 Mod 文件位置失败");}}
    public void Open(TItem item){try{host.Explorer(host.Entry(item).File.FullName);}catch(Exception error){Logger.Error(error,"打开 Mod 文件位置失败");}}
    public void DeleteItem(TItem item)=>DeleteMods(new[]{host.Entry(item)});
    public void ToggleItem(TItem item)=>EDMods(new[]{host.Entry(item)},!host.Entry(item).IsEnabled);
    private sealed class Source : ILegacyBaseSearchSource {public string[] Aliases{get;}public double Weight{get;}public Source(string? text,double weight){Aliases=new[]{text!};Weight=weight;}}
    private sealed class SearchItem : ILegacyBaseSearchEntry<Source> {public TMod Item=null!;public List<Source> Sources{get;}=new();public double Similarity{get;set;}public bool AbsoluteRight{get;set;}}
    private static bool Same(string? a,string? b)=>string.Equals(a??"",b??"",StringComparison.Ordinal);
    public void SearchRun(){
        if(IsSearching){List<SearchItem> query=new();foreach(TMod mod in host.Output()!){SearchItem item=new(){Item=mod};item.Sources.Add(new(mod.Display,1));item.Sources.Add(new(mod.File.Name,1));if(mod.Version is not null)item.Sources.Add(new(mod.Version,.2));if(!string.IsNullOrEmpty(mod.Description))item.Sources.Add(new(mod.Description,.4));if(mod.Project is not null){if(!Same(mod.Project.RawName,mod.Display))item.Sources.Add(new(mod.Project.RawName,1));if(!Same(mod.Project.TranslatedName,mod.Project.RawName))item.Sources.Add(new(mod.Project.TranslatedName,1));if(!Same(mod.Project.Description,mod.Description))item.Sources.Add(new(mod.Project.Description,.4));item.Sources.Add(new(string.Join("",mod.Project.Tags),.2));}query.Add(item);}searchResult=LegacyBaseSearch.Search<SearchItem,Source>(query,view.Search(),6,.35).Select(item=>item.Item).ToList();}RefreshUI();
    }
}
