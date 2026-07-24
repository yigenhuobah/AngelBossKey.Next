# 天使老板键 Next

天使老板键 Next 是对旧版老板键的干净重写。v0.1 只使用受支持的 Windows 用户态 API，
不注入其他进程、不加载加密 DLL、不联网，也不默认申请管理员权限。

## v0.1 功能

- 从当前可见窗口选择目标程序，按完整可执行文件路径匹配同进程窗口。
- 用户自定义全局热键，一键隐藏并再次恢复。
- 隐藏期间自动处理目标程序新出现的顶层窗口。
- 保存窗口显示状态和前台状态；正常退出及异常重启后自动恢复。
- 托盘常驻、关闭到托盘、当前用户开机启动和单实例唤醒。
- 高权限目标在普通模式下跳过并计数，不自动提权。

## 开发

要求 Windows 10 22H2 或 Windows 11 x64，以及 .NET 10 SDK。

```powershell
dotnet build .\AngelBossKey.Next.slnx -c Release
dotnet test .\AngelBossKey.Next.Tests\AngelBossKey.Next.Tests.csproj -c Release
dotnet publish .\AngelBossKey.Next.App\AngelBossKey.Next.App.csproj -p:PublishProfile=Portable
```

发布输出位于 `dist\AngelBossKey.Next-v0.1.0-win-x64`。运行
`AngelBossKey.Next.exe` 即可，无需安装。自包含包固定携带稳定版 .NET 10.0.10 运行时。

## 本地数据

设置与恢复日志保存在 `%LocalAppData%\AngelBossKey.Next`：

- `settings.json`：热键、目标程序及启动选项。
- `recovery.json`：仅在窗口处于隐藏状态时存在，恢复完成后删除。

开机启动使用当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，
命令行参数为 `--background`。移动程序目录后，下次正常启动会自动修复路径。
