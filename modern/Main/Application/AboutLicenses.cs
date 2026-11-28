// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using MeloongCore.Wpf;

namespace PCL;

internal static class AboutLicenses
{
    internal static string Read(string name)
    {
        using var stream = Open(name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Stream Open(string name) =>
        Application.GetResourceStream(new Uri("pack://application:,,,/LivingPCL;component/Licenses/" + name))?.Stream
        ?? throw new InvalidOperationException("Missing embedded license/source resource: " + name);

    internal static void WriteTransformerSource(Stream output)
    {
        const string prefix = "HMCLTransformerDiscoveryService/";
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        using var manifest = JsonDocument.Parse(Read(prefix + "source-manifest.json"));
        AddFile("source-manifest.json");
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
            AddFile(file.GetProperty("path").GetString()!);

        void AddFile(string path)
        {
            using var source = Open(prefix + path);
            using var destination = archive.CreateEntry(path).Open();
            source.CopyTo(destination);
        }
    }

    internal static void SaveTransformerSource()
    {
        try
        {
            using var source = new MemoryStream();
            WriteTransformerSource(source);
            var path = Dialogs.SaveFile("保存组件对应源码", "HMCLTransformerDiscoveryService-1.0-source.zip",
                filter: new[] { ("zip", "ZIP 压缩文件") });
            if (path is null) return;
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            source.Position = 0;
            source.CopyTo(file);
            ModMain.Hint("已保存 Transformer Discovery Service 对应源码。", ModMain.HintType.Green);
        }
        catch (Exception error) { ModMain.Hint("保存组件对应源码失败：" + error.Message, ModMain.HintType.Red); }
    }
}
