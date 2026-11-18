using System;
using System.Collections.Generic;
using System.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;

namespace PCLCS;

/// <summary>LF's existing Java settings, worker, loader and UI services; no instance state is copied.</summary>
public sealed class LegacyJavaHost<TInstance> where TInstance : class {
    public Func<string, TInstance?, string> ReadString = null!;
    public Func<string, TInstance, int> ReadInteger = null!;
    public Action<string, int, TInstance> WriteInteger = null!;
    public Func<TInstance, IConfigProvider> Config = null!;
    public Func<TInstance, McVersion> Version = null!;
    public Func<TInstance, string> VersionPath = null!;
    public Func<TInstance, string> InstanceName = null!;
    public Func<TInstance, string> VersionDisplayName = null!;
    public Func<TInstance, DateTime> ReleaseTime = null!;
    public Func<TInstance, JObject?> JsonObject = null!;
    public Func<TInstance, JObject?> JsonVersion = null!;
    public Func<bool> IsNideLogin = null!;
    // Read the original mutable WithEvents worker at the time of each operation.
    public Func<RedoableWorker> RefreshWorker = null!;
    public Action UpdateJavaLists = null!;
    public Func<string?> SelectJavaExecutable = null!;
    public Action<string, LegacyJavaHintKind> Hint = null!;
    public Action ReloadCurrentJava = null!;
    public Action<Action> RunInThread = null!;
    public Action<TInstance> SwitchToInstanceSetup = null!;
    public Action<string, string, bool, bool> Message = null!;
    public Action DisableJavaSettings = null!;
    public Action RestoreJavaSettings = null!;
    public Func<IEnumerable<string>, IEnumerable<string>, IEnumerable<string>> VersionListOrder = null!;
    public Func<IEnumerable<string>, IEnumerable<string>, IEnumerable<string>> SourceOrder = null!;
    public Func<IEnumerable<string>, string> RequestJson = null!;
    public Func<LegacyJavaDownloadPlan, LegacyJavaDownloadOperation> CreateDownload = null!;
}

public enum LegacyJavaHintKind { Red, Green }

/// <summary>The original Mojang windows-x64 component and its manifest entry.</summary>
public sealed class LegacyJavaRuntime {
    public string ComponentName = null!;
    public JObject Java = null!;
    public Version JavaVersion = null!;
}

/// <summary>One original raw manifest download, retaining source order, size and SHA-1 checks.</summary>
public sealed class LegacyJavaDownloadFile {
    public IEnumerable<string> Sources = null!;
    public string Destination = null!;
    public long ActualSize;
    public string Hash = null!;
}

public sealed class LegacyJavaDownloadPlan {
    public string BaseDirectory = null!;
    public List<LegacyJavaDownloadFile> Files = [];

    /// <summary>Cleanup after download writers have drained and while the Java maintenance lease is held.</summary>
    public void Incomplete() {
        Logger.Warn($"由于 Java 下载未完成，清理未下载完成的 Java 文件夹：{BaseDirectory}");
        DirectoryUtils.Delete(BaseDirectory);
    }
}

/// <summary>Primitive operations on the original LoaderCombo; cancellation remains unconditional after waiting.</summary>
public sealed class LegacyJavaDownloadOperation {
    public Action<CancellationToken, ProgressProvider?> Wait = null!;
    public Action Cancel = null!;
}
