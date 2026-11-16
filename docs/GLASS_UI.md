# WPF 工作台界面

新版界面使用同一套启动、账号、实例、资源与任务服务，采用窗口内的窄图标导航、左侧操作或二级导航面板、右侧内容区结构。原先参考图中的外置侧栏、三段悬浮胶囊和大磨砂主卡已经移除。

## 背景与层次

启动器绘制自己的不透明基底，只使用自定义壁纸，不创建桌面模糊窗口，不读取或采样 Windows 桌面。未设置壁纸时显示主题底色；局部半透明卡片和导航仅透出启动器内部的图片或内容。图片可从背景菜单设置或清除，也兼容原 Pictures 目录。

背景图片保留不透明度、局部图片模糊、居中、裁剪、拉伸、平铺和边缘对齐设置。图片低透明度、无图片或浮出菜单的空白区域都有应用自身的底色，不会透出其他 Windows 窗口。高对比度使用系统颜色。

## 新的呈现组件

页面、账号、列表、表单、任务按钮和菜单使用新的标准 WPF 呈现树。原有控制器和状态源保留在隐藏的加载树中，新控件通过明确的命令接口、输入同步及设置事务接入业务；源 UIElement 不会放入可见树。原 PCL 标识选项、官方主题推广和不再适用的整窗透明度设置不在新版显示。

动态列表保持按需加载。按钮点击、复选预览取消、输入校验、账号选择、设置保存、加载错误重试、资源详情及收藏更新继续沿用实际业务路径。原始页面与新呈现分别管理生命周期，关闭时释放绑定和事件。

账号头像沿用已加载的皮肤裁片，脸与帽层在同一像素网格合成。竖栏使用 32 DIP 图像和最近邻缩放；账号面板保留原皮肤图像的缩放方式，避免像素头像被平滑拉糊。

## 导航与首页

主导航位于窗口内最左侧。具体页面的二级选项出现在其右边的纵向面板，扩展选项菜单保留文件夹管理等功能。右侧顶部显示必要的页面标题和返回入口，窗口保留最小化和关闭按钮。隐藏页面偏好继续影响主导航。

首页左侧面板从上到下放置标题与加号、当前游戏实例图标和名称、启动/取消按钮；底部左右分别为选择实例和实例管理。首页右侧默认留空，只显示启动器背景；点击加号后才向右展开依赖检查和最近游玩。最左导航与主面板相隔 12 DIP，两块外轮廓均为 12 DIP 圆角，间隙与圆角之外保持透明。竖栏顶部放应用 Logo、底部放登录角色头像；头像左右及底部留白一致，Logo 左右及顶部留白一致，并随 DPI 舍入。壁纸入口位于主面板右下角。任务、音乐、启动日志及原自定义内容保持入口。

外轮廓使用抗锯齿透明度遮罩，保留边缘的分级 alpha；原生窗口区域只排除两面板之间的空隙，避免 GDI 的整数圆角切掉平滑边缘。透明像素的窗口命中遵循 Windows 分层窗口机制。实现依据见 [WPF 透明度遮罩](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/opacity-masks-overview) 和 [Windows 分层窗口](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)。

## 设计参考与动效

信息组织参考 [BlockHelm Launcher](https://github.com/zqq-699/BlockHelm-Launcher) 及用户提供的首页、资源页截图，包括窄图标导航、纵向二级面板、分组表单和内部壁纸磨砂。参考源码已克隆于忽略目录 `artifacts/references/BlockHelm-Launcher`，阅读版本为 `f858f0d5f9e841c96e5ffd214045fb1ba4a451b5`。视觉布局和代码独立实现，不引入该项目的源码或资产；截图中的弹幕不属于界面。

交互经验参考 [Apple Design](https://developer.apple.com/cn/design/) 中的清晰层级、即时反馈与连续过渡；桌面表单和键盘使用参考 Windows 的焦点及可访问性规范。新界面不采用 Liquid Glass 材质语言。动效集中于按压反馈、选择变化和页面状态过渡，保留可打断与快速反向；键盘操作和系统减少动效即时完成。

## 验证

需要 .NET 10 SDK 和 Windows WPF 环境：

```powershell
dotnet build tests/PCLLF.Native.Facade.Tests/PCLLF.Native.Facade.Tests.csproj -c Debug
$env:PCL_NATIVE_REPOSITORY_ROOT = $PWD.Path
dotnet run --project tests/PCLLF.Native.Facade.Tests -c Debug --no-build -- --groups glass-shell
dotnet run --project tests/PCLLF.Native.Facade.Tests -c Debug --no-build -- --groups glass-window
dotnet run --project tests/PCLLF.Native.Facade.Tests -c Debug --no-build -- --groups glass-presentation
dotnet run --project tests/PCLLF.Native.Facade.Tests -c Debug --no-build -- --groups glass-portals
dotnet run --project tests/PCLLF.Native.Facade.Tests -c Debug --no-build -- --groups glass-account-ui
dotnet run --project tests/PCLLF.Native.Facade.Tests -c Debug --no-build -- --groups workbench-commands
```

历史检查组名称保留以兼容 CI。新窗口验收检查单一 HWND 中两面板的命中范围、抗锯齿半透明边缘、透明间隙及角点、壁纸与面板内的不透明背景、头像与 Logo 对称留白、零桌面合成辅助窗口、原命令接线、缩放、最小化恢复和清理。账号自动化使用实际认证引擎与加密事务，但账号和传输为合成夹具，不代表真实微软授权或 Minecraft 游戏内运行已验收。
