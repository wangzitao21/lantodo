**LanTodo 性能与后续完善评估 · 2026-09-12**

后续实施记录：用户确认优先优化打开和终端操作，暂不改造历史积累机制。本报告保留 build 18 的评估现场；已实施的 build 19 改动、对照测量及回归结果见 [终端响应与启动优化](optimization-build19.md)。

结论：存在明确的进一步优化空间。当前本地优先、SQLite 事务、不可变版本与原生界面的基础值得继续使用。下一轮应优先解决界面线程阻塞、Android 卡片复用和启动阶段划分，再逐步改造历史读取与同步协议。性能目标应覆盖输入、保存、显示、对端提交和对端显示整个过程。

评估对象是当前工作目录中的 build 18 源码，包括已有未提交修改。本轮新增了此报告与 `.tools/performance-review/` 中的隔离基准程序，没有修改应用业务代码。检查覆盖 Core、Android、Windows 以及 NAS 的相关同步/备份路径。以下明确区分本次测量、此前测试日志和静态代码分析。

**已有改进应保留**

- 本地操作先提交 SQLite，网络不在本地保存的必经路径中；草稿与发送提交已有事务保护。
- Android 提前显示输入区，初始化期间的文字通过独立收件箱持久保存；这对快速记录有实际价值。
- 两端列表已有可见项虚拟化；Windows 还使用了回收容器和未变化行保留。
- 当前头版本、单条历史、附件引用已有内存索引；普通保存没有重新遍历全部历史。
- 空闲 watch 复用连接，自动重连采用地址竞速，文字优先于附件继续传输。
- Android Release 已启用 partial trimming 和 profiled AOT；重复建议“启用 AOT”不会解决剩余问题。

**启动速度的实际含义**

此前 build 18 的三次 Android 模拟器冷启动日志如下。`ComposerReadyMs` 和 `DataReadyMs` 均从 `MainActivity.OnCreate` 内的 Stopwatch 开始计时；系统 Fully drawn 的计时起点不同，不能直接把这两类数值相减比较。

| 原始日志 | 系统 Fully drawn | ComposerReadyMs | DataReadyMs | 输入区可用后至 DataReady 的差值 |
| --- | ---: | ---: | ---: | ---: |
| cold-1.log | 906ms | 649ms | 1,985ms | 1,336ms |
| cold-2.log | 871ms | 618ms | 1,990ms | 1,372ms |
| cold-3.log | 893ms | 642ms | 1,960ms | 1,318ms |

因此，约 0.89 秒代表首页输入区可以使用；此后仍有约 1.3 秒才记录 DataReady。DataReady 发出前只是排队调用 `StoreChanged()`，列表还要经过 80ms 合并延迟及实际绘制，所以它也不是历史列表已呈现的精确时间。这个改善是真实的快速记录体验改善，但不同阶段不能混用为“完整冷启动提速倍数”。此前数据集只有约 26–29 条记录，这段剩余时间需要分阶段测量，不能直接归因于历史数量。

依据：[启动日志](D:/Archives/code/lantodo/.tools/test-results/build18/cold-1.log:7)、[另一次日志](D:/Archives/code/lantodo/.tools/test-results/build18/cold-2.log:7)、[第三次日志](D:/Archives/code/lantodo/.tools/test-results/build18/cold-3.log:7)、[提前报告 Fully drawn](D:/Archives/code/lantodo/src/LanTodo.Android/MainActivity.cs:55)、[DataReady 日志位置](D:/Archives/code/lantodo/src/LanTodo.Android/MainActivity.Startup.cs:54)。

