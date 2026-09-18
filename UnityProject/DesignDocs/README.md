# DesignDocs 权威索引

> 本页是玩法与技术规格的统一入口。AI 开工时先判断任务类型，再读取对应文档；不要把目录中所有历史文件同时当作当前需求。

## 当前产品权威（2026-09-18 起）

1. [`ProjectA_GDD.md`](ProjectA_GDD.md)  
   **地球归还**完整产品设计：机械聚落经营、蓝图/固件、四阵营清场、任意机器直控、返航终局。  
   内部代号仍为 ProjectA。
2. [`ProjectA_Milestones.md`](ProjectA_Milestones.md)  
   ER-M* 实施顺序与转向门禁；详细竖切片见 `production/design/earth-reclamation/MILESTONES.md`。
3. 迁移配套（Accepted 输入，非第二套产品权威）  
   `production/design/earth-reclamation/`：`SYSTEMS-SPEC.md` / `TERM-MIGRATION.md` / `OPEN-QUESTIONS.md`

## 专项权威（技术保留）

| 任务 | 读取文档 | 权威边界 |
|---|---|---|
| 模块组合 / ComposeEngine | `最新改动需求/组合引擎-正名与全阶段变化词宪法.md` | 保留编译与反应语义；**玩家文案必须机械**；与 GDD 产品对象冲突时以 GDD 为准 |
| AOT Sim、HotFix、Jobs、资源 | `Game_Framework_Design.md` | 技术架构保留 |
| 内容仓与热更新 | `Art_Repo_And_HotUpdate.md` | 仅资源管线；新美术只硬表面 |
| 实现完整性门禁 | `detailed/00_Implementation_Completeness_Contract.md` | 门禁仍适用；示例中的生物产品对象以 GDD 重解释 |

## 已被取代但保留作历史

| 文档/目录 | 状态 | 可以复用 | 禁止继续执行 |
|---|---|---|---|
| `Archive/ProjectA_GDD_bio-mechanical_v2.1_archived-2026-09-18.md` | 旧完整 GDD | 已实现系统与迁移对照 | 债兽、生态债务、生物主叙事、锚点公开进化 |
| `Archive/ProjectA_Milestones_bio-mechanical_archived-2026-09-18.md` | 旧里程碑 | 历史任务与文件线索 | 按旧 M4-R / 债兽队列派工 |
| `detailed/01`～`07`（除完整性契约外） | 生物机械详细规格 | 战斗投射、编队、路径等**技术**条款 | 食源/护群/债兽/Alpha 产品假设 |
| `Cell_Stage_Spec.md` | 更早产品规格 | 历史名与性能目标 | 六阶段倒计时等 |
| `GDD_Starter_Pack.md` | 远期愿景档案 | 灵感 | 当前生产承诺 |
| 仓库根 `production/design/` 旧 Epic | 实现历史 | 代码事实与资产 | 未经新 GDD 引用的产品规则 |

## 冲突判定

- 新 GDD 规定「做成什么」；ComposeEngine / Framework 规定「内部怎么算」。
- 旧代码存在 ≠ 旧产品假设有效。
- 不得自行把生物机械 GDD 与地球归还拼成第三套规则。

## 当前开发入口

见 `production/session-state/DIGEST.md`。  
**立即**：停生物叙事 story → 只还 ER-TECH 存档/身份/直控债 → ER-M1 竖切片。
