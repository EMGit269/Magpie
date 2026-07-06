# Magpie Handoff

`Magpie` 是从原 `ADDGH` 项目中拆出的独立 Grasshopper 插件分支，目标是把原来内嵌在 Rhino / Grasshopper 插件里的 AI 能力，迁移成：

- `Magpie.gha` 作为 Grasshopper 宿主插件
- 外部 `agent_service` 默认按 LangGraph / OpenAI-compatible runtime 接入，旧 LangChain endpoint 保留兼容回退
- 本地 `host bridge` 作为 Grasshopper 能力暴露层

这份文档是给下一个 AI 或开发者的迁移说明。重点不是介绍历史，而是说明：

- `Magpie` 现在是什么状态
- 它和原 `ADDGH` 还残留哪些耦合
- 后续应按什么顺序继续迁移
- 哪些地方可以改，哪些地方改动风险高

## 1. 当前项目是什么

当前 `Magpie/` 已经具备以下性质：

- 可以单独打开 [Magpie.csproj](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\Magpie.csproj)
- 可以单独打开 [Magpie.sln](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\Magpie.sln)
- 目录内自带 [NuGet.config](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\NuGet.config) 和 `nuget-offline/`
- 可以在本地单独执行 `dotnet build .\Magpie.csproj`
- 构建结果是 `Magpie.gha`
- Grasshopper 菜单入口已经独立为直接点击 `Magpie`

目前它本质上仍然是：

- 从 `ADDGH` 复制出来的一个独立可编译分支
- 外部品牌和构建入口已经改成 `Magpie`
- 内部大量源码仍保留 `ADDGH` 命名空间、类名、注释、环境变量历史痕迹

换句话说，`Magpie` 现在是“结构独立，但内部尚未完全去 `ADDGH` 化”的状态。

## 2. 现有架构

当前架构分三层：

1. Grasshopper 插件层

- 插件工程：`Magpie.csproj`
- 菜单入口：[MenuIntegration.cs](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\MenuIntegration.cs)
- 插件信息：[ADDGHInfo.cs](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\ADDGHInfo.cs)

2. 插件内 UI / 宿主执行层

- 主 UI 和大量 Grasshopper 操作逻辑仍集中在 `ChatWindow*.cs`
- Agent Runtime 外部窗口入口在 [AgentRuntime/MagpieWindow.cs](C:\Users\26933\Desktop\Magpie\AgentRuntime\MagpieWindow.cs)
- 外部服务客户端在：
  - [AgentRuntime/MagpieSettings.cs](C:\Users\26933\Desktop\Magpie\AgentRuntime\MagpieSettings.cs)
  - [AgentRuntime/MagpieServiceClient.cs](C:\Users\26933\Desktop\Magpie\AgentRuntime\MagpieServiceClient.cs)

3. Host bridge / tool 暴露层

- 本地 bridge 生命周期与 facade：
  - [GrasshopperHost.cs](C:\Users\26933\Desktop\Magpie\GrasshopperHost.cs)
  - [Host/MagpieHostBridgeBackend.cs](C:\Users\26933\Desktop\Magpie\Host\MagpieHostBridgeBackend.cs)
- 参数验证与 tool spec 在：
  - [HostBridge/HostBridgeModels.cs](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\HostBridge\HostBridgeModels.cs)
  - [HostBridge/HostBridgeValidation.cs](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\HostBridge\HostBridgeValidation.cs)

## 3. Agent Runtime 相关现状

当前 `Magpie` 已经能作为外部 agent UI 的插件壳使用。插件侧默认按 LangGraph runtime 调用外部服务，但仍保留旧 LangChain endpoint 的兼容回退。

已经具备：

- 独立窗口 `MagpieWindow`
- 对外部 `agent_service` 的 HTTP 调用
- 本地 host bridge
- 一批已经 formalize 的 Grasshopper tools
- `DeepSeek / OpenAI-compatible` 这类服务端方向已经在外部 `agent_service` 里走通

