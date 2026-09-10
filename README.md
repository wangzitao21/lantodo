<p align="center"><img src="assets/icon/lantodo.svg" width="112" alt="LanTodo 图标"></p>
<h1 align="center">LanTodo</h1>
<p align="center">v1.0 · 本地优先的 Windows / Android 待办清单</p>

记录想法、安排日期，在自己的设备之间同步。无需账号、中心服务器或云端数据库，离线也能查看和编辑。

## 功能

- 清单、备注、日期与时间、完成状态、历史记录和日期筛选。
- 已配对设备通过局域网自动发现，使用双向 TLS 同步。
- 离线修改、删除和并发冲突均保留，冲突由用户选择最终内容。
- SQLite 本机存储；导出全部清单历史，跨设备合并恢复。
- Windows 便携运行、托盘后台同步；设备与备份窗口支持 Esc 返回。
- Android 原生界面、逐层返回、可选后台同步。
- 可选 NAS 辅助同步：Docker 常在线节点、多 NAS 中转、固定地址配对、离线补同步与自动历史快照，见 [NAS 部署与使用](docs/nas.md)。

## 使用

公开发布后，从仓库的 **Releases** 获取 `LanTodo.exe` 或 `LanTodo.apk`。源码仓库不包含安装包；自行构建见 [构建说明](docs/build.md)。

Windows 将 EXE 放到可写入的普通目录后运行，自带运行时。关闭主窗口进入托盘，从托盘“退出 LanTodo”完整退出。Android 支持 8.0 及以上，覆盖升级需相同签名。详细操作见 [使用说明](docs/usage.md)。

两端连接同一可互通局域网，在“设备与同步”中，一端生成配对码，另一端粘贴确认。首次配对后长期记住设备。Windows 防火墙需允许可信专用网络上的 TCP 42851 / UDP 42852。手机后台同步需允许通知和相应电池设置。电脑休眠、手机强制停止或网络隔离会暂时中断同步。

也可在 Debian NAS 上用 Docker 部署常在线同步节点。手机可分别绑定家中、办公室的 NAS，两台 NAS 无需互联，手机在两处联网时携带并同步同一份清单；需要直接跨网同步时再配置 NAS 互联。见 [NAS 部署说明](docs/nas.md)。

## 数据与备份

Windows 默认数据库是 EXE 同目录的 `LanTodo.sqlite`，可在“备份与恢复”中更改位置。Android 数据库位于应用私有 `files/LanTodo/LanTodo.sqlite`。清单、历史、设备身份、配对和同步间隔都在 SQLite 内；Android 的草稿、界面偏好和系统权限由系统另外管理。

导出的 `.lantodo.zip` 含 JSON 格式的完整待办历史，包括删除与冲突。它用于校验、去重及合并恢复，不含设备私钥、配对和设置。**SQLite 存储不意味着导出必须是 SQL 文件。** 格式选择与整库快照方法见 [存储与备份](docs/backup.md)。

数据和导出文件未加密，建议主动保存到独立备份位置。换机使用 ZIP 恢复并重新配对，避免复制同一个设备身份。

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
