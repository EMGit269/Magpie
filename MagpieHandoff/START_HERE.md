# Magpie Handoff Bundle

从这里开始即可，不需要再回原仓库翻目录。

## 先看这些

1. `Handoff/README.md`
2. `Handoff/TOOLS.md`
3. `Handoff/PROMPTS.md`
4. `UIReference/UI_SPEC.md`

## 目录说明

- `Handoff/`
  - agent prompt、tool schema、host bridge、迁移边界说明
- `UIReference/`
  - 当前真实运行态 UI 契约与参考资料

## 当前重点

- 真实完整能力仍以原始 `ADDGH` 设计为基准
- 新工作空间的 `Magpie` 需要继续迁移 prompt、tools、host bridge 与 UI
- `show_plan_steps` / `show_reference_options` 这类特殊交互不能在迁移时丢掉
