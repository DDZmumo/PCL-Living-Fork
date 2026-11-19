using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PCLLF.Presentation.Controls;

// LF Controls/MyResizer.vb (no corresponding MyResizer in fixed CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5).
// The legacy shell retains the original worker. The glass shell coalesces input at the render cadence.
public class WindowResizer {
    [StructLayout(LayoutKind.Sequential)] private struct PointData { public int X, Y; public PointData(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public PointData Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct RectangleData {
        public int Left, Top, Right, Bottom;
        public int Width => checked(Right - Left);
        public int Height => checked(Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private sealed class MonitorInfo {
        public int Size = Marshal.SizeOf(typeof(MonitorInfo)); public RectangleData Monitor, Work; public int Flags;
    }
    private readonly Window target;
    private readonly Func<double, double> wpfSize;
    private readonly Action<Action, string> startWorker;
    private readonly Action<bool>? liveResizeChanged;
    private bool resizeRight, resizeLeft, resizeUp, resizeDown;
    private readonly Dictionary<UIElement, short> leftElements = new(), rightElements = new(), upElements = new(), downElements = new();
    private PointData startMousePoint, startWindowLeftUpPoint;
    private Size startWindowSize;
    private static int workAreaMaxHeight = -1;
    private HwndSource? hs;
    private UIElement? resizeGrip;
    private HwndSource? resizeSource;
    private RectangleData initialBounds, currentBounds;
    private PointData initialCursor;
    private int workAreaBottom;
    private TimeSpan? lastResizeFrame;

    public WindowResizer(Window target, Func<double, double>? pixelToWpf = null, Action<Action, string>? worker = null, Action<bool>? liveResizeChanged = null) {
        this.target = target; if (target is null) throw new Exception("Invalid Window handle");
        if (pixelToWpf is null) {
            double dpi = System.Drawing.Graphics.FromHwnd(IntPtr.Zero).DpiX;
            wpfSize = value => value * 96 / dpi;
        } else wpfSize = pixelToWpf;
        startWorker = worker ?? ((work, name) => new Thread(() => { try { work(); } catch (Exception error) { ControlServices.Current.Error(error, name + "：线程执行失败"); } }) { Name = name }.Start());
        this.liveResizeChanged = liveResizeChanged;
        target.SourceInitialized += SourceInitialized;
    }
    private void SourceInitialized(object? sender, EventArgs args) { hs = PresentationSource.FromVisual((Visual)sender!) as HwndSource; hs!.AddHook(WndProc); }
    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (message == 36) { GetMinMaxInfo(hwnd, lParam); handled = true; } return IntPtr.Zero;
    }
    private static void GetMinMaxInfo(IntPtr hwnd, IntPtr pointer) {
        MinMaxInfo info = (MinMaxInfo)Marshal.PtrToStructure(pointer, typeof(MinMaxInfo))!;
        IntPtr monitor = MonitorFromWindow(hwnd, 2);
        if (monitor != IntPtr.Zero) {
            MonitorInfo state = new(); GetMonitorInfo(monitor, state);
            info.MaxPosition.X = Math.Abs(checked(state.Work.Left - state.Monitor.Left)); info.MaxPosition.Y = Math.Abs(checked(state.Work.Top - state.Monitor.Top));
            info.MaxSize.Y = Math.Abs(checked(state.Work.Bottom - state.Work.Top)); workAreaMaxHeight = info.MaxSize.Y;
            if (state.Work.Height == state.Monitor.Height) info.MaxSize.Y = checked(info.MaxSize.Y - 2);
        }
        // This handler marks the message handled, which also skips WPF's own MinWidth/MinHeight enforcement.
        // Apply the window's minimum here (in device pixels) so native edge resizing cannot go below it.
        if (System.Windows.Interop.HwndSource.FromHwnd(hwnd) is { RootVisual: System.Windows.Window window, CompositionTarget: { } target }) {
            System.Windows.Media.Matrix toDevice = target.TransformToDevice;
            if (window.MinWidth > 0) info.MinTrackSize.X = (int)Math.Ceiling(window.MinWidth * toDevice.M11);
            if (window.MinHeight > 0) info.MinTrackSize.Y = (int)Math.Ceiling(window.MinHeight * toDevice.M22);
        }
        Marshal.StructureToPtr(info, pointer, true);
    }
    private void Connect(UIElement element) => element.MouseLeftButtonDown += MouseDown;
    public void addResizerRight(UIElement element) { Connect(element); rightElements.Add(element, 0); }
    public void addResizerLeft(UIElement element) { Connect(element); leftElements.Add(element, 0); }
    public void addResizerUp(UIElement element) { Connect(element); upElements.Add(element, 0); }
    public void addResizerDown(UIElement element) { Connect(element); downElements.Add(element, 0); }
    public void addResizerRightDown(UIElement element) { Connect(element); rightElements.Add(element, 0); downElements.Add(element, 0); }
    public void addResizerLeftDown(UIElement element) { Connect(element); leftElements.Add(element, 0); downElements.Add(element, 0); }
    public void addResizerRightUp(UIElement element) { Connect(element); rightElements.Add(element, 0); upElements.Add(element, 0); }
    public void addResizerLeftUp(UIElement element) { Connect(element); leftElements.Add(element, 0); upElements.Add(element, 0); }
    private static int Integer(double value) => Convert.ToInt32(Math.Round(value));
    private void MouseDown(object sender, MouseButtonEventArgs args) {
        UIElement grip = (UIElement)sender;
        if (resizeGrip is not null) return;
        resizeLeft = leftElements.ContainsKey(grip);
        resizeRight = rightElements.ContainsKey(grip);
        resizeUp = upElements.ContainsKey(grip);
        resizeDown = downElements.ContainsKey(grip);
        if (liveResizeChanged is not null && StartFrameResize(grip)) {
            args.Handled = true;
            return;
        }
        GetCursorPos(out startMousePoint); startMousePoint.X = Integer(wpfSize(startMousePoint.X)); startMousePoint.Y = Integer(wpfSize(startMousePoint.Y));
        startWindowSize = new Size(target.Width, target.Height); startWindowLeftUpPoint = new PointData(Integer(target.Left), Integer(target.Top));
        startWorker(UpdateSizeLoop, "窗口大小调整检测");
    }

    private bool StartFrameResize(UIElement grip) {
        if (PresentationSource.FromVisual(target) is not HwndSource { Handle: var handle } source || handle == IntPtr.Zero ||
            !GetWindowRect(handle, out initialBounds) || !GetCursorPos(out initialCursor) || !grip.CaptureMouse()) return false;
        resizeGrip = grip;
        resizeSource = source;
        currentBounds = initialBounds;
        lastResizeFrame = null;
        var monitor = MonitorFromWindow(handle, 2);
        var monitorInfo = new MonitorInfo();
        workAreaBottom = monitor != IntPtr.Zero && GetMonitorInfo(monitor, monitorInfo) ? monitorInfo.Work.Bottom : int.MaxValue;
        grip.MouseLeftButtonUp += EndFrameResize;
        grip.LostMouseCapture += CancelFrameResize;
        target.Closed += CancelFrameResize;
        liveResizeChanged?.Invoke(true);
        CompositionTarget.Rendering += ResizeOnFrame;
        return true;
    }

    private void ResizeOnFrame(object? sender, EventArgs args) {
        if (Mouse.LeftButton == MouseButtonState.Released) {
            FinishFrameResize(applyFinalPosition: true);
            return;
        }
        if (args is not RenderingEventArgs frame || frame.RenderingTime == lastResizeFrame) return;
        lastResizeFrame = frame.RenderingTime;
        ApplyFrameResize();
    }

    private void ApplyFrameResize() {
        if (resizeSource?.CompositionTarget is not { } composition || !GetCursorPos(out PointData cursor)) return;
        Matrix toDevice = composition.TransformToDevice;
        int dx = cursor.X - initialCursor.X;
        int dy = cursor.Y - initialCursor.Y;
        int minWidth = Math.Max(1, (int)Math.Ceiling(target.MinWidth * toDevice.M11));
        int minHeight = Math.Max(1, (int)Math.Ceiling(target.MinHeight * toDevice.M22));
        int maxWidth = PixelLimit(target.MaxWidth, toDevice.M11);
        int maxHeight = PixelLimit(target.MaxHeight, toDevice.M22);
        if (resizeDown && workAreaBottom != int.MaxValue) maxHeight = Math.Min(maxHeight, workAreaBottom - initialBounds.Top);

        var bounds = initialBounds;
        if (resizeLeft || resizeRight) {
            int width = Math.Clamp(initialBounds.Width + (resizeLeft ? -dx : dx), minWidth, Math.Max(minWidth, maxWidth));
            if (resizeLeft) bounds.Left = bounds.Right - width;
            else bounds.Right = bounds.Left + width;
        }
        if (resizeUp || resizeDown) {
            int height = Math.Clamp(initialBounds.Height + (resizeUp ? -dy : dy), minHeight, Math.Max(minHeight, maxHeight));
            if (resizeUp) bounds.Top = bounds.Bottom - height;
            else bounds.Bottom = bounds.Top + height;
        }
        if (bounds.Left == currentBounds.Left && bounds.Top == currentBounds.Top &&
            bounds.Right == currentBounds.Right && bounds.Bottom == currentBounds.Bottom) return;
        const uint NoZOrderOrActivate = 0x0004 | 0x0010;
        if (SetWindowPos(resizeSource.Handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, NoZOrderOrActivate))
            currentBounds = bounds;

        static int PixelLimit(double value, double scale) => (int)Math.Min(int.MaxValue, Math.Floor(value * scale));
    }

    private void EndFrameResize(object sender, MouseButtonEventArgs args) {
        args.Handled = true;
        FinishFrameResize(applyFinalPosition: true);
    }
    private void CancelFrameResize(object? sender, EventArgs args) => FinishFrameResize(applyFinalPosition: false);
    private void FinishFrameResize(bool applyFinalPosition) {
        if (resizeGrip is not { } grip) return;
        if (applyFinalPosition) ApplyFrameResize();
        resizeGrip = null;
        resizeSource = null;
        CompositionTarget.Rendering -= ResizeOnFrame;
        target.Closed -= CancelFrameResize;
        grip.MouseLeftButtonUp -= EndFrameResize;
        grip.LostMouseCapture -= CancelFrameResize;
        if (grip.IsMouseCaptured) grip.ReleaseMouseCapture();
        liveResizeChanged?.Invoke(false);
    }

    private void UpdateSizeLoop() {
        try {
            while (resizeDown || resizeLeft || resizeRight || resizeUp) {
                target.Dispatcher.Invoke(new Action(UpdateSize), DispatcherPriority.Render);
                target.Dispatcher.Invoke(new Action(UpdateMouseDown), DispatcherPriority.Render); Thread.Sleep(0);
            }
        } catch { }
    }
    private void UpdateSize() {
        GetCursorPos(out PointData point); point.X = Integer(wpfSize(point.X)); point.Y = Integer(wpfSize(point.Y));
        try {
            double width = -1, height = -1, left = -10000, top = -10000;
            int DeltaX() => checked(startMousePoint.X - point.X);
            int DeltaY() => checked(startMousePoint.Y - point.Y);
            if (resizeRight) {
                if (target.Width == target.MinWidth) { if (startMousePoint.X < point.X) width = startWindowSize.Width - DeltaX(); }
                else width = startWindowSize.Width - DeltaX() >= target.MinWidth ? startWindowSize.Width - DeltaX() : target.MinWidth;
            }
            if (resizeDown) {
                if (target.Height == target.MinHeight) {
                    if (startMousePoint.Y < point.Y) height = workAreaMaxHeight > 0
                        ? (startWindowSize.Height - DeltaY() + target.Top <= workAreaMaxHeight ? startWindowSize.Height - DeltaY() : workAreaMaxHeight - target.Top) : startWindowSize.Height - DeltaY();
                } else if (startWindowSize.Height - DeltaY() >= target.MinHeight) height = workAreaMaxHeight > 0
                    ? (startWindowSize.Height - DeltaY() + target.Top <= workAreaMaxHeight ? startWindowSize.Height - DeltaY() : workAreaMaxHeight - target.Top) : startWindowSize.Height - DeltaY();
                else height = target.MinHeight;
            }
            if (resizeLeft) {
                if (target.Width == target.MinWidth) {
                    if (startMousePoint.X > point.X) { width = startWindowSize.Width + startMousePoint.X - point.X; left = checked(startWindowLeftUpPoint.X - DeltaX()); }
                    else { width = target.MinWidth; left = startWindowLeftUpPoint.X + startWindowSize.Width - target.Width; }
                } else if (startWindowSize.Width + DeltaX() >= target.MinWidth) { width = startWindowSize.Width + DeltaX(); left = checked(startWindowLeftUpPoint.X - DeltaX()); }
                else { width = target.MinWidth; left = startWindowLeftUpPoint.X + startWindowSize.Width - target.Width; }
            }
            if (resizeUp) {
                if (target.Height == target.MinHeight) {
                    if (startMousePoint.Y > point.Y) { height = startWindowSize.Height + startMousePoint.Y - point.Y; top = checked(startWindowLeftUpPoint.Y - DeltaY()); }
                    else { height = target.MinHeight; top = startWindowLeftUpPoint.Y + startWindowSize.Height - target.Height; }
                } else if (startWindowSize.Height + DeltaY() >= target.MinHeight) { height = startWindowSize.Height + DeltaY(); top = checked(startWindowLeftUpPoint.Y - DeltaY()); }
                else { height = target.MinHeight; top = startWindowLeftUpPoint.Y + startWindowSize.Height - target.Height; }
            }
            if (width > 10 && Math.Abs(width - target.Width) > .7) target.Width = width;
            if (height > 10 && Math.Abs(height - target.Height) > .7) target.Height = height;
            if (left > -9999 && Math.Abs(left - target.Left) > .7) target.Left = left;
            if (top > -9999 && Math.Abs(top - target.Top) > .7) target.Top = top;
        } catch { }
    }
    private void UpdateMouseDown() { if ((GetAsyncKeyState(1) & 0x8000) != 0) return; resizeRight = false; resizeLeft = false; resizeUp = false; resizeDown = false; }
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(IntPtr monitor, MonitorInfo info);
    [DllImport("User32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] private static extern bool GetCursorPos([Out] out PointData point);
    [DllImport("user32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectangleData bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
