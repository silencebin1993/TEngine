# 直控动作与输入契约（M2-03b 交付物）

**状态：** 已实现，随 M2-03b 一同验收（自检 `[16]` 段，33 条断言）。
**适用范围：** "接管一个单位之后，能按出什么、按下去会发生什么"。
**单一规则真相：** 本文件 + `HotFix/GameLogic/Control/DirectControlActions.cs`
+ `DirectActionSet.cs` + `OrganKernelAction.cs`。

前置契约：装配归属见 `Unit_Loadout_Contract.md`（M2-03a），输入所有权见 `View_And_Input_Contract.md`（M2-01），
编队命令与键位见 `Squad_Command_Contract.md`（M2-02）。

本段**不含**代谢 / 热债 / 冷却上屏（M2-03c），也不新增器官、基因、可交互物。

---

## 1. 这一段在修什么

M2-03a 把装配挂到了实体上，但"装配"当时还只是一张能查的表——**按键和它没有任何关系**。
直控输入仍然在按玩家自己的技能槽与 Carrier 自动开火跑，所以接管谁都一样。

里程碑验收原话是「两个不同装配单位被接管时动作集不同**且与实体一致**」。
后半句是本段的全部难点：只让数据不同、行为却一样，是不算过的。

---

## 2. 动作集为什么是"编译"出来的，不是每帧查

`DirectActionSet` 只在两种时刻重建：

| 时机 | 触发点 |
|---|---|
| 控制权变更 | 订阅 M1-06 的单一出口 `ControlledUnitChangedSignal` |
| 装配延迟登记落地 | `CellStageFlow.Update` 里 `ResolvePending` **返回 > 0** 的那一帧 |

`Bind` 时也会编译一次——开局第一帧按键就该有反应，不必等到第一次切换控制权。

**不每帧查注册表**的理由是硬约束而不是优化偏好：M2-03a 契约 §10 已经写明，
每帧对多个单位调 `UnitLoadoutRegistry.Get` 在冷启动那一帧是 O(单位数 × 查询数)，
而热更层每帧必须与场上单位数无关（仓规架构红线第 4 条）。
事件驱动把它压成"每次切换一次"。

第二条触发（`ResolvePending > 0`）补的是一个真实窗口：友军的装配是延迟登记的，
接管发生在登记落地之前时，那一刻编译出来的是空装配。只挂控制权变更会让它一直空到下次切换。
注意判据是**返回值 > 0**，不是每帧无条件重建——稳态下 `ResolvePending` 一行都不扫。

---

## 3. 缓存不是释放判据

`ActionSet` 是给查询 / UI 用的。**释放一律走 `DirectControlActions.TryRelease`，那里重新读一次实时装配。**

理由：装配随时会变（玩家选卡装上新器官、器官被打坏失能），而动作集只在控制权变更时重建。
只信缓存的话，"装配已经变了但还没切过控制权"的那段窗口里会按出一个已经不存在的动作。
重读的代价是 O(器官数)，且只发生在按键那一刻，不在每帧路径上。

自检 `[16]-D` 专门守这条：**失能之后不重建动作集**就直接释放，必须被拒。

---

## 4. 失能为什么必须在释放入口拦截

只在 UI 上灰掉、不在入口拦，按键照样生效——那是「显示禁用、实际可用」，
玩家会把它当成随机失效，是最难查的一类 bug。

`TryRelease` 的拦截顺序固定，且每一级给出**不同的原因**（`DirectActionAvailability`）：

| 顺序 | 判据 | 拒绝原因 |
|---|---|---|
| 1 | 动作是 `Move` | `NotAReleaseAction`（移动是每帧意图，不是一次性动作） |
| 2 | 没有有效受控实体 | `NoControlledUnit` |
| 3 | `TryGetOrgan` 拿不到未失能器官，且该槽**有**器官 | `OrganDisabled` |
| 4 | `TryGetOrgan` 拿不到，且该槽**没有**器官 | `NoOrgan` |
| 5 | 交互动作且无可交互目标 | `NoInteractTarget` |
| 6 | 玩家本体委托路：被委托的技能槽没就绪 | `NotReady` |
| 7 | 友军内核路：这件器官没有可释放形态 | `NoKernelAction` |

