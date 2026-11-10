# 导入配置后修改目录：历史识别修复记录

日期：2026-11-09。基线 main 5c8fa82 / 正式 0.7.0。本轮仅为本地修复，不提交、推送或发布，不修改正式版本号。

## 用户场景及证据边界

用户提供 schema 1 的游戏配置，来源 ID 为 9b74b71f-244f-47da-84c6-7e0abff7f3e3，游戏名为 switch2模拟器存档，启用文件变化备份与 IncludeOtherDeviceBackups，并说明导入后改为了新目录。

当前机器没有用户配置中的 F:/BaiduSyncdisk，也没有用户修改后的新路径信息；不能据此断言用户是改了 SaveDirectory 还是 BackupDirectory，亦未查看真实云盘层级。下列是隔离样本和源码已确认的结论，不能代替用户实际目录核对。

- 改本机存档路径不应影响历史读取，导入和真实确认窗口会保留来源 linked ID。
- 改备份路径后只读取新根。配置导入不包含存档数据；更改路径不会自动搬移其他设备的关联历史。Runtime 的迁移仅处理当前设备的写入 ID，不能擅自搬走云盘中其他设备的数据。
- 原历史列表只枚举备份根下的一级 N 格式 GUID 目录；发现备份功能则递归扫描，存在范围不一致。

## 已复现与修复

使用用户来源 ID 和同样的启用组合，创建只含测试 slot.sav 的快照，导出并导入，再改路径：

| 备份布局 | 原实现历史数 | 修复后历史数 |
|---|---:|---:|
| 一级无横杠 GUID | 1 | 1 |
| 一级带横杠 GUID | 0 | 1 |
| 额外云盘子目录 / 来源 GUID | 0 | 1 |

来源身份并未丢失，漏读发生在目录定位范围。修复引入只读 BackupProfileLocator：直接根/N/D 快路径，同时允许显式 own/linked ID 在选择根内定向查找；不会凭游戏名称关联未知嵌套游戏。现有 IncludeOtherDeviceBackups 的广扫范围仍限原有一级 ID 目录，避免递归混入其他游戏。

Runtime 将 own 与 linked 同次传入，显式关联不再因 broad 分支而跳过定向定位。发现过程不迁移、删除或重写清单、对象和存档。

- 元数据定位在工作线程运行，缓存最多 8 个查找键、30 秒；直接目录与历史内容仍按刷新读取。路径失效立即重新定位，手动“刷新”强制重新搜索，避免云盘总目录每 5 秒全量遍历。
- 区分 cloud reparse 与路径重定向：不一概拒绝所有 ReparsePoint，使用 LinkTarget 拒绝 junction/symlink；快路径与缓存都检查所选根至来源目录的完整祖先链，snapshots 和版本目录也检查。
- 单个来源枚举时消失/拒绝访问，局部处理，不丢弃其他来源已读版本。此枚举时序边界由控制流复核，未强制造成系统级枚举中断。
- 导入后和修改已导入档案的备份路径后，按实际版本数反馈；找不到版本会明确显示当前读取的备份目录和数据同步要求，不再无条件声称“历史已关联”。

## 验证

- Release 构建：0 警告、0 错误；SmokeTests 47/47 通过。
- 新回归包括：改存档路径、搬云盘根并改备份路径、N/D/嵌套/所选根本身为来源目录、显式来源隔离、未知游戏不误关联、缺对象等待及到齐、坏清单隔离、配置重载、只读与取消。
- 真实 Windows junction 回归覆盖来源目录、snapshots、版本目录、所选根、已缓存中间祖先替换，均不跟随到外部历史。
- DesktopTests 通过：真实导入确认窗口修改两种路径 → 保存确认结果 → 临时配置保存/重载 → 真实 Runtime.RefreshHistoryAsync → 找到 1 个来源版本；本机目标存档目录没有被创建。未调用正式 Startup，不读用户真实配置或注册自启。
- 原始复现结果在 temp/import-discovery-fix/baseline-results.json，桌面结果与截图在 temp/desktop-tests。均为本地忽略产物。

## 本地修复预览

包：temp/preview-import-history-fix-final/PathEcho-0.7.0-Lite.zip

大小：634679 字节。

SHA-256：A59DCD1A0D209DAD17F7F1B9216FD75B859942F6A129E1DCBB62BD3DBA0C9896

包内更新器 verify-only 已通过。本地预览沿用 0.7.0，仅用于验收，不是新的正式 Release；需要 .NET 8 Desktop Runtime x64。Start-Preview.cmd 仅展示隔离示例，复核真实目录前仍需用户确认新备份根和实际 snapshots 层级。没有改动用户真实存档、配置和备份。

## 仍需用户信息

用户改的是存档目录、备份目录还是两者，以及新备份目录到 snapshots 的两三层目录名。若数据没有复制/同步到新根，定位修复无法生成不存在的历史；应先确认数据位置，再决定是否需要更具体的迁移流程。

## 后续现场证据与日志缺口

用户随后提供真实编辑页：新备份根位于该机用户文档下的 BaiduSyncdisk，本机存档目录也已更换；关联提示可见。Debug 日志仅记录启用及新档案 fee1fc2d02724cd88ad77471fc819ce5 创建。原程序未记录正常零结果扫描，不能从这两行判断备份是否存在。当前本地新增结构化历史诊断：RootUnavailable、ProfileDirectoriesLocated、SnapshotDirectoriesLocated、ManifestRejected、ManifestReadFailed、ContentPending、Completed，以及Runtime实际请求根/当前ID/来源ID。诊断接收器失败不改变读取结果，相关行为回归通过。该追加尚未发布，也未要求用户再跑脚本；当前最小现场证据是新备份根的目录截图。

## 现场最终结论与复盘

用户最终确认：云盘漏同步 manifest.json。接收端提供的来源目录是标准 N 格式，ID 与导出一致，因此本文 N/D/嵌套定位修复不构成现场根因修复。该修复保留为独立本地候选，不据此发布。排查教训和快速定位设计见 [快速诊断方案](../design/rapid-diagnostics.md)，其中新增UI/默认摘要/一键导出尚未完整实施。
