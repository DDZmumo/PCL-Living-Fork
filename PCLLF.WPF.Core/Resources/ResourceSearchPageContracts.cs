using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;

namespace PCLCS;

public sealed class ResourceSearchPageView {
    public TextBox Name = null!;
    public ComboBox Version = null!, Source = null!, Tag = null!, Loader = null!;
    public FrameworkElement InstallPack = null!, Pages = null!, First = null!, Left = null!, Right = null!, Error = null!;
    public TextBlock PageNumber = null!;
    public Panel Projects = null!;
    public Action<string> SearchTitle = null!, LoadingText = null!, ErrorText = null!;
    public Action ScrollHome = null!, ScrollTop = null!;
}
public sealed class ResourceSearchPageHost<TInstance, TProject, TResult, TRequest>
    where TInstance : class where TProject : ResourceProjectModelData
    where TResult : ResourceSearchResultData<TProject> {
    public Func<ResourceSearchPageView> View = null!;
    public Func<TInstance?> TargetInstance = null!;
    public Action<TInstance?> SetTargetInstance = null!;
    public Func<string?> TargetName = null!;
    public Action<string?> SetTargetName = null!;
    public Func<TResult> Storage = null!;
    public Action<TResult> SetStorage = null!;
    public Func<int> Page = null!;
    public Action<int> SetPage = null!;
    public Func<ResourceTypes, TResult, int, TRequest> Request = null!;
    public Func<TRequest> Input = null!;
    public Func<TRequest, bool> ShouldStart = null!;
    public Action Start = null!, ResetFinishedTime = null!, InitializeLoader = null!, Restart = null!, BackToTop = null!, InstallPack = null!;
    public Action<string> LoaderName = null!;
    public Func<bool> Failed = null!;
    public Func<Exception?> Error = null!;
    public Func<List<int>?> AllDrops = null!;
    public Func<object, object?, bool, ComboBoxItem> Item = null!;
    public Func<TProject, bool, bool, FrameworkElement> ProjectItem = null!;
    public Action<Action> RunInThread = null!, RunInUi = null!;
}
