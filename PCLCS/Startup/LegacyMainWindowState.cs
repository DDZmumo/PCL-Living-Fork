using System;
using MeloongCore;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLCS.Startup;

// Portable LF window state/equality and the original statement-by-statement forced-exit behavior.
public enum LegacyMainPage { Launch, Download, Link, Setup, Other, InstanceSelect, TaskManager, InstanceSetup, ResourceDetail, HelpDetail, Favorites = 10, Schematics = 11 }
public interface ILegacyMainPageStack { int PageCode { get; set; } object? Additional { get; } }
public static class LegacyMainPageIdentity {
    public static bool Equals<TStack>(TStack self, object? other) where TStack : class, ILegacyMainPageStack {
        if (other is null) return false;
        if (other is TStack value) {
            if (self.PageCode != value.PageCode) return false;
            return self.Additional is null ? value.Additional is null : value.Additional is not null && self.Additional.Equals(value.Additional);
        }
        if (other is int number) return self.PageCode == number && self.Additional is null;
        return false;
    }
}
public sealed class LegacyMainTerminationHost {
    public Action MarkEnding = null!, DisableAnimations = null!, Feedback = null!, OpenLog = null!, SaveAll = null!, FlushLog = null!, Kill = null!;
    public Func<bool> LogShown = null!;
    public Action<bool> WriteLogShown = null!;
    public Func<string> IssuesUrl = null!;
    public Action<int> Exit = null!;
    public Func<int, string> DescribeCode = null!;
}

public static class LegacyMainTermination {
    // Each failed statement resumes at the next one, matching the original On Error Resume Next;
    // a broad catch around the method would incorrectly skip later saves/flush/exit/kill actions.
    private static void ResumeNext(Action action) { try { action(); } catch (Exception ex) { ProjectData.SetProjectError(ex); } }
    public static bool ResumeNextCondition(Func<bool> condition) { try { return condition(); } catch (Exception ex) { ProjectData.SetProjectError(ex); return true; } }
    public static void EndProgramForce(int returnCode, LegacyMainTerminationHost host) {
        ProjectData.ClearProjectError();
        ResumeNext(host.MarkEnding); ResumeNext(host.DisableAnimations);
        if (returnCode == 2) { // LF ProcessReturnValues.Exception; Fail is 1.
            if (ResumeNextCondition(() => !host.LogShown())) {
                ResumeNext(host.Feedback); ResumeNext(() => Logger.Info($"请在 {host.IssuesUrl()} 提交启动器错误报告，以便于本项目维护者排查。"));
                ResumeNext(() => host.WriteLogShown(true)); ResumeNext(host.OpenLog);
            }
            ResumeNext(() => System.Threading.Thread.Sleep(500));
        }
        ResumeNext(() => Logger.Info($"程序已退出，返回值：{host.DescribeCode(returnCode)}")); ResumeNext(host.SaveAll); ResumeNext(host.FlushLog);
        if (returnCode != 0) ResumeNext(() => host.Exit(returnCode)); ResumeNext(host.Kill);
    }
}
