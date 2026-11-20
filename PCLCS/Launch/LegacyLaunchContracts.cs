using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// Behavior source: LF ModLaunch.vb at 07dd3503b38b39095c8b92b40ae0454907387f9f.
// LF lineage starts at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983; the PCL baseline is
// 5ef81e490ae46b1100d61e33d0183094c7a80ca8. CE stable
// 1a7653127d578efdb2a9388999a3fdc67d9e49d5/Plain Craft Launcher 2/Modules/Minecraft/ModLaunch.cs
// supplies conversion/structure reference only. LF login, Java, arguments and lifecycle policies are retained.
public enum LegacyLaunchState { Waiting, Loading, Finished, Failed, Canceled }
public enum LegacyLaunchHint { Default, Red, Green, Blue }

public interface ILegacyLaunchOptions<TInstance> where TInstance : class {
    string ServerIp { get; set; }
    string SaveBatch { get; set; }
    TInstance Instance { get; set; }
    List<string> ExtraGameArgs { get; }
    string QuickPlayWorld { get; set; }
    PersonalOptionsSessionData PersonalOptions { get; set; }
    QuickPlaySessionData QuickPlay { get; set; }
    string EnvironmentPoint { get; set; }
}
public interface ILegacyLaunchLoginData { int TypeCode { get; } }
public interface ILegacyLaunchServerData : ILegacyLaunchLoginData {
    string UserName { get; } string Password { get; } string BaseUrl { get; }
    string Token { get; } string Description { get; } bool ForceReselectProfile { get; }
}
public static class AuthlibEndpoint {
    public const string CredentialServerSetting = "CacheAuthCredentialServer";
    public const string AuthPath = "/authserver";
    public static string ForServer(string? server) => (server ?? "").Trim().TrimEnd('/') + AuthPath;

    public static bool Same(string? left, string? right) =>
        TryParse(left, out Uri a) && TryParse(right, out Uri b) &&
        string.Equals(a.GetLeftPart(UriPartial.Authority), b.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);

    public static bool TryParse(string? address, out Uri uri) {
        if (Uri.TryCreate(address, UriKind.Absolute, out var candidate) && candidate.Scheme is "https" or "http" && candidate.Host.Length > 0 &&
            candidate.UserInfo.Length == 0 && candidate.Query.Length == 0 && candidate.Fragment.Length == 0) {
            uri = candidate;
            return true;
        }
        uri = null!;
        return false;
    }
}
public interface ILegacyLaunchMicrosoftData : ILegacyLaunchLoginData {
    string OAuthRefreshToken { get; } string AccessToken { get; }
    string Uuid { get; } string UserName { get; } string ProfileJson { get; }
}
public interface ILegacyLaunchOfflineData : ILegacyLaunchLoginData {
    string UserName { get; } int SkinType { get; } string SkinName { get; }
}
public interface ILegacyLaunchLoginResult {
    string Name { get; set; } string Uuid { get; set; } string AccessToken { get; set; }
    string Type { get; set; } string ClientToken { get; set; } string ProfileJson { get; set; }
}
public sealed class LegacyLaunchLoginResult : ILegacyLaunchLoginResult {
    public string Name { get; set; } = null!;
    public string Uuid { get; set; } = null!;
    public string AccessToken { get; set; } = null!;
    public string Type { get; set; } = null!;
    public string ClientToken { get; set; } = null!;
    public string ProfileJson { get; set; } = null!;
}

/// <summary>Live field access to the original McLoginResult structure. No second persistent login result is stored.</summary>
public sealed class LegacyLaunchLoginResultPort : ILegacyLaunchLoginResult {
    public Func<string> ReadName = null!;
    public Action<string> WriteName = null!;
    public string Name { get => ReadName(); set => WriteName(value); }
    public Func<string> ReadUuid = null!;
    public Action<string> WriteUuid = null!;
    public string Uuid { get => ReadUuid(); set => WriteUuid(value); }
    public Func<string> ReadAccessToken = null!;
    public Action<string> WriteAccessToken = null!;
    public string AccessToken { get => ReadAccessToken(); set => WriteAccessToken(value); }
    public Func<string> ReadType = null!;
    public Action<string> WriteType = null!;
    public string Type { get => ReadType(); set => WriteType(value); }
    public Func<string> ReadClientToken = null!;
    public Action<string> WriteClientToken = null!;
    public string ClientToken { get => ReadClientToken(); set => WriteClientToken(value); }
    public Func<string> ReadProfileJson = null!;
    public Action<string> WriteProfileJson = null!;
    public string ProfileJson { get => ReadProfileJson(); set => WriteProfileJson(value); }
}

