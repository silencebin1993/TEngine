# DesignDocs 当前权威入口

> 2026-09-18 起，当前产品只有《地球归还》一套语义。旧生物、Cell Stage、债兽和旧 PCG 文档不得作为产品需求输入。

## 必读顺序

1. [ProjectA_GDD.md](ProjectA_GDD.md)：产品最高权威，规定做成什么和 Demo 封闭范围。
2. [ProjectA_Milestones.md](ProjectA_Milestones.md)：阶段门禁与当前实施顺序。
3. [DEMO-IMPLEMENTATION-SPEC.md](../../../production/design/earth-reclamation/DEMO-IMPLEMENTATION-SPEC.md)：带 ERD ID 的自包含实现需求。
4. [DEMO-CONTENT-LOCK.md](../../../production/design/earth-reclamation/DEMO-CONTENT-LOCK.md)：已批准系统的具体配表、地图、战利品、反馈和时间预算。
5. [PRIMITIVE-FULL-DEMO-SPEC.md](../../../production/design/earth-reclamation/PRIMITIVE-FULL-DEMO-SPEC.md)：基元、3×3 电路、有限仓、装卸、合成和实战接线的强制规格。
6. [AI-EXECUTION-PROTOCOL.md](../../../production/design/earth-reclamation/AI-EXECUTION-PROTOCOL.md)：AI 施工纪律、证据与 Windows 可玩构建放行门禁。
7. [STORY-BOARD.md](../../../production/design/earth-reclamation/STORY-BOARD.md)：53 项顺序与状态。
8. [STORY-EXECUTION-CARDS.md](../../../production/design/earth-reclamation/STORY-EXECUTION-CARDS.md)：每项逐条施工卡。
9. [UI-AND-ONBOARDING-SPEC.md](../../../production/design/earth-reclamation/UI-AND-ONBOARDING-SPEC.md)：19 屏交互与错误反馈（教学最后再做）。
10. [MILESTONES.md](../../../production/design/earth-reclamation/MILESTONES.md)：一次领取一个的 Story 清单。
11. [DEMO-ACCEPTANCE.md](../../../production/design/earth-reclamation/DEMO-ACCEPTANCE.md)：全功能 Demo 验收矩阵。
12. [SYSTEMS-SPEC.md](../../../production/design/earth-reclamation/SYSTEMS-SPEC.md)：现有代码复用、Facade 和接入顺序。
13. [00_Implementation_Completeness_Contract.md](detailed/00_Implementation_Completeness_Contract.md)：所有 Story 的完整性强制门禁。

正式版（FG，门禁中）：[方向稿](ProjectA_FullGame_Design.md) → [里程碑](ProjectA_FullGame_Milestones.md) → [详细设计 fullgame/](fullgame/FG00_Completeness_Addendum.md) → `production/design/full-game/`（卡片与看板）。Demo 封版并由用户宣布开启 FG-M0 之前只允许改 FG 文档；Demo 施工、验收与术语审计仍只以 ProjectA_GDD.md 及其 ERD 为准，不得据此改 Demo Story。

63 条 ERD 逐项承接和专项验收映射：[REQUIREMENT-TO-PLAYABLE-TRACE.md](../../../production/design/earth-reclamation/REQUIREMENT-TO-PLAYABLE-TRACE.md)。旧文件保留/清理状态：[OLD-DOC-RETIREMENT-AUDIT.md](../../../production/design/earth-reclamation/OLD-DOC-RETIREMENT-AUDIT.md)。

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
| 术语包装 | production/design/earth-reclamation/TERM-MIGRATION.md | 旧符号到机械领域的包装策略 |

技术参考中的旧生物名是当前代码事实，不是继续实现旧玩法的授权。冲突时始终以 GDD v3.1 和 ERD 需求为准。

## 历史与禁止入口

- Archive/：历史，只用于追溯。
- Cell_Stage_Spec.md：已失效产品案，仅供历史考古；`GDD_Starter_Pack.md`、`Art_Direction_Cell_Stage.md` 已按清理审计删除，若需历史版本只查 Git 历史。
- detailed/01～07：除 00 完整性契约外，只能由当前 Story 点名读取技术片段。
- 最新改动需求/ 中的旧玩家文案与复玩旧案不得作为产品需求；上表两份基元技术参考必须保留用于核对，不因旧标题删除。
- production/design/ 下未被当前 ERD 点名的旧 Epic：只作实现历史。

## 当前唯一 Next

ER-0 文档门禁已于 2026-09-19 完成；下一条仅为 ER1-REUSE-01。用户本轮要求暂不修改业务代码，因此这条属于后续施工，不得把本轮文档交付冒充可玩 Demo。禁止继续旧生物产品 Story。
