# M4-R00-02：串行返工队列

> 生成于 2026-09-15，依据 `05_M1_M4_Backfill_And_Acceptance.md` §2 M4-R00-02 的优先级规则，
> 综合 `M1_M4_Completeness_Audit.md`（CP/FC/IC-REQ）与 `FS_REQ_M1_M4_Blocking_Audit.md`
> （FS-REQ）两份审计的全部"部分/缺失/冲突/接口阻塞/直接阻塞"判定。**每个队列项对应一个完整
> 玩家结果，不按"改一个类"拆碎**；同一优先级内的顺序按依赖关系排列，不代表工作量顺序。

按 05_ 规定的 7 级优先级排列：①数据真相/身份/资源串体 ②正式入口不存在 ③战斗角度/命中/阵营/
成本错误 ④命令/AI状态机不能闭合 ⑤死亡/暂停/存读档/失败恢复 ⑥UI/表现/可访问性 ⑦性能规模/
内容扩展。

## ① 数据真相/身份/资源重复或串体

1. **统一玩家本体与被接管身体的资源/技能账本**（`CP-REQ-060`冲突、`FS-REQ-031`直接阻塞、
   `CP-REQ-002`缺失`EmissionContext`）——四块独立审计共同指向的最严重架构缺口，后续几乎所有
   命令/教义/直控故事都建立在它之上。折入 `M2-R01`+`M2-R03`（原设计包）。
   **2026-09-15 状态：部分完成，未收口**——设计与实施记录见
   `production/design/m4-r00-02-item1-ability-truth-unification/DESIGN.md`。已落地：友军释放
   （直控+AI）改走真实 `CarrierCompiler` 编译结果而非热更层自拍常量，代价随真实伤害重算，
   `EmissionContext`/`ControllerKind` 类型已建立（诊断用途）；Unity batchmode 全量自检 835/835
   通过。**仍未解决**：`AbilitySystem`/`ResourceWallet`（卡牌）与 `UnitVitalsRegistry`（器官）
   两套并行资源模型未合并——这正是 `CP-REQ-060`/`FS-REQ-031` 违反证据的主体，本次未动；
   `MetabolicSliceBridge`（玩家本体自动开火，2807行零测试覆盖）泛化为任意实体可用，被
   `Direct_Control_Contract.md`§5 与本次设计文档双重确认是独立大重构，未在本次触碰；友军装配
   仍无基因数据，故"友军基因影响战斗表现"这条症状实质未解决（只是从"热更层常量"移到了
   "ComposeEngine裸链路对所有器官给同一基础值"这个更下游的点）。
   **2026-09-16 决策**：友军无基因（`DEBT-ABILITY-TRUTH-02`）并入②号项一起解决（②本来就是
   "任意实体接入统一装配签名"，天然覆盖）；卡牌/器官双资源模型（`DEBT-01`）与
   `MetabolicSliceBridge`泛化（`DEBT-03`）不并入②，继续独立登记，见①DESIGN.md §8。
2. **友军/敌方/Alpha 接入统一装配签名释放**（`M3-R02-ANY-ENTITY-PROJECTION`缺失）——依赖①-1
   的`EmissionContext`落地。折入 `M3-R02`。
3. **伤害/死亡事件迁移到稳定`SimEntityId`**（`IC-REQ-010`部分，2026-09-11遗留至今未落地）。
   折入 `M1-R01`。

## ② 正式入口不存在

4. **编队/十类命令接入真实右键输入**（`FC-REQ-021`——本包最大单点缺口）。折入 `M4-R02`。
5. **`Interact`动作接入真实交互目标**（`M2-R04`缺失——已有`WildOrganRegistry.TryPickup`未接线）。
6. **模板/萌生/回巢/野生器官 UI 从调试面板迁移到正式玩家入口**（`M3-R05`缺失）。
7. **手术接点从2槽占位转为按原型/骨骼配置的N接点+正式生成池**（`CP-REQ-071`部分）。
8. **M4-06 多线固定遭遇真实关卡编排框架**（`FC-TEST-007`缺失——当前是绕过命令生产链的headless
   脚本），依赖④的 Carry/信号压制先落地，是本队列里最大的一个故事，建议拆结算里程碑单开子队列。

## ③ 战斗角度/命中/阵营/成本错误

