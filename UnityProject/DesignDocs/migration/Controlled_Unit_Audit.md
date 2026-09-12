# M1-01 玩家索引依赖审计

> 审计日期：2026-09-11  
> 代码基线：`TEngine e1e07c0be7e742863847d35e68a77f9e4c5c7616`  
> 产品权威：`DesignDocs/ProjectA_GDD.md` §7、§16.1、§16.4  
> 实施权威：`DesignDocs/ProjectA_Milestones.md` M1-01～M1-06  
> 本任务只审计，不修改运行逻辑。

## 1. 审计结论

当前模拟不是“任意友军 + 可切换意图来源”，而是把四件事绑定在一起：

1. 数组槽位 `0`；
2. `SimFaction.Player` 阵营；
3. 唯一的 `PlayerIntent`；
4. 一组 `PlayerPosition`、`PlayerHealth`、`PlayerRadius`、`PlayerDamageTaken` 快捷字段。

因此不能通过把 `SimConst.PlayerIndex` 替换成一个可变 `int` 完成迁移。M1-02 必须先建立独立于数组槽位的 `ControlledUnitId` 和 `IntentSource`，M1-03 再让作业消费统一意图，之后才能安全迁移桥接、镜头、HUD、特效、死亡与存档。

另一个关键结论是：现有 `LogicId` **不能直接充当稳定实体 ID**。

- `SpawnDirector.EncodeLogicId` 只写敌人配置 ID，高 16 位实例序号尚未使用，同类敌人会重复；
- `SimBridge.NextLogicId` 虽能分配正数，但不是所有生成路径都强制使用；
- 内核生成单位使用负数，槽位释放时又把 `LogicId` 清零；
- 多处代码把 `LogicId` 当内容/来源标识，而不是世界实体身份。

应保留 `LogicId` 的既有内容与事件语义，另建稳定实体 ID；不要一字段兼任两种职责。

## 2. 产品不变量

后续迁移必须满足以下规则：

- 控制权锁定一个真实友方个体，不锁定数组槽位、模板或编队；
- RTS、直控和表现层读取同一个模拟实体；切换前后位置、伤势、器官和命令不丢失；
- 玩家离开后，原个体恢复 AI；新个体接收玩家意图；敌军不能被控制；
- 控制身份与阵营正交，不能再由 `SimFaction.Player` 表示“当前被玩家控制”；
- 目标死亡、移除、场景卸载、读档和世界重置后，控制状态必须确定；
- 表现层只消费快照与事件，不成为控制规则真相。

## 3. 分类审计表

判定说明：

- **必须迁移**：M1 出口前不能保留固定 0 号语义；
- **兼容适配**：可以短期保留 API 形状，但实现必须改为解析当前控制实体；
- **可保留瞬时索引**：索引只作单帧/单步内的高性能地址，不得成为持久身份。

