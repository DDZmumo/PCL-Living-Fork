using System;
using System.Windows;
using System.Windows.Controls;

namespace PCLCS;

public sealed class InstanceRamDisplayView {
    public TextBlock Game = null!, Used = null!, Total = null!, GameTitle = null!, UsedTitle = null!;
    public FrameworkElement RectUsed = null!, Display = null!;
    public ColumnDefinition UsedColumn = null!, GameColumn = null!, EmptyColumn = null!;
    public Func<int> LeftState = null!, RightState = null!;
    public Action<int> SetLeftState = null!, SetRightState = null!, SetSliderMax = null!;
}
public sealed class InstanceRamDisplayHost {
    public Func<bool> Active = null!;
    public Func<double> Ram = null!, TotalBytes = null!, AvailableBytes = null!;
    public Func<int> Animation = null!;
    public Func<string,bool> IsAnimating = null!;
    public Action<double,double,double> AnimateGrid = null!, AnimateLeft = null!;
    public Action<double,double> AnimateRight = null!;
}
public sealed class InstanceRamDisplayController {
    private readonly InstanceRamDisplayView view; private readonly InstanceRamDisplayHost host;
    public InstanceRamDisplayController(InstanceRamDisplayView view, InstanceRamDisplayHost host) { this.view = view; this.host = host; }
    public void Refresh(bool animate) {
        if (view.Game is null || view.Used is null || !host.Active()) return;
        double game = Math.Round(host.Ram(),5), total = Math.Round(host.TotalBytes() / 1024 / 1024 / 1024,1), available = Math.Round(host.AvailableBytes() / 1024 / 1024 / 1024,1);
        double actual = Math.Round(Math.Min(game,available),5), used = Math.Round(total-available,5), empty = Math.Round(Math.Min(Math.Max(total-used-game,0),1000),1);
        view.SetSliderMax(InstanceRamPolicy.SliderMaximum(total));
        view.Game.Text = InstanceRamPolicy.Amount(game) + " GB" + (game != actual ? " (可用 " + InstanceRamPolicy.Amount(actual) + " GB)" : "");
        view.Used.Text = InstanceRamPolicy.Amount(used) + " GB"; view.Total.Text = " / " + InstanceRamPolicy.Amount(total) + " GB";
        if (animate) host.AnimateGrid(used-view.UsedColumn.Width.Value,actual-view.GameColumn.Width.Value,empty-view.EmptyColumn.Width.Value);
        else { view.UsedColumn.Width = new GridLength(used,GridUnitType.Star); view.GameColumn.Width = new GridLength(actual,GridUnitType.Star); view.EmptyColumn.Width = new GridLength(empty,GridUnitType.Star); }
    }
    public void RefreshText() {
        double used = view.RectUsed.ActualWidth, total = view.Display.ActualWidth, game = view.Game.ActualWidth, usedLabel = view.Used.ActualWidth, totalLabel = view.Total.ActualWidth, gameTitle = view.GameTitle.ActualWidth, usedTitle = view.UsedTitle.ActualWidth;
        int left = used - 30 < usedLabel || used - 30 < usedTitle ? 0 : used - 25 < usedLabel + totalLabel ? 1 : 2;
        if (view.LeftState() != left) { view.SetLeftState(left); host.AnimateLeft((left == 0 ? 0 : 1)-view.Used.Opacity,(left == 2 ? 1 : 0)-view.Total.Opacity,(left == 0 ? 0 : 0.7)-view.UsedTitle.Opacity); }
        int right = total < game + 2 + used || total < gameTitle + 2 + used ? 0 : 1;
        double x = right == 0 ? total-game : 2+used, titleX = right == 0 ? total-gameTitle : 2+used;
        if (host.Animation() == 0 && (view.RightState() != right || host.IsAnimating("VersionSetup Ram TextRight"))) host.AnimateRight(x-view.Game.Margin.Left,titleX-view.GameTitle.Margin.Left);
        else { view.Game.Margin = new Thickness(x,3,0,0); view.GameTitle.Margin = new Thickness(titleX,0,0,5); }
        view.SetRightState(right);
    }
}
