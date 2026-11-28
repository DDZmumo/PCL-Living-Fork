using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;
using PCLLF.Presentation.Controls;
using PCLLF.Presentation.Foundation;

namespace PCLLF.Presentation.Shell;

public struct MainHintData<TKind> {
    public string Text;
    public TKind Type;
    public bool Log;
}

public sealed class MainHintHost<TKind> {
    public Func<ConcurrentQueue<MainHintData<TKind>>> Queue = null!;
    public Func<MainHintData<TKind>, bool> Deliver = null!;
    public Action DismissAll = null!;
}

public sealed class MainHintRuntime<TKind>(MainHintHost<TKind> host) {
    public void Enqueue(string? text, TKind type, bool log = true) =>
        host.Queue().Enqueue(new MainHintData<TKind> { Text = text ?? "", Type = type, Log = log });

    public void Tick() {
        try {
            var queue = host.Queue();
            while (queue.TryPeek(out var current)) {
                current.Text = StringExtensions.ReplaceLineEndings(current.Text, " ", mergeMultiple: true);
                if (!host.Deliver(current)) return;
                queue.TryDequeue(out _);
                if (current.Log) Logger.Info($"弹出提示：{current.Text}");
            }
        } catch (Exception error) { Logger.Info(error, "显示弹出提示失败"); }
    }

    public void HideAll() => host.DismissAll();
}
