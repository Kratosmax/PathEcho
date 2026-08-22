# PathEcho 0.2.2

PathEcho 0.2.2 修复 Lite Setup 对已安装 .NET 8 Desktop Runtime x64 的误判。

- 修复安装器把 .NET 运行时版本值错误当成注册表子项的问题。
- 同时检查 32 位与 64 位注册表视图，兼容 .NET 官方安装器的 x64 运行时登记位置。
- 已安装符合要求运行时的用户现在可以直接使用 Lite Setup，不再被错误拦截。

Full 包自带 .NET 8 运行时；Lite 包需要预先安装 .NET 8 Desktop Runtime x64。用户配置和备份数据不会因覆盖安装而删除。
