using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PCLLF.Presentation.Controls;

namespace PCLLF.Presentation.Animation;

// LF Modules/Base/ModAnimation.vb Aa factories; see root LICENCE.
// Same-source C# migration reference: CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5,
// Plain Craft Launcher 2/Modules/Base/ModAnimation.cs; CE LICENSE and its PCL LICENCE.
// Actual LF structs, boxed color identity and late-bound primitive reads are supplied by the host.
public sealed class AnimationFactories<TData> where TData : struct, IAnimationData {
    private readonly AnimationHost host;
    public AnimationFactories(AnimationHost host) { this.host = host; }

    private TData Number(AnimationSubKind subKind, object obj, double value, int time, int delay, AnimationEase? ease, bool after) {
        TData data = default;
        data.Kind = AnimationKind.Number;
        data.SubKind = subKind;
        data.Duration = time;
        data.Easing = ease ?? host.NewLinearEasing();
        data.Target = obj;
        data.Value = value;
        data.After = after;
        data.Elapsed = checked(-delay);
        return data;
    }
    public TData AaX(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null, bool after = false) =>
        Number(AnimationSubKind.X, obj, value, time, delay, ease, after);
    public TData AaY(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null, bool after = false) =>
        Number(AnimationSubKind.Y, obj, value, time, delay, ease, after);
    public TData AaWidth(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null, bool after = false) =>
        Number(AnimationSubKind.Width, obj, value, time, delay, ease, after);
    public TData AaHeight(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null, bool after = false) =>
        Number(AnimationSubKind.Height, obj, value, time, delay, ease, after);
    public TData AaOpacity(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null, bool after = false) =>
        Number(AnimationSubKind.Opacity, obj, value, time, delay, ease, after);
    public TData AaValue(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null, bool after = false) =>
        Number(AnimationSubKind.Value, obj, value, time, delay, ease, after);
    public TData AaRadius(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null, bool after = false) =>
        Number(AnimationSubKind.Radius, obj, value, time, delay, ease, after);
    public TData AaBorderThickness(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null, bool after = false) =>
        Number(AnimationSubKind.BorderThickness, obj, value, time, delay, ease, after);
    public TData AaStrokeThickness(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null, bool after = false) =>
        Number(AnimationSubKind.StrokeThickness, obj, value, time, delay, ease, after);
    public TData AaGridLengthWidth(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null, bool after = false) =>
        Number(AnimationSubKind.GridLengthWidth, obj, value, time, delay, ease, after);

    public TData AaDouble(object obj, DependencyProperty prop, double value, int time = 400, int delay = 0,
        AnimationEase? ease = null, bool after = false) {
        TData data = default;
        data.Kind = AnimationKind.Number;
        data.SubKind = AnimationSubKind.Double;
        data.Duration = time;
        data.Easing = ease ?? host.NewLinearEasing();
        data.Target = new object[] { obj, prop, "" };
        data.Value = value;
        data.After = after;
        data.Elapsed = checked(-delay);
        return data;
    }
    public TData AaDouble(ParameterizedThreadStart lambda, double value, int time = 400, int delay = 0,
        AnimationEase? ease = null, bool after = false) => Number(AnimationSubKind.DoubleParam, lambda, value, time, delay, ease, after);

    public TData AaColor(FrameworkElement obj, DependencyProperty prop, object? value, int time = 400, int delay = 0,
        AnimationEase? ease = null, bool after = false) {
        TData data = default;
        data.Kind = AnimationKind.Color;
        data.Duration = time;
        data.Easing = ease ?? host.NewLinearEasing();
        data.Target = new object[] { obj, prop, "" };
        data.Value = value;
        data.After = after;
        data.Elapsed = checked(-delay);
        data.Remainder = host.BoxColor(new ControlColor(0, 0, 0, 0));
        return data;
    }
    public TData AaColor(FrameworkElement obj, DependencyProperty prop, string res, int time = 400, int delay = 0,
        AnimationEase? ease = null, bool after = false) {
        TData data = default;
        data.Kind = AnimationKind.Color;
        data.Duration = time;
        data.Easing = ease ?? host.NewLinearEasing();
        data.Target = new object[] { obj, prop, res };
        object? resource = obj.TryFindResource(res) ?? host.FindResource(res);
        data.Value = host.BoxColor(AnimationColors.Subtract(host.ReadColor(resource), host.ReadColor(obj.GetValue(prop))));
        data.After = after;
        data.Elapsed = checked(-delay);
        data.Remainder = host.BoxColor(new ControlColor(0, 0, 0, 0));
        return data;
    }

