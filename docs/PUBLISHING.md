# 公开发布约定

## 公开发布

- 默认只发布精简版，不捆绑 .NET 运行时。不要向 GitHub Release、CNB 或其他公开发布入口上传自包含运行时的完整版 EXE/ZIP。
- 公开附件统一为 `PCL-Living-Fork-<版本>.exe`、`PCL-Living-Fork-<版本>-win-x64.zip`、`SHA256SUMS.txt`，不使用 `LivingPCL.Native`、`LivingPCL.Slim` 或 `-slim` 等内部产物命名。
- ZIP 内的主程序也使用 `PCL-Living-Fork-<版本>.exe`。公开 EXE 必须提供旧版更新所需的轻量交接层，其内嵌负载必须与 ZIP 中已校验的精简主程序逐字节一致，不能内嵌包含运行时的完整版；发布前用实际旧版验证器确认兼容性。
- 发布说明明确要求先安装 .NET 10 Desktop Runtime x64；不保留完整版下载入口或“已包含运行时”的提示。
- 自包含产物可以用于内部 CI 或诊断验证，但不能混入公开附件清单。使用 `git` 推送源码和标签，用 `gh` 创建 Release、上传和发布。
- 发布前核对标签对应源码、必需 CI、程序版本、ZIP/交接层负载及 SHA256；发布后回读附件名称、大小和摘要。

## 版本更新说明

- 版本更新说明写在对应的 GitHub Release 正文中。
- 不在源码仓库新增、提交或推送 `RELEASE_NOTES*.md`，也不以其他名称创建逐版本更新说明文件。
- README 的更新说明链接指向对应 GitHub Release 页面。
- 临时说明草稿只放在 Git 已忽略的临时或构建目录。
