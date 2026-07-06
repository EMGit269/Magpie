# Prompt Inventory

本文只整理“对迁移有决定性影响”的 prompt 体系，不讨论普通 UI 文案。

## 1. 原始 ADDGH 主系统 Prompt

权威源：

- `ADDGH/ChatWindow.cs`
- 常量：`SYSTEM_PROMPT`
- 构建入口：`BuildSystemPrompt()`

## 1.1 Prompt 组装顺序

原始 `ADDGH` 的 system prompt 不是单段常量，而是按下面顺序组装：

1. `SYSTEM_PROMPT`
2. `BuildModePrompt(_layoutMode)`
3. `GetModeSystemSkillPrompt()`
4. 如果是执行态且 `CSharpFirst`
   - `BuildCSharpDedicatedToolPrompt()`
5. 如果是 `SelfTrain`
   - `BuildSelfTrainingPrompt()`

之后系统消息还会经过 `ContextPipeline` 再拼：

1. base prompt
2. typed prompt
3. context pack
4. context ledger
5. skill summary

权威源：

- `ADDGH/Agent/ContextPipeline.cs`
- `ADDGH/ChatWindow.cs`
- `ADDGH/ChatWindow.AgentRouting.cs`

## 1.2 原始 SYSTEM_PROMPT 的主题块

原始 `SYSTEM_PROMPT` 不是简单角色设定，而是完整工作规则。核心块如下：

### A. 建模逻辑

- 先对齐需求和约束，再规划数据流和关键电池，再修改画布
- 强约束普通电池命名与 Slider 命名
- 最终回复必须结构化 Markdown
- reference 使用必须发生在方案确定后
- skills 使用必须发生在建模规划前
- `web_research` 只能读取本机镜像文档，不访问公网
- 完成建模后必须检查关键输出，不能只看“无报错”
- 尺寸任务必须先核对 Rhino 文档单位

### B. 多模态图片任务路由

- 上传图片不等于要建模
- 视觉分析只是线索，不是最终事实
- 纯图片创作应走 `create_ai_image`
- 图片理解/诊断不应自动变成 GH 操作
- 参考图建模才进入 GH 工具链

### C. 工具调用效率

- 默认优先使用短号 `01/02/03` 这类对外 id
- 新增一整块逻辑时优先 `create_component_graph`
- 尽量减少碎片化 add/connect 循环
- C# Script 有专门规则
- 每次 function call 都应填写 `summary`
- 尽量批量、直接行动

### D. 对用户表达

- 直接说明改动内容、影响范围、待确认信息
- 避免暴露内部函数名和 API 名

## 1.3 Mode System Skill 注入

权威源：

- `skills/system_mixed_mode.md`
- `skills/system_csharp_mode.md`
- `ADDGH/ChatWindow.cs` 的 `GetModeSystemSkillPrompt()`

### `system_mixed_mode.md`

作用：

- 当 layout mode 为 `Mixed`
- 指导模型在原生 GH 电池和 C# Script 之间做边界选择

核心策略：

- 简单直观逻辑优先原生 GH
- 冗长、脆弱、重复、算法性逻辑优先 C#
- 不强迫全电池，也不强迫全 C#

### `system_csharp_mode.md`

作用：

- 当 layout mode 为 `C# 优先`
- 强化“核心逻辑必须进一两个 C# Script”这一约束

核心策略：

- 非 script 组件只允许做 `Params` / `Display` 辅助
- 不允许长原生 GH 链替代核心脚本逻辑
- 规划、编辑、分组、验证都有专门限制

## 2. ADDGH Context Pipeline Prompt

权威源：

- `ADDGH/Agent/ContextPipeline.cs`

该层不定义业务规则，但定义了 prompt 拼装结构：

- `BasePromptProvider`
- `TypedPromptProvider`
- `ContextPackProvider`
- `ContextLedgerProvider`
- `SkillSummaryProvider`

迁移时建议不要把这些再塞回一个超长字符串，而是保留成可单测、可观察的多段策略层。

## 3. agent_service LangChain Dynamic Prompt

权威源：

- `agent_service/app/runtime.py`
- `_build_prompt_middleware()`

这是外部 agent 当前真正使用的 system prompt 生成器。

### 3.1 Prompt 结构

它是一个 `dynamic_prompt` 中间件，按 turn 动态生成，主要包含：

1. 角色说明
   - `You are the external ADDGH agent.`
   - 只有插件宿主允许修改 Rhino / Grasshopper 状态
   - 需要画布状态或修改时必须用 host bridge tools

2. Session 元信息
   - `session_id`
   - `user_goal`

3. Session facts
   - 来自 `SessionContextStore`

4. Open tasks
   - 来自 `TaskStore`

5. Recent messages
   - 最近最多 8 条

6. Host tool count

7. 收尾行为要求
   - 简洁规划
   - 维护 task list
   - 在宣称完成前校验 host 结果

### 3.2 这层和原始 ADDGH Prompt 的关系

当前它不是原始 `SYSTEM_PROMPT` 的等价迁移版，只是外部 agent 的基础运行 prompt。

也就是说：

- 它有 session/task/context 能力
- 但没有完整搬运原始 GH 建模规则、视觉路由、mode 技能注入

这正是新 `Magpie` 需要继续迁移的重点。

## 4. agent_service Workflow Fallback

权威源：

- `agent_service/app/workflow.py`

当模型未配置时，LangChain 窗口会退到 `/workflow/run`。

这条线不是 prompt-driven agent，而是：

- `MinimalPlanner`
- `MinimalVerifier`
- 固定流程读取画布和错误状态

它当前只适合作为保底，不是正式主 agent 架构。

## 5. 其它需要保留的 prompt-like 常量

### `LangChainSettings.DefaultUserGoal`

源：

- `ADDGH/LangChain/LangChainSettings.cs`

值：

- `Use the separated LangChain window to work with Grasshopper through the local host bridge.`

作用：

- 作为外部窗口默认 goal

### `LangChainSettings.WindowSubtitle`

源：

- `ADDGH/LangChain/LangChainSettings.cs`

值：

- `External agent runtime with local Grasshopper host tools`

作用：

- UI 提示，不是核心 prompt

## 6. 给新工作空间 AI 的建议

如果要做正式 `Magpie`，建议把 prompt 体系重建成四层：

1. Stable base prompt
   - GH 建模、图片路由、验证原则、用户表达

2. Mode policy prompt
   - Mixed / CSharpFirst / 未来其它 mode

3. Context prompt
   - goal、facts、recent messages、task list、host capability summary

4. Skill/reference injections
   - 候选 skill 摘要
   - reference index 摘要
   - 必要时才注入正文

## 7. 一句话结论

当前外部 LangChain 版本只迁了“session/task/context 的 prompt 外壳”，还没有完整迁移原始 `ADDGH` 的 GH 工作台级 prompt 规则。
