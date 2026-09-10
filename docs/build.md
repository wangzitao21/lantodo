# 构建与发布

## 开发环境

使用 Windows 10/11、PowerShell 和 .NET 10 SDK（`global.json` 指定最低 10.0.100，允许后续稳定功能版本）。首次还原依赖需要联网。脚本优先使用本机 `.tools/dotnet`，不存在时使用 PATH 中的 `dotnet`；全新克隆无需复制原作者的 `.tools`。

```powershell
./scripts/test.ps1
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

应用对外版本为 **v0.1**。共享项目中的 `Version=0.1.0` 是 .NET 标准数值形式，`InformationalVersion=0.1` 用于展示。Android 的 `versionName` 从共享属性读取；`versionCode` 是必须递增的整数安装序号，当前为 6，用于兼容本机此前安装包的升级，与公开版本重置无关。SQLite schema、同步和备份格式继续保持 v1。

发布文件默认写入 `release/windows/LanTodo.exe` 与 `release/android/LanTodo.apk`，支持 `-OutputRoot <目录>`。Windows 为 x64 自带运行时单文件程序，Android APK 支持 arm64 / x64、Android 8.0 及以上。先完整退出旧 Windows 程序，再覆盖其 EXE。

`release/` 为本机生成目录，不纳入 Git。发布时只选择 EXE、APK、使用说明、许可证、第三方声明和 `SHA256SUMS.txt`，不要上传整个便携程序文件夹里的个人数据库。公开二进制时，同时提供对应版本完整源码，例如 GitHub Release 标签对应的源码归档。

## GitHub

```powershell
git init -b main
git add .
git diff --cached --stat
git commit -m "Initial release v0.1"
git tag v0.1
# 在 GitHub 创建空仓库，再按页面提示添加 origin 和推送。
```

`.gitignore` 排除本机工具、安装包、清单和密钥。CI 在 Windows 运行核心测试、桌面发布和界面检查，另一个任务编译 Android Debug APK。CI 配置需推送后由 GitHub 实际执行；本机验证不能代替远端结果。