当前 runtime 调用规则：

- 默认 `MAGPIE_AGENT_RUNTIME=langgraph`
- 默认优先调用 `/graph/invoke`，再尝试 `/langgraph/invoke`；`agent_service` 已暴露这两个端点作为 `/agent/invoke` 的别名
- 如果服务端未实现上述路径，插件会对 404 / 405 回退到旧 `/agent/invoke` 或 `/workflow/run`
- 如果你的服务端 LangGraph 路径不同，可设置 `MAGPIE_AGENT_INVOKE_PATH=/your/path`
- 如果需要临时回旧模式，可设置 `MAGPIE_AGENT_RUNTIME=langchain`
- 如果希望打开 Magpie 时自动启动服务，可设置 `MAGPIE_AGENT_SERVICE_COMMAND`
- 如果启动命令需要指定目录，可设置 `MAGPIE_AGENT_SERVICE_WORKDIR`
- 也可以直接在 Magpie 设置面板里填写 `Runtime URL`、`Start Command`、`Working Directory`、`Invoke Path` 并保存；插件会优先读取这些设置

示例：

```powershell
setx MAGPIE_AGENT_SERVICE_URL "http://127.0.0.1:8000"
setx MAGPIE_AGENT_SERVICE_WORKDIR "C:\path\to\agent_service"
setx MAGPIE_AGENT_SERVICE_COMMAND "python -m uvicorn app:app --host 127.0.0.1 --port 8000"
```

尚未完成：

- `Magpie` 内部命名空间仍大量使用 `ADDGH`
- `host bridge` 仍挂在 `ChatWindow` partial class 上，不是独立宿主服务类
- 工具执行逻辑仍然和旧 UI / 旧插件代码强耦合
- LangGraph / agent 编排策略主要还在外部 `agent_service`，插件侧尚未收敛成更稳定 contract
- `Magpie` 还不是“只保留宿主职责”的最简插件

## 4. 已完成的独立化工作

已完成的事项：

- 新建独立目录 `Magpie/`
- 独立 `Magpie.csproj`
- 独立 `Magpie.sln`
- 独立 `NuGet.config`
- 复制离线包到 `Magpie/nuget-offline/`
- 插件 AssemblyName 改为 `Magpie`
- 菜单入口改为直接点击 `Magpie`
- Agent Runtime 窗口品牌改为 `Magpie`
- 若 Rhino 锁定默认输出目录，可用 `OutDir` 单独编译
- 第一批运行前缀已切到 `MAGPIE_*`

已改过的外部运行标识包括：

- `MAGPIE_LOG_VERBOSE`
- `MAGPIE_ERROR_POPUP`
- `MAGPIE_PLAINTEXT_API_KEYS`
- `MAGPIE_PROJECT_ROOT`
- `MAGPIE_HTTPS_PROXY`
- `MAGPIE_HTTP_PROXY`
- 若干 `MAGPIE_paste_ / MAGPIE_drop_ / MAGPIE_restore_` 临时文件前缀

## 5. 目前最重要的遗留问题

### 5.1 命名空间未清理

这是最明显的遗留问题。

当前很多文件仍然是：

- `namespace ADDGH`
- `namespace ADDGH.Agent`
- `namespace ADDGH.HostBridge`
- `namespace ADDGH.LangChain`

这不影响构建，但会带来几个问题：

- 后续 AI 很容易误以为仍在改原项目
- 代码搜索和迁移边界不清晰
- 外部交付时不专业
- 后续拆 DLL 或子项目时命名冲突概率高

### 5.2 Host bridge 已从 ChatWindow 解耦（已完成）

`GrasshopperHost` 现在持有 bridge 生命周期，`MagpieHostBridgeBackend` 已把所有 bridge tools 委托给 `GrasshopperDocumentHost`。`ChatWindow` 不再直接执行任何 bridge tool。

### 5.3 Tool 执行已迁移到宿主层（first-wave 及主要工具已完成）

