// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MeloongCore;
using PCLCS;
using SearchRequest = PCL.ResourceSearcher.SearchRequest;
using SearchResult = PCL.ResourceSearcher.SearchResult;
using SearchLoader = PCL.ModLoader.LoaderTask<PCL.ResourceSearcher.SearchRequest, int>;
using McInstance = PCL.ModMinecraft.McInstance;

namespace PCL;

internal sealed record ResourceSearchOptions(string Name, string? Version, string Tag, ModLoaders Loader, ResourcePlatforms Source);

internal sealed record ResourceSearchSnapshot(SearchRequest? Request, IReadOnlyList<ResourceProject> Results,
    int Page, bool Loading, bool CanNext, string? Warning, Exception? Error)
{
    internal bool CanPrevious => Page > 0;
}

/// <summary>Window-independent ownership of a search loader, its stable page and its preview.</summary>
internal sealed class LauncherResourceBrowser : IDisposable
{
    internal const string PageSizeSetting = "ToolDownloadResourcePageSize";
    internal static IReadOnlyList<int> PageSizes { get; } = Array.AsReadOnly(new[] { 15, 30, 40, 60, 80 });
    internal static int PageSize
    {
        get
        {
            int saved = Settings.Get<int>(PageSizeSetting);
            return PageSizes.Contains(saved) ? saved : PageSizes[0];
        }
    }

    private readonly ResourceTypes _type;
    private readonly Action<Action> _dispatch;
    private readonly Func<Action<SearchRequest, IReadOnlyList<ResourceProject>>, SearchLoader> _buildLoader;
    private SearchLoader? _loader;
    private SearchRequest? _stableRequest;
    private int _size;
    private bool _disposed, _retriedInvalidReply;

    internal LauncherResourceBrowser(ResourceTypes type, Action<Action> dispatch,
        Func<Action<SearchRequest, IReadOnlyList<ResourceProject>>, SearchLoader>? buildLoader = null)
    {
        _type = type;
        _dispatch = dispatch;
        _buildLoader = buildLoader ?? (preview => ResourceSearcher.GetLoader(null, preview));
        _size = PageSize;
    }

    internal double Progress => _loader?.Progress ?? 0;
    internal ResourceSearchSnapshot Snapshot { get; private set; } = new(null, [], 0, false, false, null, null);
    internal McInstance? DownloadTarget { get; set; }
    internal event Action? Changed;

    internal void Start(ResourceSearchOptions options, int size)
    {
        var request = new SearchRequest(_type, new(), size)
        {
            SearchText = options.Name, GameVersion = options.Version, ModLoaders = options.Loader,
            Sources = options.Source, Tag = options.Tag, PresentationPageSize = size
        };
        if (_loader is { } loader && !loader.ShouldStart(ref request)) return;
        _size = size;
        _retriedInvalidReply = false;
        Load(request);
    }

    internal void Refresh()
    {
        if (Snapshot.Request is not { } request) return;
        _stableRequest = null;
        _retriedInvalidReply = false;
        Load(CopyRequest(request, new(), _size, _size));
    }

    internal void Resize(int size)
    {
        if (_size == size) return;
        _size = size;
        if (Snapshot.Request is not null) LoadPage(0);
    }

    internal void Navigate(int page)
    {
        if (Snapshot.Request is null || page < 0 || page == Snapshot.Page ||
            page > Snapshot.Page && (!Snapshot.CanNext || page != Snapshot.Page + 1)) return;
        LoadPage(page);
    }

    private void LoadPage(int page)
    {
        SearchRequest previous = Snapshot.Request!;
        // A canceled worker still owns its offsets and result list. Only a completed request can seed another one.
        var completed = _stableRequest is { } stable && stable.SameQuery(previous) ? stable.Storage : null;
        var storage = completed is null ? new SearchResult() : new SearchResult {
            Results = completed.Results.ToList(), ErrorMessage = completed.ErrorMessage,
            CurseForgeOffset = completed.CurseForgeOffset, CurseForgeTotal = completed.CurseForgeTotal,
            ModrinthOffset = completed.ModrinthOffset, ModrinthTotal = completed.ModrinthTotal,
            ModrinthDirectOffset = completed.ModrinthDirectOffset, ModrinthDirectTotal = completed.ModrinthDirectTotal
        };
        Load(CopyRequest(previous, storage, checked((page + 1) * _size), _size));
    }

