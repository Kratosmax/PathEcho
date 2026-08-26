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

- 最后核验：2026-08-26，Asia/Shanghai。
- 版本：`0.4.0` 正式版，唯一来源为 `Directory.Build.props`；程序集版本、界面显示、包名、清单和标签从该版本推导。GitHub latest 已核验指向 `v0.4.0`。
- 分支：`main`；正式标签 `v0.4.0` 指向功能提交 `15bf74a46e36de1580359cca871b9ff2db9f0c8e`，历史正式标签保持固定。
- 远程：`origin` 指向公开仓库 `https://github.com/Kratosmax/PathEcho.git`，远端 `main` 与本地提交已核验一致。
- 当前发布代码基线：`v0.4.0` 固定在功能提交 `15bf74a46e36de1580359cca871b9ff2db9f0c8e`；`main` 仅在其后追加本发布证据文档提交，产品代码与标签一致。
- GitHub Actions Secret `PATHECHO_UPDATE_PRIVATE_KEY` 已配置；本地临时私钥已删除，仅保留可公开的公钥。
- `v0.1.0` 已保留为失败发布标签；Actions Run `32558765725` 因 CI 未把 Chocolatey 的 ISCC 路径传给构建脚本而失败，线上没有 `v0.1.0` Release。
- `v0.1.1` Actions Run `32559042297` 已完成且结论为 success；正式 Release 为 `https://github.com/Kratosmax/PathEcho/releases/tag/v0.1.1`，latest 已指向该版本。
- `v0.2.0` 已正式发布：标签指向 `7a4749aca3ecd182f64c8a118dbf57088f99dd47`，GitHub Actions Run `32564746250` 成功，Release 为 `https://github.com/Kratosmax/PathEcho/releases/tag/v0.2.0`。
- `v0.2.1` 已正式发布：标签指向 `f3506aff4f7271495f160b7fd6e859a17949b4e0`，GitHub Actions Run `32569018960` 为 `completed/success`，Release 为 `https://github.com/Kratosmax/PathEcho/releases/tag/v0.2.1`，GitHub latest API 已指向该版本。
- `v0.2.2` 已正式发布：标签指向 `91111ad3651db3310322e8cb2414c64e94dc2f53`，GitHub Actions Run `32583240047` 为 `completed/success`，Release 为 `https://github.com/Kratosmax/PathEcho/releases/tag/v0.2.2`，GitHub latest API 已指向该版本。
- `v0.2.5` 已正式发布：标签指向 `c1c1482e747a4cc8eb43629557850e54e7dda87a`，GitHub Actions Run `32626115826` 为 `completed/success`，Release 为 `https://github.com/Kratosmax/PathEcho/releases/tag/v0.2.5`。
- `v0.2.6` 已正式发布：标签指向 `d421f7cefb616b696740135117b9321f3ef81400`，GitHub Actions Run `32627247501` 为 `completed/success`，Release 为 `https://github.com/Kratosmax/PathEcho/releases/tag/v0.2.6`；8 个资产、7 条 SHA-256、Lite/Full 签名和 latest 路由均已核验。
- `v0.2.7` 已正式发布：标签指向 `aa7d096362a41acbb962286113caade897975853`，GitHub Actions Run `32629134027` 为 `completed/success`，Release 为 `https://github.com/Kratosmax/PathEcho/releases/tag/v0.2.7`；8 个资产、7 条 SHA-256、Lite/Full 签名和 latest 路由均已核验。
- `v0.3.0` 已正式发布：标签指向 `2f57bf220217ddf286dfc667ff4aaa8de4012494`，GitHub Actions Run `32874494465` 为 `completed/success`，Release 为 `https://github.com/Kratosmax/PathEcho/releases/tag/v0.3.0`；8 个资产均为 uploaded，latest 已指向该版本，三份签名清单的版本、通道、ZIP URL、大小和 SHA-256 与 Release 资产一致。按用户要求未下载 Full/Lite 包，也未执行真实旧版升级链。
- `v0.3.1` 已正式发布：标签指向 `d54c13bc4b7f8cc9053cbe689c190375fd22527f`，GitHub Actions Run `32876134335` 为 `completed/success`，Release 为 `https://github.com/Kratosmax/PathEcho/releases/tag/v0.3.1`；8 个资产均为 uploaded，latest 已指向该版本，三份签名清单与 Release ZIP 元数据一致。
- `v0.4.0` 已正式发布：标签指向 `15bf74a46e36de1580359cca871b9ff2db9f0c8e`，GitHub Actions Run `32945051053` 为 `completed/success`，Release 为 `https://github.com/Kratosmax/PathEcho/releases/tag/v0.4.0`；8 个资产、7 条 SHA-256、三份正式签名清单、Lite/Full 包结构、latest 路由及 `v0.3.1 -> v0.4.0` 隔离升级链均已核验。
- Git 身份：仓库级 `小火车 <kratosthemax@gmail.com>`。
- 用户已授权创建公开仓库 `Kratosmax/PathEcho`、提交、推送、配置更新签名 Secret 并发布首个可用版本；失败标签按发布规则递增补丁版本。
- 初始实现与接续文件均已推送，当前可从公开仓库跨设备接续。