    private double Dimension(object obj, bool width) {
        if (obj is FrameworkElement element) return width ? element.ActualWidth : element.ActualHeight;
        return host.Number(host.GetProperty(obj, width ? "ActualWidth" : "ActualHeight"));
    }
    public TData AaScale(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null,
        bool after = false, bool absolute = false) {
        object changeRect = host.BoxRect(absolute
            ? new AnimationRect(-0.5 * value, -0.5 * value, value, value)
            : new AnimationRect(-0.5 * Dimension(obj, true) * value, -0.5 * Dimension(obj, false) * value,
                Dimension(obj, true) * value, Dimension(obj, false) * value));
        TData data = default;
        data.Kind = AnimationKind.Scale;
        data.Duration = time;
        data.Easing = ease ?? host.NewLinearEasing();
        data.Target = obj;
        data.Value = changeRect;
        data.After = after;
        data.Elapsed = checked(-delay);
        return data;
    }

    private object? Text(object obj) => obj is TextBlock block ? block.Text : host.GetProperty(obj, "Context")!.ToString();
    public TData AaTextAppear(object obj, bool hide = false, bool timePerText = true, int time = 70, int delay = 0,
        AnimationEase? ease = null, bool after = false) {
        TData data = default;
        data.Kind = AnimationKind.TextAppear;
        data.Easing = ease ?? host.NewLinearEasing();
        data.Duration = timePerText ? checked(time * Text(obj)!.ToString()!.Length) : time;
        data.Target = obj;
        data.Value = new object?[] { Text(obj), hide };
        data.After = after;
        data.Elapsed = checked(-delay);
        return data;
    }
    public TData AaCode(ThreadStart code, int delay = 0, bool after = false) {
        TData data = default;
        data.Kind = AnimationKind.Code;
        data.Duration = 1;
        data.Value = code;
        data.After = after;
        data.Elapsed = checked(-delay);
        return data;
    }

    private TData Transform(AnimationKind kind, object obj, double value, int time, int delay, AnimationEase? ease, bool after) {
        TData data = default;
        data.Kind = kind;
        data.Duration = time;
        data.Easing = ease ?? host.NewLinearEasing();
        data.Target = obj;
        data.Value = value;
        data.After = after;
        data.Elapsed = checked(-delay);
        return data;
    }
    public TData AaScaleTransform(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null, bool after = false) =>
        Transform(AnimationKind.ScaleTransform, obj, value, time, delay, ease, after);
    public TData AaRotateTransform(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null, bool after = false) =>
        Transform(AnimationKind.RotateTransform, obj, value, time, delay, ease, after);
    public TData AaTranslateX(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null, bool after = false) =>
        Number(AnimationSubKind.TranslateX, obj, value, time, delay, ease, after);
    public TData AaTranslateY(object obj, double value, int time = 400, int delay = 0, AnimationEase? ease = null, bool after = false) =>
        Number(AnimationSubKind.TranslateY, obj, value, time, delay, ease, after);

    public List<TData> AaStack(StackPanel stack, int time = 100, int delay = 25) {
        List<TData> result = new();
        int animationDelay = 0;
        foreach (UIElement item in stack.Children) {
            item.Opacity = 0;
            result.Add(AaOpacity(item, 1, time, animationDelay));
            animationDelay = checked(animationDelay + delay);
        }
        return result;
    }
}
