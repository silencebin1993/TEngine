# FG14 技术架构迁移

> 需求前缀：`FGR-ARC`；测试前缀：`FGT-ARC`。里程碑：FG-M0（技术验证与决策），各里程碑按本文件约束施工。
> 上游：[ProjectA_FullGame_Design.md](../ProjectA_FullGame_Design.md) 第 15 章；契约：[FG00](FG00_Completeness_Addendum.md)。
> 路径均相对 `TEngine/UnityProject/`。

---

## 1. 为什么要先做架构

正式版的四个新要求，Demo 的架构都承接不了：
1. **全面传送带**需要格网自由建造，Demo 只有固定锚点。
2. **远征时家园遭袭**需要家园和远征区同时运行，Demo 出发时会退出家园。
3. **突袭规模**（上百敌人、几十座炮塔）需要逐单位逻辑下沉到 AOT 内核。
4. **40 小时以上的内容量**需要数据驱动和本地化，Demo 的敌人是手写字典，文案是硬编码中文。
5. **程序生成的无限世界**（用户推翻 D-20）需要按种子确定性地生成、按区块流式加载、只保存差异，Demo 的地图全是手工摆放的。

所以 FG-M0 先做技术验证，把决策定下来，再开始铺功能。

---

## 2. 现状事实（2026-09-24 盘点，施工前需复核）

| 领域 | 现状 | 位置 |
|---|---|---|
| 家园建造 | **固定锚点**：每种建筑一个预设坐标，玩家只能在预设空地上解锁建造；不能自由放置；`BuildingRecord.Rotation` 字段存在但没有任何代码写入 | `Regions/HomeValleyLayout.cs`、`Regions/HomeValleyWorkOrders.cs`（TryCreateBuild）、`Campaign/CampaignRecords.cs` |
| 区域运行 | 家园、破碎都市、铸造前哨三个控制器都是纯 C# 类，直接操作共享的 `CampaignState`；`GameRoot.OnUpdate` 每帧无条件更新三者，互斥靠出发事务显式 `Exit()` 保证 | `Stage/GameRoot.cs`、`Campaign/Regions/ExpeditionDepartureService.cs`、`Regions/HomeValleyController.cs` |
| 内核 | `SimWorld`（Burst、SoA）和门面 `SimBridge` 目前只服务旧细胞阶段；家园和远征区**据盘点没有走内核** | `Main/Sim/SimWorld.cs`、`Battle/SimBridge.cs` |
| 全局单例 | `CampaignSession.Current`、`MachineRegistry` 是全局共享状态；每个控制器各自持有 `CameraDirector` 和 `InputRouter` | — |
| 存档 | `CampaignState` 有 `SchemaVersion`、`ContentVersion`；17 个状态域；3 个存档槽；6 个自动存档时机 | `Campaign/CampaignState.cs`、`CampaignSaveService.cs`、`CampaignAutoSaveService.cs`、`CampaignEnums.cs`（SaveReason） |
| 敌人数据 | 手写静态字典，不走 Luban | `Content/EnemyCatalog.cs` |
| Boss | 六态状态机，唯一写入口 `TryTransition` | `Regions/FoundryOutpostCoreBoss.cs` |
| 敌方反制 | 出发时锁定，四态 | `Regions/EnemyAdaptationService.cs`、`Content/AdaptationCatalog.cs` |
| 时间 | `StrategyClock` 支持 0.5x / 1x / 2x 和暂停；没有游戏内时间、昼夜和天气 | `Core/StrategyClock.cs`、`Stage/GameRoot.cs`（IsWorldPaused） |
| UI | 新面板用 UI Toolkit；主菜单是 UGUI；**没有通用 toast 和确认框**；tooltip 只在战斗浮层 | `GameRes/Raw/UI/*.uxml`、`UI/BattleOverlayUIToolkit/` |
| 告警 | 六级严重度枚举，只有"仓满""工作受阻"两级有真实数据源 | `Regions/HomeValleyAlarms.cs` |
| 设置 | UI 缩放、镜头、音量四类、字幕、色盲图标、震屏、闪光；可重绑动作只有 6 个 | `Settings/GameSettings.cs`、`Core/InputBindingSet.cs` |
| 本地化 | **没有**；文案全是硬编码中文 | — |
| 图鉴 | 有注册表和跨局持久化，但内容只覆盖细胞阶段 | `Progression/CodexRegistry.cs` |
| 目标追踪 | 后端有，**没有独立任务面板** | `Campaign/CampaignObjectiveTracker.cs` |
| 反应 | 旧反应引擎是预编译 DLL，看不到配对表；能看到 16 个具名反应和 2 条环境反应；机械反应 2 条 | `Assets/Plugins/ComposeEngine/`、`ReactionFeedbackCatalog.cs`、`EnvironmentReactionCatalog.cs`、`Campaign/Content/MechanicalReactionCatalog.cs` |

---

## 3. 架构决策（FG-M0 用技术验证定稿）

