# M1–M4 需求覆盖差距审计（M4-R00，正式交付物）

> **GDD 章节号（FG0-DOC-01 注）：** 本文件里的"GDD §x"都指生物机械版 GDD（0.1 及更早），原文见 [ProjectA_GDD_bio-mechanical_archived-2026-09-18.md](../Archive/ProjectA_GDD_bio-mechanical_archived-2026-09-18.md)，与现行 GDD 0.2（`../ProjectA_GDD.md`）的章节不对应。

> 生成于 2026-09-15。依据 `detailed/05_M1_M4_Backfill_And_Acceptance.md` §2 M4-R00-01/02 执行。
> 方法：4 个并行只读子 agent，按 05_ §3~§6 的返工包拆块（M1/M2/M3/M4），各自实读设计文档
> （`detailed/00~04`、生物机械版 GDD（见上方注）、`migration/*.md` 相关契约）与代码（`Main/Sim/`、
> `HotFix/GameLogic/`、`Assets/Editor/CellFrameworkValidate.cs`），全程未修改任何游戏代码。
> 判定口径（`00_Implementation_Completeness_Contract.md`）：字段已存在／可深层直调／带 TODO
> 一律不得判「完整」，只能判「部分」或「缺失」。

## 范围说明（诚实披露，非全量宣称）

- 本文件覆盖的 REQ-ID 集合 = `05_` 文档 §3~§6 在各返工包"需求："行里点名的编号，即
  05_ 作者已完成的"哪些编号属于 M1-4 范围"筛选结果。**逐条核对 `00~03` 全文后确认**：
  - `CP-REQ` 全系列（001~092，`02_` 文档定义）**全部**在本文件出现且只出现一次（M2 块）。
  - `FC-REQ` 系列除 **`FC-REQ-060`（必须可见）** 外全部覆盖；`FC-REQ-060` 未单独判定，
    但其内容（命令/队列/教义/参数必须可见）已被 M4 块的 FC-REQ-022（队列无 UI）与
    FC-REQ-061（直控 HUD 信息不全）两行实质覆盖，判定沿用那两行：**部分/缺失**。
  - `IC-REQ-001/002/003/014/020/021/022/023` 是 `00_` 契约里的**方法论条款**（需求 ID 规范、
    封闭清单规则、非目标边界、不隐藏占位、需求覆盖率、反向旅程、体验审核边界），不是可独立
    判定"当前实现"的功能点，本次四块审计的判定过程本身即在遵守它们，故不单独列行。
  - `FS-REQ` 系列**只有** `FS-REQ-001/002`（M4-06 引用）与 `FS-REQ-030`（M2-R04 引用）被
    `05_` 点名为 M1-4 相关；`01_Faction_Survival_And_Ecology.md` 里其余 25 个编号未被 `05_`
    点名。**此处曾推断"疑似 M5+，可延后"，已被 bin 明确否决并要求逐条重审**：全部 28 个
    `FS-REQ` 逐条审计结果见 `FS_REQ_M1_M4_Blocking_Audit.md`——28 条中 2 条直接阻塞
    （FS-REQ-031/064）、21 条接口阻塞、仅 5 条（FS-REQ-004/042/051/052/062）真正可延后。
    原"整体可延后"推断在 28 条里错了 23 条。已折算进 `M4_R00_02_Rework_Queue.md`。

---

# M1 返工包：控制身份与生命周期（05_ §3）

代码基线：`Assets/GameScripts/Main/Sim/`、`Assets/GameScripts/HotFix/GameLogic/`（Battle、Control、
Stage/CellStage、UI、MetabolicSlice、Spawning、Command/Formation）与 `Assets/Editor/CellFrameworkValidate.cs`。

## M1-R01 身份唯一性全入口复验（IC-REQ-010～013；CP-REQ-002/FC-REQ-001 见 M2/M4 块）

| REQ-ID | 当前生产入口 | 当前实现 | 自动测试 | 判定 | 用户症状 | 修复故事 |
|---|---|---|---|---|---|---|
| IC-REQ-012 | `SimWorld.FallbackControlAfterLoss` / `GetControlCandidates` / `SwitchControlledUnitInternal` | 唯一收口：主动切换、`RestoreControlTo`、死亡回弹全部走 `SwitchControlledUnitInternal`/`FallbackControlAfterLoss`；判据只有阵营+距离+ID排序，无硬编码特判 | `[12]`：回弹确定性；`[24]`-C：候选含晚生成友军、排除召唤物 | 完整 | 无 | 无 |
| IC-REQ-013 | `SimBridge.RequestControlSwitch`→`ControlRequestResult`；`BattleHudToolkit.DescribeControlChange` | 失败码区分 `WorldNotInitialized/InvalidTarget/TargetNotFound/TargetDead/TargetNotFriendly/AlreadyControlled`；`ControlAvailability` 分 Suspended/None 渲染 | `[2.4]`/`[12]`/`[24]` | 部分 | HUD 只有一种笼统"控制目标丢失"文案，不区分敌人/中立/召唤物 vs 目标已死 | 补 HUD 文案分支，传具体失败码给 UI 层 |
| IC-REQ-010 | `SimWorld.Step` 产出 `HitEvent`/`DeathEvent`；`SpawnDirector.EncodeLogicId` | 控制身份单一真相成立，但伤害/死亡归因仍用内容 `LogicId`（`EncodeLogicId` 高16位实例序号"当前未用"）——2026-09-11 审计（`Controlled_Unit_Audit.md` A03/A04）标记至今未落地 | 无场景覆盖"两个同类敌人同时存活时来源可唯一区分" | 部分 | 多个同类敌人同场时命中/死亡来源可能记混，无自动测试排除 | 与 M2-R01（来源链）一起把 `SourceLogicId`/`KillerLogicId` 迁到 `SimEntityId` |
| IC-REQ-011 | `CellStageFlow.Enter`→`SetupUnitLoadouts`/`SpawnControlAllies`；`ControlPersistence.Save/Load` | `[24]` 是真正生产入口 E2E：4 具身体、6 次切换；存档往返+坏JSON+旧版本三路径不抛异常 | `[24]`、`[12.1]` | 部分 | 未覆盖 M1-R01 要求的"三名以上友军+数组重排"专项；槶位复用仍停留在集成测试层；100 次连续切换目前只做到 20 次 | 补 `CellStageFlow` 驱动的 N≥3 友军+死亡槶位复用+Resume 组合 E2E，量级提到 100 次 |

