# 权威切换与冲突表（2026-08-11，2026-08-12 补丁：D7/D8 文案融合）

> [!IMPORTANT]
> **2026-09-11 产品权威边界补丁：** 本文件继续作为代谢化学、器官/基因内部组合语义和 ComposeEngine 的专项权威；游戏的产品循环、对象层级、获取与装配传播、RTS/意识传递、敌方进化、首领和胜负，以上级目录 [`ProjectA_GDD.md`](../ProjectA_GDD.md) 为准。下文指向的 `production/design` 文件仅在这些专项内部规则上可复用，不得覆盖新 GDD 的产品语义。实施迁移按 [`ProjectA_Milestones.md`](../ProjectA_Milestones.md) 执行。

> **用途**：给总控 / Claude Code 短读。禁止为「对账」整本通读旧 §17 与本夹全部文件。  
> **决策**：人要求先消双源，再实现新需求。  
> **补丁（2026-08-12）**：`epic/metabolic-slice` 001～008 已 Done，产品化/玩家化后续工作转入 `epic/metabolic-playerization`（Decision 表 D1~D8，见 `production/epics/metabolic-playerization/EPIC.md`）。本文件的化学/代谢权威结论不变，仅补充：D3（新设计进池，旧词缀卡默认出池）+ D3b（`cell_tables` 已全表改代谢口径，story-005 完成）+ D8（老化学需求正式 Retired，非仅 Paused）。

---

## 1. 当前权威（化学 / 代谢组合）

| 主题 | 唯一权威 | 何时读 |
|---|---|---|
| **器官 vs 基因（攻击方式 / 改装）** | `production/design/combat-identity-rework/DESIGN.md` + `CATALOG.md` | 改目录、装配栏、编译器、战斗文案；**sprint-027**。冻结总案 §5.2/§5.3/§7 **不得**当施工清单 |
| **结构器官（第三类，常驻被动全体生效）** | `production/design/organelle-structural-tier/DESIGN.md` + `CATALOG.md` | Draft，2026-09-03 方向+获取方式（直接掉落）+槽位规则（一 Tag 一槽互斥）已定案；清单数值/复合渲染路径待估；不改攻击器官/基因分类法 |
| **正名 + 全阶段变化词 / 全能组合代数** | `组合引擎-正名与全阶段变化词宪法.md` | 扩词库、跨阶段皮、**默认全能混合**（非白名单 pair）；**sprint-010** |
| 冻结宪法（细胞 v1 基元/字段/管道/禁令） | `代谢切片-冻结总案-基元与美术.md` | 仅管道序/正交字段/禁 `is` 组合；**目录与分类以 combat-identity-rework 为准** |
| ComposeEngine 独立库（做法 C API） | `组合引擎-ClaudeCode需求规格.md` | 实现/改核时；与正名宪法一起守做法 C |
| 卡牌/切片包装 | `细胞肉鸽-基元卡牌包装.md` | ComposeEngine 绿后的下一窗 |
| 复玩三轴 | `复玩三轴-ClaudeCode需求规格.md` | 包装落地后 |
| 玩家白话名词 | `代谢切片-白话名词说明书.md` | 写 UI/文案时；程序窗默认不读 |
| 美术出图 | `美术AI静图出图规范.md` | 美术窗；程序窗禁止读 |

**正名（2026-08-13）**：对外权威名 = **组合引擎 / ComposeEngine**（sprint-010 story-001 **已真改符号**：库目录/命名空间/DLL/Unity Plugins 路径/规格文件名均已切换）。元素反应 ⊂ Substance 轴；变大/散射/旋转/位移等是一等能力。细胞 v1 目录不因此作废。

旧权威中的化学相关节：

| 旧节 | 状态 |
|---|---|
| `Cell_Stage_Spec.md` §17 | **Superseded**（正文保留作史，实现禁止照做） |
| `Game_Framework_Design.md` §5.8 | **Superseded** |
| `cell.ReactionRule` / `cell.CatalystRule` 表规划 | **确认不走 Luban 表**；新架构改用代码内目录 `MetabolicSlice/ContentCatalog/` 硬编码代替，两张表规划已从 `Game_Framework_Design.md` §6 表格删除 |
| epic `combat-alchemy` / sprint-006 | **Retired（D8，2026-08-12 正式退役）**，禁止再派 Worker；文件保留原位（`production/epics/combat-alchemy/`）仅供历史查阅，**不**迁移到 Archive（迁移不改变权威性，徒增 diff，人未要求物理搬迁） |