## 已实现

- 本机目录单向、反向和双向同步，支持删除策略、删除前备份和双向冲突策略。
- 游戏存档定时、重点文件变化、文件变化和进程退出备份；新版本使用完整逻辑清单与 SHA-256 对象库去重，不再创建会联动修改历史的硬链接视图。
- 文件变化备份携带具体改名、新增和删除路径，只重新读取变化范围；重点文件、定时、游戏退出和监听溢出执行整目录快照，监听 overflow 使用独立原子标记，不会被普通事件覆盖。
- 旧 `files` 快照继续支持浏览、迁移和回档；全部对象逐项验真后自动升级到 schema 2，损坏对象可回退有效旧视图。“打开备份”生成独立浏览副本，修改副本不会影响历史。
- 备份迁移与导入按清单复制唯一对象并逐项校验；发现功能按 `(ProfileId, ProfileRoot)` 区分同 ID 的多份备份。
- 整目录、变动文件和正则文件事务回档，包含占用识别、PID 与启动时间校验、关键进程保护和失败回滚。
- WPF 主界面、业务弹窗、托盘、当前用户开机自启、统一设置页和预览截图模式。
- Windows 11 build 22621+ 全客户区 Acrylic；DWM 返回失败、旧系统或 `PATHECHO_DISABLE_BACKDROP=1` 时完整回退为不透明 `#F4F7F8`。
- ECDSA P-256/SHA-256 签名更新清单，签名覆盖产品、版本、通道、URL、SHA-256、大小和公告；客户端与外部更新器均验签验包。
- Full/Lite 同通道更新，包含 URL 前缀线路、HTTP 出网代理、稳定故障转移、重定向 allowlist、下载大小与停滞限制、失败暂存清理、包结构与路径穿越检查。
- 安装目录 marker、外部 launcher、PID 身份等待、事务 stage/backup/rollback、重启交接和安装版卸载器保留。
- Full Setup、Lite Setup、Full ZIP、Lite ZIP、兼容/通道清单、SHA256 文件和标签触发的 GitHub Actions 发布链。
- Lite Setup 在 32 位与 64 位注册表视图中读取 `InstalledVersions\x64` 的 Desktop Runtime 版本值，避免把版本值误当子项或漏掉官方 x64 登记位置。
- 新建同步任务和游戏存档的 WPF 绑定集合写入统一切回 Dispatcher，修复跨线程创建错误。
- 游戏程序可从运行中进程选择，支持程序名、PID、窗口标题和路径搜索，持久化仍只保存可执行路径。
- 更新设置支持多条 URL 前缀、直连兜底、0 到 10 优先级与 0 禁用；HTTP 出网代理继续独立配置。
- 可选 Debug 滚动日志、统一 PathEcho Logo、窗口/任务栏/托盘/安装器图标和高对比侧栏 Hover。
- 同步任务页支持弹性列宽、绿色高对比 Hover/选中态、搜索与状态筛选、目录可用性提示、双击编辑、右键同步/编辑/复制/打开目录/删除，以及空数据禁用操作。
- 同步任务支持相对路径 glob 包含/排除规则；扫描与规划均应用相同规则，跳过重解析点，预演与真实执行共用 `SyncPlanner` 且预演不更新目录或基线。
- 同步监听记录具体路径并只失效相关哈希缓存；普通同步从每轮最多四次全目录枚举降为两次，只有生成冲突副本时为准确建立基线而额外重扫。`DeletionMode.Ignore` 优先于冲突偏好，目标端被外部修改也会触发单向校准。
- 同步成功与失败运行记录原子保存并限制为最近 200 条；历史写入失败不会反转已经成功的文件同步结果。
- 智能游戏识别从 GitHub 签名目录匹配当前运行进程，复用更新的 URL 前缀线路和 HTTP 出网代理，具备结构/大小/签名校验、原子缓存、离线回退和修订号防回退。
- `config/game-catalog.source.json` 由独立 GitHub Actions 工作流用现有正式密钥签为 `config/game-catalog.json`；游戏规则可独立更新，不需要重新发布程序包。
- 预览模式使用隔离的内存配置和独立单实例锁，不读取真实用户配置，也不受已运行正式实例阻塞。
- 游戏备份首次读写失败后在默认备份目录的 `temp\<游戏ID>\<事务ID>` 建立完整校验副本；读源、写正式备份和清理旧版本均按 5 秒重试，每 10 次询问是否继续，并受后台监听与运行时退出令牌取消。
- 旧版本清理失败不会重复创建快照；停止正式写入重试时保留完整临时副本并报告路径，成功后清理本事务目录，目录清理不进入重解析点。
- 存档历史支持搜索与按游戏 ID 筛选，大量版本的集合刷新会合并到一次 UI 调度；同名游戏不会混筛。
- 游戏存档支持按钮和双击编辑；修改单独备份目录会迁移、校验并在保存失败时回滚，其他字段修改只重启对应监听。
- 设置保存会先提交线路表格编辑、验证新备份目录可写，再原子写入并反序列化复核配置；默认目录改变时迁移已有隐式备份，没有可迁移数据或开机自启登记失败都会给出明确反馈。
- “浏览”选择默认备份目录后会弹出迁移确认并立即复用设置保存链路；单实例协调器继续使用 `Local\PathEcho.SingleInstance` 兼容旧版，同时由专用线程持有 Mutex，第二实例只发送激活信号，不再错误释放锁。

