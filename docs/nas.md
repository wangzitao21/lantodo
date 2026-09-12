# NAS 同步成员

NAS 与手机、电脑使用同一个空间。它保留完整清单历史和可用附件，适合作为常在线副本；无需把它设为中心节点。移除 NAS 后，其他可连接的成员仍能继续同步。

## Docker 部署

在源码目录或解压的 `LanTodo-v1.0.1-NAS.zip` 目录中执行：

```sh
docker compose up -d --build
```

管理网页默认 `http://NAS地址:42000`，同步端口默认 TCP `42851`。数据与备份分别保存在 `lantodo-data`、`lantodo-backups` 命名卷。升级时重新构建并保留这两个卷，不要运行删除数据卷的命令。

首次启动就需要管理令牌；未配置时会在数据目录生成 `admin-token`，不会提供匿名管理入口。Docker 管理员可执行 `docker compose exec lantodo cat /data/admin-token` 在本机读取，然后登录网页更换令牌（至少 24 字符）。浏览器会保留登录会话，有效期 14 天。已有数据卷的令牌与会话密钥继续有效。也可在 `.env` 设置 `LANTODO_ADMIN_TOKEN`，环境变量优先。管理令牌仅用于控制台登录，与空间邀请授权码用途不同。

## 加入与邀请

如果手机或电脑已经建立空间，在该设备生成授权码，在 NAS 网页“加入已有空间”粘贴并确认加入网络空间并共享全部内容即可。NAS 加入一次后，其他空间成员会自动认识它。

也可以先在 NAS 创建空间，再通过网页“添加设备”生成授权码和二维码。手机扫描，Windows 可粘贴授权码或读取二维码图片。之后其他成员也能发起邀请，无需总是从 NAS 邀请。

Docker 桥接网络通常无法把局域网广播直接送入容器。网页“添加设备”中的“本机对外地址”会建议使用当前浏览器访问的主机名与同步端口。请确认这里填写的是其他设备可以访问的 NAS 地址。若端口映射改为 `42860:42851`，应填写 `NAS地址:42860`。浏览器的 `42000` 是管理网页端口，不能当作同步端口。

地址随邀请及成员信息传递；终端在同一网络仍会自动寻找其他成员。跨网络需要可达地址或已有 VPN；本版本不提供公共中继、打洞服务或自动公网映射。

## 页面操作

- 空间成员：显示昵称与连接状态，可改所有成员昵称，或将设备从整个空间移除。
- 添加设备：生成五分钟、单设备使用的授权码与二维码，可作废当前码。
- 加入已有空间：确认共享本机清单、附件与历史后加入，原数据按版本合并。
- 高级连接与邀请记录：查看连接诊断，设置某成员的备用地址，清理邀请历史，退出空间；顶部支持直接新建、加入和切换空间。
- 待确认内容：保留某一冲突版本，或删除此条，决定与完整历史继续同步。
- 备份全部内容：保存到备份卷。备份失败会在状态中显示。

一个成员被移除后，移除决定自动传给其他成员；无需在每台终端逐个取消。离线设备下次连接后收到通知，本机已收到的数据仍保留。删除邀请历史不影响成员资格；记录不保存授权码秘密。

## 命令行

管理命令在正在运行的容器内执行，使用仅限同一系统用户的本地控制通道：

```sh
docker compose exec lantodo dotnet LanTodo.Nas.dll status
docker compose exec lantodo dotnet LanTodo.Nas.dll public-address nas.home:42851
docker compose exec lantodo dotnet LanTodo.Nas.dll invite
docker compose exec lantodo dotnet LanTodo.Nas.dll cancel-invite
docker compose exec -it lantodo dotnet LanTodo.Nas.dll pair
```

`pair` 从标准输入读取授权码，不将秘密放进命令参数；可选附加邀请设备地址 `pair nas.home:42851`。加入仅创建独立连接配置，所有网络使用同一份清单与附件。

```sh
docker compose exec lantodo dotnet LanTodo.Nas.dll address DEVICE_ID nas.home:42851
docker compose exec lantodo dotnet LanTodo.Nas.dll remove-address DEVICE_ID
docker compose exec lantodo dotnet LanTodo.Nas.dll revoke DEVICE_ID
docker compose exec lantodo dotnet LanTodo.Nas.dll backup
```

旧版配对资料需先执行 `upgrade-space`（明确升级并替换旧授权）。`leave-space` 退出当前空间，`new-space` 建立新的网络连接空间，无需先退出。`status` 包含空间、成员、邀请历史、同步与冲突状态；请避免公开其中的私人内容。CLI `delete-device` 在新空间中等同移除成员，不清除内部移除记录。

## 数据与备份

`/data/LanTodo.sqlite` 存储身份、成员日志、清单历史与设置，`/data/attachments/` 存放原件。`/data/spaces/` 只保存各组连接的成员与路由配置。后台为全部内容按日保留一份自动 ZIP 快照，手动备份额外生成带时间的文件，默认写入 `/backups`。独立备份不会随同步删除操作改写。

任务 ZIP 包含全部清单历史、回收站、可用附件与附件失效记录，不含设备私钥或成员身份。使用终端“从备份合并恢复”可导入后同步回空间。完整迁移原 NAS 可在停止容器后复制数据卷与备份卷；不要把同一份身份同时启动为两台设备。

数据卷还包含管理令牌与 `admin-keys/` 会话密钥，应随原 NAS 整体迁移。新设备建议创建独立身份，通过邀请加入，再合并恢复备份。详细数据格式见 [备份说明](backup.md)。

## 验证范围

本机已测试实际 NAS 进程、HTTP 控制台、授权码 / 二维码、昵称回传、冲突删除、跨进程同步、崩溃重启和备份恢复。当前环境没有 Docker CLI；Linux 容器与多架构镜像需在 Docker 主机或现有 CI 运行 `scripts/test-docker.sh` 验证。