| ID | 类别 | 文件与符号 | 当前假设 / 证据 | 风险 | 判定 | 迁移任务 |
|---|---|---|---|---|---|---|
| A01 | 数据模型 | `Main/Sim/SimTypes.cs` — `SimConst.PlayerIndex` | 常量明确定义玩家固定占用索引 0。 | 所有上层代码都能绕过控制身份模型直达槽位。 | 必须迁移 | M1-02 |
| A02 | 数据模型 | `Main/Sim/SimTypes.cs` — `SimFaction.Player`、`PlayerMinion` | “玩家本体”和友军附属体是不同阵营；控制身份被编码进阵营。 | 接管友军后，敌方攻击、区域和查询仍指向旧 `Player` 阵营。 | 必须迁移；保留枚举数值时只可作数据兼容 | M1-02、M1-03 |
| A03 | 数据模型 | `Main/Sim/SimTypes.cs` — `SpawnRequest.LogicId`；`Spawning/SpawnDirector.cs` — `EncodeLogicId` | `LogicId` 是热更内容标识；同类敌人可重复，高 16 位实例序号未用。 | 不能抵抗同类实例、槽位复用、重排和读档。 | 保留内容 ID；新增稳定实体 ID | M1-02 |
| A04 | 数据模型 | `Main/Sim/SimTypes.cs` — `HitEvent.TargetIndex`、`DeathEvent.LogicId` | 事件一部分用槽位索引，一部分用非唯一 LogicId。 | 跨帧追踪可能串到复用后的新单位；无法可靠识别控制目标死亡。 | `TargetIndex` 可保留为瞬时地址，同时补稳定实体 ID | M1-02、M1-06 |
| A05 | 数据模型 | `Main/Sim/SimSnapshot.cs` — `SimSnapshot` | 文档声明索引 0 恒为玩家，并复制出四个 `Player*` 字段；快照没有 `ControlledUnitId`。 | UI 和 HotFix 无法从稳定身份解析当前目标。 | 必须迁移；`Player*` 可短期做委托式兼容字段 | M1-02、M1-05 |
| A06 | 模拟规则 | `Main/Sim/SimWorld.cs` — `Initialize` | 世界创建时预占槽位 0，创建唯一 `SimFaction.Player`，`LogicId=0`。 | 世界天然只有一个不可替换主角；槽位 0 不参与普通生命周期。 | 必须迁移；首个单位的启动创建可保留为内容流程 | M1-02 |
| A07 | 模拟规则 | `Main/Sim/SimWorld.cs` — `SetPlayerStats`、`SetPlayerVisualId`、`PlayerHealth/Position/Radius`、`DamagePlayer`、`HealPlayer`、`SetPlayerPosition` | 全部直接读写索引 0。 | 切换后属性、视觉、治疗和伤害仍落在旧身体。 | 兼容 API 可暂留，但必须委托当前稳定 ID | M1-02、M1-04 |
| A08 | 模拟规则 | `Main/Sim/SimWorld.cs` — `Step` 玩家意图段 | 单例 `PlayerIntent` 无目标，直接写 `_desiredDir[0]`、状态、半径和速度。 | 不能表达 A 由 AI 控制、B 由玩家控制；切换时易残留旧意图。 | 必须迁移为带实体 ID/来源的统一意图 | M1-02、M1-03 |
| A09 | 模拟规则 | `Main/Sim/SimWorld.cs` — `ResolveMinionCombat` | 循环从 `PlayerIndex + 1` 开始，只处理 `PlayerMinion`。 | 槽位顺序被当作单位职责；受控友军无法在 AI/玩家间正交切换。 | 必须迁移 | M1-03 |
| A10 | 模拟规则 | `Main/Sim/SimWorld.cs` — `ResolveHostileRangedCombat`、`ResolveHostileAbilities` | 敌人永远瞄准索引 0 的位置，弹体和区域目标阵营固定为 `SimFaction.Player`。 | 接管后敌人仍追旧身体；新身体可能免疫原本应承受的攻击。 | 必须迁移；具体仇恨规则不在 M1 扩展 | M1-03 |
| A11 | 生命周期 | `Main/Sim/SimWorld.cs` — `ApplyCommands`、`EmitDeath`、`KillUnit`、`ReleaseSlot` | `idx <= PlayerIndex` 被禁止 Despawn/死亡事件/回收；索引 0 仅靠血量触发关卡结束。 | 无法对任意受控实体执行标准死亡，也无法产生意识回弹依据。 | 必须迁移 | M1-02、M1-06 |
| A12 | 模拟规则 | `Main/Sim/Jobs/JobSteering.cs` — `Execute` | 索引 0 跳过 AI，所有其他单位根据 `PlayerPos` 转向。 | 玩家意图和 AI 意图不是同一数据形状；离开单位无法自然恢复 AI。 | 必须迁移 | M1-03 |
| A13 | 模拟规则 | `Main/Sim/Jobs/JobDamage.cs` — `JobDamage.TryDamage` | 命中索引 0 时不走普通生命/死亡流程，只累加长度 1 的 `PlayerDamageOut`。 | 任意受控友军无法继承同一伤害反馈；身份切换会改变伤害语义。 | 必须迁移；反馈聚合可改为带实体 ID 的事件 | M1-03、M1-06 |
| A14 | 模拟规则 | `Main/Sim/Jobs/JobDamage.cs` — `JobContactDamage` | 围绕唯一 `PlayerPos/PlayerRadius` 扫描，把伤害写入单槽输出。 | 接触伤害只认识旧主角，不认识普通友军和当前控制实体。 | 必须迁移 | M1-03 |
| A15 | 模拟规则 | `Main/Sim/Jobs/JobQuery.cs` — `JobDevourScan` | 吞噬候选只围绕 `PlayerPos/PlayerRadius`，并跳过索引 0。 | 切换后吞噬能力仍绑定旧身体；受控目标的体积不生效。 | 必须迁移 | M1-03 |
| A16 | 输入 | `Main/Sim/SimCommandBuffer.cs` — `PlayerIntent`、`SetPlayerIntent`、`TryGetIntent` | 缓冲区最多存一个匿名玩家意图，没有目标 ID 和 `IntentSource`。 | 无法在同帧表达 AI、Player、Scripted 三种来源，也无法验证目标。 | 必须迁移 | M1-02、M1-03 |
| A17 | 桥接 | `HotFix/GameLogic/Battle/SimBridge.cs` — `Intent`、`OnUpdate` | 每帧无条件提交唯一 `PlayerIntent`；没有查询、候选、切换、失败码或切换事件。 | HotFix 无单一受控切换入口，上层只能继续使用玩家快捷接口。 | 必须新增控制服务 API | M1-04 |
| A18 | 桥接 | `HotFix/GameLogic/Battle/SimBridge.cs` — `Player*` 读写接口、`World` | Player 快捷接口直达快照或 `SimWorld`，并公开具体 `World`。 | 调用者可绕过稳定 ID 和合法性校验；M1-04 的桥接边界无法成立。 | `Player*` 可短期委托；控制相关直接 `World` 访问必须收口 | M1-04 |
| A19 | 输入 | `Stage/CellStage/CellPlayerController.cs` — `OnUpdate`、`ReadAimDirection`、`ApplyRegen` | 键鼠、瞄准、技能、属性同步和回血都写匿名玩家；属性来自单一全局 `StatSheet`。 | 切换后意图、装配属性和资源归属可能仍指向旧身体。 | 必须迁移 | M1-04；装配映射后续 M3 |
| A20 | 效果执行 | `Ability/Executors/EffectApplyStatus.cs`、`EffectDash.cs` | Self/冲刺直接对 `PlayerIndex` 施加状态。 | 新受控实体得不到状态，旧身体错误获得状态。 | 必须迁移 | M1-04 |
| A21 | 效果执行 | `Ability/AbilitySystem.cs`、`EffectResource.cs`、`EffectDealDamage.cs` | 施法原点、低血量倍率、治疗、自损读取或写入 `Player*`。 | 技能仍绑定旧主角；切换后伤害、治疗和瞄准串体。 | 必须迁移 | M1-04 |
| A22 | 效果执行 | `Cards/CardTriggerBus.cs`、`MetabolicSliceBridge.cs`、`StructuralHookRunner.cs` | 卡牌、代谢、位移、结构钩子广泛用 `PlayerPosition/Health` 作为原点或状态。 | 组合技和被动效果在切换后继续围绕旧身体触发。 | 先经桥接兼容，随后逐调用点改为受控实体上下文 | M1-04 |
| A23 | 规则/生成 | `Battle/AreaZoneSystem.cs`、`Spawning/SpawnDirector.cs` | 跟随区域、刷怪中心和难度读取唯一玩家位置/生命。 | 接管时区域、刷怪和压力判定突然留在旧身体；可能被玩家利用或导致不可读伤害。 | 必须明确“跟随受控实体”或“跟随战略锚点” | M1-04、M1-05 |
| A24 | 渲染 | `Main/Sim/SimRenderer.cs` — `Draw`、`SetPlayerLunge` | 玩家前冲只对索引 0 的实例矩阵生效。 | 切换后特效留在旧身体。 | 必须按快照中的控制 ID 解析 | M1-05 |
| A25 | 镜头 | `Stage/CellStage/CellStageFlow.cs` — `FollowCamera`、`DebugToggleCameraVerifyMode` | 相机每帧跟随 `SimBridge.PlayerPosition`；无控制目标时没有战略锚点回退。 | 目标死亡/延迟生成时相机丢失或停在错误实体。 | 必须迁移 | M1-05、M1-06 |
| A26 | UI | `UI/BattleHudToolkit/BattleHudToolkit.cs`、`BattleMainUI.cs`、`CellDebugHud.cs` | HUD 生命直接读 `PlayerHealth`，状态掩码直接读 `Status[PlayerIndex]`。 | 连续切换时显示旧身体生命/状态；直接数组访问绕过桥接。 | 必须迁移；UI 只读受控实体视图 | M1-05 |
| A27 | UI/状态 | `Battle/StatusSystem.cs` — `Entry`、`PlayerTimers`、`OnUpdate` | 玩家计时器只筛 `UnitIndex == PlayerIndex`；槽位复用保护依赖可能重复的 LogicId。 | 状态可能串槽；HUD 的限时状态不会随控制目标切换。 | 条目改用稳定 ID；玩家视图改由控制 ID 筛选 | M1-02、M1-05 |
| A28 | 表现 | `Battle/Feedback/WhiteboxHealthBar.cs` — `Sync`、`HitTrackEntry` | 玩家血条来自独立 `Player*` 字段；敌方短显追踪只存 UnitIndex。 | 控制切换或槽位复用时血条串体。 | 玩家血条按控制 ID；跨帧追踪使用稳定 ID | M1-05 |
| A29 | 表现 | `CarrierBodyVisualPresenter.cs`、`StructuralVisualPresenter.cs`、`EnvFluidBackground.cs`、`WhiteboxComposeAimIndicator.cs` | 本体造型、结构挂件、液体涟漪和瞄准指示器都围绕 `Player*` 快捷接口。 | 切换时旧目标残留玩家专属表现，新目标缺表现。 | 必须迁移并在切换事件中清理旧目标 | M1-05 |
| A30 | 关卡结束 | `Stage/CellStage/CellStageFlow.cs` — `CheckEnd` | 只要 `PlayerHealth <= 0` 就结束整局。 | 与 GDD 的“直控身体死亡 → 意识回弹”直接冲突。 | 必须改为受控实体死亡处理；正式惩罚不在 M1 | M1-06 |
| A31 | 存档 | `Stage/StageOutcome.cs`、现有持久化入口 | 未发现 `ControlledUnitId`、`IntentSource`、回退锚点或控制恢复字段。 | 暂停、读档、重进场景后无法确定唯一控制实体。 | 功能缺口，需新增版本化默认值 | M1-06 |
| A32 | 测试 | `Assets/Editor/CellFrameworkValidate.cs` | 多段验证固定调用 `SetPlayer*`、`PlayerIntent`、`PlayerHealth/Position`。 | 旧测试会把固定主角行为继续当成正确结果。 | 保留旧行为回归，并新增稳定 ID/切换矩阵 | M1-02～M1-06 |
| A33 | 测试 | `Battle/SimStressTest.cs`；`MetabolicSlice/DebugTools/*SmokeReport.cs` | 压测和烟测断言唯一玩家、`SimFaction.Player` 与 `PlayerDamageTaken`。 | M1 改动可能被误判为回归，且没有多友军控制覆盖。 | 迁移断言；增加三友军、20 次切换、死亡/复用/读档测试 | M1-03、M1-05、M1-06 |

