# 谱系 / 表型模板 ← 现有 Carrier·基因系统 映射（M3-01）

> **GDD 章节号（FG0-DOC-01 注）：** 本文件里的"GDD §x"都指生物机械版 GDD（0.1 及更早），原文见 [ProjectA_GDD_bio-mechanical_archived-2026-09-18.md](../Archive/ProjectA_GDD_bio-mechanical_archived-2026-09-18.md)，与现行 GDD 0.2（`../ProjectA_GDD.md`）的章节不对应。

**状态：** M3-01 交付物，2026-09-13。只做映射与适配方案，**不改代码**（里程碑非目标）。
**上游权威：** 产品语义以生物机械版 GDD §6 为准（见上方注）；实施顺序以 `ProjectA_Milestones.md` M3 为准。
**下游消费者：** M3-02 蓝图库、M3-03 谱系/表型模板、M3-04 装配签名缓存。

> 注意：M3 整体仍被 M2-06 人测门禁阻塞（见 `Consciousness_Playtest_Gate.md`）。
> 本文件是门禁期间可以先做的分析，**不构成开工 M3-02 的许可**。

---

## 1. 判定总表

每个旧概念给且只给一个判定：保留 / 包装 / 迁移 / 废弃。

| 旧概念 | 它今天实际是什么 | 判定 | 新模型中的位置 |
|---|---|---|---|
| `CarrierInstance` | **一件器官实例 + 它自己的有序基因槽**（不是个体） | **包装** | 「个体已表达的一件器官」；由模板版本实例化，不再由玩家手捏 |
| `CarrierRegistry`（容器部分） | 一个个体持有的多件 Carrier 器官 | **迁移** | 从全局单例迁到按 `SimEntityId` 归属（M2-03a 已立同一口径） |
| `CarrierRegistry.ActiveCarrierId` | 全局「当前激活的攻击器官」 | **迁移** | 降级成个体的**主器官槽**（GDD §6.5），不再是全局状态 |
| `CarrierGeneService` 的唯一占用规则 | 一个基因实例同时只能插一处，冲突 Reject-to-Safe | **保留** | 继续管**实物**（个体已表达器官、单体临时移植）；**模板层不适用**，见 §4 |
| `GeneReserve` 储备囊 | 持有基因**实物实例** | **保留** | 仍是实物仓储，**不是**蓝图库 |
| `GeneCatalog` / `OrganelleCatalog` | 静态定义表 | **保留** | M3-02 明写「引用现有 Catalog，不复制定义」 |
| `CarrierCompiler.Compile` | Carrier → RuleVector+模块链 → `HitEvent` | **包装** | M3-04 经适配层调用，**不重写 ComposeEngine** |
| ComposeEngine 化学（`NormalizeContracts`/`RunAssembly`/`ApplyPipeline`） | 物质代数与规则流水线 | **保留** | 一行不动。M3 只改「谁来喂它输入」 |
| `MetabolicSlicePanel.Instance` 作为数据宿主 | UI 单例同时持有核心玩法数据 | **废弃**（仅数据宿主身份） | 数据归个体；面板退回纯 UI。已由 `IPlayerLoadoutSource` 隔出一层 |
| M2 `UnitLoadoutRegistry` / `IPlayerLoadoutSource` | 按 `SimEntityId` 归属的装配投影 | **保留** | 就是 M3 的落点：模板版本 → 装配签名 → `UnitLoadout` |
| M2 `ArchetypeLoadoutTable` | 按行为原型给 AI 单位一份固定装配 | **迁移** | 表型模板的前身；模板落地后它退化成「未绑定谱系的默认表型」 |

---

## 2. 事实记录（里程碑实施第 1–4 条）

### 2.1 Carrier 是器官实例，不是个体

`CarrierInstance` 的 `CarrierId` 就是玩家囊里那件器官 `PartInstance.PartId`，`OrganelleId` 是器官 def id
（`MetabolicSlice/Carrier/CarrierInstance.cs:5-23`）。创建入口 `CarrierRegistry.EnsureCarrier(carrierId, organelleId, …)`
的两个生产调用点传的都是 `part.PartId` / `part.CardDefId`
（`UI/Battle/MetabolicSlicePanel.cs:123`、`MetabolicSlice/StructuralOrganService.cs:65`）。

所以层级是 **个体 → CarrierRegistry → 多个 CarrierInstance（每个=一件器官）→ 槽内基因**。
同一器官抽到两份会产生两个互不影响的实例（`CarrierRegistry.cs:6-10`）。