9. **合并四套扇形角度公式为单一纯函数**（`CP-REQ-012`冲突）。**2026-09-16 已完成**——实读代码后
   确认三套是死代码（零调用点，已删除），真正在用的 `CombatBallistics.FanDirection` 两处违规经
   bin 拍板修复：新增 `Packet/HitEvent.RadialRequested`（ComposeEngine 侧）让环射改为显式声明
   （org_orbitcilia/gene_harmonic 视觉不变），单发抖动分支按规格删除（`gene_fan` 单发场景手感
   变化，登记 `DEBT-COMBAT-ANGLE-01`）。设计与实施记录：
   `production/design/m4-r00-02-item3-9-fan-angle-unification/DESIGN.md`。
10. **真实发射点挂点+身体朝向字段**（`CP-REQ-003/004`缺失）。
11. **编队锚点算法改领队优先，排除脱队/卡死成员**（`FC-REQ-003`冲突）。

## ④ 命令/AI状态机不能闭合

12. **六教义接入真实决策路径**（`FC-REQ-030`冲突——本包最严重单点，`FormationDoctrineProfile`
    零消费者）。折入 `M4-R03`。
13. **`Guard/Carry/Occupy/Ambush/OrganCategory`五类命令补生产链**（`FC-REQ-020`缺失），依赖
    `FS-REQ-010`(生物质六态)、`FS-REQ-014`(占领四阶段)、`FS-REQ-020~022`(食源对象)先给最小接口。
14. **命令优先级枚举化**（`FC-REQ-011`缺失，7级优先级替代裸int）。
15. **编队职责/信号/续航最小字段**（`FC-REQ-002`部分，`FS-REQ-001/002/011/013/040/041`接口阻塞
    共同要求：任务来源优先级、`ResourceMargin`、`SignalLink`、`LocalReserve`、`Reserve/
    EnduranceEstimate`、`IsNetworked`状态位）。折入 `M4-R01`。
16. **直控交还三分支判定+教义失散策略**（`FC-REQ-051`缺失，依赖⑫先落地）。
17. **共享能力13项清单显式化**（`FS-REQ-030`接口阻塞——已落地5项挂靠，未落地5项标"未实现"）。

## ⑤ 死亡/暂停/存读档/失败恢复

18. **战略暂停与普通暂停分裂成两条独立输入路径**（`M1-R03-INPUT-MODAL-PAUSE`缺失，`FS-REQ-063`
    接口阻塞），依赖尚未开工的 M2-02。
19. **编队死亡/俘获/换阵营/读档四条路径接线**（`FC-REQ-001`部分）。
20. **编队/命令/教义接入存读档**（`FS-REQ-064`**直接阻塞**——`ControlPersistence.cs`零涉及
    Formation/Doctrine，`M4-01`验收标准"读档不串队"现在测不过）。折入 `M4-R01`。
21. **萌生/回巢/野生器官死亡掉落真实接线+存读档**（`M3-R03-RETURN-REAL-COMBAT-EXIT`、
    `M3-R04-WILD-DROP-ON-DEATH`缺失）。
22. **`IsNetworked`从恒true改为显式可切换状态**（`FS-REQ-041/061`接口阻塞——`M4-GATE-03`"断网"
    负向项目前无法被真实触发）。折入 `M3-R03`。
23. **搬运/护送命令死亡分支统一失败原因码**（`FS-REQ-060`接口阻塞，不静默清空/悬挂）。
    **2026-09-16 状态：不可直接开工**——已实读代码核实：`FormationRegistry.HandleMemberDeath`
    确实对 `CommandKind.Carry` 零处理（只判 `Attack`/`OrganCategory`），死亡时成员被静默移出
    编队但 `ActiveCommand` 停在 `Active` 不收口，问题真实存在；但 `SharedCapability.Carry`
    在 `SharedCapabilityCatalog.cs` 标记 `Placeholder`——全仓 `CommandKind.Carry` 仅2处命中
    （枚举声明+UI配色），没有任何生产入口会真正激活一条 Carry 命令，也没有独立的
    "护送/Escort"命令种类（"护送"目前只是 M4-06 产品叙事，无代码实体）。只补死亡分支状态机
    会停留在死代码上，无法从正式入口跑通正负旅程，违反规格完整性硬规则。**需先有 Carry
    真实激活接线（搬运者分配+载荷/目标绑定）才能一并补死亡失败原因码**，建议与那条接线
    故事合并开工，不单独作为本队列的下一项。
