# 任务四：UI 重设计（沙盒迁 UI Toolkit）

> 2026-08-23（one-shot-prompt.txt 全量执行）

## 改动

新增 `BattleSandboxUIToolkit.cs`（`Assets/GameScripts/HotFix/GameLogic/UI/BattleSandboxUIToolkit/`）
+ 两份 `BattleSandboxUI.uxml`（Art 源 + Raw 热更包）+ `BattleUI.uss` 追加
`.sandbox-column`/`.sandbox-scroll`/`.sandbox-row`/`.sandbox-override-row` 类。

- 建面板模式照抄 `BattleCarrierUIToolkit`：`Awake` 设 `Instance`+`DontDestroyOnLoad`，`Start`
  里 `LoadAssetAsync<VisualTreeAsset>("BattleSandboxUI")` + `PanelSettings`，10 帧
  rootVisualElement 轮询保护，`sortingOrder=5`（现况 HUD=0/Carrier=4/Draft=6/Overlay=10/
  Result=12，5 是空档）。
- UXML 三列用 `flex-direction: row` + `.sandbox-column{flex-grow:1;flex-basis:0;min-width:0}`，
  **不写死宽度**；文字统一挂 `.list-row`/`.dim`（已有 `white-space: normal`），允许换行。
- 数据/逻辑全部复用既有静态方法，未重写：`SandboxAssembler.Compose`、
  `SandboxAssembler.OverridesFromEvent`、`LookDevFixtures.All`、
  `MetabolicSliceBridge.ApplyEvent`（开火）、`MetabolicSliceBridge` 的 Sandbox DPS 读数。
  自动连发计时器迁到新控制器自己的 `Update()`（同 `CellDebugHud.Update()` 的实现思路）。
  7 维度覆盖行用 UI Toolkit `Toggle`+`Slider`/`TextField` 动态构建（本仓库首次引入这三个
  控件，之前只用过 Button/Label/ScrollView）。
- `CellDebugHud.cs`：主菜单"LookDev 沙盒"按钮改唤起 `BattleSandboxUIToolkit.Instance.Show()`
  （连同 `GameRoot.StartLookDevSandbox()`），不再置位旧 `_lookDevActive`。旧 IMGUI
  `DrawLookDevSandbox` 系列方法整段保留未删，降级为"对照模式"——运行中按 L 键
  （`#if UNITY_EDITOR || DEVELOPMENT_BUILD`）手动切换，默认关闭，同 J/K/I 三个既有对照开关
  同一模式。
- `GameApp.cs`：新增 `BattleSandboxUIToolkit` 常驻单例挂载行。
- 顺带集成任务二的"生成/刷新对比台"/"清除对比台"按钮到沙盒右列。

## 验收（Play 截图）

`sandbox-panel-clean.png`：`BattleSandboxUIToolkit.Instance.Show()` 后截图确认——
纯 UI Toolkit 面板（无 IMGUI 窗口边框/GUILayout 痕迹），三列在 2560px 宽屏下均匀自适应铺满，
中列 7 维度覆盖行文字完整换行显示、无溢出裁切，右列 HitEvent 预览/开火按钮/自动连发/DPS 读数/
造型对比台入口/退出按钮全部可见。

## Unity 真编译 + Play 验证

`refresh_unity` 0 error。`BattleSandboxUIToolkit.Instance.Show()`→选基因/器官→
`SandboxAssembler.Compose` 实时刷新预览→点"开火"→`MetabolicSliceBridge.ApplyEvent` 触发，
全程 `read_console(types=[error,warning])` 0 条。