## 4. 允许短期保留的兼容适配点

以下兼容只用于降低一次性改动面，不得继续代表规则真相：

1. `SimBridge.PlayerPosition/PlayerHealth/PlayerRadius` 可暂时保留名称，但内部必须按 `ControlledUnitId` 查询；无目标时返回显式失败或受控的只读默认值，不能偷偷回到索引 0。
2. `SimSnapshot.Player*` 可在 M1-05 完成前作为派生字段保留，但必须同时带 `ControlledUnitId`，并由快照构建阶段解析；新代码不得直接使用固定索引。
3. `HitEvent.TargetIndex`、作业内部索引和 NativeArray 槽位可以继续用于单帧性能路径，但跨帧缓存、UI 追踪、存档和控制身份必须使用稳定实体 ID。
4. `LogicId` 保留为内容/来源标识；新增实体 ID，避免破坏敌人配置解码、伤害来源和既有事件消费者。
5. `SimFaction` 的数值可为旧表兼容而保留，但“当前是否被玩家控制”必须由 `IntentSource`/控制状态决定，不能再由 `Player` 阵营决定。

## 5. 建议迁移顺序

严格按里程碑串行：

1. **M1-02：身份真相** — 新增稳定实体 ID、`ControlledUnitId`、`IntentSource`、ID→槽位解析、合法切换状态机；先定义死亡/移除/重置语义。
2. **M1-03：模拟去特判** — 玩家与 AI 都写统一意图；Jobs 和主线程战斗逻辑不识别索引 0；阵营只表达敌我关系。
3. **M1-04：桥接收口** — `SimBridge` 提供查询、候选、请求切换、失败码和一次性事件；HotFix 不直接用模拟数组判定控制目标。
4. **M1-05：表现迁移** — 相机、HUD、血条、状态、前冲、瞄准、挂件和环境反馈统一跟随当前控制 ID，并清理旧目标表现。
5. **M1-06：生命周期闭环** — 意识回弹、无控制实体、场景卸载、旧存档默认值、固定种子回归。

