# 快速定位与本地诊断方案

状态：P0/P1 已在 0.8.0 候选实现并通过本地回归，正在发布核验；P2 跨业务扩展仍为后续工作。适用对象：PathEcho 的配置导入、路径修改、历史发现及云盘同步。依据：2026-11-09 用户确认此次历史消失的实际原因是云盘漏同步 manifest.json。不得将此前本地复现的 N/D、嵌套目录缺陷冒充本次现场根因。

## 1. 本次复盘

| 环节 | 事实与错误 | 改正方式 |
|---|---|---|
| 首轮取证 | 有导出配置，但尚无接收端目录结构和清单存在性。过早扩展到 GUID 格式、嵌套目录与关联逻辑，并修改了另一些可复现问题 | 先核对实际读取根、snapshot 目录和 manifest；每个根因假设必须附支持/反对证据；未证实前保持只读 |
| 最小诊断顺序 | 用户给出标准 N 格式目录后，才询问 manifest；这一检查本应优先 | 实际根 → 来源目录 → 快照目录 → 清单存在/可读 → 解析/身份 → 对象存在/长度 → UI 筛选，按最早失败环节定位 |
| 日志价值 | 正式版只有开启 Debug 与创建档案等动作记录，零结果和被跳过清单无解释。没有先验证日志能否回答问题就让用户取日志 | 默认保留关键结果摘要；零结果必须有分类，不依赖提前开 Debug |
| 脚本交付 | 测试显式指定 OutputDirectory，漏测用户真实的默认无参数 -File/双击入口；Windows PowerShell5.1 参数默认值中的 PSScriptRoot 为空导致失败 | 用户实际入口是验收对象；不以底层函数/带参数运行成功代替默认启动验收。已本地复现并修源，不再要求用户替开发者测试 |
| 结论隔离 | 确认了另一些产品缺陷，但不能据此宣布用户现场已修复 | 分开记录“现场根因”“独立复现缺陷”“待验证假设”；只发布与证据匹配的结论 |

本次正确的一轮判断应为：接收端实际根和来源 ID 匹配 → 已有快照文件夹 → 检查 manifest.json → 缺失 → 显示元数据未到齐。无需先开发采集脚本、修改扫描范围或分析全部内容对象。

## 2. 目标与边界

目标是一次界面操作即可回答：程序读哪里、期待哪个来源、发现多少候选、停在哪一阶段、应采取什么动作。以本次案例为标准，期望在一次“检查历史”后直接显示清单缺失；“5 分钟内定位”是验收目标，不是已达成或对所有网络故障的保证。

这是本地诊断事件，不接入统计平台、不自动上传、不增加服务器或账号。默认诊断不读取/哈希实际存档或内容对象；必须验证完整性时由用户在单独操作中明确发起。读 metadata 的诊断不代替恢复前内容校验。

## 3. 单一诊断模型，UI 和日志共用

Core 返回 HistoryScanReport，而不只返回一个空版本数组；日志、界面、证据导出都消费同一报告，避免三个组件自行扫描产生矛盾。

建议模型：

- HistoryScanReport：operationId、profileId、sourceProfileIds、configRevision、实际解析后的备份根、开始/结束时间、单调时钟 durationMs、是否完成/取消/截断、来源和快照计数、逐候选结论。
- SnapshotInspection：实际路径、阶段、reasonCode、元数据来源、可信度、是否可回档、清单 schema/ID、必要的原生错误码。无清单时文件数和版本时间为未知，不编造 Manifest。
- ScanSummary：candidateDirectories、readyVersions、awaitingManifest、manifestInvalid、identityMismatch、awaitingContent、accessDenied、otherFailures、skippedByPolicy。每个候选只归入最早失败的主原因；细节可以记录多个子原因。汇总应能对账。

关键 reasonCode：

