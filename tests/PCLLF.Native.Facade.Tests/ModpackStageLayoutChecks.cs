using System.Windows;
using System.Windows.Controls;
using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static class ModpackStageLayoutChecks
{
    internal static void Run(string root)
    {
        using var taskbar = new TaskbarScope();
        var task = new ModLoader.LoaderCombo<string>(new string('长', 180), [
            new ModLoader.LoaderTask<string, int>("验证任务阶段", _ => { })
        ]);
        ModLoader.LoaderTaskbarAdd(task);
        var page = new GlassTaskManagerPage(LauncherApplicationServices.TaskManager);
        page.Open();
        page.Measure(new Size(400, 500));
        page.Arrange(new Rect(0, 0, 400, 500));
        page.UpdateLayout();
        var card = ((System.Collections.IDictionary)Native.Get(page, "_cards")!).Values.Cast<object>().Single();
        var cancel = (Button)Native.Get(card, "_cancel")!;
        Native.Check(cancel.ActualWidth > 0 && cancel.TranslatePoint(new Point(cancel.ActualWidth, 0), page).X <= 400,
            "A long task title pushed cancel beyond the visible card.");
    }

    internal static void CaptureTask(ModLoader.LoaderCombo<string> task, string path)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() => {
            var page = new GlassTaskManagerPage(LauncherApplicationServices.TaskManager);
            page.Open();
            Save(page, path, 850, 560);
        });
    }

    internal static void Save(FrameworkElement element, string path, int width, int height) =>
        VisualAssetChecks.Render(element, path, width, height);

    private sealed class TaskbarScope : IDisposable
    {
        private readonly List<ModLoader.LoaderBase> _prior = ModLoader.LoaderTaskbar.ToList();
        public TaskbarScope() => ModLoader.LoaderTaskbar.Clear();
        public void Dispose() { ModLoader.LoaderTaskbar.Clear(); foreach (var task in _prior) ModLoader.LoaderTaskbar.Add(task); }
    }
}
