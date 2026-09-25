# 《ProjectA》全功能 Demo 实施里程碑

> 版本：0.1（历次修订见 [设计版本总账](../../../production/design/DESIGN-VERSIONS.md)）
> 产品权威：[ProjectA_GDD.md](ProjectA_GDD.md)
> 需求权威：[DEMO-IMPLEMENTATION-SPEC.md](../../../production/design/earth-reclamation/DEMO-IMPLEMENTATION-SPEC.md)
> 内容基线：[DEMO-CONTENT-LOCK.md](../../../production/design/earth-reclamation/DEMO-CONTENT-LOCK.md)
> 基元全功能：[PRIMITIVE-FULL-DEMO-SPEC.md](../../../production/design/earth-reclamation/PRIMITIVE-FULL-DEMO-SPEC.md)
> 细项任务：[MILESTONES.md](../../../production/design/earth-reclamation/MILESTONES.md)
> 验收权威：[DEMO-ACCEPTANCE.md](../../../production/design/earth-reclamation/DEMO-ACCEPTANCE.md)
> AI 施工与放行：[AI-EXECUTION-PROTOCOL.md](../../../production/design/earth-reclamation/AI-EXECUTION-PROTOCOL.md)；[STORY-BOARD.md](../../../production/design/earth-reclamation/STORY-BOARD.md)；[STORY-EXECUTION-CARDS.md](../../../production/design/earth-reclamation/STORY-EXECUTION-CARDS.md)；[UI-AND-ONBOARDING-SPEC.md](../../../production/design/earth-reclamation/UI-AND-ONBOARDING-SPEC.md)；[REQUIREMENT-TO-PLAYABLE-TRACE.md](../../../production/design/earth-reclamation/REQUIREMENT-TO-PLAYABLE-TRACE.md)

---

## 0. 总纪律

### 0.1 最终目标

交付一个 45～75 分钟、从主菜单开始、有教学、存读档、机械聚落、生产、蓝图、基元/3×3 有向电路/有限仓/装卸/合成、三次必经远征、两次回城解析、编队、任意机器直控、两种跨派系组合、敌方反制、两阶段 Boss、失败恢复和胜利结算的完整 Demo。

任何里程碑都不能用下列结果冒充完成：只有数据结构、只有调试入口、只有自动战斗、只有白模面板、只有成功路径、只有日志反馈、只有旧题材内容换了标题。

### 0.2 串行与门禁

- 一次只实现一个 Story；当前 Story 未过 Done 表不得开启下一项。
- 一个阶段内可以先写纯规则，但阶段出口必须从正式主菜单或正式场景入口完成玩家旅程。
- 存档不是末期功能；从 ER-1 起，每个新领域状态都必须在同一 Story 或紧邻 Story 接入存读档。
- 新系统同时交付正常、失败、中断、恢复、暂停、换场、读档和生命周期测试；不适用必须写理由。
- 新内容同时交付数据、机械文案、绑定、表现、来源、AI、UI、存档和测试，禁止“功能先做，美术以后再说”的永久占位。
- 所有阶段继续遵守 detailed/00_Implementation_Completeness_Contract.md。

### 0.3 每次开工必读

1. 仓库根 AGENTS.md 与工程 AGENTS.md；
2. production/session-state/DIGEST.md；
3. DesignDocs/README.md；
4. ProjectA_GDD.md；
5. 本文当前阶段；
6. AI-EXECUTION-PROTOCOL.md 与 STORY-BOARD.md 当前唯一 Pending/InProgress 项；
7. STORY-EXECUTION-CARDS.md 当前 Story 逐条施工卡，UI Story 同时读 UI-AND-ONBOARDING-SPEC.md；
8. MILESTONES.md 当前 Story；
9. DEMO-IMPLEMENTATION-SPEC.md 中 Story 点名的 ERD IDs；
10. DEMO-CONTENT-LOCK.md 中与 Story 相关的固定对象/数值/地图/文案；
11. DEMO-ACCEPTANCE.md 对应验收项；
12. SYSTEMS-SPEC.md 的复用映射；
13. REQUIREMENT-TO-PLAYABLE-TRACE.md 中当前 ERD 的主承接和专项验收。