`ExecuteGetGhComponents`、`ExecuteCreateComponentGraph`、`ExecuteCheckGhErrors` 等执行逻辑已迁移到：

- [Host/GrasshopperDocumentHost.Tools.cs](C:\Users\26933\Desktop\Magpie\Host\GrasshopperDocumentHost.Tools.cs)

`ChatWindow.GhTools.Execution.cs` 中对应重复实现已删除，仅保留 Code Surface / 视觉复核等非 bridge 工具的辅助方法。

### 5.4 README 之外的正式迁移文档还不够

如果未来这个目录完全搬走，接手者仍需要快速知道：

- 哪些文件是“先别动”的
- 哪些文件是 Agent Runtime migration 核心
- 哪些部分只是历史遗留，不是当前优先级

## 6. 建议的后续迁移顺序

不要同时做所有事情。推荐按下面顺序推进。

### 阶段 A：代码身份清理

目标：让 `Magpie` 从“复制品”变成“命名明确的独立项目”

建议操作：

1. 把命名空间系统性改成：
   - `Magpie`
   - `Magpie.Agent`
   - `Magpie.HostBridge`
   - `Magpie.AgentRuntime`
2. 把 `using ADDGH.*` 改成 `using Magpie.*`
3. 评估是否把 [ADDGHInfo.cs](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\ADDGHInfo.cs) 改名为 `MagpieInfo.cs`
4. 清理注释和用户可见文本中的 `ADDGH`
5. 清理本地目录常量中的 `ADDGH`

阶段 A 的收益最大，而且对功能影响最小。

### 阶段 B：Bridge 与执行层解耦（已完成）

目标：让 `host bridge` 不再依附 `ChatWindow`

当前状态：

- `GrasshopperHost` 持有 bridge 生命周期
- `MagpieHostBridgeBackend` 把所有 tools 委托给 `GrasshopperDocumentHost`
- `ChatWindow` 的 UI 调用者也改走 `GrasshopperDocumentHost`
- `ChatWindow.GhTools.Execution.cs` 中对应重复实现已删除

后续如需继续收敛，可进一步把 `HostBridge/GrasshopperHostToolExecutor.cs` 里的 tool dispatch 也合并进宿主层，并把 `ChatWindow.GhTools.Execution.cs` 里剩余的非 bridge helper 继续瘦身。

### 阶段 C：正式 LangGraph 适配

目标：让 `Magpie` 真正成为“LangGraph runtime 的 Grasshopper 宿主”

建议操作：

1. 固化 bridge contract
   - tool manifest version
   - invoke request / response schema
   - structured result contract
2. 让 `agent_service` 只依赖这些正式 contract，不再依赖历史兼容行为
3. 在 `Magpie` 侧明确 tool surface policy
   - 哪些是 read-only
   - 哪些是 mutation
   - 哪些需要用户确认
4. 后续继续保持插件协议稳定，尽量只在 `agent_service` 内调整 graph 编排

关键原则：

- 插件侧负责“宿主能力暴露”
- `agent_service` 负责“LangGraph / planner / memory / tool orchestration”
- 不要把 agent 编排逻辑重新塞回 `.gha`

### 阶段 D：UI 收敛

目标：决定 `Magpie` 最终保留哪个 UI

当前可选方向：

1. 保留 `MagpieWindow` 作为轻量 native 窗口
2. 用 WebView / HTML UI 取代大部分原 `ChatWindow`
3. 让 Grasshopper 只负责打开一个宿主窗口，主交互放到网页或外部前端

建议：

- 如果目标是正式外部 agent 架构，优先保留 `MagpieWindow` 作为最小 native 壳
- 大量复杂 UI 不建议继续在旧 `ChatWindow` 上迭代

### 阶段 E：发布与安装彻底独立

目标：把 `Magpie` 变成可单独交付的产品目录

建议操作：

1. 增加独立发布脚本
2. 输出独立 `release/Magpie/`
3. 增加安装文档：
   - 插件安装
   - `agent_service` 安装
   - `.env` 配置
   - Rhino / Grasshopper 运行说明