| 阶段 | reasonCode | 界面含义/处理 |
|---|---|---|
| 配置与根 | CONFIG_UNSAVED / ROOT_NOT_FOUND / ROOT_ACCESS_DENIED | 区分编辑值与磁盘配置、路径不存在与权限问题；不能仅用 Directory.Exists=false 断言数据不存在 |
| 定位来源 | SOURCE_NOT_FOUND / SOURCE_UNASSOCIATED / SCAN_INCOMPLETE | 清楚区分没有找到、找到但身份不符、扫描未完成 |
| 清单 | MANIFEST_MISSING | 已有快照目录但缺少清单；保留候选，等待同步 |
| 清单 | MANIFEST_ACCESS_DENIED / MANIFEST_CLOUD_UNAVAILABLE | 权限拒绝或已知云提供程序错误；仅有一般 I/O 错误时保留原始错误，不猜云盘故障 |
| 清单 | MANIFEST_INVALID_JSON / MANIFEST_INVALID_SCHEMA | 解析或格式无效；记录异常类型/错误码，不记录完整清单正文 |
| 身份 | PROFILE_ID_MISMATCH / PROFILE_UNASSOCIATED | 记录预期 ID、清单 ID 与关联规则，不凭游戏名猜测 |
| 内容 | OBJECT_MISSING / OBJECT_LENGTH_MISMATCH / OBJECT_ACCESS_DENIED | 等待内容到齐，禁止回档；元数据大小匹配也不代表哈希已经通过 |
| 展示 | RESULT_FILTERED_OUT | 报告原始数量、当前游戏/搜索筛选及可见数量，区分后台没找到与前台筛掉 |
| 完成 | READY / CANCELED / TIMEOUT / SCAN_INCOMPLETE | 只有完整扫描和可用结果才称成功；取消不冒充空历史 |

不能用 MissingManifest 直接认定“云盘质量差”或“用户配置错误”。诊断只报告本机可观察事实；本次同步漏文件是用户最终确认的原因。

## 4. 界面先给原因

本案例应显示：

> 已发现 1 个快照目录，0 个可回档版本，1 个等待备份清单。
> 原因：manifest.json 尚未出现在本机目录。
> 操作：打开该快照目录 / 重新检查 / 导出诊断。

缺清单候选不是可信 SnapshotVersion。可以使用独立的待同步行或状态面板，禁止打开/回档入口；不自动补造空清单、不删除候选、不将对象库猜测拼成存档。

导入后必须分开反馈“配置已保存且已设置来源关联”与“已找到可用历史”。前者不能作为后者的证据。若是空存档/首次使用，应报告真实空历史；若存在被跳过候选，应显示原因，不能统一使用“还没有备份版本”。

状态变化自动恢复：清单到达 → 校验身份 → 检查对象 → 数据到齐后 Ready。此前的缺清单记录不能永久缓存成不存在。显式手动检查应绕过定位缓存；周期轮询仍合并并节流。

## 5. 埋点规范

关键流程：import.start → import.parsed → import.confirmed/config.persisted → history.scan.start → root.checked → source.located → snapshot.enumerated → manifest.checked → identity.checked → content.checked → history.scan.completed → ui.presented。

- 每次用户操作产生 operationId；导入/改路径触发的扫描以 parentOperationId 关联。每次周期扫描有独立 ID，同时保留 profileId/configRevision，防止串台。
- 配置保存记录旧/新根、是否保存成功和读回验证结果；字段白名单，不能序列化整个配置。
- 日志采用逐行 JSON，包含 schemaVersion、timestamp、durationMs、appVersion/构建提交、operationId、parentOperationId、stage、result、reasonCode、计数与错误类型/HResult。异常堆栈仅详细诊断需要时记录。
- 关键结果默认保留：导入/保存结果、手动检查结果、空历史原因、状态变化和错误，即使 Debug 关闭也可查。Debug 只增加逐文件与细节，不是排障的前提。
- 周期扫描相同结果只记录首次/状态变化，重复错误聚合计数，不每 5 秒逐项刷屏。保留有界最近事件缓冲与轮转文件，建议 5×2MiB；写入失败/满盘必须降级，不影响备份和回档，也不形成递归日志故障。
- 日志说明须标注范围：查到 0 个不是全盘不存在；截断、超时、取消都不能导出“全部正常”的摘要。

以下为设计示例，不是本次实测日志：

~~~json
{"schemaVersion":1,"operationId":"scan-...","parentOperationId":"import-...","stage":"manifest.checked","result":"pending","reasonCode":"MANIFEST_MISSING","pathAlias":"backupRoot/source/snapshots/version/manifest.json"}
{"schemaVersion":1,"operationId":"scan-...","stage":"history.scan.completed","completed":true,"candidateDirectories":1,"readyVersions":0,"awaitingManifest":1}
~~~

## 6. 一键证据包

统一设置页或历史状态面板提供“检查并导出诊断”。先只读扫描并显示摘要，再由用户保存 ZIP；绝不自动发送。

建议包含：

