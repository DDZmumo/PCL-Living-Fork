using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PCLCS;

public sealed class ResourceItemView {
    public Grid Root = null!;
    public TextBlock Title = null!, RawTitle = null!, Info = null!;
    public ToolTip InfoToolTip = null!;
    public Panel Tags = null!;
}
public sealed class ResourceItemHost<TProject, TRequest, TInstance> {
    public Func<ResourceItemView> View = null!;
    public Func<int> Uuid = null!, CurrentPage = null!;
    public int DownloadPage, ResourceDetailPage, InstanceSetupPage;
    public Func<bool> DetailAvailable = null!, DownloadAvailable = null!;
    public Func<IEnumerable<(string Title, bool IsSwapped)>> ExpandedCards = null!;
    public Func<TRequest?> CurrentSearch = null!;
    public Func<(TRequest? Search,TInstance? Instance)> CurrentDownloadContext = null!;
    public Func<object[]?> CurrentArguments = null!;
    public Func<TInstance?> CurrentInstance = null!;
    public Action<object[]> OpenDetail = null!;
    public Action<object, MouseButtonEventArgs> RaiseClick = null!;
    public Func<TextBlock, bool> TextTrimmed = null!;
    public Func<string, Brush> Brush = null!;
    public Func<double, double> PixelSize = null!;
}
