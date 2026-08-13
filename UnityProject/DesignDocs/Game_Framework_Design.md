# 《文明织造》正式游戏框架设计

> 版本：v1.0
> 创建日期：2026-08-04
> 配套文档：`Cell_Stage_Spec.md`（细胞阶段规格）
> 适用范围：全项目八阶段。本文档定义的框架必须在不改动内核的前提下承载后续所有阶段。
>
> **权威补丁（2026-08-11）**：§5.8 反应/催化 **Superseded**。组合核心见 `DesignDocs/最新改动需求/`（ComposeEngine 独立库，零 Unity）。  
> 短读冲突表：`最新改动需求/AUTHORITY_AND_CONFLICTS.md`。热更边界 / AOT Sim / 模块框架等其它章仍有效。

---

## 1. 设计目标

1. **新增功能不改老代码**。新卡牌/敌人/技能/阶段一律通过"加配置 + 加数据驱动的小模块"实现。
2. **大范围敌人**。战斗内核走数据导向（Burst + Jobs + 原生容器），目标 10000+ 单位稳定 60 FPS。
3. **多阶段适配**。细胞、生物、文明、星海阶段共用同一套内核与模块框架，差异体现在数据与表现层。
4. **符合 TEngine 规范**。热更边界、资源释放、事件解耦、模块访问全部遵守项目既有红线。

---

## 2. 最重要的架构约束：DOTS 与 HybridCLR 的边界

这是整个框架的形状决定因素，必须先讲清楚。

### 2.1 三条硬约束

| 约束 | 后果 |
|---|---|
| **Burst 是 AOT 编译的**。它在构建期扫描程序集并生成原生代码。 | HybridCLR 在运行时加载的热更 DLL **永远不会被 Burst 编译**，只能走解释执行（慢 10-20 倍）。所以**性能内核必须在 AOT 程序集里**。 |
| **本工程是 Built-in RP**（已核实 `GraphicsSettings.m_CustomRenderPipeline: {fileID: 0}`，且未安装 `com.unity.entities.graphics`）。 | Unity 官方 **Entities Graphics 只支持 URP/HDRP**，在 Built-in RP 下不可用。即使引入完整 ECS，实体渲染也得自己写。 |
| **热更是项目既定策略**（`hotUpdateAssemblies: [GameProto, GameLogic]`）。 | 玩法内容必须留在热更层，否则失去热更能力。 |

### 2.2 结论：DOTS 取 Burst + Jobs + Collections + Mathematics，不引入 Entities 框架

DOTS 是四件套：**Burst、Jobs、Collections、Mathematics、Entities(ECS)**。本项目采用前四件，**有意不使用 Entities/ECS 框架**，理由：

- ECS 最大的性能红利来自 **Entities Graphics 的批量渲染**，而它在 Built-in RP 下不可用 —— 红利直接消失。
- ECS 的 `ISystem`/`IJobEntity` 依赖源生成器 + `TypeManager` 类型注册，与 HybridCLR 热更类型系统组合是已知的高风险区。
- 战斗内核的组件集合是**固定已知**的，用 SoA（Structure of Arrays）`NativeArray` 比 archetype chunk **更简单也更快**，没有结构变化开销。
- 项目已有先例：`Nebukam.ORCA` 就是纯 Burst + Jobs 方案，已在 FP demo 中跑通。

**性能核算**：SoA + Burst + 空间哈希 + `Graphics.RenderMeshInstanced`，在中端 PC 上 10k-30k 单位 60 FPS 是常规水平，远超 `Cell_Stage_Spec.md` §3 要求的 10000 上限。对比参考：《吸血鬼幸存者》同屏峰值约 1-2k。

**可替换性**：内核通过 `ISimBackend` 接口暴露（见 §4.5）。若将来迁移 URP，可新增一个 `EntitiesSimBackend` 实现替换，上层玩法代码零改动。这也是"为多阶段适配"的一部分。

### 2.3 分层结果

