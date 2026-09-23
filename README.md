# SeedToolBox

Windows 桌面工具箱。当前功能：应用快捷启动（拖动排序、项目属性、高清图标、拼音搜索）、截图（窗口识别、标注、贴图）、取色（HEX/RGB/HSL）、屏幕标尺、录屏（区域/窗口、系统声音与麦克风、暂停、裁剪首尾、导出 MP4/GIF）、文字识别（PaddleOCR 离线识别，中英文）、二维码识别与生成、可自定义的全局热键、开机自启、托盘菜单、系统工具入口。

- C# WPF + .NET Framework 4.8（Win10/11 自带，免装运行时）
- 便携：配置保存在 exe 同目录 `Data/`
- 模块化：功能以 `IModule` 接入，支持外部插件与 C++/Rust 原生组件

架构说明见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 开发

需要 .NET SDK（8 或更高即可，用来编译 net48）。

```bash
dotnet build
dotnet run --project src/SeedToolBox
```

## 发布

```bash
dotnet build src/SeedToolBox -c Release
```

产物在 `src/SeedToolBox/bin/Release/net48/`，把该目录（不含 `*.pdb`）整体打包即可。
