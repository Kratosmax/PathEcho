# PathEcho 开发接续

## 接手校准

每次接手先运行：

~~~powershell
git status --short --branch
git remote -v
git log -3 --oneline --decorate
git tag --list
Get-Content -Raw Directory.Build.props
dotnet --version
~~~

再读取 README、当前需求涉及的源码、测试、打包脚本和发布工作流。本文若与 Git、版本源、源码、测试或 GitHub 线上状态冲突，以较新的可验证证据为准并同步本文。

## 当前快照

- 最后核验：2026-08-22，Asia/Shanghai。
- 版本：`0.1.0`，唯一来源为 `Directory.Build.props`；程序集版本、界面显示、包名、清单和标签从该版本推导。
- 分支：`main`；初始实现提交为 `39e08ec26bca632d280baf5f50e8cf25d6a24622`。
- 远程：`origin` 指向公开仓库 `https://github.com/Kratosmax/PathEcho.git`，远端 `main` 已核验与初始实现提交一致。
- GitHub Actions Secret `PATHECHO_UPDATE_PRIVATE_KEY` 已配置；本地临时私钥已删除，仅保留可公开的公钥。
- 标签、Actions 和 Release 尚未创建或在线核验。
- Git 身份：仓库级 `小火车 <kratosthemax@gmail.com>`。
- 用户已授权创建公开仓库 `Kratosmax/PathEcho`、提交、推送、配置更新签名 Secret、推送 `v0.1.0` 标签并发布首版。
- 初始实现和本接续文件均需推送后才可宣称跨设备接续完整。

## 已实现

- 本机目录单向、反向和双向同步，支持删除策略、删除前备份和双向冲突策略。
- 游戏存档定时、重点文件变化、文件变化和进程退出备份，支持内容去重、硬链接与复制回退、版本保留。
- 整目录、变动文件和正则文件事务回档，包含占用识别、PID 与启动时间校验、关键进程保护和失败回滚。
- WPF 主界面、业务弹窗、托盘、当前用户开机自启、统一设置页和预览截图模式。
- Windows 11 build 22621+ 全客户区 Acrylic；DWM 返回失败、旧系统或 `PATHECHO_DISABLE_BACKDROP=1` 时完整回退为不透明 `#F4F7F8`。
- ECDSA P-256/SHA-256 签名更新清单，签名覆盖产品、版本、通道、URL、SHA-256、大小和公告；客户端与外部更新器均验签验包。
- Full/Lite 同通道更新，包含 URL 前缀线路、HTTP 出网代理、稳定故障转移、重定向 allowlist、下载大小与停滞限制、失败暂存清理、包结构与路径穿越检查。
- 安装目录 marker、外部 launcher、PID 身份等待、事务 stage/backup/rollback、重启交接和安装版卸载器保留。
- Full Setup、Lite Setup、Full ZIP、Lite ZIP、兼容/通道清单、SHA256 文件和标签触发的 GitHub Actions 发布链。

## 当前待办

1. 提交并推送本次接续快照。
2. 创建并推送 `v0.1.0`，等待 Release Actions 成功。
3. 核验线上 8 个资产、清单验签、SHA256、latest 路由和下载包。
4. 发布完成后把标签、Actions、Release 和线上核验结果写回本文，再提交推送收尾文档。

## 关键入口

- 版本源：`Directory.Build.props`
- 同步：`src/PathEcho.Core/Sync`
- 游戏快照：`src/PathEcho.Core/Backup`
- 回档事务：`src/PathEcho.Core/Restore`
- 清单、代理、下载和包验证：`src/PathEcho.Core/Update`
- DWM、占用检测和自启：`src/PathEcho.Platform.Windows`
- WPF 界面与更新协调：`src/PathEcho`
- 外部事务更新器：`src/PathEcho.Updater`
- 构建与安装：`build/Build-Preview.ps1`、`build/Build-Release.ps1`、`build/PathEcho.iss`
- 发布工作流：`.github/workflows/release.yml`
- 回归测试：`tests/PathEcho.SmokeTests`