**"能不能按"与"为什么不能按"分开存**是刻意的：只给一个 bool，UI 只能显示"灰的"，
玩家读不出是"这具身体没长这东西"、"长了但坏了"还是"现在没有目标"——三件事的处置完全不同。
为此 `UnitLoadout` 新增了 `HasOrganInSlot`（含失能），与 `HasAction`（只算未失能）配对。

`DirectActionSet.Rebuild` 的判定顺序与 `TryRelease` 保持一致（交互目标先于内核形态判），
否则 UI 上写的原因会和实际拒绝的理由对不上。

---

## 5. 玩家本体与友军为什么是两条释放路径

这是本段最容易被误认为"没做完"的地方，所以写清楚：**它是设计，不是妥协的残留。**

### 玩家本体 → 委托

玩家本体的输出手段早就有一整套：`AbilitySystem` 的技能槽（槽 0 手动冲刺、槽 1..N Ready 即自动施放）
+ `MetabolicSliceRunner.TickCarrier` 的 Carrier 自动开火。它们和手感、体力账本、卡牌触发全绑在一起。

在这里另起一条释放路径，等于给玩家本体加**第二个伤害来源**——回归风险最高的地方。
所以 `Primary` / `Utility` 在玩家本体上只是 `AbilitySystem.TryCastAuto(1)` / `TryCastAuto(2)`。

槽位刻意落在 1..N 区间：那一段本来就 Ready 即自动施放，所以这两个键是**幂等委托**——
按下去时槽多半正在冷却，什么都不会多发生。槽 0 是冲刺（Space），不在这里，
否则右键会变成闪避，那就是真的改了手感。

### 非玩家友军 → 一次性写内核

`AbilitySystem` 是玩家全局单例，友军身上根本没有对应实例，委托无从谈起。
所以按动作集里那件器官的参数**一次性向内核下一条请求**（同 M2-02 `IssueCommand` 的模式）：
下令那一刻付出常数代价，逐帧的事全归 AOT 作业，热更层这里一个循环都没有。

### 两条路刻意不统一

统一它们意味着把 `MetabolicSliceBridge` 从"钉死在玩家本体"改成"任意实体都能用"——
那条路径从头到尾是 `MetabolicSlicePanel.Instance` 取装配、`_sim.PlayerPosition` 取落点、
一个全局 `_timer` 驱动节奏。这是一次独立的大重构，本段碰它必然超时返工。

---

## 6. 玩家专属路径在接管友军期间必须停

上面两条路能并存，前提是它们不会同时开火。所以加了两道同判据的闸门：

| 位置 | 停的是什么 |
|---|---|
| `CellPlayerController.OnUpdate` | `PollAbilityInput`（玩家技能槽，含 Space 冲刺与槽 1..N 自动施放） |
| `MetabolicSliceBridge.OnUpdate` | Carrier 自动开火那一拍 |

判据统一为 `SimBridge.ControllingPlayerBody`（阵营判定：世界槽位 0 恒为 `SimFaction.Player`，
可接管友军一律 `PlayerMinion`）。**不另存"玩家本体 id"字段**——那是第二个会漂移的真相源。

不加这两道闸门的话，不管接管谁，打出来的都是玩家那套装配，
「动作集与实体一致」在行为层面当场不成立——而里程碑验收问的正是行为。

**玩家控制自己本体时这两个判断恒为 true，原有行为一行未变。**

---

## 7. 器官 → 内核请求：这张表凭什么算"与器官对应"

`OrganKernelActionTable.Resolve` 的判据**全部**来自 `OrganelleCatalog` 里那件器官自己的条目：

| 器官条目上的东西 | 落成的内核请求 |
|---|---|
| `Category == Structural` + `TriggerHook.ThornsRatio > 0` | `Zone`（跟随自身的杀伤区），伤害按反伤比例 |
| `Category == Structural` + 无反伤 | `Status`（范围挂标记，零伤害），状态位走 `StructuralHookRunner.ParseTag` |
| `AttackFamily == "Cone"/"Melee"` | `DamageCone`，扇角取 `OrganModuleParams.SpreadAngle` |
| `AttackFamily == "Pool"/"Rain"` | `Zone` 钉在瞄准点 |
| `AttackFamily == "Aura"` | `Zone` 跟随自身，半径取 `AuraRadius` |
| `AttackFamily == "SummonAnchor"` | `Zone` 钉在瞄准点（"钉一根菌丝炮台"） |
| `AttackFamily == "SummonFollow"` | `Zone` 跟随自身 |
| 其余攻击器官 | `FireProjectile`（内核真弹体） |
| `IsRetired` 或 `AttackMethod == false` | **无动作**（`NoKernelAction`） |

