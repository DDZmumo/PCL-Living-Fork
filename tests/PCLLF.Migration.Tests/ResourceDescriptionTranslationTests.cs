// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Newtonsoft.Json.Linq;
using PCLCS;

internal static class ResourceDescriptionTranslationTests {
    internal static IEnumerable<(string Name,Action Run)> Cases() {
        yield return ("description translation caches by provider project and exact source text", IdentityCache);
        yield return ("description translation survives corrupt cache and rejects cross-project responses and canceled requests", FailureBoundaries);
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static void IdentityCache() {
        string root=Path.Combine(Path.GetTempPath(),"pcllf-description-"+Guid.NewGuid().ToString("N"));
        try {
            int requests=0;
            var provider=new ResourceDescriptionTranslation((url,token)=>{
                requests++;return new JObject{["project_id"]="123",["original"]="English summary",["translated"]="中文简介"};
            }){CacheDirectory=root};
            var first=provider.Get(ResourcePlatforms.Modrinth,"123","English summary");
            Check(first is {IsCurrent:true,Translated:"中文简介"}&&requests==1,"Public translation was not returned with its matching source.");
            provider.Get(ResourcePlatforms.Modrinth,"123","English summary");Check(requests==1,"Repeated clicks refetched the same translation.");
            provider.Get(ResourcePlatforms.CurseForge,"123","English summary");Check(requests==2,"Providers shared a project-ID-only translation cache.");
            var changed=provider.Get(ResourcePlatforms.Modrinth,"123","Updated summary");
            Check(requests==3&&changed is {IsCurrent:false,Original:"English summary"},"An old translation silently replaced the updated original.");
            var disk=new ResourceDescriptionTranslation((_,_)=>throw new IOException("offline")){CacheDirectory=root};
            Check(disk.Get(ResourcePlatforms.Modrinth,"123","English summary")?.Translated=="中文简介","A second session could not reuse the valid disk cache offline.");
        } finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private static void FailureBoundaries() {
        string root=Path.Combine(Path.GetTempPath(),"pcllf-description-"+Guid.NewGuid().ToString("N"));
        try {
            int calls=0;
            var provider=new ResourceDescriptionTranslation((_,_)=>{calls++;return new JObject{["project_id"]="abc",["original"]="text",["translated"]="译文"};}){CacheDirectory=root};
            provider.Get(ResourcePlatforms.Modrinth,"abc","text");
            foreach(string file in Directory.GetFiles(root))File.WriteAllText(file,"{bad json");
            var fresh=new ResourceDescriptionTranslation((_,_)=>{calls++;return new JObject{["project_id"]="different",["translated"]="wrong"};}){CacheDirectory=root};
            bool rejected=false;try{fresh.Get(ResourcePlatforms.Modrinth,"abc","text");}catch(InvalidDataException){rejected=true;}
            Check(rejected&&calls==2,"A damaged cache hid the network failure or a different project's translation was accepted.");
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            bool stopped=false;try{fresh.Get(ResourcePlatforms.Modrinth,"abc","text",cancel.Token);}catch(OperationCanceledException){stopped=true;}
            Check(stopped&&calls==2,"A canceled description request still reached the provider.");
        } finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