/// <summary>Primitive live operations on the original loader. Source is an opaque legacy identity used only by its typed adapter.</summary>
public class LegacyLaunchTaskPort {
    public object Source = null!;
    public Func<LegacyLaunchState> ReadState = null!;
    public Action<LegacyLaunchState> WriteState = null!;
    public Func<Exception> ReadError = null!;
    public Func<bool> ReadCanceled = null!;
    public Func<bool> ReadForceRestarting = null!;
    public Func<Thread, bool> CanceledWithThread = null!;
    public Func<double> ReadProgress = null!;
    public Action<double> WriteProgress = null!;
    public Func<CancellationToken> CreateCancellationToken = null!;
    public Func<ProgressProvider> CreateSyncProgressProvider = null!;
    public Action Cancel = null!;
    public Action CancelParent = null!;
    public LegacyLaunchState State { get => ReadState(); set => WriteState(value); }
    public Exception Error => ReadError();
    public bool IsCanceled => ReadCanceled();
    public bool IsForceRestarting => ReadForceRestarting();
    public bool IsCanceledWithThread(Thread thread) => CanceledWithThread(thread);
    public double Progress { get => ReadProgress(); set => WriteProgress(value); }
}
public sealed class LegacyLaunchTask<TInput, TOutput> : LegacyLaunchTaskPort {
    public Func<TInput> ReadInput = null!;
    public Func<TOutput> ReadOutput = null!;
    public Action<TOutput> WriteOutput = null!;
    public Action<TInput, bool> StartWithInput = null!;
    public Action<TInput, LegacyLaunchTaskPort?, bool, CancellationToken, ProgressProvider?> Wait = null!;
    public TInput Input => ReadInput();
    public TOutput Output { get => ReadOutput(); set => WriteOutput(value); }
    public void Start(TInput input = default!, bool isForceRestart = false) => StartWithInput(input, isForceRestart);
    public void WaitForExit(TInput input = default!, LegacyLaunchTaskPort? progressSource = null, bool isForceRestart = false,
                            CancellationToken c = default, ProgressProvider? p = null) => Wait(input, progressSource, isForceRestart, c, p);
}
public sealed class LegacyLaunchOperation : LegacyLaunchTaskPort {
    public Action Start = null!;
}
public abstract class LegacyLaunchStep {
    public string Name = null!;
    public double ProgressWeight = 1;
    public bool Block = true, Show = true, InterruptOnCancel = true;
}
public sealed class LegacyLaunchIntStep : LegacyLaunchStep { public Action<LegacyLaunchTask<int, int>> Work = null!; }
public sealed class LegacyLaunchStringStep : LegacyLaunchStep { public Action<LegacyLaunchTask<string, int>> Work = null!; }
public sealed class LegacyLaunchProcessStep : LegacyLaunchStep { public Action<LegacyLaunchTask<int, Process>> Work = null!; }
public sealed class LegacyLaunchWaitStep : LegacyLaunchStep { public Action<LegacyLaunchTask<Process, int>> Work = null!; }
public sealed class LegacyLaunchExistingStep : LegacyLaunchStep { public LegacyLaunchTaskPort Task = null!; public bool PreserveMetadata = true; }

public sealed class LegacyLaunchMessage {
    public string Text = null!, Title = "提示", Button1 = "确定", Button2 = "", Button3 = "";
    public bool IsWarn, HighLight = true, ForceWait;
    public Action? Button1Action, Button2Action, Button3Action;
}
public enum LegacyLaunchRequestKind { Retry, Multiple }
public sealed class LegacyLaunchRequest {
    public string Url = null!;
    public LegacyLaunchRequestKind Kind;
    public HttpMethod? Method;
    public object? Content;
    public string? ContentType;
    public string[,]? Headers;
    public bool RequireJson;
    public Encoding? Encoding;
    public int? ThreadCount;
}
public enum LegacyLaunchDeviceResultKind { Tokens, Restart, Error }
public sealed class LegacyLaunchDeviceResult {
    public LegacyLaunchDeviceResultKind Kind;
    public Exception? Error;
    public string AccessToken = null!, RefreshToken = null!;
}
public sealed class LegacyLaunchImage {
    public Func<double> ReadHeight = null!;
    public Action<string> Save = null!;
    public Func<int, int, int, int, LegacyLaunchImage> Clip = null!;
    public double Height => ReadHeight();
}

/// <summary>Typed call ports for the already migrated C013/C015 inspectors and plans, retaining the actual reports and transactions.</summary>
public sealed class LegacyLaunchDependencyContext { public Func<CancellationToken, LegacyLaunchDependencyReport> Inspect = null!; }
public sealed class LegacyLaunchDependencyReport {
    public object Source = null!;
    public Func<bool> ReadHasErrors = null!, WasAccepted = null!;
    public Func<bool>? ReadNeedsAttention;
    public Action Accept = null!;
    public Func<CancellationToken, LegacyLaunchDependencyPlan> BuildAutoFill = null!, BuildActivation = null!;
    public bool HasErrors => ReadHasErrors();
    public bool NeedsAttention => ReadNeedsAttention?.Invoke() ?? HasErrors;
}
public sealed class LegacyLaunchDependencyPlan {
    public object Source = null!;
    public Func<string> ReadRoot = null!, ReadActionText = null!;
    public Action<string> WriteCaption = null!;
    public Func<int> ReadItemsCount = null!, ReadEnablesCount = null!;
    public Func<CancellationToken, int> Install = null!;
    public Action? ConfirmAuthorReplacement;
    public string Root => ReadRoot();
    public string ActionText => ReadActionText();
    public int ItemsCount => ReadItemsCount();
    public int EnablesCount => ReadEnablesCount();
    public string Caption { set => WriteCaption(value); }
}
