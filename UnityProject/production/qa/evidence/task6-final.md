# 任务六：整体验收

> 2026-08-23（one-shot-prompt.txt 全量执行）

## 编译

`bash tools/check-build.sh`（glob 版 scratch csproj）全程绿；每改完一个文件即时自检，
过程中出现的 3 处编译错误（`in` 参数传属性、元组/裸 Mesh 混传 `Combine`、GameRoot 缺
using）均已修复。Unity 真编译：`refresh_unity(mode=force, scope=all, compile=request)` →
`compilation.is_compiling=false`，`read_console(types=[error])` 0 条。

## Play 模式回归

`GameRoot.StartCellStage()` 进入战斗，全流程多次 `read_console(types=[error,warning])`
**始终 0 条**（含：任务一图鉴/Carrier 面板切换、任务二造型对比台生成、任务三召唤共生体
施放+索敌+爆炸+自毁全链路、任务四沙盒面板打开/开火、任务五 tooltip 展示）。

回归项逐一 execute_code 断言：
- 卡组（`ov.ShowDeck()`→`IsDeckVisible`）、商店（`ov.ShowShop()`→`IsShopVisible`，
  `ShopSystem.SlotCount==3`）、图鉴（`ov.ShowCodex()`→`IsCodexVisible`）三面板独立开关正常，
  `CloseAll()` 后三者均关闭（互斥逻辑未被任务四改动破坏）。
- 玩家位置读写（`SimWorld.SetPlayerPosition`/`PlayerPosition`）在验收过程中反复调用，
  位置正确写入/读出，移动管线未受任务二 `SetPlayerVisualId` 新增字段影响
  （`_visualId`/`_position` 是独立数组）。
- Carrier 装配面板全量目录/已拥有视图切换（`SetShowAllOrgans`）正常。
- 吞噬/技能/暂停：`AbilitySystem.TryCast`、`AreaZoneSystem`（菌丝体光环复用路径）、
  `JobDamage`/`JobCollectDeaths` 标准死亡管线全程零报错间接证明未被破坏
  （任务三的 Sim 核心改动只新增 `MinionSeekAttack/Explode` 分支与 `ResolveMinionCombat`
  独立方法，未修改任何既有 `BehaviorKind`/`JobDamage`/`JobContactDamage` 分支）。

## 证据汇总

| 任务 | 证据文件 |
|---|---|
| 任务一 | `task1-organelle-reclassify.md` + 3 张截图 |
| 任务二 | `task2-3d-differentiation.md` + `visual-compare-stand-final.png`（36 造型同屏两两可辨） |
| 任务三 | `task3-summon.md` + 2 张截图（附属体场景可见） |
| 任务四 | `task4-ui-redesign.md` + `sandbox-panel-clean.png`（三列自适应无溢出） |
| 任务五 | `task5-tooltip.md` + `codex-tooltip.png` |
| 任务六（本文件） | `carrier-catalog-grouped.png`/`visual-compare-stand-final.png`/`sandbox-panel-clean.png` 汇总 |

## 已知偏差（诚实记录，均在对应任务证据文件里详细说明原因）

1. 任务二：玩家 Carrier 本体只按"出口器官类型 × 是否挂基因"四态切换造型，不逐基因建
   24 种专属挂件（24 种形状差异已在图鉴/对比台完整体现）。
2. 任务三："新增召唤技能卡"因旧战斗卡池（Route/Trigger/grantAbilityId）已被
   metabolic-playerization-004 整体 Delist 而不存在可挂载的抽卡入口，改为与 dash 相同的
   `GrantStarterAbilities()` 直接授予（功能等价：CD12s/耗能25/场上限3 的可主动施放技能）。
3. `AbilitySystem.TryCastInternal` 返回值在 `TargetMode=Self` 场景下与实际是否执行效果不完全
   一致（返回 false 但效果已生效，MinionCap 已消耗、冷却已进入倒计时）——这是发现的既有代码
   行为，不在本次六个任务范围内，未修改，仅记录供后续 story 排查。
