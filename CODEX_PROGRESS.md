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
- 版本：`0.2.1`，唯一来源为 `Directory.Build.props`；程序集版本、界面显示、包名、清单和标签从该版本推导。
- 分支：`main`；本轮校准基线为 `0f7dc8c236ad0cd8fe989d651bf6f7e249333b2c`，与 `origin/main` 一致；正式 `v0.2.0` 标签指向 `7a4749aca3ecd182f64c8a118dbf57088f99dd47`。
- 远程：`origin` 指向公开仓库 `https://github.com/Kratosmax/PathEcho.git`，远端 `main` 与本地提交已核验一致。
- GitHub Actions Secret `PATHECHO_UPDATE_PRIVATE_KEY` 已配置；本地临时私钥已删除，仅保留可公开的公钥。
- `v0.1.0` 已保留为失败发布标签；Actions Run `32558765725` 因 CI 未把 Chocolatey 的 ISCC 路径传给构建脚本而失败，线上没有 `v0.1.0` Release。
- `v0.1.1` Actions Run `32559042297` 已完成且结论为 success；正式 Release 为 `https://github.com/Kratosmax/PathEcho/releases/tag/v0.1.1`，latest 已指向该版本。
- `v0.2.0` 已正式发布：标签指向 `7a4749aca3ecd182f64c8a118dbf57088f99dd47`，GitHub Actions Run `32564746250` 成功，Release 为 `https://github.com/Kratosmax/PathEcho/releases/tag/v0.2.0`。
- Git 身份：仓库级 `小火车 <kratosthemax@gmail.com>`。
- 用户已授权创建公开仓库 `Kratosmax/PathEcho`、提交、推送、配置更新签名 Secret 并发布首个可用版本；失败标签按发布规则递增补丁版本。
- 初始实现与接续文件均已推送，当前可从公开仓库跨设备接续。

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
- 新建同步任务和游戏存档的 WPF 绑定集合写入统一切回 Dispatcher，修复跨线程创建错误。
- 游戏程序可从运行中进程选择，支持程序名、PID、窗口标题和路径搜索，持久化仍只保存可执行路径。
- 更新设置支持多条 URL 前缀、直连兜底、0 到 10 优先级与 0 禁用；HTTP 出网代理继续独立配置。
- 可选 Debug 滚动日志、统一 PathEcho Logo、窗口/任务栏/托盘/安装器图标和高对比侧栏 Hover。
- 同步任务页支持弹性列宽、绿色高对比 Hover/选中态、搜索与状态筛选、目录可用性提示、双击编辑、右键同步/编辑/复制/打开目录/删除，以及空数据禁用操作。
- 预览模式使用隔离的内存配置和独立单实例锁，不读取真实用户配置，也不受已运行正式实例阻塞。

## 当前待办

- `0.2.1` 双击闪退与运行时一致性修复已获提交、推送和发布授权；当前待完成完整 Release 构建、签名云端发布、上一正式版兼容检查和线上资产核验。正式 `v0.2.0` 不得覆盖。
- GitHub 连接若受全局失效代理 `http.https://github.com.proxy=http://127.0.0.1:10809` 影响，只能命令级临时覆盖，不应静默修改用户全局配置。发布工作流已按官方最新稳定版本升级到 `checkout@v7` 与 `setup-dotnet@v6`。

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

