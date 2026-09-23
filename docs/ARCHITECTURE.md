# 架构

```
SeedToolBox.sln
├─ Directory.Build.props        公共编译配置：net48 / x64 / C# latest
├─ src/SeedToolBox.Core/        核心库（不依赖界面），插件只需引用它
│   ├─ AppPaths.cs              目录约定：Data / Plugins / Native
│   ├─ Modules/                 IModule（功能模块）、IAppHost（宿主提供给模块的能力）
│   ├─ Services/                ISettingsStore（JSON 配置）、Log（日志）
│   └─ Native/                  NativeLibraries（原生 DLL 加载）、MemoryTrimmer
├─ src/SeedToolBox/             主程序（WPF 外壳）
│   ├─ Host/                    AppHost、ModuleManager（加载/隔离模块）、TrayIcon
│   ├─ Launcher/                快捷启动：模型、图标提取、进程启动
│   ├─ Modules/                 内置模块
│   └─ Views/                   通用对话框
└─ native/                      C++ / Rust 高性能组件源码，产物放 native/bin/*.dll
```

## 运行目录

```
SeedToolBox.exe
Data/            配置（<模块Id>.json）与日志（Logs/app.log）
Native/          原生 DLL（由 native/bin 自动复制）
Plugins/<名字>/<名字>.dll   外部插件及其依赖
```

## 加一个功能

1. 新建类实现 `IModule`，在 `Initialize` 里通过 `IAppHost` 注册托盘菜单、读写配置。
2. 内置功能：放 `src/SeedToolBox/Modules/`，在 `ModuleManager.LoadAll` 里登记。
   外部插件：独立类库引用 `SeedToolBox.Core`，输出到 `Plugins/<名字>/`，启动时自动发现。
3. 模块初始化或菜单动作抛异常只会记日志并提示，不会拖垮主程序。

## 高性能功能（C++ / Rust）

两种接入方式，按需求选：

| 方式 | 适合 | 做法 |
|---|---|---|
| **进程内 DLL** | 调用频繁、要低延迟（搜索索引、图像处理） | 导出 C ABI 函数，DLL 放 `native/bin/`，C# 用 `[DllImport("xxx.dll")]` 调用 |
| **独立子进程** | 可能崩溃、要常驻后台、要管理员权限 | 编译成独立 exe，通过命名管道 / 标准输入输出通信 |

约定：
- 只出 **x64** 版本（主程序固定 x64）。
- 接口只用 C ABI（`extern "C"` / Rust `#[no_mangle] extern "C"`），字符串统一 UTF-16（`wchar_t*`），内存谁分配谁释放。
- 启动时 `NativeLibraries.Init()` 已把 `Native/` 加入 DLL 搜索路径，`DllImport` 直接写文件名即可。

## 内存

窗口隐藏到托盘 2 秒后自动 `MemoryTrimmer.Trim()`，把工作集换出。
实测（v0.1）：显示时私有工作集约 75MB，隐藏后约 3MB。
