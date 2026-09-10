# 数据库接口 v1

两端共用 `LanTodo.Core`，SQLite 驱动只出现在 `SqliteProfile` 中。界面和同步不要执行 SQL。以下约定作为后续兼容边界；内部表和索引仍可按版本迁移，不能靠直接改表破坏已有资料。

## 业务接口 `ITodoStore`

| 接口 | 约定 |
| --- | --- |
| `List()` | 返回未删除条目及未解决冲突的快照；已完成仍属于条目 |
| `Export()` / `History(todoId)` | 返回完整不可变版本 / 指定待办历史；包含删除版本 |
| `Save(actor, deviceName, data, todoId?, expectedHeads?)` | 新建或以指定头版本为父创建版本；头已变化抛 `StaleEditException`，不覆盖新数据 |
| `Import(revisions)` | 先校验哈希及完整父图，去重，再原子提交一个批次；返回新导入数量 |
| `DeleteCompleted(actor, name, confirmed)` | 跳过冲突、未完成、已删及确认后变化的记录，其余在一个事务内生成删除版本；返回删除 / 跳过数 |
| `Changed` | 成功提交并更新内存后触发一次；失败或没有新版本不触发 |
| `Backup(path)` / `Restore(path)` | 数据专用 ZIP v1，导出完整版本清单；恢复校验后合并，不覆盖新修改、不复制设备身份 |

`TodoStore` 负责线程内串行写入和内存头索引，`PeerNode` 只依赖上述接口。一个同步会话含多个原子批次，允许中断后重新同步。返回成功表示本机提交完成，不表示所有设备在线并已收到。

## 持久化接口 `IProfileDatabase`

| 接口 | 约定 |
| --- | --- |
| `FilePath` | 当前 SQLite 绝对路径；迁移成功后更新 |
| `ReadRevisions()` | 返回已持久化版本，校验行标识及内容哈希；父图由上层统一校验 |
| `Append(revisions)` | 写入已由业务层校验的新版本；整批提交或整批回滚 |
| `ReadMetadata(key)` | 返回本机元数据字节，不存在返回 null |
| `WriteMetadata(key,value)` | 原子插入或替换一个本机元数据值 |
| `Dispose()` | 关闭连接并释放独占运行时租约；设备身份对象须先释放 |

不向外暴露连接和 SQL 事务。身份对象不拥有数据库，`AppRuntime` 统一管理生命周期。界面不缓存路径常量，显示位置时读取 `Store.Database.FilePath`。`ProfileMigration.MoveTo` 是本机文件生命周期操作，必须先暂停网络及编辑。

## SQLite 结构 v1

固定文件名 `LanTodo.sqlite`；`PRAGMA application_id=1279349828`，`PRAGMA user_version=1`。

```sql
CREATE TABLE revisions (
  id TEXT PRIMARY KEY,
  todo_id TEXT NOT NULL,
  payload BLOB NOT NULL
);
CREATE INDEX revisions_todo ON revisions(todo_id);
CREATE TABLE metadata (
  key TEXT PRIMARY KEY,
  value BLOB NOT NULL
);
```

`payload` 是现有 Revision v1 的 UTF-8 JSON 字节，保留内容哈希和父引用。使用 SQLite 存储及事务，同时保持原网络协议与 ZIP 兼容。头索引由版本图重建，没有额外 sidecar 索引文件。

元数据键：`identity.pfx`（本机私钥证书）、`name.json`（名称）、`trusted.json`（长期信任列表）、`sync-settings.json`（1 或 2 小时）。这些名字是数据库内部键，不再对应散落文件。`legacy-backup/<旧文件名>` 保存升级前已有自动备份的原始字节，保留取回能力，不参与设备同步。Android 的通知 / 电池授权及草稿偏好继续由系统私有设置管理。

## 兼容和迁移规则

1. 业务接口 v1、Revision v1、同步协议 v1、备份 v1、SQLite schema 版本独立演进。局域网只交换版本，不传数据库页、私钥或可信设备表。
2. 新内部结构必须提供从旧 `user_version` 到新版的事务迁移；验证成功才提交版本号。旧程序遇到未来版本必须拒绝打开，禁止重置为空库。
3. JSON 升级先在临时数据库导入完整历史和本机资料，重新打开校验后才发布最终文件。仅清理已成功导入的旧文件，失败保留原数据。
4. 移动目录用 SQLite 备份 API 复制一致快照，验证历史及元数据，持久化位置后删除原库。迁移失败恢复原库可写状态和位置指引；任何情况下不覆盖目标已有资料。
5. `journal_mode=DELETE`、`synchronous=FULL`、`Pooling=false`。正常空闲只留一个数据库；写入或故障恢复可能短暂留下日志，不能为了目录外观关闭日志。

依赖固定为 Microsoft.Data.Sqlite 10.0.0、SQLitePCLRaw.bundle_e_sqlite3 3.0.5（SQLite 原生包 3.53.4）。后续安全补丁更新不要求业务接口变化。参考 [Microsoft SQLite 原生库配置](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)、[SQLite 事务临时文件](https://www.sqlite.org/tempfiles.html)。