4. 让“插件包”和“服务包”明确分离

## 7. 下一个 AI 最应该优先看的文件

接手时建议优先阅读：

1. 构建与入口

- [Magpie.csproj](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\Magpie.csproj)
- [MenuIntegration.cs](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\MenuIntegration.cs)
- [ADDGHInfo.cs](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\ADDGHInfo.cs)

2. Agent Runtime 外部窗口

- [AgentRuntime/MagpieWindow.cs](C:\Users\26933\Desktop\Magpie\AgentRuntime\MagpieWindow.cs)
- [AgentRuntime/MagpieServiceClient.cs](C:\Users\26933\Desktop\Magpie\AgentRuntime\MagpieServiceClient.cs)
- [AgentRuntime/MagpieSettings.cs](C:\Users\26933\Desktop\Magpie\AgentRuntime\MagpieSettings.cs)

3. Host bridge

- [GrasshopperHost.cs](C:\Users\26933\Desktop\Magpie\GrasshopperHost.cs)
- [Host/MagpieHostBridgeBackend.cs](C:\Users\26933\Desktop\Magpie\Host\MagpieHostBridgeBackend.cs)
- [HostBridge/HostBridgeModels.cs](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\HostBridge\HostBridgeModels.cs)
- [HostBridge/HostBridgeValidation.cs](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\HostBridge\HostBridgeValidation.cs)

4. Grasshopper tool execution

- [ChatWindow.GhTools.Execution.cs](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\ChatWindow.GhTools.Execution.cs)
- [ChatWindow.ToolDispatch*.cs](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie)

5. 运行配置

- [DeploymentOptions.cs](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\DeploymentOptions.cs)
- [AddGhLog.cs](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\AddGhLog.cs)
- [ChatWindow.RuntimeConfig.cs](C:\Users\26933\.codex\worktrees\fdf4\DEMOagent\Magpie\ChatWindow.RuntimeConfig.cs)

## 8. 建议不要优先做的事

下面这些事短期内不建议优先做：

- 不要先大改 UI 风格
- 不要先把所有旧 `ChatWindow` 代码删掉
- 不要先引入更多模型 provider 分支
- 不要先把 agent 逻辑塞回插件内部
- 不要先追求“命名空间全改”同时又“bridge 重构”同时又“tool 执行抽象”，这样容易炸

## 9. 推荐的接手执行清单

接手 AI 建议直接按这个顺序执行：

1. 全量扫描 `Magpie/` 下的 `ADDGH` 残留
2. 先改命名空间和 `using`
3. 再把 `HostBridge` 从 `ChatWindow` 拆出去
4. 再把 first-wave tools 提炼成独立 executor
5. 再检查 `agent_service` 是否只依赖 formal tools
6. 最后再考虑 UI 简化或 WebView 化

## 10. 当前已验证结论

截至这份文档写入时，已经验证：

- `Magpie.csproj` 可以独立编译
- `Magpie` 作为独立工程可继续开发
- 运行标识前缀已经开始从 `ADDGH_*` 迁移到 `MAGPIE_*`

但还没有完成：

- 完整 namespace 重命名
- 完整 host bridge 解耦
- 完整 LangGraph tool/runtime 正式化
- 完整发布包拆分

## 11. 给下一个 AI 的简短指令

如果你是接手这个目录的 AI，建议先做：

1. 只在 `Magpie/` 内工作，不要再改 `ADDGH/`
2. 把 `Magpie` 当成独立产品，而不是原项目子模块
3. 优先做“内部独立化”，再做“LangGraph 正式适配”
4. 迁移时每一批都重新 `dotnet build Magpie\Magpie.csproj`

最重要的一句话：

`Magpie` 现在已经不是“能不能独立编译”的问题，而是“如何把复制出来的旧宿主代码，逐步收敛成一个正式的 LangGraph 外部宿主插件”。  