## 当前待办

- 当前无已授权但未完成的发布任务；等待用户后续需求。
- `v0.4.0` 已公开，不得覆盖同版本资产；后续修复必须递增版本并保留现有标签和 Release 审计记录。
- `v0.3.0` 及更早版本若已出现文件占用或下载后退出无后续，需要手动覆盖安装 `v0.3.1` 一次；旧客户端无法通过新包修复自身正在执行的旧更新器代码。
- GitHub 连接若受全局失效代理 `http.https://github.com.proxy=http://127.0.0.1:10809` 影响，只能命令级临时覆盖，不应静默修改用户全局配置。

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

2026-08-26 本地候选实际通过：

- `dotnet build PathEcho.sln -c Release --no-restore -m:1 -v:minimal`：0 警告、0 错误。
- `dotnet run --project tests\PathEcho.SmokeTests\PathEcho.SmokeTests.csproj -c Release --no-build`：37/37 通过；本轮新增覆盖同大小同时间戳变化、忽略删除优先级、独立浏览副本、增量改名/删除清单、旧 schema 迁移与坏对象回退、同步/游戏监听 overflow、重点文件整目录语义、唯一对象导入及同 ID 不同根发现。
- `dotnet format PathEcho.sln --no-restore --verify-no-changes` 与 `git diff --check` 通过。
- 本地 `0.4.0` Lite 候选包 `temp/preview-0.4.0-candidate/PathEcho-0.4.0-Lite.zip`：563246 字节，SHA-256 `95011F90A11D0A2060C09B10697FBDB0C5F92389D66F5230F673F5A4D2759DDC`；包内版本、Lite 通道、结构及更新器 `--verify-only` 均通过。
- 正式四包链已用一次性测试密钥演练到包生成：Lite ZIP 574262 字节、Lite Setup 2345544 字节、Full ZIP 103017741 字节、Full Setup 73100145 字节；Lite/Full ZIP 包内更新器预检均返回 0。测试密钥已删除，ReleaseTool 按安全设计拒绝测试密钥签出的清单；正式三份清单只能由 GitHub Actions 的 `PATHECHO_UPDATE_PRIVATE_KEY` 生成并在线验签。
- `0.4.0` 包内程序真实 WPF 1180×760 截图 `temp/verification-20260826/packaged-history-0.4.0.png`：81005 字节，SHA-256 `185793E813B92A6FCBDAB99B0D5ED8642F49D4CA12C9FCC2925CCB75E936E2ED`；应用退出码 0，采样 8968 像素中 5864 个非白、104 种颜色。版本显示、历史搜索/筛选、长路径、滚动区及打开/回档按钮均无重叠或不可读文本。
- Windows 应用控制辅助工具两次初始化均因自身 `failed to write kernel assets: 系统找不到指定的路径` 失败，因此未完成自动化双击动作；事件绑定、编辑构造器字段回填和 ID 保留由编译与静态契约测试覆盖，视觉由真实 WPF 主窗截图覆盖。