```
┌──────────────────────────────────────────────────────────────┐
│ HotFix 层（GameLogic，可热更，HybridCLR 解释执行）              │
│ ─ 阶段流程 / 卡牌构筑 / 能力效果 / 成长 / 刷怪导演 / 事件 / UI    │
│ ─ 每帧只做"决策"与"下命令"，不做逐单位循环                       │
└───────────────────────────┬──────────────────────────────────┘
                            │ SimBridge（命令缓冲 + 只读快照）
┌───────────────────────────▼──────────────────────────────────┐
│ AOT 层（BinGames.Sim，不热更，Burst 编译）                      │
│ ─ SoA 单位数据 / 空间哈希 / 移动 / 转向 / 伤害 / 投射物 / 渲染     │
│ ─ 通用"agent 模拟器"，不认识"细胞"，只认识数据                   │
└──────────────────────────────────────────────────────────────┘
```

关键纪律：**热更层每帧的工作量与敌人数量无关**（O(1) 或 O(玩家卡牌数)），逐单位的 O(N) 全部在 AOT 层。这样解释执行的性能劣势不会成为瓶颈。

---

## 3. 目录与程序集布局

```
Assets/GameScripts/
├── Main/                            # AOT，不热更
│   └── Sim/                         # ★ 新增：BinGames.Sim.asmdef
│       ├── SimWorld.cs              #   世界容器，持有所有 NativeArray
│       ├── SimTypes.cs              #   共享结构体、枚举、常量
│       ├── SimCommandBuffer.cs      #   热更层 → 内核 的命令队列
│       ├── SimSnapshot.cs           #   内核 → 热更层 的只读视图
│       ├── SpatialHash.cs           #   Burst 友好均匀网格
│       ├── Jobs/
│       │   ├── JobBuildHash.cs
│       │   ├── JobSteering.cs       #   行为原型驱动的转向
│       │   ├── JobIntegrate.cs      #   位移积分 + 边界
│       │   ├── JobSeparation.cs     #   轻量分离（替代完整 ORCA）
│       │   ├── JobDamage.cs         #   范围/单体/连锁伤害求解
│       │   ├── JobProjectile.cs
│       │   ├── JobStatus.cs         #   状态效果 tick
│       │   └── JobQuery.cs          #   最近目标 / 范围查询
│       ├── SimRenderer.cs           #   GPU 实例化渲染
│       └── ISimBackend.cs           #   后端抽象（预留 ECS 替换）
│
└── HotFix/GameLogic/                # 热更
    ├── Core/                        # ★ 新增：框架基础设施
    │   ├── IGameModule.cs           #   模块接口 + 生命周期
    │   ├── ModuleHub.cs             #   模块注册/解析/更新
    │   ├── GameFsm.cs               #   通用状态机
    │   ├── Signals.cs               #   类型安全局内信号总线
    │   ├── ObjectPoolLite.cs
    │   ├── DataRegistry.cs          #   Luban 表统一门面
    │   └── ServiceRef.cs            #   延迟解析引用
    │
    ├── Battle/                      # ★ 新增：战斗层（内核之上的玩法封装）
    │   ├── BattleContext.cs         #   一局战斗的根上下文
    │   ├── SimBridge.cs             #   AOT 内核的热更侧门面
    │   ├── UnitRegistry.cs          #   逻辑单位 ↔ 内核索引 映射
    │   ├── DamagePipeline.cs        #   伤害计算管线（可插拔修正器）
    │   ├── StatusSystem.cs          #   状态效果（导电/破体/标记...）
    │   └── TargetingService.cs      #   目标选择策略
    │
    ├── Stats/                       # ★ 新增：属性系统
    │   ├── StatId.cs
    │   ├── StatSheet.cs             #   属性容器 + 脏标记重算
    │   └── StatModifier.cs          #   Flat/PctAdd/PctMul 三层
    │
    ├── Ability/                     # ★ 新增：能力/效果系统
    │   ├── AbilitySpec.cs           #   数据定义
    │   ├── AbilityRuntime.cs        #   实例状态（冷却/充能）
    │   ├── AbilitySystem.cs         #   槽位管理 + 施放
    │   ├── EffectSpec.cs            #   效果数据
    │   ├── IEffectExecutor.cs       #   效果执行器接口
    │   └── Executors/               #   ★ 新增效果 = 新增一个文件
    │       ├── EffectDealDamage.cs
    │       ├── EffectApplyStatus.cs
    │       ├── EffectSpawn.cs
    │       ├── EffectModifyStat.cs
    │       ├── EffectResource.cs
    │       ├── EffectDash.cs
    │       ├── EffectChain.cs
    │       ├── EffectArea.cs
    │       └── EffectRule.cs        #   改规则（吞噬阈值等）
    │
    ├── Cards/                       # ★ 新增：卡牌构筑
    │   ├── CardSpec.cs
    │   ├── Deck.cs                  #   已获得卡 + 层数
    │   ├── CardTriggerBus.cs        #   触发器 → 效果 派发
    │   ├── DraftService.cs          #   抽卡权重 + 保底
    │   └── CardEffectBinder.cs      #   卡牌 → 能力/属性/规则 绑定
    │
    ├── Progression/                 # ★ 新增：成长系统
    │   ├── LevelCurve.cs
    │   ├── ProgressionModule.cs     #   进化能/升级/选卡触发
    │   ├── ResourceWallet.cs        #   营养质/突变质/污染度
    │   └── RuleFlags.cs             #   规则开关集合
    │
    ├── Spawning/                    # ★ 新增：刷怪
    │   ├── SpawnDirector.cs         #   压力预算导演
    │   ├── PressureBudget.cs
    │   ├── SpawnPatterns.cs         #   环形/边缘/成群/精英
    │   └── EcoEventScheduler.cs     #   生态事件调度
    │
    ├── Stage/                       # ★ 新增：阶段流程
    │   ├── IStageFlow.cs
    │   ├── StageOutcome.cs          #   跨阶段继承载荷
    │   ├── StageDirector.cs         #   阶段注册与切换
    │   ├── PhaseTimeline.cs         #   阶段内时期推进
    │   └── CellStage/               #   ★ 细胞阶段实现（多阶段样板）
    │       ├── CellStageFlow.cs
    │       ├── CellPlayerController.cs
    │       ├── CellDevourSystem.cs  #   吞噬判定（本阶段核心动词）
    │       └── CellStageConfig.cs
    │
    └── UI/                          # 沿用 TEngine UIWindow 体系
        ├── Battle/BattleHudWindow.cs
        ├── Draft/EvolutionDraftWindow.cs
        ├── Result/StageResultWindow.cs
        └── Common/                  #   可复用 UI 组件
```

