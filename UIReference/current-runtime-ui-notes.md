# Current Runtime UI Notes

这份文档描述当前 `ADDGH` 实际运行时的主窗口结构，用于后续把现行 UI 迁移到 `Magpie`。

## 真实来源

当前窗口不是简单加载 `ADDGH/ui.xaml`。

实际运行入口在：

- `ADDGH/ChatWindow.cs`

关键位置：

- 动态解析窗口：`XamlReader.Parse(...)`
- 主题应用：`ApplyThemeMode()`
- 历史侧栏：`HistorySidebar`
- 代码区：`CodeViewBorder`
- 设置浮层：`SettingsOverlay`
- 电池库：`LibraryPanel`

## 和旧 ui.xaml 的差异

旧 `ui.xaml` 里只有：

- 聊天区
- 代码区
- 设置浮层
- 电池库扩展区

当前运行态新增或强化了这些内容：

1. 历史侧栏
   - 当前真实 UI 有 `HistorySidebar`
   - 旧快照没有这部分

2. 动态主题资源
   - 当前通过 `ThemeWindowBackgroundBrush`
   - `ThemeToolbarTextBrush`
   - `ThemePrimaryTextBrush`
   - `ThemeBorderBrush`
   - 等资源统一切换

3. 代码区布局更复杂
   - 不只是单列读代码
   - 还包括画布诊断区和显示模式切换

4. 顶栏按钮结构已升级
   - 新对话
   - 设置
   - 代码视图切换
   - 历史侧栏切换

5. 输入区能力更多
   - 附件预览
   - 图片输入
   - 停止按钮
   - agent mode 切换

## 现行 UI 的迁移优先级

建议拆成三层移植：

1. 外壳层
   - 圆角窗口
   - 顶栏
   - 聊天区
   - 输入区
   - 代码区框架

2. 交互层
   - 新对话
   - 设置浮层
   - 代码区展开收起
   - 历史栏展开收起

3. 数据联动层
   - 画布诊断
   - 代码视图同步
   - 电池库
   - agent 状态

## 给 Magpie 的正确参考结论

如果你要“移植现在的 UI”，正确参考源不是 `ADDGH/ui.xaml`，而是：

1. `ADDGH/ChatWindow.cs` 的动态 XAML 主体
2. `ADDGH/ui.xaml` 的旧视觉基底
3. `ApplyThemeMode()` 对主题资源的运行时覆盖

## 下一步建议

下一步不应该继续整理旧快照，而应该直接做：

1. 从 `ChatWindow.cs` 抽出现行运行态 XAML
2. 生成 `Magpie` 专用窗口壳
3. 先接回发送、状态、host bridge
4. 再补历史栏、代码区和设置浮层
