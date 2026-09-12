# 单位装配契约（M2-03a 交付物）

**状态：** 已实现，随 M2-03a 一同验收（自检 `[15]` 段）。
**适用范围：** "某个实体身上装着什么" 的归属、查询、失能位与生命周期。
**单一规则真相：** 本文件 + `HotFix/GameLogic/Control/`（四个文件）。

前置契约：控制身份见 `Control_Lifecycle_Contract.md`，输入所有权见 `View_And_Input_Contract.md`，
编队命令见 `Squad_Command_Contract.md`。

本段**不含**输入接线（M2-03b）与代谢/热债/冷却（M2-03c）。

---

## 1. 这一段在修什么

M2-03 的产品立论是：**没有额外英雄技能表**，直控时能按出来的动作必须从个体的**实际装配**读出来。

但在此之前，装配根本不挂在单位上：`CarrierRegistry` / `BagInventory` / `SlotGrid` 全是单例，
唯一持有者是 `UI/Battle/MetabolicSlicePanel`，它自陈"本面板持有的 Grid/Bag 就是玩家状态本身"。
全仓只有一处 `new CarrierRegistry()`。

那套东西描述得了"玩家"，描述不了"某一个实体"。所以"接管另一名友军后读它的装配"
不是实现得不好，而是**无从读起**。`UnitLoadoutRegistry` 补的就是这个缺口。

---

## 2. 键为什么是 `SimEntityId`

| 候选 | 为什么不行 |
|---|---|
| 槽位 index | `SimWorld.cs:237` 的注释已写明索引不得跨帧缓存。槽位会被复用，缓存索引等于"过一会儿指到别的单位身上"。 |
| `LogicId` | 热更层自己发的号，只在同一局内可比，且死亡回收后没有失效语义——拿它当键，死者的条目永远不会被识别为死者。 |

`SimEntityId` 在槽位复用时会重新分配。因此 M2-02 在 `ReleaseSlot` 踩到的
"新生成的单位继承了上一个槽位占用者的数据"那类 bug，在这里**结构上不成立**：
新单位拿到的是新 id，在注册表里查不到任何旧条目。

### 但"结构上不串体"不等于"不用清理"

死者的条目不会自己消失。两条清理路径：

1. **查询路径顺手验活**（`Get`）：解析不到该实体就当场删掉条目并返回空装配；
2. **登记路径按容量阈值全表清扫**（`SweepThreshold = 64`）：兜住"登记了但从没人查过"的条目。

**刻意没有逐帧清扫。** 热更层每帧不得出现与场上单位数相关的循环（仓规架构红线第 4 条），
而清扫是 O(登记条目数)——虽然注册表只收友方可指挥单位（当前三名），
把它挂上每帧路径仍然是在给未来留一条会长胖的路。

---

## 3. 玩家本体为什么是实时投影，不是快照

玩家在战斗中随时会选卡、装上新器官。**生成时快照一次**会让"装配"与"此刻身上真有什么"
当场脱节——而 M2-03 的全部立论就是这两者必须一致。所以每次 `Get(玩家本体)` 都从注入源重新投影。

代价是 O(玩家器官数)，与场上单位数无关，落在允许的 `O(卡牌)` 区间内。

**投影是只读镜像，绝不反向写回。** `MetabolicSlicePanel` / `CarrierRegistry` 的单例结构一行没动，
装配系统是它的下游读者，不是第二个所有者。两个所有者会立刻产生"以谁为准"的问题。

### 返回的对象身份是稳定的

刷新是在原 `UnitLoadout` 对象上**原地重建**，不是每次 new 一个。
所以调用方持有引用跨帧是安全的——拿到的永远是最新值，
不会出现"缓存了一份，装配早变了还在按旧的算"（那正是快照要害的翻版）。

---

## 4. 投影源为什么必须可注入

`UnitLoadoutRegistry` 不持有 `MetabolicSlicePanel` 实例，只持有 `IPlayerLoadoutSource`。两条理由，任一条都足够：

- **耦合方向错了**：那是个 `MonoBehaviour` UI 面板。热更层的核心玩法数据依赖一个 UI 实例，
  意味着面板换实现、换挂载点，装配系统跟着塌。
- **不可注入就没法验收**：Edit 模式起不了那个面板。投影源写死的话，"玩家装配"这条路径
  永远只能靠进 Play 手点验，而本仓明令默认不走交互式 Play 验收。
  自检 `[15]` 段能对"实时投影 vs 快照"下真断言，全靠这一层可替换。

