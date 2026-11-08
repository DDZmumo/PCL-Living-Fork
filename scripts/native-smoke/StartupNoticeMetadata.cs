using System;
using System.Text.RegularExpressions;

// Metadata-derived routing only. This never loads or invokes product code.
internal sealed record StartupNoticeMetadata(string Title, string Style, string Source)
{
    internal static StartupNoticeMetadata Describe(string display, string version, int buildType, bool prerelease, bool promptView, bool notesView)
    {
        if (promptView && !notesView) throw new InvalidOperationException("Native notice prompt has no Markdown renderer metadata.");
        bool published = notesView && buildType == 50 && !prerelease && Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+\z");
        string style = promptView ? "markdown-suppress" : published ? "markdown" : "plain";
        return new(display + " " + version + (style == "plain" ? " 版本说明" : " 更新说明"), style, published ? "published-or-local" : "local");
    }

    internal static void VerifyPolicy()
    {
        void Expect(string version, int build, bool preview, bool prompt, bool notes, string style, string source)
        {
            var actual = Describe("PCL Living Fork", version, build, preview, prompt, notes);
            string title = "PCL Living Fork " + version + (style == "plain" ? " 版本说明" : " 更新说明");
            if (actual != new StartupNoticeMetadata(title, style, source)) throw new InvalidOperationException("Startup notice metadata route differs.");
        }
        Expect("dev_edition", 50, false, true, true, "markdown-suppress", "local");
        Expect("1.4.0", 50, false, true, true, "markdown-suppress", "published-or-local");
        Expect("1.4.1", 50, false, true, true, "markdown-suppress", "published-or-local");
        Expect("1.4.0-pre-4", 50, true, true, true, "markdown-suppress", "local");
        Expect("1.4.0", 0, false, true, true, "markdown-suppress", "local");
        Expect("dev_edition", 100, false, true, true, "markdown-suppress", "local");
        Expect("dev_edition", 50, false, false, true, "plain", "local");
        Expect("1.4.0-pre-4", 50, true, false, true, "plain", "local");
        Expect("1.4.0", 50, false, false, true, "markdown", "published-or-local");
        Expect("1.4.1", 50, false, false, true, "markdown", "published-or-local");
        Expect("1.4.0", 0, false, false, true, "plain", "local");
        Expect("1.4.0", 50, true, false, true, "plain", "local");
        Expect("1.4.0", 50, false, false, false, "plain", "local");
        Expect("1.4.2-pre-1", 50, true, true, true, "markdown-suppress", "local");
        Expect("1.4.2-pre-1", 50, true, false, true, "plain", "local");
        bool refused = false;
        try { Describe("PCL Living Fork", "dev_edition", 50, false, true, false); }
        catch (InvalidOperationException) { refused = true; }
        if (!refused) throw new InvalidOperationException("Inconsistent prompt/renderer metadata was accepted.");
        Console.WriteLine("PASS 16 metadata-only startup notice checks: new Markdown/suppression, earlier stable Markdown, legacy plain and inconsistent-type refusal; no product code or profile loaded.");
    }
}