- 本轮修复：全局只读 `DataGrid` 阻止 WPF 向只读行模型执行 TwoWay 回写；同步表双击仅接受真实数据行，运行进程表双击同样限制到数据行。
- 本轮运行时加固：手动/后台同步共享完整基线事务锁，手动/后台游戏备份共享服务锁；配置增删改先持久化再切换内存状态；监听启动失败隔离；备份目录迁移失败使用不可取消令牌回滚；未知 UI 异常强制落日志并等待清理后退出。
- `dotnet build PathEcho.sln -c Release --no-restore -m:1 -v:minimal`：0 警告、0 错误。
- `dotnet run --project tests\PathEcho.SmokeTests\PathEcho.SmokeTests.csproj -c Release --no-build`：15/15 通过，新增并发基线串行与只读表格契约回归。
- `dotnet format PathEcho.sln --no-restore --verify-no-changes`、`git diff --check` 通过。
- `0.2.1` 最终本地 Lite 预览包 `temp/preview/PathEcho-0.2.1-Lite.zip`：454409 字节，SHA-256 `98B854000F4521272F5DB93C6478BA17701D15AE60B1D0B3CC351CC82F432766`；更新器 `--verify-only` 返回 0。本地预览仅供验收，不是 Release。
- 真实 WPF 隔离预览已核验：双击同步源路径打开编辑窗且进程存活；双击游戏存档路径不退出；运行进程列表双击会回填程序名和路径；更新线路仍可新增编辑行。920×620 最小窗截图为 `temp/ui-check/double-click-fix-min-920x620.png`，截图进程走等待式退出并返回 0。
- `0.2.1` 四包本地构建已生成 Lite/Full ZIP 和 Setup；两个 ZIP 均通过候选包内更新器的版本、通道、哈希和结构预检，两个安装器由 Inno Setup 6.7.3 编译成功。一次性测试密钥不匹配客户端内置正式公钥，因此本地发布脚本按安全设计在清单自验阶段拒绝继续；正式清单只能由 GitHub Actions Secret 生成。
- 上一正式版 `v0.2.0` 原始 Lite ZIP（448783 字节，SHA-256 `F8267BD06DCF07D0C689ED8449A2D939E04E4FFD4EC71AD6C91F8356F708F292`）中的旧更新器已接受 `0.2.1` Lite 候选包。旧 Full 原包直连下载超过约 4 分钟且未产生文件，已中止；发布后需用线上签名清单、资产 digest、SHA256 文件和 Full 包结构交叉核验，不得宣称旧 Full 完整升级链已通过。
- `0.2.1` 920×620 最小窗截图为 `temp/ui-check/release-0.2.1-sync-920x620.png`，界面版本显示、选中态、长路径布局和等待式退出均正常，进程退出码为 0。

~~~powershell
dotnet build PathEcho.sln -c Release --no-restore -m:1 -v:minimal
dotnet run --project tests\PathEcho.SmokeTests\PathEcho.SmokeTests.csproj -c Release --no-build
dotnet format PathEcho.sln --no-restore --verify-no-changes --include src\PathEcho\MainWindow.xaml.cs src\PathEcho\Services\ApplicationUpdateService.cs
powershell -ExecutionPolicy Bypass -File build\Build-Release.ps1 -PrivateKeyPath <ignored-key> -ExpectedTag v0.1.0
~~~