默认实现 `MetabolicSlicePlayerLoadoutSource` 走既有单例链路，单例缺席时收集为空——
Reject-to-Safe，口径与 `Progression/ControlPersistence` 一致。

### 投影口径（产品决策，可推翻）

- 当前激活 Carrier → `Primary`（它就是玩家此刻的主要输出手段）；
- 其余 Carrier → `Utility`；
- `Interact` 对玩家本体**留空**——交互该绑到哪件器官上属于 M2-03b 的输入设计，
  这里凭空指定一件只会把错误决策固化下来；
- 结构器官（`StructuralSlots`）不参与：它们是被动的壳/甲，不产出动作，
  塞进动作集会让"动作集 = 能按出来的东西"这条口径失效。

---

## 5. 为什么"未登记"返回空装配而不是 null

`Get` 永不返回 null、永不抛。未登记 / 无效 id / 实体已死一律返回 `UnitLoadout.Empty`（只有移动）。

查询方因此不需要判空分支。有判空分支的地方就会有漏判空的地方，
而这条路径将来会被每帧的输入层调用——那是最不该出 `NullReferenceException` 的位置。

---

## 6. 移动为什么不由器官提供

`LoadoutAction.Move` 恒可用，不占器官槽，且 `Rebuild` 会**丢弃**任何试图占移动槽的器官条目。

理由：移动是"还活着就有"的基础能力。挂在某件器官上的话，那件器官一失能，
单位就既不能打也不能跑，变成一个站着挨揍的空壳——那是 bug 的观感，不是玩法。
移动器官（鞭毛之类）将来只应该改移动的**参数**，不应该决定移动是否存在。

---

## 7. 失能位为什么不下沉内核

内核不认识"器官"这个概念，它只认识 archetype 和弹体参数。
把器官级失能塞进 `Main/Sim/`，等于要求 AOT 内核开始理解一套它完全不消费的玩法语义，
换来的是每单位多一串状态数组和一条没人读的写入路径。

（`SimStatus` 是**单位级**状态位，不是器官级，不能拿来顶这个用。）

所以 `Disabled` 放在 loadout 的器官条目上，纯热更层。
**本段只建字段 + 查询 + 一个置位入口**（`UnitLoadoutRegistry.SetOrganDisabled`），
供测试与后续里程碑调用。**刻意不定义"什么情况下器官会失能"**——
那属于后续的伤害定位 / 手术提取，凭空发明规则只会被推翻。

### 失能位必须跨投影刷新保留

投影源（玩家的 Carrier 注册表）根本不知道"失能"这回事，它每次都交回一份全是健康器官的列表。
`Rebuild` 因此按 `OrganId` 把旧的失能位抄回去。

不这么做的症状是：**玩家随便装卸一件东西，身上所有损坏器官一键治好**。

已知边界：同一件器官卸下再装回，失能位会丢。那时它已经是"另一份装配"了，沿用旧战损反而更难解释。

---

## 8. 原型 → 装配是代码里的一张小表，不是 Luban 表

M1 固定房间里真正会被接管的非玩家友军只有两名（原型 13 孢子 / 15 菌丝体，
见 `CellStageFlow.SpawnControlAllies`）。为两行数据新开一张配置表、走一遍 codegen 与热更包，
成本远大于收益。等友军种类真的展开（M2-04 之后）再搬表，届时只需换掉 `ArchetypeLoadoutTable`
的实现，调用方一行都不用改——这正是把它收敛成单一入口的理由。

| 原型 | 主器官 | 功能 | 交互 | 动作掩码 |
|---|---|---|---|---|
| 13 孢子 | `org_confusion_spore` | `org_dash_spore` | — | 7（Move+Primary+Utility）|
| 15 菌丝体 | `org_mycelium` | — | `org_hook` | 11（Move+Primary+Interact）|

两者刻意取**不同的槽位组合**而不是同槽换个 id：M2-03 的验收项是"两个不同装配单位被接管时
动作集不同"，同槽换 id 从动作集上是看不出来的。

**只用既有器官 id**，不新增器官/基因/美术内容。

**未登记的原型落到只有移动的空装配。** 给未知原型编一套默认动作，会让
"这个单位为什么能放这一招"变得无从追查，而 M2-03 的立论正是动作必须有来源。

---

## 9. 生成是入队的，所以登记必须能延迟

`SimBridge.Spawn` 只是写命令缓冲，实体 id 要等下一次 `Step` 才存在——生成点当场拿不到键。

`RegisterArchetypePending(logicId, archetypeId)` 先按 `LogicId` 记账，
`ResolvePending(snapshot)` 在实体落地后补登记。