不得通读旧生物设计寻找需求。只有当前 Story 明确点名某个旧技术契约时，才读取其相关章节核对既有行为。

---

## 1. 阶段总览

| 阶段 | 玩家可验证结果 | 禁止带出阶段的缺口 |
|---|---|---|
| ER-0 权威与基线 | 只剩一套地球归还产品真相；旧文档不会误导施工 | 冲突入口、旧 Next、禁用词作为新需求 |
| ER-1 复用与持久身份 | 新局、存档、读档后同一机器不串体，现有控制/编队/战斗基线全绿 | SimEntityId 落盘、旧局引用、无备份坏档 |
| ER-2 机械外壳与正式启动 | 从主菜单进入机械家园，所有玩家可见内容已机械化 | 生物占位、调试入口依赖、裸输入冲突 |
| ER-3 家园经营闭环 | 发电、仓储、五类工作单、维修、堵塞与软失败可玩可恢复 | 静默失败、资源复制、不可达软锁 |
| ER-4 工厂与蓝图闭环 | 生产机器、编辑 3×3 电路、有限仓装卸与合成升级、保存蓝图版本、回厂改造并实战生效，保留个体经历 | 只做外层槽/静态九格；旧图链未接战斗；合成仅文案；事务二次扣款、旧机身份丢失 |
| ER-5 第一次远征 | 3～5 台机器出征、战略命令、任意接管、E 交互、撤离与回城结算 | 调试生成战利品、无失败结算、区域切换重复实体 |
| ER-6 跨派系编译与反制 | 首次夺技编译后再赴铸造外围回收重炮，第二次解析与编译、暴露和克制编制形成完整第二循环 | 组合硬编码、跳过外围直接打 Boss、预览和实战不一致 |
| ER-7 主核心与返航结局 | 两阶段 Boss、失败重试、导航信标、胜利结算完整 | 只有日志结局、Boss 阶段重复奖励、战后无存档 |
| ER-8 完整性与发布候选 | 全旅程、反向旅程、压力、可访问性、机械题材和内容绑定全部过门禁 | P0/P1 缺陷、占位资产、未登记债务 |

---

## 2. ER-0 — 权威与范围冻结

### 目标

让 AI 在不开旧文档的情况下也能完整实现 Demo；旧文档只能用于代码事实核对。

### 必须完成

- GDD 0.1、DEMO-IMPLEMENTATION-SPEC、DEMO-ACCEPTANCE、SYSTEMS-SPEC、MILESTONES 互相链接且没有冲突。
- DesignDocs/README 与 production/design/README 只把上述文档列为当前产品/实施入口。
- production/design/earth-reclamation/GDD.md 改成短重定向，不保留第二份产品正文。
- 旧生物设计从权威索引移除，Archive README 明确“禁止作为需求输入”。
- 禁用词扫描区分“历史/迁移文档允许”和“当前玩家文案禁止”。

### 出口

随机给一个新 AI 只提供 README，它能找到唯一 GDD、当前 Story、需求 IDs、验收项和复用映射；不会打开旧 Cell Stage 或债兽文档当需求。

---

## 3. ER-1 — 复用基线与跨局身份

### 目标

在不改产品行为的前提下证明现有控制、编队、装配和战斗链可复用，并建立完整战役存档骨架。

### 必须完成

- 固定种子记录现有 SimBridge、ControlledUnitId、死亡回弹、CameraDirector、InputRouter、SquadCommandSystem、UnitLoadout、DirectControlActions、CarrierCompiler/ComposeEngine 基线。
- CampaignState、MachineRecord、BlueprintRecord、BuildingRecord、EventLedger 和版本头可写可读。
- 跨局只保存 LogicId，不保存 SimEntityId；恢复顺序与 DEMO-IMPLEMENTATION-SPEC 一致。
- 临时文件、原子替换、bak、坏档拒绝与备份恢复可验证。
- 新局、同局重进、跨进程读档、控制目标死亡四条身份路径不串体。

### 出口旅程

主菜单创建新档 → 进入当前可玩场景 → 切换两台友军 → 保存退出 → 读取 → 控制恢复到同一 LogicId 或按规则安全回退；损坏主档可恢复备份。

---

## 4. ER-2 — 机械外壳与正式启动