**没有任何一条分支按 organId 写死特例**，所以"这个单位为什么能打出这一发"永远追得到来源。
查不到形态时**不许退回一发通用弹**——那会让所有单位打出同一种东西，验收当场失效。

`ParseTag` 为此从 `private` 改成 `public`：复制一份的话，同一件器官在"常驻触发"和"直控释放"
两条路上会挂出不同的状态位。

### 三条如实记录的降级

1. **召唤底盘落成区域，不是真召唤。** 被召唤的 archetype id 只存在于 ComposeEngine 模块实例内部
   （`SummonModule.summonId`），`OrganelleDef` 没有把它暴露出来。凭空指定一个 archetype 等于发明内容，
   所以取语义最近的内核原语。等召唤 id 有正式出口再换成 `SimBridge.Spawn`。
2. **伤害是常数（`DefaultDamage`）。** 器官的伤害数值今天只在 ComposeEngine 跑完整条装配链之后
   才以 `HitEvent.Damage` 出现，热更层没有"问某件器官打多少"的入口。在这里复制一份数值管线，
   等于制造第二个会漂移的真相源。**形状由器官决定，数值统一**——这是本段刻意接受的最小实现边界。
3. **状态区域在 `StatusSystem` 缺席时退回 `SimBridge.ApplyStatusArea`（无时限）。**
   生产路径注入了 `StatusSystem`，走有时限的那条；Edit 模式回归起不了整套 `ModuleHub`，
   走无时限的兜底。可注入正是为了让这条路径能写真断言（同 M2-03a §4 的理由）。

---

## 8. 内核零改动

`Assets/GameScripts/Main/Sim/` 本段**一行未改**。
四种释放形态用的 `FireProjectile` / `DamageCone` / `SpawnZone` / `ApplyStatusArea`
全是 `SimBridge` 上的既有入口。热更层碰内核仍然只有 `SimBridge.cs` 这一个口子。

`SimBridge` 唯一的新增是只读属性 `ControllingPlayerBody`（O(1)，只读快照），不新增任何内核能力。

---

## 9. 键位（产品决策，可推翻）

全部在 `Direct` 域。与 M2-02 的 `Strategy` 键表**同一批物理键**，靠 `InputScope` 互斥——
这已是 M2-01 立下的既定模式，不靠各处自己判断镜头状态。

| 键 | 含义 | 与 `Strategy` 域的对照 |
|---|---|---|
| `WASD` / 方向键 | 移动 | 该域下是镜头平移 |
| 鼠标左键 | 主器官动作 | 该域下是框选 |
| 鼠标右键 | 功能器官动作 | 该域下是智能命令 |
| `E` | 交互 | 该域未占用 |
| `Space` | 冲刺（技能槽 0） | 该域下是战略暂停开关 |
| `Tab` | 切换控制目标 | 该域未占用 |

鼠标键用 `InputRouter.ConsumeKeyDown(KeyCode.Mouse0/1, Direct)` 而不是 `Input.GetMouseButtonDown`：
后者绕开了 `InputRouter` 的同帧唯一性，"不产生双重输入"就又变成靠自觉了。

### `CellPlayerController` 里已无裸 `Input` 调用

任务书列的"收口裸 `Input.GetKeyDown`"这一条，M2-01 已经做完了——
本段核对确认该文件 100% 走 `InputRouter`，没有可收的口。
`View_And_Input_Contract.md` §8 列的面板/调试键（`BattleHudToolkit` U/N、`BattleCarrierUIToolkit` O、
`BattleSandboxUIToolkit` L、`StressTestToggle`/`SimStressTest` F11）本段**未动**，仍在那份清单里。

---

## 10. `Interact` 为什么是空的

槽位、键位、可用性判定全都在，但 `DirectControlActions.InteractTargetsAvailable` **恒为 `false`，
且没有任何生产代码会写它**。

