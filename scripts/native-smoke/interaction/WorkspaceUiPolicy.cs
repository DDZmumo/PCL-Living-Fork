// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;

namespace PclLfNativeInteraction;

/// <summary>Admits only the observed integrated workspace, or the unchanged historical classic shell.</summary>
internal static class ShellUiPolicy
{
    private static readonly (string Id, string Name)[] Navigation = [
        ("NavLaunch", "启动"), ("NavDownload", "下载"), ("NavFavorites", "收藏"),
        ("NavSettings", "设置"), ("NavOther", "更多")
    ];
    internal static (string Id, string Name) HeaderTarget(string name) => Navigation.Single(item => item.Name == name);
    internal static bool IsGlassMarker(ShellNode node) => node.ClassName == "GlassLauncherView" ||
        node.AutomationId is "AccountButton" or "SidebarScroller" || Navigation.Any(item => item.Id == node.AutomationId);

    internal static ObservedShell Detect(IReadOnlyList<ShellNode> nodes, System.Windows.Rect client, double scale)
    {
        bool classicLaunch = nodes.Any(node => ClassicHeader(node, "启动", client));
        bool classicSettings = nodes.Any(node => ClassicHeader(node, "设置", client));
        if (!nodes.Any(IsGlassMarker)) {
            One(nodes, node => ClassicHeader(node, "启动", client), "classic launch LabText");
            One(nodes, node => ClassicHeader(node, "设置", client), "classic settings LabText");
            return ObservedShell.Classic;
        }
        if (classicLaunch || classicSettings) throw Pending("mixed workspace/classic navigation peers");
        var buttons = Navigation.Select(item => One(nodes, node => node.AutomationId == item.Id, item.Id)).ToArray();
        for (int index = 0; index < buttons.Length; index++) {
            var target = Navigation[index];
            if (!IsGlassHeaderTarget(buttons[index], target.Id, target.Name, client, scale)) throw Pending("workspace navigation identity/geometry differs: " + target.Id);
            if (index > 0 && (buttons[index].Bounds.Top < buttons[index - 1].Bounds.Bottom ||
                Math.Abs(buttons[index].Bounds.Left - buttons[index - 1].Bounds.Left) > scale ||
                Math.Abs(buttons[index].Bounds.Width - buttons[index - 1].Bounds.Width) > scale ||
                Math.Abs(buttons[index].Bounds.Top - buttons[index - 1].Bounds.Top - 52 * scale) > scale))
                throw Pending("workspace primary navigation is not the exact ordered vertical rail");
        }
        var account = One(nodes, node => node.AutomationId == "AccountButton", "AccountButton");
        if (account.Type != ControlType.Button || account.ClassName != "Button" ||
            !account.Name.StartsWith("当前账户：", StringComparison.Ordinal) || account.Name.Length <= "当前账户：".Length ||
            !Inside(account.Bounds, client) || Math.Abs(account.Bounds.Left - client.Left - 12 * scale) > 1 ||
            Math.Abs(account.Bounds.Right - client.Left - 52 * scale) > 1 ||
            Math.Abs(account.Bounds.Top - client.Bottom + 52 * scale) > 1 ||
            Math.Abs(account.Bounds.Bottom - client.Bottom + 12 * scale) > 1)
            throw Pending("workspace integrated account control identity/geometry differs");
        return ObservedShell.Workspace;
    }
    internal static bool IsGlassHeaderTarget(ShellNode node, string id, string name, System.Windows.Rect client, double scale)
        => Scale(scale) && Inside(node.Bounds, client) && node.Type == ControlType.Button && node.ClassName == "Button" &&
           node.AutomationId == id && node.Name == name && node.Bounds.Left >= client.Left + 4 * scale &&
           node.Bounds.Right <= client.Left + 64 * scale && node.Bounds.Top >= client.Top + 72 * scale &&
           node.Bounds.Bottom <= client.Top + 364 * scale && node.Bounds.Width >= 40 * scale &&
           node.Bounds.Width <= 56 * scale && node.Bounds.Height >= 36 * scale && node.Bounds.Height <= 48 * scale;

    internal static bool IsGlassContextPane(ShellNode node, System.Windows.Rect client, double scale)
        => Scale(scale) && Inside(node.Bounds, client) && node.Type == ControlType.Pane && node.ClassName == "ScrollViewer" &&
           node.AutomationId == "SidebarScroller" && node.Bounds.Left >= client.Left + 80 * scale &&
           node.Bounds.Right <= client.Left + 344 * scale && node.Bounds.Top >= client.Top + 64 * scale &&
           node.Bounds.Bottom <= client.Bottom - 12 * scale;

    internal static bool IsGlassContextTarget(ShellNode node, string name, ShellNode pane, System.Windows.Rect client, double scale)
        => IsGlassContextPane(pane, client, scale) && Inside(node.Bounds, pane.Bounds) &&
           node.Type == ControlType.Button && node.ClassName == "Button" && node.AutomationId.Length == 0 && node.Name == name &&
           node.Bounds.Width >= 100 * scale && node.Bounds.Width <= 232 * scale &&
           node.Bounds.Height >= 28 * scale && node.Bounds.Height <= 48 * scale;

    private static bool ClassicHeader(ShellNode node, string name, System.Windows.Rect client) =>
        node.Type == ControlType.Text && node.AutomationId == "LabText" && node.Name == name && node.Bounds.Bottom <= client.Top + 57;
    private static bool Scale(double scale) => double.IsFinite(scale) && scale > 0;
    private static bool Inside(System.Windows.Rect bounds, System.Windows.Rect client) => !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 && client.Contains(bounds);
    private static ShellNode One(IEnumerable<ShellNode> nodes, Func<ShellNode, bool> match, string label) {
        var found = nodes.Where(match).ToArray();
        if (found.Length != 1) throw Pending("expected one visible " + label + "; found " + found.Length);
        return found[0];
    }
    private static InvalidOperationException Pending(string reason) => new("PENDING: " + reason + ".");

