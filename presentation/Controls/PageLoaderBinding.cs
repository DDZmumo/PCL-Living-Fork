using System;

namespace PCLLF.Presentation.Controls;

// Delegates bind the actual original closed LoaderTask/LoaderCombo and MyLoading instances;
// the C# view owns the complete page state machine, waits, animation and restart policy.
public enum PageLoaderState { Waiting, Loading, Finished, Failed, Canceled }
public sealed class PageLoaderBinding {
    public object Original = null!;
    public Func<PageLoaderState> ReadState = null!;
    public Action<object?, bool> Start = null!;
    public bool ProjectContent;
    public bool UsesStartGetInput;
    public Func<object?>? InitialInput;
    public Func<object?, object?>? StartGetInput;
    public Func<object?>? InputInvoke;
    public Action<Action> ConnectPreviewFinish = null!;
    public Action<Action<PageLoaderState, PageLoaderState>> ConnectState = null!;
    public Action? Finished;
    public Action SetLoadingState = null!;
    public Action<Action> ConnectClick = null!;
}
