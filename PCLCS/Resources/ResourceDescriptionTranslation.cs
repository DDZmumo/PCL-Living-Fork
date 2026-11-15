// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed record ResourceDescriptionTranslationResult(string Original, string Translated, bool IsCurrent);

/// <summary>On-demand public MCIM project descriptions; never sends account data or local files.</summary>
public sealed class ResourceDescriptionTranslation {
    // Service/reference reviewed in CE ProjectProps.cs at b4949a92ba5ad3b490b139fece0172d8d5b2c846.
    // LF owns its platform+description cache, bounded transport and stale-page handling.
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    private readonly Func<string,CancellationToken,JObject?> request;
    private readonly ConcurrentDictionary<string,SemaphoreSlim> gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,(DateTime Time,ResourceDescriptionTranslationResult Value)> memory = new(StringComparer.Ordinal);
    public string CacheDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PCLLivingFork","Cache","DescriptionTranslations");
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    public ResourceDescriptionTranslation(Func<string,CancellationToken,JObject?>? request = null) => this.request = request ?? Request;
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Normal(string text) => Regex.Replace(text.Trim(),@"\s+"," ");
    public ResourceDescriptionTranslationResult? Get(ResourcePlatforms platform,string id,string description,CancellationToken token = default) {
        token.ThrowIfCancellationRequested();
        if (platform is not ResourcePlatforms.Modrinth and not ResourcePlatforms.CurseForge || id is null || !Regex.IsMatch(id,@"\A[A-Za-z0-9_-]{1,128}\z"))
            throw new ArgumentException("无效的翻译项目身份。");
        if (string.IsNullOrWhiteSpace(description)) return null;
        string identity = ResourceProjectKey.Get(platform,id), key = Digest(identity+"\n"+description);
        SemaphoreSlim gate = gates.GetOrAdd(key,_ => new(1,1)); gate.Wait(token);
        try {
            if (memory.TryGetValue(key,out var saved) && Clock()-saved.Time < TimeSpan.FromHours(6)) return saved.Value;
            string path = Path.Combine(CacheDirectory,key+".json");
            try {
                var info = new FileInfo(path);
                if (info.Exists && info.Length <= 65536 && (info.Attributes&FileAttributes.ReparsePoint)==0) {
                    JObject cached = JObject.Parse(File.ReadAllText(path,Encoding.UTF8));
                    if ((string?)cached["key"]==key && Clock()-(DateTime)cached["time"]! < TimeSpan.FromHours(6)) {
                        var result = new ResourceDescriptionTranslationResult((string)cached["original"]!, (string)cached["translated"]!, (bool)cached["current"]!);
                        if (!string.IsNullOrWhiteSpace(result.Translated)) { memory[key] = ((DateTime)cached["time"]!,result);return result; }
                    }
                }
            } catch(Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidCastException or FormatException or NullReferenceException) { }
            string source = platform == ResourcePlatforms.Modrinth ? "modrinth" : "curseforge";
            JObject? response = request("https://mod.mcimirror.top/translate/"+source+"/"+Uri.EscapeDataString(id),token);
            token.ThrowIfCancellationRequested();
            if (response is null) return null;
            if ((string?)response["project_id"] != id) throw new InvalidDataException("翻译服务返回了其他项目的数据。");
            string? translated = response["translated"]?.Type == JTokenType.String ? (string?)response["translated"] : null;
            if (string.IsNullOrWhiteSpace(translated)) return null;
            string original = (string?)response["original"] ?? description;
            var value = new ResourceDescriptionTranslationResult(original,translated,Normal(original)==Normal(description));
            if (memory.Count>=256) memory.Clear(); memory[key]=(Clock(),value);
            try {
                Directory.CreateDirectory(CacheDirectory);
                if ((File.GetAttributes(CacheDirectory)&FileAttributes.ReparsePoint)!=0) return value;
                var data = new JObject { ["key"]=key,["time"]=Clock(),["original"]=original,["translated"]=translated,["current"]=value.IsCurrent };
                string json=data.ToString(Formatting.None);
                if (Encoding.UTF8.GetByteCount(json)<=65536 && (File.Exists(path)||Directory.GetFiles(CacheDirectory,"*.json").Length<256)) {
                    string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                    try { File.WriteAllText(temporary,json,new UTF8Encoding(false));File.Move(temporary,path,true); }
                    finally { if(File.Exists(temporary))File.Delete(temporary); }
                }
            } catch(Exception error) when(error is IOException or UnauthorizedAccessException) { }
            return value;
        } finally { gate.Release(); }
    }
    private static JObject? Request(string url,CancellationToken token) {
        using var message = new HttpRequestMessage(HttpMethod.Get,url);
        message.Headers.UserAgent.ParseAdd("PCL-Living-Fork/description-translation");
        using var response = Client.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,token).GetAwaiter().GetResult();
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>65536)throw new InvalidDataException("翻译响应过大。");
        using var input=response.Content.ReadAsStream(token);using var output=new MemoryStream();
        byte[] buffer=new byte[4096];int count;
        while((count=input.Read(buffer,0,buffer.Length))!=0){token.ThrowIfCancellationRequested();if(output.Length+count>65536)throw new InvalidDataException("翻译响应过大。");output.Write(buffer,0,count);}
        using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(output.ToArray()))) { MaxDepth=16 };
        return JObject.Load(reader);
    }
}
