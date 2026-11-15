using System;

namespace PCLCS;

public enum SelectionPageHint { Red, Blue, Green }
/// <summary>Parameters of LF's actual message box; no replacement UI or confirmation policy.</summary>
public sealed class SelectionPageMessage {
    public string Text = null!, Title = "提示", Button1 = "确定", Button2 = "", Button3 = "";
    public bool IsWarn;
}
