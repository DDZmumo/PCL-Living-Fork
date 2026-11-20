using System;
using System.Threading;
using System.Windows;
using PCLLF.Presentation.Controls;

namespace PCL
{

    public class MyCard : MyCardView
    {

        static MyCard()
        {
            PresentationRuntime.EnsureConfigured();
        }
        public static new readonly DependencyProperty TitleProperty = MyCardView.TitleProperty;
        public static new readonly DependencyProperty IsSwapedProperty = MyCardView.IsSwapedProperty;
        public new MyDropShadow MainChrome
        {
            get
            {
                return (MyDropShadow)base.MainChrome;
            }
        }
        protected override MyDropShadowView CreateChrome()
        {
            return new MyDropShadow();
        }
        protected override System.Windows.Controls.Border CreateCardBackground()
        {
            if (!FormMain.GlassShellPreferred) return base.CreateCardBackground();
            var surface = new PCLLF.Wpf.Glass.GlassSurface { CornerRadius = new CornerRadius(16), IsHitTestVisible = false };
            Loaded += (_, _) => {
                CornerRadius = new CornerRadius(16);
                MainChrome.Visibility = Visibility.Collapsed;
            };
            return surface;
        }
        protected override double WpfSize(double Pixels)
        {
            return ModBase.GetWPFSize(Pixels);
        }
        protected override bool HasMainWindow
        {
            get
            {
                return ModMain.FrmMain is not null;
            }
        }
        protected override void PostLoaded(Action Work)
        {
            ModBase.RunInUi(Work, true);
        }
        public new event PreviewSwapEventHandler PreviewSwap;

        public new delegate void PreviewSwapEventHandler(object sender, ModBase.RouteEventArgs e);
        public new event SwapEventHandler Swap;

        public new delegate void SwapEventHandler(object sender, ModBase.RouteEventArgs e);
        protected override object CreateSwapArgs()
        {
            return new ModBase.RouteEventArgs(true);
        }
        protected override bool OnPreviewSwap(object Args)
        {
            ModBase.RouteEventArgs e = (ModBase.RouteEventArgs)Args;
            PreviewSwap?.Invoke(this, e);
            return e.Handled;
        }
        protected override void OnSwap(object Args)
        {
            Swap?.Invoke(this, (ModBase.RouteEventArgs)Args);
        }
    }

    public static partial class ModAnimation
    {
        public static void AniDispose(MyCard Control, bool RemoveFromChildren, ParameterizedThreadStart CallBack = null)
        {
            Control.DisposeAnimated(RemoveFromChildren, CallBack is null ? null : new Action<object>(Value => CallBack(Value)));
        }
    }
}
