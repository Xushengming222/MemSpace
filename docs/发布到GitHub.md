# 发布与更新

仓库：https://github.com/Xushengming222/MemSpace

## 首次使用

普通使用者从 Releases 下载便携包，解压后运行，不需要安装 Git 或编译器。

源码、MIT 许可证与构建脚本位于 main 分支。构建输出、本机诊断报告、截图与运行日志不会进入 Git 历史。

## 后续更新

在本地修改后运行 `build.ps1 -Test`，必要时执行 README 中的界面检查。确认结果后运行 `package.ps1`。

将源码变更提交并推送：

```powershell
git add .
git commit -m "Describe the change"
git push origin main
```

版本更新时同步修改 `src/AssemblyInfo.cs`、`src/app.manifest`、`package.ps1`、使用帮助及使用说明中的版本号，再构建和打包。

在 GitHub 仓库的 Releases → Draft a new release 中创建新版本标签（例如 `v1.3.1`），选择 main 或明确的提交，填写更新内容，上传 `artifacts/MemSpace-版本号.zip` 与 SHA-256 校验文件，最后发布。

发布前确认便携包至少包含 `MemSpace.exe`、`MemSpace.exe.config`、快速使用说明与许可证。不要上传本机诊断报告、测试截图、设置文件、访问令牌或 PCL 原程序。
