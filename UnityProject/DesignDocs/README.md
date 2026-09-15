# DesignDocs 权威索引

> 本页是玩法与技术规格的统一入口。AI 开工时先判断任务类型，再读取对应文档；不要把目录中所有历史文件同时当作当前需求。

## 当前产品权威

1. [`ProjectA_GDD.md`](ProjectA_GDD.md)
   完整产品设计：核心循环、器官/基因/谱系、RTS、意识传递、敌方进化、首领、PCG、胜负、表现和范围。
2. [`ProjectA_Milestones.md`](ProjectA_Milestones.md)
   AI 实施顺序、任务边界、依赖和验收。当前开发不得跳过里程碑门禁。

## ProjectA 详细规格（里程碑必须定向读取）

| 文档 | 解决的问题 | 主要里程碑 |
|---|---|---|
| [`detailed/00_Implementation_Completeness_Contract.md`](detailed/00_Implementation_Completeness_Contract.md) | 防止只实现任务标题/示例；规定需求 ID、完整性十四面、自动验收与 Done 证据 | 所有故事 |
| [`detailed/01_Faction_Survival_And_Ecology.md`](detailed/01_Faction_Survival_And_Ecology.md) | 敌我生存目标、共享资源、食源、搬运、供养、繁殖与 AI 目标 | M4–M7 |
| [`detailed/02_Combat_Projection_And_Primitives.md`](detailed/02_Combat_Projection_And_Primitives.md) | 发射点、瞄准角、多发、命中阶段、基元锚点、子事件和 AI/玩家一致性 | M1–M3、M6–M7 |
| [`detailed/03_Formation_Command_Pathfinding_And_Doctrine.md`](detailed/03_Formation_Command_Pathfinding_And_Doctrine.md) | 十类命令、六教义、共享路径、队形、直控脱队与大军规模 | M4 |
| [`detailed/04_Alpha_Pack_And_Adaptive_Abilities.md`](detailed/04_Alpha_Pack_And_Adaptive_Abilities.md) | 一只核心 + 真实护群、食源/诱离、学习基元生成复杂技能 | M6–M8 |
| [`detailed/05_M1_M4_Backfill_And_Acceptance.md`](detailed/05_M1_M4_Backfill_And_Acceptance.md) | 对已完成 M1–M3 与当前 M4 做规格补录、返工排序和自动总验收 | 当前 M4 |
| [`detailed/06_M9_M10_PCG_Content_Polish_And_Acceptance.md`](detailed/06_M9_M10_PCG_Content_Polish_And_Acceptance.md) | 受控 PCG、图章/导航/生态投放、正式视觉/声音、可访问性、演示版与玩家门禁 | M9–M10 |
| [`detailed/07_M0_M10_Story_Coverage_Matrix.md`](detailed/07_M0_M10_Story_Coverage_Matrix.md) | M0～M10 全 77 条任务的详细文案路由、覆盖 ID、玩家结果、正常/负向自动测试 | 所有故事 |

里程碑文档只决定顺序；详细规格决定“行为到底是什么”。任务卡必须点名需求 ID，不得只写“参考 GDD”。

## 专项权威

| 任务 | 读取文档 | 权威边界 |
|---|---|---|
| 代谢、基因组合、ComposeEngine | `最新改动需求/组合引擎-正名与全阶段变化词宪法.md`（唯一真权威；`AUTHORITY_AND_CONFLICTS.md` 只是导航，同目录其余多为 Superseded/stub） | 保留化学/编译语义；若与完整 GDD 的产品对象、获取、传播或节奏冲突，以完整 GDD 为准 |
| AOT Sim、HotFix 边界、Jobs、资源和模块框架 | `Game_Framework_Design.md` 相关章节 | 技术架构保留；旧六阶段产品流程和时间压力失效 |
| 内容仓与热更新 | `Art_Repo_And_HotUpdate.md` | 仅资源管线 |

## 已被取代但保留作历史

| 文档/目录 | 状态 | 可以复用 | 禁止继续执行 |
|---|---|---|---|
| `Cell_Stage_Spec.md` | 产品规格已被取代 | 已实现内容名、历史性能目标、可迁移素材 | 60 分钟六阶段、倒计时压力、固定卡牌节奏、旧终局 |
| `GDD_Starter_Pack.md` | 远期愿景档案 | 主题与灵感 | 从细胞到宇宙的当前生产承诺 |
| `Archive/` | 归档 | 历史证据 | 任何当前需求 |
| 仓库根 `production/design/` | 实现历史与迁移输入 | 已完成系统、目录、资产和技术事实 | 未经新 GDD 明确引用的产品规则 |

## 冲突判定

- 新 GDD 规定“做成什么、玩家如何体验”；专项文档规定“保留系统内部如何工作”。
- 旧代码或旧文档已经存在，不代表其产品假设继续有效。
- 遇到冲突不得自行折中：先记录冲突，再按 GDD 和里程碑的迁移任务处理。
- 不删除历史完成事实；但历史事实不能反向否决已批准的新产品方向。

## 当前开发入口

当前实际进度见 `production/session-state/DIGEST.md`。截至 2026-09-15，M1–M3 与 M4-01～M4-05 已按旧任务卡完成；下一项必须先执行详细计划中的 **M4-R00 需求覆盖差距审计**，再继续 M4-06。历史 Done 不自动等于满足新完整性契约。