不应先改 UI，也不应先批量替换 `PlayerIndex`。那会把同一个错误假设换成另一个可变索引，并留下槽位复用、阵营、死亡和存档问题。

## 6. 最小验收矩阵

| 阶段 | 必须新增的验证 |
|---|---|
| M1-02 | 三个友军拥有不同稳定 ID；槽位释放再复用后旧 ID 不解析到新单位；数组重排不改变控制目标；敌军/死亡单位切换返回稳定错误码。 |
| M1-03 | 控制从 A 切到 B 后，B 接收玩家意图，A 同帧或下一确定帧恢复 AI；伤害、接触、吞噬、敌方投射物和区域不再依赖槽位 0。 |
| M1-04 | 合法切换只发一次事件；非法切换不改现状；HotFix 控制相关调用只通过桥接。 |
| M1-05 | 连续切换 20 次，无血条串体、相机丢失、状态错显、前冲/瞄准/挂件残留；目标晚于 UI 生成时无空引用。 |
| M1-06 | 当前身体死亡后意识回弹；目标卸载、暂停、保存、读取和重进场景后只有一个控制实体或明确为无；旧存档安全落到默认锚点。 |

## 7. 扫描边界与排除项

已覆盖：

- `Assets/GameScripts/Main/Sim/SimWorld.cs`
- `Assets/GameScripts/Main/Sim/SimTypes.cs`
- `Assets/GameScripts/Main/Sim/SimSnapshot.cs`
- `Assets/GameScripts/Main/Sim/SimCommandBuffer.cs`
- `Assets/GameScripts/Main/Sim/Jobs/`
- `Assets/GameScripts/Main/Sim/SimRenderer.cs`
- HotFix 的战斗桥接、状态、能力执行、卡牌、代谢、生成、关卡、UI 与表现消费者
- `Assets/Editor/CellFrameworkValidate.cs`、压力测试和相关 SmokeReport
- 当前 `StageOutcome` 与可见持久化入口

按仓库规则排除：

- `FirstPlayable/` 与 `FirstPlayableDemo.unity`：历史演示，不作为新功能迁移基线；
- `DesignDocs/Archive/`、旧六阶段产品规则和未被新 GDD 引用的旧产品假设；
- M2 之后的战略相机、RTS 命令、信号网络、正式转场美术与数值惩罚实现。

## 8. M1-01 完成判定

- [x] 搜索固定玩家索引、玩家快捷字段、阵营语义与相关注释；
- [x] 按模拟规则、输入、镜头、UI、渲染、存档、测试分类；
- [x] 标出必须使用稳定实体 ID 的位置；
- [x] 标出可暂留的兼容适配点；
- [x] 每项包含文件、符号、风险与迁移任务编号；
- [x] 未修改运行逻辑。
