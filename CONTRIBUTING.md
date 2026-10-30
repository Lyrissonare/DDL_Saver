# 参与贡献

欢迎提交 Issue 与 Pull Request。提交前请先搜索是否已有相同问题。

## 开发环境

- Windows 10 1809+ / Windows 11 64 位
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 推荐 Visual Studio 2022 或 Rider

```powershell
dotnet restore DDLSaver.sln
dotnet build  DDLSaver.sln -c Release
dotnet run --project DDLSaver
```

首次运行会在 `%AppData%\DDLSaver\` 生成示例数据。调试时可用：

| 参数 | 作用 |
| --- | --- |
| `--open-edit` | 启动后直接打开「任务编辑」窗口，便于检查表单布局 |
| `--toast-preview` | 在右下角预览应用内通知（Toast 兜底样式） |

构造 UI 场景也可以直接使用命名管道唤醒主窗口：

```powershell
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "DdlSaver.Activate", [System.IO.Pipes.PipeDirection]::Out)
$pipe.Connect(3000)
$w = New-Object System.IO.StreamWriter($pipe); $w.AutoFlush = $true
$w.WriteLine("show")   # task:{guid} 可定位到指定任务
$w.Dispose(); $pipe.Dispose()
```

## 代码约定

- 目标框架 `net8.0-windows10.0.19041.0`，C# 12，可空引用开启。
- UI 基于 **WPF-UI（Fluent 2）**：优先使用 `ui:FluentWindow` / `ui:TitleBar` 与原生控件的 Fluent 隐式样式；不要在 XAML 里直接写死颜色，统一使用 `App.xaml` / `ThemeService` 中的语义画刷。
- 所有窗口保持 `UseLayoutRounding="True"` 与 `SnapsToDevicePixels="True"`，避免高 DPI（125%/150%）下 1px 描边被抗锯齿吃掉。
- 中文文案宽度约为字号的 1 倍：标签列用 `Width="Auto"` + `MinWidth`，长说明用 `TextWrapping="Wrap"` 或显式 `LineBreak`，不要用固定列宽容纳可变长文案，否则会被裁切。
- 代码构建的窗口（浮窗、应用内通知）必须保持 `AllowsTransparency` 能力，不能改成 `FluentWindow`。
- 数据与设置只写入 `%AppData%\DDLSaver\`，应用不联网、不申请管理员权限，请保持这一约束。

## 提交 PR

1. 从 `main` 拉出主题分支（`fix/...`、`feat/...`）。
2. 确保 `dotnet build DDLSaver.sln -c Release` 零警告零错误。
3. 涉及 UI 的改动请附上浅色 / 暗色截图；涉及浮窗的改动请说明是否验证过鼠标穿透与全屏降级。
4. 不要提交 `bin/`、`obj/`、本地数据文件或个人配置。