24. **战斗内弹体/场/召唤/持续事件存读档**（`CP-REQ-092`缺失，需与存读档里程碑口径确认是否阻塞）。
    **2026-09-16 已完成**——核实：全仓唯一存档触发点在 `CellStageFlow.Exit()`→`SimBridge.End()`→
    `_backend.Dispose()`，发生在整个 `SimWorld` 已销毁之后，弹体/场/召唤（召唤物即普通单位，
    与本项无关）在此之前一律被 Dispose 静默带走，无结算无反馈，违反 `CP-REQ-092`"清空必须
    确定性结算或明确销毁并反馈"的底线；未被 `ARCH-TASK-ENTITY-IDENTITY-01` 等阻塞（存档时刻
    这些实体已随世界一起销毁，不存在"跨局重建身份"的场景）。选择"明确销毁并反馈"分支
    （非完整序列化，条文本身允许）：
    - `SimTypes.cs` 新增 `ProjectileEndReason.WorldTeardown` + `SimTeardownSummary`（计数）。
    - `ISimBackend`/`SimWorld.TerminateTransientsForTeardown()`：`Dispose` 之前对存活弹体
      逐个置终结并真实回传 `ProjectileEndEvent`（同 `JobProjectile.End` 的回传纪律），
      持续区域逐个置终结并计数。
    - `SimBridge.End()` 在 `_backend.Dispose()` 之前调用，结果存入新增
      `SimBridge.LastTeardownSummary` 供断言/遥测。
    - `CellFrameworkValidate.cs [51]`：真起 `SimBridge` 发射1枚长寿命弹体+铺1块长持续区域，
      断言拆除前存活、`sim.End()` 后 `LastTeardownSummary` 两项计数均≥1。Unity batchmode
      `RunAll` 1051/1051 通过（`production/qa/evidence/_unity-validate.log`，本地未入库）。

## ⑥ UI/表现/可访问性

25. **命令队列可视化+插队/取消/清空交互**（`FC-REQ-022/060`部分）。
    **2026-09-17 已完成**——"可视化"（`FormationCommandOverlay` 新增渲染
    `Formation.PendingCommands` 每行+取消按钮+清空队列按钮）、"取消单条"
    （`Formation.CancelQueuedCommand(index)`，按下标安全，越界no-op）、"清队列"
    （`Formation.ClearQueue()`，返回清空条数，终结态/FailReason写入同`InterruptActiveCommand`
    口径）、"插队/追加"（`SquadCommandSystem.Issue` 新增 `queueBehindActive` 参数，
    `HandleCommandInput` 右键分支识别 Shift 修饰键，编队忙时纯追加、编队空闲时立即提升为
    Active 并按需补发 `_sim.IssueCommand`）全部落地。自动化 `CellFrameworkValidate.cs
    [52]`（Formation队列API，15项）+`[53]`（Issue接线含真实Shift+右键输入驱动，18项）+
    真实Play会话验证（真实`FormationRegistry`+`FormationCommandOverlay`绑定，渲染多帧无
    console error）。首轮实现只做了可视化+取消/清空，"插队"输入接线是同日第二轮补完
    （原先误判为独立后续故事，实测后发现范围可控，当场一并做完）。
26. **直控HUD补编队目标/汇合方向/失败提示+战略视角"被直控过"标记**（`FC-REQV-061`缺失）。
27. **拒绝反馈补炮口阻挡/落点非法/召唤容量/信号权限对应码+可访问性辅助**（`CP-REQ-081/082`）。
28. **HUD失败文案改"原因→状态→后果→可恢复方法"四段式模板**（`IC-REQ-013`部分、`CP-REQ-081`、
    `FS-REQ-072`接口阻塞，结构改造可与⑱⑲同批做）。
29. **敌方任务来源可解释调试接口**（`FS-REQ-050`接口阻塞——违反`05_`自己"没契约测试不得声称
    已准备"的规则）。
30. **模板多谱系版本对比/成本冲突可视化+个体面板追溯**（`M3-R05`剩余UI细项）。
31. **战略层占位显示"编队续航"+"资源中断原因"**（`FS-REQ-070`部分，其余四子项登记DEBT-ID）。
32. **受控个体最小本地状态结构供拒绝反馈读取**（`FS-REQ-071`接口阻塞）。

## ⑦ 性能规模/内容扩展

