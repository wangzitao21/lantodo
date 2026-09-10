# 参与开发

LanTodo 使用 C# / .NET 10，Windows 使用 WPF，Android 使用 .NET for Android。环境与命令见 [构建说明](docs/build.md)。贡献以 GPL-3.0-only 授权。

提交前运行 `./scripts/test.ps1`。修改桌面界面时运行 `./scripts/test-windows.ps1` 和 Windows 构建；修改 Android 界面时编译 APK，并在测试设备验证相关操作。PR 说明问题、最终行为和实际完成的验证，区分自动测试与真机检查。

涉及 NAS 或同步连接时运行 `./scripts/test-nas.ps1`；Linux / Docker 命令见 [NAS 说明](docs/nas.md)。应用版本集中在 `Directory.Build.props`；变更公开版本时同步 Windows manifest、Android 递增安装序号和发布文档，保持协议版本独立。

保持改动聚焦，不把生成的 `bin/`、`obj/`、`.tools/`、`release/`、数据库或签名密钥加入 Git。图标以 `assets/icon/lantodo.svg` 为源，修改后运行 `./scripts/generate-icons.ps1` 并提交生成的资源。

涉及存储和同步时，先阅读 [设计说明](docs/architecture.md) 和 [数据库接口](docs/database-v1.md)。不得丢弃历史或冲突分支，不按时间戳静默覆盖，不复制设备私钥到跨设备备份。数据库结构和备份协议版本独立于应用版本，升级必须保留兼容路径。