命名与放置遵循 `TEngine/UnityProject/CLAUDE.md`：模块按领域分目录，文件 200-400 行为宜、上限 800 行。

---

## 4. AOT 模拟内核（BinGames.Sim）

### 4.1 数据布局：SoA

```csharp
// 单位数据按字段分数组，Burst 与缓存友好
NativeArray<float2>  Position;
NativeArray<float2>  Velocity;
NativeArray<float2>  DesiredDir;
NativeArray<float>   Health;
NativeArray<float>   Radius;      // 兼作"体积"
NativeArray<float>   MaxSpeed;
NativeArray<int>     ArchetypeId; // 指向行为原型
NativeArray<uint>    StatusMask;  // 位掩码：导电/破体/标记/减速...
NativeArray<byte>    Faction;
NativeArray<byte>    Alive;
```

容量固定预分配（默认 16384，可配），用**自由列表**复用槽位，运行期零 GC。

### 4.2 空间哈希

均匀网格，cell 大小 = 最大交互半径。`NativeParallelMultiHashMap<int, int>` 每帧重建（并行 job，10k 单位约 0.1ms）。所有邻域查询（吞噬判定、伤害范围、分离、电弧连锁）走它，避免 O(N²)。

### 4.3 行为原型（Archetype）

敌人 AI 不写成"每种敌人一个类"，而是**参数化行为原型**：

```csharp
struct BehaviorArchetype {
    BehaviorKind Kind;      // Drift / Chase / Patrol / Charge / Ranged / Stationary / Swarm / Flee
    float   Accel, TurnRate, AggroRange, AttackRange, AttackCd, Separation;
    float   OrbitRadius;    // 风筝/环绕型用
    int     ChargeTelegraphMs;
    // ... 全部来自 Luban 配置
}
```

20 种敌人 = 8 个原型 × 参数差异。**新增敌人是配表工作**，不需要新代码。这直接服务"新增功能不改老代码"。

### 4.4 帧管线

