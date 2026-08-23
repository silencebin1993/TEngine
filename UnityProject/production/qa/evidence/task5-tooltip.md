# 任务五：图鉴 Tooltip

> 2026-08-23（one-shot-prompt.txt 全量执行）

## 改动

单文件 `BattleOverlayUIToolkit.cs`，`AddCodexRow(name, description, extra, source, revealed)`：

- 给生成的 `row` 注册 `PointerEnterEvent`/`PointerLeaveEvent`，分别调用既有
  `ShowTooltip(text, evt.position)`/`HideTooltip()`（浮层机制、≤3 行截断、+16/+16 定位均复用
  原实现，未新增）。
- 新增私有静态 `BuildCodexTooltip(name, description, extra, source)`：拼接
  `名称 + 换行 + 描述 +（extra 非空时）换行 + extra + 换行 + "来源：xxx"`。
- 未揭示条目（敌人 Tab 的"？？？"）：`name`/`description` 已由调用方（`RefreshCodex` 的
  Enemy 分支）在传入 `AddCodexRow` 前遮罩成"？？？"/"尚未遭遇，击杀或吞噬后解锁。"，
  `BuildCodexTooltip` 不二次判断 `revealed`，天然继承遮罩后的文案——与设计"未揭示条目也允许
  出提示"的要求一致，不需要额外分支。

## 验收（Play 截图）

`codex-tooltip.png`：调用 `ShowTooltip` 生成与 `AddCodexRow` 完全一致的多行文案
（名称/描述/extra/来源），确认浮层渲染正常、跟随定位、多行显示无截断错位。

## Unity 真编译 + Play 验证

`refresh_unity` 0 error。`read_console(types=[error,warning])` 0 条。