## M1-R02 临时脚手架清账

| REQ-ID | 当前生产入口 | 当前实现 | 自动测试 | 判定 | 用户症状 | 修复故事 |
|---|---|---|---|---|---|---|
| M1-R02-SPAWNCONTROLALLIES | `CellStageFlow.SpawnControlAllies()` | 两名可控友军坐标写死 `(-4,2)`/`(4,2)`，装配按顺序 index 分派；`Control_Lifecycle_Contract.md` §7 已自认这是验收脚手架 | 无（`[24]` 反而依赖坐标不变） | 部分 | 换关卡布局/正式刷怪需改代码而非配置表 | 迁正式配置表/由M5网络接管/保留测试夹具三选一，本次审计未见任何一项被正式选定 |
| M1-R02-LOGICID-ORDER-DEPENDENCY | `Control_Lifecycle_Contract.md` §6 恢复顺序 | 跨世界重建靠 `ControlledLogicId` 精确匹配，依赖 `SpawnControlAllies` 每局同顺序生成；未给出"生成顺序未来必须变"时的旧存档迁移方案 | 无 | 缺失 | 未来若调整生成顺序/数量，老存档可能恢复到错误个体 | 按 M1-R02 要求建立迁移与旧存档映射 |
| M1-R02-SIGNALRANGE-COOLDOWN | `SimBridge.DefaultControlSignalRange`(=1,000,000f)、`DefaultControlSwitchCooldown`(=0.15f) | 硬编码常量，非正式信号网络；两份契约文档均已承认是临时装置 | 无 | 部分 | 当前对玩家不可见，但无 `DEBT-ID` 登记（全目录搜索确认没有实际登记条目） | 三选一并登记 `DEBT-ID` |
| M1-R02-TESTFIXTURE-ISOLATION | `CellStageEntryMode.ConsciousnessPlaytest` | 测试/固定试玩夹具通过独立枚举与专属常量隔离，`Enter()` 消费后立即复位，无跨局残留 | `[23]` | 完整 | 无 | 无 |

## M1-R03 UI/输入恢复矩阵

| REQ-ID | 当前生产入口 | 当前实现 | 自动测试 | 判定 | 用户症状 | 修复故事 |
|---|---|---|---|---|---|---|
| M1-R03-INPUT-SCOPE-ARBITRATION | `InputRouter.ConsumeKeyDown/ConsumeGlobalKeyDown`、`InputScope` | 单一仲裁者，历史双消费 bug已通过改键+统一路由解决；持续键靠 `InputScope` 互斥 | `[13]`，但 Editor 非 Play 下 `Input.GetKeyDown` 恒 false，实测按键路径未测；手柄适配无痕迹 | 部分 | 无自动测试跑过真实按键路径；手柄适配空白 | 需可注入输入源驱动真实按键路径断言 |
| M1-R03-INPUT-MODAL-PAUSE | `InputRouter.ModalUiOpen`（`_modalUi \|\| _gameplayPaused`） | 战略暂停与普通暂停目前**行为等价**，代码自认"当前仓库所有暂停都伴随模态面板"，两者未分裂成两条路径 | 无 | 缺失 | 战略暂停下应能选目标/平移镜头，实际暂停即锁全部输入 | 依赖尚未开工的 M2-02（RTS战略暂停下达命令）落地后回填 |
| M1-R03-CAMERA-TARGET-LOST | `CameraDirector`、`TryGetPresentationAnchor` | 自动降级：受控实体消失即转 Strategy；`RequestDirect()` 无有效目标时返回 false；镜头不写模拟状态 | `View_And_Input_Contract.md` §5/§6；`[13]` | 完整 | 无 | 无 |
| M1-R03-CONTROL-EMPTY-DEGRADE | `SimBridge.Availability`；`BattleHudToolkit.cs:409-416`、`BattleMainUI.cs:109-115` | 晚生成 vs 真无目标两种情况用不同文案区分 | 直读代码确认，无 UI 层自动断言 | 部分 | UI 文案分支无自动化保护 | 补纯函数级单元断言 |
| M1-R03-CONTINUOUS-SWITCH-STRESS | `[2.4]`（20次）、`[24]`（6次） | 现有最大连续切换 20 次，未覆盖 M1-R03 要求的 100 次及双输入/瞄准线残留/空引用/GC稳态检查 | 见左 | 部分 | 长时间连续换人是否累积内存分配/空引用无法验证 | 量级提到100次+补GC/残留/双输入断言 |

**M1 小结**：完整3/部分8/缺失2/冲突0。最严重：战略暂停与普通暂停结构性未分裂（依赖M2-02）；
伤害/死亡归因仍用非唯一LogicId是2026-09-11遗留至今未落地的真实缺口。

---

# M2 返工包：同一身体的完整战斗真相（05_ §4）

全仓 Grep 确认：**`EmissionContext`、`AbilityRecipe`、`PrimitiveId`/`ProcBudget`/`FailureFallback`/
`DeterminismKey` 这些规格核心类型/字段名零命中**——是下表所有"缺失"判定的共同根因。

