# 文明织造 First Playable 开发任务拆解

## 1. 当前工程状态

当前 Unity 项目已有：
- `Assets/Scenes/main.unity`
- `Assets/GameScripts/HotFix/GameLogic/GameApp.cs`
- TEngine 热更代码目录与基础 UI 框架
- `GDD_Starter_Pack.md`
- `First_Playable_Spec.md`

当前未发现：
- First Playable 专用流程管理器
- 细胞 / 生物阶段场景
- 构筑选择 UI
- 阶段 HUD
- 玩法数据模型
- 敌人、资源、阶段目标等 gameplay 脚本

因此第一批开发目标是先建立最小可运行闭环，而不是直接做完整内容。

---

## 2. 开发总目标

First Playable 要跑通：

`主入口 -> 细胞阶段 -> 器官/组织构筑阶段 -> 生物阶段 -> 结算 -> 返回或重开`

验收重点：
- 前一阶段选择会影响后一阶段。
- 三条路线有可感知差异。
- 三阶段能连续完成，不依赖手动切场景。
- 范围不进入文明、大明、废土、星海、宇宙终局。

---

## 3. 阶段 0：流程骨架

目标：先打通可运行的三阶段流程。

建议新增目录：
- `Assets/GameScripts/HotFix/GameLogic/FirstPlayable/`
- `Assets/GameScripts/HotFix/GameLogic/FirstPlayable/Core/`
- `Assets/GameScripts/HotFix/GameLogic/FirstPlayable/Stage/`
- `Assets/GameScripts/HotFix/GameLogic/FirstPlayable/UI/`

建议新增脚本：
- `FirstPlayableGameMode.cs`
- `FirstPlayableState.cs`
- `FirstPlayableRunData.cs`
- `StageControllerBase.cs`
- `CellStageController.cs`
- `BuildStageController.cs`
- `CreatureStageController.cs`

任务：
- 建立 `FirstPlayableState` 枚举：`None`、`Cell`、`Build`、`Creature`、`Summary`。
- 建立 `FirstPlayableRunData` 保存本局数据：生命值、生物质、进化点、已选模块、路线统计。
- 建立 `FirstPlayableGameMode` 管理阶段切换。
- 从 `GameApp.StartGameLogic()` 进入 First Playable 流程。
- 暂时允许所有阶段在 `main.unity` 内运行，避免早期被场景加载阻塞。

验收：
- 运行项目后能进入 First Playable。
- 能按调试按钮或代码流程依次切到 `Cell -> Build -> Creature -> Summary`。
- 阶段切换不会丢失 `FirstPlayableRunData`。

---

## 4. 阶段 1：数据模型

目标：让构筑、资源、敌人和玩家形态有统一数据来源。

建议新增脚本：
- `EvolutionRoute.cs`
- `EvolutionModuleData.cs`
- `PlayerStats.cs`
- `EnemyStats.cs`
- `StageGoalData.cs`

核心枚举：
- `EvolutionRoute.None`
- `EvolutionRoute.Devour`
- `EvolutionRoute.Specialize`
- `EvolutionRoute.Technology`

首版 6 个构筑模块：
- `吞噬口器`：吞噬扩张型，提高吞噬效率。
- `厚壁细胞层`：吞噬扩张型，提高生命值。
- `感知纤毛`：功能特化型，提高移动/探测。
- `协同神经束`：功能特化型，提高特殊动作效率。
- `原始放电囊`：科技统治型，提供短距离攻击。
- `能量导流组织`：科技统治型，提高资源转化。

任务：
- 先用代码内静态数据定义 6 个模块。
- 每个模块至少包含：`Id`、`Name`、`Route`、`Cost`、`Description`、`StatModifiers`。
- `FirstPlayableRunData` 能记录最多 3 个模块。
- 生物阶段能根据模块计算玩家最终属性。

验收：
- 构筑模块能被读取。
- 选择模块后数据能进入 `RunData`。
- 生物阶段能看到模块带来的属性差异。

---

## 5. 阶段 2：细胞阶段

目标：实现最小移动、吞噬、受伤、资源获取。

建议新增脚本：
- `CellPlayerController.cs`
- `CellEntity.cs`
- `CellFood.cs`
- `CellThreat.cs`
- `CellStageGoal.cs`

最小玩法：
- 玩家控制一个细胞移动。
- 场景中存在可吞噬目标。
- 场景中存在威胁目标。
- 吞噬获得生物质和进化点。
- 接触威胁造成伤害。
- 达到进化点门槛后进入构筑阶段。

任务：
- 实现 2D 或 3D 平面移动。
- 创建玩家细胞白模。
- 创建可吞噬目标白模。
- 创建威胁目标白模。
- 实现触碰检测。
- 实现生命值、生物质、进化点变化。
- 实现阶段胜利和失败判断。

验收：
- 玩家能移动。
- 玩家能吞噬目标并增长资源。
- 玩家会因威胁受伤。
- 生命值归零会失败。
- 达到目标会进入构筑阶段。

---

## 6. 阶段 3：器官/组织构筑阶段

