# 外科窗口契约（M2-05a/b/c 交付物）

**状态：** M2-05a/b/c 已实现并验收。a 段见 `[20]`；b 段见 `[21]`；c 段见 `[22]`；
全量 450 条、FAIL=0。
**适用范围：** 手术窗口原型的身体接点、RTS/直控命中方式与最小奖励结算；不覆盖正式器官掉落池、
背包物品、品质或持久化（里程碑明确非目标，后续由 M3 定义）。
**单一规则真相：** 本文件 + `Main/Sim/SimTypes.cs`（`SimBodyPartSlot` / `SimBodyPart` /
`SimUnitBody` / `DamageRequest.TargetPart` / `SpawnRequest.PrimaryPartMaxHealth` /
`SecondaryPartMaxHealth`）+ `Main/Sim/Jobs/JobDamage.cs`（`TryApplyPartDamage`）+
`Main/Sim/SimWorld.cs`（`_bodies` 登记表、`SpawnSurgicalTestEnemy`、`TryGetBodyPart`、
`HasBody`）+ `HotFix/GameLogic/Progression/SurgicalRewardLedger.cs`（精准/粗暴奖励口径）。
任何其它地方对"接点怎么扣血、摧毁算不算死、产出什么"的推断都是复制品，冲突以这里为准。

前置：伤害结算见既有 `JobDamage`（本段之前只认单一 `Health`）；测试敌人生成见
`Spawning/SpawnDirector.cs`（热更层正式随机生成池，本段**不接入**，见 §6）。

---

## 1. 这一段在修什么

里程碑原文（`ProjectA_Milestones.md` M2-05）：

> 目标：证明直控能在同数值下提供独特高价值，而非只提高 DPS。
> 实施：1. 给一个测试敌人设置身体和两个器官接点；2. RTS 可指定器官类别；
> 3. 直控可瞄准具体接点；4. 低伤精准切离保留完整器官；5. 粗暴击杀只给生物质。

M2-05a 只做第 1 条的**内核基础**，并为第 4/5 条留一个最小的事实信号位（§5）。
第 2、3 条（RTS/直控怎么把"打哪个接点"这个意图传进来）与第 4、5 条的**结算/奖励**逻辑，
都不在本段——本段交付的是"内核能不能表达接点"，不是"玩法怎么用它"。

在此之前，`Main/Sim/` 里的实体只有一个 `Health` float（`SimTypes.cs`），
全仓 `Attach`/`BodyPart`/`Segment`/`HitZone`/`Organ` 命名零命中——没有任何"多子部位"的既有结构。
`OrganelleCatalog`/`OrganelleDef` 只存在热更层（`Control/OrganKernelAction.cs`、
`Cards/CardSpec.cs`），内核完全不认识它们，本段延续这条边界：内核只认识
"接点 1 / 接点 2"这种通用槽位，不认识"器官"。

---

## 2. 数据结构：稀疏登记，不给所有单位加固定字段

```csharp
// SimTypes.cs
public enum SimBodyPartSlot : byte { None = 0, Primary = 1, Secondary = 2 }

public struct SimBodyPart
{
    public float Health;
    public float MaxHealth;              // <= 0 表示这个接点未配置
    public byte Destroyed;               // 0 完好，1 已摧毁（切离）
    public byte DestroyedBySingleTargetHit; // 精准信号位，见 §5
    public float LastHitAmount;          // 摧毁这一击的最终伤害量
}

public struct SimUnitBody { public SimBodyPart Primary; public SimBodyPart Secondary; }
```

`SimWorld` 用 `NativeParallelHashMap<SimEntityId, SimUnitBody> _bodies` 稀疏登记，
**只有显式配置了接点血量的实体才会出现**：

- `SpawnRequest` 新增 `PrimaryPartMaxHealth` / `SecondaryPartMaxHealth`（默认 0）。
  两者都 ≤ 0 时 `SpawnUnit` 完全跳过登记——绝大多数单位（包括所有已上线的敌人/召唤物）
  两个字段都不会被设置，走的还是原来的路径，**零额外内存、零额外遍历**。
- 键用 `SimEntityId` 而不是槽位索引：槽位会被复用，但新分配的 `SimEntityId`
  全局唯一（见其类型注释），不会与旧条目撞键。`ReleaseSlot` 在回收槽位时
  `_bodies.Remove(_entityId[idx])` 摘掉旧登记，避免稀疏表随槽位周转无限堆积。
- `RegisterBody` 用 `TryAdd` 失败才扩容重试一次；正常路径不会走到扩容分支
  （这张表按设计只该装极少数配了身体的实体）。