**这条对 M3 的意义**：新模型里的「个体」不需要新建概念——`CarrierRegistry` 就是它，只是今天挂错了地方。

### 2.2 活动 Carrier 的全局单例假设

单例状态在 `CarrierRegistry.cs:13,18-19`，切换入口 `SetActive` 发 `CarrierActivatedEvent`（`:50-62`）。
依赖「全局只有一个活动 Carrier」的读取点至少 9 处：

- 战斗结算：`MetabolicSlice/Combat/MetabolicSliceRunner.cs:47`
- 吞噬判定：`Stage/CellStage/CellDevourSystem.cs:90`
- 表现：`Battle/Feedback/CarrierBodyVisualPresenter.cs:89`、`Battle/Feedback/WhiteboxComposeAimIndicator.cs:115`
- UI：`UI/Battle/MetabolicSlicePanel.cs:266,278`、`UI/BattleCarrierUIToolkit/BattleCarrierUIToolkit.cs:382,468,641,662,858,928`
- M2 装配投影：`Control/IPlayerLoadoutSource.cs:62`

**这是 M3 最大的一块迁移面，也是最容易做漏的一处**：只要还有一个读取点直接读 `ActiveCarrier`，
「100 个同模板单位各自有自己的主器官」在行为层面就不成立——那个读取点会拿到玩家本体的器官去算别人的账。
M2-03b 已经在直控路径上踩过一模一样的坑（「不管接管谁，打出来的都是玩家那套装配」）。

全量 grep（`Assets/` 下 `ActiveCarrier`）命中 9 个文件，除上列与定义方 `CarrierRegistry.cs` 外只多一个
`MetabolicSlice/Structural/StructuralOrganService.cs:33,63`——那里是**注释**，说明「结构器官也开一份
`CarrierInstance` 基因槽，但不抢占 `ActiveCarrierId`」。即**结构器官本来就不参与主器官单例**，
与 GDD §6.5 把结构器官单列一槽的口径一致，迁移时不必给它造第二套规则。
无 `DebugTools/` 目录，`Assets/Editor/` 零命中。

### 2.3 基因实例唯一占用

`CarrierGeneService.EquipGene`：基因不在储备囊（即已插在别处）直接拒绝 `GeneAlreadyEquipped`
（`MetabolicSlice/Carrier/CarrierGeneService.cs:25-28`）；目标槽被占则拒绝、**不交换**（`:32-36`）；
`UnequipGene` 把 `Location` 置回 `Reserve()`（`:64-69`）。全部 Reject-to-Safe，不抛异常、不静默覆盖。

### 2.4 ComposeEngine 编译入口

`CarrierCompiler.Compile(Engine, CarrierInstance, GeneReserve, WorldState, int seed, string cellId)`
→ `List<HitEvent>`（`MetabolicSlice/Carrier/CarrierCompiler.cs:15`）。

内部实际是**两个相位**，而里程碑文档没有区分它们——M3-04 的缓存设计成败就在这条线上：

| 相位 | 代码 | 输入依赖 | 可否按装配签名缓存 |
|---|---|---|---|
| 静态 | 收集 contracts/moduleGenes、`NormalizeContracts` 出 `rules`、拼链 | 只依赖 `OrganelleId` + 槽内基因 id 与顺序 | **可以** |
| 动态 | `RunAssembly(chain, ticks:1, seed)`、`ApplyPipeline(evt, rules, world)` | 还吃 `seed` + `WorldState` + `cellId` | **不可以** |

（`:22-69` 为静态相位，`:71-78` 为动态相位。）

**⚠️ 给 M3-04 的硬约束**：M3-04 验收写的是「100 个同模板单位只编译一次静态组合」，
这只能指**静态相位**。若把 `RunAssembly`/`ApplyPipeline` 的产物也缓存进签名，
100 个单位会共用第一次编译时那一刻的世界状态与随机种子——
表现是「所有同模板单位的命中结果一模一样、且随世界变化不更新」，而且**单元测试很可能测不出来**
（固定种子 + 静止世界下两者结果相同）。

另一条同等重要的：缓存的必须是**链配方**（器官 id + 基因 id 有序列表），
**不是 `IModule` 实例**。链上每件都是现造的（`new EnergyCore(10f)`、`tailDef.CreateModule()`，`:67-69`），
跨单位共用实例等于共用模块内部状态。

---

## 3. 与 M2 已有装配层的耦合现状

耦合是**单向**的：`Control` 层读 Carrier，反向零引用。