33. **`FormationMovementDriver`逐成员命令迁到`Main/Sim/`Job**（`FC-REQ-040`冲突——正面撞架构
    红线，规模一大必破防）。
34. **定义`INavigationQuery`，圆障碍启发式降级为其一个实现**（`FC-REQ-041`冲突）。
35. **六队形+窄口压缩恢复+体型过滤**（`FC-REQ-042`缺失——M4-R04工作量最大的一块）。
36. **完成判据/五级卡死降级/动态障碍版本号**（`FC-REQ-043/044/045`部分/缺失）。
37. **1/2/8/64与4×64+512压力矩阵**（`FC-REQ-070`缺失，依赖③③先修复）。
38. **食源剩余四类型落地**（`FS-REQ-021`接口阻塞的骨架层面已解决，内容扩展可延后到此优先级）。

## Ⓐ 架构专项（独立任务，禁止被单项队列故事顺手吞并）

2026-09-16 在④/⑤队列实施过程中发现的两条底层架构缺口，均横跨多个队列项，任何单一队列故事
解决时都只能"顺带绕过"而不是"真正解决"——一旦被某个具体故事顺手实现一半，后来者会误以为
问题已解决，实际只是那个故事自己用得到的窄路径。因此正式独立登记，不挂靠任何队列编号。

### ARCH-TASK-ENTITY-IDENTITY-01：单位跨局身份不稳定

**现象**：`SimEntityId` 只在生成它的那个 `SimWorld` 实例内有效，进程重开/换场景后旧值作废
（`ControlPersistence.cs` 类型注释已有此结论）。本仓目前唯一的"稳定 id ↔ SimEntityId"重建
机制只覆盖"当前受控单位"一个特例（靠 `ControlledLogicId` 方案）。

**阻塞**：
- ④-20（编队/命令/教义接入存读档，`FS-REQ-064`）——已折入 `M4-R01`。
- ⑤-20（同一条，⑤节队列文本重复引用同一需求）。
- `WildOrganRegistry._carried`/`_installed`（本次⑤-21已明确排除出存读档范围，
  见 `DEBT-WILDORGAN-OWNED-PERSIST-01`）。
- `GerminationChamberRegistry._bindings`（个体的谱系/表型/版本绑定）。
- 未来任何"按实体记账且要求跨局存活"的系统，一律先撞到这堵墙。

**不应该被吞并的理由**：④-20/⑤-20 的既有措辞（"折入 M4-R01"）容易让人误以为只是 Formation
一个系统的存档问题；实际上是通用基础设施缺口，Formation 只是最先撞上它的消费者之一。

### ARCH-TASK-LINEAGE-PERSISTENCE-01：谱系数据（LineageRegistry）完全没有持久化

**现象**：全仓无 `LineagePersistence.cs`，`LineageRegistry`（M3-03 谱系/表型模板）没有任何
`OnEnter`/`OnExit` 读写——2026-09-16 核实⑤-21时新发现，此前任何审计文档都未登记过。

**阻塞**：
- 萌生腔队列（`GerminationChamberRegistry._queues`）即使解决了 ARCH-TASK-ENTITY-IDENTITY-01
  也无法安全存读档——`GerminationTicket.Version` 引用的 `PhenotypeTemplateVersion` 对象在
  读档后无法解析，因为 `LineageRegistry` 本身是空的。
- 回巢改造（`HomecomingRetrofitService`）相关的任何存读档需求。
- 任何依赖"谱系模板提交历史跨局存活"的功能。

**不应该被吞并的理由**：表面看像是"萌生腔存读档"故事的一个子任务，实际是更底层、影响面更广
的独立缺口；需要独立设计谱系提交历史的序列化与版本对象引用链重建（多处引用同一版本对象时，
读档后必须指向同一个重建实例，不能各自反序列化出不同副本）。

## 已确认无需现在处理（真正可延后，仅登记去处）

`FS-REQ-004`→M8+；`FS-REQ-042`→M5-04；`FS-REQ-051`→M6-04；`FS-REQ-052`→M5-04/M6-03；
`FS-REQ-062`→M5-03a；`AL-REQ-001~080`→M7-02/M7-03。详见 `05_`§8"真正可延后"表。

## 下一步

按①→⑦顺序开故事；同一优先级内按上面编号顺序开（编号不是强制先后，但存在依赖的已在描述里
注明）。**队列第1项**（统一资源/技能账本）建议作为下一个开工的返工 story。
