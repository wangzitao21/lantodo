<p align="center"><img src="assets/icon/lantodo.svg" width="112" alt="LanTodo 图标"></p>
<h1 align="center">LanTodo</h1>
<p align="center">v1.0.1 · 本地优先的 Windows / Android 待办清单</p>

记录想法、安排日期，在自己的设备之间同步。无需账号、中心服务器或云端数据库，离线也能查看和编辑。

## 功能

- 图片、文件与文件夹原件附件；Windows 粘贴/拖拽/加号选择，Android 图片与文件选择。
- Android 消息长按菜单、滑动超过一半删除，未过半自动回弹；两端可改本机昵称并开关最后修改设备显示。
- Docker NAS 管理网页：统一设备与同步入口、添加设备引导、全设备昵称、绑定状态同步、冲突卡片与备份。

- 多个可命名的网络空间，共用清单与附件；粉色按钮的回收站、恢复和全部清除。
- 清单、备注、日期与时间、完成状态、历史记录和日期筛选。
- 加入同一私有空间后自动认识所有成员，局域网发现与可达地址使用同一套双向 TLS 同步。
- 离线修改、删除和并发冲突均保留，冲突由用户选择最终内容。
- SQLite 本机存储；导出全部清单历史，跨设备合并恢复。
- Windows 便携运行、托盘后台同步；设备与备份窗口支持 Esc 返回。
- Android 原生界面、逐层返回、可选后台同步。
- 可选 NAS 成员：与手机、电脑使用相同授权码 / 二维码入口，保存并转发修改，离线补同步与自动历史快照，见 [NAS 部署与使用](docs/nas.md)。

## 使用

公开发布后，从仓库的 **Releases** 获取 `LanTodo-v1.0.1-Windows.zip` 或 `LanTodo-v1.0.1-Android.apk`。源码仓库不包含安装包；自行构建见 [构建说明](docs/build.md)。

Windows 完整解压 ZIP 到可写目录，运行其中的 EXE，自带运行时；移动时请带上整个目录。关闭主窗口进入托盘，从托盘“退出 LanTodo”完整退出。Android 支持 8.0 及以上，覆盖升级需相同签名。详细操作见 [使用说明](docs/usage.md)。

任一设备打开‘设备与同步 → 添加设备’，生成授权码与二维码。新设备选择‘加入已有空间’，扫码或粘贴加入网络空间，本机全部内容会与成员同步。加入一次后，成员与修改自动传播，无需逐台配对。NAS 是可选的常在线成员，邀请设备离线后其余设备仍可互通。

同一网络自动发现；跨网络需要可达地址或已有 VPN。Windows 防火墙需允许专用网络 TCP 42851 / UDP 42852。手机后台与电池限制、电脑休眠或网络隔离会暂时中断同步，恢复后补齐。见 [统一空间说明](docs/sync-review-v1.0.1.md)。

## 数据与备份

Windows 默认数据库是 EXE 同目录的 `LanTodo.sqlite`，数据库与附件固定随程序存放。Android 数据库位于应用私有 `files/LanTodo/LanTodo.sqlite`。每台设备可加入多个网络空间，同一份内容在已连接设备之间接力同步。`spaces/` 仅保留各组连接的成员与路由；全部附件原件按原名称平铺在根目录的 `attachments/`，映射与失效记录位于 `attachment-index.json`；清单、历史、设备身份、配对和同步间隔都在 SQLite 内；Android 的草稿、界面偏好和系统权限由系统另外管理。

导出的 `.lantodo.zip` 含全部清单 JSON 格式的完整待办历史，包括删除与冲突，以及回收站、可用附件与附件失效记录。它用于校验、去重及合并恢复，不含设备私钥、配对和设置。**SQLite 存储不意味着导出必须是 SQL 文件。** 格式选择与整库快照方法见 [存储与备份](docs/backup.md)。

数据和导出文件未加密，建议主动保存到独立备份位置。换机使用 ZIP 恢复并重新配对，避免复制同一个设备身份。

统一空间的规则、离线转发与升级步骤见 [v1.0.1 同步检查](docs/sync-review-v1.0.1.md)。

## 开发

C# / .NET 10，共享核心使用 Microsoft.Data.Sqlite，Windows 使用 WPF + XAML，Android 使用 .NET for Android。

```powershell
./scripts/test.ps1                 # 核心回归与本机 TLS 测试
./scripts/test-nas.ps1             # NAS 进程、双节点中转与重启恢复测试
./scripts/build.ps1                # Windows 便携版
./scripts/test-windows.ps1         # 桌面布局和 Esc 检查
./scripts/build.ps1 -Android       # Windows + 正式签名 APK（需要密钥）
```

全新环境、Android Debug 构建、正式签名及 GitHub 发布流程见 [构建说明](docs/build.md)。

| 路径 | 内容 |
| --- | --- |
| `src/` | 共享核心、Windows / Android 客户端、NAS 服务 |
| `tests/` | 核心与 NAS 回归、WPF 界面检查、Android 传输探针 |
| `scripts/` | 构建、测试、源码打包、签名校验和图标生成 |
| `assets/icon/` | SVG 图标源文件与 PNG 预览 |
| `docs/` | 使用、备份、架构、数据库约定和验证说明 |
| `.github/` | 自动构建与测试配置 |
| `Dockerfile` / `compose.yaml` | NAS 容器构建与部署 |

本机 `.tools/`、`release/`、数据库、密钥及中间产物被 Git 忽略，不随源码上传。图标为青绿色圆角底与白色 L / 勾形，通过 `scripts/generate-icons.ps1` 生成各平台资源。

## 贡献与许可

见 [贡献指南](CONTRIBUTING.md)、[设计说明](docs/architecture.md)、[验证说明](docs/acceptance.md) 和 [更新记录](CHANGELOG.md)。

LanTodo 源码及原创图标使用 **GNU GPL v3.0 only（GPL-3.0-only）**，全文见 [LICENSE](LICENSE)。第三方组件保留各自许可，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
