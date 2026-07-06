# Tool Inventory

本文把当前体系分成三层：

1. 原始 `ADDGH` function-calling tools
2. host bridge tools
3. `agent_service` local tools

不要把这三层混为一谈。

## 1. 原始 ADDGH Function-Calling Tools

权威源：

- `ADDGH/ChatWindow.ToolDefinitions.cs`

这些是原始内嵌 agent 直接暴露给模型的工具面。

### 1.1 画布与建模工具

- `ensure_gh_canvas`
- `get_gh_components`
- `add_gh_component`
- `connect_gh_components`
- `remove_gh_component`
- `set_gh_component_value`
- `remove_gh_connection`
- `create_component_graph`
- `recompute_gh_canvas`
- `check_gh_errors`
- `set_gh_component_status`
- `set_all_csharp_script_previews`
- `modify_gh_component_ports`
- `manage_gh_groups`
- `modify_gh_port_data`

### 1.2 C# Script 相关工具

- `create_csharp_script_component`
- `edit_csharp_script_component`
- `create_script_component_graph`
- `gh_native_script_editor`
- `read_component_script`
- `get_component_context`
- `query_gh_components`

### 1.3 搜索 / 知识 / 参考工具

- `search_component_library`
- `search_gh_component_catalog`
- `web_research`
- `read_skill_file`
- `create_gh_skill`
- `read_reference_json`
- `import_reference_gh`

### 1.4 特殊 UI / 交互工具

- `show_reference_options`
- `show_plan_steps`

### 1.5 图片工具

- `create_ai_image`

## 2. 原始 ToolRegistry 里的 Workflow 语义

权威源：

- `ADDGH/Agent/ToolRegistry.cs`
- `ADDGH/Agent/WorkflowIntent.cs`

`ToolRegistry` 给工具补了额外语义，不只是名字：

- 是否只读
- 是否修改画布
- 是否写文件
- intended workflows
- lifecycle 是否 deferred

### 2.1 重要的 WorkflowIntent

- `GeneralChat`
- `GrasshopperCreate`
- `GrasshopperModify`
- `CSharpScriptCreate`
- `CSharpScriptFix`
- `VisualUnderstanding`
- `VisualModeling`
- `AiImageGeneration`
- `SkillLookup`
- `SkillAuthoring`
- `ReferenceLookup`
- `ReferenceImport`
- `SelfTraining`
- `ApiDocLookup`
- `WebResearch`

迁移时建议把这套 workflow 标签保留下来，不要退化成“所有工具平铺给 agent”。

## 3. Host Bridge Tools

权威源：

- `ADDGH/ChatWindow.HostBridge.cs`
- `ADDGH/ChatWindow.HostBridgeServer.cs`
- `ADDGH/HostBridge/HostBridgeValidation.cs`

这是外部 agent 通过 HTTP 调用宿主时能看到的工具面。

HTTP 接口：

- `GET /health`
- `GET /tools/manifest`
- `POST /tools/invoke`

当前 manifest 中的 host tools 只有这些：

- `get_canvas_summary`
- `query_components`
- `get_component_context`
- `read_component_script`
- `check_gh_errors`
- `recompute_canvas`
- `connect_components`
- `remove_component`
- `set_component_value`
- `create_component_graph`
- `create_csharp_script`
- `edit_csharp_script`

### 3.1 这层和原始 ADDGH tools 的对应关系

- `get_canvas_summary` ~= `get_gh_components`
- `query_components` ~= `query_gh_components`
- `get_component_context` ~= `get_component_context`
- `read_component_script` ~= `read_component_script`
- `check_gh_errors` ~= `check_gh_errors`
- `recompute_canvas` ~= `recompute_gh_canvas`
- `connect_components` ~= `connect_gh_components`
- `remove_component` ~= `remove_gh_component`
- `set_component_value` ~= `set_gh_component_value`
- `create_component_graph` ~= `create_component_graph`
- `create_csharp_script` ~= `create_csharp_script_component`
- `edit_csharp_script` ~= `edit_csharp_script_component`

注意：

- host bridge 不是原始 tool 的全量镜像
- 有些原始工具目前根本没暴露给外部 agent
- 命名也经过了裁剪和改写

### 3.2 First-Wave Formal Tools

当前只有下面这些做了更正式的 schema 和结构化结果规整：

- `get_canvas_summary`
- `query_components`
- `get_component_context`
- `create_component_graph`
- `check_gh_errors`

它们会：

- 走 `HostBridgeValidation`
- 返回 `structured = true`
- 返回 `version = host-tool-v1`

其它 host tools 目前仍偏 loose schema。

## 4. agent_service Local Tools

权威源：

- `agent_service/app/tools.py`
- `agent_service/app/schemas.py`

这些工具不碰 Rhino / GH，只负责外部 agent 的 session 状态。

- `list_tasks`
- `add_task`
- `update_task`
- `get_session_context`
- `set_session_fact`

### 4.1 作用

- 维护 session task list
- 维护 session facts
- 让 external agent 能持续记住目标、任务、最近消息、host capabilities

### 4.2 当前局限

这层是有用的，但它不是原始 `Maipo` 能力本体，只是 orchestration 支撑层。

## 5. 特殊 UI Tools 说明

### `show_plan_steps`

源：

- `ADDGH/ChatWindow.PlanSteps.cs`

它不是宿主画布 mutation tool，而是：

- 在聊天区渲染大号计划卡片
- 展示 steps
- 允许用户点击一个底部执行按钮
- 点击后切换到 Create 模式并发送 `execute_prompt`

如果新 `Magpie` 不保留这个等价交互，Plan 模式体验会断。

### `show_reference_options`

源：

- `ADDGH/ChatWindow.ReferenceOptions.cs`

它用于：

- 在聊天区展示 5 条候选说明
- 用户点选或自定义
- 然后保存 reference JSON / 更新 reference 相关资产

如果新前端不保留它，reference 创建流程会断。

## 6. 给新工作空间 AI 的迁移建议

建议把工具契约重建成三份显式 schema：

1. `Magpie.SharedContracts/AddghFunctionTools`
   - 保留原始完整 tool surface

2. `Magpie.SharedContracts/HostBridgeTools`
   - 保留对外 HTTP 契约

3. `Magpie.SharedContracts/AgentLocalTools`
   - 保留 task/context/fact/session 层

不要再把工具定义埋在：

- WPF UI 文件
- 巨型 `ChatWindow` partial class
- Python runtime 的动态闭包里

## 7. 一句话结论

现在真正“全量”的工具面仍在 `ADDGH/ChatWindow.ToolDefinitions.cs`，host bridge 只是外部迁移的第一波裁剪面，`agent_service` local tools 只是 orchestration 辅助层。
