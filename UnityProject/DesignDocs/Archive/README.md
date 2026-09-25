# 历史设计文档（禁止作为当前需求）

本目录全部文件都不是现行需求，只用于追溯历史实现和设计决策。现行设计为 GDD 0.2（《地球归还》正式版）。

## 现行入口

- [ProjectA_GDD.md](../ProjectA_GDD.md)（GDD 0.2）
- [ProjectA_FullGame_Milestones.md](../ProjectA_FullGame_Milestones.md)
- [fullgame/FG00_Completeness_Addendum.md](../fullgame/FG00_Completeness_Addendum.md)
- [production/design/full-game/README.md](../../../../production/design/full-game/README.md)

## 本目录内容

| 文件 | 所属 | 说明 |
|---|---|---|
| [ProjectA_GDD_0.1_Demo.md](ProjectA_GDD_0.1_Demo.md) | 《地球归还》Demo，设计 0.1 | **Demo 版本冻结**。2026-09-25 由 FG0-DOC-01 从 `DesignDocs/ProjectA_GDD.md` 移入；Demo 回归测试、ERD / AC 编号追溯仍以它为 Demo 产品上级 |
| ProjectA_GDD_bio-mechanical_archived-2026-09-18.md、ProjectA_Milestones_bio-mechanical_archived-2026-09-18.md | 生物机械版，设计 0.1 | 已被《地球归还》取代 |
| Cell_Stage_Demo_*、First_Playable_* | 细胞阶段 / First Playable，设计 0.1 | 已被取代 |

Demo（0.1）的其余派生文档（`ProjectA_Milestones.md`、`production/design/earth-reclamation/` 下除 TERM-MIGRATION 外的全部文件）留在原位置，文件头标"Demo 版本冻结"，不删除、不再推进。

允许：当前 Story 明确点名后，读取旧文件中的代码路径、字段、性能数据和测试经验；Demo 回归测试继续引用 Demo 的 ERD / AC 编号。
禁止：把本目录任何文件当作 0.2 需求来源；继承旧题材、卡牌节奏、固定主角、细胞阶段、器官/基因获取、债兽/生态债务、旧胜负条件或旧任务顺序。

旧代码仍存在不代表旧产品语义有效；按 SYSTEMS-SPEC 使用机械 Facade 复用算法和实现。