2026-08-26 `v0.4.0` 发布后核验：

- GitHub Actions Run `32945051053` 在 3 分 13 秒内完成且结论为 success；Release 非草稿、非预发布，标题、标签和正文只包含 `0.4.0`，GitHub latest API 与 `releases/latest/download/update.json` 均指向本版本。
- 8 个正式资产全部实际下载到项目 `temp`；`SHA256SUMS.txt` 的 7 条记录与本地重算值及 Release API digest 全部一致。Full Setup、Full ZIP、Lite Setup、Lite ZIP 的 SHA-256 分别为 `F56ECBAE1B585C9DCCD9E26C36C92670F14C3FB3D7E37E702DC2340AD074969E`、`C3EF8031F9F97F27D1DD1F1D3B68F65EEBCFD4E1CAB0AFBF8A93470A9D0E2DA3`、`AE73ACA09958EBEFE1A5F91F27FB7E5D223B436EF785A987CB860167E05A4783`、`C14A3D96F90E395A029A2307B704B462A5060B7544A228CB966BECBA461C2975`。
- `update.json` 与 `update-lite.json` 逐字节一致；三份清单使用 PathEcho 内置正式 ECDSA 公钥实际验签通过。Lite 指向 562547 字节正式 ZIP，Full 指向 99826469 字节正式 ZIP，版本、通道、URL、大小和哈希全部匹配；两个正式 ZIP 又分别通过各自包内更新器的哈希、版本、通道和结构预检，退出码均为 0。
- 上一正式版原始 `PathEcho-0.3.1-Lite.zip` 为 548388 字节，SHA-256 `2C8492E1E00FB3BE7465F1493692D34EAD87EA7CB0430B8E501A5A31411B9B80`。其原始 `PathEcho.Core.dll 0.3.1.0` 完成 latest 清单获取、正式公钥验签、562547 字节真实包下载与预检，原始更新器完成握手、父进程退出、原子替换、结果反馈、备份清理和 `0.4.0` 预览重启；结果为 `succeeded`，安装程序集版本 `0.4.0+15bf74a46e36de1580359cca871b9ff2db9f0c8e`，事务残留和进程残留均为 0。
- 升级后预览截图 `temp/release-verification-v0.4.0/upgrade-e2e/updated-preview.png` 为 1180×760、80889 字节，SHA-256 `78F96CF841DF6A57EA306D428538D26BA0ED09445F59C3C332322342B709EAB9`；8968 个采样像素中 5241 个非白，107 种颜色。沙盒查看工具受项目 ACL 限制未能再次人工读图，视觉结论沿用同版候选包已完成的真实 WPF 人工核验，不把本次像素检查扩张成新的人工视觉通过。
- E2E 临时目录联接创建前确认目标路径不存在，结束后仅删除联接条目；联接已不存在，项目 `temp` 中证据仍保留，真实用户配置未读取或修改。