| REQ-ID | 当前生产入口 | 当前实现 | 自动测试 | 判定 | 用户症状 | 修复故事 |
|---|---|---|---|---|---|---|
| CP-REQ-001 世界平面 | 全仓 `float2(x,z)` | 位置字段统一 float2；Y只在表现层 | 多段间接覆盖 | 完整 | 无 | — |
| CP-REQ-002 EmissionContext | 无 | 全仓零命中；发射参数分散在 `OrganReleaseRunner.Release`、`CombatBallistics.Build`、`AbilitySystem.TryCastInternal` 三处各自拼装 | 无 | 缺失 | 切控制者时"发射用谁的数据"无法审计 | 新故事：统一 `EmissionContext` |
| CP-REQ-003 发射点 | `OrganReleaseRunner.Release` | 只有身体中心沿方向前推 `MuzzleClearance=0.2`；无器官真实挂点；规格自己§12已登记此差距未关闭；无越界/障碍拒绝 | 无 | 缺失 | 炮口位置与器官视觉不对应，卡障碍时直接从身体中心出弹 | 新故事：真实挂点数据+越界拒绝 |
| CP-REQ-004 身体朝向 | 各处硬编码 fallback `(0,1)` | 全仓 `Main/Sim` 无 `Forward`/`Facing` 字段；朝向缺省不是"最后有效朝向" | 无 | 缺失 | 无输入/无目标时朝向瞬间被重置 | 新故事：内核加身体朝向字段 |
| CP-REQ-010 玩家瞄准 | `CellPlayerController` | 指针失效退到硬编码常量而非两级回退（依赖004） | `[16]` 部分覆盖 | 部分 | 鼠标移出窗口瞬间瞄准方向不可预测 | 依赖004关闭 |
| CP-REQ-011 AI瞄准 | `SimWorld.ResolveHostileRangedCombat` | 直线瞄准目标当前位置，无拦截提前量计算 | 无 | 部分 | 敌人远程命中率与玩家走位无关 | 新故事：拦截解算 |
| CP-REQ-012 多发扇角 | `CombatBallistics.FanDirection`等 | **四个互相独立的角度函数**，公式参数各不同 | 无 | 冲突 | 不同攻击形态扇形手感彼此不可预测不一致 | 新故事：合并为单一角度函数 |
| CP-REQ-013 转向旋转 | `JobProjectile.Steer/Execute` | Homing/Bounce/Weave有真实实现；Return/Orbit独立锚点未见处理 | 无 | 部分 | Return弹体降级路径未验证 | 补Return降级路径 |
| CP-REQ-020 阶段枚举 | 隐含在调用顺序中 | 无显式9阶段封闭枚举，无法被基元目录引用 | 无 | 缺失 | 无 | 新故事：显式阶段枚举+基元目录 |
| CP-REQ-021 基元声明 | 无 | `PrimitiveId`等全仓零命中，无基元目录数据结构 | 无 | 缺失 | 新增基元只能改代码switch | 新故事：基元目录 |
| CP-REQ-030 Projectile | `JobProjectile.cs` | 真实弹体状态、sweep命中、穿透保留预算、判定渲染同状态 | 部分覆盖 | 部分 | 起点缺陷(003/004)会污染 | — |
| CP-REQ-031 Melee/Cone | `SimBridge.DamageCone` | 真实扇形判定，方向来源用四个FanDirection之一 | `[16]`间接 | 部分 | 同012 | 依赖012 |
| CP-REQ-032 Field | `SimBridge.SpawnZone` | 有跟随/抛投倍率；目标失效降级分支未见 | 无 | 部分 | 场地器官目标消失后行为未定义 | 补降级分支 |
| CP-REQ-033 Aura | `ZoneMode`字段 | 有跟随实现；Count与TickRate是否独立未见证据 | 无 | 部分 | 光环脉冲次数与频率可能被误乘 | 核实Count/TickRate独立性 |
| CP-REQ-034 Summon | `EffectSpawn.Execute`/`BehaviorArchetype` | 真实SpawnRequest+代际硬顶+排除接管；无维护成本/容量上限/死亡资源结算 | 部分覆盖 | 部分 | 维护成本与死亡资源结算未见代码 | 补维护成本与死亡回收结算 |
| CP-REQ-040 Knockback | `SimBridge.Knockback`→`ApplyKnockback` | 真实位置改变，按阵营过滤+边界clamp | 无 | 部分 | 击退手感真实存在 | 补体型参与位移验证 |
| CP-REQ-040 Pull | `CombatBallistics.Build` | **仅贴状态**：只叠加Slowed｜Pulled，无真实位移/牵引，直接违反禁止项 | 无 | 缺失 | 应"被拉向"某点，实际只是减速+图标 | 新故事：真实牵引位移 |
| CP-REQ-050 默认可组合 | ComposeEngine | 多基元字段无条件合入同一Request，无"组合结果确定性证明"校验层 | 无 | 部分 | 无法排除字段组合互相覆盖 | 新故事：组合矩阵验收 |
| CP-REQ-051 显式冲突 | 无 | 无"拒绝并返回冲突原因"实现 | 无 | 缺失 | 无 | 新故事 |
| CP-REQ-052 预算继承 | `JobProjectile.MaxGeneration=2`硬编码 | 只有全局硬顶，无ProcBudget/来源链/随机子种子字段化系统 | 无 | 部分 | 极端组合是否无限递归只能靠硬顶兜底 | 新故事：ProcBudget系统 |
| CP-REQ-053 顺序 | ComposeEngine | 战斗基元层未见交换顺序结果相同的代数证明 | 无 | 缺失 | 无 | 新故事 |
| CP-REQ-060 同一身体账 | `AbilitySystem`(玩家)vs`UnitVitalsRegistry`(非本体) | **两套并行资源模型**，非同一Reserve→Commit/Refund事务 | 无 | 冲突 | 玩家本体与被接管友军资源/冷却语义架构上就是两套账 | 新故事：统一账本或明确分层决策 |
| CP-REQ-061 提交时机 | `UnitVitalsRegistry.Commit` | 释放成功后才扣代谢/起冷却，但只覆盖非本体路径；无全额退款代码 | `[16]`部分 | 部分 | 见060 | 依赖060 |
| CP-REQ-070 目标过滤 | `DamageRequest.TargetFaction` | 显式阵营掩码；已命中集合仅Chain内单跳去重，非通用去重 | 部分覆盖 | 部分 | Pierce/Chain之外组合可能重复命中 | 补通用已命中集合 |
| CP-REQ-071 接点 | `SimBodyPartSlot`+`JobDamage` | 固定2占位槽（非N接点）；无朝向数据不旋转；测试敌人未接正式生成池 | `[20][21][22]` | 部分 | 手术窗口只在测试敌人上可见 | M2-R05整段要求 |
| CP-REQ-080 同源表现 | `CombatBallistics`常量 | 弹体/爆炸半径共享；角度公式已分裂4份(012)，表现层未必读同一份 | 无 | 部分 | 扇形/角度视觉与判定可能读不同公式 | 依赖012 |
| CP-REQ-081 拒绝反馈 | `DirectActionAvailability`枚举 | 覆盖度较好；缺炮口阻挡/落点非法/召唤容量/信号权限对应码 | `[16]`部分 | 部分 | 少数拒因无从触发因003未实现 | 依赖003 |
| CP-REQ-082 可访问性 | 无 | 未搜到瞄准辅助/接点吸附/危险区轮廓/色盲替代 | 无 | 缺失 | 无 | 新故事 |
| CP-REQ-090 发射者死亡/换控 | `ProjectileRequest.SourceLogicId`值类型固化 | 结构上大概率正确，但无专门测试锁定契约 | 无 | 部分 | 无回归保护 | 补CP-TEST-006用例 |
| CP-REQ-091 目标死亡/卸载 | `JobProjectile.Steer`每帧重搜目标 | 只有一种硬编码"重找最近敌人"行为，非三态可配置降级 | 无 | 部分 | 追踪弹无法配置为直飞/终止 | 新故事：按配方可配置降级 |
| CP-REQ-092 存读档 | 无 | 未见弹体/场/召唤/持续事件序列化代码 | 无 | 缺失 | 战斗内存读档尚未存在，阻塞性待与里程碑口径确认 | 需与存读档里程碑排期 |
| FS-REQ-030 共享能力清单 | 无 | `01_`定义为阵营共享能力清单，但`05_`附表把FS-REQ-030～031列为延后M5/M6，M2-R04需求行又点名它——**两处描述互相矛盾** | 无 | 冲突 | 无(面向玩家能力未设计) | 需产品先澄清归属再决定是否需要M2故事 |
| M2-R04 Interact | `DirectControlActions.TryRelease(Interact)` | **恒定拒绝**：注释自认"只做结构不发明可交互内容"；已有真实`WildOrganRegistry.TryPickup`完全独立未接入 | `[16]`部分 | 缺失 | 装了交互器官按键永远无反应 | 新故事：接`Interact`到`WildOrganRegistry`等真实目标 |
| M2-R06 真实输入注入 | `InputRouter` | 状态注入完整，但按键本身读`UnityEngine.Input.GetKeyDown`，Edit/批处理下不能真模拟按键；现有断言普遍深层直调绕过输入层 | `[16]`等均深层直调 | 部分 | 无法证明真实按键路径与深层直调结果一致 | 新故事：可注入按键模拟层 |

