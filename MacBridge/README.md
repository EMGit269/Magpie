# Rhino 8 for Mac Bridge

独立的 .NET 8 Grasshopper 插件，通过本机 HTTP 将画布能力提供给 `agent_service`。
原 Windows 工程仍使用 `Magpie.csproj`；Mac 请构建本目录工程。

## 安装

需要 macOS、Rhino 8（使用 .NET 8）和 .NET 8 SDK。退出 Rhino 后，在仓库根目录运行：

```bash
./MacBridge/build-install.sh
```

非默认 SDK 或 Rhino 路径可这样指定：

```bash
DOTNET=/path/to/dotnet ./MacBridge/build-install.sh \
  '-p:RhinoResources=/Applications/Rhino 8.app/Contents/Frameworks/RhCore.framework/Versions/A/Resources'
```

默认安装到 `~/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper (b45a29b1-4343-4035-989e-044e8580d9cf)/Libraries/Magpie.MacBridge/`。
如 Grasshopper 的 Libraries 位置不同，设置 `MAGPIE_GH_LIBRARIES` 为该目录。
只安装 `Magpie.MacBridge.gha`，依赖使用 Rhino 自带程序集。

重新打开 Rhino，在命令行输入 `Grasshopper` 并打开或新建 GH 文档。
插件加载后自动启动 Bridge；也可在画布放置 `Magpie → Host → Magpie Mac Bridge` 查看状态。
默认地址：

```bash
curl http://127.0.0.1:8765/health
curl http://127.0.0.1:8765/tools/manifest
curl -H 'Content-Type: application/json' \
  -d '{"tool":"get_canvas_summary","args":{}}' \
  http://127.0.0.1:8765/tools/invoke
```

Bridge 只监听 `127.0.0.1`。端口被占用时加载失败，日志位于 `~/Library/Logs/Magpie/bridge.log`。
可在启动 Rhino 的进程环境中设置 `MAGPIE_HOST_BRIDGE_PORT`，同时将 Agent 的
`MAGPIE_AGENT_HOST_BRIDGE_BASE_URL` 改为相同地址。通过 Finder 启动的应用不会继承终端的 `export`。

## 支持范围

支持 9 个工具：画布摘要、组件查询、邻域上下文、错误检查、重算、连线、删除、改值、批量创建组件图。
全部画布请求（包括 ID 解析）在 Rhino 主线程执行，支持 Public ID 和完整实例 GUID。
改值支持 Number Slider、Panel、Boolean Toggle。滑块支持 `min`、`max`、`decimals`。
组件按准确名称、昵称或 `component_guid` 查找，名称重复时需要 GUID。
批量创建的连接使用 `from_alias` / `to_alias` 或现有组件的 `from_id`；
目标必须是本批新建组件。向现有目标连线请单独调用 `connect_components`，以保留撤销行为。
创建操作先验证所有组件和连线，失败时回滚本批已添加对象。

尚未移植 Windows 聊天窗口、C# 脚本读写和 Graph Mapper 配置；这些脚本工具不会出现在 Mac manifest 中。
Mac Bridge 不启动 Python 服务，也不保存模型密钥。Agent 安装及启动见 [agent_service 文档](../agent_service/README.md)。
当 Agent `/health` 返回 `host_bridge_available: true` 时连接成功。

## 实机测试

编译完成并启动 Rhino 8 后：

```bash
"/Applications/Rhino 8.app/Contents/Resources/bin/rhinocode" list
python3 MacBridge/tests/run_smoke.py --rhino rhinocode_remotepipe_实际PID
```

测试临时切换到独立 GH 文档，结束时恢复原画布；测试期间请勿手动切换或编辑画布。
涵盖 manifest、不可用工具、无效 JSON、创建/查询/改值/连线/删除、无效操作不留对象、24 个并发请求。
`rhinocode` 首次执行可能需要初始化 Rhino 自带 Python；请先完成初始化后再运行。
仅构建可运行 `dotnet build MacBridge/Magpie.MacBridge.csproj -c Release`。
