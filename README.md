# SeedToolBox

Windows 桌面工具箱，当前功能：应用快捷启动 + 托盘图标。

## 开发

需要 .NET 8 SDK。

```bash
dotnet build
dotnet run --project src/SeedToolBox
```

## 发布

```bash
dotnet publish src/SeedToolBox -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

配置保存在 exe 同目录的 `Data/config.json`。
