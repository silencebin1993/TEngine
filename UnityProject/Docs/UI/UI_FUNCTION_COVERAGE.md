# 玩家功能—UI 覆盖契约

> 目标：每一个需要玩家理解、选择、确认、追踪或配置的现有玩法能力，都有清晰的可达 UI；不把开发验证入口伪装成正式玩法。

## 生产流程

| 页面 / 入口 | 玩家目的 | 真实代码入口 | 状态与操作 |
| --- | --- | --- | --- |
| 运行中枢 | 新开一局、继续当前局、查看当前阶段 | `GameRoot.StartCellStage` / `ResumeCellStage` | 新开、继续、阶段状态、资源摘要 |
| 战斗 HUD | 读取生命、进化、阶段、技能与环境 | `CellStageFlow`、`AbilitySystem`、`PhaseTimeline` | 常驻状态与快捷操作 |
| 进化抉择 | 选择或跳过突变 | `DraftService`、`CellStageFlow.PendingOptions` | 三选一、跳过、暂停边界 |
| 商店 / 卡组 / 图鉴 | 买升级、管理构筑、查询发现 | `ShopSystem`、`Deck`、`CodexRegistry` | 购买、刷新、滚动列表、搜索、分类 |
| 器官与基因装配 | 选择载体、装备/卸下器官与基因 | `MetabolicSlicePanel`、`CarrierGeneService`、`StructuralOrganService` | 列表、拖放、当前槽位、反馈 |
| 表型工坊 | 查看蓝图、提交模板版本、查看旧版本个体 | `BlueprintRegistry`、`LineageRegistry`、`HomecomingRetrofitService` | 蓝图进度、器官单选、基因多选、提交前校验、回巢/取消 |
| 萌生腔 | 以已提交模板生成单位 | `GerminationChamberRegistry.Enqueue` | 成本、队列数量、执行反馈 |
| 战术指挥 | 读取选择集、创建/召回数字编队、清空选择 | `SquadCommandSystem` | 编队 1–9、队列计数、最近下达结果、世界点击指引 |
| 暂停 / 放弃 | 暂停、恢复、放弃本局 | `CellStageFlow.SetPaused`、`MarkAbandoned`、`GameRoot.EndRun` | 明确的模态状态与确认路径 |
| 结算 | 查看战绩后重开或返回运行中枢 | `StageOutcome`、`GameRoot.StartCellStage` | 结果、重开、关闭结算 |

## 目前不作为正式入口的代码

| 功能 | 原因 | 处理 |
| --- | --- | --- |
| LookDev 沙盒、压力测试、意识试玩、数值调试 | 仅编辑器 / 开发构建验证 | 仅在开发构建保留，不进入玩家导航 |
| 瞄准接点 `PendingAttackPart` | 代码明确标注为调试输入，尚无正式战斗设计 | 不在生产 UI 中暴露 |
| AI 教义 | 当前只保存标签，未驱动 AI 决策 | 工坊仅标为“记录标签，不改变当前 AI” |
| 器官、生物等后续大阶段 | `StageId` 已预留，但尚未有可运行阶段 | 运行中枢展示路线图和“尚未开放”，不提供无效按钮 |

## 导航与输入规则

- 列表超过当前可视高度必须使用 `ScrollView`；不会靠缩小文字塞下内容。
- 会改变玩法状态的操作必须给出即时成功/失败反馈，失败原因沿用领域模块返回值。
- 运行中枢负责打开各玩法面板；面板互斥时先关闭旧模态，以 `InputRouter` 保持唯一输入所有权。
- 战术面板不遮挡世界命令：单位选择与落点仍发生在战场，面板只管理编组与反馈。
- 旧 IMGUI HUD 仅作为开发构建对照；生产构建由 UI Toolkit 覆盖其玩家功能。
