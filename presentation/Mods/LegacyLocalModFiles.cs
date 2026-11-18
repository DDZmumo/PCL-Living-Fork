using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using PCLCS;

namespace PCLLF.Presentation.Mods;

internal sealed class ModUpdateDependencyException(string message) : IOException(message);

// Real PageInstanceMod update stages, separated from WPF binding so the same
// production plan/replacement code can be exercised with owned files.
public sealed class LegacyLocalModFiles<TMod,TProject,TVersion,TDownload>
    where TMod : LocalResourceFileData<TProject,TVersion> where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData where TDownload : class {
    private readonly object fileGate = new();
    private readonly string stage;
    private readonly HashSet<string> originals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> enabledReplacements = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<TDownload> Files=new();
    private readonly Dictionary<string,string> copies = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> FinishedFileNames=new();
    public IReadOnlyDictionary<string, string> EnabledReplacements => enabledReplacements;

    public LegacyLocalModFiles(IEnumerable<TMod> mods,Func<string> tempPath,Func<TVersion,string,TDownload> toNetFile){
        stage = Path.Combine(tempPath(), "mods", Guid.NewGuid().ToString("N"));
        foreach(TMod entry in mods){
            TVersion version=entry.UpdateFile!;if(!version.DownloadAvailable)continue;
            string current=PathUtils.GetFileNameWithoutExtension(entry.ProjectVersion!.FileName!),newest=PathUtils.GetFileNameWithoutExtension(entry.UpdateFile!.FileName!);
            List<string> currentParts=current.Split('-').ToList(),newParts=newest.Split('-').ToList();bool shortened=false;
            while(true){if(!currentParts.Any()||!newParts.Any())break;if(currentParts.First()!=newParts.First())break;currentParts.RemoveAt(0);newParts.RemoveAt(0);shortened=true;}
            while(true){if(!currentParts.Any()||!newParts.Any())break;if(currentParts.Last()!=newParts.Last())break;currentParts.RemoveAt(currentParts.Count-1);newParts.RemoveAt(newParts.Count-1);shortened=true;}
            if(shortened&&currentParts.Any()&&newParts.Any()){current=string.Join("-",currentParts);newest=string.Join("-",newParts);}
            string name = entry.File.Name.Replace(current, newest);
            string staged = Path.Combine(stage, name);
            string original = entry.File.FullName;
            string destination = Path.Combine(PathUtils.RemoveLastPart(original), name);
            if (!originals.Add(original) || !copies.TryAdd(staged, destination))
                throw new IOException("本批更新包含重复目标文件：" + name);
            if (entry.IsEnabled) enabledReplacements.Add(original, staged);
            Files.Add(toNetFile(version, staged));
        }
    }

    public void Replace(Action checkDependencies, Action captureHistory, CancellationToken token) {
        lock (fileGate) {
            token.ThrowIfCancellationRequested();
            if (copies.Keys.Any(path => !File.Exists(path)) || originals.Any(path => !File.Exists(path)))
                throw new IOException("更新前的 Mod 或暂存文件已变化，请重新检查更新。");
            checkDependencies();
            token.ThrowIfCancellationRequested();
            captureHistory();
            token.ThrowIfCancellationRequested();
            string? occupied = copies.Values.FirstOrDefault(path => Directory.Exists(path) ||
                (!originals.Contains(path) && File.Exists(path)));
            if (occupied is not null)
                throw new IOException("已有同名文件，已保留原有 Mod：" + Path.GetFileName(occupied));
            if (copies.Keys.Any(path => !File.Exists(path)) || originals.Any(path => !File.Exists(path)))
                throw new IOException("更新前的 Mod 或暂存文件已变化，请重新检查更新。");
            foreach (string path in originals)
                FileUtils.Delete(path, true);
            foreach (var (source, target) in copies) {
                FileUtils.Move(source, target);
                FinishedFileNames.Add(Path.GetFileName(target));
            }
        }
    }

    public void Clean() {
        lock (fileGate) {
            try {
                foreach (string file in copies.Keys) FileUtils.Delete(file);
                if (Directory.Exists(stage)) Directory.Delete(stage);
            } catch (Exception error) { Logger.Warn(error, "清理 Mod 更新缓存失败"); }
        }
    }
}
public static class LegacyLocalModText {
    public static string Compare<TMod,TProject,TVersion>(TMod entry,Func<TimeSpan,bool,string> formatTime)
        where TMod : LocalResourceFileData<TProject,TVersion> where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData {
        string current=(entry.ProjectVersion!.Version??entry.ProjectVersion.Display)!.Replace(".jar","");string newest=(entry.UpdateFile!.Version??entry.UpdateFile.Display)!.Replace(".jar","");
        List<string> currentSegments=current.Split("-_+ ".ToCharArray()).ToList(),newSegments=newest.Split("-_+ ".ToCharArray()).ToList();bool shortened=false;
        foreach(string segment in currentSegments.ToList()){if(!newSegments.Contains(segment))continue;currentSegments.Remove(segment);newSegments.Remove(segment);shortened=true;}
        if(shortened&&currentSegments.Any()&&newSegments.Any()){current=string.Join("-",currentSegments);newest=string.Join("-",newSegments);entry._Version=current;}
        return $"当前版本：{current}（{formatTime(entry.ProjectVersion.ReleaseDate-DateTime.Now,false)}）\r\n最新版本：{newest}（{formatTime(entry.UpdateFile.ReleaseDate-DateTime.Now,false)}）";
    }
}
