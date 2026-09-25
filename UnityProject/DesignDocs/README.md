# DesignDocs 当前权威入口

> 2026-09-25 起，现行设计为 **GDD 0.2（《地球归还》正式版）**；Demo（0.1）设计冻结，只用于 Demo 回归与追溯。旧生物、Cell Stage、债兽和旧 PCG 文档不得作为产品需求输入。

## 必读顺序（0.2 正式版）

1. [ProjectA_GDD.md](ProjectA_GDD.md)：GDD 0.2，产品最高权威（已合并正式版设计案与里程碑默认决策 D-01～D-22）。
2. [ProjectA_FullGame_Milestones.md](ProjectA_FullGame_Milestones.md)：FG-M0～M15 的顺序、出口与出口旅程。
3. [fullgame/FG00_Completeness_Addendum.md](fullgame/FG00_Completeness_Addendum.md)：FG 完整性附加契约（B01～B25 正常游戏基线）。
4. [detailed/00_Implementation_Completeness_Contract.md](detailed/00_Implementation_Completeness_Contract.md)：所有 Story 的完整性强制门禁（Ready 十项与完成矩阵）。
5. [production/design/full-game/README.md](../../../production/design/full-game/README.md)：FG 制作目录（领取流程、证据规则）。
6. [FG-STORY-BOARD.md](../../../production/design/full-game/FG-STORY-BOARD.md)：139 条 Story 的顺序与状态。
7. [FG-STORY-CARDS.md](../../../production/design/full-game/FG-STORY-CARDS.md)：逐条施工卡片；只读自己那一张。
8. `fullgame/FG01～FG17`：卡片点名的章节与需求 ID（不要整本通读）。
9. [FG-GAP-REGISTER.md](../../../production/design/full-game/FG-GAP-REGISTER.md)：规格漏写、但正常玩家需要的缺口登记。
10. [ProjectA_FullGame_Design.md](ProjectA_FullGame_Design.md)：GDD 0.2 的展开背景与名表出处，需要时读对应章节。
11. [TERM-MIGRATION.md](../../../production/design/earth-reclamation/TERM-MIGRATION.md)：术语迁移 0.2（§6 正式版名表与禁用词，题材审计词表与之逐词一致）。

设计版本：Demo 及更早的全部设计为 **0.1**，正式版为 **0.2**。每次设计/计划文案提交的修订号（0.x.y）与旧版本号对照见 [DESIGN-VERSIONS.md](../../../production/design/DESIGN-VERSIONS.md)。

## Demo（0.1，版本冻结）

以下文件标有"Demo 版本冻结"，不删除、不再推进，只用于 Demo 回归测试、ERD / AC 编号追溯与代码考古：

- [Archive/ProjectA_GDD_0.1_Demo.md](Archive/ProjectA_GDD_0.1_Demo.md)：Demo 的 GDD 0.1 原文。
- [ProjectA_Milestones.md](ProjectA_Milestones.md)：Demo 阶段门禁 ER-0～ER-9。
- [production/design/earth-reclamation/](../../../production/design/earth-reclamation/README.md)：DEMO-IMPLEMENTATION-SPEC、DEMO-CONTENT-LOCK、PRIMITIVE-FULL-DEMO-SPEC、AI-EXECUTION-PROTOCOL、STORY-BOARD、STORY-EXECUTION-CARDS、UI-AND-ONBOARDING-SPEC、MILESTONES、DEMO-ACCEPTANCE、SYSTEMS-SPEC、REQUIREMENT-TO-PLAYABLE-TRACE 等（TERM-MIGRATION 除外，它已升为 0.2）。

Demo 的 63 条 ERD 逐项承接见 [REQUIREMENT-TO-PLAYABLE-TRACE.md](../../../production/design/earth-reclamation/REQUIREMENT-TO-PLAYABLE-TRACE.md)；旧文件保留/清理状态见 [OLD-DOC-RETIREMENT-AUDIT.md](../../../production/design/earth-reclamation/OLD-DOC-RETIREMENT-AUDIT.md)。代码与自检里引用的 ERD / AC 编号仍指向这些冻结文档，不得删除或改号。

## 按需技术参考

| 任务 | 可读文件 | 只允许读取什么 |
|---|---|---|
| 架构/性能 | Game_Framework_Design.md | AOT/HotFix/SimBridge/Jobs/资源边界 |
| 控制身份 | migration/Control_Lifecycle_Contract.md | 已实现不变量、回弹、恢复与存档禁区 |
| 镜头/输入 | migration/View_And_Input_Contract.md | CameraDirector、InputRouter、scope 互斥 |
| 编队命令 | migration/Squad_Command_Contract.md | 已实现命令状态机与暂停排队 |
| 个体装配 | migration/Unit_Loadout_Contract.md | 已实现装配登记与生命周期 |
| 直控动作 | migration/Direct_Control_Contract.md | 已实现动作入口与已知 E 交互缺口 |
| 组合引擎 | 最新改动需求/组合引擎-正名与全阶段变化词宪法.md | ComposeEngine 内部规则；玩家文案仍以机械规格为准 |
| 基元装配 | 最新改动需求/细胞肉鸽-基元卡牌包装.md | 已实现的 3×3 有向图、空槽被动、有限仓与装卸事实；以新规格为玩家规则 |
| 基元字段/结算 | 最新改动需求/代谢切片-冻结总案-基元与美术.md | 已实现的正交字段和结算顺序；旧卡牌与生物美术不继承 |
| 术语包装 | production/design/earth-reclamation/TERM-MIGRATION.md | 旧符号到机械领域的包装策略；§6 为 0.2 名表 |

技术参考中的旧生物名是当前代码事实，不是继续实现旧玩法的授权。冲突时始终以 GDD 0.2 与 FG 详细设计为准。

## 历史与禁止入口

- Archive/：历史，只用于追溯。
- Cell_Stage_Spec.md：已失效产品案，仅供历史考古；`GDD_Starter_Pack.md`、`Art_Direction_Cell_Stage.md` 已按清理审计删除，若需历史版本只查 Git 历史。
- detailed/01～07：除 00 完整性契约外，只能由当前 Story 点名读取技术片段。
- 最新改动需求/ 中的旧玩家文案与复玩旧案不得作为产品需求；上表两份基元技术参考必须保留用于核对，不因旧标题删除。
- production/design/ 下未被当前 ERD 点名的旧 Epic：只作实现历史。

## 当前 Next

0.2 正式版施工中：当前里程碑与当前 Story 以 `production/session-state/DIGEST.md` 的"正式版（FG）"段和 [FG-STORY-BOARD.md](../../../production/design/full-game/FG-STORY-BOARD.md) 为准。Demo 队列（ER-0～ER-9）已冻结，禁止继续旧生物产品 Story。
