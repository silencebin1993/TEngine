> **说明**：本 change 是**追溯记录型**（retroactive）。以下 `[x]` 均为「回填已完成里程碑」，对应 `epic/metabolic-slice` story-001~007 的实际交付，不是待实现计划。每条附对应 `qa/evidence/` 证据链接（相对 `TEngine/UnityProject/` 的仓库根路径 `production/qa/evidence/`）。

## 1. ChemEngine 独立库

- [x] 1.1 建立 `ChemEngine/` 独立库（`src/ChemEngine/{Core,Modules,Contracts,Statuses,Engine,Catalog}`，`netstandard2.1`，零 Unity 依赖）—— 人窗交付，17/17 单测通过

## 2. story-001 切片包装

- [x] 2.1 `MetabolicSlice/` 全套包装（Grid/Bag/Transfer/Crafting/Graph/Combat/DebugTools）+ `Assets/Plugins/ChemEngine` 正式引用；ChemEngine 核心零改动，17/17 不变。证据：`production/qa/evidence/metabolic-slice-001-packaging.md`

## 3. story-002 复玩轴 A：环境残留

- [x] 3.1 `GameLogic/MetabolicSlice/Environment/`（TerrainCell/ResidueStack/WorldEnvironment/EnvironmentReactionCatalog）；`env_fire_wet_steam` + `env_oil_fire_burn` 两例 UnityMCP Play 模式反射验证通过；check-build 绿。证据：`production/qa/evidence/metabolic-slice-002-axis-a.md`

## 4. story-003 复玩轴 B：背包逼弃 + 双系统合成

- [x] 4.1 `CraftRecipe.AllowFromEquipped` + `CraftService.Craft(recipeId, bag, grid)` 重载补齐「囊+槽合成」缺口；B1 满囊逼弃 + B2 囊+槽合成升级 两例验证通过。证据：`production/qa/evidence/metabolic-slice-003-axis-b.md`

## 5. story-004 复玩轴 C1：捕食消化

- [x] 5.1 新建 `Digestion/`（Reagent/DigestionChamber/DigestionEvent 三文件）；Bag 集成走事件解耦，不直连。证据：`production/qa/evidence/metabolic-slice-004-axis-c1.md`

## 6. story-005 冻结目录

- [x] 6.1 Organelle/Gene/SlotType/Terrain/Residue/Reagent/Reaction 7 张目录冻结 + 新建通用 Module `TagAttach`；24 器官中 17 条复用现有 Module、7 条仅注册元数据；11 基因复用既有 12 个 Contract 中的 11 个。证据：`production/qa/evidence/metabolic-slice-005-freeze-catalog.md`

## 7. story-006 战斗桥接 + 删旧 ReactionSystem

- [x] 7.1 整删 `Battle/{ReactionSystem,ReactionRuleSpec}.cs` + 联动清理（`DataRegistry`/`StatusSystem`/`EffectDealDamage`/`GameSignals`/`ModulePriority.Reaction→MetabolicBridge`/`CellStageFlow` 删 `DebugTriggerReactions`）；新建 `MetabolicSliceBridge` 接入 `CellStageFlow`；Play 模式确认 Tick 持续触发 + 敌方真实掉血；check-build 绿；代码层零残留旧类型引用。证据：`production/qa/evidence/metabolic-slice-006-bridge-and-purge.md`

## 8. story-007 OpenSpec 变更 + DesignDocs 深度回写（本 change）

- [x] 8.1 创建本 OpenSpec change（`proposal.md` + `design.md` + `tasks.md`）
- [x] 8.2 `Cell_Stage_Spec.md` §17 整节改写为新权威叙述（替换旧反应矩阵/催化卡机制细节）
- [x] 8.3 `Game_Framework_Design.md` §5.8 整节改写为真实落地架构 + §6 表格删除 `cell.ReactionRule`/`cell.CatalystRule` 两张废表行
- [x] 8.4 更新 `AUTHORITY_AND_CONFLICTS.md` 三处（半成品代码标注 006 已完成删除 / 制作队列 007 状态 Ready→Done / Luban 表规划措辞改为确认不走 Luban）
- [x] 8.5 证据写入 `production/qa/evidence/metabolic-slice-007-openspec-docs.md`
