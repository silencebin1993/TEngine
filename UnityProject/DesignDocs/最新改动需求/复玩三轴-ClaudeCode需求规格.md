# 复玩三轴 — Claude Code / AI 实现需求规格

> 在现有 **ComposeEngine（做法 C）** + **细胞切片包装（有向流、双系统）** 之上，落地三条复玩轴。  
> 交付：与引擎同样标准的 **独立 C#（netstandard2.1）逻辑库**，可出 DLL、可拷进 Unity，**核心零 Unity 引用**。  
> 本文可与 `组合引擎-ClaudeCode需求规格.md`、`细胞肉鸽-基元卡牌包装.md` 一并喂给 AI。

---

## 0. 三轴一览

| 轴 | 名字 | 玩家乐点 | 依赖 |
|----|------|----------|------|
| **A** | 环境残留 + 地形 tag | 地图是坩埚；走位/弹道留试剂再反应 | ComposeEngine 反应表 + WorldGrid |
| **B** | 背包逼弃 + 双系统合成 | 限容取舍；囊↔切片合成升级 | BagInventory + Craft |
| **C** | 长线上瘾（两条子路径） | **C1 捕食消化**（推荐先做）/ **C2 不可逆蜕变**（其二期） | 掉落→消化泡 / 永久烧器官 |

成功标准（共同）：

1. 新内容以 **数据 + 注册** 扩展，不改旧基元配对 if  
2. 与 `Fire` / `SimGraph` / `Packet` / `RegisterReaction` 对齐  
3. 每轴至少 **2 个**可运行例子 + 单元测试  
4. `dotnet build` 出 DLL；控制台 Demo 不启动 Unity  

---

## 1. 轴 A — 环境残留 + 地形 Tag

### 1.1 目标

战场格子带 **TerrainTag**；单位/弹体可留下 **Residue**（有寿命、可叠加、可反应）。  
攻击、移动、停留与地形/残留相遇时走 ComposeEngine 催化，而不是写死「酸弹打草地」特例表。

### 1.2 核心类型

```csharp
TerrainCell {
  CellId, Position,
  Tags: HashSet<string>,          // 如 Wet, AcidPool, SugarFilm, Light
  Residues: List<ResidueStack>
}

ResidueStack {
  Tag, Amount, TtlTicks,
  SourceId?,                      // 谁留下的
  Payload
}

WorldEnvironment {
  Grid or SparseDict<CellId, TerrainCell>
  void Tick(int dt)
  void AddResidue(cell, tag, amount, ttl)
  IReadOnlyList<string> GetTags(cell)  // terrain ∪ residues
}
```

### 1.3 与 ComposeEngine 的接法

1. 弹体/出口 `HitEvent` 命中或掠过格子时：  
   `tags = event.Tags ∪ cell.GetTags()` → `ReactionEngine.Resolve(tags, event)`  
2. 单位站立 Tick：可选 `StandingReaction`  
3. 反应结果可：改 HitEvent、生成新 Residual、清 tag、对单位上 Status  
4. **禁止** `if (weapon is Acid && terrain is Grass)`；只认 tag 集合  

预置反应（例子，可数据化）：

| 输入 tags | 结果 |
|-----------|------|
| `Fire` + `Wet` | 出 `Steam` 残留，清部分 Wet，改伤害类型 |
| `Acid` + `SugarFilm` | 出 `StickyAcid`，减速区 |
| `Shock` + `Wet` | 额外感电伤，扩散湿到邻格（有向或四邻限一次） |
| `Fire` + `Oil` | 延时燃烧残留 |

### 1.4 谁负责「留下残留」

- Module/Contract 只往 `HitEvent.Tags` / `Payload` 写「会留下什么」  
- **World 层**根据事件的 `LeaveResidue` 指令写格子  
- 配置：`ResidueDeposit { Tag, Amount, Ttl, OnHit | OnTravel | OnExpire }`  

### 1.5 API

```csharp
FireResult FireWithWorld(..., WorldEnvironment world, CellId impactCell);
WorldEnvironment.TickResidues();
void RegisterTerrainReaction(ReactionRule rule);
string ExplainEnvironment(cell);
```

### 1.6 例子（≥2）

1. **火打湿地出蒸汽：** 地形 Wet + 火系 Hit → Steam 残留 + Explain  
2. **先铺油再点火：** 上一发留 Oil，下一发 Fire → 燃烧区；断言第二发结果含燃烧残留  

