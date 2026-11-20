using System.Windows;
using PCLLF.Presentation.Controls;

namespace PCL
{

    public class MyResizer : WindowResizer
    {
        public MyResizer(Window Target) : base(Target, ModBase.GetWPFSize, (Work, Name) => ModBase.RunInNewThread(Work, Name),
            Target is FormMain { GlassShellActive: true } window ? window.SetGlassResizing : null)
        {
        }
    }
}
