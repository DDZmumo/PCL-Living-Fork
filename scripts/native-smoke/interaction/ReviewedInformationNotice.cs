using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace PclLfNativeInteraction;

// Exact reviewed informational dialogs only; no regex or unknown-dialog fallback.
internal static class ReviewedInformationNotice
{
    private const string CurrentHighlights = "- PCLLF 原创独立代码采用 MPL-2.0，关于页可离线查看许可与来源\r\n- 个人基础设置同步独立侧栏，全局和版本设置均支持编辑后保存\r\n- 修复跨平台前置误报和模组更新漏报，保留本地依赖版本范围检查\r\n- 首页更多设置可统一收起面板，展开沿用 PCL 回弹动画\r\n- 修复深色主题提示框，完善原生开关、键位和拖动条对照\r\n- 预发布精简版适用于 Windows x64，运行前需 .NET 10 Desktop Runtime";
    internal const string SuppressionLabel = "下次更新前不再弹出";

    internal static ReviewedNotice ForTitle(string title, string style = "plain", string source = "local")
    {
        if (style == "plain" && source == "local") return new(title, style, source, CaptionForTitle(title), "");
        string version = title switch
        {
            "PCL Living Fork dev_edition 更新说明" => "dev_edition",
            "PCL Living Fork 1.4.2-pre-1 更新说明" => "1.4.2-pre-1",
            "PCL Living Fork 1.4.0 更新说明" => "1.4.0",
            "PCL Living Fork 1.4.1 更新说明" => "1.4.1",
            _ => throw new InvalidOperationException("PENDING: only exact reviewed Markdown notice titles may be dismissed.")
        };
        if (style is not ("markdown-suppress" or "markdown") || source is not ("local" or "published-or-local") ||
            (source == "published-or-local" && version is not ("1.4.0" or "1.4.1")) || (style == "markdown" && (version is not ("1.4.0" or "1.4.1") || source != "published-or-local")))
            throw new InvalidOperationException("PENDING: reviewed Markdown notice metadata binding differs.");
        string caption = "基于 PCL 官方开源版的第三方版本。\r\n" + (style == "markdown" ? "暂未读取到当前版本的完整发布说明，以下为本地简述。" : "以下为当前版本的本地简述。");
        string plain = CaptionForTitle("PCL Living Fork " + version + " 版本说明");
        return new(title, style, source, caption, plain[(plain.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
    }

    internal static string NormalizeDocument(string text)
    {
        // UIA may omit list markers or return a bullet and extra paragraph
        // whitespace. Keep every word, punctuation mark and its order intact.
        string withoutMarkers = Regex.Replace(text, @"(?m)^[\t ]*[-*+\u2022\u25cf\u25aa][\t ]*", "");
        return Regex.Replace(withoutMarkers, @"\s+", " ").Trim();
    }

    internal static void VerifyRichGeometry(Rect client, Rect title, Rect content, Rect document, Rect? suppression, Rect confirm, Rect published)
    {
        var all = new List<Rect> { title, content, document, confirm, published };
        if (suppression is Rect check) all.Add(check);
        if (all.Any(rect => rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0 || !client.Contains(rect)) ||
            title.Bottom > content.Top || !content.Contains(document) || content.Bottom > confirm.Top || content.Bottom > published.Top ||
            confirm.IntersectsWith(published) || (suppression is Rect footer && (!content.Contains(footer) || document.Bottom > footer.Top || footer.IntersectsWith(confirm) || footer.IntersectsWith(published))))
            throw new InvalidOperationException("PENDING: reviewed Markdown notice/document/footer/button geometry differs or is clipped.");
    }

    internal static string CaptionForTitle(string title)
    {
        string version = title switch
        {
            "PCL Living Fork 1.3.5-pre-3 版本说明" => "1.3.5-pre-3",
            "PCL Living Fork 1.4.0-pre-4 版本说明" => "1.4.0-pre-4",
            "PCL Living Fork 1.4.0 版本说明" => "1.4.0",
            "PCL Living Fork 1.4.1 版本说明" => "1.4.1",
            "PCL Living Fork dev_edition 版本说明" => "dev_edition",
            "PCL Living Fork 1.4.2-pre-1 版本说明" => "1.4.2-pre-1",
            _ => throw new InvalidOperationException("PENDING: only exact reviewed information notices may be dismissed.")
        };
        string highlights = version == "1.3.5-pre-3"
            ? "- 首页可开关模组依赖检查与自动补全\r\n- 整合包更新按分类预览，下载显示实际进度\r\n- 还原点可选设置自动清理上限，手动记录单独保留"
            : (version == "1.4.0-pre-4" ? "- 主产品迁移至 C#/.NET 10，精简版需 .NET 10 Desktop Runtime\r\n- 更新页新增版本搜索、当前版本标记与 Markdown 说明\r\n- 修复图片、中文搜索、资源详情异常及删除版本卡顿" : (version is "1.4.1" or "dev_edition" ? "- 全局项目收藏：下载、版本资源管理与收藏页同步，跨文件和版本去重\r\n- 整合包更新独立侧栏，预览中处理缺失前置与不兼容模组\r\n- 更新任务分项显示原生进度，增加文件读取和写入速度\r\n- 顶栏快捷切换深浅色，圆角侧栏、渐变光圈与夜间星光\r\n- 更新公告可选择下次更新前不再弹出，暂缓更新后可从顶栏重开\r\n- 改善部分 Windows 10 启动兼容性，更新 README 与项目日历\r\n- 精简版适用于 Windows x64，运行前需 .NET 10 Desktop Runtime" : "- 汇总四个预发布版本，包含依赖、主题、登录隔离与本次实机修复\r\n- C#/.NET 10 与 Windows x64；运行前需 .NET 10 Desktop Runtime\r\n- 完善依赖索引、内嵌前置识别与补全、深浅色主题和面板不透明度\r\n- 原生更新页支持版本搜索、通道选择与 Markdown 更新说明\r\n- 备份、克隆、恢复显示实际进度；存档与资源支持正倒序\r\n- 微软登录缓存使用 _LF 隔离；修复图标、中文搜索与删除卡顿\r\n- 整包更新保护个人修改和世界数据，修复配置误关联与冲突预览"));
        if (version is "dev_edition" or "1.4.2-pre-1") highlights = CurrentHighlights;
        return "PCL Living Fork " + version + " 是基于 PCL 官方开源版的第三方版本。\r\n\r\n" + highlights;
    }

    internal static void VerifyPolicy()
    {
        foreach (string version in new[] { "1.3.5-pre-3", "1.4.0-pre-4", "1.4.0", "1.4.1", "1.4.2-pre-1", "dev_edition" })
        {
            string caption = CaptionForTitle("PCL Living Fork " + version + " 版本说明");
            string expected = "PCL Living Fork " + version + " 是基于 PCL 官方开源版的第三方版本。\r\n\r\n" +
                (version == "1.3.5-pre-3"
                    ? "- 首页可开关模组依赖检查与自动补全\r\n- 整合包更新按分类预览，下载显示实际进度\r\n- 还原点可选设置自动清理上限，手动记录单独保留"
                    : (version == "1.4.0-pre-4" ? "- 主产品迁移至 C#/.NET 10，精简版需 .NET 10 Desktop Runtime\r\n- 更新页新增版本搜索、当前版本标记与 Markdown 说明\r\n- 修复图片、中文搜索、资源详情异常及删除版本卡顿" : (version is "1.4.1" or "dev_edition" ? "- 全局项目收藏：下载、版本资源管理与收藏页同步，跨文件和版本去重\r\n- 整合包更新独立侧栏，预览中处理缺失前置与不兼容模组\r\n- 更新任务分项显示原生进度，增加文件读取和写入速度\r\n- 顶栏快捷切换深浅色，圆角侧栏、渐变光圈与夜间星光\r\n- 更新公告可选择下次更新前不再弹出，暂缓更新后可从顶栏重开\r\n- 改善部分 Windows 10 启动兼容性，更新 README 与项目日历\r\n- 精简版适用于 Windows x64，运行前需 .NET 10 Desktop Runtime" : "- 汇总四个预发布版本，包含依赖、主题、登录隔离与本次实机修复\r\n- C#/.NET 10 与 Windows x64；运行前需 .NET 10 Desktop Runtime\r\n- 完善依赖索引、内嵌前置识别与补全、深浅色主题和面板不透明度\r\n- 原生更新页支持版本搜索、通道选择与 Markdown 更新说明\r\n- 备份、克隆、恢复显示实际进度；存档与资源支持正倒序\r\n- 微软登录缓存使用 _LF 隔离；修复图标、中文搜索与删除卡顿\r\n- 整包更新保护个人修改和世界数据，修复配置误关联与冲突预览")));
            if (version is "dev_edition" or "1.4.2-pre-1") expected = "PCL Living Fork " + version + " 是基于 PCL 官方开源版的第三方版本。\r\n\r\n" + CurrentHighlights;
            if (!string.Equals(caption, expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Reviewed information notice body changed.");
        }
        foreach (string title in new[] { "PCL Living Fork 更新失败", "PCL Living Fork 1.4.0-pre-5 版本说明", "PCL Living Fork dev_edition 版本说明 suffix", "PCL Living Fork dev_edition\n版本说明", "" })
        {
            bool refused = false;
            try { _ = CaptionForTitle(title); }
            catch (InvalidOperationException) { refused = true; }
            if (!refused) throw new InvalidOperationException("Unknown information notice was accepted.");
        }
        void Refuse(Action action)
        {
            bool refused = false; try { action(); } catch (InvalidOperationException) { refused = true; }
            if (!refused) throw new InvalidOperationException("Unsafe rich information notice was accepted.");
        }
        foreach (string version in new[] { "dev_edition", "1.4.0", "1.4.1", "1.4.2-pre-1" })
        {
            string title = "PCL Living Fork " + version + " 更新说明", source = version is "dev_edition" or "1.4.2-pre-1" ? "local" : "published-or-local";
            var notice = ForTitle(title, "markdown-suppress", source);
            string log = "00:00:00.001 I [MessageDialogController] 普通弹窗：" + title + "\r\n" + string.Join("\r\n", notice.LogCaption.Split("\r\n").Select(line => "00:00:00.001 I [MessageDialogController] " + line)) + "\r\n";
            notice.VerifyLogCaption(log);
            notice.VerifyDocument(notice.DocumentText);
            notice.VerifyDocument(notice.DocumentText.Replace("- ", "• ").Replace("\r\n", "\r\n\r\n"));
            notice.VerifyDocument(notice.DocumentText.Replace("- ", ""));
            Refuse(() => notice.VerifyDocument(notice.DocumentText + "\r\n额外内容"));
            Refuse(() => notice.VerifyDocument(notice.DocumentText.Replace("更新", "删除")));
            Refuse(() => notice.VerifyDocument(notice.DocumentText[..40]));
            Refuse(() => notice.VerifyLogCaption(log.Replace("本地简述", "完整发布说明")));
            Refuse(() => notice.VerifyLogCaption(log.TrimEnd('\r', '\n')));
            Refuse(() => notice.VerifyLogCaption(log + "00:00:00.002 I [MessageDialogController] extra\r\n"));
            Refuse(() => notice.VerifyLogCaption(log + log));
            Refuse(() => notice.VerifyLogCaption(log.Replace("普通弹窗：", "选择弹窗：")));
            Refuse(() => notice.VerifyLogCaption(log + "00:00:00.002 I [MessageDialogController] 普通弹窗：\r\n"));
            Refuse(() => notice.VerifyLogCaption(log + "00:00:00.002 I [MessageDialogController] 普通弹窗：删除所有版本？\r\n"));
            Refuse(() => ForTitle(title, "plain", source));
        }
        Refuse(() => ForTitle("PCL Living Fork 1.4.2 更新说明", "markdown-suppress", "local"));
        Refuse(() => ForTitle("PCL Living Fork dev_edition 更新说明", "markdown-suppress", "published-or-local"));
        Refuse(() => ForTitle("PCL Living Fork dev_edition 更新说明 suffix", "markdown-suppress", "local"));
        var earlier = ForTitle("PCL Living Fork 1.4.0 更新说明", "markdown", "published-or-local");
        earlier.VerifyDocument(earlier.DocumentText);
        earlier.VerifyLogCaption("00:00:00.001 I [MessageDialogController] 普通弹窗：PCL Living Fork 1.4.0 更新说明\r\n00:00:00.001 I [MessageDialogController] 基于 PCL 官方开源版的第三方版本。\r\n00:00:00.001 I [MessageDialogController] 暂未读取到当前版本的完整发布说明，以下为本地简述。\r\n");
        Refuse(() => ForTitle("PCL Living Fork 1.4.0 更新说明", "markdown", "local"));
        var client = new Rect(0, 0, 1000, 800); var titleRect = new Rect(100, 50, 400, 30);
        var content = new Rect(100, 100, 700, 450); var document = new Rect(100, 100, 700, 390); var checkRect = new Rect(100, 510, 240, 20);
        var confirm = new Rect(550, 580, 60, 20); var published = new Rect(650, 580, 100, 20);
        VerifyRichGeometry(client, titleRect, content, document, checkRect, confirm, published);
        Refuse(() => VerifyRichGeometry(client, titleRect, content, document, new Rect(100, 480, 240, 20), confirm, published));
        Refuse(() => VerifyRichGeometry(client, titleRect, content, document, checkRect, new Rect(550, 540, 60, 20), published));
        Refuse(() => VerifyRichGeometry(client, titleRect, content, document, checkRect, confirm, confirm));
        Refuse(() => VerifyRichGeometry(new Rect(0, 0, 600, 800), titleRect, content, document, checkRect, confirm, published));
        Console.WriteLine("PASS exact reviewed plain/Markdown notice policy: complete local body, metadata/log caption, marker/whitespace variants and footer geometry; unknown, changed, clipped, duplicate and online notices refused. No UI, product, profile or input action.");
    }
}

internal sealed record ReviewedNotice(string Title, string Style, string Source, string LogCaption, string DocumentText)
{
    internal bool IsMarkdown => Style != "plain";
    internal bool HasSuppression => Style == "markdown-suppress";

    internal void VerifyDocument(string text)
    {
        if (!IsMarkdown || text.Length > 16000 || ReviewedInformationNotice.NormalizeDocument(text) != ReviewedInformationNotice.NormalizeDocument(DocumentText))
            throw new InvalidOperationException("PENDING: actual FlowDocument is not the complete reviewed local Markdown body.");
    }

    internal void VerifyLogCaption(string log)
    {
        foreach (Match dialog in Regex.Matches(log, @"(?<Kind>普通|选择|输入)弹窗：(?<Title>[^\r\n]*)"))
            if (dialog.Groups["Kind"].Value != "普通" || dialog.Groups["Title"].Value != Title)
                throw new InvalidOperationException("PENDING: unexpected actual dialog, never auto-clicked: " + dialog.Groups["Title"].Value);
        if (!IsMarkdown) return;
        var body = new List<string>(); bool current = false; int count = 0;
        foreach (Match line in Regex.Matches(log, @"(?m)^\d{2}:\d{2}:\d{2}\.\d{3} I (?:<[^>\r\n]*> )?\[MessageDialogController\] (?<Content>[^\r\n]*)\r?\n"))
        {
            string content = line.Groups["Content"].Value;
            if (Regex.IsMatch(content, @"^(?:普通|选择|输入)弹窗："))
            {
                current = content == "普通弹窗：" + Title;
                if (current) count++;
            }
            else if (current && content.Length > 0) body.Add(content);
        }
        if (!log.EndsWith('\n') || count != 1 || string.Join("\r\n", body) != LogCaption)
            throw new InvalidOperationException("PENDING: only the complete reviewed local notice log caption authorizes Markdown dismissal.");
    }
}