每条决策都写了默认方向。FG-M0 的技术验证 Story 必须产出数据，证明默认方向可行，或者提出替代方案并请用户拍板。

### FGR-ARC-001 家园空间以格网为唯一真相
- 家园地图是一张按种子生成的**无限格网**（FG17），按区块存储（**初值：32 × 32 格**），1 格 = 1 米。远征区域同样是格网表面（FGR-ARC-014）。
- 分层：地形层（可建 / 水源 / 矿脉 / 废墟 / 悬崖）、污染层（0～3 级）、占用层（建筑占地）、传送带层、管线层、迷雾层。
- 建筑占地用矩形格；可以旋转 90°；放置校验在格网上完成。
- **迁移**：Demo 的固定锚点建筑变成开局预置的格网建筑（位置、朝向写在开局布局表里）；`BuildingRecord.Rotation` 正式启用。

### FGR-ARC-002 家园与远征同时运行
- **模拟**和**展示焦点**分离：
  - 模拟：家园永远运行；进行远征时，远征区同时运行。
  - 展示焦点：同一时刻只有一个区域被渲染、接收输入。信号所在的区域就是焦点。
- 改造要点：
  - `CameraDirector` 和 `InputRouter` 改由一个全局的"视图焦点管理器"持有，不再由每个区域控制器各自持有。
  - 全局单例按需改为按区域划分的上下文。
  - 出发事务不再 `Exit()` 家园，改为"切换焦点"。
- 必须满足 FGR-BASE-021：未被观察的区域，模拟结果和被观察时一致。
- 性能：未被渲染的区域不生成表现对象，只跑模拟。

### FGR-ARC-003 战斗逐单位逻辑下沉内核
- 正式版规模（见 FG15）下，逐单位、逐弹体的循环不允许留在热更层（项目架构第 4 条）。
- **默认方向**：把远征区和家园突袭的战斗接入 `SimWorld` 内核，复用它已有的弹道（`JobProjectile`）和基元读法。热更层只下发命令、读取汇总结果。
- 备选：保留纯 C# 控制器并设规模上限。这个方案只在技术验证证明内核接入代价过高时才考虑，并且必须把正式版的规模目标一起下调，由用户拍板。
- 弹道唯一真相仍然在内核，炮塔也走同一条弹道路径。

### FGR-ARC-004 传送带与管线内核
- 新建 AOT 模块，建议路径 `Main/Sim/Logistics/`，用 Burst + Jobs，数据用 SoA 布局。
- 固定步长运行，**初值 20 Hz**。与渲染帧率无关，结果确定。
- 热更层只读三类数据：每座建筑的输入输出汇总、每个物流网络的吞吐统计、渲染缓冲。
- 渲染：传送带上的物品用 GPU 实例化；镜头拉远后切换为流动贴图，不再逐物品绘制。
- 存档：按网络分块序列化。

### FGR-ARC-005 内容数据驱动
- 所有正式版内容进 Luban 表，经 `tools/cell_tables/`（`python tools/cell_tables/gen_all.py`）生成：建筑、配方、物品、流体、研究节点、固件、组件、敌人、突袭编成、事件、掉落池、任务、天气、文本。
- `Content/EnemyCatalog.cs` 的敌人迁移到表里，行为代码按标签和接口解释，不按 ID 逐个硬编码（IC-REQ-012）。
- 遵守 Luban 已知坑：不同模块不能有同名表，子表主键必须唯一。

### FGR-ARC-006 文本键与本地化
- 新建文本表（键 → 简体中文、英文）和运行时查询接口。
- **从 FG-M0 起，所有新增的玩家可见文本都必须走文本键**，禁止硬编码。
- 缺失的键在界面上显示明显标记（例如 `⟦key⟧`），不能静默显示为空。
- Demo 遗留的硬编码文案在 FG-M15 统一迁移。

### FGR-ARC-007 UI 基础件
在 UI Toolkit 下建一套通用组件，FG-M0 完成，后续所有面板复用：
- 通知中心（toast + 历史）
- 确认框
- 悬停提示（UITK 版 tooltip，支持富文本、数值来源展开）
- 右键菜单
- 拖放
- 搜索框
- 标签页
- 虚拟化列表（大数量）
- 图表（折线、柱状）
- 快捷键提示
- 进度条
- 状态图标集（形状 + 颜色，满足色盲要求）

遵守 `.claude/rules/unity-ui-toolkit.md` 和 `Docs/UI/UI_PIPELINE.md`。主菜单等旧 UGUI 链路不迁移。

### FGR-ARC-008 存档 v2
- 提升 `SchemaVersion`，按版本号写逐级升级器。
- 新增状态域：格网、传送带和物品、管线和流体、研究、天气与时间、突袭、事件、任务、常驻规则、信号核、掉落数据包、统计、**世界种子与生成器版本、各表面的区块差异**（只保存被修改过的区块，FGR-GEN-050）。
- **Demo 存档不迁移**到正式版。读到 Demo 存档时给出明确提示，不能崩溃或静默失败。
- 性能目标见 FG15（存档体积、存读时长）。