```
热更层 Update
  ├─ 1. 玩家输入 / 能力施放 / 卡牌触发（O(卡牌数)）
  ├─ 2. 导演决策：本帧要生成什么（O(1)）
  ├─ 3. 写 SimCommandBuffer（生成/伤害/状态/移除）
  └─ 4. SimWorld.Step(dt)  ──► AOT，一次性调度整条 job 链
                                ├─ ApplyCommands
                                ├─ BuildSpatialHash
                                ├─ Steering        (并行)
                                ├─ Separation      (并行)
                                ├─ Integrate       (并行)
                                ├─ Projectile      (并行)
                                ├─ Damage 求解      (并行 + 原子累加)
                                ├─ Status tick     (并行)
                                └─ Compact/Death 收集 → 事件回写
  └─ 5. 读 SimSnapshot（只读，用于 UI/VFX/吞噬判定）
  └─ 6. SimRenderer.Draw()   ──► GPU 实例化
```

命令缓冲 + 快照的双向隔离，使热更层**永远不直接触碰 NativeArray**，规避 HybridCLR 处理泛型原生容器的风险。

### 4.5 后端抽象

```csharp
public interface ISimBackend {
    void  Initialize(SimConfig cfg);
    void  Step(float dt, SimCommandBuffer cmds);
    SimSnapshot GetSnapshot();
    void  Dispose();
}
```

当前实现 `BurstSimBackend`。预留 `EntitiesSimBackend`（迁 URP 后）与 `NullSimBackend`（单元测试用）。

### 4.6 渲染

`Graphics.RenderMeshInstanced`（每批 1023 个）+ 按 `ArchetypeId` 分批。大规模时切 `RenderMeshIndirect` + `ComputeBuffer`，实例数据由 job 直接写入，零 CPU 拷贝。Built-in RP 完全支持。

---

## 5. 热更层框架

### 5.1 模块框架

```csharp
public interface IGameModule {
    int   Priority { get; }
    void  OnInit(ModuleHub hub);
    void  OnEnter();
    void  OnUpdate(float dt);
    void  OnExit();
    void  OnDispose();
}
```

`ModuleHub` 负责注册、按 `Priority` 排序更新、按类型解析。模块间**不直接持引用**，通过 `ServiceRef<T>` 延迟解析或 `Signals` 通信。新增系统 = 注册一个新模块，老模块不动。

### 5.2 信号总线（局内）

```csharp
Signals.Subscribe<DevourEvent>(OnDevour);
Signals.Publish(new DevourEvent { TargetId = id, Volume = v });
```

结构体信号 + 泛型订阅，零装箱。**局内**用 `Signals`；**跨模块持久**用 TEngine `GameEvent`；**UI 内部**用 `AddUIEvent`。三者边界明确，避免事件风暴。

局末 `Signals.Clear()` 强制清空，规避 TEngine 事件泄漏的经典坑。

### 5.3 属性系统

三层修正器，顺序固定，避免乘法爆炸：

```
final = (base + Σflat) * (1 + ΣpctAdd) * Π(1 + pctMul)
```

- `Flat` — 加值（+20 生命）
- `PctAdd` — 加法百分比（+15% 移速，同类相加）
- `PctMul` — 乘法百分比（稀有卡专用，独立相乘）

`StatSheet` 用脏标记，只在修正器变动时重算，不每帧算。

### 5.4 能力 / 效果系统

**能力**（Ability）= 玩家可施放的动作。**效果**（Effect）= 可组合的原子结果。

```csharp
AbilitySpec {
  Id, Name, Cooldown, Charges, CastRange, TargetMode
  Effects[]        // 有序效果列表
  Tags[]           // 用于卡牌"强化所有电系技能"这类修正
}

EffectSpec {
  Kind             // Damage / Status / Spawn / Stat / Resource / Dash / Chain / Area / Rule
  Params[]         // 数值参数（float/int 数组）
  Shape            // Point / Circle / Cone / Line / Self
  Affixes[]        // 词缀，改写执行细节
}
```

执行器按 `Kind` 注册到字典。**新增一种效果 = 新增一个 `IEffectExecutor` 文件 + 注册一行**，不改任何现有执行器。这是"不频繁改老代码"的主要落点。

词缀（Spec §8.4，**已作废，历史目标**）曾实现为**执行器的装饰器**：`导电` 词缀包裹 `EffectDealDamage`，在其执行后追加施加导电状态。32 个词缀 × 9 类效果 = 288 种组合的目标随词缀战斗卡整体 Delist（story-005，2026-08-12 前）一并作废；装饰器机制本身作为通用能力保留，但当前 28 张代谢卡的 `Affixes[]` 恒为空，组合深度改由 ComposeEngine 正交叠乘承担。