2026-08-26 `v0.3.0` 发布后轻量核验：Actions Run `32874494465` 在 2 分 49 秒内完成且结论为 success；Release 非草稿、非预发布，8 个预期资产均为 uploaded，GitHub latest API 指向 `v0.3.0`。`update.json`、`update-lite.json` 和 `update-full.json` 可下载，版本均为 `0.3.0` 且带签名；兼容清单指向 Lite，Lite/Full 的下载 URL、大小与 SHA-256 均匹配对应 ZIP 资产。按用户要求未下载正式安装包或 ZIP，未执行真实旧版升级链。

2026-08-26 `0.3.1` 根因修复：外置更新器此前未设置 `WorkingDirectory`，会继承 PathEcho 安装目录并使 Windows 拒绝 `Directory.Move(target, backup)`。隔离复现得到与用户截图一致的 `The process cannot access the file because it is being used by another process.`；将当前目录切到外部目录后同一移动立即成功。主程序现显式把 launcher 设为工作目录，更新器自身也切到 `AppContext.BaseDirectory`。Release 构建 0 警告、0 错误，32/32 测试通过。`v0.3.0` 及更早版本执行的是各自旧更新器，无法通过新包修复正在运行的旧代码，必须手动覆盖安装 `0.3.1` 一次。

2026-08-26 `v0.3.1` 发布后轻量核验：Actions Run `32876134335` 在 2 分 55 秒内完成且结论为 success；Release 非草稿、非预发布，8 个预期资产均为 uploaded，GitHub latest API 指向 `v0.3.1`。三份更新清单可下载、版本均为 `0.3.1` 且带签名；兼容清单指向 Lite，Lite/Full 的下载 URL、大小与 SHA-256 均匹配对应 ZIP 资产。按用户要求未下载正式安装包或 ZIP，未执行真实旧版升级链。

2026-08-23 本地功能分支实际通过：

- `dotnet build PathEcho.sln -c Release --no-restore -m:1 -v:minimal`：0 警告、0 错误。
- `dotnet run --project tests\PathEcho.SmokeTests\PathEcho.SmokeTests.csproj -c Release --no-build` 在真实 Windows 用户环境 21/21 通过；新增覆盖配置全部用户设置的保存后重载、暂存清理、线路表格提交、目录选择确认、有/无旧备份的目录迁移以及重复启动锁生命周期，并覆盖同步过滤/预演、200 条运行历史上限、签名游戏目录、前缀故障转移、可信缓存与旧签名重放拒绝。Restart Manager 同一轮测试通过。
- `dotnet format PathEcho.sln --no-restore --verify-no-changes` 与 `git diff --check` 通过。
- 920×620 隔离 WPF 设置页截图 `temp/settings-directory-confirm-fix.png` 已真实渲染并核验，没有空白、重叠或控件截断；此前同步页、游戏页、存档历史与同步运行 Tab 的证据位于忽略提交的 `temp/ui-check`。
- 本地 Lite 预览包 `temp/preview-review/PathEcho-0.2.2-Lite.zip`：512478 字节，SHA-256 `2E60A35F51504080C6EC511EE370729BF563DBA4083EA1709395FBB4A7729A12`；最新单实例退出竞态修复已包含，包内更新器 `--verify-only` 返回 0。该包沿用当前正式版本号，仅为本地验收，不是 Release。默认 `temp/preview` 因旧预览实例 PID 38272 占用而保留未覆盖。

2026-08-22 实际通过：

