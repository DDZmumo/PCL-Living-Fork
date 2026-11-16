using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCL
{
    public class LazyLoadBehavior
    {

        /// <summary>
    /// 指定首次进入 ScrollViewer 的可视范围时执行的操作。
    /// </summary>
        public static void OnFirstEnterScrollViewerViewport(DependencyObject obj, Action value)
        {
            obj.SetValue(IsInViewportProperty, value);
        }

        private static readonly DependencyProperty IsInViewportProperty = DependencyProperty.RegisterAttached("IsInViewport", typeof(Action), typeof(LazyLoadBehavior), new PropertyMetadata(null, OnIsInViewportChanged));
        private static readonly DependencyProperty CompletedActionProperty = DependencyProperty.RegisterAttached("CompletedAction", typeof(Action), typeof(LazyLoadBehavior));

        /// <summary>Observes a replacement presentation while keeping lazy work owned by the source.</summary>
        public static IDisposable BindPresentationViewport(FrameworkElement source, FrameworkElement presentation)
            => new PresentationViewport(source, presentation);

        private sealed class PresentationViewport : IDisposable
        {
            private readonly FrameworkElement source, presentation;
            internal PresentationViewport(FrameworkElement source, FrameworkElement presentation)
            {
                this.source = source; this.presentation = presentation;
                presentation.LayoutUpdated += Check;
            }
            private void Check(object sender, EventArgs args)
            {
                if (source.GetValue(IsInViewportProperty) is not Action action ||
                    ReferenceEquals(source.GetValue(CompletedActionProperty), action) ||
                    !presentation.IsVisible || presentation.RenderSize.Width <= 0) return;
                var scroll = FindParentScrollViewer(presentation);
                if (scroll is null || !new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight).IntersectsWith(
                    presentation.TransformToAncestor(scroll).TransformBounds(new Rect(presentation.RenderSize)))) return;
                source.SetValue(CompletedActionProperty, action);
                action();
            }
            public void Dispose() => presentation.LayoutUpdated -= Check;
        }

        private static void OnIsInViewportChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            FrameworkElement Element = d as FrameworkElement;
            if (Element is null || e.NewValue is null)
                return;

            bool Handled = false;
            EventHandler Handler = null;
            Handler = (Sender, Args) =>
        {
            if (Handled)
                return;
            if (ReferenceEquals(Element.GetValue(CompletedActionProperty), e.NewValue)) {
                Handled = true; Element.LayoutUpdated -= Handler; return;
            }
            // 判断元素是否可见
            if (Element.RenderSize.Width < double.Epsilon)
                return;
            if (!Element.IsVisible)
                return;
            // 判断是否在 ScrollViewer 的可视区域内
            var Scroll = FindParentScrollViewer(Element);
            if (Scroll is null)
                return;
            if (!new Rect(0d, 0d, Scroll.ViewportWidth, Scroll.ViewportHeight).IntersectsWith(Element.TransformToAncestor(Scroll).TransformBounds(new Rect(new Point(0d, 0d), Element.RenderSize))))
                return;
            // 执行
            Handled = true;
            Element.SetValue(CompletedActionProperty, e.NewValue);
            ((Action)e.NewValue).Invoke();
            Element.LayoutUpdated -= Handler;
        };
            Element.LayoutUpdated += Handler;
        }

        private static ScrollViewer FindParentScrollViewer(DependencyObject d)
        {
            while (d is not null)
            {
                if (d is ScrollViewer)
                    return (ScrollViewer)d;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

    }
}
