# WPF 玻璃界面

玻璃外观使用同一套启动、账号、实例和任务服务。界面由 Main 的 `GlassLauncherView` 与 `GlassShellIntegration` 组合，材质与弹簧组件位于 `PCLLF.WPF.Core`。它不另外保存账号会话或游戏目录，也不替换文件操作事务。

## 形态与背景

- 顶部三个独立胶囊分别容纳当前账号、主导航、最小化与关闭，位于主面板上方。
- 上下文侧栏独立位于主面板左侧；不存在侧栏的页面不显示它。圆角采用主面板 36、内容卡片 16、顶栏和侧栏 28 DIP。
- 默认材质透出真实桌面和后方窗口；各表面之间的空隙保持透明。原生合成只作用于各自圆角形状，不使用截图模拟玻璃。
- 自选壁纸只在主面板内显示，不铺满顶栏、侧栏之间的空隙；所有卡片、顶栏、侧栏继续使用对齐的磨砂采样。原有 `PCL/Pictures` 背景可继续使用。
- 右下背景按钮可设置壁纸、恢复桌面磨砂及打开外观设置。`UiBackgroundMode` 切换会保留已选壁纸路径；界面不透明度调整磨砂厚度，不修改整窗透明度。

Windows 11 22621 及以上优先使用 Windows.UI.Composition HostBackdrop 圆角合成。原生材质层与 WPF 内容分离，随主窗口拖动、缩放、最小化、隐藏和关闭同步。该装饰层没有窗口标题，不能激活或拦截输入。系统禁用透明效果、高对比度或不支持的系统使用明确的不透明回退；Windows 10 原生桌面路径未经本次验证，不承诺同等效果。壁纸磨砂在可用时由 WPF 绘制，文字和图标不参与模糊。

## 主页与功能入口

实例卡固定在主面板左侧，显示当前实例图标、名称与启动入口；下方两个圆形按钮分别进入实例选择和实例设置。加号只向右展开“依赖检查／自动补全”和“上次游玩”两张卡片，实例卡本身不移动。启动及游戏运行期间沿用原规则收起附加选项，结束后恢复展开偏好。

账号弹层托管原有登录内容、正版／离线选择及单一登录方式标签，不创建第二套登录表单。上下文侧栏使用原控件的真实点击和选择行为；“全部页面选项”可打开原完整侧栏，保留文件夹管理等不能缩成单个图标的入口。

原右侧页面容器仍用于下载、收藏、设置、帮助和实例管理；提示、模态窗口、任务及音乐等按钮保持连接。背景菜单中的“主页内容与启动日志”显示原自定义主页与日志，返回即可回到实例卡主页。隐藏页面偏好继续控制对应的新导航按钮。

## 动效与交互

弹簧按当前值和当前速度重定目标，快速重复点击不会跳回起点或锁住输入。侧栏从主面板向左出现并沿原路径收回；两张附加卡片向右展开。按压反馈即时响应。系统减少动效、高对比度及键盘即时操作采用较轻或即时状态变化。

新旧页面通过原容器托管，主页中隐藏的右侧页面仍完成加载。账号与导航面板重复打开保持原逻辑所有者及 ContentPresenter 绑定，不把模板的内部视觉节点当作内容所有者。

## 验证入口

```powershell
dotnet build PCL-Living-Fork.Modern.slnx --configuration Debug -nr:false
dotnet run --project tests/PCLLF.Migration.Tests --configuration Debug --no-build -- --group settings --group foundation
dotnet run --project tests/PCLLF.Modern.Wpf.Tests --configuration Debug --no-build -- $PWD
$env:PCL_NATIVE_REPOSITORY_ROOT = $PWD.Path
dotnet run --project tests/PCLLF.Native.Facade.Tests --configuration Debug --no-build -- --groups glass-shell
dotnet run --project tests/PCLLF.Native.Facade.Tests --configuration Debug --no-build -- --groups glass-context-navigation
dotnet run --project tests/PCLLF.Native.Facade.Tests --configuration Debug --no-build -- --groups glass-portals
dotnet run --project tests/PCLLF.Native.Facade.Tests --configuration Debug --no-build -- --groups glass-window
dotnet run --project tests/PCLLF.Native.Facade.Tests --configuration Debug --no-build -- --groups glass-account-ui
```

原页面的固定几何对照在独立夹具中明确选择经典样式；新增玻璃检查验证真实 WPF 视图和完整 FormMain。组件布局使用固定逻辑视口，并覆盖原生宿主小于视口的情况；完整窗口检查另行验证原缩放接线及边缘命中。正常冷启动及连续交互检查使用产品默认玻璃样式，不借经典对照替代新外观验收。账号检查保留真实认证引擎和加密事务，但传输与账号为合成夹具，不代表真实授权或 Minecraft 游戏已启动。

连续交互检查对玻璃导航的标准 WPF 按钮使用原生 UIA Invoke：先验证真实悬停并恢复光标，再重新核对唯一按钮、运行时身份、尺寸和窗口归属，最后调用提供程序。经典控件与提示框仍保留原鼠标消息路径；提供程序调用不发生在光标被移动的阶段。原生最小化与恢复检查等待实际窗口状态完成，避免系统动画延迟影响预览。

外观设置中的“界面样式”可切换玻璃与经典，下次启动生效。实现与状态边界见 [架构说明](ARCHITECTURE.md)。
