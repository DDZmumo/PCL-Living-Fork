// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using Page = PCL.FormMain.PageType;
using Sub = PCL.FormMain.PageSubType;

namespace PCL;

internal sealed partial class GlassShellIntegration
{
    internal void RefreshJavaPages()
    {
        if (_activeJava is GlassInstanceJavaPage instance) instance.Reload();
        else if (_activeJava is GlassJavaManagementPage management) management.Refresh();
    }
}

internal sealed partial class GlassShellIntegration
{
    private GlassJavaManagementPage? _javaManagement;
    private GlassJavaDownloadPage? _javaCatalogue;
    private GlassInstanceJavaPage? _instanceJava;
    private GlassJavaRuntimePage? _activeJava;

    private bool IsJavaRoute(Page page, Sub sub) => IsJavaRoute(page, sub, _owner.PageCurrent.Additional);
    private static bool IsJavaRoute(Page page, Sub sub, object? additional) =>
        (page, sub) is (Page.Setup, Sub.SetupJava) or (Page.InstanceSetup, Sub.InstanceJava) ||
        page == Page.HelpDetail && additional is object[] { Length: 1 } elements && Equals(elements[0], "下载 Java");

    internal void OpenJavaCatalogue() => _owner.PageChange(new FormMain.PageStackData { Page = Page.HelpDetail, Additional = new object[] { "下载 Java" } });

    private void OpenJava(Page page)
    {
        bool data = _owner.GlassPreviewState is null;
        if (page == Page.Setup)
        {
            _javaManagement ??= Track(new GlassJavaManagementPage(OpenJavaCatalogue));
            _javaManagement.Open(data);
            _activeJava = _javaManagement;
        }
        else if (page == Page.InstanceSetup)
        {
            _instanceJava ??= Track(new GlassInstanceJavaPage(() => _owner.PageChange(Page.Setup, Sub.SetupJava)));
            _instanceJava.Open(data);
            _activeJava = _instanceJava;
        }
        else
        {
            _javaCatalogue ??= Track(new GlassJavaDownloadPage());
            _javaCatalogue.Open(data);
            _activeJava = _javaCatalogue;
        }
        View.SetPageContent(_activeJava);
    }

    private T Track<T>(T page) where T : GlassJavaRuntimePage
    {
        page.Scroll.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) _owner.RefreshBackToTop(); };
        return page;
    }

    private void LeaveJava()
    {
        if (_activeJava is GlassJavaDownloadPage download) download.Leave();
        if (_activeJava is GlassInstanceJavaPage instance) instance.Leave();
        _activeJava = null;
    }

    private void RefreshJava()
    {
        if (_activeJava is GlassJavaManagementPage management) management.Refresh();
        if (_activeJava is GlassInstanceJavaPage instance) instance.Refresh();
    }
}
