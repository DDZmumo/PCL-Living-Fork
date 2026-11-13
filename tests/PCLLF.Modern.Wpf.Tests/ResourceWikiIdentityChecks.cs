// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCLLF.Modern.Wpf.Tests;
internal static class ResourceWikiIdentityChecks {
    private static ResourceProjectServices Services()=>new(){StoreCache=_=>{},MirrorUrl=s=>s,AllDrops=()=>new List<int>(),ImageRoot=()=>"",Info=_=>{}};
    private static JObject Metadata(string id,string slug,string name,ResourcePlatforms platform=ResourcePlatforms.Modrinth)=>new(){
        ["Tags"]=new JArray(),["Platform"]=(int)platform,["Types"]=(int)ResourceTypes.Mod,["Id"]=id,["Slug"]=slug,["RawName"]=name,
        ["Description"]="fixture",["Website"]="https://example.invalid/"+id,["LastUpdate"]=new DateTime(2026,11,13),["DownloadCount"]=100,["Drops"]=new JArray()};
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    internal static void Run() {
        var errors=new List<Exception>();
        foreach(var item in new (string Name,Action Run)[]{("wiki name/platform identity",Names),("wiki blank identity",Blank),("Chinese search stale slug",Search),("generic wiki collisions and transfer guards",Generic)})
            try{item.Run();Console.WriteLine("PASS "+item.Name);}catch(Exception e){Console.Error.WriteLine("FAIL "+item.Name+": "+e.Message);errors.Add(e);}
        if(errors.Count>0)throw new AggregateException(errors);
    }
    private static void Names() {
        var home=new ResourceProjectModel(Metadata("uJT3ahxz","youkaishomecoming","Youkai's Homecoming"),Services());
        var feast=new ResourceProjectModel(Metadata("Ohy0TgO2","gensokyo-delight-youkais-feasts","Gensokyo Delight ~ Youkais' Feasts"),Services());
        Check(home.WikiId==13185&&home.TranslatedName.Contains("归家")&&!home.TranslatedName.Contains("飨宴"),"Stale Modrinth slug relabeled Homecoming as Feasts.");
        Check(feast.WikiId==21200&&feast.TranslatedName.Contains("飨宴"),"Actual Feasts project lost its verified translation.");
        Check(home.Id=="uJT3ahxz"&&feast.Id=="Ohy0TgO2"&&!home.Equals(feast),"Project identities changed during translation.");
        var cached=new ResourceProjectModel(home.ToJson(),Services());Check(cached.WikiId==13185&&cached.RawName==home.RawName,"Cache restored the stale wiki association.");
        var cfHome=new ResourceProjectModel(Metadata("957437","youkais-homecoming","Gensokyo Delight ~ Youkai's Homecoming",ResourcePlatforms.CurseForge),Services());
        var cfFeast=new ResourceProjectModel(Metadata("1309991","gensokyo-delight-youkais-feasts","Gensokyo Delight ~ Youkais' Feasts",ResourcePlatforms.CurseForge),Services());
        Check(cfHome.WikiId==13185&&cfFeast.WikiId==21200,"Actual CurseForge branding prefix lost project separation.");
        foreach(var item in new[]{("jei","Just Enough Items (JEI)",459),("modmenu","Mod Menu",1675),("sodium","Sodium",2785),("lithium","Lithium",2292)})
            Check(new ResourceProjectModel(Metadata("Sample"+item.Item3,item.Item1,item.Item2),Services()).WikiId==item.Item3,"Existing popular project translation regressed: "+item.Item1);
    }
    private static void Blank() {
        var empty=new ResourceProjectModel(Metadata("UnrelatedP",null,"Unrelated Project"),Services());
        Check(empty.WikiId==0&&empty.TranslatedName=="Unrelated Project","Missing slugs matched missing-platform database fields.");
    }
    private static void Generic() {
        var alpha=new WikiEntry{Id=11,ChineseName="甲项目 (Alpha Project)",Slugs=new(){{ResourcePlatforms.CurseForge,"alpha-project"}}};
        var beta=new WikiEntry{Id=22,ChineseName="乙项目 (Beta Project)",Slugs=new(){{ResourcePlatforms.Modrinth,"alpha-project"}}};
        var entries=new List<WikiEntry>{alpha,beta};var services=Services();services.WikiEntries=()=>entries;
        ResourceProjectModel Create(string id,string slug,string title,ResourcePlatforms platform,int drop) {
            var data=Metadata(id,slug,title,platform);data["Drops"]=new JArray(drop);return new ResourceProjectModel(data,services);
        }
        var cf=Create("CF1","alpha-project","Alpha Project",ResourcePlatforms.CurseForge,120);
        var mr=Create("MR2","alpha-project","Beta Project",ResourcePlatforms.Modrinth,121);
        Check(cf.WikiId==11&&mr.WikiId==22&&!cf.IsLike(mr),"Same slug/description across providers conflated distinct project names.");
        var unrelated=Create("MR3","other-slug","Alpha Project",ResourcePlatforms.Modrinth,121);
        Check(unrelated.WikiId==0&&!cf.IsLike(unrelated)&&unrelated.WikiId==0,"Similarity transferred a wiki label to an unrelated slug.");
        var moved=Create("MR4","alphaproject","Alpha Project (Fabric)",ResourcePlatforms.Modrinth,121);
        Check(moved.WikiId==11,"Separator/platform suffix differences lost independently matching names.");
        entries.Add(new(){Id=31,ChineseName="重复甲 (Duplicate Project)",Slugs=new(){{ResourcePlatforms.Modrinth,"duplicate"}}});
        entries.Add(new(){Id=32,ChineseName="重复乙 (Duplicate Project)",Slugs=new(){{ResourcePlatforms.Modrinth,"duplicate"}}});
        var ambiguous=Create("MR5","duplicate","Duplicate Project",ResourcePlatforms.Modrinth,121);
        Check(ambiguous.WikiId==0&&ambiguous.TranslatedName=="Duplicate Project","Ambiguous wiki rows were arbitrarily bound.");
        var nested=new WikiEntry{Id=41,ChineseName="嵌套 (Nested Project (Extra))",Slugs=new(){{ResourcePlatforms.Modrinth,"nested"}}};entries.Add(nested);
        Check(Create("MR6","nested","Nested Project (Extra)",ResourcePlatforms.Modrinth,121).WikiId==41,"Balanced original-name parentheses were truncated.");
        var acronym=new WikiEntry{Id=51,ChineseName="普通工具 (Generic Utility Toolkit)",Slugs=new(){{ResourcePlatforms.Modrinth,"generic"}}};entries.Add(acronym);
        Check(Create("MR7","generic","[GUT] Generic Utility Toolkit",ResourcePlatforms.Modrinth,121).WikiId==51,"A verified acronym prefix lost its project name.");
        Check(Create("MR8","generic","Generic Utility Toolkit (Different Edition)",ResourcePlatforms.Modrinth,121).WikiId==0,"Meaningful name differences were discarded as decoration.");
        Check(Create("MR9","generic","Brand ~ Generic Utility Toolkit",ResourcePlatforms.Modrinth,121).WikiId==51,"A separated platform branding prefix lost the original name.");
    }
    private static void Search() {
        var home=Metadata("uJT3ahxz","youkaishomecoming","Youkai's Homecoming");
        var feast=Metadata("Ohy0TgO2","gensokyo-delight-youkais-feasts","Gensokyo Delight ~ Youkais' Feasts");
        foreach(var term in new[]{("妖怪们的飨宴","Ohy0TgO2"),("妖怪们的归家","uJT3ahxz")}) {
        var requestErrors=new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var requests=new System.Collections.Concurrent.ConcurrentBag<string>();
        var engine=new ResourceSearchEngine<ResourceProjectModel,ResourceSearchResultData<ResourceProjectModel>>(new(){
            CreateProject=j=>new ResourceProjectModel(j,Services()),WikiEntries=()=>WikiEntry.All.Value,SimplifyChinese=s=>s,
            Info=_=>{},Warning=_=>{},Warn=(error,_)=>requestErrors.Add(error),
            StartThread=(action,name)=>{var thread=new Thread(()=>action());thread.Name=name;thread.Start();return thread;},
            Request=url=>{requests.Add(url);return url.Contains("/projects?")?new JArray(home.DeepClone()):new JObject{
                ["total_hits"]=2,["hits"]=url.Contains("query=youkaishomecoming")?new JArray(home.DeepClone()):new JArray(home.DeepClone(),feast.DeepClone())};}
        });
        var storage=new ResourceSearchResultData<ResourceProjectModel>();
        var request=new ResourceSearchRequestData<ResourceProjectModel,ResourceSearchResultData<ResourceProjectModel>>(ResourceTypes.Mod,storage,1){Sources=ResourcePlatforms.Modrinth,SearchText=term.Item1};
        double progress=0;engine.Run(request,new(){IsCanceled=()=>false,Progress=()=>progress,SetProgress=v=>progress=v});
        Check(requestErrors.IsEmpty,"Search used unexpected requests or failed internally.");
        Check(storage.Results.Count>0&&storage.Results[0].Id==term.Item2,"Stale/missing platform slug changed Chinese search identity: "+term.Item1+" / "+string.Join(";",requests));
        }
    }
}
