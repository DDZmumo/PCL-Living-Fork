using System;
using System.IO;
using MeloongCore;
using MeloongCore.Extensions;
namespace PCLCS;
public sealed partial class CustomEventController<TEvent,TPage,TSubPage,TInstance,THint>
    where TEvent : struct,Enum where TPage : struct,Enum where TSubPage : struct,Enum where TInstance : class where THint : struct,Enum {
    public string[] GetAbsoluteUrls(string relative,TEvent type) {
        if(relative.StartsWithF("http",true)) {
            if(host.IsUiThread())throw new Exception("能打开联网帮助页面的 MyListItem 必须手动设置 Title、Info 属性！");
            string name;
            try{name=PathUtils.GetLastPart(relative);if(PathUtils.GetExtension(name)!="json")throw new Exception("未指向 .json 后缀的文件");}
            catch(Exception ex){throw new Exception("联网帮助页面须指向一个帮助 JSON 文件，并在同路径下包含相应 XAML 文件！\r\n例如：\r\n - https://www.baidu.com/test.json（填写这个路径）\r\n - https://www.baidu.com/test.xaml（同时也需要包含这个文件）",ex);}
            string local=host.RequestTempFolder()+name;host.Info($"转换网络资源：{relative} -> {local}");
            try{host.DownloadFile(relative,local);host.DownloadFile(relative.Replace(".json",".xaml"),local.Replace(".json",".xaml"));}
            catch(Exception ex){throw new Exception("下载指定的文件失败！\r\n注意，联网帮助页面须指向一个帮助 JSON 文件，并在同路径下包含相应 XAML 文件！\r\n例如：\r\n - https://www.baidu.com/test.json（填写这个路径）\r\n - https://www.baidu.com/test.xaml（同时也需要包含这个文件）",ex);}
            relative=local;
        }
        relative=relative.Replace('/','\\').ToLowerInvariant().TrimStart('\\');
        string location,working=host.BaseFolder()+"PCL";host.ExtractHelp();int code=Convert.ToInt32(type);
        if(relative.Contains(":\\")){location=relative;host.Info($"自定义事件中由绝对路径{type}：{location}");}
        else if(host.FileExists(host.BaseFolder()+@"PCL\"+relative)){location=host.BaseFolder()+@"PCL\"+relative;host.Info($"自定义事件中由相对 PCL 文件夹的路径{type}：{location}");}
        else if(host.FileExists(host.BaseFolder()+@"PCL\Help\"+relative)){location=host.BaseFolder()+@"PCL\Help\"+relative;working=host.BaseFolder()+@"PCL\Help\";host.Info($"自定义事件中由相对 PCL 本地帮助文件夹的路径{type}：{location}");}
        else if(code==3&&host.FileExists(host.CacheFolder()+@"Help\"+relative)){location=host.CacheFolder()+@"Help\"+relative;working=host.CacheFolder()+@"Help\";host.Info($"自定义事件中由相对 PCL 自带帮助文件夹的路径{type}：{location}");}
        else if(code==2||code==4){location=relative;host.Info($"自定义事件中直接{type}：{location}");}
        else throw new FileNotFoundException("未找到 EventData 指向的本地 xaml 文件："+relative,relative);
        return new[]{location,working};
    }
}
