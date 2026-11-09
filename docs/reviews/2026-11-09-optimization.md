# PathEcho 性能、代码结构与界面改进交付

日期：2026-11-09（UTC+8）。基线：main / 9588a64 / 0.6.2。交付为本地未发布改动，未提交、推送或创建 Release，正式版本号未变。

## 已实施

### 性能

- [DirectoryScanner](../../src/PathEcho.Core/Sync/DirectoryScanner.cs) 改为按目录枚举；当排除模式可证明覆盖整个子树时，在进入前跳过。保留现有 SimpleExpression glob、排除优先、重解析点跳过、取消及错误语义。复用枚举返回的文件元数据，减少重复属性读取。
- 扫描缓存按近用顺序限制为最多 8 个根、合计 50,000 个文件条目。超预算的大根仍返回完整结果，只不保留可选哈希缓存。强制哈希扫描结束后不留缓存；缺失根释放原缓存，避免唯一暂存路径长期积累。
- [通知队列](../../src/PathEcho.Core/Notifications/BackupNotificationQueue.cs) 从 WPF Window 提取为可测试的纯状态，保留合并计数、原位续期、容量 4、5 秒过期。时间使用单调时钟。
- [通知窗口](../../src/PathEcho/Notifications/BackupNotificationWindow.xaml.cs) 按最早过期点一次调度；无变化时不重建卡片、不强制布局、不重复定位。隐藏停表，关闭解绑并释放队列。
- Lite 预览构建显式设置 PublishSelfContained=false，避免当前 SDK 推断出自包含更新器；实际更新器从 67,792,617 字节降到 428,159 字节。

### 代码结构与状态

- 展示模型从大型 Runtime 文件拆到 [RuntimeRows](../../src/PathEcho/Services/RuntimeRows.cs)、[SyncTaskRow](../../src/PathEcho/Services/SyncTaskRow.cs)、[NotifyObject](../../src/PathEcho/Services/NotifyObject.cs)，保留现有绑定接口。
- 同步运行分类改用明确的 Idle / Running / Succeeded / Failed，筛选不再通过中文状态文案 Contains 推断。错误说明、语言或成功文案包含“失败”字样不会改变分类。
- 行构造器不再同步探测磁盘/UNC；目录状态先显示“检查中”，后台更新后才进入“可用/需要检查”。启动、添加、编辑、切回同步页与约 5 秒轮询都会请求刷新。
- [运行时目录刷新](../../src/PathEcho/Services/PathEchoRuntime.cs) 将 OS 探测放到工作线程，最多一个探测批次在途；调用方最多等 2 秒，超时仍保持占位，不无限堆积线程。调用方取消与退出可结束等待；Dispatcher 投递带 token，已删除或替换的旧行不会接收结果。

### 界面审美与使用流程

- 统一主视图标题、次级说明、间距、工具条分组、按钮与下拉框、表格留白、焦点和禁用状态，保持深绿侧栏与浅色内容的现有风格。
- 移除同步列表 520px 高度上限；名称/路径保留弹性和 tooltip，固定工具列设置明确最小宽度。小窗口空间不足时横滚，不再把“模式”“触发方式”或操作按钮挤成残字。
- 设置页改为固定标题、可滚动分类正文、固定底部保存/发现备份区。滚到底部后仍可直接保存。
- PromptWindow 改善长标题与操作换行。没有统一绑定 Enter/Esc，因为“次按钮”在不同业务里不总是取消，避免改变危险操作语义。

## 实测

扫描样本为本机文件系统热缓存，12,800 个排除文件、100 个保留文件、160 个子目录；原 DLL 与新 DLL 交替运行，每场景 9 次取中位数。每次返回路径与内容哈希签名一致。

| 场景 | 改进前 | 改进后 |
|---|---:|---:|
| 有过滤，复用扫描缓存 | 22.48 ms | 0.29 ms |
| 有过滤，新建扫描器 | 39.44 ms | 15.65 ms |
| 有过滤，强制哈希 | 42.35 ms | 15.40 ms |
| 无过滤，复用扫描缓存 | 371.77 ms | 29.49 ms |

无过滤的全量哈希，两轮分别约慢 4%、快 3%，没有明确稳定加速证据。没有测试冷盘或网络盘，不能把表中倍率用于所有真实游戏目录。原始数据：[最终轮](../../temp/optimization/scanner/results.json)、[首轮](../../temp/optimization/scanner/results-first.json)。