### 目标

玩家通过正式入口看到并理解“归还核心 + 机械机器”，旧生物包装完全退出 Demo。

### 必须完成

- 主菜单、新建、继续、读取、设置、退出可用。
- 归还谷地固定场景、归还核心、ERC-001/002 两台初始搬运机、发电机/仓库/装配站残骸存在。
- 旧内部 ID 通过机械目录 Facade 显示新名称、描述、图标和标签。
- 玩家可见模型、材质、VFX、SFX、任务和 UI 无禁用词和生物占位。
- 输入域、镜头三状态、模态让位和键位重绑接入正式 UI。
- 教程状态机可保存；第一段“修发电机 → 看搬运 → 生产战斗机 → 接管射击”正式可玩。

### 出口旅程

全新玩家从主菜单进入，15 分钟内完成第一次生产和接管，并能回答“我是核心，机器是身体，家里造的机器能出征”。

---

## 5. ER-3 — 家园经营闭环

### 目标

建立一个简单但真实的机械聚落，而非静态背景菜单。

### 必须完成

- 电力脏重算、优先级、Brownout、恢复续作。
- 仓库容量、地面物、货舱、预留、运输、满仓与掉落守恒。
- Haul、Build、Repair、Salvage、Recharge 五类工作单全状态机。
- 机器按优先级、订单年龄、距离和稳定 ID 确定性领单。
- 路径无进展、目标失效、机器被接管、机器死亡、断电均可恢复。
- 家园 HUD、建筑面板、机器工作面板、警报合并和资源明细完整。
- 应急拆解防止无废料、无工作机的永久软锁。
- 全部新状态进入存档和固定种子回放。

### 出口旅程

玩家主动制造一次超载和仓满，能从 HUD 理解原因，调整电力优先级、扩容或搬运后恢复生产；保存读取后事务不重复、不丢物。

---

## 6. ER-4 — 工厂、蓝图和机器个体闭环

### 目标

让玩家真正“造机器、改蓝图、保留老机器”。

### 必须完成

- Produce 与 Retrofit 队列、资源事务、断电暂停、出口堵塞、取消退款和幂等结算。
- 三种底盘、四主组件、二功能组件、四结构模块、六固件的数据与机械绑定齐全。
- 蓝图编辑器完成目录、槽位、顺序、预览、冲突、保存、草稿和引用旧版本提示。
- 既有 SlotGrid/PathCompiler 的 3×3 有向电路经正式编辑器可画删、保存、读档并进入 AI/直控共用战斗；默认空槽路径不阻断开局攻击。
- 8 格有限基元仓、待领取、实例守恒装卸、正式合成/拆解、断电/取消/满仓与升级后的实战效果完整可用；不以 F8/旧 IMGUI 或静态网格充数。
- 编译签名稳定；新版本只影响新生产和回厂改造。
- 个体编号、经历、伤势、统计和装配签名持久化。
- 回厂改造保留 LogicId 与经历；不能改造正在直控、远征或执行不可中断事务的机器。

### 出口旅程

玩家生产 ERC-003 → 完成工作 → 打开 3×3 电路板装聚焦镜并画/删导线 → 保存蓝图新版本 → ERC-003 回厂改造 → 再次接管并打教学靶；在基元仓补印第二件、合成精校镜、再次保存/改造/实战。编号和经历不变，实例与材料守恒，保存读取后仍一致。

---

## 7. ER-5 — 第一次远征与任意接管

### 目标

证明“家与战场一体”和“任意接管”是完整玩法而非演示按键。

### 必须完成

- 修复信号塔、带宽和覆盖；破碎都市解锁。
- 远征准备校验、3～5 台名单、角色覆盖、带宽、货舱和阻塞原因。
- 区域切换事务、出发快照、失败回滚和自动存档。
- Move、Attack、Guard、Retreat、选择集、编组和战略暂停正式 UI。
- Direct/Strategy/Transition、Tab 接管、点击接管、失败码、死亡回弹和无目标回战略。
- E 交互正式完成残骸拆解、战利品装载、终端读取、撤离确认。
- 静默侦察机、干扰机、监听节点、撤离点与远征失败结算齐全。
- 回城按真实货舱结算，阵亡个体不复活，黑匣子记录可带回。