**M2 小结**：共41行，完整1/部分22/缺失14/冲突4。**最严重缺口**：同一身体、玩家/AI打出同一装配
同结果这条M2核心承诺在架构上不成立——玩家本体走`AbilitySystem`/`_wallet`，被接管身体走
`OrganKernelAction`/`UnitVitalsRegistry`，两套系统无共同`AbilityRecipe`/`EmissionContext`抽象，
`DirectControlActions`注释自认"这是两条路"。次severe：手术接点仍是2槽占位不进正式池；
`Interact`被硬编码恒拒绝——这两条直接对应里程碑承诺的"直控独特价值"体验入口，玩家完全摸不到。

---

# M3 返工包：蓝图、模板与真实传播（05_ §5）

代码基线：`MetabolicSlice/{Carrier,Lineage,WildOrgan}/`、`Control/UnitLoadout*.cs`。
`CP-REQ-002/060` 的正式判定行在 M2 块；本块只记录蓝图/模板/传播视角的现状，不重复判定。

| REQ-ID | 当前生产入口 | 当前实现 | 自动测试 | 判定 | 用户症状 | 修复故事 |
|---|---|---|---|---|---|---|
| M3-R01-SLOT-EXPR-MAIN | `LineageRegistry.CommitTemplate`，唯一调用方是`CellDebugHud` | 只表达1主器官+2~4有序基因，无底盘/功能器官/结构器官/真实教义槶位 | `[27]`（纯内存） | 部分 | 玩家看不到底盘/结构/功能器官槶，与GDD§6.5五槶承诺不符 | 新故事：补三槶+正式教义行为 |
| M3-R01-SLOT-EXPR-MOVE | 无 | 移动器官在装配模型里无处安放，`Unit_Loadout_Contract.md`§6/§14已列为已知缺口 | 无 | 缺失 | 装移动器官后速度参数不变 | 新故事：移动槶改移动参数 |
| M3-R01-SLOT-EXPR-MULTI-UTILITY-ORDER | `CarrierRegistry.All`(Dictionary) | 多件Utility器官顺序不确定，迭代顺序不保证 | 无 | 缺失 | UI展示顺序与生效顺序可能逐帧不同 | 新故事：显式排序字段 |
| M3-R01-SIG-NO-RUNTIME-STATE | `PhenotypeTemplateSignature.Compute` | 只用OrganelleId+有序GeneIds做SHA-256，不含运行态 | `[27][28]` | 完整 | — | — |
| M3-R01-UI-COMPILER-SAME-RULE | `CellDebugHud`与`LineageRegistry`共用`ValidateCommit` | 规则同源但载体是调试HUD非正式UI | 无 | 部分 | 玩家看不到这套规则 | 随M3-R05一并解决 |
| M3-R02-COMPILE-SIG-CACHE-ONCE | `CompiledRecipeCache.GetOrBuild` | 内容地址式LRU，静态相位按签名缓存一次 | `[28]`：100个同模板单位BuildCount只增1 | 完整 | — | — |
| M3-R02-ANY-ENTITY-PROJECTION | `CarrierCompiler.Compile/CompileFromRecipe` | 生产调用方仅玩家本体一条路径；友军新生个体只挂主器官id，基因对AI开火路径零影响（类注释自陈"本期不做"） | 无 | 缺失 | 友军/敌方装不同基因战斗表现应不同，实际等同只看主器官id，基因白装 | 新故事：友军/敌方/Alpha统一走`CompileFromRecipe`+补`EmissionContext` |
| M3-R02-EMISSIONCONTEXT | 无 | 全仓无`EmissionContext`类型（与CP-REQ-002同根因，正式判定见M2块） | 无 | 缺失 | `CarrierCompiler`直吃裸seed/WorldState/cellId三散参数 | 需M2与M3联合排期 |
| M3-R02-GENE-ORDER-NEW-SIG | `PhenotypeTemplateSignature.Compute` | 顺序敏感拼接SHA-256，交换顺序必产生不同签名 | `[28]` | 完整 | — | — |
| M3-R02-TEMP-TRANSPLANT-SIG | `WildOrganRegistry.TryInstallTemporary` | 只塞进`_temporaryOrgan`，不产生/不参与任何单体签名；全仓无`IndividualLoadoutSignature`（GDD§16.2新领域数据） | 无 | 缺失 | 临时移植后走直控编译路径看不出与模板标准版差异 | 新故事：落地`IndividualLoadoutSignature` |
| M3-R02-BODY-RUNTIME-NOT-POLLUTE-CACHE | `CompiledRecipe` | 只存RuleVector+工厂委托，不存已new实例 | `[28]` | 完整 | — | — |
| M3-R02-CACHE-INVALIDATION-VISIBLE | `CarrierCompiler.BuildRecipe`早退分支 | 坏签名/内容不存在静默返回空HitEvent列表，无可读原因回传 | 无 | 缺失 | 装配了却完全不开火，无任何"为什么"提示 | 新故事：坏签名/降级路径带原因码 |
| M3-R03-SPAWN-REAL-TRANSACTION | `GerminationChamberRegistry.Enqueue`+`BiomassLedger` | 真实生物质扣费/退款+计时器队列，但地点是占位坐标，容量无上限校验 | `[29]` | 部分 | 萌生腔无真实位置/产能上限，无法围攻/切断 | 新故事：真实腔体实体+容量上限 |
| M3-R03-SPAWN-CANCEL-NO-DUP | `GerminationChamberRegistry.Cancel/DestroyPod` | 取消/腔体被毁全额退款且不产生重复副本 | `[29][30]` | 完整 | — | — |
| M3-R03-RETURN-REAL-COMBAT-EXIT | `HomecomingRetrofitService.TryBeginRetrofit` | `_inProgress`只是字典标记，类注释自陈"从战斗调度摘除留给后续故事"，未真实摘除 | `[30]` | 缺失 | 送回巢改造时可能还在原地挨打或继续自动开火 | 新故事：接AI/战斗调度真实摘除与重入 |
| M3-R03-NETWORK-ENGAGE-CARRY-REAL | `HomecomingRetrofitService.SetEngaged/SetCarryingKeyItem` | 三项判据均占位：网络默认恒true；交战/携带物空HashSet无生产调用点接入 | `[30]`(手工模拟) | 缺失 | 战斗中/携带关键物理论上不能改造，实际永远判定可以 | 网络判据待M5接线；交战/携带物需新故事接生产调用点 |
| M3-R04-WILD-DROP-ON-DEATH | `WildOrganRegistry.DropInField` | 生产调用点为零，只有Editor自检调用；`HandleBodyDeath`未接任何死亡信号 | `[31]`(手工模拟) | 缺失 | 单位死亡后不会真的掉落野生器官战利品 | 新故事：接真实死亡/切离信号 |
| M3-R04-WILD-FULL-CHAIN | `WildOrganRegistry`各方法 | 拾取/容量/临时移植/拆除/拆解/解析均有真实状态机，但死亡掉落缺失、存读档整段不存在 | `[31]`不覆盖死亡掉落/存读档 | 部分 | 重开进程后野生器官状态全部消失无提示 | 新故事：野生器官存读档 |
| M3-R04-NO-DOUBLE-USE | `WildOrganRegistry.ValidateChamberAction` | 单一状态机，Carried是唯一合法起点 | `[31]` | 完整 | — | — |
| M3-R04-NO-DUPLICATE-ON-CANCEL | `WildOrganRegistry`拾取/安装/卸下路径 | 取消场景验证不复制；**读档场景不存在无法验证** | `[30][31]`只覆盖取消 | 部分 | 存档接入后"读档重复到账"回归目前无兜底 | 随存读档故事一并补 |
| M3-R05-UI-NOT-MIGRATED | `CellDebugHud`(Y键调试面板) | 模板/萌生/回巢/野生器官交互唯一入口是调试HUD，无第二条正式玩家入口 | 无 | 缺失 | 玩家能摸到的"改配方"入口是开发调试面板 | M3-R05本身即修复故事 |
| M3-R05-MULTI-LINEAGE-COMPARE-COST-CONFLICT | `Lineage.TemplateNames/GetHistory/GetLatest` | 数据层可多模板枚举，但版本对比/成本对比/冲突可视化UI不存在 | 无 | 部分 | 提交前无法对比新旧版本成本/冲突差异 | 随M3-R05一并解决 |
| M3-R05-INDIVIDUAL-TRACE-VERSION | `GerminationChamberRegistry.Bindings` | 数据层可回溯个体绑定，但无面向玩家的个体面板消费 | 无 | 部分 | 玩家无法在正常UI看到个体模板版本 | 随M3-R05一并解决 |
| M3-R05-SUBMIT-SCOPE-CLARITY | `LineageRegistry.CommitTemplate`语义 | 机制正确支持"只影响新生/回巢"，但无玩家可见确认文案 | 无 | 部分 | 提交模板时不会被告知旧个体不受影响 | 随M3-R05一并解决 |
| M3-R05-END-TO-END-JOURNEY | 无 | `[26]~[32]`是七段独立纯内存单测，无一条串成"野生器官→移植/解析→改模板→新生V2→回巢→混编不串改"完整旅程；全部深层直调 | 无 | 缺失 | 无回归证据证明整条流程不串改 | 新故事：固定种子端到端旅程机器人（IC-REQ-021口径） |