**为什么不直接在 `SimEntity`/SoA 数组上加两个定长 `SimBodyPart` 字段**：
`UnitCapacity` 默认 16384，即便只加 2×20 字节也会让*所有*单位（包括数万个从不会有
接点的普通敌人）常驻多背这份内存，且需要在 `SpawnUnit`/`ReleaseSlot` 里对全体单位
清零。本段选了里程碑文档建议的"稀疏附加表"方案：只有配置了接点的实体才占用条目，
容量与"同时存在的特殊敌人数"成正比，不是与 `UnitCapacity` 成正比。

---

## 3. 伤害路由规则

`DamageRequest` 新增 `TargetPart`（默认 `SimBodyPartSlot.None`）。路由规则（`JobDamage.TryDamage`）：

1. **只对单体请求生效**（`req.Radius < 0f`）。范围/连锁请求（`Radius >= 0`）**忽略** `TargetPart`，
   一律走整体 `Health`——"炸一整片只炸中某个接点"没有物理意义，也没必要为它定义语义。
   连锁跳到的下一个目标沿用同一个 `req` 副本（含 `TargetPart`），所以理论上一条
   "单体起手 + 连锁"的请求会让每一跳都尝试定向同一个接点槽位；本段没有产出这种请求
   （测试敌人用的是纯单体请求），行为上是自洽的，但没有专门断言，记入 §8 已知遗留。
2. **目标没有登记身体** → 回退整体 `Health` 路径，伤害正常结算，不会凭空消失。
3. **接点存在但未配置**（比如只给了 `PrimaryPartMaxHealth` 的测试体，`Secondary` 是空槽）
   → 同样回退整体 `Health`。
4. **接点已配置且未摧毁** → 只扣这个接点的 `Health`，**不touch 整体 `Health`**，
   两个接点互不干扰（自检 `[20]`-A/B）。
5. **接点已摧毁** → 这次针对它的伤害被**吃掉**（记为"已处理"），不会外溢到整体 `Health`、
   也不会被丢弃到别处（自检 `[20]`-D）。

`TargetPart` 命中时仍会产出一条 `HitEvent`（`Lethal` 恒为 `false`——接点伤害从不直接致死，
见 §4），供未来表现层挂反馈；但**没有专门标记"这是一次接点命中"的字段**（见 §5 的取舍）。

---

## 4. 死亡判定口径（本段选定，b/c 段与产品验收都依赖这条）

**接点摧毁 ≠ 整体死亡。** 无论哪个接点、哪怕两个接点都被摧毁，实体是否存活**只看整体
`Health`**——原有判定路径（`JobDamage`/`JobCollectDeaths`）一行未改。

这是里程碑原文给出的两个选项（"任一接点摧毁即死亡" vs "只有整体/核心接点归零才死亡"）
里选定的后者，理由：

- GDD 的产品语义是"低伤精准切离**保留完整器官**"——如果切掉一个接点就杀死目标，
  "保留"和"切离"就自相矛盾：玩家没法在不杀死目标的前提下拿到一个完整接点。
- 反过来，"粗暴击杀只给生物质"暗示存在一种"不管接点、直接打光整体"的路径，
  这条路径必须始终可用且始终等价于"死亡"，不能因为接点系统的介入而改变。

自检 `[20]`-C/D/G 分别验证：精准摧毁一个接点后实体存活、整体 `Health` 分毫未动；
继续打已摧毁的接点不会意外致死；打光整体 `Health` 才产出死亡事件，且与接点状态无关。

---

## 5. "精准 vs 粗暴"信号位：加了什么、为什么只加这些

里程碑实施第 4/5 条（保留完整器官 / 只给生物质）在 M2-05a 交付时仅预留事实字段；
M2-05c 现已按 §11 完成结算。
但契约要求为它预留信号位，取舍如下：

**加了：** `SimBodyPart.DestroyedBySingleTargetHit`（byte）+ `LastHitAmount`（float）。
接点被摧毁的那一刻记录：摧毁它的最后一击是不是"单体定向命中"（`TargetPart` 显式指定
且 `Radius < 0`，排除范围/连锁误伤），以及那一击的最终伤害量。这是**最小事实字段**——
只记录"发生了什么"，不做任何阈值判断。

**没加：**
- **"伤害量低于多少算精准"的阈值判定。** M2-05a 当时只给事实；M2-05c 现采用 25% 原型阈值，
  见 §11。它仍不是平衡终值。
- **"这次死亡整体上算不算粗暴"的复合判定/事件队列。** M2-05c 没有新开队列，只在
  `DeathEvent.HadSurgicalBody` 记录死亡前是否登记身体；热更层结合 `CauseKind=Damage` 解释为粗暴击杀。
