# PathEcho

当前发布版本：`0.2.6`。请从 [GitHub Releases](https://github.com/Kratosmax/PathEcho/releases/latest) 下载对应版本资产。

<img src="src/PathEcho/Assets/PathEchoLogo.png" alt="PathEcho Logo" width="96" />

PathEcho 是面向 Windows 的本机目录同步与游戏存档版本备份工具。它提供同步预演与过滤、运行记录、内容去重快照、事务回档、远程游戏识别规则、后台监听和签名自动更新。

## 下载与使用

从 [GitHub Releases](https://github.com/Kratosmax/PathEcho/releases/latest) 下载。普通用户优先选择 Full Setup；只有已经安装 .NET 8 Desktop Runtime x64 时才选择 Lite。

| 包 | 适用场景 |
|---|---|
| `PathEcho-0.2.6-Full-Setup.exe` | 推荐，自带 .NET 8 运行时并提供卸载程序 |
| `PathEcho-0.2.6-Lite-Setup.exe` | 安装版，需要 [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0) |
| `PathEcho-0.2.6-Full.zip` | 免安装，自带 .NET 8 运行时 |
| `PathEcho-0.2.6-Lite.zip` | 最小免安装包，需要 .NET 8 Desktop Runtime x64 |

系统要求：Windows 10 19045 或 Windows 11 x64。普通同步与备份不需要管理员权限；结束占用存档的高权限进程可能需要对应权限。

Windows 11 build 22621 及以上会尝试全客户区 Acrylic 系统材质；DWM API 不可用、远程桌面降级或显式禁用时，主窗口和业务弹窗完整回退为不透明浅色界面。

### 主要流程

1. 在“同步任务”中新建目录同步，选择方向、删除和冲突策略，并按相对路径 glob 设置包含/排除规则；执行前可预演全部复制、删除和冲突操作。
2. 在“游戏存档”中设置存档目录、备份目录、保留版本数和定时/文件变化/游戏退出触发；可手动选择运行进程，也可用 GitHub 规则智能识别已启动游戏。
3. 在“版本历史”中浏览存档快照和最近 200 次同步运行结果，并按完整目录、变动文件或正则匹配文件事务回档。
4. 在“设置”中管理当前用户开机自启、后台启动、默认备份目录、多条更新线路、HTTP 出网代理和 Debug 日志。

配置、运行状态和更新缓存位于 `%LocalAppData%\PathEcho`，默认游戏备份位于 `%LocalAppData%\PathEcho\Backups`。安装版程序默认位于 `%LocalAppData%\Programs\PathEcho`。卸载不会删除配置和备份；需要彻底清理时，请先确认备份已另行保存，再手动处理数据目录。

保存设置时会先提交更新线路表格的当前编辑，再对配置执行原子写入和反序列化校验。修改默认备份目录时会先创建并验证新目录可写，然后迁移所有使用默认目录且已有备份的游戏；没有现有备份时会明确提示无需迁移。游戏档案单独指定的备份目录不随全局默认目录移动。

点击“浏览”选择新的默认备份目录后，PathEcho 会先显示迁移确认，确认后立即保存并显示结果；取消不会修改设置。重复启动时会唤醒已运行的主窗口并退出第二个进程，不会弹出单实例锁异常。

### 自动更新

PathEcho 启动后可后台检查更新，也可在设置页手动检查。客户端先用内置 ECDSA P-256 公钥验证清单签名，再校验版本、通道、下载大小、SHA-256 和包结构；外部更新器会再次验签和验包，并用同卷暂存、备份、替换和失败回滚完成安装。Full 与 Lite 不跨通道更新，安装版更新时保留卸载器。

更新器启动前仍可取消；进入外部替换交接后窗口会锁定取消操作，避免留下等待主进程的半完成事务。新版只有在完成初始化并报告就绪后才会删除旧版备份；若回滚本身受文件占用或权限变化阻断，会保留恢复现场并停止自动重启，不会把未确认的新目录当成旧版启动。`v0.2.4` 若曾出现下载后退出且无后续，请先从 Release 页面手动覆盖升级到当前版本一次。

更新和远程游戏规则共用直连、多条 GitHub URL 前缀线路及独立 HTTP 出网代理。线路按 0 到 10 的优先级从高到低尝试，0 表示禁用；直连线路不可删除但可禁用。前缀线路会看到完整 GitHub 请求地址；无论使用哪条线路，真实性、结构、大小和重定向检查都不会被跳过。

### 远程游戏规则

智能识别只匹配当前已启动进程，不扫描整块磁盘。客户端从 `config/game-catalog.json` 获取声明式游戏名、可执行文件名和存档路径模板；文件使用与更新清单相同的 ECDSA 公钥验签，并限制结构、条目数量和响应大小。在线线路全部失败时只回退到上一次验签通过的原子缓存；没有可信缓存会明确报错，旧签名规则也不能覆盖较新的本地修订。

维护规则时编辑 `config/game-catalog.source.json` 并递增 `revision`。推送到 `main` 后，`.github/workflows/game-catalog.yml` 使用 GitHub Actions Secret `PATHECHO_UPDATE_PRIVATE_KEY` 生成并提交签名后的 `config/game-catalog.json`。规则文件只允许声明数据，不支持命令、脚本或任意 URL，因此更新游戏识别无需重新发布安装包。

常见问题：

- Lite 无法启动：安装 .NET 8 Desktop Runtime x64，或改用 Full 包。
- 更新检查失败：在设置中检查 URL 前缀线路和 HTTP 出网代理；也可从 Release 页面手动覆盖安装。
- 需要排查问题：在设置中启用 Debug 日志，日志写入 `%LocalAppData%\PathEcho\Logs`，最多保留 5 个滚动文件。
- 回档提示文件占用：先正常退出游戏；需要结束进程时，PathEcho 会核对 PID 与启动时间并拒绝系统关键进程、自身进程和身份变化进程。

## 自行编译

工具链：Windows x64、.NET SDK 8.0.424（或兼容 .NET 8 SDK）。构建安装器还需要 Inno Setup 6。

```powershell
dotnet restore PathEcho.sln -m:1
dotnet build PathEcho.sln -c Release --no-restore -m:1 -v:minimal
dotnet run --project tests\PathEcho.SmokeTests\PathEcho.SmokeTests.csproj -c Release --no-build
powershell -ExecutionPolicy Bypass -File build\Build-Preview.ps1
# 若旧预览实例仍占用默认输出目录，可使用项目 temp 下的旁路目录
powershell -ExecutionPolicy Bypass -File build\Build-Preview.ps1 -OutputDirectory temp\preview-settings-fix
```

正式四包构建需要 ECDSA P-256 PEM 私钥；私钥必须放在仓库外或已忽略的 `temp`，不得提交：

```powershell
powershell -ExecutionPolicy Bypass -File build\Build-Release.ps1 `
  -PrivateKeyPath temp\signing\update-private.pem
```

预览产物位于 `temp/preview`，正式本地产物位于 `temp/release`。发布工作流由 `v<版本>` 标签触发，从 `Directory.Build.props` 读取唯一版本，运行构建和测试后生成 Full/Lite Setup/ZIP、`update.json`、通道清单与 `SHA256SUMS.txt`。正式私钥只存放在 GitHub Actions Secret `PATHECHO_UPDATE_PRIVATE_KEY`。

## AI 继续开发

先完整读取 [CODEX_PROGRESS.md](CODEX_PROGRESS.md)、仓库规则、`agent-rules` 和 `desktop-tool-ui-release`。关键入口：

- 同步：`src/PathEcho.Core/Sync`
- 游戏快照：`src/PathEcho.Core/Backup`
- 回档事务：`src/PathEcho.Core/Restore`
- 清单、代理、下载和包验证：`src/PathEcho.Core/Update`
- 远程游戏目录与识别：`src/PathEcho.Core/GameCatalog`、`config`、`.github/workflows/game-catalog.yml`
- Windows DWM、占用检测和自启：`src/PathEcho.Platform.Windows`
- WPF 界面、运行时与更新协调：`src/PathEcho`
- 外部事务更新器：`src/PathEcho.Updater`
- 打包和发布：`build`、`.github/workflows/release.yml`
- 回归测试：`tests/PathEcho.SmokeTests`

版本唯一来源是 `Directory.Build.props`。最低验证门禁是 Release 构建、SmokeTests、真实 WPF 截图、Full/Lite 更新器预检和发布资产哈希复核。不得提交 `temp`、用户配置、备份、日志、私钥、令牌或签名 Secret。提交、创建仓库、推送、标签和 Release 是独立外部动作；执行前必须以用户当前授权为准。