建议保留单独的输入区可用指标，把完整首页可用指标放在首批历史行实际绘制之后。Android 官方也将首帧 TTID 与异步主要内容呈现后的 TTFD 分开，并提醒列表加载未纳入时会低估完整启动时间。[Android 启动计时说明](https://developer.android.com/topic/performance/vitals/launch-time)

**本次隔离基准**

运行环境：Windows 10.0.19045、.NET 10.0.12、Release，运行时报告 32 个逻辑处理器。只生成合成数据，没有使用实际清单。先预热 JIT、JSON 与 SQLite；数据库重开测量 5 次，保存等操作测量 20 次，以下为中位数。操作系统文件缓存未清空，重开期间统计当前线程累计托管分配。测试数据为短标题、约 143 字符备注、无附件、无冲突的线性版本链。

| 当前清单条数 | 初始历史版本数 | 数据层重开 | 重开累计托管分配 | 单次保存 | 修改后列表重建 | 核对集合构建与比对 CPU 部分 |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1,000 | 1,000 | 9.74ms | 4.97MiB | 5.33ms | 0.77ms | 0.11ms |
| 1,000 | 10,000 | 171.14ms | 54.31MiB | 6.21ms | 1.82ms | 2.22ms |
| 1,000 | 100,000 | 1,117.50ms | 531.92MiB | 5.40ms | 0.65ms | 54.86ms |
| 10,000 | 10,000 | 86.01ms | 48.99MiB | 6.45ms | 5.43ms | 1.58ms |

“累计托管分配”包含期间分配后可回收的对象，**不是常驻内存，也不是内存峰值**。数据层重开仅包含 `TodoStore` 初始化，不包含 Android 进程、UI、身份/空间初始化、网络或完整 AppRuntime。不同版本链结构和运行波动会影响结果，不能把表格当作所有数据集的精确预测。

这里的关键证据是：历史增长主要影响加载与核对；已有索引让普通保存保持了较稳定的耗时。10,000 条列表在本机重建约 5.4ms，本身还不构成严重瓶颈，但这不包含界面布局、卡片创建、图片和 Windows 的状态字符串计算。

另做了两端已拥有同一历史时的本机 TLS 回环测量。关闭 UDP 发现，用测试身份的直接信任隔离空间握手和自动同步调度；运行当前 `SyncAsync` 的版本交换代码，包含新 TLS 连接。每种情况预热一轮，正式测量三次。发送前的本机保存耗时未计入。

| 两端已有历史数 | 无变化核对 | 增加一条后的完整同步 | 增加一条至对端提交 |
| ---: | ---: | ---: | ---: |
| 1,020 | 3.74ms | 10.56ms | 10.08ms |
| 10,020 | 22.51ms | 30.88ms | 28.02ms |
| 100,020 | 139.42ms | 148.98ms | 122.08ms |

这证明即使没有外部网络因素，历史量也会增加核对成本。同时，小数据下核心同步本身很快。它不是 Android 真机结果，也没有包含设备发现、空间成员认证、远端界面刷新、附件、休眠或跨端网络延迟，不能承诺用户端同样快。

原始数据：[数据层结果 JSON](D:/Archives/code/lantodo/.tools/performance-review/runs/20260912-050058/results.json)、[回环结果 JSON](D:/Archives/code/lantodo/.tools/performance-review/runs/20260912-050236/sync-results.json)。复现程序：[Program.cs](D:/Archives/code/lantodo/.tools/performance-review/Program.cs)、[SyncProbe.cs](D:/Archives/code/lantodo/.tools/performance-review/SyncProbe.cs)。这些辅助文件位于项目原有忽略目录，不会进入普通源码提交。

**优先优化的具体位置**

| 顺序 | 已确认的实现 | 对体验的影响 | 建议 |
| --- | --- | --- | --- |
| 第一轮 | Android 发送、勾选、星标、延迟草稿保存直接在 UI 线程执行同步存储 | 事务刷盘或等待后台锁时，输入与动画一起等待 | 串行后台写入队列；UI 只处理快照和结果；区分提交中、已保存与失败 |
| 第一轮 | Android `GetView` 忽略 `convertView`，始终新建整张卡片 | 虚拟化限制了可见数量，但滚动与刷新仍反复创建控件、事件和图片任务 | 先实现安全的 ViewHolder 复用；再按需要引入 RecyclerView、稳定 ID 与局部更新 |
| 第一轮 | 所有 Android 数据变化统一延迟 80ms，再重新筛选/刷新列表 | 本机快速操作也额外等待；一条更新会重建可见卡片 | 本机成功保存优先更新受影响行；批量远端变化按帧或短窗口合并 |
| 第二轮 | 启动读取、反序列化、哈希检查并重建全部历史索引 | 历史越多，数据准备越慢，分配量越高；网络也等待 AppRuntime 完成 | 持久化当前状态及头索引、历史按需读取、受校验检查点和增量恢复 |
| 第二轮 | 每轮实际同步先交换全部历史 ID，再推送本机变化 | 一条新文字仍受整个资料库大小影响 | 每个对端的持久化增量游标、变更日志、周期性全量修复 |
| 第二轮 | watch 连接与数据同步连接分开；数据轮完成后关闭连接 | 活跃修改会重新承担 TLS 与空间握手成本 | 同一认证连接支持连续数据轮与空闲通知，保留撤销检查和老版本回退 |
| 第一至二轮 | 附件收尾哈希、部分刷盘、备份压缩发生在存储全局锁内 | 即使后台执行，前台保存/查询仍可能等待同一把锁 | 慢 I/O 与哈希在锁外处理；短锁检查引用与发布结果；备份采用一致性快照和文件保护 |

Android 主线程路径：[发送](D:/Archives/code/lantodo/src/LanTodo.Android/MainActivity.cs:348)、[勾选](D:/Archives/code/lantodo/src/LanTodo.Android/MainActivity.cs:413)、[星标](D:/Archives/code/lantodo/src/LanTodo.Android/MainActivity.cs:426)、[草稿定时器](D:/Archives/code/lantodo/src/LanTodo.Android/MainActivity.Drafts.cs:12)。SQLite 的事务提交同步执行，配置为 DELETE/FULL：[SqliteProfile.cs](D:/Archives/code/lantodo/src/LanTodo.Core/SqliteProfile.cs:44)。Windows 的普通编辑操作也同步调用 Save：[MainWindow.xaml.cs](D:/Archives/code/lantodo/src/LanTodo.Windows/MainWindow.xaml.cs:166)。

把方法包装成异步还不够：必须避免 UI 随后的 `List()`、附件状态查询等继续等待同一全局锁。写入队列要保证有序、去重、有限排队与生命周期结束时的持久化，并维持 `expectedHeads` 检查和“提交后才确认保存”的语义。正在提交的输入快照与后续新输入也必须分开，不能在成功回调中清空用户刚输入的新内容。

Android 列表依据：[FeedAdapter](D:/Archives/code/lantodo/src/LanTodo.Android/MainActivity.cs:431)、[卡片工厂](D:/Archives/code/lantodo/src/LanTodo.Android/MainActivity.cs:403)、[80ms 刷新](D:/Archives/code/lantodo/src/LanTodo.Android/MainActivity.cs:144)。复用时要完整重置颜色、删除手势、完成状态、旧事件绑定及图片目标，避免错行操作。针对小变化的最小更新与官方建议一致；官方列出了全量刷新导致重新绑定和布局的成本。[Android 滚动列表性能](https://developer.android.com/topic/performance/vitals/render)

数据层依据：[启动全历史重建](D:/Archives/code/lantodo/src/LanTodo.Core/TodoStore.cs:49)、[读取所有 payload 并校验](D:/Archives/code/lantodo/src/LanTodo.Core/SqliteProfile.cs:60)、[列表缓存失效](D:/Archives/code/lantodo/src/LanTodo.Core/TodoStore.cs:259)。当前 `List()` 缓存命中也复制整个数组；失效后重建所有当前头并排序。后续可用按 ID 更新的读模型、分类/排序索引和版本化只读快照，先消除无变化时的工作，再根据真实规模决定分页。

快速启动改造会涉及数据库接口与迁移。需要定义快照的事务一致性、损坏检测与回退路径，验证导入和恢复时的完整因果图，明确哪些历史已验证、哪些尚未验证。不能只删除校验或把未经确认的数据当作完整有效状态。Android 小数据下剩余约 1.3 秒还需要细分 `StoreOpen`、`IdentityLoad`、`SpaceLoad`、`DraftRestore`、`FirstRowsDrawn`；当前证据不足以判断各阶段占比。Windows 也在主窗口显示前同步构造完整 AppRuntime：[App.xaml.cs](D:/Archives/code/lantodo/src/LanTodo.Windows/App.xaml.cs:42)。

同步依据：[清单及发送顺序](D:/Archives/code/lantodo/src/LanTodo.Core/PeerNode.cs:398)、[每个连接重建历史快照索引](D:/Archives/code/lantodo/src/LanTodo.Core/PeerNode.cs:542)、[数据连接生命周期](D:/Archives/code/lantodo/src/LanTodo.Core/PeerNode.Space.cs:168)、[watch 新建连接](D:/Archives/code/lantodo/src/LanTodo.Core/PeerNode.Replicas.cs:161)。

按现有规则，100,000 个版本至少需要 391 页 inventory；仅 64 字符哈希及 JSON 字符串分隔约为 6.7MB，尚未计算包字段与 TLS。这个数字是代码推算，不是抓包结果。缺失 10,000 个版本时，当前每批 4 条需要约 2,500 次批次交换和接收侧事务；批量规模应按序列化字节数和处理时间预算调整，并通过协议能力协商兼容旧端的最多 8 条限制及 2MiB 帧限制。

增量同步游标必须是每个发送端的本地追加日志位置，包含从其他设备导入的版本；对端游标只在事务成功提交后推进。还需处理重启、恢复造成的日志代际变化、缺失父版本、冲突分支、撤销成员与全量回退。不能用墙上时钟或单一“最后修改时间”替代因果版本，不能直接丢弃历史或墓碑来换速度。

附件与全局锁依据：[ReceiveAttachment 持有 Store 锁](D:/Archives/code/lantodo/src/LanTodo.Core/TodoStore.cs:39)、[分块刷盘及完整文件哈希](D:/Archives/code/lantodo/src/LanTodo.Core/AttachmentStore.cs:314)、[备份整体持锁](D:/Archives/code/lantodo/src/LanTodo.Core/TodoStore.cs:289)。这类等待是终端磁盘和锁的成本，后台线程本身并不能隔离它。

**其他值得完善，但应排在上述工作之后的事项**

- Android 切换分类或日期调用 `Home()` 重建整个首页，包括输入区。保留页面与输入控件，只改变列表查询和选中态，可减少布局与 JNI 工作，也更容易保留焦点。[Home](D:/Archives/code/lantodo/src/LanTodo.Android/MainActivity.cs:278)
- 缩略图缓存达到 64 个时整体清空；排队解码任务只有 Activity 销毁检查，没有滚出屏幕后取消，也没有按哈希合并所有进行中的请求。可采用有字节预算的 LRU、目标绑定检查、可见项优先与取消，必要时持久化缩略图。[LoadThumbnail](D:/Archives/code/lantodo/src/LanTodo.Android/MainActivity.Attachments.cs:129)
- Windows 每次检查是否需要刷新，都先拼接所有头版本/设备名和附件可用性；判断“没有变化”之前仍有全表和文件状态查询。改为独立内容、成员名、附件状态版本号，缓存展示名称。[Refresh](D:/Archives/code/lantodo/src/LanTodo.Windows/MainWindow.xaml.cs:62)
- Windows `UpdateRows` 在每个位置不匹配时线性查找，批量排序/筛选切换的最坏情况可退化到平方级。改为明确的差量算法，并对大量变动采用受控批处理；仅添加字典不能消除 ObservableCollection 移动和通知的全部成本。[MessageList](D:/Archives/code/lantodo/src/LanTodo.Windows/MessageList.cs:33)
- `MessageQuery.Matches()` 对每一行重新拆分同一搜索词。先拆一次并生成查询对象是低风险简化；只有规模确有需要时再考虑全文索引，同时明确中文匹配语义。[MessageQuery](D:/Archives/code/lantodo/src/LanTodo.Core/LocalDraftStore.cs:64)
- 附件同步每轮扫描当前附件并逐个询问状态；首次附件请求还从全部历史构造 `attachmentIndex`，但 `HandleAttachment` 当前不使用这个参数，改用现有引用索引检查。可先删除确认无用的构建，再增加附件清单版本和变化队列。[多余历史扫描](D:/Archives/code/lantodo/src/LanTodo.Core/PeerNode.cs:580)、[实际附件权限检查](D:/Archives/code/lantodo/src/LanTodo.Core/PeerNode.Attachments.cs:14)
- 当前所有草稿集中存于一个 metadata JSON；单个草稿变化会复制并序列化整个字典。小规模时问题有限，若长期积累编辑草稿，可按键存储并维护待发附件引用索引。[LocalDraftStore](D:/Archives/code/lantodo/src/LanTodo.Core/LocalDraftStore.cs:42)
- 同步状态和发送唤醒锁释放逻辑部分依赖中文字符串。改为结构化状态与版本确认字段，文字由 UI 映射；分开表示文字已到达、附件待完成、设备离线，既便于测量也便于可靠反馈。[FinishConfirmedSend](D:/Archives/code/lantodo/src/LanTodo.Android/AndroidSession.cs:53)
- JSON 源生成和更精准的 trimming 可以作为后续实验，尤其考虑当前 Core/Android 整个程序集均被保留。但版本 ID 依赖序列化字节，必须先建立历史记录、空值省略、属性顺序等兼容样例，验证哈希不变。不要承诺所有反序列化路径都显著加速。[构建配置](D:/Archives/code/lantodo/src/LanTodo.Android/LanTodo.Android.csproj:25)、[Microsoft 源生成说明](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/reflection-vs-source-generation)

WAL 可以测，但当前单连接与粗粒度锁仍限制并发，只改 PRAGMA 不是完整方案。若使用 WAL，要设计 checkpoint、快照备份和便携目录复制；若考虑 `synchronous=NORMAL`，还会改变掉电后已提交事务的耐久性。优先通过减少重复事务和主线程等待提速，保留现有可靠性承诺。[SQLite WAL 性能与耐久性说明](https://sqlite.org/wal.html)

**建议的实施顺序和验收方法**

第一轮先补测量，再做后台串行写入、Android ViewHolder 复用、本机操作即时局部更新、Windows 无变化快速跳过、无用附件索引清除。这轮大部分不需要改版本格式或同步协议，最直接改善日常体感。

第二轮针对较大改造分别设计、单独验收：受校验的当前状态快照与按需历史；全局锁外的附件/备份工作；支持连续数据轮的连接；增量同步游标与按字节自适应批次。数据库迁移与协议变更分别保留旧版本兼容和回退，不把几项变化混成一个难以定位的发布。

每次性能测量都固定设备、Release 包、数据档位和前后台状态。真机建议至少覆盖实际使用的 Android 手机，另加一台较慢设备；数据档位覆盖 1,000 / 10,000 / 100,000 历史版本、10,000 当前项、图片与大附件、离线积累、冲突、同步中输入和备份中输入。高刷新率和快速连续操作要单独测。

| 指标 | 建议初始工程目标，尚未实测达到 |
| --- | --- |
| 点击、勾选、发送的可见反馈 | p95 不超过 50ms；提交中与持久保存分开表示 |
| 当前资料首屏完整可用 | 指定中端手机、1,000 当前项与 10,000 历史下，冷启动 p95 争取 1 秒内 |
| 已在进程中的前台恢复 | p95 争取 150ms 内恢复可操作列表 |
| 同步接收提交至可见更新 | p95 不超过 50ms，单独于传输延迟统计 |
| 受控局域网、双方前台且已有认证连接的一条文字 | 保存后到对端显示 p95 争取 300ms 内，并分别记录排队、编码、提交、UI 阶段 |
| 滚动流畅度 | 60Hz 以约 16.7ms、120Hz 以约 8.3ms 为每帧总预算，记录超预算帧比例和 p95/p99 |

这些是设计目标，不是本次性能承诺。需记录足够多次真机样本后评估分位数；本次 3–5 次启动/同步测量只用中位数展示，没有声称统计充分的 p95。Android 的帧预算和诊断方法可参考官方说明。[Android 渲染性能](https://developer.android.com/topic/performance/vitals/render)

**本次验证边界**

- Core 完整回归 79/79 通过，含冲突、离线接力、授权撤销、附件、草稿与重连。首次受限运行的证书导入失败；允许测试访问 Windows 用户密钥存储后，同一代码重跑全部通过。[测试日志](D:/Archives/code/lantodo/.tools/test-results/performance-review-core-tests.log)
- 新增隔离基准项目 Release 构建成功，0 警告、0 错误；数据量断言、两端同步数量与对端提交事件检查通过。
- 此前 Android 原始日志只作历史证据。本次没有重新进行 Android 真机或模拟器 UI 测量，也未重新跑完整 Windows 布局/NAS 独立进程测试；本轮没有改业务实现。
- 没有把台式机时间、累计分配量、核心协议回环结果或既有可见容器数量，等同于手机完整应用的启动时间、常驻内存、端到端显示延迟或帧率。