### 1.7 非目标

- 不做完整导航网格/寻路 AI  
- 不做 3D 体素环境  

---

## 2. 轴 B — 背包逼弃 + 双系统合成

### 2.1 目标

落实包装文中的 **储备囊 + 培养切片**：

- 掉落先进囊；**容量有限**；满则强制抉择  
- 囊 ↔ 槽 装卸；槽可 **空放**  
- 非战斗合成：材料可来自囊与/或切片，产出进囊或空槽  

### 2.2 核心类型

```csharp
PartInstance { PartId, CardDefId, Level, Params, Location }
PartLocation = Bag | Slot(SlotId) | Digestion(可选，见轴C)

BagInventory {
  int Cap; List<PartInstance> Items;
  AddResult TryAdd(part);           // Full → 返回 NeedDiscard
  bool Discard(PartId);
}

CellBlueprint { /* slots + directed edges，见包装文 */ }

CraftRecipe {
  Id,
  Ingredients: List<Ingredient>,  // CardDefId + count/level
  Result: CardDefId / SlotMutate / Materials,
  AllowFromEquipped: bool
}

CraftSystem {
  CraftResult TryCraft(recipeId, materials, OutputDest dest);
}
```

### 2.3 逼弃流程（必须可测）

```text
TryAdd → if Full → return { Status: NeedDiscard, NewPart }
调用方必须：Discard(旧|新) 或 Craft 腾位后再 Add
禁止静默丢弃；禁止超容
```

提供纯逻辑「自动策略」仅用于测试 AI/Demo（如丢最低稀有度），**默认游戏宿主自己做 UI 选择**。

### 2.4 合成

最少配方：

1. `UpgradeSame`：同 DefId ×2 → Level+1  
2. `Salvage`：高级 → 素材×n（腾位）  
3. （可选）异种合成 1 条：Scatter + ExplodeMat → BurstScatter  

约束：

- 数据驱动配方表  
- 合成不检查「是否在切片上邻接」（邻接只影响战斗图）  
- 产出目标囊满 → Craft 失败或要求 OutputDest=EmptySlot  

### 2.5 与有向切片

- `TryEquip(partId, slotId)` / `TryUnequip(slotId)` 检查囊容量与槽类型  
- 空槽合法；`SimGraph` 只收集有 Module 或可过管空槽  

### 2.6 例子（≥2）

1. **满囊逼弃：** Cap=2，加第 3 件 → NeedDiscard；丢弃后再加成功  
2. **囊+切片材料升级：** 槽上 1 件 + 囊中 1 件合成升级，断言原件移除、新件 Location 正确  

### 2.7 非目标

- 不做战斗中完整合成 UI 状态机（可留接口）  
- 不做拍卖行/交易行  

---

## 3. 轴 C — 长线上瘾

**先做 C1；C2 作为二期开关同一程序集内可选模块。**

---

### 3.1 C1 — 捕食消化（推荐优先）

#### 目标

击破敌人不直接给「成品卡」，而给 **不稳定残骸 Reagent**：

- 进入 **消化泡**（可视为特殊 Location 或囊内 DigestionSlot）  
- **限时**代谢：成功→稳定 Part 进囊；失败→毒/炸膛/浪费  
- 玩家决策：吃什么、留多久、提前排出/催熟  

#### 类型

```csharp
Reagent {
  ReagentId, SourceEnemyArchetype,
  Tags[], Toxicity, Progress, MaxTicks,
  PotentialCrafts[]                  // 可炼成的 CardDef 候选
}

DigestionChamber {
  Cap; // 可与主囊分容，如 DigestionCap=3
  Tick(dt) -> List<DigestionEvent>
  Insert(reagent)
  Expel(reagentId) -> PartialRefund?
  Catalyze(reagentId, catalystPart?) // 可选催熟，耗材料
}
```

#### 与 A/B 关系

- 成功产物 → `BagInventory.TryAdd`（走逼弃）  
- 炸膛可 `AddResidue` 到自身格/脚底（接轴 A）  
- 残骸 Tags 参与消化内迷你反应（仍用 RegisterReaction，如 `Toxic+Fire → Ash`）  

#### 例子（≥2）