目标：实现 6 选最多 3 的构筑选择，并传入生物阶段。

建议新增 UI：
- `BuildSelectionUI`

建议新增脚本：
- `BuildStageController.cs`
- `BuildSelectionUI.cs`
- `BuildModuleItem.cs`

任务：
- 显示 6 个构筑模块。
- 显示当前进化点和构筑槽位。
- 支持选择 / 取消选择模块。
- 最多选择 3 个模块。
- 进化点不足时不可选择。
- 点击确认后进入生物阶段。

验收：
- 玩家可以完成一次构筑选择。
- 不能超过 3 个模块。
- 模块结果会写入 `RunData`。
- 确认后能进入生物阶段。

---

## 7. 阶段 4：生物阶段

目标：验证前三阶段选择的实战差异。

建议新增脚本：
- `CreaturePlayerController.cs`
- `CreatureEnemy.cs`
- `CreatureEliteEnemy.cs`
- `CreatureAbilityController.cs`
- `CreatureStageGoal.cs`

最小玩法：
- 玩家控制小型生物移动。
- 场景中有 2 种普通敌人。
- 场景中有 1 个精英敌人或最终压力源。
- 玩家属性由构筑模块决定。
- 三条路线在体感上有差异。

路线体现：
- 吞噬扩张型：更高生命值、更强近距离压制。
- 功能特化型：更高移动效率或闪避能力。
- 科技统治型：短距离攻击或放电能力。

任务：
- 实现生物玩家单位。
- 实现普通敌人追踪或巡逻。
- 实现敌人接触伤害。
- 实现精英敌人。
- 将构筑模块转换为玩家属性。
- 实现至少一个路线能力。
- 实现胜利 / 失败结算。

验收：
- 生物阶段能完整游玩。
- 不同构筑路线的玩家属性或能力不同。
- 击败精英敌人或完成目标后进入总结。
- 失败后可以重新开始。

---

## 8. 阶段 5：UI 与反馈

目标：最小可用，不追求正式美术。

需要 UI：
- `FirstPlayableHUD`
- `BuildSelectionUI`
- `StageResultUI`
- `RunSummaryUI`

HUD 必须显示：
- 当前阶段
- 阶段目标
- 生命值
- 生物质
- 进化点
- 已选路线倾向

结算必须显示：
- 阶段是否成功
- 获得资源
- 已选模块
- 当前路线倾向
- 下一步按钮

任务：
- 先用简单 UGUI 文本和按钮实现。
- 节点命名遵守现有 UI 绑定风格。
- 所有按钮必须能正常绑定事件。

验收：
- 玩家不看控制台也能知道当前目标。
- 构筑选择和阶段结算可正常操作。
- 失败、成功、重开都有清楚入口。

---

## 9. 阶段 6：场景与资源

目标：先用白模，不做正式美术。

建议场景策略：
- 第一版继续使用 `main.unity` 承载全部阶段。
- 通过不同区域或动态生成对象模拟阶段切换。
- 等流程稳定后，再拆出 `CellStageScene`、`CreatureStageScene`。

需要对象：
- 玩家细胞
- 食物细胞
- 威胁细胞
- 生物玩家
- 普通敌人 A
- 普通敌人 B
- 精英敌人
- 阶段边界 / 目标点

验收：
- 所有对象可用基础几何体代替。
- 颜色区分清晰。
- 玩法对象不依赖正式 prefab 美术。

---

## 10. 开发顺序

推荐顺序：
1. 建立 `FirstPlayableRunData` 和阶段状态机。
2. 从 `GameApp` 进入 First Playable。
3. 实现细胞阶段移动、吞噬、受伤、胜负。
4. 实现构筑数据和构筑选择 UI。
5. 实现生物阶段玩家、敌人、构筑属性继承。
6. 实现 HUD、阶段结算、失败重开。
7. 补路线差异和精英敌人。
8. 做一次完整验收。

不要先做：
- 正式美术。
- 完整配置表。
- 文明 RTS。
- 多场景资源加载。
- 复杂 AI。
- 完整存档。

---

## 11. 首批代码任务

第一批最小任务：
- 新建 `FirstPlayableRunData`。
- 新建 `FirstPlayableGameMode`。
- 修改 `GameApp.StartGameLogic()` 启动 First Playable。
- 实现 `CellStageController`。
- 实现 `CellPlayerController`。
- 实现简单 HUD。

第一批验收：
- 进入游戏后能看到细胞阶段。
- 玩家能移动。
- 玩家能吞噬目标获得进化点。
- 玩家能受伤并失败。
- 达到目标后能进入下一阶段占位界面。

---

## 12. 代码前最后确认

开始写代码前只需要确认三件事：
- 细胞阶段使用 2D 平面还是 3D 平面。
- 输入先使用键鼠 WASD，还是鼠标点击移动。
- 第一版是否继续全部放在 `main.unity` 内运行。

默认建议：
- 3D 平面白模。
- WASD 移动。
- 第一版全部放在 `main.unity` 内，等流程稳定后再拆场景。
