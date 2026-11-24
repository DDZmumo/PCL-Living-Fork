using System.Diagnostics;
using System.Globalization;

namespace SchematicKit;

/// <summary>
///     库级设置。所有成员都是可选覆盖项：不设置时使用中文文案、CurrentUICulture 决定方块名称语言、
///     日志写入 <see cref="System.Diagnostics.Debug" />。
/// </summary>
public static class SchematicOptions
{
    /// <summary>
    ///     异常日志回调。库里所有非致命异常都会走这里，默认写入 Debug 输出。
    ///     宿主可以指向自己的日志系统，避免异常被静默吞掉。
    /// </summary>
    public static Action<Exception, string>? ErrorLog { get; set; }

    /// <summary>
    ///     方块名称使用的语言文件，例如 <c>zh_cn</c> / <c>en_us</c>。
    ///     为 null 时：界面语言是中文就用 zh_cn，否则 en_us。
    /// </summary>
    public static string? BlockLanguage { get; set; }

    /// <summary>打开外部链接；默认交给系统浏览器。</summary>
    public static Action<string>? OpenUrl { get; set; } = url =>
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    };

    /// <summary>打开文件夹；默认交给资源管理器。</summary>
    public static Action<string>? OpenFolder { get; set; } = path =>
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    };

    /// <summary>是否把中文当作首选语言（决定方块名称与数字格式）。</summary>
    public static bool PreferChinese =>
        BlockLanguage is { Length: > 0 }
            ? BlockLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            : CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>实际使用的方块语言文件名（不含扩展名）。</summary>
    public static string EffectiveBlockLanguage =>
        BlockLanguage is { Length: > 0 } language
            ? language
            : PreferChinese
                ? "zh_cn"
                : "en_us";

    /// <summary>把非致命异常交给 <see cref="ErrorLog" />（或 Debug 输出）。宿主可以复用它记录自己的处理失败。</summary>
    public static void Log(Exception exception, string message)
    {
        if (ErrorLog is { } handler)
        {
            try
            {
                handler(exception, message);
            }
            catch
            {
                // 日志回调自己出错时不能再抛，否则预览会整个失败
            }

            return;
        }

        System.Diagnostics.Debug.WriteLine("[SchematicKit] " + message + Environment.NewLine + exception);
    }
}
