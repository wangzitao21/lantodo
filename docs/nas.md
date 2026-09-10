# NAS 辅助同步

NAS 是保存完整版本历史的常在线节点，无待办编辑界面。Windows、Android 继续先写本机；NAS 接收后，其他设备可以稍后上线获取。多个 NAS 地位对等，互相交换版本，无主节点选举。离线修改、删除、并发冲突仍使用原有规则。

## Debian NAS 部署

需要安装 Docker Engine 和 Compose 插件。可使用完整源码仓库，也可解压随新版客户端提供的 `LanTodo-nas-source.zip`（仅含构建所需源码与文档，不含设备数据、密钥或二进制镜像）。在每台 NAS 的独立项目目录运行：

```bash
docker compose up -d --build
docker compose exec lantodo dotnet LanTodo.Nas.dll status
docker compose exec lantodo dotnet LanTodo.Nas.dll invite
```

最后一条命令返回五分钟有效、绑定一个设备的配对码。在 Windows「设备与同步 → NAS 辅助同步」或 Android「设置 → NAS 辅助同步」输入 NAS 地址（例如 `192.168.1.10:42851`）和配对码，点击「连接 NAS」。每添加一台设备重新生成一次配对码。不要把配对码放进公开日志或聊天频道。

仓库提供的是可本地构建的镜像，没有发布到公共镜像仓库。Dockerfile 使用 [.NET 官方 Linux 镜像](https://github.com/dotnet/dotnet-docker/blob/main/README.runtime.md)，支持为 amd64 / arm64 构建；Debian 宿主无需安装 .NET。默认镜像内 UID 为 `app` 用户（1654），使用 Compose 命名卷自动建立可写数据目录。

可在与 compose.yaml 同目录的 `.env` 设置：

```dotenv
LANTODO_NAME=家中 NAS
LANTODO_BIND_IP=192.168.1.10
LANTODO_PORT=42851
```

`LANTODO_BIND_IP` 是宿主机已有的 LAN/VPN 地址；默认 `0.0.0.0`。这里只开放 TCP 同步端口，使用双向 TLS 和配对证书指纹校验。管理命令通过容器内部、同一 OS 用户可访问的命名管道执行，没有网络管理接口。不要把此原生 TLS 端口当 HTTP 服务配置普通 HTTP 反向代理。

容器不依赖 UDP 发现，也不需要 host 网络。每台客户端主动连接 NAS，因此 NAS 不需要反向访问手机地址。IPv4、IPv6（如 `[fd00::10]:42851`）和域名均可；域名在新连接时重新解析。

## 两台 NAS 不互联：手机分别同步

手机在家中 Wi-Fi 下绑定家中 NAS，例如 `192.168.1.10:42851`；到单位后再绑定单位 NAS，例如 `192.168.10.20:42851`。每台 NAS 分别执行一次 `invite` 获取本次配对码，手机保留两个节点，不必手动切换。`status` 仅用于查看状态，不是每次配对的必需步骤。

两台 NAS 不需要 VPN 或彼此配对。手机与当前可达的节点同步，不可达节点独立等待重连；手机往返两处时传递已收到的修改。两边同步的是同一份清单与历史，不是两个隔离资料库。两地内容会有延迟，必须等手机在对应网络中运行并完成同步。

## 可选：两台 NAS 直接互联

如果希望不等手机往返就交换修改，两处网络需要通过已有 VPN、路由或其他受控通道互通。广播不会跨网建立连接。无论选择哪种方式，两个 NAS 都必须使用不同数据卷和独立生成的身份，不能克隆正在使用的数据库作为第二节点。

1. 在家中 NAS 执行 `invite`，取得新的配对码。
2. 在办公室 NAS 执行以下命令，地址使用办公室可达的家中 NAS 地址：

```bash
docker compose exec -it lantodo dotnet LanTodo.Nas.dll pair 10.20.0.10:42851
```

3. 粘贴配对码并回车。配对码从标准输入读取，不写入命令参数。
4. 用 `status` 查看 `nodes` 中该节点的同步状态。

只配置办公室 → 家中的主动连接，就可以双向同步。两台 NAS 都接收和提供数据，办公室会等待家中的变化通知。无需在两边重复配对；若希望两边都能主动重连，可在另一边对已配对的设备设置地址：

```bash
docker compose exec lantodo dotnet LanTodo.Nas.dll address 对方设备ID 10.30.0.10:42851
```

每台客户端可以配置多个 NAS；每个 NAS 的连接和失败重试独立，离线节点不会阻塞可用节点。不要求所有手机、电脑彼此配对。默认最多配置 16 个固定节点、接收 32 个并发连接，适用于个人设备规模。

## 客户端行为与状态

- 默认保留原局域网直连；添加 NAS 后启用 NAS 辅助同步，可选择是否同时保留局域网发现与自动直连。
- 关闭「启用 NAS 自动同步」会暂停固定节点连接，保留地址、配对及全部本机数据。重新启用后自动核对补齐。
- 地址变化可点「更新地址」；证书身份变化需要重新配对，不能仅修改 IP 绕过身份检查。
- 「移除 NAS 地址」只移除固定路线。取消授权请在「我的设备」中取消配对；NAS 可使用 `revoke`。
- 「已同步」和最近确认时间只对应该节点，不能证明其他离线设备已经收到。Android 节点列表提供「刷新节点状态」。
- 关闭局域网发现不撤销已授权设备；已配对设备仍可通过已知地址主动连接本机。真正禁止访问需要撤销配对。

每次保存成功后立即尝试同步。失败保留本地版本，按 5 秒至 160 秒退避；本地修改、手动同步和网络恢复可唤醒重试。运行中的客户端用最长 10 秒的 TLS 长轮询等待远端变化，服务端有变化时提前响应，不需要等待满 10 秒。本地修改用事件唤醒。空闲等待不交换版本清单；定期仍按一或两小时完整核对。

应用关闭、手机被系统停止、网络不可达时无法接收实时更新，恢复运行后补同步。修改必须先上传成功，才能由 NAS 在原设备离线后继续转发。Android 后台行为仍受系统电池策略限制。

## 运维命令

所有命令都在已经运行的服务上执行，不启动第二份数据库运行时：

```bash
docker compose exec lantodo dotnet LanTodo.Nas.dll status
docker compose exec lantodo dotnet LanTodo.Nas.dll invite
docker compose exec lantodo dotnet LanTodo.Nas.dll cancel-invite
docker compose exec lantodo dotnet LanTodo.Nas.dll backup
docker compose exec lantodo dotnet LanTodo.Nas.dll remove-address 对方设备ID
docker compose exec lantodo dotnet LanTodo.Nas.dll revoke 对方设备ID
docker compose logs --tail 100 lantodo
```

`status` 返回本机身份、配对设备、版本总数、固定节点状态和最近自动备份错误，不输出待办内容或私钥。`revoke` 取消本机对该设备的信任并移除固定地址；已经传出的历史无法远程收回。配对状态不沿 NAS 自动传播：应在其他曾配对的节点分别撤销。

容器配置使用 `LANTODO_DATA`、`LANTODO_BACKUPS`、`LANTODO_NAME`、`LANTODO_PORT`。设备名称仅在首次创建身份时使用，后续重启保留原名称。Compose 的 `LANTODO_PORT` 默认只改变宿主发布端口，容器内始终为 42851。

## 数据与备份

`/data` 保存 SQLite、设备身份和配置；Linux 另有 `LanTodo.sqlite.lock` 用于阻止多个进程打开同一资料库，不能删除正在使用的锁文件。`/backups` 保存数据专用 ZIP。更新容器时保留这两个卷。不要执行带 `--volumes` 的卸载命令，除非确实要删除资料。

服务每分钟检查一次：有历史时，生成 UTC 当日第一份 `daily-YYYYMMDD.lantodo.zip`，当天不覆盖；`backup` 随时生成独立手工快照。快照包含删除与冲突，不包含私钥和配对；不自动清理，请监控空间并自行制定保留策略。使用客户端「从备份合并恢复」恢复历史，重新配对空 NAS 后上传即可。保留原 NAS 身份需要另行在停服后备份整个 `/data` 卷。

命名卷默认仍位于同一 NAS。为了应对 NAS 磁盘故障，应把备份导出到另一存储位置，例如先执行 `backup`，再复制导出目录：

```bash
docker compose cp lantodo:/backups ./exported-backups
```

两台 NAS 的实时同步不能替代历史快照。ZIP 和 NAS 数据库未做静态加密；NAS 管理员可以读取完整历史。

## 验证与范围

```powershell
./scripts/test.ps1
./scripts/test-nas.ps1
./scripts/test-windows.ps1
```

Linux 对应运行 `dotnet run --project tests/LanTodo.Tests -c Release` 和 `dotnet run --project tests/LanTodo.Nas.Tests -c Release`。CI 配置增加 Linux 回归与 Docker 构建/启动验证；本地无 Docker 环境时，这些配置不能视为已通过容器实测。

覆盖重点：客户端不同时在线、双 NAS 中转、重复版本去重、删除与离线编辑冲突、撤销授权、闲置不反复交换清单、不可达节点隔离、暂停后补齐、服务被终止后恢复身份与历史、备份恢复、同资料目录排他访问。

兼容性：原客户端仍可与新版客户端局域网同步；NAS 地址入口需要新版客户端。协议 v1 新增可选 `Generation` 字段及授权后的 `watch` 请求，新客户端遇到没有变化标记的旧固定节点时回退到最多 30 秒一次核对。数据 schema、Revision 和 ZIP 格式保持不变。版本清单仍是全量分页，暂不压缩历史或删除墓碑，也不提供公网账号系统、待办 Web 编辑或手机系统级推送。
