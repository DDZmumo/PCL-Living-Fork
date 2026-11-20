using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PCLLF.Presentation.Controls;

// LF Controls/MyPageRight.vb. CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 MyPageRight.cs compared;
// CE changed PanScroll to a control reference. LF's named string DP, timings and traversal are retained.
public class MyPageRightView : AdornerDecorator {
    public int PageUuid;
    public MyPageRightView() { PageUuid = ControlServices.Current.NewUuid(); }
    public string? PanScroll { get => (string?)GetValue(PanScrollProperty); set => SetValue(PanScrollProperty, value); }
    public static readonly DependencyProperty PanScrollProperty = DependencyProperty.Register(nameof(PanScroll), typeof(string), typeof(MyPageRightView), new PropertyMetadata(null));
    public enum PageStates { Empty, LoaderWait, LoaderEnter, LoaderStayForce, LoaderStay, LoaderExit, ContentEnter, ContentStay, ContentExit, PageExit }
    private PageStates pageState = PageStates.Empty;
    public PageStates NativePageState { get => pageState; set { if (pageState == value) return; pageState = value; MeloongCore.Logger.Trace("页面状态切换为 " + value); } }
    private static ControlAnimations Animations => ControlServices.Current.Animations;
    protected virtual void UiPost(Action work) { if (Dispatcher.CheckAccess()) work(); else Dispatcher.InvokeAsync(work); }
    protected virtual void UiWait(Action work) { if (Dispatcher.CheckAccess()) work(); else Dispatcher.Invoke(work); }
    private PageLoaderBinding? loader;
    private FrameworkElement? panLoader, panContent, panAlways;
    private bool autoRun;
    public void InitializeLoader(PageLoaderBinding binding, FrameworkElement loadingPanel, FrameworkElement contentPanel, FrameworkElement? alwaysPanel, bool autoRun = true) {
        panLoader = loadingPanel; panContent = contentPanel; panAlways = alwaysPanel; loader = binding; this.autoRun = autoRun;
        if (binding.Finished is not null) binding.ConnectPreviewFinish(() => {
            while (NativePageState is PageStates.PageExit or PageStates.ContentExit) Thread.Sleep(10);
            UiWait(binding.Finished); Thread.Sleep(20);
        });
        binding.ConnectState((next, old) => UiPost(() => LoaderStateChanged(next, old)));
        loadingPanel.Visibility = Visibility.Collapsed; contentPanel.Visibility = Visibility.Collapsed; if (alwaysPanel is not null) alwaysPanel.Visibility = Visibility.Collapsed;
        if (this.autoRun) binding.Start(binding.UsesStartGetInput ? binding.InitialInput!() : binding.InputInvoke?.Invoke(), false);
        if (binding.ReadState() == PageLoaderState.Finished && binding.Finished is not null) UiWait(binding.Finished);
        binding.SetLoadingState(); binding.ConnectClick(() => { if (binding.ReadState() == PageLoaderState.Failed) PageLoaderRestart(); });
        // As in LF, repeated Init does not detach older loader/Click subscriptions.
    }
    public void PageLoaderRestart(object? input = null, bool isForceRestart = true) {
        if (!autoRun) return;
        if (loader!.UsesStartGetInput) loader.Start(loader.StartGetInput!(input), isForceRestart);
        else { if (input is null && loader.InputInvoke is not null) input = loader.InputInvoke(); loader.Start(input, isForceRestart); }
    }
    public event Action? PageEnter, PageExit;
    public event Action? PageEntering, PageLeaving;
    protected virtual void OnPageEnter() => PageEnter?.Invoke();
    protected virtual void OnPageExit() => PageExit?.Invoke();
    protected virtual bool UseProjectedNavigation => false;
    private FrameworkElement? ContentElement => (FrameworkElement?)(panContent ?? Child);
    private bool LoaderAllowsContent => loader is null || loader.ReadState() == PageLoaderState.Finished || loader.ReadState() == PageLoaderState.Waiting || loader.ReadState() == PageLoaderState.Canceled;
    public void PageOnEnter() {
        MeloongCore.Logger.Trace("已触发 PageOnEnter"); PageEntering?.Invoke(); OnPageEnter();
        switch (NativePageState) {
            case PageStates.Empty:
                if (LoaderAllowsContent) { NativePageState = PageStates.ContentEnter; ShowEnteringPage(panAlways, ContentElement); }
                else if (loader!.ReadState() == PageLoaderState.Loading) { NativePageState = PageStates.LoaderWait; Animations.Start(Animations.Code(PageOnLoaderWaitFinished, 400), "PageRight PageChange " + PageUuid); }
                else { NativePageState = PageStates.LoaderEnter; ShowEnteringPage(panAlways, panLoader); }
                break;
            case PageStates.ContentExit:
                if (LoaderAllowsContent) { NativePageState = PageStates.ContentEnter; TriggerEnterAnimation(ContentElement); }
                else if (loader!.ReadState() == PageLoaderState.Loading) { NativePageState = PageStates.LoaderWait; Animations.Start(Animations.Code(PageOnLoaderWaitFinished, 400), "PageRight PageChange " + PageUuid); }
                else { NativePageState = PageStates.LoaderEnter; TriggerEnterAnimation(panLoader); }
                break;
            case PageStates.ContentEnter: break;
            default: throw new Exception("在状态为 " + NativePageState + " 时触发了 PageOnEnter 事件。");
        }
    }
    public void PageOnExit() {
        MeloongCore.Logger.Trace("已触发 PageOnExit"); PageLeaving?.Invoke(); OnPageExit();
        switch (NativePageState) {
            case PageStates.ContentEnter: case PageStates.ContentStay: NativePageState = PageStates.PageExit; TriggerExitAnimation(panAlways, ContentElement); break;
            case PageStates.LoaderEnter: case PageStates.LoaderStayForce: case PageStates.LoaderStay: NativePageState = PageStates.PageExit; TriggerExitAnimation(panAlways, panLoader); break;
            case PageStates.LoaderWait: NativePageState = PageStates.PageExit; TriggerExitAnimation(panAlways); break;
            case PageStates.LoaderExit: case PageStates.ContentExit: NativePageState = PageStates.PageExit; if (panAlways is not null) TriggerExitAnimation(panAlways, ContentElement); break;
            case PageStates.PageExit: case PageStates.Empty: break;
        }
    }
    public void PageOnForceExit() {
        if (NativePageState == PageStates.Empty) return;
        MeloongCore.Logger.Trace("已触发 PageOnForceExit"); PageLeaving?.Invoke(); NativePageState = PageStates.Empty; Animations.Stop("PageRight PageChange " + PageUuid);
        if (loader is null) Child!.Visibility = Visibility.Collapsed;
        else { panContent!.Visibility = Visibility.Collapsed; panLoader!.Visibility = Visibility.Collapsed; if (panAlways is not null) panAlways.Visibility = Visibility.Collapsed; }
    }
    public void PageOnContentExit() {
        MeloongCore.Logger.Trace("已触发 PageOnContentExit");
        if (loader is not null && loader.ReadState() == PageLoaderState.Loading) throw new Exception("在调用 PageOnContentExit 时，加载器不能为 Loading 状态");
        switch (NativePageState) {
            case PageStates.ContentEnter: case PageStates.ContentStay: NativePageState = PageStates.ContentExit; TriggerExitAnimation(panContent); break;
            case PageStates.LoaderExit: NativePageState = PageStates.ContentExit; break;
            case PageStates.LoaderEnter: case PageStates.LoaderStayForce: case PageStates.LoaderStay: NativePageState = PageStates.ContentExit; TriggerExitAnimation(panLoader); break;
            case PageStates.LoaderWait: PageOnEnter(); break;
            case PageStates.Empty: if (IsLoaded) PageOnEnter(); break;
        }
    }
    private void PageOnEnterAnimationFinished() {
        MeloongCore.Logger.Trace("已触发 PageOnEnterAnimationFinished");
        switch (NativePageState) {
            case PageStates.ContentEnter: NativePageState = PageStates.ContentStay; break;
            case PageStates.LoaderEnter: NativePageState = PageStates.LoaderStayForce; Animations.Start(Animations.Code(PageOnLoaderStayFinished, 400), "PageRight PageChange " + PageUuid); break;
            default: throw new Exception("在状态为 " + NativePageState + " 时触发了 PageOnEnterAnimationFinished 事件。");
        }
    }
    private void PageOnExitAnimationFinished() {
        MeloongCore.Logger.Trace("已触发 PageOnExitAnimationFinished");
        switch (NativePageState) {
            case PageStates.PageExit: NativePageState = PageStates.Empty; break;
            case PageStates.ContentExit: PageOnEnter(); break;
            case PageStates.LoaderExit: NativePageState = PageStates.ContentEnter; TriggerEnterAnimation(panContent); break;
            default: throw new Exception("在状态为 " + NativePageState + " 时触发了 PageOnExitAnimationFinished 事件。");
        }
    }
    private void PageOnLoaderWaitFinished() {
        MeloongCore.Logger.Trace("已触发 PageOnLoaderWaitFinished");
        if (NativePageState != PageStates.LoaderWait) throw new Exception("在状态为 " + NativePageState + " 时触发了 PageOnLoaderWaitFinished 事件。");
        NativePageState = PageStates.LoaderEnter;
        if (panAlways is not null && panAlways.Visibility == Visibility.Collapsed) TriggerEnterAnimation(panAlways, panLoader); else TriggerEnterAnimation(panLoader);
    }
    private void PageOnLoaderStayFinished() {
        MeloongCore.Logger.Trace("已触发 PageOnLoaderStayFinished");
        if (NativePageState != PageStates.LoaderStayForce) throw new Exception("在状态为 " + NativePageState + " 时触发了 PageOnLoaderWaitFinished 事件。");
        if (loader!.ReadState() == PageLoaderState.Finished) { NativePageState = PageStates.LoaderExit; TriggerExitAnimation(panLoader); }
        else NativePageState = PageStates.LoaderStay;
    }
    private void LoaderStateChanged(PageLoaderState next, PageLoaderState old) {
        switch (next) {
            case PageLoaderState.Failed: case PageLoaderState.Loading:
                if (old is PageLoaderState.Failed or PageLoaderState.Loading) return; MeloongCore.Logger.Trace("已触发 PageLoaderState (Start/Refresh)");
                if (NativePageState is PageStates.ContentEnter or PageStates.ContentStay) { NativePageState = PageStates.ContentExit; TriggerExitAnimation(panContent); }
                else if (NativePageState == PageStates.LoaderExit) NativePageState = PageStates.ContentExit;
                break;
            case PageLoaderState.Finished: case PageLoaderState.Canceled: case PageLoaderState.Waiting:
                if (old is not PageLoaderState.Failed and not PageLoaderState.Loading) return; MeloongCore.Logger.Trace("已触发 PageLoaderState (Stop/Canceled)");
                if (NativePageState == PageStates.LoaderWait) { NativePageState = PageStates.ContentEnter; if (panAlways is not null && panAlways.Visibility == Visibility.Collapsed) TriggerEnterAnimation(panAlways, panContent); else TriggerEnterAnimation(panContent); }
                else if (NativePageState == PageStates.LoaderStay) { NativePageState = PageStates.LoaderExit; TriggerExitAnimation(panLoader); }
                break;
        }
    }
    private void ShowEnteringPage(params FrameworkElement?[] elements) {
        if (!UseProjectedNavigation) { TriggerEnterAnimation(elements); return; }
        // The projected page applies the navigation transition. The source settles its visibility and loader state
        // without scheduling a second entrance on the controls it supplies to that projection.
        foreach (FrameworkElement element in elements.OfType<FrameworkElement>()) {
            element.Visibility = Visibility.Visible;
            foreach (FrameworkElement control in GetAllAnimControls(element, true)) {
                control.IsHitTestVisible = true;
                if (control.RenderTransform is TranslateTransform) control.RenderTransform = null!;
                if (control is MyExtraTextButtonView extra) extra.Show = true;
                else control.Opacity = 1;
            }
        }
        PageOnEnterAnimationFinished();
    }

