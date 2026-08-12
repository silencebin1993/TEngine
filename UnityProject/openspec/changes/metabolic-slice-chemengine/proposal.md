## Why

旧 `Cell_Stage_Spec.md` §17「反应矩阵 ReactionMatrix + 催化卡 Catalyst Card」挂在热更层 `StatusSystem` 之上，走 Status×Status 人工打表（首版仅 6 条规则），组合深度随内容量线性增长，与产品长期目标「内容表可无限加行、代码种类冻结后只加实例」（`代谢切片-冻结总案-基元与美术.md` §1 F8）直接冲突。人在 2026-08-11 冻结新方向（做法 C：基元只改通用数据字段，引擎统一叠加，禁止 `is`/`instanceof` 组合技），驱动 story-001~006 落地独立 `ChemEngine` + `MetabolicSlice/` 包装并删除旧半成品。本 change 是**追溯记录型**提案：001~006 已全部实现并 Play 验收通过，此处把已落地的架构决策沉淀为可追溯的规格文档，不是待实现计划。

## What Changes

- 新增独立库 `ChemEngine`（`ChemEngine/`，零 Unity 依赖，netstandard2.1）：`Packet`/`HitEvent`/`RuleVector` 数据宪法 + `Module`/`Contract` 装配管道 + `Engine` 求解器
- 新增 `MetabolicSlice/` 游戏侧包装（Grid/Bag/Transfer/Crafting/Graph/Combat/DebugTools）+ 三轴复玩系统（环境残留、背包合成逼弃、捕食消化）
- 新增战斗桥接 `MetabolicSliceBridge`（`GameLogic/MetabolicSlice/Combat/`），接入 `CellStageFlow.RegisterModules()`，消费 ChemEngine `HitEvent` 转 `SimBridge.DamageArea`
- 删除旧半成品 `GameLogic/Battle/ReactionSystem.cs` / `ReactionRuleSpec.cs` 及联动挂钩（`StatusSystem` / `EffectDealDamage` / `GameSignals` / `ModulePriority.Reaction` / `CellStageFlow.DebugTriggerReactions`）
- 不改：AOT/热更边界、`Main/Sim` 判定不下沉的红线、Built-in RP / 无 Entities

## Capabilities

### New Capabilities
- `chem-engine`: 独立化学反应引擎核心库（装配基元 + 契约管道 + Packet/HitEvent/RuleVector 通用容器，零 Unity 依赖）
- `metabolic-slice`: 细胞阶段代谢切片包装（二维方格培养切片 + 玩家有向管 + 切片/储备囊双系统 + 复玩三轴）

### Modified Capabilities
（无——旧 §17/§5.8 描述的是从未真正实现的设计态，不是已发布的 spec 行为变更，因此没有「修改」而只有「新增」）

## Impact

- 受影响代码：`ChemEngine/`（新增独立库）；`Assets/GameScripts/HotFix/GameLogic/MetabolicSlice/`（新增）；`GameLogic/Battle/{ReactionSystem,ReactionRuleSpec}.cs`（删除）；`GameLogic/Stage/CellStage/CellStageFlow.cs`（挂载桥接）
- 受影响文档：`DesignDocs/Cell_Stage_Spec.md` §17、`DesignDocs/Game_Framework_Design.md` §5.8 与 §6 表格、`DesignDocs/最新改动需求/AUTHORITY_AND_CONFLICTS.md`
- 不影响：`cell.ReactionRule` / `cell.CatalystRule` 两张 Luban 表从未生成过，本 change 之后从 §6 表格整行删除（见 `tasks.md` 8.3）
