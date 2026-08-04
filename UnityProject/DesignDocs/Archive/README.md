# 历史设计文档（已被取代，不再维护）

本目录内文档**不是当前开发目标**。写代码前请读 `DesignDocs/` 根目录下的：

- `Cell_Stage_Spec.md` — 细胞阶段正式规格（当前唯一权威）
- `Game_Framework_Design.md` — 技术框架设计
- `GDD_Starter_Pack.md` — 全项目 GDD

## 为什么归档

2026-08-04 项目从"验证性 Demo"转入**正式生产**，设计目标发生了不兼容的变更：

| 项 | 归档文档（旧） | 当前（新） |
|---|---|---|
| 阶段范围 | 细胞→器官→生物 三段 | 只做细胞阶段，做到内容巨大 |
| 单局时长 | 25-30 分钟 | 约 60 分钟 |
| 进化语义 | 达阈值 → 进入下一阶段 | 局内突变构筑节点，不离开细胞阶段 |
| 内容量 | 12 卡 / 5 敌人 | 135 卡 / 30 敌人 / 28 技能 / 32 词缀 |
| 战斗规模 | 数十敌人，逐个 GameObject | 数千至上万，AOT 数据导向内核 |
| 代码定位 | 独立 demo 场景，不接主流程 | 接入正式 GameApp 流程 |

**照旧文档实现会做出错误的东西。** 尤其注意旧文档里"进化 = 进入下一阶段"的语义已被明确废弃。

## 各文档留存价值

| 文档 | 还有什么用 |
|---|---|
| `Cell_Stage_Demo_Spec.md` | 细胞阶段的初版设计推导过程；卡池与敌人的最初构思 |
| `First_Playable_Spec.md` | 三阶段继承链的设计思路，未来做器官/生物阶段时可回看 |
| `First_Playable_Card_Pool.md` | 卡牌灵感来源，部分条目已并入新规格 |
| `*_Task_Breakdown.md` | 旧任务拆解，仅作工作量参考 |

对应的旧代码 `Assets/GameScripts/HotFix/GameLogic/FirstPlayable/` 与场景
`Assets/Scenes/FirstPlayableDemo.unity` 同样是历史参考，不再维护。