**挂起表为空时 `ResolvePending` 立即返回 0，一行都不扫**——这就是它可以挂在每帧路径上的原因：
稳态代价是一次 `Count == 0` 判断，与场上单位数无关。
只有生成后的那一两帧才会出现 O(单位数 × 挂起数) 的扫描。

`MaxResolveAttempts = 120` 兜底：出生即死的单位不会让挂起项永远挂着每帧白扫——
那恰好就是本类要避免的逐帧 O(N)。

---

## 10. 验活的 O(1) 快路（以及内核的真实复杂度）

`SimWorld.TryFindUnit` 是**线性扫描**（`SimWorld.cs`），不是哈希表。
也就是说 `SimBridge.TryResolveUnitIndex` 是 O(单位数) 的，只不过整段发生在 AOT 内。
把它当每帧常规查询用，等于让热更层每帧的开销跟着敌人数走。

所以注册表条目缓存了上一次解析到的槽位，但**每次使用前先拿快照核对那一格的
`SimEntityId` 还是不是同一个**——验证在先、使用在后：

- 命中 → `snapshot.Alive[idx]`，纯 O(1)，且不可能指错单位；
- 未命中（槽位真的变了 / 首次查询）→ 回落内核解析一次，重新缓存索引。

这**不是**「缓存槽位索引」那个坑：那个坑是"缓存了然后直接拿去用"。这里缓存的只是一个猜测，
猜错了就作废重算。

> **给 M2-03b 的提醒**：如果输入层每帧对多个单位调 `Get`，请确认它们都走得到快路
> （即上一帧查过同一批实体）。冷启动那一帧仍然是 O(单位数 × 查询数)。

---

## 11. 生命周期

| 时机 | 动作 | 在哪 |
|---|---|---|
| `SetupSim` 内、`SpawnControlAllies` **之前** | 建注册表、绑 `SimBridge` + 默认投影源、登记玩家本体 | `CellStageFlow.SetupUnitLoadouts` |
| 生成两名友军 | 按 `LogicId` 挂起登记 | `CellStageFlow.SpawnControlAllies` |
| 每帧（暂停下也跑） | `ResolvePending` | `CellStageFlow` Update，紧邻 `_squadCommands.Tick` |
| `Exit` | `Unbind` 并置 null | `CellStageFlow` Exit，紧邻 `_squadCommands.Unbind` |

玩家本体取 `_sim.ControlledUnitId`，且这一行发生在 `RequestControlRestore` **之前**：
`SimWorld.Initialize` 把槽位 0 的实体直接设成受控实体，所以此刻它必然还是本体，
不会误把上一局记住的那具躯体登记成"玩家本体"。

跨局必须 `Unbind`：条目里的键是上一局那个 `SimWorld` 发的实体 id，跨局一律作废
（同 `ControlPersistence` 拒绝落盘 `SimEntityId` 的理由）。

注册表与 `CameraDirector` / `SquadCommandSystem` 同理**不进 `_hub`**：
战略暂停下查看某个单位装着什么，正是接管前的决策依据，而 `_hub` 会被暂停早退整个冻住。

---

## 12. 内核零改动

`Assets/GameScripts/Main/Sim/` 本段一行未改。

唯一的热更侧新增是 `SimBridge.TryResolveUnitIndex`（桥接层，`HotFix/GameLogic/Battle/`），
它只是把已有的 `ISimBackend.TryGetUnitControlState` 包成"解析存活实体槽位"的语义，
不新增任何内核能力。热更层碰内核仍然只有 `SimBridge.cs` 这一个口子。

---

## 13. 非目标（M2-03a 明确不做）

- 不做输入接线、不改 `CellPlayerController`（M2-03b）；
- 不做代谢 / 热债 / 冷却——这三个量代码里今天完全不存在（M2-03c）；
- 不定义器官失能的触发规则（伤害定位 / 手术提取）；
- 不改 `MetabolicSlicePanel` / `CarrierRegistry` / `BagInventory` / `SlotGrid` 的单例结构；
- 不新增器官、基因、敌人、美术内容。

## 14. 已知缺口（交给后续里程碑）

- **玩家本体没有 `Interact` 动作**：投影口径里留空（见 §4），M2-03b 决定绑什么。
- **多件 `Utility` 器官的顺序不确定**：`CarrierRegistry.All` 是 `Dictionary`，
  迭代顺序不保证。`Primary` 由激活 id 决定所以稳定；若将来 UI 要按固定顺序列出功能位，需要显式排序。
- **移动器官无法表达**：`org_flagella` 这类改移动参数的器官在本模型里无处安放（见 §6）。
