using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace PCLCS;

public enum DownloadPageEase { None, OutFluent, OutFluentExtraStrong }
/// <summary>Original animation-engine primitives; C# owns every plan and callback.</summary>
public sealed class DownloadPageAnimationHost<TAnimation> {
    public Func<int> Disabled = null!;
    public Action<int> SetDisabled = null!;
    public Func<FrameworkElement, double, double, int, DownloadPageEase, TAnimation> Opacity = null!, TranslateX = null!, TranslateY = null!;
    public Func<FrameworkElement, double, double, int, DownloadPageEase, bool, TAnimation> Scale = null!;
    public Func<FrameworkElement, DependencyProperty, string, int, int, DownloadPageEase, TAnimation> Color = null!;
    public Func<Action, int, bool, TAnimation> Code = null!;
    public Action<IEnumerable<TAnimation>, string, bool> Start = null!;
    public Action<string> Stop = null!;
}
