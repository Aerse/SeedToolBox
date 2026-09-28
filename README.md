# SeedToolBox

一个 Windows 桌面工具箱：把启动器、截图、录屏、文字识别、剪贴板历史、便签、提醒、AI 助手、开发调试工具和终端 / SSH 装进同一个便携程序里。

> 完整使用说明见 **[docs/使用说明.md](docs/使用说明.md)**。

## 功能

| 模块 | 能做什么 |
|---|---|
| **启动器** | 拖入程序/文件夹/网址即可添加，分组、排序、单独快捷键；搜索支持拼音，`=` 计算、`>` 运行命令、`f` 找文件、`g`/`bd` 网页搜索、`ai` 问 AI |
| **截图** | 窗口识别、标注（箭头/马赛克/序号等）、长截图、延时截图、贴图、截图历史 |
| **文字识别** | 离线 PaddleOCR 中英文识别，表格识别，二维码识别与生成 |
| **录屏** | 区域/窗口录制，系统声音和麦克风，暂停，裁剪首尾，导出 MP4 / GIF |
| **剪贴板历史** | 文字、图片、文件都记，快速粘贴、置顶、标签、忽略密码管理器 |
| **便签 / 提醒** | 桌面便签自动保存；用“明天9点 交报告”这样的中文添加提醒 |
| **AI 助手** | 基于 pi，支持 DeepSeek、通义、Kimi、智谱、Claude、GPT、Gemini、Ollama 等；自动化模式能整理文件、查进程、调音量，每个改动都要确认且可撤销 |
| **开发工具** | API 请求（类 Postman）、文本对比、JSON/XML/YAML 格式化与互转、编码、Base64、时间戳、正则、JWT、哈希、UUID、图片转换、批量重命名、重复文件、空间占用、端口/网络、Hosts 管理 |
| **终端 / SSH** | 对标 XTerminal：SSH / Telnet / 串口 / FTP / 本地终端，跳板机和代理，分屏与广播，SFTP（拖拽上传、文件夹自动打包传输、远程编辑、跨服务器比较），传输页，服务器监控，Docker 管理，命令片段，端口转发，AI 生成命令 |
| **系统工具** | 保持唤醒、窗口置顶、环境变量、启动项、进程、本地服务、系统监控悬浮条、声音与亮度 |
| **数据** | 便携（全部在 `Data\`），每日自动备份，一键备份恢复，WebDAV 加密同步 |

## 默认快捷键

| 功能 | 快捷键 |
|---|---|
| 呼出主窗口 | `Ctrl+Q` |
| 截图 | `Alt+Shift+A` |
| 录屏 | `Alt+Shift+E` |
| 剪贴板历史 | `Alt+Shift+V` |
| 新建便签 | `Alt+Shift+N` |

更多快捷键在「设置 → 快捷键」里自定义。

## 安装

1. 下载发布包，解压到任意文件夹。
2. 运行 `SeedToolBox.exe`。

- **系统要求**：Windows 10 / 11，.NET Framework 4.8（系统自带）。
- **终端 / SSH**：需要 WebView2 运行时（Win11 自带）。
- **配置位置**：都存在程序旁边的 `Data\`，整个文件夹拷走即可迁移。

## 开发

需要 .NET SDK 8 或更高（用来编译 net48）。

```bash
dotnet build
dotnet run --project src/SeedToolBox
```

- **技术栈**：C# WPF + .NET Framework 4.8。终端用 WebView2 + xterm.js，SSH 用 SSH.NET，OCR 用 ONNX Runtime + PaddleOCR 模型。
- **扩展方式**：功能通过 `IModule` 接入，支持外部插件（`Plugins\`）和 C++/Rust 原生组件（`Native\`）。
- **架构说明**：见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 发布

```bash
dotnet build src/SeedToolBox -c Release
```

产物在 `src/SeedToolBox/bin/Release/net48/`。把该目录整体打成 zip，不要包含 `*.pdb`，也不要包含 `Data\`（里面有个人配置和密钥）。