### 5.5 卡牌系统

```csharp
CardSpec {
  Id, Name, Route, Rarity, UnlockPhase, MaxStack
  Triggers[]       // OnDevour/OnKill/OnHit/OnDash/OnTick/OnLevelUp/OnZap/OnLowHp/OnPhaseStart...
  Effects[]        // 复用 EffectSpec
  StatMods[]       // 复用 StatModifier
  GrantAbility     // 可选：授予主动技能
  RuleFlags[]      // 可选：改规则
  Affixes[]
  SynergyTags[]
  Cost { Pollution, DrawbackSpec }
}
```

`CardTriggerBus` 订阅 `Signals`，把游戏事件路由到卡牌效果。获得卡牌时**一次性注册**，不每帧遍历卡组。

`DraftService` 实现 Spec §8.5 的权重公式（路线亲和、联动加成、保底、低血保底）。

### 5.6 刷怪导演

```csharp
PressureBudget {
  float Current;                    // 当前允许的压力值
  float Compute(float t, PlayerPower p);
}
SpawnDirector {
  // 从当前时期可用敌人池按 cost 采购，直到填满预算
  // 采购策略：加权随机 + 生态多样性约束（避免单一敌人刷屏）
}
```

`playerPowerFactor` 只影响预算的**上浮部分**，下限随时间硬性抬升 —— 防止玩家故意压制 build 换低难度（Spec §16 风险项）。

### 5.7 阶段流程

```csharp
public interface IStageFlow {
    StageId Id { get; }
    void          Enter(StageOutcome inherited);   // 接收上一阶段产物
    void          Update(float dt);
    StageOutcome  Exit();                          // 产出本阶段结果
}
```

`StageDirector` 只认 `IStageFlow`。细胞阶段是第一个实现；后续器官/生物/文明阶段各自实现同一接口，**StageDirector 不需要改**。

`PhaseTimeline` 管理阶段内的时期推进（细胞阶段的 6 个生态时期），也是数据驱动的：时期定义在配置表里，包含时长、卡池解锁、敌人池、事件池、压力曲线。

### 5.8 ComposeEngine 接入 / MetabolicSlice 战斗桥接

> **权威叙述（2026-08-11 更新）**：本节已从"计划中的 `ReactionSystem`/`CatalystRegistry`"设计态改写为**真实已落地架构**，引用真实类名，因为对应代码已存在可核实。  
> 对应玩法节 `Cell_Stage_Spec.md` §17（同样已改写）；实现细节权威仍是 `DesignDocs/最新改动需求/组合引擎-ClaudeCode需求规格.md`，本节只描述接入方式。

**核心引擎（零 Unity）**：独立库 `ComposeEngine`（`ComposeEngine/src/ComposeEngine/`，`netstandard2.1`，命名空间 `ComposeEngine.*`），承载 Packet/HitEvent/RuleVector 装配管道与求解器（`Engine`）。核心库不引用 Unity 引擎包，可脱离 Unity 独立构建与测试；游戏侧只拷贝源码或引用 DLL（`Assets/Plugins/ComposeEngine/`），**不得**把游戏桥接代码编进核心 DLL。

**游戏侧桥接**：

```csharp
public sealed class MetabolicSliceBridge : GameModuleBase
{
    public override int Priority => ModulePriority.MetabolicBridge;

    private Engine _engine;
    private SlotGrid _grid;
    private MetabolicSliceRunner _runner;
    private SimBridge _sim;

    public override void OnEnter()
    {
        _engine = new Engine();
        ReactionCatalog.RegisterDefaults(_engine);
        _grid = new SlotGrid(SlotType.Cytoplasm);
        _runner = new MetabolicSliceRunner(_engine);
    }

    public override void OnUpdate(float dt)
    {
        // 按固定间隔 Tick，产出 HitEvent[]，消费 Damage 转发到 Sim 层
        var events = _runner.Tick(_grid, Array.Empty<IContract>(), new WorldState(), seed);
        foreach (var evt in events)
        {
            if (evt.Damage > 0f)
                _sim.DamageArea(_sim.PlayerPosition, radius, evt.Damage, SimFaction.Hostile);
        }
    }
}
```

（完整实现见 `GameLogic/MetabolicSlice/Combat/MetabolicSliceBridge.cs`；上方为接入方式摘要，非逐行照抄。）

