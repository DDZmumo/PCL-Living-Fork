// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed class SymbioLauncherAsset {
    public string Name = "", Url = "", Sha256 = "";
    public long Size;
}

public sealed class SymbioLauncherSwitchHost {
    public Func<JObject> Release = null!;
    public Action<SymbioLauncherAsset, string> Download = null!;
    public Action<string> Hint = null!, Start = null!;
    public Action Close = null!;
}

/// <summary>The toolbox surprise; its host owns UI, network tasks and process lifetime.</summary>
public sealed class SymbioLauncherSwitch {
    public const string Repository = "https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition";
    public const string LatestApi = "https://api.github.com/repos/Hydrogen-Peroxide/PCL-Symbio-Edition/releases/latest";
    public const string Warning = "此操作可能导致软件无响应或崩溃，请先保存正在进行的操作。\r\n\r\n确定继续？";
    private readonly SymbioLauncherSwitchHost host;
    public SymbioLauncherSwitch(SymbioLauncherSwitchHost host) => this.host = host;

    private static bool OfficialName(string name) => Regex.IsMatch(name,
        @"\APCL-Symbio-Edition-\d+\.\d+\.\d+(?:\.\d+)?-x64\.exe\z", RegexOptions.IgnoreCase);

    public static string? FindLocal(string folder, string currentExecutable) {
        return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentExecutable), StringComparison.OrdinalIgnoreCase))
            .Where(path => string.Equals(Path.GetFileName(path), "PCLSE.exe", StringComparison.OrdinalIgnoreCase) || OfficialName(Path.GetFileName(path)))
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .OrderBy(path => string.Equals(Path.GetFileName(path), "PCLSE.exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    public static SymbioLauncherAsset SelectAsset(JObject release) {
        string tag = release["tag_name"]?.ToString() ?? "";
        if (release["draft"]?.Value<bool>() != false || release["prerelease"]?.Value<bool>() != false ||
            !Regex.IsMatch(tag, @"\Av?\d+\.\d+\.\d+(?:\.\d+)?\z")) throw new IOException("发布记录无效。");
        string name = "PCL-Symbio-Edition-" + tag.TrimStart('v') + "-x64.exe";
        JObject[] matches = (release["assets"] as JArray ?? new JArray()).OfType<JObject>()
            .Where(asset => asset["name"]?.ToString() == name && asset["state"]?.ToString() == "uploaded").ToArray();
        if (matches.Length != 1) throw new IOException("发布中没有唯一可用的 x64 程序。");
        JObject asset = matches[0];
        string url = asset["browser_download_url"]?.ToString() ?? "", digest = asset["digest"]?.ToString() ?? "";
        string expected = Repository + "/releases/download/" + tag + "/" + name;
        long size = asset["size"]?.Value<long>() ?? 0;
        if (url != expected || !Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z") || size < 1024 || size > 256L * 1024 * 1024)
            throw new IOException("发布附件的地址、大小或校验信息无效。");
        return new SymbioLauncherAsset { Name = name, Url = url, Sha256 = digest.Substring(7), Size = size };
    }

    private static void VerifyHeader(Stream input) {
        if (input.Length < 2 || input.ReadByte() != 'M' || input.ReadByte() != 'Z') throw new IOException("程序文件无效。");
        input.Position = 0;
    }

    private static void VerifyDownload(Stream input, SymbioLauncherAsset asset) {
        VerifyHeader(input);
        using SHA256 sha = SHA256.Create();
        string actual = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
        if (input.Length != asset.Size || !string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("下载文件校验失败。");
    }

    public void Run(string executableFolder, string currentExecutable) {
        SymbioLauncherAsset? downloaded = null;
        string? target = FindLocal(executableFolder, currentExecutable);
        if (target is null) {
            host.Hint("正在打包一份小惊喜，请稍候～");
            SymbioLauncherAsset asset = SelectAsset(host.Release());
            downloaded = asset;
            string stage = Path.Combine(executableFolder, ".pcl-surprise-" + Guid.NewGuid().ToString("N") + ".tmp");
            target = Path.Combine(executableFolder, "PCLSE.exe");
            try {
                host.Hint("正在搬运神秘包裹，马上就好～");
                host.Download(asset, stage);
                using (FileStream input = new(stage, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    VerifyDownload(input, asset);
                }
                // A concurrently created local executable wins; never overwrite a user's file.
                File.Move(stage, target);
            } finally { if (File.Exists(stage)) File.Delete(stage); }
        } else host.Hint("咦，彩蛋已经藏在这里了～");
        using (FileStream program = new(target, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            if (downloaded is null) VerifyHeader(program);
            else VerifyDownload(program, downloaded);
            host.Hint("惊喜就位，马上出发～");
            host.Start(target);
            host.Close();
        }
    }
}