仍有效（非化学专章）：`Cell_Stage_Spec` 其它章、`Game_Framework` 热更边界 / AOT Sim / 模块框架等——**只按节读冲突处**。

---

## 2. 冲突摘要（旧 §17 路线 vs 冻结总案）

| ID | 旧（§17 / combat-alchemy） | 新（冻结总案 / ComposeEngine） | 处置 |
|---|---|---|---|
| C1 | 状态×状态反应挂在 `StatusSystem.ApplyTimed` | 做法 C：基元改 `Packet`/`HitEvent`/`TagSet`，引擎管道叠加 | **废弃旧挂点设计**；接入时另定桥 |
| C2 | 不新增词缀/效果 Kind，只重组 32 词缀 | 新基元种类（器官/基因等）+ 反应注册表 | 新路线为准；旧「禁止新 Kind」仅适用于旧 alchemy epic |
| C3 | 催化卡 = 运行时给 EffectSpec 注入 Affix | 催化 = tag/状态反应表 + 正交修饰叠乘 | 勿实现旧 CatalystRule 故事 |
| C4 | `OnReaction` 订阅 ReactionEvent | ComposeEngine 事件/正规形另定；游戏侧后接 | 旧 story-003 不做 |
| C5 | 内容仍是「卡牌+词缀」主循环 | 代谢切片 + 有向管 + 双系统（切片/储备囊）+ 三轴 | 产品形态以冻结总案为准；**2026-08-12 更新**：D3/D3b 已定案并落地——旧 135 张词缀战斗卡整体 Delist（`cell_tables/carddata.py` 归档见 `_legacy_carddata_archive.py`，不参与生成），当前抽卡池为 28 张代谢卡（17 器官 + 11 基因），三选一**流程**骨架保留（D1/D1b），但**内容**已无词缀卡 |
| C6 | 热更层 `ReactionSystem` 直接做判定 | 核心库 **零 Unity**，游戏只写桥 | 先独立库，再桥；禁止把引擎写进 `GameLogic` 当核心 |

未冲突、仍守：热更 O(1)/事件驱动、判定不下沉 `Main/Sim`、Built-in RP / 无 Entities、资源 `GameRes/Raw`。

### C7 「Heat」一词的三重含义（2026-09-12 登记，已按改名划界处置）

ProjectA M2-03c 要给直控释放做「过载」时发现，仓里 `Heat` 同名不同义共三处：

| # | 标识 | 语义 | 生命周期 | 尺度 | 现状 |
|---|---|---|---|---|---|
| 1 | `ComposeEngine.Core.Packet.Heat` | 装配链路**过路**热负荷 | 每次组合 `new Packet()`，一趟链走完就丢 | 过热阈值 **8** | **当前恒为 0**，见下 |
| 2 | `ComposeEngine.Core.SubstanceVector.Heat` | 九维物质代数的**温度**维，**有正负**（Fire +2 / Ice −1.5，负=冷） | 随 Tag 袋混合 | 权重级 | 现役 |
| 3 | `GameLogic.Control.UnitVitalsRegistry.Strain` | **身体过载债**，按 `SimEntityId` 跨释放累积 | 跟着一具身体，25/s 衰减 | 阈值 **100**，滞回 60 | 现役（M2-03c） |

**①当前是死链路**：写 `Packet.Heat` 的四个器官 `org_lens` / `org_merge` / `org_radiator` /
`org_insulate` 在 `OrganelleCatalog` 里**全部 `isRetired: true`**；现役的 `gene_heatshock`
（`HeatShockModule`）**只读不写**，故恒走「未过热则散热」分支，`Heat 0→0` 无可观察差异
（`MetabolicSlice/DebugTools/EmergenceSmoke.cs` 早有同结论注释）。

