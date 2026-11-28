// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL;
using Application = System.Windows.Application;

namespace PCLLF.Native.Facade.Tests;

// Calls the real Main portal helpers. Offscreen WPF layout/rendering exercises its actual
// logical/visual parents and ContentPresenter bindings without loading a login page or FormMain.
internal static class GlassPortalChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        VisualAssetChecks.VerifyChild(environment);
        Native.Check(Application.Current.Dispatcher.CheckAccess() &&
            Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
            "Glass portal checks require the genuine isolated WPF STA fixture.");
        using var scope = new VisualAssetChecks.Scope(environment, "glass-portals");
        int suppression = ModAnimation.AniControlEnabled;
        ModAnimation.AniControlEnabled++;
        try
        {
            ViewboxAndRenderedPortalTransfers();
            environment.AssertNoForbiddenIo();
        }
        finally
        {
            ModAnimation.AniControlEnabled = suppression;
        }
        Console.WriteLine("PASS glass portals: actual Main detachment of Viewbox/internal visual and rendered ContentControl owners; idempotent/repeated transfers preserve live presenter bindings and rendered content.");
    }

    private static Type Integration => Native.Type("PCL.GlassShellIntegration");
    private static void Ensure(UIElement content, ContentControl target) =>
        Native.Call(Integration, "EnsureContentPortal", null, content, target);
    private static void Detach(UIElement content) => Native.Call(Integration, "Detach", null, content);

    private static void ViewboxAndRenderedPortalTransfers()
    {
        var content = new Grid { Width = 72, Height = 32, Background = Brushes.CornflowerBlue };
        var source = new Viewbox { Child = content };
        var first = Host();
        var second = Host();
        try
        {
            Native.Check(ReferenceEquals(content.Parent, source) && VisualTreeHelper.GetParent(content) is ContainerVisual,
                "The Viewbox regression did not exercise WPF's internal visual wrapper.");
            Detach(content);
            Native.Check(source.Child is null && content.Parent is null && VisualTreeHelper.GetParent(content) is null,
                "Actual Detach left the Viewbox's logical or internal visual child attached.");

            Ensure(content, first);
            RenderBlue(first, content);
            ContentPresenter firstPresenter = Presenter(first);
            BindingExpression firstBinding = Binding(firstPresenter);
            Repeat(first, content, firstPresenter, firstBinding);

            // A rendered ContentPresenter is an implementation detail, not the owning ContentControl.
            Native.Check(ReferenceEquals(content.Parent, first) && ReferenceEquals(VisualTreeHelper.GetParent(content), firstPresenter),
                "The rendered portal regression did not exercise distinct logical/visual parents.");
            Ensure(content, second);
            Layout(first);
            RenderBlue(second, content);
            Native.Check(first.Content is null && firstPresenter.Content is null &&
                ReferenceEquals(Binding(firstPresenter), firstBinding) && ReferenceEquals(content.Parent, second),
                "Moving a rendered portal cleared its template presenter or retained the old logical owner.");
            ContentPresenter secondPresenter = Presenter(second);
            BindingExpression secondBinding = Binding(secondPresenter);
            Repeat(second, content, secondPresenter, secondBinding);

            Ensure(content, first);
            Layout(second);
            RenderBlue(first, content);
            Native.Check(second.Content is null && secondPresenter.Content is null &&
                ReferenceEquals(Binding(secondPresenter), secondBinding) &&
                ReferenceEquals(Presenter(first), firstPresenter) && ReferenceEquals(Binding(firstPresenter), firstBinding),
                "Returning to an existing portal did not reactivate its original live presenter binding.");
            Repeat(first, content, firstPresenter, firstBinding);

            Detach(content);
            Layout(first);
            Native.Check(first.Content is null && firstPresenter.Content is null &&
                ReferenceEquals(Binding(firstPresenter), firstBinding) && content.Parent is null,
                "Actual Detach mutated the template ContentPresenter instead of clearing its ContentControl owner.");
            Ensure(content, first);
            RenderBlue(first, content);
        }
        finally
        {
            first.Content = null;
            second.Content = null;
            source.Child = null;
        }
    }

    private static void Repeat(PortalHost host, FrameworkElement content, ContentPresenter presenter, BindingExpression binding,
        FrameworkElement? renderedMarker = null)
    {
        int changes = host.ContentChanges;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            Ensure(content, host);
            RenderBlue(host, renderedMarker ?? content);
            Native.Check(host.ContentChanges == changes && ReferenceEquals(host.Content, content) &&
                ReferenceEquals(Presenter(host), presenter) && ReferenceEquals(presenter.Content, content) &&
                ReferenceEquals(Binding(presenter), binding),
                "Repeating an existing portal request detached its content, replaced its binding, or left an empty presenter.");
        }
    }

    private static PortalHost Host()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter), "PortalPresenter");
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding(nameof(ContentControl.Content))
        {
            RelativeSource = RelativeSource.TemplatedParent
        });
        return new PortalHost { Template = new ControlTemplate(typeof(ContentControl)) { VisualTree = presenter } };
    }
    private static void Layout(FrameworkElement host)
    {
        host.Width = 340;
        host.Height = 420;
        host.Measure(new Size(340, 420));
        host.Arrange(new Rect(0, 0, 340, 420));
        host.UpdateLayout();
    }
    private static ContentPresenter Presenter(ContentControl host) =>
        host.Template.FindName("PortalPresenter", host) as ContentPresenter ??
        throw new InvalidOperationException("The real WPF portal template did not create its ContentPresenter.");
    private static BindingExpression Binding(ContentPresenter presenter) =>
        BindingOperations.GetBindingExpression(presenter, ContentPresenter.ContentProperty) ??
        throw new InvalidOperationException("A portal helper overwrote the live ContentPresenter.Content binding.");
    private static void RenderBlue(FrameworkElement host, FrameworkElement content)
    {
        Layout(host);
        Rect area = content.TransformToAncestor(host).TransformBounds(new Rect(content.RenderSize));
        Native.Check(area.Width > 0 && area.Height > 0 && new Rect(host.RenderSize).Contains(area),
            "The portal content is empty, clipped or detached from its current rendered owner.");
        var bitmap = new RenderTargetBitmap(340, 420, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        int x = (int)Math.Floor(area.Left + area.Width / 2), y = (int)Math.Floor(area.Top + area.Height / 2);
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        Native.Check(pixel[0] == 237 && pixel[1] == 149 && pixel[2] == 100 && pixel[3] == 255,
            "The offscreen WPF portal stopped rendering its original content after a transfer/repeat.");
    }
    private sealed class PortalHost : ContentControl
    {
        internal int ContentChanges { get; private set; }
        protected override void OnContentChanged(object oldContent, object newContent)
        {
            ContentChanges++;
            base.OnContentChanged(oldContent, newContent);
        }
    }
}