- **`HitEvent.TargetPart` 这种逐次命中广播字段。** 信号位落在 `SimBodyPart` 本身、
  随查随算（`TryGetBodyPart`），足够 M2-05b/c 在需要时查询；没有已知消费者的情况下
  再给一个高频结构体（`HitEvent` 每帧可能有几十条）加字段，只是"为了预留而预留"。

---

## 6. 测试敌人：调试直调入口，不进正式生成池

`SimWorld.SpawnSurgicalTestEnemy(position, logicId, archetypeId, faction, coreHealth,
primaryPartHealth, secondaryPartHealth, radius)` 是新增的公开方法，内部就是拼一个
带 `PrimaryPartMaxHealth`/`SecondaryPartMaxHealth` 的 `SpawnRequest` 转发给既有
`SpawnUnit`——风格上比照 `DebugForceDraft` 这类"测试/自检直调"入口，**不接入**
`SpawnDirector`（热更层按预算购买生成敌人的正式路径，见 §勘查结论第 6 条的更正）。

两个接点用**占位标签** "PrimaryOrgan" / "SecondaryOrgan"（只在注释与本文档里出现，
不是代码里的字符串常量或枚举名）——GDD 没有给出具体器官类别，这是产品决策，
**待拍板**。默认数值（核心 200 / 每个接点 60）只是让自检里"精准切离一个接点"与
"整体死亡"两件事在数值上互不冲突，不是任何已定稿的平衡数值。

---

## 7. 自检覆盖（如实说明）

自检 `[20]` 段 21 条全绿，覆盖：

| 断言组 | 验证什么 |
|---|---|
| A/B | 两个接点分别定向伤害，互不干扰；伤害不外溢到整体 `Health` |
| C | 低伤精准命中把接点正好打到 0 → 摧毁、精准信号位为真、伤害量被记下、整体存活 |
| D | 继续打已摧毁的接点：伤害被吃掉，不外溢、不致死 |
| E | `TargetPart` 只对单体请求生效：范围伤害忽略它，确定性回退整体 `Health` |
| F | 没登记身体的普通单位收到误设的 `TargetPart`：确定性回退整体 `Health`，不吞伤害 |
| G | 打光整体 `Health` 才是真正死亡（粗暴击杀），与接点状态无关 |

**回归复核方法**：跑批之前（干净 HEAD，`git stash` 暂存本段改动）与跑批之后各执行一次
`CellFrameworkValidate.RunAll()`，把两次报告整段落盘按行 diff（而不是只比较 `Ok`/`Fail`
总数）。结果：`[1]`–`[19]` 段共 385 行逐字不变（本次两次跑批里所有数值都一致，
包括商店段——已知该段历史上会因随机商品导致总数浮动 ±1，本次两次运行都停在 385，
没有触发那个浮动，但这不代表以后每次都不会浮动），`[20]` 段新增 21 行，
总数 385 → 406，FAIL 全程为 0。原始报告见
`production/qa/evidence/_m2-05a-baseline-before.txt`（改动前）与
`_m2-05a-after.txt`（改动后，含 `[20]` 段），diff 见证据文档
`production/qa/evidence/projecta-m2-05a-surgical-body.md`。

### 没覆盖到的部分

- `[20]` 仍只测 Edit 模式下的内核基元；`[21]` 已补 `SimBridge`/热更层的 RTS 与直控真弹体路径，
  `[22]` 已补热更奖励账本。
- **单体起手 + 连锁（`ChainCount > 0`）叠加 `TargetPart` 的组合没有断言**（见 §3 第 1 点），
  只在文档里说明了当前实现下的行为，没有构造用例验证。
- 25% 精准阈值与粗暴死亡判据已由 `[22]` 覆盖；正式平衡与完整掉落仍未实现。
- **接点的视觉/表现层反馈完全没有做**（染色、独立血条、命中特效），本段只有数据与
  查询入口，`HitEvent` 也没有加 `TargetPart` 字段广播出去。
- **调试直调入口没有 `SimBridge`/热更层封装**，`SpawnSurgicalTestEnemy` 目前只能从
  `Main/Sim` 内部或直接引用 `BinGames.Sim` 命名空间的代码（如自检）调用。

---

## 8. 已知遗留（M2-05 收口后）

- **接点分类是占位**（§6），具体器官类别、数量是否固定为 2 都待产品拍板。
- **25% 精准末击阈值与每次粗暴击杀 1 生物质都是原型数值**，不是平衡终值。
- **完整器官目前只是 `PreservedOrganStub`**（来源 LogicId + 接点槽），不含正式掉落 id、品质、背包与持久化。
- **单体 + 连锁组合下 `TargetPart` 的语义**只在文档里说明，未经断言验证（§7）。
- **接点数量固定为 2**（`Primary`/`Secondary`），扩到 N 个接点、或按原型配置不同接点数量，
  都不在本段范围——`SimUnitBody` 目前是两个具名字段而不是数组，改动会牵动
  `JobDamage.TryApplyPartDamage` 的分支结构。

