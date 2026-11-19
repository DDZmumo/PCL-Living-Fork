# WPF 皮肤库前端接入

前端入口是 `PCL.LauncherApplicationServices.SkinLibrary`，文件和模型类型在 `PCLCS.Applications`。页面、导航、导入对话框、删除确认和 3D 预览由前端实现。当前账号头像和皮肤预览可以直接绑定到 `Image.Source`。

## 文件与预览

| 调用 | 前端用法 |
| --- | --- |
| `List(kind)` | 读取皮肤或披风列表。保留每项的 `Id`，通过 `Name` 显示名称。 |
| `Import(paths, kind)` | 批量导入 PNG。同名自动编号，返回 `Entries` 和逐文件 `Errors`。 |
| `LoadPreview(id, kind)` | 返回已冻结的 `Texture`、`Avatar` 和 `Model`。用 `Texture` 和 `Model` 构建全身或 3D 预览。皮肤 `Avatar` 沿用新 UI 的 `GlassSkinHead`。 |
| `SetModel(id, SkinModel.Classic / SkinModel.Slim)` | 保存皮肤模型后用返回项更新列表并刷新预览。 |
| `Rename(id, name, kind)` | 使用返回项的 `Id` 更新当前选择，后端同步更新离线角色的引用。 |
| `Remove(id, kind)` | 用户确认后删除并解除引用该文件的离线角色应用状态。 |
| `DirectoryFor(kind)` | 获取该类纹理的目录路径。首次导入创建目录。 |

`kind` 使用 `SkinTextureKind.Skin` 或 `SkinTextureKind.Cape`。皮肤支持 64×32 和 64×64 PNG。披风支持 64×32 的整数倍尺寸，最大 4194304 像素。单个文件最大 4 MiB。皮肤位于应用数据目录的 `SkinLibrary`，披风位于 `SkinLibrary/Capes`。细臂标记为同名 `.png.model` 文件中的 `slim`。离线角色选择保存在 `OfflineAppearances.json`。

## 账号和应用

在进入页面、切换角色和完成操作后调用 `ReadAccount()`。`Target == null` 表示当前没有可操作的角色，显示问号头像并禁用账号操作。`Avatar` 可为空，`CanImportCurrent` 表示当前原始纹理可用于 `ImportCurrent(target, name, model)`。操作应传入打开选项时读到的 `Target`，后端会拒绝已经切换的账号或角色。

| 账号 | 调用 |
| --- | --- |
| 离线 | `SetOfflineTexture(target, kind, id)` 应用，传 `null` 清除指定类别。`OfflineAppearance` 提供当前皮肤和披风 ID。 |
| Microsoft 正版 | `await ApplySkinAsync(target, id, ct)` 上传皮肤。`await GetMicrosoftCapesAsync(target, ct)` 读账号现有披风，`await ApplyMicrosoftCapeAsync(target, capeId, ct)` 启用其中一个，传 `null` 隐藏。 |
| 外置登录 | `Wardrobe` 有值时可以打开站点衣柜。`ApplySkinAsync` 返回同一网址供前端打开并提示用户上传。 |
| 统一通行证 | 可浏览库和导入已加载的原始纹理。账号外观修改由站点管理。 |

页面可以异步加载 `List`、`Import` 和 `LoadPreview`。涉及当前账号、`Rename`、`Remove` 的方法在 WPF Dispatcher 上调用。Microsoft 请求可取消，未登录或会话失效时提示重新登录。账号切换后丢弃之前的请求结果，重新读取 `ReadAccount()`。文件错误、服务拒绝和重复名称可以用对应操作的异常信息显示。

```csharp
var library = LauncherApplicationServices.SkinLibrary;
var account = library.ReadAccount();
if (account.Target is { } target && selectedSkin is { } skin)
{
    Uri? wardrobe = await library.ApplySkinAsync(target, skin.Id, cancellationToken);
    if (wardrobe is not null)
        ModBase.OpenWebsite(wardrobe.AbsoluteUri);
    account = library.ReadAccount();
}
```

## 游戏和验证

普通实例和受管理的 MMC 实例会在启动时读取选定离线玩家的纹理。有分配的纹理时，启动器用 Authlib-Injector 接入仅监听 `127.0.0.1` 的本地 Yggdrasil，沿用游戏启动所用的玩家 UUID。首次使用需要下载和校验代理，之后可使用本机缓存。服务跟随本次游戏进程，启动器关闭后仍可继续提供纹理。这条路径需要 Java 8 或更高版本，面向本机离线外观。

```powershell
dotnet build tests/PCLLF.Native.Facade.Tests/PCLLF.Native.Facade.Tests.csproj -c Debug
& .\tests\PCLLF.Native.Facade.Tests\bin\Debug\net10.0-windows\PCLLF.Native.Facade.Tests.exe --groups skin-library
```

验证用例使用生成的 PNG 和模拟玩家，覆盖导入、改名、删除、模型、WPF 预览、签名纹理、进程生命周期。联调页面时可以用两个离线名称分别分配纹理，再进入单人游戏看皮肤和披风。正版账号需用有授权的账号验证上传和披风切换。

文件格式和功能参照用户提供的 PCL Symbio Edition `2.17.16-beta.7` 皮肤库源文件包（`49a2864033e254daec4d55662ef2f768577d6d16`）。本地协议遵循 [Authlib-Injector Yggdrasil 服务端规范](https://github.com/yushijinhun/authlib-injector/blob/develop/wiki/src/en/yggdrasil-server-technical-specification.md)。