- **挂载点**：`CellStageFlow.RegisterModules()` 里 `_hub.Register(new MetabolicSliceBridge())`；`Priority = ModulePriority.MetabolicBridge`（旧 `ModulePriority.Reaction` 已随删除的 `ReactionSystem` 一并移除）。
- **出口消费路径**：ComposeEngine 求解产出 `HitEvent` 列表 → 桥接层读取 `HitEvent.Damage` → `SimBridge.DamageArea(...)`。`Heal`/`Shield`/`Displace` 等其余字段留后续 story 消费，接口已预留。
- **游戏侧包装**：`GameLogic/MetabolicSlice/`（Grid/Bag/Transfer/Crafting/Graph/Combat/Environment/Digestion/DebugTools），承载切片、储备囊、复玩三轴（环境残留/背包合成/捕食消化）等玩法逻辑；判定核心全部在 `ComposeEngine` 内，桥接层只做"读 HitEvent → 转伤害"的薄适配。
- **已删除的旧半成品**：`GameLogic/Battle/ReactionSystem.cs`、`ReactionRuleSpec.cs`（及联动的 `CatalystRegistry` 设计态、`ReactionEvent`/`OnReaction` 信号分支）已在 story-006 整删，不留兼容层；旧机制关键词（`Status×Status`/`CatalystRegistry`/`ReactionEvent`）不应再作为实现指引出现。
- **性能与边界不变**：全部在热更层、事件驱动/固定间隔 Tick，符合 §5.6 的"热更层每帧与敌人数无关"；判定不下沉 `Main/Sim`，`Main/Sim` 只提供纯数据位与 `DamageArea` 等既有接口。
- **Luban 表规划变更**：曾规划过的 `cell.ReactionRule`/`cell.CatalystRule` 两张表从未生成，新架构改用代码内目录 `MetabolicSlice/ContentCatalog/` 硬编码内容，不走 Luban 这条路（§6 表格已整行删除这两张表）。

---

## 6. 数据驱动（Luban）

所有内容进配置表。新增表清单：

| 表 | 内容 | 行数目标 |
|---|---|---:|
| `cell.Card` | 卡牌定义 | 135 |
| `cell.CardEffect` | 卡牌效果条目（一卡多效果） | ~350 |
| `cell.Ability` | 主动技能 | 28 |
| `cell.AbilityEffect` | 技能效果条目 | ~90 |
| `cell.Affix` | 词缀 | 32 |
| `cell.Enemy` | 敌人 | 30 |
| `cell.BehaviorArchetype` | 行为原型 | 8 |
| `cell.Phase` | 生态时期 | 6 |
| `cell.EcoEvent` | 生态事件 | 16 |
| `cell.Boss` | 首领阶段 | 3 |
| `cell.LevelCurve` | 升级曲线 | 40 |
| `cell.PressureCurve` | 压力预算曲线 | 24 |
| `cell.StatusEffect` | 状态效果 | 24 |
| `cell.Global` | 全局常量 | 1 |
| `cell.Text` | 文案（可本地化） | ~400 |

原 `fp.*` 表保留但不再扩展，标记为 legacy。

> **行数目标已过时（2026-08-12 更新）**：`cell.Card`（135）/ `cell.CardEffect`（~350）/ `cell.Affix`（32）三行是词缀战斗卡时代的原始 MVP 目标，随 story-005 表格全改（D3b）作废。当前 `cell.Card` 实际 28 行（17 器官 + 11 基因，来自 `OrganelleCatalog`/`GeneCatalog`），无 `CardEffect` 行（新卡效果走 `contentKind`/`contentId` 指向代谢目录，不走旧 `EffectSpec` 触发器体系），`cell.Affix` 表结构保留但无生效内容。其余表（`Enemy`/`Phase`/`EcoEvent`/…）不受影响。

---

## 7. 多阶段扩展点