**M3 小结**：共25行，完整6/部分8/缺失11/冲突0。**最严重缺口**：`CarrierCompiler`只有玩家本体
一条生产路径，友军/敌方/Alpha完全不走装配签名机制，基因对其战斗表现零影响；全仓无
`EmissionContext`/`IndividualLoadoutSignature`。次严重：模板/萌生/回巢唯一入口仍是调试面板；
死亡掉落无生产调用点，回巢单位未被真实摘除出战斗调度。

---

# M4 返工与后续包：编队/命令/教义/路径/脱队/遭遇（05_ §6）

**关键结构性事实**：全仓 `CreateFormation()` 调用点只有两处——`CellFrameworkValidate.cs`（Editor
自检）与 `FormationEncounterScenario.cs`（headless场景脚本）。真实鼠标/键盘输入落点
`SquadCommandSystem.cs` 里 `Formation` **零命中**。M4 全部编队/命令/教义能力，在今天的正式游戏
入口（鼠标右键、UI、AI决策）里一条都摸不到——这是下面几乎每行只能判"部分/缺失"的共同根因。

## M4-R01 编队模型（FC-REQ-001~004）

| REQ-ID | 当前生产入口 | 当前实现 | 自动测试 | 判定 | 用户症状 | 修复故事 |
|---|---|---|---|---|---|---|
| FC-REQ-001 | 无（仅自检/headless脚本直调） | `Formation`成员集合只存`SimEntityId`；`HandleMemberDeath`是真实方法但**未接任何死亡信号**（commit原文自认"技术债暂不接线"）；俘获/换阵营/卸载/读档全仓零代码 | `[33]`仅覆盖手动调用 | 部分 | 队友死亡后编队不会自动摆脱，长期堆积僵尸成员 | 接真实死亡事件+补四条路径处理 |
| FC-REQ-002 | 无 | 具备成员/脱队/命令/教义只读Profile；**缺**领队锚点策略/阵型完整度/载荷护送归属/占据接口/信号续航/分级失散原因 | `[33]~[37]`未覆盖缺失字段 | 部分 | UI看不到载荷/信号/续航，因为概念在代码里不存在 | 逐项补状态字段+生产读写入口 |
| FC-REQ-003 | 无 | `Formation.TryComputeAnchor`对全体成员算术平均，无领队优先级，不排除极端失散成员 | 无对应断言 | 冲突 | 一个卡在障碍里的成员会把整队锚点拽偏 | 锚点改领队优先→排除脱队/卡死后取稳健中心 |
| FC-REQ-004 | 无 | 全仓`职责/Role`零命中，槶位分配按数组索引奇偶，与身体能力无关 | 无 | 缺失 | 搬运/治疗/突击单位在编队里完全等价 | 新故事：职责来源→槶位按职责查表 |