### 出口旅程

同一批家园机器出发 → 战略下令 → 接管至少两台 → 在干扰区收到明确拒绝 → 摧毁监听恢复接管 → 装载战利品 → 撤离回城；任一环节保存/读档不复制个体或物品。

---

## 8. ER-6 — 跨派系编译、暴露与反制

### 目标

证明最重要的构筑差异点：敌方技术改变下一次远征，敌方又以可读方式回应。

### 必须完成

- 未归档模块、解析台、重复内容处理和技术数据结算。
- 熔穿过载、标记跳转两种反应的预览、实战、VFX、SFX、日志和敌方对策。
- 信号暴露来源、降低手段、30/60/90 阈值、一次性事件和 HUD 来源明细。
- HeatResistant、Flanker、JammerSupport 三个 adaptationId；出发时锁定并提前给情报。
- 铸造步进炮、护甲机、维修机完成数据、表现、AI、掉落和测试。
- 铸造前哨外围第二次出征有固定重炮保底与主动撤离；回城解析重炮，保存并实装“熔穿过载”蓝图后才解锁核心区。
- 所有组合走 CarrierCompiler/ComposeEngine 通用规则，禁止按当前内容 ID 硬编码伤害结果。

### 出口旅程

第一次远征夺静默技术 → 解析 → 保存“标记跳转”蓝图 → 改造老机器 → 第二次远征铸造外围夺重炮并撤离 → 回城解析、保存并实装“熔穿过载”蓝图 → 第三次远征前敌方预告克制 → 玩家换构筑或战术推进核心区。

---

## 9. ER-7 — 主核心、失败与返航结局

### 目标

把 Demo 从“能循环”收口成有目标、有高潮、有结局的完整短战役。

### 必须完成

- 第三次远征重返持久的铸造前哨地图；目标、两供能节点、主核心六状态、两个战斗阶段和阶段字幕。
- Boss 前自动存档；撤离/失败按明确规则恢复，不允许磨血或重复奖励。
- 主核心摧毁后停止敌方新生产，核心数据只结算一次。
- 导航信标建造、供电、主动启动、10 秒演出、胜利状态和结算页面。
- 归还核心被毁的失败界面、最近自动档重试和返回主菜单。
- 结算展示幸存、阵亡、蓝图、切换、时长和关键经历。

### 出口旅程

从 Boss 前存档进入 → 完成两阶段战斗 → 撤离 → 建造并启动信标 → 胜利结算 → 返回主菜单 → 继续按钮显示已完成存档且不会重复发奖。

---

## 10. ER-8 — 完整性、性能与发布候选

### 目标

把“功能齐”变成“正常玩家能完整玩完”。

### 必须完成

- DEMO-ACCEPTANCE 全部 Must 项通过，需求 ID 到代码和测试 100% 可追踪。
- 完整旅程机器人从主菜单通关；反向旅程覆盖断电、仓满、路径失败、干扰、死亡、远征全灭、坏档和 Boss 重试。
- 典型与压力规模满足性能预算，无稳态 GC、无 HotFix 每帧敌人全扫。
- 三种速度、战略暂停、模态 UI、连续切换、场景往返和多次读档不串态。
- 内容绑定清单无缺口，玩家可见无禁用词、开发占位、内部 ID 或调试面板。
- 可访问性最低集、键位冲突、UI 缩放、字幕和非颜色反馈通过。
- 人工只审核乐趣、节奏、信息负担、视觉和声音品味；功能遗漏由自动验收发现。

### 最终出口

独立安装或干净环境启动后，普通玩家无需开发者解释即可创建战役、理解核心身份、完成全部循环、遇到并恢复失败、打败主核心、启动返航、看到结算。任何正常流程仍依赖控制台、GM、测试场景或人工修档，均不得标 Demo Complete。

---

## 11. 当前唯一 Next

1. 完成 ER-0 文档一致性与旧入口清理；
2. 执行 ER-1 的 REUSE-01 基线审计；
3. 严格按 production/design/earth-reclamation/MILESTONES.md 一次领取一个 Story。

禁止继续领取任何生物叙事、债兽、生态债务、器官幻想、旧六阶段或旧 PCG 产品故事。