1. 插入试剂，Tick 至完成 → 囊中获得稳定器官  
2. 超时/高毒性 → DigestionFail 事件；断言无非法超容  

---

### 3.2 C2 — 不可逆蜕变（二期）

#### 目标

某些升级/反应 **永久消耗或变形** 已装备器官，不能免费卸回原状。

```csharp
MetamorphosisOffer {
  Id, CostParts[], ResultPart,
  Irreversible: true
}

bool TryMetamorphose(offerId); // 成功则原 Part 销毁/替换，记入 RunHistory
```

- 与合成区别：Metamorphosis **不可拆解回原配**（Salvage 禁用或大打折扣）  
- 一局形成单向分支；提供 `RunHistory` 供结算图鉴  

#### 例子（≥1 即可二期）

1. 聚焦 → 永久变成过载聚焦；尝试 Salvage 失败或只给灰烬素材  

---

## 4. 程序集与目录建议

```text
ComposeEngine.GameplayLoops/   # 或并入 ComposeEngine 下的 Loops/
  Environment/     # 轴 A
  Inventory/       # 轴 B
  Digestion/       # 轴 C1
  Metamorphosis/   # 轴 C2（可条件编译或功能开关）
  Examples/
  Tests/
```

依赖：`ComposeEngine` 核心项目。  
**仍禁止引用 UnityEngine。**

---

## 5. 配置与扩展

- 所有 Terrain/Residue/Recipe/Reagent/Metamorphosis 用 JSON 或 Scriptable 友好的 POCO 加载  
- `EngineConfig` 增加：`BagCap`, `ResidueMaxPerCell`, `DigestionCap`, `EnableMetamorphosis`  
- Explain：环境、合成失败原因、消化进度均可字符串输出  

---

## 6. 测试清单

| ID | 断言 |
|----|------|
| A1 | Fire+Wet → Steam，可复现 |
| A2 | Oil 再 Fire → 燃烧残留 |
| B1 | 超容 NeedDiscard，无静默加项 |
| B2 | 跨 Location 合成后材料消失、产物在 dest |
| C1a | 消化成功进囊 |
| C1b | 消化失败不超容、有事件 |
| C2 | （二期）蜕变不可逆向还原 |

另：随机 50 次「掉落→加囊→可能合成→装备→带地形 Fire」集成冒烟不崩溃。

---

## 7. 交付清单

- [ ] 轴 A：WorldEnvironment + 残留 Tick + 与 Fire 集成 + ≥2 例  
- [ ] 轴 B：Bag 逼弃 + 装卸 + ≥2 配方合成 + ≥2 例  
- [ ] 轴 C1：消化泡限时代谢 + ≥2 例  
- [ ] 轴 C2：接口 + 开关 + ≥1 例（可标 TODO 但类型与 API 先定）  
- [ ] README：三轴如何挂到切片有向图；扩展新地形/配方/猎物模板  
- [ ] `dotnet test` 通过；Release DLL  

---

## 8. 给 AI 的启动提示（可复制）

```text
请根据《复玩三轴-ClaudeCode需求规格》在 ComposeEngine 之上实现 GameplayLoops（C#，netstandard2.1，零 Unity 引用）。

三轴：
A) 环境残留 + 地形 tag：格子 tags/residues，与 RegisterReaction 集成；Fire 命中格子时催化。
B) 背包逼弃 + 双系统合成：限容、NeedDiscard、囊↔槽装卸、空槽允许、数据驱动 Craft（至少升级+拆解）。
C) 先做捕食消化（限时试剂→稳定零件进囊）；不可逆蜕变做 API+开关+最少一例。

禁止基元两两 isinstance 写反应。例子每轴至少 2 个（C2 可 1 个）。dotnet test 通过，可出 DLL。
先 A+B 跑通，再 C1，最后 C2。
```

---

## 9. 设计决策摘要

| 决策 | 选择 |
|------|------|
| 环境 | tag + 残留寿命，接现有反应表 |
| 背包 | 硬容量 + 强制抉择，驱动每局取舍 |
| 长线 | 优先消化吃炼；蜕变作二期单向进化 |
| 与包装 | 有向切片仍管战斗构筑；三轴管复玩压力与发现 |

---

*配合阅读：`细胞肉鸽-基元卡牌包装.md`、`化学引擎-ClaudeCode需求规格.md`。*
