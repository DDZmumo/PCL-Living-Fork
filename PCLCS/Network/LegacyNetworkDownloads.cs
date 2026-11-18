using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeloongCore;
using MeloongCore.Extensions;
using PCLCS.Loaders;

namespace PCLCS.Network;
public sealed partial class LegacyNetworkRuntime<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState>
    where TSource : LegacyNetSourceData<TThread>, new()
    where TThread : LegacyNetThreadData<TFile, TSource, TThread, TNetState>, new()
    where TFile : LegacyNetFile<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState> where TDownload : TLoader, ILegacyNetworkDownload<TFile> where TManager : LegacyNetManager<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState> where TLoader : LegacyLoaderBase<TLoader, TLoadState, TLoadingState> where TNetState : struct, Enum where TLoadState : struct, Enum
{
    private static int NetCode(TNetState value) => Convert.ToInt32(value);
    private static TNetState NetStateValue(int value) => (TNetState)Enum.ToObject(typeof(TNetState), value);
    private static int LoadCode(TLoadState value) => Convert.ToInt32(value);
    private static TLoadState LoadStateValue(int value) => (TLoadState)Enum.ToObject(typeof(TLoadState), value);
    public double DownloadProgress(TDownload owner) => LoadCode(owner.State) >= 2 ? 1 : !owner.CoreFiles.Any() ? 0 : owner.CoreRecord.Progress;
    public static void RejectDownloadProgress(double value) => throw new Exception("文件下载不允许指定进度");
    public int DownloadFailCount(TDownload owner) => owner.CoreRecord.FailCount;
    public void SetDownloadFailCount(TDownload owner, int value)
    {
        LegacyNetworkDownloadRecord record = owner.CoreRecord;
        record.FailCount = value;
        if (LoadCode(owner.State) == 1 && value >= Math.Min(10000, Math.Max(checked(record.FileRemain * 8), checked(NetTaskThreadLimit * 8 + 3))))
        {
            Logger.Error($"由于同加载器中失败次数过多引发强制失败：连续失败了 {value} 次", LogBehavior.ToastIfDebug);
            // Original On Error Resume Next begins after the log. Keep failures
            // local to the failing statement/iteration, including the OnFail call.
            var errors = new List<Exception>();
            try
            {
                foreach (TFile file in owner.CoreFiles)
                {
                    try
                    {
                        foreach (TSource source in file.Sources)
                        {
                            try
                            {
                                if (source.Ex is not null)
                                    errors.Add(source.Ex);
                            }
                            catch
                            {
                            }

                            if (errors.Count > 10)
                                goto FinishedCollecting;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            FinishedCollecting:
                try
                {
                    OnDownloadFail(owner, errors);
                }
                catch
                {
                }
        }
    }

    public void RefreshDownloadStat(TDownload owner)
    {
        double progress = 0, total = 0;
        foreach (TFile file in owner.CoreFiles)
        {
            if (file.IsCopy)
            {
                progress += file.Progress * 0.2;
                total += 0.2;
            }
            else
            {
                progress += file.Progress;
                total += 1;
            }
        }

        if (total > 0 && !double.IsNaN(total))
            progress /= total;
        owner.CoreRecord.Progress = progress;
    }

    public void InitializeDownload(TDownload owner, string name, IEnumerable<TFile> files)
    {
        owner.Name = name;
        owner.CoreFiles = new ConcurrentQueue<TFile>(files);
    }

    public void StartDownload(TDownload owner, object? Input = null, bool IsForceRestart = false)
    {
        IDisposable? startWork;
        lock (owner.LockState) {
            if (LoadCode(owner.State) == 1) {
                if (!IsForceRestart && Input is null) return;
                throw new InvalidOperationException("Cancel and drain the active download operation before restarting it.");
            }
            if (owner.CoreRecord.IoWork.Active != 0) throw new InvalidOperationException("Previous download workers have not drained.");
            if (LoadCode(owner.State) >= 2 && owner.CoreFiles.Any(file => NetCode(file.State) < 5 || !file.IoWork.IsSealed || file.IoWork.Active != 0))
                throw new InvalidOperationException("Previous download target writers have not reached their terminal completion barrier.");
            var inputs = Input is null ? owner.CoreFiles.ToArray() : ((IEnumerable<TFile>)Input).ToArray();
            if (inputs.Any(file => file is null || file.IoWork.IsSealed))
                throw new InvalidOperationException("NetFile instances are single-operation inputs; create fresh files after terminal download/drain.");
            owner.CoreFiles = new ConcurrentQueue<TFile>(MeloongCore.Extensions.EnumerableExtensions.DistinctBy(inputs, file => PathUtils.ForCompare(file.LocalPath).Lower()));
            lock (owner.CoreRecord.FileRemainLock) owner.CoreRecord.FileRemain = owner.CoreFiles.Count;
            lock (owner.CoreRecord.CandidateInitialization) owner.CoreRecord.CandidateSizes = null;
            owner.CoreRecord.FailCount = 0; owner.CoreRecord.Progress = 0;
            startWork = owner.CoreRecord.IoWork.Enter() ?? throw new InvalidOperationException("Download operation has closed registration.");
            try { owner.State = LoadStateValue(1); }
            catch { startWork.Dispose(); throw; }
        }
        try {
        RunDownloadWorker(owner, () =>
        {
            try
            {
                if (!owner.CoreFiles.Any())
                {
                    OnDownloadFinish(owner);
                    return;
                }

                foreach (TFile file in owner.CoreFiles)
                {
                    if (file is null)
                        throw new ArgumentException("存在空文件请求！");
                    foreach (TSource source in file.Sources)
                    {
                        if (!(source.Url.StartsWithF("https://", true) || source.Url.StartsWithF("http://", true)))
                        {
                            source.Ex = new ArgumentException("输入的下载链接不正确：" + source.Url);
                            source.IsFailed = true;
                        }
                    }

                    if (!file.HasAvailableSource())
                        throw new ArgumentException("输入的下载链接不正确！");
                    file.LocalPath = file.LocalPath.Replace("/", "\\");
                    if (!file.LocalPath.Contains(":\\"))
                        throw new ArgumentException("输入的本地文件地址不正确：" + file.LocalPath);
                    if (file.LocalPath.EndsWithF("\\"))
                        throw new ArgumentException("请输入含文件名的完整文件路径：" + file.LocalPath);
                    DirectoryUtils.Create(PathUtils.RemoveLastPart(file.LocalPath));
                }

                List<TFile> newFiles = Host.Manager().Start(owner);
                var filesToCheck = new List<TFile>();
                foreach (TFile file in newFiles)
                {
                    if (file.Check is not null)
                        filesToCheck.Add(file);
                    else
                    {
                        lock (file.LockState)
                        {
                            if (!file.IoWork.IsSealed) file.State = NetStateValue(0);
                            file.IsCopy = false;
                        }
                    }
                }

                if (!filesToCheck.Any())
                    return;
                var folders = new List<string>
                {
                    Host.AppData() + ".minecraft\\"
                };
                folders.AddRange(Host.MinecraftFolders());
                folders = folders.Distinct().Where(folder => DirectoryUtils.Exists(folder)).ToList();
                int threadCount = (filesToCheck.Count / 40).Clamp(1, 8);
                if (threadCount == 1)
                    CheckExistingFiles(owner, filesToCheck, folders);
                else
                {
                    int baseSize = filesToCheck.Count / threadCount;
                    int remainder = filesToCheck.Count % threadCount;
                    int index = 0;
                    for (int i = 0; i < threadCount; i = checked(i + 1))
                    {
                        int size = checked(baseSize + (i < remainder ? 1 : 0));
                        List<TFile> threadFiles = filesToCheck.GetRange(index, size);
                        index = checked(index + size);
                        RunDownloadWorker(owner, () => CheckExistingFiles(owner, threadFiles, folders), $"下载 文件复制 {owner.Uuid}/{Host.NewUuid()}", ThreadPriority.Normal);
                    }
                }
            }
            catch (Exception error)
            {
                OnDownloadFail(owner, new List<Exception> { new Exception("下载初始化失败", error) });
            }
        }, "L/下载 " + owner.Uuid, ThreadPriority.Normal, startWork);
        startWork = null;
        } finally { startWork?.Dispose(); }
    }

    public void CheckExistingFiles(TDownload owner, List<TFile> files, List<string> folders)
    {
        try
        {
            Logger.Trace(() => $"文件检查开始，本线程负责 {files.Count} 个文件，首个文件为 {files.FirstOrDefault()?.LocalName}");
            var versionFolders = new List<string>();
            foreach (string folder in folders)
            {
                foreach (string versionFolder in DirectoryUtils.EnumerateDirectories(folder + "versions\\"))
                    versionFolders.Add(PathUtils.AddSlashSuffix(versionFolder));
            }

            foreach (TFile file in files)
            {
                using IDisposable? io = file.EnterCopyIo();
                if (io is null) continue;
                string? target = CheckExistingFile(owner, folders, versionFolders, file);
                Logger.Trace(() => $"本地文件匹配：{file.LocalName} → {target ?? "无匹配"}，当前状态 {file.State}");
                if (NetCode(file.State) >= 0)
                    continue;
                if (target is not null)
                {
                    file.IsCopy = true;
                    try
                    {
                        FileUtils.Copy(target, file.LocalPath);
                        file.Finish(false);
                        continue;
                    }
                    catch
                    {
                    }
                }

                lock (file.LockState)
                {
                    if (!file.IoWork.IsSealed) file.State = NetStateValue(0);
                    file.IsCopy = false;
                }
            }
        }
        catch (Exception error)
        {
            OnDownloadFail(owner, new List<Exception> { new Exception("下载已存在文件查找失败", error) });
        }
    }

    public string? CheckExistingFile(TDownload owner, List<string> folders, List<string> versionFolders, TFile file)
    {
        if ((!FileUtils.Exists(file.LocalPath) || FileUtils.GetInfo(file.LocalPath).Length <= file.MaximumSizeBytes) && file.Check.Check(file.LocalPath)is null)
            return file.LocalPath;
        // Cross-directory FileUtils.Copy has no streaming upper bound. Finite-limit requests
        // use the ordinary bounded transfer path instead of this optional copy shortcut.
        if (file.MaximumSizeBytes != long.MaxValue) return null;
        if (Host.BooleanSetting("SystemDebugSkipCopy"))
            return null;
        if (file.Check.Hash is null && file.Check.ActualSize < 0)
            return null;
        var typeIndexes = new[]
        {
            "\\assets\\",
            "\\libraries\\",
            "\\versions\\",
            "\\mods\\",
            "\\coremods\\",
            "\\lib\\",
            "\\resourcepacks\\",
            "\\texturepacks\\",
            "\\shaderpacks\\"
        }.Select(folder => (Folder: folder, Index: file.LocalPath.IndexOfF(folder, true))).Where(item => item.Index >= 0).ToList();
        if (!typeIndexes.Any())
        {
            if (PathUtils.GetExtension(file.LocalName) == "jar")
                typeIndexes.Add(("\\versions\\", 1));
            else
                return null;
        }

        string type = MeloongCore.Extensions.EnumerableExtensions.MaxBy(typeIndexes, item => item.Index).Folder.TrimStart('\\');
        LegacyNetworkDownloadRecord record = owner.CoreRecord;
        ConcurrentDictionary<string, long> sizes;
        lock (record.CandidateInitialization)
            sizes = record.CandidateSizes ??= new ConcurrentDictionary<string, long>();
        bool CheckCandidate(string candidate, FileChecker checker)
        {
            long length = sizes.GetOrAdd(candidate, path => FileUtils.Exists(path) ? FileUtils.GetInfo(path).Length : -1);
            if (length > file.MaximumSizeBytes) return false;
            if (checker.ActualSize != length)
                return false;
            return checker.Check(candidate)is null;
        }

        switch (type)
        {
            case "assets\\":
            case "libraries\\":
                foreach (string folder in folders)
                {
                    string candidate = folder + type + file.LocalPath.AfterFirst(type);
                    if (CheckCandidate(candidate, file.Check))
                        return candidate;
                }

                return null;
            case "versions\\":
                foreach (string version in versionFolders)
                {
                    foreach (string candidate in DirectoryUtils.EnumerateFiles(version, searchPattern: "*." + PathUtils.GetExtension(file.LocalPath)))
                    {
                        if (CheckCandidate(candidate, file.Check))
                            return candidate;
                    }
                }

                break;
            default:
                if (file.Check.ActualSize < 0 || file.Check.Hash is null)
                    return null;
                foreach (string folder in folders.Concat(versionFolders))
                {
                    foreach (string candidate in DirectoryUtils.EnumerateFiles(folder + type))
                    {
                        if (CheckCandidate(candidate, file.Check))
                            return candidate;
                    }
                }

                break;
        }

        return null;
    }

    public void OnDownloadFileFinish(TDownload owner, TFile file)
    {
        lock (owner.CoreRecord.FileRemainLock)
        {
            owner.CoreRecord.FileRemain = checked(owner.CoreRecord.FileRemain - 1);
            if (owner.CoreRecord.FileRemain > 0)
                return;
        }

        OnDownloadFinish(owner);
    }

    public void OnDownloadFinish(TDownload owner)
    {
        owner.CorePreviewFinish();
        lock (owner.LockState)
        {
            if (LoadCode(owner.State) > 1)
                return;
            owner.State = LoadStateValue(2);
        }
    }

    public void OnDownloadFileFail(TDownload owner, TFile file)
    {
        foreach (TSource source in file.Sources)
            if (source.Ex is not null)
                file.Ex.Add(source.Ex);
        OnDownloadFail(owner, file.Ex);
    }

    public void OnDownloadFail(TDownload owner, List<Exception>? errors)
    {
        lock (owner.LockState)
        {
            if (LoadCode(owner.State) >= 2)
                return;
            if (errors is null || !errors.Any())
                errors = new List<Exception>
                {
                    new Exception("未知错误！")
                };
            List<Exception> useful = errors.Where(error => !error.IsCanceled() && error is not TimeoutException).ToList();
            owner.Error = useful.FirstOrDefault() ?? errors.FirstOrDefault();
            foreach (TFile file in owner.CoreFiles)
            {
                if (NetCode(file.State) != 6)
                    continue;
                if (file.Sources.All(source => source.Ex is not null && (source.Ex.IsCanceled() || source.Ex is TimeoutException)))
                    continue;
                string detail = string.Join("\r\n", file.Sources.Select(source => $"{(source.Ex is null ? "无错误信息。" : source.Ex.GetDisplay(false))}（{source.Url}）"));
                owner.Error = new Exception("文件下载失败：" + file.LocalPath + "\r\n各下载源的错误如下：\r\n" + detail, owner.Error);
                break;
            }

            owner.State = LoadStateValue(3);
        }

        foreach (TFile file in owner.CoreFiles)
            file.Cancel(owner);
        Logger.Error(new AggregateException(errors), "下载加载器失败", LogBehavior.None);
    }

    public void FailDownload(TDownload owner, Exception error) => OnDownloadFail(owner, new List<Exception> { error });
    public void CancelDownload(TDownload owner)
    {
        lock (owner.LockState)
        {
            if (LoadCode(owner.State) >= 2)
                return;
            owner.State = LoadStateValue(4);
        }

        Logger.Info($"{owner.Name} 已取消！");
        foreach (TFile file in owner.CoreFiles)
            file.Cancel(owner);
    }
}