- summary.json：主要原因、完整性/截断状态、建议动作。
- operation-events.jsonl：此次操作及相关的最近事件，按 operationId 截取。
- effective-settings.json：本游戏保存后的有效路径别名、当前/来源 ID 关联和筛选条件，不含全局网络配置。
- backup-layout.json：相关目录的有限结构、清单是否存在/可读、schema/来源 ID/文件数/必要的对象计数。
- environment.json：应用版本、构建提交、系统/.NET 版本、是否云占位/重定向链接及错误码。

默认去除用户名、盘符和非必要绝对路径，用本地别名替代；ID 在同一包内稳定映射以保留关联证据。保存前预览导出内容；确有需要时用户可选择包含原路径。不包含存档内容、blob、完整配置、完整 manifest 文件列表、代理凭据、token、签名私钥或整份历史日志。

大型/离线云盘必须可取消、有界采样并明确部分完成；不得为收集元数据强制下载全部对象。对不可中断的 OS 探测采用有界单飞，不能超时后不断堆积线程。

## 7. 开发者排查协议

1. 先写下观察事实与所缺最小证据；应用版本、实际保存后的备份根、来源 ID、候选目录及 manifest 存在性通常足够形成首轮分支。
2. 优先让程序自己的报告回答；缺少功能时一次集中请求最小截图或单个文件。避免让用户先安装工具、搬全目录、开 Debug 后重走所有流程。
3. 一个根因假设必须给出验证/反证动作，最早失败阶段未通过前，不深入后续阶段。
4. 两次无新增证据的推测或工具失败即停，改变取证方式；不通过继续写代码掩盖未知现场。
5. 根因未确认时只读取证。独立发现的产品缺陷单独建项、独立补丁，不能混作现场修复；未经必要验证不发布。
6. 交付验证从用户入口走：打包后的程序、双击启动器、默认参数、目标 PowerShell 版本、中文/空格路径。测试清单必须注明实际执行项和未测项。

这是本项目方案和本次教训记录，不自动修改用户的通用技能或授权规则。

## 8. 实施优先级与验收

| 阶段 | 最小实现 | 验收 |
|---|---|---|
| P0，先做 | 明确 MANIFEST_MISSING；返回结构化汇总；空状态/待同步提示；关键摘要不依赖Debug | 本次固定复现：目录存在、清单缺失 → 一次检查给出等待清单1/可回档0及精确原因；清单到齐自动恢复 |
| P1 | operationId/configRevision贯通，JSONL轮转、周期去重；一键本地脱敏导出 | 一份包能说明保存路径、来源、扫描结果、前台筛选；Debug关闭仍可定位；不含敏感/存档内容 |
| P2 | 同类诊断接入备份/回档/迁移/更新，性能计数和故障恢复 | 每次用户失败都有操作ID、阶段和结果；记录层故障不影响业务 |

必须加入的故障矩阵：清单缺失后到达、清单截断/非法JSON、ID不匹配、对象缺失/尺寸不符、根或单个来源无权限、目录扫描中消失、只改存档路径、改备份路径但数据未到、UI筛选隐藏、缓存命中后数据到达、取消/超时、关闭Debug、日志目录不可写/满盘、导出取消、路径脱敏及真实默认启动入口。

首个案例的可重复回归必须使用无清单的现存快照目录，而不是只测试整个备份根不存在。用户无须再次制造或复现云盘漏同步。

## 9. 当前实现与验证状态

- 已实现 HistoryScanReport、缺清单原因型历史状态、检查与取消、默认本地摘要 journal、operationId/配置修订、字段白名单与默认脱敏的五文件 ZIP 导出。
- 显式候选目录缺清单 → 清单到达但内容未到 → 内容元数据到齐恢复可用已在 Core 和真实 WPF 流程中验证；Debug 关闭仍有关键记录，周期相同结果去重；导出前/中途取消都保留原文件。
- 扫描有单飞、时间与目录/快照数量上限，单份清单8MiB、累计32MiB/100000条目及字符串长度限制；超限明确不完整，不虚构无数据。权限、解析、身份、内容元数据故障和链接边界回归通过。
- 本地Release构建0警告0错误，Smoke50/50、Desktop套件和独立审查通过；实际云提供程序按需行为、多DPI及所有网络盘长期阻塞未覆盖，不将模拟注入称实机验证。
- P2 接入所有备份/回档/迁移/更新阶段尚未实现，本版优先解决已确认的导入/历史快速定位链。
- 本次真实根因仍为用户确认云盘漏同步 manifest.json；N/D/嵌套来源定位为独立兼容性补丁，未冒充现场根因。