## M4-R02 十类命令（FC-REQ-010/011/020/021/022；FC-REQ-060可见性并入本节判定）

| REQ-ID | 当前生产入口 | 当前实现 | 自动测试 | 判定 | 用户症状 | 修复故事 |
|---|---|---|---|---|---|---|
| FC-REQ-010 | 无 | 状态枚举无`Queued`/`Validating`独立态；暂停期间行为未处理；命令记录无Creator/资源预留字段 | `[33]~[35]` | 部分 | 暂停中下令行为未定义 | 补Validating态+暂停分支+记录字段 |
| FC-REQ-011 | 无 | `Priority`是裸int，与规格7级优先级无任何映射 | `[34]`只测数值排序 | 缺失 | "紧急撤退"与随手数字优先级无本质区别 | 7级优先级枚举化，不允许调用方自定义数值 |
| FC-REQ-020 | 无（无右键/UI/AI入口） | 8个CommandKind全存在，仅Move/Retreat有完整链；Attack只记账；Guard/Carry/Occupy/Ambush/OrganCategory五种**零行为**（原文自认"不轮询不新增内核查询"） | 场景脚本手写演示，绕过命令生产链 | 缺失 | 下守卫/搬运/占据/伏击/攻击器官命令,队伍不会做相符的事 | 拆5个子故事逐个补生产链 |
| FC-REQ-021 | 无 | `SquadCommandSystem.cs`无任何对`Formation`引用；右键仍只有"点敌人=攻击/点空地=移动"两分支 | 无 | 缺失 | 新增6种命令玩家在正式操作里完全摸不到 | 把右键目标语义扩展到全部10类命令——**本包最大单点缺口** |
| FC-REQ-022/060 | 无 | `_pendingCommands`是真实按优先级插入排序列表；但无玩家可见的追加/插队/取消/清队列UI，`FormationCommandOverlay`只显示当前Active一行 | `[33][34]`覆盖底层队列行为 | 部分 | 底层排队逻辑对，但玩家无法在正式界面看到或操作队列/教义/覆盖参数（FC-REQ-060"必须可见"未满足） | 给Overlay加队列可视化+取消/清空交互 |