    public void TriggerEnterAnimation(params FrameworkElement?[] elements) {
        var real = elements.Where(element => element is not null).Select(element => element!);
        foreach (FrameworkElement element in real) element.Visibility = Visibility.Visible;
        List<ControlAnimation> frames = new(); int delay = 0;
        foreach (FrameworkElement element in real) {
            foreach (FrameworkElement control in GetAllAnimControls(element, true)) { control.IsHitTestVisible = true; if (control.RenderTransform is TranslateTransform) control.RenderTransform = null!; }
            foreach (FrameworkElement control in GetAllAnimControls(element)) {
                if (control is MyExtraTextButtonView extra) extra.Show = true;
                else { control.Opacity = 0; control.RenderTransform = new TranslateTransform(0, -16);
                    frames.Add(Animations.Opacity(control, 1, new(100, delay, ControlEase.OutFluent, ControlEasePower.Weak)));
                    frames.Add(Animations.TranslateY(control, 5, new(250, delay, ControlEase.OutFluent)));
                    frames.Add(Animations.TranslateY(control, 11, new(350, delay, ControlEase.OutBack))); delay = checked(delay + 25); }
            }
        }
        MyScrollBarView? scroll = FirstScrollBar(real);
        if (scroll is not null) { if (scroll.RenderTransform is not TranslateTransform) scroll.RenderTransform = new TranslateTransform(10, 0); frames.Add(Animations.TranslateX(scroll, -((TranslateTransform)scroll.RenderTransform).X, new(350, ease: ControlEase.OutFluent))); }
        frames.Add(Animations.Code(PageOnEnterAnimationFinished, after: true)); Animations.Start(frames, "PageRight PageChange " + PageUuid, true);
    }
    public void TriggerExitAnimation(params FrameworkElement?[] elements) {
        var real = elements.Where(element => element is not null).Select(element => element!); List<ControlAnimation> frames = new(); int delay = 0;
        foreach (FrameworkElement element in real) foreach (FrameworkElement control in GetAllAnimControls(element)) {
            if (control is MyExtraTextButtonView extra) extra.Show = false;
            else { control.IsHitTestVisible = false; frames.Add(Animations.Opacity(control, -1, new(70, delay))); frames.Add(Animations.TranslateY(control, -6, new(70, delay))); delay = checked(delay + 15); }
        }
        MyScrollBarView? scroll = FirstScrollBar(real);
        if (scroll is not null) { if (scroll.RenderTransform is not TranslateTransform) scroll.RenderTransform = new TranslateTransform(); frames.Add(Animations.TranslateX(scroll, 10 - ((TranslateTransform)scroll.RenderTransform).X, new(90, ease: ControlEase.InFluent))); }
        frames.Add(Animations.Code(() => { foreach (FrameworkElement element in real) element.Visibility = Visibility.Collapsed; PageOnExitAnimationFinished(); }, after: true));
        Animations.Start(frames, "PageRight PageChange " + PageUuid);
    }
    public List<FrameworkElement> DisabledPageAnimControls = new();
    public IEnumerable<FrameworkElement> GetAllAnimControls(FrameworkElement element, bool ignoreInvisibility = false) {
        List<FrameworkElement> all = new(); Collect(element, all, ignoreInvisibility); return all.Except(DisabledPageAnimControls);
    }
    private void Collect(FrameworkElement element, List<FrameworkElement> all, bool ignore) {
        if (!ignore && element.Visibility == Visibility.Collapsed) return;
        if (element is MyCardView or MyHintView or MyExtraTextButtonView or TextBlock or MyTextButtonView) all.Add(element);
        else if (element is ContentControl content) { if (content.Content is FrameworkElement child) Collect(child, all, ignore); }
        else if (element is Panel panel) foreach (object item in panel.Children) if (item is FrameworkElement child) Collect(child, all, ignore);
    }
    private MyScrollBarView? FirstScrollBar(IEnumerable<FrameworkElement> elements) {
        MyScrollViewerView? viewer = null;
        foreach (FrameworkElement element in elements) {
            if (element is MyScrollViewerView self) { viewer = self; break; }
            foreach (object child in LogicalTreeHelper.GetChildren(element)) if (child is MyScrollViewerView nested) { viewer = nested; break; }
            if (viewer is not null) break;
        }
        return viewer is null || viewer.ComputedVerticalScrollBarVisibility != Visibility.Visible ? null : viewer.CurrentScrollBar;
    }
}