- Release 构建 0 警告、0 错误；SmokeTests 13/13 通过。
- `0.2.0` 再次完成 Release 构建 0 警告、0 错误、全解决方案 `dotnet format --verify-no-changes` 和 SmokeTests 13/13；配置测试覆盖 Debug 开关与旧配置默认关闭。
- 真实 WPF 窗口已核验侧栏对比度、Logo、多线路设置、Debug 开关、添加游戏窗口、运行中进程列表与选择回填；落盘截图位于忽略提交的 `temp/ui`。
- 同步任务页真实 WPF 截图已覆盖 1180×760 长路径与选中态、920×620 最小尺寸、空数据和禁用操作；隔离截图位于忽略提交的 `temp/ui-check`，未包含真实用户配置。
- `v0.2.0` Actions Run `32564746250` 成功；8 个 Release 资产均为 uploaded，`SHA256SUMS.txt` 的 7 条记录与对应 GitHub API digest 一致。
- 线上 Lite 与 Full 清单均通过客户端内置公钥实际验签；线上 Lite ZIP 已实际下载，并通过更新器的 SHA-256、版本、通道和包结构只验证检查。
- `0.2.0` 本地 Lite 预览包为 `temp/preview/PathEcho-0.2.0-Lite.zip`，448830 字节，SHA-256 `09F0233669CD8534FAF877809689C6BC068E93AEBDE220BA986A1BC3AD4C4A06`；更新器只验证模式通过。
- Inno Setup 6.7.3 已成功编译带新 Logo 的 `temp/installer-test/PathEcho-0.2.0-Lite-Setup-Test.exe`。
- `0.1.1` 补丁改动后再次完成 Release 构建 0 警告、0 错误和 SmokeTests 13/13。
- Full/Lite 最终 ZIP 均通过对应更新器 `--verify-only` 的哈希、版本、通道和结构预检。
- 本机未安装全局 .NET 8 Desktop Runtime，Lite Setup 正确阻止安装并给出依赖提示。
- Full Setup 完成静默安装、签名清单与包二次验证、外部更新器替换、预览重启截图、卸载器保留和静默卸载；三个进程退出码均为 0，测试安装目录与卸载登记已清理。
- UI 已核验设置页、同步页、常用尺寸、920×620 最小尺寸和强制不透明回退；证据位于忽略提交的 `temp/ui` 与 `temp/e2e`。
- `0.1.1` 本地 Lite 预览包为 `temp/preview/PathEcho-0.1.1-Lite.zip`，390235 字节，SHA-256 `0F05CDE5C4C542FD9C16BE2F278AC18C5CF34471AE590491EDBB3B3D0AD075BF`；更新器预检与真实设置页截图通过。
- 线上 `update-lite.json` 与 `update-full.json` 均使用客户端内置公钥实际验签通过；`update.json` 与 Lite 清单逐字节一致。
- 线上 Lite ZIP 已实际下载并通过 SHA-256、版本、通道和包结构验证；Lite Setup 与三清单也完成实际下载哈希核对。
- `SHA256SUMS.txt` 的 7 条记录与 GitHub API 返回的 7 个资产 digest 全部一致；两份签名清单的 URL、包大小和哈希与对应线上 ZIP 一致，latest 清单路由验证通过。
- Full ZIP 与 Full Setup 因本机直连下载速度过慢未完整下载；两者使用 GitHub API digest、`SHA256SUMS.txt` 和已验签清单交叉核对。首个正式版本不存在上一正式客户端，因此“上一正式版升级到候选版”矩阵不适用；本地 Full 安装、更新器替换和卸载 E2E 已覆盖安装链。

正式 `v0.1.1` 线上资产：

| 资产 | 字节 | SHA-256 |
|---|---:|---|
| `PathEcho-0.1.1-Full-Setup.exe` | 73000315 | `7E631E5381AFED57B8A2FD0400F221D59F6E820A07044F2A370CBCA6D3C5B0A0` |
| `PathEcho-0.1.1-Full.zip` | 99655027 | `F304F233738369F8DAF22A4DAF9A6E7AF54354E7E7EB1F3C53FFC754E388D89E` |
| `PathEcho-0.1.1-Lite-Setup.exe` | 2290061 | `06A248C9FDD22167F59EDD12FF33C35A499D3C6B59CAA300E0386462D5F52C75` |
| `PathEcho-0.1.1-Lite.zip` | 389962 | `546DA9D8F30DAFF4F90A6CEE1408F78AFA3F84CFA92026DBE556A106FFFF4C02` |
| `update.json` / `update-lite.json` | 1835 | `0B83E567C2EC6B762D9BE5EC9C70B9A14D5A94E642F97874F0C13CCC5EA2D8B7` |
| `update-full.json` | 1832 | `B479A3F233B901C97E02AE956F481EA9A19BE3C8AABB9B90092725E921F7AACE` |
| `SHA256SUMS.txt` | 623 | `610C5BE02A66155FF5BD8842CABC74F77BEE0BCA95373E30CD0A14188613EC26` |

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
