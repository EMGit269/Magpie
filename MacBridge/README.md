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
`MAGPIE_HOST_BRIDGE_BASE_URL` 改为相同地址。通过 Finder 启动的应用不会继承终端的 `export`。

## 支持范围

支持 9 个工具：画布摘要、组件查询、邻域上下文、错误检查、重算、连线、删除、改值、批量创建组件图。
全部画布请求（包括 ID 解析）在 Rhino 主线程执行，支持 Public ID 和完整实例 GUID。
改值支持 Number Slider、Panel、Boolean Toggle。滑块支持 `min`、`max`、`decimals`。
组件按准确名称、昵称或 `component_guid` 查找，名称重复时需要 GUID。
批量创建的连接使用 `from_alias` / `to_alias` 或现有组件的 `from_id`；
目标必须是本批新建组件。向现有目标连线请单独调用 `connect_components`，以保留撤销行为。
创建操作先验证所有组件和连线，失败时回滚本批已添加对象。

Mac 已提供独立 Eto 聊天窗口。C# 脚本读写和 Graph Mapper 配置尚未移植，脚本工具不会出现在 Mac manifest 中。
Bridge 本身不负责模型调用；聊天窗口连接或自动启动外部 Python Agent 服务。Agent 安装及依赖见 [agent_service 文档](../agent_service/README.md)。
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

## Mac 聊天窗口

在 Grasshopper 的 `Magpie → Agent` 分类放置 **Magpie Agent** 组件，双击组件打开聊天窗口。
输入自然语言任务后点击发送，或按 `⌘ + Enter`；生成过程中再次点击发送按钮可停止。
窗口支持流式回复、工具结果、会话历史、新建、重命名和删除会话，以及主题切换。
关闭聊天窗口会取消当前生成，Bridge 和共享的 Agent 服务继续运行。

回复支持 Markdown 标题、列表、代码块和表格，并可一键复制。文字与工具调用按执行阶段排列，
同名工具通过调用 ID 对应结果；工具详情默认折叠，失败时自动展开。阅读较早消息时不会强制滚动到底部。

### 引用画布

打开窗口或新建对话时，自动引用当前 Grasshopper 文档；输入框上方显示文件名、组件数量和选中组件数量。
也可点击 **引用当前画布**，下一次发送会向 Agent 提供最新画布信息和选中组件 ID。
模型仍需调用工具读取具体组件状态，引用信息不会代替实际检查。

每个对话关联具体文档 ID，引用关系保存在本机 `~/Library/Application Support/Magpie/canvas-bindings.json`。
切换到其他画布后，原对话会提示不匹配；发送检查和每次工具执行都会验证文档 ID，拒绝操作另一张画布。
切回原画布可继续对话；有消息的对话改为引用另一张画布时，会自动开始新对话。
加载历史对话会恢复原引用，关闭并重新打开的 GH 文档如果获得新的运行时 ID，需要重新引用。
未打开画布时仍可进行普通问答，但画布工具会拒绝执行。

更新后请重启现有 Python Agent 服务，让 `/health` 返回 `canvas_binding_supported: true`。
窗口会拒绝向不支持绑定参数的旧服务发送请求，防止旧服务忽略文档 ID。

安装脚本会生成 `~/Library/Application Support/Magpie/mac-agent.json`，记录 Python 可执行文件、
`agent_service` 目录和默认服务地址 `http://127.0.0.1:8123`。默认寻找仓库根目录或
`agent_service` 目录下的 `.venv/bin/python`，可用 `MAGPIE_AGENT_PYTHON` 指定其他解释器。
请先按 Agent 文档安装 Python 依赖。窗口打开时优先连接已有服务；连接失败才自动启动 Python。
自动启动使用参数列表直接运行 Python，不经过 shell。Rhino 退出时只停止插件自己启动的进程。

在窗口 Settings 中选择 DeepSeek、Qwen、OpenAI 或 OpenAI compatible，填写 Base URL、Model 和 API key。
已有的模型配置会继续使用；留空 API key 表示保留已保存的密钥，勾选 Remove 才会删除。
密钥保存在 `~/Library/Application Support/Magpie/agent-service-model.json`，文件权限为 `0600`，
不会回传到聊天页面。服务会在下一次请求读取新模型配置，保存模型时无需重启已有服务。
Advanced Runtime 中的 **Python executable** 填解释器绝对路径，Working Directory 填
`agent_service` 目录；不接受 shell 启动命令。Invoke Path 默认 `/graph/invoke/stream`。
如没有配置模型、服务连接失败或 Bridge 未连接，窗口底部会显示状态，可点击 Reconnect 重试。

文本附件会作为文本发送，每个最多 12000 字符、合计最多 40000 字符。
Mac 窗口暂不支持图片分析。它通过 Rhino 自带 Eto / WebKit 显示嵌入的页面，无需 WebView2。

### 窗口与服务测试

```bash
# HTTP/SSE、取消、密钥脱敏、权限、服务复用、真实 Python 自动启动
# 先运行 configure_service.py 或安装脚本配置解释器路径
python3 MacBridge/configure_service.py
dotnet run --project MacBridge/tests/Native/Native.csproj

# 实际 Rhino / WebKit 窗口测试：流式模型、Markdown、安全渲染、工具顺序、长中文消息、画布绑定及历史
# 需要已配置可用的模型（会发出少量模型请求）
python3 MacBridge/tests/ui_smoke.py --rhino rhinocode_remotepipe_实际PID
```

原生测试使用独立临时配置目录，不修改当前模型密钥；窗口测试切换临时画布，并在结束时恢复原画布、删除测试会话。
测试期间请勿手动操作 Magpie 窗口。更新 `.gha` 后需重新启动 Rhino 加载新版程序集。