GDD 里的"交互"指拾取野生器官 / 临时移植，那些对象在仓库里根本不存在。
造一个假的交互物出来凑验收，等真正的拾取系统进来时必然要连带拆掉，
而在此之前它会让"E 键为什么没反应"变成一个查不清的问题。

所以这里只留一个接缝，明确判为 `NoInteractTarget`。

附带一条 M2-03a 遗留的事实：原型表给菌丝体的交互器官是 `org_hook`，而它在目录里
**已退役**（`IsRetired = true`，效果已迁到 `org_cilia + gene_return`）。即便将来有了交互目标，
它也会落到 `NoKernelAction`。真要开交互时需要先换掉这个 id——这属于内容决策，本段不改。

---

## 11. 生命周期

| 时机 | 动作 | 在哪 |
|---|---|---|
| `RegisterModules` 末尾 | `new DirectControlActions()` 并交给 `CellPlayerController` | `CellStageFlow` |
| `SetupUnitLoadouts` 末尾 | `Bind(sim, registry, abilities, status)` + 首次编译 | `CellStageFlow` |
| 控制权变更 | 订阅重建 | `ControlledUnitChangedSignal` |
| 每帧（`ResolvePending > 0` 时） | 重建 | `CellStageFlow.Update` |
| `Exit` | `Unbind()`，**排在 `_unitLoadouts.Unbind()` 之前** | `CellStageFlow` |

`Unbind` 必须先行：它订阅着控制权变更信号，留着会在下一局用上一局的注册表引用重建动作集。

与 `CameraDirector` / `SquadCommandSystem` / `UnitLoadoutRegistry` 同理**不进 `_hub`**：
控制权在暂停下也可能变（死亡回弹、读档恢复），而 `_hub` 会被暂停早退整个冻住，
冻住它会让恢复之后的动作集停在上一具身体上。

---

## 12. 验收覆盖程度（如实说明）

自检 `[16]` 段 33 条全绿。真实行为断言，不是字段比对：

- 两名友军释放后去内核里看真的落下了什么（孢子 → 敌人挂上状态位且**不**产生区域；
  菌丝体 → 场上真的多出一块持续区域）。两者互为对方的反证。
- 失能后**不重建**直接释放 → 被拒 + 内核里零残留（证明拦截在入口，不在缓存）。
- 让位那一帧塞一个非零意图再跑输入帧，断言意图被清空（而不是"它本来就是零"）。

**只做了结构部分的项：**

- **"直控键在 Direct 域拥有输入时生效"这个正方向没测。** Edit 模式敲不出真实按键，
  测的是让位那一侧（三种让位来源下都不拥有输入、不发生释放尝试、意图被清空）。
  释放逻辑本身通过直调 `TryRelease` 全覆盖。
- **`Interact` 只有结构**（见 §10），没有任何可交互内容。
- **玩家本体的委托路径没有端到端断言**：Edit 模式起不了 `AbilitySystem` 的整套依赖，
  `[16]` 里 `abilities` 传的是 null，走到委托路会落 `NotReady`。
  该路径的零回归靠"它只调既有 `TryCastAuto`、且落在本来就自动施放的槽位"这条论证，不是靠断言。

---

## 13. 非目标（M2-03b 明确不做）

- 不做代谢 / 热债 / 冷却的计算与上屏（M2-03c，里程碑实施第 4 条）；
- 不统一 `AbilitySystem` 与 `MetabolicSliceRunner` 两条攻击路径；
- 不重建 M1-06 的控制生命周期链路（本段只复用并回归验证）；
- 不改 `MetabolicSlicePanel` / `CarrierRegistry` / `BagInventory` / `SlotGrid` 的单例结构；
- 不新增器官、基因、敌人、美术、可交互物；
- 不改内核。

## 14. 已知缺口（交给后续里程碑）

- **友军释放没有冷却/资源代价**：按一下打一下。冷却与代谢开销是 M2-03c 的内容，
  在这里先造一套必然和那边对不上。
- **友军释放没有表现层反馈**：不发 `AbilityCastSignal` / `ComposeCastSignal`——
  那两条信号的消费方都假设施放者是玩家本体（读 `PlayerPosition`）。
- **伤害数值统一**（见 §7 降级 2）。
- **召唤底盘不是真召唤**（见 §7 降级 1）。
- **`org_hook` 已退役**（见 §10）。
