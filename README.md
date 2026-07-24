# 天使老板键 Next

天使老板键 Next 是面向 Windows 10/11 x64 的规范化重写。核心功能使用受支持的
Windows API，不注入其他进程、不远程写内存、不联网，也不默认申请管理员权限。

## v0.6 功能

- 多场景配置：每个场景拥有独立热键、目标规则、自动化和工作模式。
- 按可执行文件及可选标题条件隐藏窗口；规则停用、排除或删除后只恢复对应窗口。
- 保存窗口原显示状态和前台状态；正常退出与异常重启均自动恢复。
- 隐藏期间持续处理新窗口，支持托盘、单实例、开机启动和 Explorer 重启恢复。
- 使用 Core Audio 按进程静音，可逐规则启用；原音量和静音状态在操作前写入
  `audio-recovery.json`，恢复后清除，不修改系统总音量。
- 支持键鼠空闲自动隐藏，以及显式启用后的鼠标侧键、中键和滚轮触发；包含冷却、
  去抖和本次运行暂停。
- 可选短生命周期 Elevated Broker。仅在操作高权限窗口时显示 UAC，通过当前用户专用
  命名管道接受查询、隐藏和恢复请求，完成一次请求后退出。
- 可选独立隐私桌面，使用 `CreateDesktop` / `SetThreadDesktop` / `SwitchDesktop`；
  进入前启动并验证独立的轻量 Shell，提供程序启动和返回入口；
  `Ctrl+Alt+Shift+F12` 为紧急返回键。Windows Explorer 不作为该模式的 Shell，避免其
  单实例机制把窗口转回原桌面后留下黑屏。
- 不把独立桌面描述为 Windows 任务视图虚拟桌面，也不承诺兼容反作弊、独占全屏或
  系统安全桌面。检测到全屏前台窗口时会拒绝切换。

## 开发

要求 Windows 10 22H2 或 Windows 11 x64，以及 .NET 10 SDK。

```powershell
dotnet build .\AngelBossKey.Next.slnx -c Release
dotnet test .\AngelBossKey.Next.Tests\AngelBossKey.Next.Tests.csproj -c Release
dotnet publish .\AngelBossKey.Next.App\AngelBossKey.Next.App.csproj -p:PublishProfile=Portable
```

发布输出位于 `dist\AngelBossKey.Next-v0.6.0-win-x64`。运行
`AngelBossKey.Next.exe` 即可，无需安装。自包含包固定携带稳定版 .NET 10.0.10 运行时。

## 本地数据

设置、恢复数据和诊断日志保存在 `%LocalAppData%\AngelBossKey.Next`：

- `settings.json`：场景、热键、规则、自动化及启动选项。
- `recovery.json`：隐藏窗口的身份和原始显示状态，恢复完成后删除。
- `audio-recovery.json`：被修改音频会话的原始状态，恢复完成后删除。
- `logs\angelbosskey.log`：不含窗口标题的滚动诊断日志，最多保留三份备份。

开机启动使用当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，命令行
参数为 `--background`。移动程序目录后，下次正常启动会自动修复路径。