    private void Load(SearchRequest request)
    {
        if (_disposed) return;
        // Keep each worker's input, error and completion scoped to the request it started with.
        StopLoader();
        var loader = _loader = _buildLoader(QueuePreview);
        loader.OnStateChangedUi += OnStateChanged;
        int size = request.PresentationPageSize;
        int page = (request.TargetResultCount - 1) / size;
        var preview = request.Storage.Results.Skip(page * size).Take(size).ToArray();
        Publish(new(request, preview, page, true, HasNextPage(request, page, preview.Length), null, null));
        if (!_disposed && ReferenceEquals(_loader, loader)) loader.Start(request, IsForceRestart: true);
    }

    private static SearchRequest CopyRequest(SearchRequest input, SearchResult storage, int count, int size) =>
        new(input.Type, storage, count) {
            SearchText = input.SearchText, GameVersion = input.GameVersion, ModLoaders = input.ModLoaders,
            Sources = input.Sources, Tag = input.Tag, PresentationPageSize = size
        };

    private void QueuePreview(SearchRequest request, IReadOnlyList<ResourceProject> results) => _dispatch(() =>
    {
        if (_disposed || !ReferenceEquals(Snapshot.Request, request) || !Snapshot.Loading || results.Count <= Snapshot.Results.Count) return;
        Publish(Snapshot with { Results = results });
    });

    private void OnStateChanged(ModLoader.LoaderBase loader, ModBase.LoadState state, ModBase.LoadState former)
    {
        if (_disposed || !ReferenceEquals(_loader, loader) || loader.State != state ||
            !Snapshot.Loading || Snapshot.Request is not { } request) return;
        int size = request.PresentationPageSize;
        if (state == ModBase.LoadState.Finished)
        {
            _stableRequest = request;
            SearchResult storage = request.Storage;
            int page = Math.Clamp(Snapshot.Page, 0, Math.Max(0, (storage.Results.Count - 1) / size));
            var results = storage.Results.Skip(page * size).Take(size).ToArray();
            Publish(new(request, results, page, false, HasNextPage(request, page, results.Length), storage.ErrorMessage, null));
        }
        else if (state == ModBase.LoadState.Failed)
        {
            if (!_retriedInvalidReply && loader.Error?.Message.Contains("不是有效的 json 文件", StringComparison.Ordinal) == true)
            {
                _retriedInvalidReply = true;
                Load(CopyRequest(request, new(), request.TargetResultCount, size));
                return;
            }
            Publish(Snapshot with { Loading = false, CanNext = false, Error = loader.Error });
        }
        else if (state == ModBase.LoadState.Canceled)
            Publish(Snapshot with { Loading = false, CanNext = false });
    }

    private static bool HasNextPage(SearchRequest request, int page, int visibleCount)
    {
        int size = request.PresentationPageSize;
        if (visibleCount < size) return false;
        SearchResult storage = request.Storage;
        return storage.Results.Count > (page + 1) * size || storage.CurseForgeOffset < storage.CurseForgeTotal ||
            storage.ModrinthOffset < storage.ModrinthTotal || storage.ModrinthDirectOffset < storage.ModrinthDirectTotal;
    }

    private void Publish(ResourceSearchSnapshot snapshot) { Snapshot = snapshot; Changed?.Invoke(); }

    private void StopLoader()
    {
        if (_loader is not { } loader) return;
        _loader = null;
        loader.OnStateChangedUi -= OnStateChanged;
        loader.Cancel();
    }

    public void Dispose()
    {
        _disposed = true;
        StopLoader();
    }
}
