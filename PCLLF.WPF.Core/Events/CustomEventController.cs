using System;
using System.Diagnostics;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLCS;

// LF ModEvent.vb is the behavior source. Its nested VB enum remains the sole enum/DP type.
// CE stable Event/CustomEvent.cs is only a same-source dispatch-organization reference;
// CE's plain objects/custom-variable store do not replace LF dependency objects/Registry.
public sealed partial class CustomEventController<TEvent,TPage,TSubPage,TInstance,THint>
    where TEvent : struct,Enum where TPage : struct,Enum where TSubPage : struct,Enum where TInstance : class where THint : struct,Enum {
    private readonly CustomEventHost<TPage,TSubPage,TInstance,THint> host;
    public CustomEventController(CustomEventHost<TPage,TSubPage,TInstance,THint> host) { this.host=host; }
    public void Raise(TEvent type,string? argument) {
        int code=Convert.ToInt32(type);if(code==0)return;
        host.Info($"执行自定义事件：{type}, {argument}");
        string arg=argument??"";string[] args=arg.Split('|');string first=arg.BeforeFirst("|"),rest=arg.AfterFirst("|");
        try {
            switch(code) {
                case 1:
                    arg=arg.Replace('\\','/');
                    if(!arg.Contains("://")||arg.StartsWithF("file",true)){Dialog("EventData 必须为一个网址。\r\n如果想要启动程序，请将 EventType 改为 打开文件。","事件执行失败");return;}
                    Hint("正在开启，请稍候："+arg);host.RunInThread(()=>host.OpenWebsite(arg));break;
                case 2:case 3:case 4:
                    host.RunInThread(()=> {
                        try {
                            string[] paths=GetAbsoluteUrls(first,type);string location=paths[0],working=paths[1];host.Info($"打开类自定义事件实际路径：{location}，工作目录：{working}");
                            if(code==3)host.EnterHelp(location);
                            else { if(!SafetyConfirm("即将执行："+location+(args.Length>=2?" "+rest:"")))return;host.StartProcess(new ProcessStartInfo{UseShellExecute=true,Arguments=args.Length>=2?rest:"",FileName=location,WorkingDirectory=working}); }
                        }catch(Exception ex){host.ErrorAlert(ex,"执行打开类自定义事件失败");}
                    });break;
                case 5:
                    if(first==@"\current") { if(host.SelectedInstance() is null){Hint("请先选择一个 Minecraft 版本！",host.Red);return;}first=host.InstanceName(host.SelectedInstance()!); }
                    host.RunInUi(()=> { if(host.Launch(host.CreateInstance(first),args.Length>=2?args[1]:null))Hint($"正在启动 {first}……"); });break;
                case 6:host.Clipboard(arg);break;
                case 7:host.RunInUi(()=>{host.RefreshHome();if(string.IsNullOrEmpty(arg))Hint("已刷新！",host.Green);});break;
                case 8:
                    if(host.CanRefreshCurrent()){host.RunInUiWait(host.RefreshCurrent);if(string.IsNullOrEmpty(arg))Hint("已刷新！",host.Green);}else Hint("当前页面不支持刷新操作！",host.Red);break;
                case 9:host.RunInUiWait(host.RefreshHelp);if(string.IsNullOrEmpty(arg))Hint("已刷新！",host.Green);break;
                case 10:host.Jrrp();break;
                case 11:host.RunInThread(host.MemoryOptimize);break;
                case 12:host.RunInThread(host.RubbishClear);break;
                case 13:
                    if(args.Length==1)throw new Exception($"EventType {type} 需要至少 2 个以 | 分割的参数，例如 弹窗标题|弹窗内容");
                    host.Message(rest.BeforeLast("|").Replace(@"\n","\r\n"),first.Replace(@"\n","\r\n"),args.Length>2?rest.AfterLast("|"):"确定","","");break;
                case 14:Hint(first.Replace(@"\n","\r\n"),args.Length==1?host.Blue:EnumUtils.FromString<THint>(rest));break;
                case 15:
                    host.RunInUi(()=> {
                        TPage page=EnumUtils.FromString<TPage>(first);
                        if(page.Equals(host.InstanceSetup)) {
                            if(host.SelectedInstance() is null){Hint("请先选择一个 Minecraft 版本！",host.Red);return;}
                            if(host.Locked(host.SelectedInstance()!)){Hint("该版本正在被修改，暂时无法进入版本设置！",host.Red);return;}
                            host.SetEditedInstance(host.SelectedInstance()!);
                        }
                        host.ChangePage(page,args.Length==1?host.DefaultSubPage:EnumUtils.FromString<TSubPage>(rest));
                    });break;
                case 16:case 17:host.RunInUi(host.InstallModpack);break;
                case 18:
                    first=first.Replace('\\','/');
                    if(!(first.StartsWithF("http://",true)||first.StartsWithF("https://",true))){Dialog("EventData 必须为以 http:// 或 https:// 开头的网址。\r\nPCL 不支持其他乱七八糟的下载协议。","事件执行失败");return;}
                    if(!SafetyConfirm("即将从该网址下载文件：\r\n"+first))return;
                    host.RunInUi(()=>{try{if(args.Length==1)host.Download(first,PathUtils.GetLastPart(first),null);else if(args.Length==2)host.Download(first,rest,null);else host.Download(first,args[1],args[2]);}catch{host.Download(first,"未知",null);}});break;
                case 19:case 20:
                    if(args.Length==1)throw new Exception($"EventType {type} 需要至少 2 个以 | 分割的参数，例如 UiLauncherTransparent|400");
                    host.SetSetting(first,args[1],host.SelectedInstance());if(args.Length==2)Hint($"已写入设置：{first} → {args[1]}",host.Green);break;
                case 21:case 22:
                    if(args.Length==1)throw new Exception($"EventType {type} 需要至少 2 个以 | 分割的参数，例如 VariableName|SomeValue");
                    try{host.WriteRegistry($@"HKEY_CURRENT_USER\Software\{host.RegistryFolder()}","CustomEvent"+first,args[1]);if(args.Length==2)Hint($"已写入变量：{first} → {args[1]}",host.Green);}catch(Exception ex){host.WarnToast(ex,$"写入变量失败：{first} → {args[1]}");}break;
                case 23:
                    if(host.LegacyLink)host.RunInUi(()=>{if(string.IsNullOrWhiteSpace(arg))host.JoinDefault!();else{if(!SafetyConfirm("即将加入第三方联机房间。"))return;host.Join!(arg);}});
                    else Hint("旧联机模块暂未参与编译，当前版本无法加入联机房间。");break;
                case 24:host.CheckUpdates();break;
                default:Dialog("未知的事件类型："+Conversions.ToString(code)+"\r\n请检查事件类型填写是否正确，或者 PCL 是否为最新版本。","事件执行失败");break;
            }
        }catch(Exception ex){host.ErrorAlert(ex,$"事件执行失败（{type}, {arg}）");}
    }
    private void Hint(string text)=>host.Hint(text,host.Blue);
    private void Hint(string text,THint kind)=>host.Hint(text,kind);
    private int Dialog(string text,string title)=>host.Message(text,title,"确定","","");
    public bool SafetyConfirm(string message) {
        if(host.HintCustomCommand())return true;
        switch(host.Message(message+"\r\n请在确认没有安全隐患后再继续。","执行确认","继续","继续且今后不再要求确认","取消")){case 1:return true;case 2:host.RememberCustomCommand();return true;default:return false;}
    }
}
