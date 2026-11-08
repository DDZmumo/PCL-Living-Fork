using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class ToolboxSurpriseChecks {
    internal static void Run() {
        PageOtherTest page = new();
        MyButton button = (MyButton)page.FindName("BtnSurprise");
        MyListItem navigation = (MyListItem)new PageOtherLeft().FindName("ItemTest");
        Native.Check(navigation.Title == "工具百宝箱" && button.Text == "千万别点" && button.ColorType == MyButton.ColorState.Red,
            "Toolbox rename or native red surprise button is missing.");
        var cards = (System.Windows.Controls.StackPanel)((MyScrollViewer)page.FindName("PanBack")).Content;
        foreach (MyCard card in cards.Children) _ = card.MainTextBlock;
        var action = Native.Field(typeof(PageOtherTest), "SurpriseAction");
        object? previous = action.GetValue(null); int clicks = 0;
        try {
            action.SetValue(null, new Action(() => clicks++));
            var args = new MouseButtonEventArgs(InputManager.Current.PrimaryMouseDevice, 0, MouseButton.Left);
            Native.Call(typeof(MyButton), "OnClick", button, button, args);
            Native.Check(clicks == 1, "Actual BAML surprise button is not wired exactly once.");
        } finally { action.SetValue(null, previous); }
        Native.Check(SymbioLauncherSwitch.Warning.Contains("崩溃", StringComparison.Ordinal) && !SymbioLauncherSwitch.Warning.Contains("PCLSE", StringComparison.Ordinal),
            "The requested warning is missing or spoils the surprise.");
        SymbioLauncherAsset asset = new() { Size = 2048, Sha256 = new string('0', 64), Url = SymbioLauncherSwitch.Repository + "/releases/download/v2.17.14/PCL-Symbio-Edition-2.17.14-x64.exe" };
        var task = (ModLoader.LoaderCombo<int>)Native.Static("PCL.PageOtherTest", "CreateSurpriseDownloadTask", asset, Path.Combine(Path.GetTempPath(), ".pcl-surprise-ui.tmp"))!;
        Native.Check(task.Name == "准备一份小惊喜", "Download task names spoil the surprise.");
        page.Measure(new Size(820, 560)); page.Arrange(new Rect(0, 0, 820, 560)); page.UpdateLayout();
        Native.Check(button.ActualWidth > 150 && ((SolidColorBrush)button.PanFore.BorderBrush).Color.R > ((SolidColorBrush)button.PanFore.BorderBrush).Color.G,
            "The native surprise button lost its red frame or usable width.");
        string? folder = Environment.GetEnvironmentVariable("PCLLF_PERSONAL_OPTIONS_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(folder)) {
            Directory.CreateDirectory(folder);
            var image = new RenderTargetBitmap(820, 560, 96, 96, PixelFormats.Pbgra32); image.Render(page);
            PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image));
            using FileStream file = File.Create(Path.Combine(folder, "toolbox-surprise.png")); encoder.Save(file);
        }
        Console.WriteLine("PASS toolbox surprise: renamed native navigation, red button/frame, actual click wiring, mystery warning and neutral native download task. No program download/start/shutdown.");
    }
}