**⚠️ 本表 §5.2 的器官表把 6 个器官写成「读 Heat」，那是设计意图不是运行现状**——照它判断
「热这套已经在跑」会出错。

**处置（2026-09-12 人拍板 A：改名划界，不做统一）**：③ 已全量改名 `Heat`→`Strain`、
中文「热债」→「过载债」（含 HUD 文案与 `BattleHud.uxml` / `BattleUI.uss` 注释），
①② 保留原名不动。改名后 Unity 自检 290/290 逐条与改名前一致（纯重命名，零行为改动）。
理由与「不要改回去」的完整论证写在 `GameLogic/Control/UnitVitals.cs` 的 `UnitVitalsRegistry` 类注释里。

**用词已全仓对齐**：`ProjectA_GDD.md`（4 处：L355 / L420 / L462 / L1013）与
`ProjectA_Milestones.md`（2 处：M2-03 实施第 4 条、M3 §335）里原先的「热债」**全部指 ③**，
已一并改为「过载债」。核实依据：GDD 说的「蓄力/过载」在内核是 `Charge` 原型的冲刺前摇
（`JobSteering.cs:110`，`AttackTimer` 复用为蓄力计时），与 ComposeEngine 的 `Packet.Heat` 无关；
ComposeEngine 的 `Capacitor` 放大的是 `Energy` 不是 `Heat`，`Charge` 在 `SubstanceVector` 里是「电」维。
于是「热」这个词在本仓只剩 ①② 两个 ComposeEngine 内部含义，grep 不再断链。

**未决（将来复活 ① 时才需要拍板）**：若复活 `org_lens` 等产热器官，要不要把 `Packet.Heat`
结算完回灌进 `Strain`（两者尺度差 12.5 倍，需定换算）。当前**刻意不接**。

---

## 3. 已存在旧半成品代码（勿继续扩）

仓库内曾有按旧 §17 开工痕迹（至少）：

- `GameLogic/Battle/ReactionSystem.cs`
- `GameLogic/Battle/ReactionRuleSpec.cs`
- 及相关 `StatusSystem` / `EffectDealDamage` / Luban 加载挂钩

**状态：已完成删除（story-006，2026-08-11）**。上述文件与联动挂钩已整删并接入新桥接 `MetabolicSliceBridge`，代码层零残留旧类型引用（`ReactionSystem`/`ReactionRuleSpec`/`ReactionSignal`/`AddReactionRule`/`ReactionRules` 全仓 grep 零命中）。证据：`qa/evidence/metabolic-slice-006-bridge-and-purge.md`。

---

## 4. Claude Code / 工人纪律

1. `epic/metabolic-slice` 001～008 已 Done；当前优先领 `production/epics/metabolic-playerization/` 当前 Ready story（sprint-008，D1~D8）；总控自动串行，不问人「下一步」。  
2. 一窗只读本 story 点名的规格文件 + AUTHORITY 短文；禁止整本 GDD_Starter / FirstPlayable / 旧 §17「对照实现」。  
3. 代码主落点：`GameLogic/MetabolicSlice/` + 可改 `ComposeEngine/`；ComposeEngine 核心零 Unity。  
4. Spec 深度回写与 OpenSpec：story-007。  
5. **组合出口无「纯表现档」（2026-08-13）**：`HitEvent` 的 `Count`/`Scale`/`Spin`/`Orbit`/`ExplodeOnHit` 等修饰轴**一律进机制**（多段、半径、轨迹、爆炸段）。禁止 Preflight/Worker 再按旧 `HostApplyContract`「可忽略纯表现」口径决策。权威 Apply 清单：`ComposeEngine/docs/HostApplyContract.md`。真·VFX（闪光/拖尾/镜头）走 Presenter，不占用这些字段装成「不结算」。

---

## 5. 制作队列 —— **已作废，内容已移出**

> 2026-09-13 起 Superseded；2026-09-14 正文移入 `archive/冻结总案-已作废章节-2026-09-14.md`。
> **当前队列只看 `production/session-state/DIGEST.md` 与 `DesignDocs/ProjectA_Milestones.md`。**
