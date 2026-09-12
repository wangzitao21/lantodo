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

Android Release 使用 partial trimming 和 profiled AOT，保留 Core/Android 程序集中的反射序列化类型与回调；Debug 不裁剪、不做 AOT。发布测试必须使用正式 APK，覆盖旧资料读取、发送、同步和附件，不能只验证 Debug。多 ABI 发布关闭主机运行时标识自动推断，避免 .NET SDK 把 Windows 主机标识带入 Android 发布。

## 输出与版本

应用对外版本保持 **v1.0.1**。共享配置 `Directory.Build.props` 中的 `Version` 和 `InformationalVersion` 均为 `1.0.1`。Android `versionName` 从共享配置读取，`versionCode=19` 为安装序号，本次修订递增安装序号，对外仍显示 v1.0.1，使用原签名覆盖升级。SQLite schema、任务版本和备份格式继续保持 v1；空间授权协议使用 lantodo2，须全端升级；已有统一空间可直接保留，旧版逐台配对需重新加入。

展示版本不附加 Git 提交哈希。NAS 本地镜像标签为 `lantodo-nas:1.0.1`，OCI 版本标签为 `1.0.1`；升级版本时一并更新 Dockerfile 与 Compose。

发布文件默认直接平铺到 `release/`，不创建平台或版本子目录，支持 `-OutputRoot <目录>`：

- `LanTodo-v1.0.1-Windows.zip`：Windows x64 文件夹式便携包，完整解压后运行 `LanTodo.exe`，依赖不自解压到 Temp。
- `LanTodo-v1.0.1-Android.apk`：Android 8.0+，arm64 / x64，正式签名。
- `LanTodo-v1.0.1-NAS.zip`：Docker NAS 精简部署源码。
- `LanTodo-v1.0.1-source.zip`：完整源码、测试、文档与 CI。
- `使用说明.md`：客户端、NAS 部署及同步策略的合并说明。
- `许可证与第三方声明.txt`：项目许可证及第三方许可全文。
- `SHA256SUMS.txt`：发布文件校验值。

文件名中的版本自动读取 `Directory.Build.props`。先完整退出 Windows 程序，再替换 EXE。已有 `LanTodo.location` 和运行数据库不属于发布附件，构建不会覆盖它们。

```powershell
./scripts/build.ps1 -Android
./scripts/check-apk.ps1
./scripts/package-source.ps1
```

`release/` 不纳入 Git。完整源码包排除工具链、构建产物、数据库和密钥；公开二进制时一并提供完整源码包或对应 Git 标签源码。

## GitHub

```powershell
# 全新目录且尚未初始化 Git 时执行：git init -b main
git add .
git diff --cached --name-only
git diff --cached --stat
git commit -m "Prepare v1.0.1 release"
git tag v1.0.1
# 在 GitHub 创建空仓库，再按页面提示添加 origin 和推送。
```

`.gitignore` 排除本机工具、安装包、清单、密钥和测试输出。提交前检查暂存文件列表；本地已有 Git 仓库不需要重新初始化。CI 在 Windows 运行核心、NAS 进程与界面测试，编译 Android Debug APK，并在 Linux 运行回归、构建两种 NAS 架构镜像及验证容器启动。CI 配置需推送后实际执行；本机验证不能代替远端结果。当前没有自动发布镜像或 GitHub Release 的流程。

本次终端响应与启动修订的 Android 安装序号为 19；改动与验证范围见 `docs/optimization-build19.md`，前两轮修订见 `docs/optimization-build18.md` 和 `docs/optimization-build17.md`。
