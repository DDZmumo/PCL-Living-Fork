using System.Windows;
using System.Windows.Markup;
using PCLLF.Presentation.Controls;

namespace PCL
{

    [ContentProperty("Inlines")]
    public class MyRadioButton : MyRadioButtonView
    {

        static MyRadioButton()
        {
            PresentationRuntime.EnsureConfigured();
        }

        public static new readonly DependencyProperty TextProperty = MyRadioButtonView.TextProperty;
        /// <summary>Activates the original preview/check navigation pipeline without pointer input.</summary>
        public void PerformClick() => ActivateRadioButton();

        public new enum ColorState
        {
            White = 0,
            Highlight = 1
        }
        public new ColorState ColorType
        {
            get
            {
                return (ColorState)(int)base.ColorType;
            }
            set
            {
                base.ColorType = (MyRadioButtonView.ColorState)(int)value;
            }
        }

        public new event CheckEventHandler Check;

        public new delegate void CheckEventHandler(object sender, bool raiseByMouse);
        public new event PreviewClickEventHandler PreviewClick;

        public new delegate void PreviewClickEventHandler(object sender, ModBase.RouteEventArgs e);
        protected override void OnCheck(bool raiseByMouse)
        {
            Check?.Invoke(this, raiseByMouse);
        }
        protected override bool RaisePreviewClick(bool user)
        {
            var e = new ModBase.RouteEventArgs(user);
            PreviewClick?.Invoke(this, e);
            return e.Handled;
        }
    }
}
