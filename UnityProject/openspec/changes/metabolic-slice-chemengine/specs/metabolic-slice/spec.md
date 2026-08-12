## ADDED Requirements

### Requirement: 二维方格切片 + 玩家有向管
细胞阶段战斗切片 SHALL 是二维方格、四邻可连；玩家自绘箭头，`Packet` 只沿箭头流动；空槽可作为中继（只跑 `SlotPassive`，不装 Module）。

#### Scenario: 空槽中继不打断传包
- **WHEN** 一条有向边经过一个未安装器官的空槽
- **THEN** `Packet` 继续沿边传递，只触发该槽的 `SlotPassive`，不触发任何 `Module.Step`

### Requirement: 双系统（切片战斗 + 储备囊限容）
玩家 SHALL 拥有两套系统：参战的切片（`SlotGrid`）与限容的储备囊（`Bag`）；囊内器官默认不参战，可与切片互相转移、装卸。

#### Scenario: 囊满触发逼弃
- **WHEN** 玩家尝试把新器官放入已达容量上限的储备囊
- **THEN** 系统要求先逼弃（丢弃或转移）已有物品，才能完成放入

### Requirement: 复玩三轴
细胞阶段 SHALL 提供三条复玩轴：轴 A 环境残留 + 地形 Tag（`GameLogic/MetabolicSlice/Environment/`）、轴 B 背包逼弃 + 双系统合成（`CraftService.Craft` 支持囊+槽跨位合成）、轴 C1 捕食消化（`Digestion/` 限时炼成器官，事件解耦不直连 Bag）。

#### Scenario: 环境残留触发世界反应
- **WHEN** 一个携带 `Fire` Tag 的 `HitEvent` 命中带有 `Wet` 残留的地形格
- **THEN** 该地形格触发一次登记在 `EnvironmentReactionCatalog` 的世界反应（如生成 `Steam` 残留）

#### Scenario: 跨囊/槽合成
- **WHEN** 玩家发起一次合成，配方 `CraftRecipe.AllowFromEquipped=true` 且原料分别位于储备囊与已装配的切片槽
- **THEN** `CraftService.Craft(recipeId, bag, grid)` 能同时从囊和槽取材完成合成，不要求原料集中在同一容器

### Requirement: 战斗桥接接入正式流程
ChemEngine 出口事件 SHALL 通过 `MetabolicSliceBridge`（`GameLogic/MetabolicSlice/Combat/`）接入正式细胞阶段战斗流程：由 `CellStageFlow.RegisterModules()` 注册挂载，`Priority = ModulePriority.MetabolicBridge`，消费 `HitEvent.Damage` 转发到 `SimBridge.DamageArea`。

#### Scenario: Tick 产出的伤害事件转化为真实掉血
- **WHEN** `MetabolicSliceBridge.OnUpdate` 达到 Tick 间隔并调用 `MetabolicSliceRunner.Tick` 产出至少一个 `Damage > 0` 的 `HitEvent`
- **THEN** 桥接层调用 `SimBridge.DamageArea`，对应区域内敌方单位血量真实减少
