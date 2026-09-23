# native

C++ / Rust 高性能组件放这里，每个组件一个子目录。

编译产物按架构放到 `native/bin/x64/` 和 `native/bin/x86/`，主程序构建时会自动复制到输出目录的 `Native/x64/`、`Native/x86/` 下，运行时按进程位数加载。

接口约定见 [docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md#高性能功能c--rust)。
