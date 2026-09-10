# 构建与发布

## 开发环境

使用 Windows 10/11、PowerShell 和 .NET 10 SDK（`global.json` 指定最低 10.0.100，允许后续稳定功能版本）。首次还原依赖需要联网。脚本优先使用本机 `.tools/dotnet`，不存在时使用 PATH 中的 `dotnet`；全新克隆无需复制原作者的 `.tools`。

```powershell
./scripts/test.ps1
./scripts/test-nas.ps1
./scripts/build.ps1
./scripts/test-windows.ps1
./scripts/windows-smoke.ps1
```

核心测试是返回退出码的控制台测试程序，应使用上面的脚本或 `dotnet run --project tests/LanTodo.Tests -c Release`，不是 `dotnet test`。界面测试渲染合成数据，并在屏幕外的测试窗口验证 Esc 关闭和再次打开。启动检查用独立数据库，不读取个人清单。

## Android

安装 .NET Android workload、JDK 21 和 Android SDK。可使用 `ANDROID_HOME` / `JAVA_HOME` 指向已有环境；本地 `.tools/android-sdk` / `.tools/jdk` 存在时构建脚本优先使用它们。

```powershell
dotnet workload install android
dotnet build src/LanTodo.Android -t:InstallAndroidDependencies -f net10.0-android -p:AcceptAndroidSdkLicenses=true
dotnet build src/LanTodo.Android -c Debug
```

Debug 构建使用开发签名，可用于独立测试设备。正式发布需自己的密钥：

```powershell
keytool -genkeypair -keystore lantodo.p12 -storetype PKCS12 -alias lantodo -keyalg RSA -keysize 3072 -validity 10000
$env:LANTODO_KEYSTORE = '你的密钥绝对路径'
# LANTODO_KEY_PASSWORD 通过本机安全环境或 CI Secret 设置，不提交到脚本。
./scripts/build.ps1 -Android
./scripts/check-apk.ps1
./scripts/android-smoke.ps1 -Serial emulator-5554
```

密钥别名默认 `lantodo`，可用 `LANTODO_KEY_ALIAS` 覆盖。本机已有 `.tools/signing/lantodo.p12` 与 `password.txt` 时脚本沿用原签名。覆盖升级必须使用同一密钥，不要卸载旧应用或清除数据。源码仓库不提供发布密钥。

## 输出与版本

应用对外版本为 **v1.0**。共享配置 `Directory.Build.props` 中的 `Version=1.0.0` 是 .NET 数值形式，`InformationalVersion=1.0` 用于展示。Windows manifest 使用 `1.0.0.0`；Android `versionName` 从共享配置读取，`versionCode=8` 是递增安装序号，保证已有开发版可覆盖升级。SQLite schema、同步和备份格式继续保持 v1，不随应用版本改动。

展示版本不附加 Git 提交哈希。NAS 本地镜像标签为 `lantodo-nas:1.0`，OCI 版本标签为 `1.0`；升级版本时一并更新 Dockerfile 与 Compose。

发布文件默认写入 `release/windows/LanTodo.exe` 与 `release/android/LanTodo.apk`，支持 `-OutputRoot <目录>`。Windows 为 x64 自带运行时单文件程序，Android APK 支持 arm64 / x64、Android 8.0 及以上。先完整退出旧 Windows 程序，再覆盖其 EXE。

`release/` 为本机生成目录，不纳入 Git。构建同时生成 NAS 精简部署源码包 `LanTodo-nas-source.zip`、使用说明、NAS 说明、许可证、第三方声明和 `SHA256SUMS.txt`。这些文件作为 GitHub Release 附件发布，不把整个便携程序目录或数据库提交到源码仓库。

可将同版本文件放在独立目录，避免替换正在运行的程序：

```powershell
./scripts/build.ps1 -Android -OutputRoot release/v1.0
./scripts/check-apk.ps1 -Apk release/v1.0/android/LanTodo.apk
./scripts/package-source.ps1
```

`package-source.ps1` 默认生成 `release/v1.0/LanTodo-v1.0-source.zip`，包含当前源码、测试、文档、CI 和 Docker 配置；不含 `.git`、工具链、构建产物、数据库或密钥。此包与仅用于 NAS 构建的精简源码包不同。公开二进制时提供完整源码包或对应 Git 标签的源码归档。

## GitHub

```powershell
# 全新目录且尚未初始化 Git 时执行：git init -b main
git add .
git diff --cached --name-only
git diff --cached --stat
git commit -m "Prepare v1.0 release"
git tag v1.0
# 在 GitHub 创建空仓库，再按页面提示添加 origin 和推送。
```

`.gitignore` 排除本机工具、安装包、清单、密钥和测试输出。提交前检查暂存文件列表；本地已有 Git 仓库不需要重新初始化。CI 在 Windows 运行核心、NAS 进程与界面测试，编译 Android Debug APK，并在 Linux 运行回归、构建两种 NAS 架构镜像及验证容器启动。CI 配置需推送后实际执行；本机验证不能代替远端结果。当前没有自动发布镜像或 GitHub Release 的流程。