- `MetabolicSlicePlayerLoadoutSource.CollectOrgans` 走 `MetabolicSlicePanel.Instance` → `panel.CarrierRegistry`
  → 遍历 `All`，用 `ActiveCarrierId` 判谁是 `Primary`、其余记 `Utility`（`Control/IPlayerLoadoutSource.cs:41-83`）。
  该文件注释自陈这是已知技术债（`:10-15`）。
- `UnitLoadoutRegistry` 对 `CarrierRegistry` / `MetabolicSlicePanel` **零直接引用**，只认 `IPlayerLoadoutSource`
  接口，且可注入（`Control/UnitLoadoutRegistry.cs:93-96,323-328`）。

**结论：M2 已经把这笔债圈在了一个可替换的实现类里。** M3 不需要先做大重构——
把 `IPlayerLoadoutSource` 的生产实现换成「读个体的模板版本」即可，`UnitLoadoutRegistry` 一行不动。
这是本次映射里最省事的一条路径，也是 M3-05「新单位表达 V2、旧单位保持 V1」的天然落点。

---

## 4. 适配方案（里程碑实施第 5 条）：不破坏现有化学规则

### 4.1 一条不能越的线：模板是配方，不是实物

GDD §6.4 明写「拾到器官不会自动解锁蓝图」，M3-02 把它写成了验收条件。对应到代码，划界是：

- **实物轨**：`GeneReserve` 的基因实例、玩家囊里的器官 `PartInstance` → 继续受 `CarrierGeneService` 的
  唯一占用规则约束（判定：保留）。
- **配方轨**：蓝图库（M3-02 新建）、表型模板版本（M3-03 新建）→ 只存**蓝图 id 与顺序**，
  **不持有任何实例、不调用 `EquipGene`**。

**如果模板层复用 `EquipGene`，M3 会当场撞墙**：一条模板一旦「装上」一个基因实例，
该实例就被锁死在储备囊之外，第二个同模板单位无基因可用——
而 M3-04 的验收要求恰恰是 100 个同模板单位同时存在。这是本次映射里最容易做错的一步。

### 4.2 适配层形状（只加，不改化学）

新增一个编译适配入口，与现有 `Compile` 并存：

```
CompileFromRecipe(engine, organelleId, IReadOnlyList<string> geneIds, world, seed, cellId)
```

- 它做的事与 `Compile` 逐字相同，**唯一差别是基因来源**：
  `Compile` 走 `reserve.Find(slot.GeneInstanceId)` 拿实例再取 `gene.GeneId`（`CarrierCompiler.cs:33-40`）；
  适配入口直接收 `GeneId` 列表。
- `GeneCatalog.Get` / `GetModule` / `OrganelleCatalog.Get` / `NormalizeContracts` / `RunAssembly` /
  `ApplyPipeline` **全部原样调用**——化学规则、九维物质代数、`Packet.Heat` 那套过路热负荷一行不碰。
- 现有 `Compile` 保留，改成薄壳：解析出 `geneIds` 后转调适配入口。这样「实物装配」与「模板装配」
  共用同一条化学链路，不会出现两套结果不一致的编译器。

这样做的理由：ComposeEngine 本来就只认 `GeneId` 与 `OrganelleId`，**实例身份从来没进过化学计算**。
今天之所以传实例，只是因为唯一的调用方恰好是玩家手里的实物。

### 4.3 `Heat` 三义的既有划界照旧

`AUTHORITY_AND_CONFLICTS.md` §C7 已拍板：`Packet.Heat`（过路热负荷，阈值 8）、
`SubstanceVector.Heat`（温度维，有正负）、`Strain` 过载债（阈值 100，按 `SimEntityId`）三者分立。
M3 不触碰前两者；模板的「代谢成本」（M3-03 实施第 4 条）算的是 `Strain`/代谢那一轨，
**不要顺手接到 `Packet.Heat` 上**——那条链路目前还是死的（写它的四个器官全部 `isRetired`）。

---

## 5. 遗留与未确认

- `IModule` 实现是否持有跨 tick 状态：**未确认**。§2.4 的「缓存配方不缓存实例」是按现有
  工厂式创建（每次 `new` / `CreateModule()`）给出的保守要求，不是实测结论。
- 「单体临时移植」（GDD §6.7）走实物轨还是配方轨：本文件按**实物轨**归类（它接的是一件野生器官实物），
  但 M3-04 的「签名 = 模板版本 + 临时移植」意味着签名要能表达一个不在模板里的器官，
  具体编码方式留给 M3-04，本文件不预设。
- 污染 / 完整度 / 重复解析进度（M3-02 实施第 2 条）在现有代码里**完全不存在**，是新建，不是接线。
