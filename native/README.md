# native

C++ / Rust 高性能组件放这里，每个组件一个子目录。

编译产物（x64 DLL）放到 `native/bin/`，主程序构建时会自动复制到输出目录的 `Native/` 下。

接口约定见 [docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md#高性能功能c--rust)。