| 扩展需求 | 做法 | 是否改老代码 |
|---|---|---|
| 新增一张卡 | 加 `cell.Card` + `cell.CardEffect` 行 | ❌ |
| 新增一种敌人 | 加 `cell.Enemy` 行，复用行为原型 | ❌ |
| 新增一种行为原型 | 加 `BehaviorKind` 枚举 + `JobSteering` 一个 case | ⚠️ AOT 一处 |
| 新增一种效果 | 加 `IEffectExecutor` 实现 + 注册一行 | ❌ |
| 新增一个词缀 | 加 `cell.Affix` 行（组合已有装饰器）或加一个装饰器 | ❌ |
| 新增一个生态时期 | 加 `cell.Phase` 行 | ❌ |
| 新增一个主动技能 | 加 `cell.Ability` + `AbilityEffect` 行 | ❌ |
| **新增一个游戏大阶段** | 实现 `IStageFlow` + 该阶段的配置表 + 表现层 | ❌ 框架不动 |
| 切换到 URP + ECS | 新增 `EntitiesSimBackend` 实现 `ISimBackend` | ❌ 玩法层不动 |
| RTS 阶段的编队/寻路 | 内核加 job + 新 `BehaviorKind`；SoA 加字段 | ⚠️ AOT 扩展 |

唯一需要碰 AOT 内核的场景是"新增底层行为/新增单位字段"，这符合预期：内核是**通用 agent 模拟器**，它的扩展频率应远低于内容扩展。

---

## 8. 接入正式流程

细胞阶段从"独立 demo 场景"改为接入正式流程：

```
GameEntry.Awake
  → Procedure 链（Launch → Splash → InitPackage → InitResources
                  → CreateDownloader → DownloadFile → DownloadOver
                  → ClearCache → LoadAssembly → StartGame）
  → GameApp.Entrance                       ← 热更入口
      → ModuleHub 注册全部模块
      → StageDirector 注册所有 IStageFlow
      → 打开主菜单 UI
      → 玩家点"开始漂流"
      → StageDirector.Enter(StageId.Cell)
          → CellStageFlow.Enter
              → SimWorld 初始化（AOT）
              → BattleHudWindow 打开
              → PhaseTimeline 启动
```

原 `FirstPlayableDemo.unity` 与 `FirstPlayable/` 代码转入 `Archive/`，作为白模验证的历史参考保留，不再维护。

---

## 9. 性能预算

| 项 | 预算 | 措施 |
|---|---:|---|
| 内核 Step（10k 单位） | ≤ 4 ms | Burst + 并行 job + 空间哈希 |
| 渲染（10k 单位） | ≤ 3 ms | GPU 实例化，按原型分批 |
| 热更层逻辑 | ≤ 3 ms | O(1)/O(卡牌数)，绝不遍历单位 |
| GC 分配（稳定期） | 0 B/帧 | 预分配 + 对象池 + 结构体信号 |
| 内存（单位数据） | ≤ 8 MB | SoA，16384 容量 |

验证方式：Unity Profiler + 自动化压力测试场景（1k/5k/10k/20k 单位挡位）。

---

## 10. 风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| HybridCLR 解释执行拖慢热更层 | 帧率 | 逐单位循环全部下沉 AOT；热更层保持 O(1) |
| 热更层无法定义 Burst job | 新玩法受限 | 内核做"通用参数化模拟器"，玩法通过参数与命令表达 |
| Built-in RP 无 ECS 渲染 | 规模上限 | 自写实例化渲染；已核算满足 10k+ 需求 |
| AOT 泛型缺失导致运行时报错 | 崩溃 | 桥接接口只用具体类型；`AOTGenericReferences.cs` 补泛型实例 |
| 内容表膨胀后配表易错 | 数据 bug | Luban schema 强类型 + 生成期校验 + 运行时表自检 |
| 内核与热更层数据不同步 | 逻辑错乱 | 单向数据流：命令进、快照出，禁止双向可写引用 |

---

## 11. 实施顺序

1. **AOT 内核骨架** — `BinGames.Sim` 程序集 + SoA + 空间哈希 + 基础 job 链 + 实例化渲染
2. **Core 框架** — ModuleHub / Signals / StatSheet / DataRegistry
3. **战斗层** — SimBridge / DamagePipeline / StatusSystem
4. **能力效果系统** — AbilitySystem + 9 类执行器 + 词缀装饰器
5. **卡牌系统** — CardSpec / TriggerBus / DraftService
6. **成长与刷怪** — Progression / SpawnDirector / EcoEventScheduler
7. **阶段流程** — StageDirector / PhaseTimeline / CellStageFlow
8. **配置表** — Luban schema + 内容数据生成
9. **UI** — HUD / 进化选择 / 结算
10. **接入正式流程 + 压力测试**
