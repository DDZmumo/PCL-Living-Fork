using System;
using System.Collections.Generic;
using System.Windows;
using Microsoft.VisualBasic.CompilerServices;
using PCLLF.Presentation.Controls;

namespace PCL
{

    public class MyPageRight : MyPageRightView
    {
        static MyPageRight()
        {
            PresentationRuntime.EnsureConfigured();
        }
        private static new readonly DependencyProperty PanScrollProperty = MyPageRightView.PanScrollProperty;
        public new enum PageStates
        {
            Empty,
            LoaderWait,
            LoaderEnter,
            LoaderStayForce,
            LoaderStay,
            LoaderExit,
            ContentEnter,
            ContentStay,
            ContentExit,
            PageExit
        }
        public PageStates PageState
        {
            get
            {
                return (PageStates)(int)NativePageState;
            }
            set
            {
                NativePageState = (MyPageRightView.PageStates)(int)value;
            }
        }
        public new event PageEnterEventHandler PageEnter;

        public new delegate void PageEnterEventHandler();
        public new event PageExitEventHandler PageExit;

        public new delegate void PageExitEventHandler();
        protected override void OnPageEnter()
        {
            PageEnter?.Invoke();
        }
        protected override void OnPageExit()
        {
            PageExit?.Invoke();
        }
        protected override bool UseProjectedNavigation => ModMain.FrmMain?.GlassShellActive == true;
        protected override void UiPost(Action Work)
        {
            ModBase.RunInUi(Work);
        }
        protected override void UiWait(Action Work)
        {
            ModBase.RunInUiWait(Work);
        }
        public void PageLoaderInit<TIn, TOut>(MyLoading LoaderUi, FrameworkElement PanLoader, FrameworkElement PanContent, FrameworkElement PanAlways, ModLoader.LoaderTask<TIn, TOut> RealLoader, Action<ModLoader.LoaderTask<TIn, TOut>> FinishedInvoke = null, Func<TIn> InputInvoke = null, bool AutoRun = true)
        {
            var Binding = new PageLoaderBinding()
            {
                Original = RealLoader,
                ReadState = new Func<PageLoaderState>(() => (PageLoaderState)(int)RealLoader.State),
                Start = new Action<object, bool>((Input, Force) => RealLoader.Start(Input, IsForceRestart: Force)),
                UsesStartGetInput = true,
                InitialInput = new Func<object>(() => RealLoader.StartGetInput(default, InputInvoke)),
                StartGetInput = new Func<object, object>(Input => NewLateBinding.LateGet(RealLoader, null, "StartGetInput", new[] { Input, InputInvoke }, null, null, null)),
                InputInvoke = InputInvoke is null ? null : new Func<object>(() => InputInvoke()),
                Finished = FinishedInvoke is null ? null : new Action(() => FinishedInvoke(RealLoader)),
                ConnectPreviewFinish = new Action<Action>(Callback => RealLoader.PreviewFinish += (Loader) => Callback()),
                ConnectState = new Action<Action<PageLoaderState, PageLoaderState>>(Callback => RealLoader.OnStateChangedUi += (Loader, NextState, OldState) => Callback((PageLoaderState)(int)NextState, (PageLoaderState)(int)OldState)),
                SetLoadingState = new Action(() => LoaderUi.State = RealLoader),
                ConnectClick = new Action<Action>(Callback => LoaderUi.Click += (Sender, Args) => Callback())
            };
            InitializeLoader(Binding, PanLoader, PanContent, PanAlways, AutoRun);
        }
        public void PageLoaderInit<TIn>(MyLoading LoaderUi, FrameworkElement PanLoader, FrameworkElement PanContent, FrameworkElement PanAlways, ModLoader.LoaderCombo<TIn> RealLoader, Action<ModLoader.LoaderCombo<TIn>> FinishedInvoke = null, Func<TIn> InputInvoke = null, bool AutoRun = true)
        {
            var Binding = new PageLoaderBinding()
            {
                Original = RealLoader,
                ReadState = new Func<PageLoaderState>(() => (PageLoaderState)(int)RealLoader.State),
                Start = new Action<object, bool>((Input, Force) => RealLoader.Start(Input, IsForceRestart: Force)),
                UsesStartGetInput = false,
                InputInvoke = InputInvoke is null ? null : new Func<object>(() => InputInvoke()),
                Finished = FinishedInvoke is null ? null : new Action(() => FinishedInvoke(RealLoader)),
                ConnectPreviewFinish = new Action<Action>(Callback => RealLoader.PreviewFinish += (Loader) => Callback()),
                ConnectState = new Action<Action<PageLoaderState, PageLoaderState>>(Callback => RealLoader.OnStateChangedUi += (Loader, NextState, OldState) => Callback((PageLoaderState)(int)NextState, (PageLoaderState)(int)OldState)),
                SetLoadingState = new Action(() => LoaderUi.State = RealLoader),
                ConnectClick = new Action<Action>(Callback => LoaderUi.Click += (Sender, Args) => Callback())
            };
            InitializeLoader(Binding, PanLoader, PanContent, PanAlways, AutoRun);
        }
        public new void PageLoaderRestart(object Input = null, bool IsForceRestart = true)
        {
            base.PageLoaderRestart(Input, IsForceRestart);
        }
        public new void TriggerEnterAnimation(params FrameworkElement[] Elements)
        {
            base.TriggerEnterAnimation(Elements);
        }
        public new void TriggerExitAnimation(params FrameworkElement[] Elements)
        {
            base.TriggerExitAnimation(Elements);
        }
        internal new IEnumerable<FrameworkElement> GetAllAnimControls(FrameworkElement Element, bool IgnoreInvisibility = false)
        {
            return base.GetAllAnimControls(Element, IgnoreInvisibility);
        }
    }
}
