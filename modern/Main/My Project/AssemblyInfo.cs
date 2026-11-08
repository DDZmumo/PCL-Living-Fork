using System;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using System.Windows;

// 有关程序集的一般信息由以下
// 控制。更改这些特性值可修改
// 与程序集关联的信息。

// 查看程序集特性的值

[assembly: AssemblyTitle("PCL Living Fork")]
[assembly: AssemblyDescription("基于 PCL 原项目的第三方修改版本")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("PCL Living Fork")]
[assembly: AssemblyCopyright("Copyright © 成都瓜皮龙科技有限公司")]
[assembly: AssemblyConfiguration("PCL-Living-Fork")]
// Same platform contract emitted by the SDK for net10.0-windows; legacy assembly identity stays explicit.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("Windows7.0")]
[assembly: ComVisible(false)]

// 若要开始生成可本地化的应用程序，请设置
// 在您的 .vbproj 文件中的 <PropertyGroup> 内设置 <UICulture>CultureYouAreCodingWith</UICulture>。
// 例如，如果您在源文件中使用的是美国英语，
// 请将 <UICulture> 设置为“en-US”。  然后取消下面对
// NeutralResourceLanguage 特性的注释。  更新下面行中的“en-US”
// 以与项目文件中的 UICulture 设置匹配。

// <Assembly: NeutralResourcesLanguage("en-US", UltimateResourceFallbackLocation.Satellite)>


// ThemeInfo 特性说明在何处可以找到任何特定于主题的和一般性的资源词典。
// 第一个参数: 特定于主题的资源词典的位置
// (未在页面中找到资源时使用，
// 或应用程序资源字典中找到时使用)

// 第二个参数: 一般性资源词典的位置
// (未在页面中找到资源时使用，
// 资源词典中找到资源时使用)
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]



// 如果此项目向 COM 公开，则下列 GUID 用于类型库的 ID
[assembly: Guid("fc6bb5fb-fe5c-4b25-84d7-2effb821c4bd")]

// 程序集的版本信息由下列四个值组成: 
// 
// 主版本
// 次版本
// 生成号
// 修订号
// 
// 可以指定所有值，也可以使用以下所示的 "*" 预置版本号和修订号
// 方法是按如下所示使用“*”

[assembly: AssemblyVersion("1.4.2.1")]
[assembly: AssemblyFileVersion("1.4.2.1")]
[assembly: AssemblyInformationalVersion(PCL.ModBase.VersionBaseName)]
[assembly: NeutralResourcesLanguage("")]