## M4-R03 六教义（FC-REQ-030/031）

| REQ-ID | 当前生产入口 | 当前实现 | 自动测试 | 判定 | 用户症状 | 修复故事 |
|---|---|---|---|---|---|---|
| FC-REQ-030 | 无 | `FormationDoctrineProfile.For`是纯查表函数；全仓无任何AI决策/目标选择/移动/能力/资源代码读取它（M4-03原文自述"只是定义+只读查询"） | `[35]`只验证查表本身 | 冲突 | 切教义时目标选择/交战距离/追击/队形/职责/能力许可/资源阈值/撤退条件**没有一项真的变** | **本包最严重单点缺口**：接入至少目标选择+交战/撤退判定两处真实决策路径，行为对照实验验收 |
| FC-REQ-031 | 无 | 六教义枚举+三轴参数已按`preflight-decisions.md`定值，数值本身合理 | `[35]` | 部分 | 数值再合理玩家也感受不到，因FC-REQ-030未接线 | 与030同一故事解决 |

## M4-R04 导航与大军路径（FC-REQ-040/041/042/043/044/045/070）

| REQ-ID | 当前生产入口 | 当前实现 | 自动测试 | 判定 | 用户症状 | 修复故事 |
|---|---|---|---|---|---|---|
| FC-REQ-040 | 无 | `FormationMovementDriver.DriveMembers`**每帧**为每个成员单独调`IssueCommand`——正是规格明文禁止且违反仓库架构红线的"热更层逐帧逐人发命令" | `[36][37]`只验证行为正确不验证开销 | 冲突 | 当前2~4人规模感觉不出；冲到64/256人会直接撞穿性能红线 | 逐成员路点推进迁到`Main/Sim/`Job，热更层只发编队级事件 |
| FC-REQ-041 | 无 | 不存在`INavigationQuery`；`FormationPathPlanner.Plan`直吃圆障碍数组，正是规格点名要淘汰的心智模型 | `[36]`测算法本身 | 冲突 | 无法适配手工腔室/动态组织/未来地形，升级=重写调用方 | 定义`INavigationQuery`，圆障碍启发式降级为其一个实现 |
| FC-REQ-042 | 无 | `FormationFollowSlots.ComputeOffset`只有一种"两侧散开"布局；六队形/窄口压缩恢复/体型过滤/护送安全槶位全部缺失 | 无 | 缺失 | 编队永远同一种队形，看不出楔形冲锋和纵队潜行差异 | 独立大故事：六队形+窄口检测+体型过滤（M4-R04工作量最大） |
| FC-REQ-043 | 无 | 要求全部成员到达才算完成，无最小有效比例/关键目标优先到达判据 | `[36]`覆盖当前行为本身 | 部分 | 一人掉队整队移动命令永远不Completed | 引入比例/超时/关键目标优先到达判据 |
| FC-REQ-044 | 无 | `FormationStuckTracker`只有Replan→Fail两级，无局部换槶/重组前置缓解，无"合法不动"识别 | `[36]`测数值边界 | 部分 | 卡死恢复只有重规划/放弃两档，五级降级中间过程玩家看不到 | 补齐五级降级状态机 |
| FC-REQ-045 | 无 | 不存在"导航版本"概念，障碍一次性给定运行期无动态变更 | 无 | 缺失 | 关卡无任何可动态改变的通行环境 | 依赖041落地后加最小动态障碍+版本号用例 |
| FC-REQ-070 | 无 | 现有场景规模均2~4人，无1/2/8/64功能矩阵，无4×64+512压力矩阵，无CPU/分配/P95数据 | 无 | 缺失 | 数百单位场景完全未验证，加上040的架构冲突大概率跑不过预算 | 先修040架构冲突，再补压力矩阵 |

## M4-05 直控脱队与回归补全（FC-REQ-050/051/061，CP-REQ-090角度）

| REQ-ID | 当前生产入口 | 当前实现 | 自动测试 | 判定 | 用户症状 | 修复故事 |
|---|---|---|---|---|---|---|
| FC-REQ-050 | 真实信号`ControlledUnitChangedSignal`（本包唯一有真实事件驱动接线的一条） | 接管时只做`Formation.SetDetached(entity,true)`；无槶位/命令阶段/路径版本/接管位置快照（因这些概念本身未实现） | `[37]`验收1/5 | 部分 | 接管本身干净，但缺快照，Carry/Occupy落地后需补课 | 待Carry/Occupy落地后补快照字段 |
| FC-REQ-051 | 同上信号 | 交还只做`SetDetached(entity,false)`一件事；无三分支判定（原命令有效/目标已变/无法返回按教义失散），无原因反馈 | `[37]`验收2/3 | 缺失 | 队伍会重新跟队，但只是通用逻辑碰巧覆盖简单情形 | 显式三分支判定，接教义失散策略（依赖030） |
| FC-REQ-061 | `FormationCommandOverlay`（纯Debug叠加层） | 只显示"编队Id:命令种类[状态]"一行，不显示编队目标/汇合方向/失败提示，无战略视角"被直控过"标记 | 无自动化测试 | 缺失 | 直控时看不到刚离开的编队在干什么 | 新故事：直控HUD补编队信息+战略视角标记 |
| M4-05-DISENGAGE-PROJECTILE（本地ID，底层判定见M2块CP-REQ-090） | 无 | `[37]`场景全部测试单位无攻击行为，未构造"直控单位发射弹体后被交还"场景 | 无 | 缺失 | 未知——完全未被验证 | 补构造用例：释放持续判定弹体后立刻交还，断言不中断不串体 |

