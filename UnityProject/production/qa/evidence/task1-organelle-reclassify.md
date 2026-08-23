# 任务一：器官重新分类（方案②）

> 2026-08-23（one-shot-prompt.txt 全量执行）

## 改动

发现 `CodexRegistry.cs` 已含 `AllCarrierOrganelleEntries()`/`AllMetabolicModuleEntries()`、
`BattleOverlayUIToolkit.cs` 已含 `CodexCategory.MetabolicModule`/`RefreshCodex` 分支（此前 story 已落地）。
本次补齐剩余两处：

1. `BattleCarrierUIToolkit.RefreshCarrierListAllCatalog()`：拆两组渲染——
   `AddCarrierCatalogSection("载体器官", codex.AllCarrierOrganelleEntries())` +
   `AddCarrierCatalogSection("代谢模块", codex.AllMetabolicModuleEntries())`，分组标题复用
   `gene-section-title` 样式（同 `RefreshGeneList` 契约基因/模块基因先例）。
2. 两份 `BattleOverlays.uxml`（`GameRes/Art/.../uxml/` 与 `GameRes/Raw/UI/BattleUI/`）：
   `CodexTabBar` 内 `TabOrganelle` 后插入 `TabModule` 按钮，与 C# 端
   `CodexTabNodeNames` 数组顺序（`"TabOrganelle","TabModule","TabGene",...`）对齐。

## 验收（execute_code 断言 + 截图）

```
codex.AllCarrierOrganelleEntries() → 2 条（分泌喷射器/纤毛刺）
codex.AllMetabolicModuleEntries() → 22 条
codex.AllOrganelleEntries()（旧接口保留）→ 24 条，未破坏向后兼容
BattleCarrierUIToolkit.VisibleCarrierCount（全量目录视图）→ 26（2 组标题 + 24 条目）
```

截图：
- `codex-organelle-tab.png`：V 键图鉴「器官」Tab，仅 2 项。
- `codex-metabolic-module-tab.png`：「代谢模块」Tab，22 项。
- `carrier-catalog-grouped.png`：Carrier 装配面板全量目录，"载体器官"/"代谢模块" 两组标题清晰可见。

## Unity 真编译 + Play 验证

`refresh_unity(mode=force, scope=all, compile=request)` → 0 error。Play 模式下
`GameRoot.StartCellStage()` 全程 `read_console(types=[error,warning])` 0 条。
