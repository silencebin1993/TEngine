## Context

旧 combat-alchemy epic（`Cell_Stage_Spec.md` §17 + `Game_Framework_Design.md` §5.8）设计「反应矩阵 ReactionMatrix + 催化卡 Catalyst Card」，挂在热更层 `StatusSystem` 之上做 Status×Status 查表。该方案要求人工逐条设计反应规则（首版仅 6 条），组合深度随内容量线性打表，与「内容表可无限加行，代码种类冻结后只加实例」的长期目标冲突。人于 2026-08-11 冻结新方向并驱动 story-001~006 落地：独立 `ChemEngine` 核心库（零 Unity）+ `MetabolicSlice/` 游戏侧包装，替代并删除旧半成品。

## Goals / Non-Goals

**Goals:**
- 独立 `ChemEngine` 核心库零 Unity 依赖、可脱离 Unity 独立测试
- `MetabolicSlice/` 游戏侧包装接入正式细胞阶段战斗流程
- 删除旧 `ReactionSystem*` 半成品，消除双源
- 把已落地架构回写进 `DesignDocs/`，使其重新成为可信权威

**Non-Goals:**
- sprint-005 剩余 UI 收尾 story（独立范围，不属于本 change）
- `openspec archive` / 写入 `openspec/specs/` 的 spec delta（见下方 Decisions D7）

## Decisions

**D1 — 数据宪法冻结**（摘 `代谢切片-冻结总案-基元与美术.md` §3）：三个通用容器 `Packet`（装配链流通物：Energy/Heat/Shape/Scale/Count/Spin/Orbit/Tags/Mult/Payload）、`HitEvent`（出口事件）、`RuleVector`（基因/全局正规形），禁止基元携带私有字段。结算顺序冻结（§3.5）：切片图 Tick 走「源注入 → 沿有向边传包 → 每节点 SlotPassive → Module.Step → 汇出 HitEvent」；契约管道每出口固定 11 步（BanRanged → DelayWrap → ElementAttach → Reflect → Share → SwarmScale → Commit → Lifesteal → RageCheck → HeatSettle → WorldTagReactions）。

**D2 — 基元种类冻结**（摘 §4）：9 种参与化学的基元种类——`SlotType`/`Organelle`/`Gene`/`Terrain`/`Residue`/`Status`/`Reaction`/`Reagent`/`CardMeta`。代码种类冻结后只许加实例，不许加「新职责种类」；已废除种类 `MembranePart`/`EdgeMod` 禁止再实现。

**D3 — 有向图操作语义**（摘 §7）：二维方格、四邻可连；玩家自绘箭头，`Packet` 只走箭头；空槽可中继（只跑 `SlotPassive`）；管类器官（`AttachTarget=DirectedEdge`）挂在箭头上，传包时先跑管器官再进目标格。

**D4 — ChemEngine 技术形态**（摘 `化学引擎-ClaudeCode需求规格.md` §3/§4）：C# `netstandard2.1` 独立文件夹 `ChemEngine/`，`src/ChemEngine/{Core,Modules,Contracts,Statuses,Engine,Catalog,Serialization}`；`dotnet build -c Release` 产出 `ChemEngine.dll`；Unity 侧只拷贝源码或放 DLL 到 `Assets/Plugins/ChemEngine/`，**不得**把游戏桥接代码编进核心 DLL；核心概念覆盖 `Packet`/`HitEvent`/`RuleVector`/`Module`/`Contract`/`Status`/`Reaction`/`Engine` 主入口。

**D5 — 三轴复玩摘要**（摘 `复玩三轴-ClaudeCode需求规格.md` §1/§2/§3）：轴 A 环境残留 + 地形 Tag（落地于 `GameLogic/MetabolicSlice/Environment/`：TerrainCell/ResidueStack/WorldEnvironment/EnvironmentReactionCatalog）；轴 B 背包逼弃 + 双系统合成（限容储备囊 + 合成台，落地为 `CraftRecipe.AllowFromEquipped` + `CraftService.Craft` 重载支持囊+槽合成）；轴 C1 捕食消化（落地于 `Digestion/`：Reagent/DigestionChamber/DigestionEvent，事件解耦不直连 Bag）；C2 不可逆蜕变留二期未做。

**D6 — 真实落地战斗桥接架构**（区别于规格文件里的设计态描述，本节引用真实已存在代码）：`MetabolicSliceBridge`（`GameLogic/MetabolicSlice/Combat/MetabolicSliceBridge.cs`）持有 `ChemEngine.Engine` + `SlotGrid` + `MetabolicSliceRunner`；`Priority = ModulePriority.MetabolicBridge`；由 `CellStageFlow.RegisterModules()` 注册挂载。每 Tick 调用 `_runner.Tick(grid, contracts, worldState, seed)` 产出 `HitEvent[]`，当前只消费 `evt.Damage` 转 `_sim.DamageArea(...)`，`Heal`/`Shield`/`Displace` 等字段留后续 story。

**D7 — 与 `openspec/specs/` 的关系**：本 change 的 `specs/chem-engine/spec.md` 与 `specs/metabolic-slice/spec.md` 是**change 目录内的 delta**（`openspec validate` 要求的格式），只用于满足工具校验、留痕已实现能力的形式化描述，**不代表**它们会被合并进全局 `openspec/specs/`。本 change **不执行 `openspec archive`**——`archive` 才是把 delta 真正写入全局 `openspec/specs/` 的动作。不 archive 的原因：那会给同一主题（代谢/化学）新开第三个权威源（`DesignDocs/` + `openspec/specs/` + `最新改动需求/`），违反仓库「消双源」的明确要求。`DesignDocs/` 仍是唯一活权威；本 change 永久保留在 `openspec/changes/metabolic-slice-chemengine/` 作为「已实现变更」的历史提案记录，不迁移、不 archive。

## Risks / Trade-offs

- 追溯记录型 change（先实现后补规格）不是 OpenSpec 的常规流程；`tasks.md` 顶部已注明各项 `[x]` 是「回填已完成里程碑」而非「计划中的任务」，避免读者误判为待办。
- 不产出 `openspec/specs/` delta，意味着 OpenSpec 自身的 spec-driven 校验不会跟踪这次能力变更；用本 story 第 2 项「DesignDocs 深度回写」补偿，`DesignDocs/` 继续作为唯一权威落点。