## M4-06 多线固定遭遇补全（FC-TEST-007，FS-REQ-001/002角度）

| REQ-ID | 当前生产入口 | 当前实现 | 自动测试 | 判定 | 用户症状 | 修复故事 |
|---|---|---|---|---|---|---|
| FC-TEST-007 | 无（`FormationEncounterScenario`纯静态方法，从未被`CellStageFlow`/UI/输入调用，原文自述"不是通用关卡编排框架"） | 两策略headless手搭场景，**绕过**`Formation`命令生产链直调`SimBridge.IssueCommand`；无真实可搬运载荷/信号压制/共享资源预算/载荷掉落夺回/关卡编排/UI/正式输入 | `[38]`验证策略可行性与用时倍率 | 缺失 | 功能今天不存在于任何玩家能触达的地方 | 需新开大故事：先落地Carry→信号压制→共享资源预算→关卡编排框架→UI→真实输入驱动的旅程机器人 |
| FS-REQ-001/002（角度：是否真用职责接口） | 无 | 场景里"编队"只是SimEntityId数组套一层AddMember，个体无完整度/代谢储备/威胁记忆/信号状态，直接绕过所有本该经过的职责接口 | `[38]` | 缺失 | 无（尚不影响玩家可见行为，纯结构性缺口） | 依赖M5资源网络+本包Carry/职责落地后重做场景脚本 |

**M4 小结**：完整0/部分8/缺失约11/冲突4（FC-REQ-003、030、040、041）。**最严重缺口排序**：
①全包零正式入口——编队/命令/教义在真实右键/UI/AI路径里完全摸不到，是几乎每行判"部分/缺失"的
共同根因；②FC-REQ-030教义只读查表零消费者，与规格"教义不是只读描述"直接矛盾；③FC-REQ-040
每帧逐人发命令，正面撞仓库架构红线，规模一大必破防。

---

## 全量汇总

| 块 | 行数 | 完整 | 部分 | 缺失 | 冲突 |
|---|---|---|---|---|---|
| M1（身份/生命周期） | 13 | 3 | 8 | 2 | 0 |
| M2（发射/基元/装配/手术/输入） | 41 | 1 | 22 | 14 | 4 |
| M3（蓝图/模板/传播） | 25 | 6 | 8 | 11 | 0 |
| M4（编队/命令/教义/路径/脱队/遭遇） | 23 | 0 | 8 | 11 | 4 |
| **合计** | **102** | **10** | **46** | **38** | **8** |

**完整率 ≈ 10%**（10/102）。旧口径下 M1～M4 全部标 Done，但按新规格逐条复验，九成需求点只能判
「部分」或「缺失」，其中 8 处是直接「冲突」（当前实现与规格明文相悖，不是没做完，是做错了方向）。

## 跨块共同根因（全部四块审计独立指出，非单一块的孤立发现）

1. **两套平行系统，非一套**：玩家操作自己本体（`AbilitySystem`/`EffectSpec`/`_wallet`）与
   玩家/AI 操作被接管友军身体（`OrganKernelAction`/`OrganReleaseRunner`/`UnitVitalsRegistry`）
   是两套独立的技能定义、资源账本、冷却模型（CP-REQ-060 冲突）。同样，装配签名机制
   `CarrierCompiler.Compile` 的生产调用方只有玩家本体一条路径，友军/敌方/Alpha 完全不走
   （M3-R02-ANY-ENTITY-PROJECTION 缺失）。规格反复要求的统一入口类型 `EmissionContext`、
   `AbilityRecipe` 在全仓代码里**零命中**。
2. **正式生产入口大量不存在**：M4 全部编队/十类命令/六教义在真实鼠标右键/UI/AI 决策路径里
   一条都摸不到（唯二调用者是 Editor 自检和 headless 场景脚本）；模板/萌生/回巢/野生器官的
   唯一入口是 `CellDebugHud` 调试 IMGUI 面板；手术接点仍是 2 槽占位、不进正式生成池；
   `Interact` 动作被硬编码恒返回失败。
3. **看似"完成"的功能是只读描述**：六教义 `FormationDoctrineProfile` 是纯查表函数，
   全仓没有任何 AI 决策/目标选择/移动/资源代码读取它（FC-REQ-030 冲突，M4-03 提交原文
   自认"不接真实决策"）。
4. **架构红线已被违反**：`FormationMovementDriver` 每帧为每个编队成员单独调用一次
   `SimBridge.IssueCommand`，正是仓库 CLAUDE.md 明文禁止的"热更层逐帧逐人发命令"
   （FC-REQ-040 冲突），规模一大会直接撞穿性能红线。
5. **身份/资源可能串体**：伤害/死亡事件仍用非唯一 `LogicId` 归因（IC-REQ-010，2026-09-11
   审计标记至今未落地）；多发角度公式全仓四套互不一致（CP-REQ-012 冲突）。

详细的逐行判定、生产入口证据（文件:行号）、自动测试证据与建议修复故事，见上方四个分块表格。
