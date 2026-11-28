// SPDX-License-Identifier: MPL-2.0
using System;
using System.Threading;
using MeloongCore;
using PCLCS;

namespace PCL;

internal static class LauncherInstanceDeletion
{
    internal static IWorker Begin(ModMinecraft.McInstance instance, bool recycle, Action completed)
    {
        string path = instance.PathVersion;
        string name = instance.Name;
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        return InstanceDeletionOperation.Start(
            () => InstanceFolderDeletion.Delete(path, recycle, RecycleInstanceFolder),
            worker => ModMinecraft.McInstanceLock.TryAcquire(worker, path),
            worker => ModMinecraft.McInstanceLock.Release(worker, path),
            work => dispatcher.InvokeAsync(work),
            () => {
                try { completed(); }
                catch (Exception error) { Logger.Error(error, "删除完成后刷新版本列表失败", LogBehavior.Alert); }
            },
            error => Logger.Error(error, "删除版本 " + name + " 失败", LogBehavior.Alert),
            () => ModMain.Hint("已取消删除版本 " + name + "。", ModMain.HintType.Blue));
    }
    private static void RecycleInstanceFolder(string path)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo failure = null;
        void Recycle()
        {
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException); }
            catch (Exception error) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
        }
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) Recycle();
        else
        {
            var thread = new Thread(Recycle) { IsBackground = true, Name = "Recycle Minecraft Instance" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        }
        failure?.Throw();
    }
    internal static void Start(ModMinecraft.McInstance instance, bool recycle, Action completed)
    {
        if (Begin(instance, recycle, completed) is null)
            ModMain.Hint("该版本正在被修改，请稍后再删除！", ModMain.HintType.Red);
        else ModMain.Hint("正在删除版本 " + instance.Name + "……", ModMain.HintType.Blue);
    }
}