- 本轮安装器修复：系统 `dotnet --list-runtimes` 已确认 `Microsoft.WindowsDesktop.App 8.0.30 x64` 存在；实际登记位于 32 位注册表视图，版本 `8.0.30` 是值名而非子项。Lite Setup 改用 `RegGetValueNames` 并覆盖 `HKLM64/HKLM32/HKCU64/HKCU32`。
- `0.2.2` Release 配置构建 0 警告、0 错误；SmokeTests 16/16 通过，新增 Lite 安装器运行时检测契约；全解决方案 `dotnet format --verify-no-changes` 通过。
- `0.2.2` 本地 Lite 预览包 `temp/preview/PathEcho-0.2.2-Lite.zip`：454347 字节，SHA-256 `0027294A15654A9A447EA03996C75C002FD0BDEFB4D3C91754FA9791C22646B8`；候选包内更新器 `--verify-only` 返回 0，程序集、`version.txt` 和通道均为 `0.2.2/Lite`。本地预览不是正式 Release。
- Inno Setup 6.7.3 成功编译 `temp/installer-test-0.2.2/PathEcho-0.2.2-Lite-Setup-Test.exe`：2284514 字节，SHA-256 `FCBB194BE3C9E899B4642F78BF492B61AA33597E3739ABE5CF39F56F91CFD4F5`。
- 真实 Lite Setup 在已安装 `.NET 8.0.30 Desktop Runtime x64` 的系统中静默安装到隔离测试目录并通过，安装器与卸载器退出码均为 0；入口、运行配置和卸载器存在，卸载后目录与 HKCU 卸载登记均已清理。
- `v0.2.2` GitHub Actions Run `32583240047` 已完成且结论为 success，Run 的 `head_sha` 与标签提交 `91111ad3651db3310322e8cb2414c64e94dc2f53` 一致。
- `v0.2.2` Release 非草稿、非预发布，共 8 个 uploaded 资产，正文只包含 `0.2.2` 变更；`SHA256SUMS.txt` 的 7 条记录与对应 Release API digest 全部一致，GitHub latest API 已指向该 Release。
- 线上 `update.json` 与 `update-lite.json` 逐字节一致；Lite 与 Full 清单均使用客户端内置正式公钥实际验签通过。Lite 指向 454219 字节、SHA-256 `DCA04E96924CB38A7C2724CC676B36B9974D7D41EBB6F94DFAC74D9256F84DD3` 的 ZIP，Full 指向 99719217 字节、SHA-256 `7CBFD0B4F12A85037E65C2099A0CDD51C7FF9AB14C193F22CC93B765F0D36CF3` 的 ZIP。
- 正式 `PathEcho-0.2.2-Lite-Setup.exe` 为 2283317 字节，SHA-256 `DE49187E98A8F1A2E1888DE330F9EA097FDB0E34A00F46D189D6F1ACFB91F246`；已在本机真实安装 `0.2.2` 并完整卸载，安装/卸载退出码均为 0，测试目录与卸载登记已清理。
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
- `v0.2.1` GitHub Actions Run `32569018960` 已完成且结论为 success，Run 的 `head_sha` 与标签提交 `f3506aff4f7271495f160b7fd6e859a17949b4e0` 一致。
- `v0.2.1` Release 非草稿、非预发布，共 8 个 uploaded 资产：四种安装/便携包、三份更新清单和 `SHA256SUMS.txt`；Release 正文只包含 `0.2.1` 变更。
- `SHA256SUMS.txt` 包含 7 条记录并与 GitHub Release API digest 一致；`update.json` 与 `update-lite.json` 均为 2707 字节且 SHA-256 同为 `5C95518378D0A6532B81D2F202B14A4A94D3532F8791501F67CBAC35358C9E8B`，`update-full.json` 为 2704 字节且 SHA-256 为 `379F436CBA6C9364B4D6030BCE388FB777E70579FAC60B4CC3D140522E10F9C1`。
- 正式 Lite 清单指向 454249 字节、SHA-256 `72597DFF1D7592B391C27CBBB216F8A9AE2B8AAEF5E91BD85784F04239FECDA7` 的 `PathEcho-0.2.1-Lite.zip`；正式 Full 清单指向 99719260 字节、SHA-256 `79485BC86B1583AF6DC9C0220F039F20694425221C3AD63340ED4282CBB799D2` 的 `PathEcho-0.2.1-Full.zip`。两份线上清单均已使用客户端内置公钥实际验签通过。

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
> 当前正式版本：`0.4.0`。`v0.2.3` 因 ReleaseTool 编译失败未发布，历史发布记录保持不变。