### FGR-ARC-009 统一游戏时钟
- 一个全局游戏时钟驱动所有时间相关系统：昼夜、天气、静默夜、突袭、研究、生产、解析。
- `StrategyClock` 增加 3x 档。
- 暂停时，所有系统同时停止；倍速时，所有系统同比例加速，结果与 1x 一致。

### FGR-ARC-010 确定性
- 固定步长模拟。
- 随机数按系统分流（掉落、突袭、事件、天气各自一条），种子进存档。
- **世界生成**有独立的随机流，玩法随机流的种子由世界种子派生，但互不影响（FGR-GEN-003）。
- 同一存档、同一输入序列，回放结果一致。

### FGR-ARC-013 世界生成
- 按（种子，生成器版本，世界设置）确定性地生成，与访问顺序无关（FG17）。
- 区块按需生成，计算放在 AOT 内核的工作线程里；主线程只接入结果，不能因生成而卡顿。
- 生成器改动必须提升版本号；旧存档用旧版本生成未修改的区块（FGR-GEN-051）。

### FGR-ARC-014 表面
- 世界由多个"表面"组成：家园、各阵营领地、登陆场；以后可以加"星球"类型（FGR-GEN-010、011）。
- FGR-ARC-002 所说的"区域"就是表面：每个表面独立存储区块、独立模拟；视图焦点在表面之间切换。
- 存档格式必须能在不破坏旧存档的前提下新增表面类型。

### FGR-ARC-011 测试基础设施
- 扩展 ER8 的旅程机器人框架，支持 FG 里程碑的旅程。
- 物流和战斗内核要有不依赖场景的无头测试。
- 性能场景：为 FG15 定义的后期规模搭建固定测试档。
- 沿用现有自检路径：Editor 打开时用影子工程（`tools/unity-validate.sh`），否则用 batchmode。

### FGR-ARC-012 输入上下文
- 输入按上下文划分：战略、建造、接入、界面。同一按键在不同上下文可以有不同功能，但同一上下文内不能冲突。
- `InputBindingSet` 扩展到**所有**玩家动作，全部可重绑，并检测冲突。

---

## 4. FG-M0 技术验证与通过标准

| Story | 验证内容 | 通过标准 |
|---|---|---|
| FG0-ARCH-01 | 家园与远征同时运行；统一游戏时钟底座 | 两个区域同时模拟 30 分钟游戏时间；焦点切换 100 次；未被观察的区域与被观察时结果逐字段一致；两个区域共用统一时钟，暂停和倍速同步 |
| FG0-ARCH-02 | 传送带内核原型 | 15,000 格传送带、30,000 个物品，内核单步耗时 ≤ 2 ms（推荐配置）；热更层每帧开销与物品数无关 |
| FG0-ARCH-03 | 战斗内核接入 | 200 个敌人、80 座炮塔、1,500 个弹体同时存在，帧率 ≥ 60（推荐配置）；与 Demo 战斗规则一致 |
| FG0-ARCH-04 | 格网建造原型 | 放置、旋转、拆除、占地校验跑通；Demo 锚点建筑迁移为开局预置 |
| FG0-ARCH-05 | 世界生成与区块流式加载 | 同一种子按不同访问顺序生成逐格一致；单区块工作线程生成 ≤ 5 ms、主线程接入 ≤ 0.5 ms；快速平移不卡顿；差异存档只保存被修改的区块；生成器版本回归哈希可用 |
| FG0-DATA-01 | 数据驱动与文本键管线 | 一个建筑、一个敌人、一条文本从表到运行时走通；缺失键有可见标记 |
| FG0-UX-01 | UI 基础件 | 12 类组件都有可交互样例，并通过无头布局探针 |
| FG0-SAVE-01 | 存档 v2 骨架 | 新状态域可以存读；Demo 存档给出明确提示 |

每个验证 Story 都要产出一份**决策记录**：结论、数据、放弃的方案、对后续里程碑的影响。放在 `production/design/full-game/adr/` 目录下。

---

## 5. 硬约束汇总

1. 逐物品、逐单位、逐弹体的 O(N) 逻辑只能在 `Main/Sim/`；热更层每帧开销与数量无关。
2. 不用 Entities/ECS，只用 Burst、Jobs、Collections、Mathematics。
3. 玩法留在热更层（GameLogic）；内核只提供计算。热更层唯一碰内核的入口仍是桥接层。
4. 表现层只读取正式计算结果，不另算一套（IC-REQ-010）。
5. 新面板一律 UI Toolkit。
6. 数值和内容入表，文本走文本键。
7. 资源加载用 `UniTask`，经 `GameModule` 访问，成对释放；跨模块通信用 `GameEvent`（沿用工程 CLAUDE.md 的编码红线）。
8. **内容不能依赖固定坐标**：世界是按种子生成的，任何玩法和剧情内容都要通过生成规则或手工锚点定位，并在任意种子下成立（FG00 B25）。
