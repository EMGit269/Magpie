# Magpie UI Reference

这个目录现在分成两类参考：

- `original-ui.xaml`
  - 早期 `ADDGH/ui.xaml` 静态快照
  - 只能作为旧布局参考
- `current-runtime-ui-notes.md`
  - 当前真正运行中的 UI 结构说明
  - 以 `ADDGH/ChatWindow.cs` 里的运行时代码为准

## 重要说明

之前那份参考偏旧，原因不是文件错了，而是：

- 当前主窗口已经不再单纯依赖 `ui.xaml`
- 真实运行 UI 是在 `ADDGH/ChatWindow.cs` 里用 `XamlReader.Parse(...)` 动态生成
- 之后又通过代码挂接了主题、历史侧栏、代码视图、电池库、设置浮层等增强逻辑

也就是说：

- `original-ui.xaml` 只能代表旧壳子
- 当前现行 UI 以 `ChatWindow.cs` 为准

## 当前运行态 UI 的关键特征

1. 四列主结构
   - 历史侧栏
   - 聊天主区
   - 分隔列
   - 代码视图区

2. 动态主题
   - Light / Dark / System
   - 不是固定深色皮肤

3. 左侧聊天区
   - 顶部工具栏
   - 中部消息流
   - 底部输入区
   - 电池库扩展区

4. 右侧代码区
   - Graph Logic
   - JSON / 代码视图切换
   - 画布诊断区

5. 顶部操作集合
   - 新对话
   - 设置
   - 历史侧栏切换
   - 代码视图切换

## 迁移建议

如果要把当前 UI 真正移到 `Magpie`，应当以 `current-runtime-ui-notes.md` 为主，不要再只照抄 `original-ui.xaml`。