## 9. 非目标（M2-05 整体）

- 不做完整器官掉落池（里程碑原文明确的非目标）。
- 不把接点接入正式随机生成池 `SpawnDirector`；不新增可通过热更层触发的公开入口。
- 不给内核加"器官"概念——`SimBodyPartSlot` 是通用槽位，不是器官系统。
- 不改变任何既有单位（无接点）的行为与内存占用。

---

## 10. M2-05b：RTS 类别指定与直控具体接点

### 10.1 RTS

- `UnitCommand.TargetPart` 持久保存 Attack 命令指定的类别；默认 `None` 保持整体伤害。
- `SquadCommandSystem` 用 P 在 None / Primary / Secondary 间循环，只影响之后的新 Attack 命令。
- `WhiteboxSquadOverlay` 在战略输入域显示当前类别；Move / Guard / Retreat 不消费该字段。
- `ResolveMinionCombat` 对合法 Attack 命令锁定 `TargetEntity`，不再在混战中错误改打最近敌人。
- 目标失效、非 Attack 或自主 AI 仍走原最近敌人路径；`[21]` 组 1–5 覆盖两条正向与两条回归分支。

### 10.2 直控

- 接点只在稀疏 `SimUnitBody` 中增加 `AimOffset` / `AimRadius`，未登记单位没有额外常驻成本。
- 直控单体弹体带 `SimProjectileFlags.SurgicalAim`；普通弹体默认关闭，行为不变。
- `JobProjectile` 在实际命中帧按弹体位置选择具体接点，不在热更层预猜目标或逐帧扫描单位。
- 同一双接点敌人可分别命中 Primary / Secondary；漏过接点时继续飞，不在身体外轮廓提前结算。
- 无可瞄准接点的单位回退普通碰撞和整体伤害；范围伤害仍不定向接点。

### 10.3 原型边界

- 接点偏移随单位平移，但当前没有单位朝向数据，暂不旋转；正式骨骼挂点映射不在 M2-05b。
- 只有 Projectile 动作验证了具体接点瞄准；Cone / Zone / Status 没有单体连接点语义。
- 两个接点仍是占位类别，不接正式敌人生成池，不做完整器官掉落池。
- 精准阈值、完整器官存根与粗暴击杀生物质见 §11；正式经济与掉落仍不在本里程碑。

---

## 11. M2-05c：精准切离与粗暴击杀奖励

### 11.1 归因与阈值

- `SimBridge.FireProjectile(..., surgicalAim: true)` 只给弹体写 `SurgicalAim` 标志；飞行期间
  `SourceLogicId` 保持真实值。`JobProjectile` 确认实际命中具体接点后，才给该条单体伤害来源加封套。
- 封套使用 `10xx...`（最低负数四分之一区间），与内核从 `-1` 向下分配的普通负数 LogicId
  所在 `11xx...` 区间明确分离；RTS 来源和结构器官反伤 `-1` 都不会被误判。
- 只有直控几何命中切离接点，且 `LastHitAmount <= MaxHealth * 0.25`，才保留完整器官。
  25% 是可替换的原型常量 `PreciseLastHitMaxFraction`，不是平衡终值。

### 11.2 最小奖励账本

- `SurgicalRewardLedger` 位于热更层，模拟内核不认识“器官”或“生物质”。
- 账本只遍历本帧 `HitEvent` / `DeathEvent`；每条命中最多查询 Primary / Secondary 两个稀疏接点，
  用 `(SimEntityId, Slot)` 去重，不扫描 `UnitCapacity`，也不扩展高频 `HitEvent` 布局。
- 完整器官只记录 `PreservedOrganStub(SourceLogicId, Slot)`；不接正式掉落池、背包或存档。
- 带接点 Hostile 因整体伤害死亡（`HadSurgicalBody=1 && CauseKind=Damage`）时增加 1 生物质，
  不增加完整器官。普通敌人或 `Devour` 清除不进入这条奖励轨道。

### 11.3 验证

- `[21]` 新增生产集成断言：直控真弹体的命中事件携带可解码来源标记。
- `[22]` 11 条覆盖：低伤直控成功、RTS 低伤不保留、直控高伤不保留、粗暴整体击杀只给生物质、
  普通敌人与吞噬不重复结算。
- 2026-09-13 最终结果：快速构建 `BinGames.Sim` / `GameLogic` 双绿；Unity Console 0 error；
  Burst 开启状态全量 `PASS=450, FAIL=0`。
