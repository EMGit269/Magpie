# UI_SPEC

这份文档描述当前 `ADDGH` 实际运行中的主窗口 UI 契约，目标是让新的 AI 或新的开发者在不了解历史代码的情况下，仍然能正确理解界面结构、交互行为和样式逻辑。

适用范围：

- 当前 `ADDGH` 主窗口
- 后续 `Magpie` UI 迁移

权威来源：

- [ChatWindow.cs](C:/Users/26933/.codex/worktrees/fdf4/DEMOagent/ADDGH/ChatWindow.cs:1)
- [ui.xaml](C:/Users/26933/.codex/worktrees/fdf4/DEMOagent/ADDGH/ui.xaml:1)

注意：

- 当前运行态 UI 以 `ChatWindow.cs` 中 `XamlReader.Parse(...)` 构建的动态 XAML 为准，不以旧 `ui.xaml` 为准。

## 1. 产品定位

这个窗口不是单纯聊天框，而是一个 Grasshopper AI 工作台。它同时承载：

- 对话
- 工具调用
- 画布状态检查
- 代码/图结构查看
- 历史会话恢复
- 设置管理

所以任何新实现都不能把它误简化成“一个输入框 + 一个消息列表”。

## 2. 全局布局

当前运行态是四列结构：

1. 历史侧栏列
2. 聊天主列
3. 分隔列
4. 代码区列

关键尺寸常量定义在 [ChatWindow.cs](C:/Users/26933/.codex/worktrees/fdf4/DEMOagent/ADDGH/ChatWindow.cs:31)：

- `DefaultWindowWidth = 410`
- `ChatPaneMinWidth = 386`
- `CodePaneMinWidth = 450`
- `CodeViewColumnWidth = 750`
- `HistorySidebarWidth = 320`

## 3. 主要区域

### 3.1 顶部工具栏

来源：

- [ChatWindow.cs](C:/Users/26933/.codex/worktrees/fdf4/DEMOagent/ADDGH/ChatWindow.cs:2041)

组成：

- `BtnToggleHistory`
- `BtnNewChat`
- 标题文本 `Maipo`
- `BtnToggleViewMode`
- `BtnSettings`
- `BtnToggleCode`

视觉特征：

- 扁平工具按钮
- 主要使用描边图标
- 主题前景色来自 `ThemeToolbarTextBrush`

### 3.2 历史侧栏

来源：

- [ChatWindow.cs](C:/Users/26933/.codex/worktrees/fdf4/DEMOagent/ADDGH/ChatWindow.cs:2082)

关键控件：

- `HistorySidebar`
- `TxtHistoryCount`
- `BtnCloseHistory`
- `HistoryListPanel`

功能：

- 展示本地保存的会话
- 点击后恢复会话
- 可被工具栏按钮打开
- 可被右上角 `✕` 关闭

默认状态：

- `Collapsed`

### 3.3 聊天消息区

关键控件：

- `ChatScroll`
- `ChatPanel`
- `StickyUserMessageHost`
- `StickyUserMessageStack`

功能：

- 展示消息流
- 顶部可出现粘性用户消息
- 中央空态时显示大号提示语

空态控件：

- `EmptyChatPrompt`

### 3.4 输入区

来源：

- [ChatWindow.cs](C:/Users/26933/.codex/worktrees/fdf4/DEMOagent/ADDGH/ChatWindow.cs:2116)

关键控件：

- `InputChromeBorder`
- `AttachmentPreviewPanel`
- `TxtInput`
- `BtnUploadImage`
- `BtnStop`
- `BtnAgentModeDropdown`
- `ContextMeterHost`
- `BtnSend`

功能：

- 输入多行文本
- 粘贴图片/附件
- 切换 agent mode
- 生成中显示停止按钮
- 显示上下文使用环
- 发送消息

### 3.5 电池库区域

关键控件：

- `LibraryRow`
- `LibraryPanel`
- `BtnToggleLibrary`

功能：

- 在主窗口底部展开电池库区域
- 不是独立窗口
- 展开/收起通过行高切换实现

默认状态：