## 验证证据

2026-08-22 实际通过：

~~~powershell
dotnet build PathEcho.sln -c Release --no-restore -m:1 -v:minimal
dotnet run --project tests\PathEcho.SmokeTests\PathEcho.SmokeTests.csproj -c Release --no-build
dotnet format PathEcho.sln --no-restore --verify-no-changes --include src\PathEcho\MainWindow.xaml.cs src\PathEcho\Services\ApplicationUpdateService.cs
powershell -ExecutionPolicy Bypass -File build\Build-Release.ps1 -PrivateKeyPath <ignored-key> -ExpectedTag v0.1.0
~~~

- Release 构建 0 警告、0 错误；SmokeTests 13/13 通过。
- Full/Lite 最终 ZIP 均通过对应更新器 `--verify-only` 的哈希、版本、通道和结构预检。
- 本机未安装全局 .NET 8 Desktop Runtime，Lite Setup 正确阻止安装并给出依赖提示。
- Full Setup 完成静默安装、签名清单与包二次验证、外部更新器替换、预览重启截图、卸载器保留和静默卸载；三个进程退出码均为 0，测试安装目录与卸载登记已清理。
- UI 已核验设置页、同步页、常用尺寸、920×620 最小尺寸和强制不透明回退；证据位于忽略提交的 `temp/ui` 与 `temp/e2e`。

当前本地候选资产：

| 资产 | 字节 | SHA-256 |
|---|---:|---|
| `PathEcho-0.1.0-Full-Setup.exe` | 72998675 | `1B2EA71C52D29569A9184480FDB8C2BC4F586F9427DB34E36278A88E49CEE623` |
| `PathEcho-0.1.0-Full.zip` | 99654023 | `60C4A8D09B37D86099E066C1318E4BBF064F7C31F9F6CA744B43C822D3984D63` |
| `PathEcho-0.1.0-Lite-Setup.exe` | 2290937 | `70DD452CD54C0A45DA6A5BD992E6D5270C311EC96A2ED99197584818A38E81B1` |
| `PathEcho-0.1.0-Lite.zip` | 389031 | `0AA460EE28CA153001D5ACEE4570A5C46C817BAF3C1012A1F22B10698F14CDB7` |
| `update.json` / `update-lite.json` | 1803 | `D31D50B940681601E775B447F8CEE35D62BBE16D5E1961B7304A30EB770AAB9A` |
| `update-full.json` | 1815 | `17134AA678371081FA1BAE21350F98F93130377DAFB2083961C9456B648BF7AA` |
| `SHA256SUMS.txt` | 623 | `5A0EF84E8E3F6A042F3D9DA748B86840CEE356720168626FBD348E35D8DAF3F2` |

本地与云端由不同环境重建时哈希可能不同；发布后必须验证线上清单与线上资产彼此匹配，不能拿本地 ZIP 时间戳差异误判篡改。

## 不得破坏

- 同步两端、存档与备份目录不得相同或互相包含。
- 文件替换使用同目录暂存后移动；回档提交失败必须回滚。
- 不使用未公开 API 强拆句柄，不结束服务、关键进程、自身进程或身份变化进程。
- 备份目录迁移必须先验证目标，失败时保留或恢复旧目录。
- 更新必须验证签名清单、哈希、版本、通道、结构和目标边界；Full/Lite 不跨通道。
- 正式私钥、令牌、用户配置、备份、日志、截图、`temp`、`bin` 和 `obj` 不得进入 Git 或 Release。
- 线上发布后不得静默覆盖同版本资产；修复发布必须递增版本。

## 维护

每次交付前更新校准时间、工作区状态、实际测试、产物、线上核验、阻塞和下一步。删除失效信息，不记录私钥、令牌、用户数据、日志正文或设备专属绝对路径。用 `git diff --check`、`git status --short`、版本源、测试和 GitHub API 独立校准。