扫描 32 个不同根后，普通缓存根数从 32 降到 8；32 个强制哈希扫描根从 32 降到 0。这是缓存条目约束，不是进程 RSS 上限。

真实 WPF 通知窗口对比：1.1 秒没有新通知的时间段，旧原始程序集触发 5 次 timer tick 且替换卡片，新版为 0 次且保留同一卡片。两者均在 5.4 秒前后检查时已隐藏，重复通知合并为 2 次；新版关闭后 timer 停止，旧版未停止。数据：[旧版](../../temp/optimization/ui-probes/baseline-results.json)、[新版](../../temp/desktop-tests/results.json)。

## 验证与独立审查

- Release 构建：0 警告、0 错误。
- 原有 43 项与 3 组新行为回归合计 **46/46 通过**。新增组内覆盖过滤交叉与前缀边界、缓存预算、真实 junction 跳过、取消、通知到期/续期/淘汰、状态分类与目录离线恢复。
- [桌面行为测试](../../tests/PathEcho.DesktopTests/Program.cs) 已纳入解决方案，使用真实 WPF 控件验证横滚右端动作、固定保存区、下拉弹层/方向键/Esc/优先级回写、通知生命周期。受控 OS probe 闩锁验证限等、单飞、调用方取消、退出后不再探测右侧且不更新旧状态；不连接真实网络卷。
- 测试宿主使用隔离内存配置；不调用应用的正常 Startup，不读取用户真实配置，不注册自启，不创建实际存档备份。
- 独立成员审查代码并亲自检查 1180×760 / 920×620 四页截图，以及窄窗修正、横滚右端、设置底部、同步空态。首轮发现的压列问题已修复后复核，无剩余本轮阻断。
- 当前格式与 whitespace 检查通过；最终 Lite 包通过包内更新器哈希、版本、Lite 通道及结构预检，包内真实入口以隔离预览方式启动并退出 0。

可重复命令（Windows 交互会话，桌面测试会短暂显示窗口）：

~~~powershell
dotnet build PathEcho.sln -c Release -m:1 -v:minimal
dotnet run --project tests/PathEcho.SmokeTests/PathEcho.SmokeTests.csproj -c Release --no-build
dotnet run --project tests/PathEcho.DesktopTests/PathEcho.DesktopTests.csproj -c Release --no-build
dotnet format PathEcho.sln --no-restore --verify-no-changes
powershell -ExecutionPolicy Bypass -File build/Build-Preview.ps1 -OutputDirectory temp/preview-optimization-local
~~~

## 本地预览

[Lite ZIP](../../temp/preview-optimization-20261109-final/PathEcho-0.6.2-Lite.zip)：**629,400 字节**。

SHA-256：

~~~text
11CA631C7798D7F41AAD99DBB942EDEAF72CD5339E1C226D62F6F503B3CB048B
~~~

这是本地预览，非正式 Release。需要 .NET 8 Desktop Runtime x64。解压后运行 [Start-Preview.cmd](../../temp/preview-optimization-20261109-final/PathEcho-0.6.2-Lite/Start-Preview.cmd) 可查看隔离示例，不读取或修改正式配置；直接运行 PathEcho.exe 则使用正常应用配置。

真实包内界面：[设置页](../../temp/optimization/ui/packaged-settings-1180.png)。其他证据：[同步页](../../temp/optimization/ui/Sync-920-final.png)、[同步右端](../../temp/optimization/ui/Sync-920-right.png)、[历史右端](../../temp/optimization/ui/History-920-right.png)、[游戏右端](../../temp/optimization/ui/Game-920-right.png)、[设置底部](../../temp/optimization/ui/Settings-920-bottom.png)。temp 中的包、图片、基线 DLL 和基准数据仅为本地交付，不进入 Git。

## 保留边界

- 本轮落实性能、代码结构与界面改进，修复了前次审查 R9 的目录状态陈旧问题；[初始审查](2026-11-09.md) 的 R1–R8 备份/网络/发布问题没有在本轮全部修复，仍需独立排期，不能把本次回归通过解释为它们已解决。
- 单个 OS 网络目录探测仍可能被系统长期阻塞，无法强拆；单飞会让其他目录的状态刷新暂缓，但不阻塞界面及退出等待，也不会不断新增探测线程。
- 未验证多 DPI、真实云盘双机传输、Prompt 三按钮长文案、显示器热插拔全过程或旧版自动更新全链。通知定位在队列变化时重算，不再每 200ms 重算。
- 超 50,000 条目的大根会放弃缓存，后续扫描可能重新哈希；扫描器没有扩大原有并发使用契约。
