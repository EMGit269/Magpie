# Magpie Handoff

这套交接包的目标不是解释历史，而是让新的工作空间里的 AI 能直接继续做 `Magpie` 迁移。

## 当前真实状态

- 当前仓库里真正可运行的插件仍是 `ADDGH/`
- 当前仓库里的 `Magpie/` 还不是独立插件工程
- `Magpie/UIReference/` 只是一套 UI 参考与交接资料
- 外部 LangChain 运行时在 `agent_service/`

也就是说，新的 `Magpie` 工作空间如果要继续迁移，不能假设这里已经有一个完整独立的 `Magpie.csproj`。

## 这份交接包包含什么

- `PROMPTS.md`
  - 说明原始 `ADDGH` 的 prompt 体系
  - 说明 `agent_service` 的 LangChain dynamic prompt
  - 说明 mode system skill 注入方式
- `TOOLS.md`
  - 说明原始 `ADDGH` function-calling tools
  - 说明 host bridge tools
  - 说明 `agent_service` 的 local tools
  - 说明哪些只是 UI tool，哪些是正式宿主 tool
- `tool-inventory.json`
  - 机器可读工具索引
- `prompt-inventory.json`
  - 机器可读 prompt / prompt-source 索引

## 新工作空间建议阅读顺序

1. 先读 `TOOLS.md`
2. 再读 `PROMPTS.md`
3. 再读 `tool-inventory.json`
4. 然后对照原始实现：
   - `ADDGH/ChatWindow.ToolDefinitions.cs`
   - `ADDGH/ChatWindow.HostBridge.cs`
   - `ADDGH/Agent/ToolRegistry.cs`
   - `agent_service/app/tools.py`
   - `agent_service/app/runtime.py`

## 迁移边界

新 `Magpie` 建议拆成三层：

1. `Magpie.Plugin`
   - Rhino / Grasshopper 宿主层
   - 负责窗口、host bridge、画布操作执行

2. `Magpie.AgentService`
   - 外部 LangChain / LangGraph / DeepSeek agent 层
   - 负责 prompt、任务管理、context、tool orchestration

3. `Magpie.SharedContracts`
   - tool schema
   - host bridge request / response schema
   - task / session / context schema

## 迁移时最容易出错的点

### 1. 把“原始 function tool”误当成“host bridge tool”

不是同一层。

- `ADDGH/ChatWindow.ToolDefinitions.cs` 里的 tool 数量更多
- `ADDGH/ChatWindow.HostBridge.cs` 里的 host bridge 只是一个裁剪后的对外工具面

### 2. 误以为 `Magpie` 已支持原始 `Maipo` 全量能力

当前没有。

- 原始完整能力主要还在 `ADDGH`
- `agent_service` 只接上了基础 LangChain 运行时和一小部分本地 session/task tools
- host bridge 当前只正式暴露了一波工具

### 3. 把 prompt 简化成普通聊天 prompt

原始系统不是普通聊天窗，而是 GH 工作台 agent。prompt 明确包含：

- GH 建模规则
- 图片任务分流
- C# / Mixed mode 策略
- skills / references / local docs 使用规则
- tool 调用效率约束

### 4. 误删 UI tool

`show_plan_steps` 和 `show_reference_options` 不是普通宿主工具，但它们是原始交互的一部分。新 `Magpie` 如果不保留等价物，Plan / Reference 工作流会断。

## 推荐迁移顺序

1. 先复制 `host bridge` 契约，不要先搬 UI
2. 再把 `agent_service` 里的 local tool、task store、context store 平移出去
3. 再把 `ADDGH` 原始 function-calling schema 抽成 shared contract
4. 最后迁 UI，并把 Plan / Reference 特殊卡片做成新前端组件

## 一句话结论

新的 `Magpie` 工作空间，应该把这里视为“原始宿主 + 外部 agent 原型 + UI 参考 + 交接索引”，而不是已经完成的独立产品。