`0.2.8` 本地候选验证：更新交接改用落盘握手，更新器全程持有独占锁，普通启动只瞬时探测更新事务以保留原有单实例唤醒；安装目录移动、回滚、卸载器保留和 launcher 复制使用有限文件占用重试并报告阶段/路径，失败写入独立轮转日志，半成品与过期缓存可清理。Release 与 ReleaseTool 构建均为 0 警告、0 错误，SmokeTests 27/27、`dotnet format --verify-no-changes` 与 `git diff --check` 通过。Lite 预览包 `temp/preview-0.2.8-final/PathEcho-0.2.8-Lite.zip` 为 527726 字节，SHA-256 `19FDB471A8D585F1291F9BCE1DF347032A9ACDBE86A38D7B63DE21626F2D6883`；包内新更新器与 `v0.2.7` 原始更新器的版本/通道/哈希/结构预检均返回 0。1180×760 真实 WPF 截图位于 `temp/preview-0.2.8-final/main-window.png`，进程退出码 0。最终更新器在隔离安装副本中面对 1.2 秒独占文件仍完成替换、ready 和备份清理，退出码 0、事务残留 0；后续正式发布状态以当前快照为准。

`0.2.7` 本地验证：Release 构建与 ReleaseTool 构建均为 0 警告、0 错误，SmokeTests 24/24 通过，`dotnet format --verify-no-changes` 与 `git diff --check` 通过。Lite 预览包 `temp/preview-0.2.7/PathEcho-0.2.7-Lite.zip` 为 519276 字节，SHA-256 `413ACBA09150E68788F65AF21799D2AC257FD8382FF2B1EF06F8C1F31645B65A`。真实 WPF 验证覆盖有效更新结果显示后消费删除，以及损坏 JSON 始终显示通用反馈、保留证据并写 Critical 日志。

`v0.2.7` 发布后验证：Actions Run `32629134027` 为 `completed/success`，8 个资产均 uploaded，`SHA256SUMS.txt` 的 7 条记录与 Release API digest 一致；Lite 清单指向 519074 字节、SHA-256 `0994B3F7D6139D6C8E7AFB26CF370F279B901DFF6843C97F88F71BE158CD90F9` 的 ZIP，Full 清单指向 99784760 字节、SHA-256 `E736D2BF8FFF3403B51D4D62F60CFAAF0386BECE2E434E3E2A0DE8AAB4A73784` 的 ZIP，两份清单均使用客户端内置公钥验签通过。`update.json` 与 Lite 清单逐字节一致，latest 实际下载在一次连接重置后有限重试成功返回 200 且与标签资产逐字节一致。`v0.2.6` 原始 Lite 更新器已用线上签名清单和正式包完成到 `v0.2.7` 的父进程等待、替换、结果反馈、ready、备份清理和预览重启，更新器退出码 0。

`v0.2.6` 发布后验证：Actions Run `32627247501` 为 `completed/success`，8 个资产均 uploaded，`SHA256SUMS.txt` 的 7 条记录与 Release API digest 一致；Lite/Full 清单使用客户端内置公钥验签通过，`update.json` 与 Lite 清单逐字节一致，latest 清单实际下载返回 200 且与标签资产逐字节一致。`v0.2.5` 原始 Lite 更新器已用线上签名清单和 `v0.2.6` 正式包完成父进程等待、替换、结果反馈、ready、备份清理和预览重启，更新器退出码 0。

`0.2.5` 本地验证：Release 构建与 ReleaseTool 构建均为 0 警告、0 错误，SmokeTests 23/23 通过。Lite 预览包 `temp/preview-0.2.5/PathEcho-0.2.5-Lite.zip` 为 517664 字节，SHA-256 `F92DA1896A167658469F31B6780010A2DF532C1AE72230B94773A618FEDAF7F4`，包内更新器预检返回 0；真实 WPF 设置页截图位于 `temp/preview-0.2.5/settings.png`。线上 `v0.2.4` 旧更新器在 `temp/update-e2e-0.2.4` 隔离环境完成等待、替换和重启，退出码 0；因此旧版问题不是干净环境必现，但旧代码缺少失败反馈。
