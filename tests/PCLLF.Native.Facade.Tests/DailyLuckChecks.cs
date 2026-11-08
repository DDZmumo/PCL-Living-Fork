using System.Windows.Input;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class DailyLuckChecks {
    internal static void Run() {
        object runtime = Native.Field(Native.Type("PCL.AuxiliaryPageRuntime"), "Tools").GetValue(null)!;
        object host = Native.Get(runtime, "Host")!, common = Native.Get(host, "Common")!;
        List<Action> restore = new();
        void Replace(object target, string name, object value) {
            var field = Native.Field(target.GetType(), name); object? old = field.GetValue(target);
            restore.Add(() => field.SetValue(target, old)); field.SetValue(target, value);
        }
        List<(string Text, string Title, bool Warn)> messages = new(); int response = 1;
        try {
            Replace(host, "LuckDate", new Func<DateTime>(() => new DateTime(2026,11,8,12,0,0)));
            Replace(host, "LuckIdentify", new Func<string>(() => "fixture-machine"));
            Replace(host, "LuckTimeChanged", new Func<bool>(() => false));
            Replace(common, "Message", new LegacySetupMessage((text,title,b1,b2,b3,warn) => { messages.Add((text,title,warn)); return response; }));
            PageOtherTest page = new(); MyButton button = (MyButton)page.FindName("BtnJrrp");
            void Click() { messages.Clear(); Native.Call(typeof(MyButton), "OnClick", button, button, new MouseButtonEventArgs(InputManager.Current.PrimaryMouseDevice, 0, MouseButton.Left)); }
            Click();
            Native.Check(button.Text == "今日人品" && messages.Count == 1 && messages[0] == ("你今天的人品值是：26！呜……", "今日人品 - 2026/11/8", false),
                "Daily luck's actual native button lost the reference algorithm/result dialog.");
            Replace(host, "LuckTimeChanged", new Func<bool>(() => true)); Click();
            Native.Check(messages[0].Text.StartsWith("在这个时候", StringComparison.Ordinal) && messages[0].Title.StartsWith("今日人品?", StringComparison.Ordinal), "Clock-change wording was lost.");
            Replace(host, "LuckIdentify", new Func<string>(() => "boundary-79")); response = 2; Click();
            Native.Check(messages.Count == 1 && messages[0].Warn && messages[0].Title.Contains("附加使用条款", StringComparison.Ordinal), "Zero-score cancellation did not stop before revealing the result.");
            response = 1; Click(); Native.Check(messages.Count == 2 && messages[1].Warn && messages[1].Text.Contains("0？！", StringComparison.Ordinal), "Zero-score confirmation lost the low-score warning.");
            Replace(host, "LuckIdentify", new Func<string>(() => "boundary-55")); Click();
            Native.Check(messages.Count == 1 && messages[0].Text.Contains("100！100！100", StringComparison.Ordinal), "Full-score wording changed.");
        } finally { foreach (Action action in restore.AsEnumerable().Reverse()) action(); }
        Console.WriteLine("PASS daily luck: real native click/service and fixed PCL299 vector, changed-clock wording, zero-score confirm/cancel and full score; no original executable or machine identifier accessed.");
    }
}