- 收起

### 3.6 代码区

来源：

- [ChatWindow.cs](C:/Users/26933/.codex/worktrees/fdf4/DEMOagent/ADDGH/ChatWindow.cs:2262)

关键控件：

- `CodeViewBorder`
- `CanvasPane`
- `InspectorPane`
- `RichCodeView`
- `CodeCanvasIssuesHost`
- `TxtCanvasIssues`
- `BtnCanvasPaneView`
- `BtnInspectorPaneView`
- `BtnCanvasSync`
- `ChatCodeSplitter`

功能：

- 展示 Graph Logic / JSON / 代码文本
- 展示画布诊断
- 在不同视图间切换
- 允许和聊天区分栏拖拽

默认状态：

- 隐藏

### 3.7 设置浮层

关键控件：

- `SettingsOverlay`
- `SettingsPanel`

内部主要块：

- 主模型设置
- 视觉模型设置
- 生图模型设置
- 电池库设置

打开方式：

- `BtnSettings`

关闭方式：

- 点击关闭按钮
- 保存
- 某些取消路径

默认状态：

- `Collapsed`

## 4. 核心交互契约

### 4.1 打开/关闭历史侧栏

状态变量：

- `_isHistorySidebarVisible`

关键逻辑：

- `BtnToggleHistory` 调 `ToggleHistorySidebar()`
- `BtnCloseHistory` 调 `SetHistorySidebarVisible(false)`

行为要求：

- 侧栏可覆盖或占位，取决于窗口宽度
- 打开后需要刷新历史列表
- 工具栏底部分隔线可能出现

### 4.2 打开/关闭代码区

状态变量：

- `_isCodeVisible`

关键逻辑：

- `BtnToggleCode` 切换 `_isCodeVisible`
- 打开时会扩展窗口宽度
- 关闭时恢复原先宽度

行为要求：

- 打开代码区不是弹窗，而是右侧展开
- 打开后应同步代码视图内容
- 若历史侧栏已打开，布局要一起重算

### 4.3 打开/关闭电池库

状态变量：

- `_isLibraryVisible`

关键逻辑：

- `BtnToggleLibrary` 切换
- 通过 `LibraryRow.Height` 在 `0` 和 `400` 之间切换

行为要求：

- 电池库是主布局中的下拉扩展区
- 不是悬浮弹窗

### 4.4 打开/关闭设置浮层

关键逻辑：

- `SetSettingsOverlayVisible(bool visible)`

行为要求：

- 设置是覆盖层，不是新窗口
- 打开后应保留底层布局，但阻断主要编辑流程
- 显示时需要重新应用主题

### 4.5 发送与停止

状态变量：

- `_isGenerating`

关键逻辑：

- `BtnSend_Click(...)`
- 生成中再次点发送，会转成停止/取消行为

行为要求：

- 生成开始后：
  - 禁用一部分模式切换按钮
  - 更新发送按钮样式
  - 显示 `BtnStop`
- 生成结束后恢复

### 4.6 新对话

关键控件：

- `BtnNewChat`

行为要求：

- 清空当前对话上下文
- 重新初始化消息
- 若历史侧栏打开，刷新历史列表

## 5. 状态机

至少要理解下面这些状态，不然 AI 很容易改坏：

### 5.1 默认态

- 历史侧栏关闭
- 代码区关闭
- 电池库关闭
- 设置浮层关闭
- 未生成

### 5.2 生成中态

- `_isGenerating = true`
- 发送按钮语义改变
- 停止按钮可见
- 部分模式按钮禁用

### 5.3 历史展开态

- `_isHistorySidebarVisible = true`
- 侧栏展示本地会话列表
- 布局可能从占位切到覆盖

### 5.4 代码区展开态

- `_isCodeVisible = true`
- 窗口宽度增大
- 分隔条可见
- 右侧代码区域显示

### 5.5 设置浮层态

- `SettingsOverlay.Visible`
- 浮层覆盖主界面

### 5.6 电池库展开态

- `_isLibraryVisible = true`
- `LibraryRow.Height = 400`

## 6. 样式逻辑

