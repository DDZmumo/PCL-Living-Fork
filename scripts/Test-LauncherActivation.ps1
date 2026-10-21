param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# 只创建随机标题的隐藏测试窗口，不启动、查找或聚焦用户的启动器。
$ErrorActionPreference='Stop'
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
public static class LivingPclActivationTests {
    [DllImport("user32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    static extern IntPtr CreateWindowExW(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", ExactSpelling=true)]
    static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", ExactSpelling=true)]
    static extern IntPtr FindWindowA(IntPtr cls, IntPtr title);
    public static void Run(Assembly assembly) {
        Type type=assembly.GetType("PCL.LauncherWindowActivation",true);
        BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Static;
        MethodInfo request=type.GetMethod("RequestActivation",flags);
        MethodInfo lookup=type.GetMethod("FindWindowW",flags);
        string title="LivingPCL-Test-"+Guid.NewGuid().ToString("N")+"\u3000";
        IntPtr window=CreateWindowExW(0,"STATIC",title,0,0,0,1,1,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(window==IntPtr.Zero) throw new Exception("Hidden native fixture creation failed");
        try {
            if((IntPtr)lookup.Invoke(null,new object[]{null,title})!=window) throw new Exception("Unicode title lookup failed");
            byte[] ansi=Encoding.GetEncoding(1252).GetBytes(title+"\0");
            IntPtr buffer=Marshal.AllocHGlobal(ansi.Length);
            try {
                Marshal.Copy(ansi,0,buffer,ansi.Length);
                if(FindWindowA(IntPtr.Zero,buffer)!=IntPtr.Zero) throw new Exception("Expected the old ANSI lookup to miss this Unicode title");
            } finally { Marshal.FreeHGlobal(buffer); }
        } finally { if(!DestroyWindow(window)) throw new Exception("Native fixture cleanup failed"); }
        var order=new List<string>();
        int pauses=0;
        Func<string,IntPtr> find=delegate(string caption) {
            return caption=="Plain Craft Launcher\u3000" ? new IntPtr(17) : IntPtr.Zero;
        };
        Func<IntPtr,uint,bool> queue=delegate(IntPtr hwnd,uint message) {
            if(hwnd!=new IntPtr(17)||message!=6402) throw new Exception("Official activation protocol changed");
            order.Add("queue");return true;
        };
        Action<IntPtr> foreground=delegate(IntPtr hwnd) { order.Add("foreground"); };
        Action<int> pause=delegate(int ms) { if(ms!=200)throw new Exception("Retry interval changed");pauses++; };
        if(!(bool)request.Invoke(null,new object[]{find,queue,foreground,pause})||String.Join(",",order)!="queue,foreground"||pauses!=0) throw new Exception("Activation ordering failed");
        order.Clear();
        find=delegate(string caption) { return pauses==2&&caption=="LivingPCL\u3000"?new IntPtr(17):IntPtr.Zero; };
        if(!(bool)request.Invoke(null,new object[]{find,queue,foreground,pause})||pauses!=2) throw new Exception("Delayed window retry failed");
        pauses=0;order.Clear();int searches=0;
        find=delegate(string caption) { searches++;return IntPtr.Zero; };
        if((bool)request.Invoke(null,new object[]{find,queue,foreground,pause})||searches!=30||pauses!=9||order.Count!=0) throw new Exception("Missing window retries are not bounded");
        find=delegate(string caption) { return new IntPtr(17); };
        queue=delegate(IntPtr hwnd,uint message) { return false; };
        if((bool)request.Invoke(null,new object[]{find,queue,foreground,pause})) throw new Exception("Failed message submission was reported as success");
    }
}
'@
[LivingPclActivationTests]::Run($assembly)
Write-Output 'Launcher activation passed: Unicode native lookup, old ANSI regression, official 6402 protocol, delayed and bounded retries, failed message fallback; hidden fixture cleaned up.'