    internal static void VerifyPolicy()
    {
        var client = new System.Windows.Rect(100, 200, 850, 500);
        ShellNode Classic(string name, double bottom = 45) => new("LabText", name, "TextBlock", ControlType.Text, new(client.Left + 320, client.Top + bottom - 20, 30, 20));
        ShellNode[] classic = [Classic("启动"), Classic("设置")];
        Require(Detect(classic, client, 1) == ObservedShell.Classic, "Classic policy changed.");
        Require(Detect([Classic("启动", 57), Classic("设置", 57)], client, 1) == ObservedShell.Classic, "Classic boundary changed.");
        Reject(() => Detect([Classic("启动", 58), Classic("设置")], client, 1));
        Reject(() => Detect([classic[0]], client, 1));
        Reject(() => Detect([classic[0], classic[1], classic[1]], client, 1));
        foreach (double scale in new[] { 1d, 1.25d, 1.5d })
        foreach (double width in new[] { 810d, 1120d }) {
            var frame = new System.Windows.Rect(100, 200, width * scale, 470 * scale);
            System.Windows.Rect Bounds(double x, double y, double w, double h) => new(frame.Left + x * scale, frame.Top + y * scale, w * scale, h * scale);
            var workspace = Navigation.Select((item, index) => new ShellNode(item.Id, item.Name, "Button", ControlType.Button, Bounds(9, 85 + index * 52, 48, 42)))
                .Append(new("AccountButton", "当前账户：CI", "Button", ControlType.Button, Bounds(12, 418, 40, 40))).ToArray();
            Require(Detect(workspace, frame, scale) == ObservedShell.Workspace, "Integrated workspace was refused.");
            Reject(() => Detect(workspace.Concat(classic).ToArray(), frame, scale));
            Reject(() => Detect(workspace.Where(node => node.AutomationId != "NavSettings").ToArray(), frame, scale));
            Reject(() => Detect(workspace.Append(workspace[3]).ToArray(), frame, scale));
            foreach (ShellNode invalid in new[] {
                workspace[3] with { Name = "启动" }, workspace[3] with { Type = ControlType.Text },
                workspace[3] with { Bounds = Bounds(200, 269, 62, 60) }, workspace[3] with { Bounds = workspace[0].Bounds },
                workspace[3] with { Bounds = Bounds(9, 269, 2, 60) }, workspace[3] with { Bounds = Bounds(9, 10, 62, 60) }
            }) Reject(() => Detect(workspace.Select(node => node.AutomationId == "NavSettings" ? invalid : node).ToArray(), frame, scale));
            Reject(() => Detect(workspace.Select(node => node.AutomationId == "AccountButton" ? node with { Name = "账户" } : node).ToArray(), frame, scale));
            var account = workspace[^1];
            ShellNode[] WithAccount(ShellNode replacement) => workspace.Select(node => node.AutomationId == "AccountButton" ? replacement : node).ToArray();
            Require(Detect(WithAccount(account with { Bounds = Bounds(12, 418 + .5 / scale, 40, 40) }), frame, scale) == ObservedShell.Workspace,
                "The exact account footer boundary was refused.");
            foreach (ShellNode invalid in new[] {
                account with { Bounds = Bounds(12, 420, 40, 40) }, account with { Bounds = Bounds(12, 393, 40, 40) },
                account with { Bounds = Bounds(61, 418, 40, 40) }, account with { ClassName = "TextBlock" },
                account with { Type = ControlType.Text }
            }) Reject(() => Detect(WithAccount(invalid), frame, scale));
            var pane = new ShellNode("SidebarScroller", "", "ScrollViewer", ControlType.Pane, Bounds(113, 105, 204, 286));
            var item = new ShellNode("", "个性化", "Button", ControlType.Button, Bounds(113, 140, 204, 36));
            Require(IsGlassContextPane(pane, frame, scale), "Context sidebar was refused.");
            Require(IsGlassContextTarget(item, "个性化", pane, frame, scale), "Workspace contextual button was refused.");
            Require(!IsGlassContextTarget(item with { Name = "初始化本页设置" }, "个性化", pane, frame, scale), "A reset action was admitted.");
            Require(!IsGlassContextTarget(item with { Type = ControlType.Text }, "个性化", pane, frame, scale), "A text peer was admitted.");
            Require(!IsGlassContextTarget(item with { Bounds = Bounds(350, 140, 180, 36) }, "个性化", pane, frame, scale), "A page-content or popup peer was admitted.");
            Require(!IsGlassContextTarget(item with { Bounds = Bounds(120, 408, 180, 36) }, "个性化", pane, frame, scale), "An out-of-sidebar peer was admitted.");
            Require(!IsGlassContextTarget(item, "个性化", pane with { ClassName = "Button" }, frame, scale), "A context popup trigger was admitted.");
            Require(!IsGlassContextTarget(item, "个性化", pane with { AutomationId = "PanBack" }, frame, scale), "An unrelated scroller was admitted.");
        }
        Console.WriteLine("PASS workspace shell policy: exact vertical navigation and integrated account/context controls; classic ownership/geometry rules retained; mixed, missing, duplicate, displaced and out-of-frame peers refused.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action) {
        try { action(); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("PENDING:", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("An invalid workspace identity was accepted.");
    }
}