### 6.1 当前 UI 不是固定深色主题

主题模式：

- `Dark`
- `Light`
- `System`

来源：

- [ChatWindow.cs](C:/Users/26933/.codex/worktrees/fdf4/DEMOagent/ADDGH/ChatWindow.cs:177)

### 6.2 关键主题资源

来源：

- [ChatWindow.cs](C:/Users/26933/.codex/worktrees/fdf4/DEMOagent/ADDGH/ChatWindow.cs:687)

必须认这些变量，而不是写死颜色：

- `ThemeWindowBackgroundBrush`
- `ThemeToolbarTextBrush`
- `ThemePrimaryTextBrush`
- `ThemeSecondaryTextBrush`
- `ThemeBorderBrush`
- `ThemePanelBrush`
- `ThemeSurfaceBrush`
- `ThemeInputBrush`
- `ThemeHoverBrush`
- `ThemePressedBrush`
- `ThemeSelectedSurfaceBrush`
- `ThemeScrollbarThumbBrush`
- `ThemeOverlayBrush`

### 6.3 视觉语言

当前 UI 风格不是重玻璃、不是强拟物，属于：

- 干净的桌面生产力工具
- 较轻的圆角
- 中等密度
- 浅色主题优先，但支持暗色
- 顶栏图标细描边
- 输入区是高权重卡片

### 6.4 形状与间距特征

重要特征：

- 外层窗口为桌面工具窗口，不是网页风大卡片
- 输入区圆角更大，强调主操作区
- 工具按钮圆角较小
- 区块之间靠细边框和弱背景差异分层

## 7. 控件语义映射

下面这些控件名在迁移时不要随便改，因为它们本身就承载了语义：

- `BtnToggleHistory`: 打开历史
- `BtnNewChat`: 新对话
- `BtnSettings`: 打开设置
- `BtnToggleCode`: 打开代码区
- `BtnToggleViewMode`: 切换代码视图模式
- `BtnUploadImage`: 添加附件或参考
- `BtnStop`: 中断生成
- `BtnAgentModeDropdown`: 切换 agent 行为模式
- `BtnSend`: 发送
- `HistorySidebar`: 历史栏容器
- `LibraryPanel`: 电池库容器
- `CodeViewBorder`: 代码区容器
- `SettingsOverlay`: 设置浮层
- `SettingsPanel`: 设置主体

## 8. 给新 AI 的硬性规则

如果新的 AI 要重写或移植这套 UI，必须遵守：

1. 不要把当前 UI 误判成纯聊天窗
2. 不要只参考旧 `ui.xaml`
3. 不要删掉历史侧栏、代码区、设置浮层这三类结构
4. 不要把主题逻辑写死成固定深色
5. 不要把所有按钮都重做成网页式大按钮
6. 不要把电池库和设置面板改成完全脱离主布局的新窗口，除非有明确产品决定

## 9. 迁移到 Magpie 的最小可接受方案

第一阶段最小方案应保留：

- 顶栏
- 消息区
- 输入区
- 设置浮层壳子
- 代码区壳子
- 基础主题资源

可以暂缓的部分：

- 历史恢复完整逻辑
- 代码区数据联动
- 电池库内容同步
- 全部 provider 设置项

## 10. 推荐阅读顺序

给新的 AI 或开发者时，按这个顺序喂：

1. 本文 `UI_SPEC.md`
2. [current-runtime-ui-notes.md](C:/Users/26933/.codex/worktrees/fdf4/DEMOagent/Magpie/UIReference/current-runtime-ui-notes.md:1)
3. [ChatWindow.cs](C:/Users/26933/.codex/worktrees/fdf4/DEMOagent/ADDGH/ChatWindow.cs:2030)
4. [ui.xaml](C:/Users/26933/.codex/worktrees/fdf4/DEMOagent/ADDGH/ui.xaml:1)

## 11. 一句话总结

当前 UI 的本质是：

一个支持主题切换、历史会话、代码视图、设置浮层和 Grasshopper 工作流联动的桌面式 AI 工作台，而不是普通聊天面板。
