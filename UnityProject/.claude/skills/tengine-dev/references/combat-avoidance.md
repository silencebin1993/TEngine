# 群体移动 / 避障（DOTS + ORCA）使用说明

> **适用场景**：大规模敌人战斗、群体移动、局部避障、鸟群/阵型移动 | **状态**：避障库已导入，寻路/导航网格待补（见文末）

## 现状总览

| 能力 | 方案 | 状态 |
|---|---|---|
| 大规模战斗底层 | Unity Entities (DOTS/ECS) | ✅ 已导入，未开始用 |
| 局部避障（智能体-智能体、智能体-障碍物） | Nebukam ORCA（RVO2 算法） | ✅ 已导入，未接入项目代码 |
| 阵型系统（编队变化） | 无现成插件 | ❌ 需自建（leader-follower + 槽位偏移表） |
| 鸟群/Boids 行为（分离/对齐/聚合） | 无现成插件 | ❌ 需自建（可与 ORCA 叠加） |
| 全局寻路 / 动态导航网格 | 无 | ❌ 待定，见下方"寻路方案待补"章节 |

**为什么不用 Unity 内置 NavMesh**：项目后续地形是种子噪声程序化生成，内置 NavMesh 重新烘焙成本太高，已被否决（用户明确要求）。

---

## 已导入的包

在 `Packages/manifest.json` 中：

```json
"com.unity.entities": "1.4.8",
"com.nebukam.common": "https://github.com/Nebukam/com.nebukam.common.git",
"com.nebukam.job-assist": "https://github.com/Nebukam/com.nebukam.job-assist.git",
"com.nebukam.orca": "https://github.com/Nebukam/com.nebukam.orca.git",
```

- 都是 Package Manager 管理的外部依赖，实际代码解压在 `Library/PackageCache/`（不进 git，机器本地生成）。
- Nebukam 三个包有依赖顺序：`orca` 依赖 `job-assist` 和 `common`，改动/升级时不要单独删除底层包。
- 官方最低 Unity 版本：common 2019.4，job-assist/orca 2022.1 — 当前项目 Unity 6000.3.17f1，无兼容问题。

## Nebukam ORCA 核心概念

命名空间：`Nebukam.ORCA`（引用 `Nebukam.Common`、`Unity.Mathematics`）。

**核心对象**：

| 类型 | 作用 |
|---|---|
| `ORCA` | 一次避障模拟的容器，`Schedule(deltaTime)` 调度 Job，`TryComplete()` 在 LateUpdate 里非强制拉取结果 |
| `AgentGroup<Agent>` | 智能体集合，每个 `Agent` 实现 `IAgent` |
| `ObstacleGroup` | 静态/动态障碍物集合（用多边形顶点表示，如墙体、地形轮廓） |
| `RaycastGroup` | 模拟内的射线检测（可选，用于视野/命中判断） |

**`IAgent` 关键字段**（每个战斗单位对应一个 Agent）：

```csharp
agent.prefVelocity   // 期望速度（AI 逻辑写这个，比如朝目标移动的向量）
agent.velocity       // 模拟解算后的"实际"无碰撞速度（渲染/位移读这个，不要直接写）
agent.radius         // 智能体间碰撞半径
agent.radiusObst     // 智能体对障碍物的碰撞半径
agent.maxSpeed       // 限速
agent.neighborDist   // 感知范围（多远内的其他 agent 参与避让计算）
agent.maxNeighbors   // 单帧最多考虑多少个邻居（影响性能，密集战斗场景要调低）
agent.layerOccupation / layerIgnore  // ORCALayer 位掩码，分组免碰撞（比如友军之间不避让敌军单独判定）
```

**标准使用流程**（每帧）：

```csharp
// Awake: 建立一次性容器
var agents = new AgentGroup<Agent>();
var obstacles = new ObstacleGroup();
var simulation = new ORCA { agents = agents, staticObstacles = obstacles };

// Update: 每个单位写入期望速度（AI 决策输出）
foreach (var unit in units)
    unit.orcaAgent.prefVelocity = normalize(unit.aiTargetPos - unit.orcaAgent.pos) * unit.moveSpeed;

simulation.Schedule(Time.deltaTime);   // 调度避障 Job（Burst 并行计算所有 agent）

// LateUpdate: 拉取结果并应用到 Transform/ECS 组件
if (simulation.TryComplete())
    foreach (var unit in units)
        unit.transform.position = unit.orcaAgent.pos;  // pos 由 velocity 积分得到

// OnApplicationQuit / 场景卸载:
simulation.DisposeAll();  // 必须清理 Job 分配的 NativeContainer，否则内存泄漏
```

**官方示例代码位置**（供参考，不要直接照抄进项目，是 package 自带 Samples）：
`Library/PackageCache/com.nebukam.orca@.../Samples~/Setup/ORCASetup.cs`
— 通过 Package Manager 的 "Samples" 面板导入到 `Assets/Samples/` 后才能查看/运行。

## 在 TEngine 里怎么接

- ORCA 的 `Agent`/`AgentGroup` 是纯 C# 对象，不是 ECS Component，可以先在 MonoBehaviour 层跑通逻辑，后续要接入 DOTS 战斗系统时，需要把 `agent.velocity` 结果同步进 ECS 的 `LocalTransform`（通过一个桥接 System，每帧把 ORCA 模拟结果写回 Entity）。
- 遵守热更边界：避障相关业务逻辑（AI 决策、编队计算）放 `Assets/GameScripts/HotFix/GameLogic/`；ORCA/Entities 包本身是 Main 侧的 Package 依赖，不算热更范畴。
- 资源释放规则同样适用：`AgentGroup`/`ObstacleGroup`/`ORCA` 都要在场景销毁或战斗结束时调用 `DisposeAll()`/`Dispose()`，否则残留 Job 句柄和 Native 内存。

## 阵型 / Boids（需自建，无现成插件）

- **阵型系统**：推荐 leader-follower 模式——队长走一条路径（AI 输出的 `prefVelocity`），跟随者的 `prefVelocity` 目标点 = 队长位置 + 预定义槽位偏移（相对队长朝向旋转）。槽位表可以按阵型类型（一字/箭头/方阵）配置成不同的 offset 数组，切换阵型只是切换 offset 表，插值过渡即可。
- **Boids（分离/对齐/聚合）**：如果需要更自然的鸟群感，可以在写入 `prefVelocity` 前叠加三条向量（分离用 ORCA 自带的 avoidance 已经部分覆盖，主要还需要自己算对齐+聚合），再和阵型目标向量按权重混合。ORCA 负责保证最终不穿模/不重叠，Boids/阵型逻辑负责"往哪走"。

## 寻路方案待补（当前空缺）

ORCA 只解决**局部避障**（"这一步往哪走不会撞人"），不解决**全局路径规划**（"怎么绕过整片地形障碍走到目标"）。曾评估过 DotsNav（纯 DOTS 动态导航网格，本来是最契合的方案），但其最后一次提交是 2022-08，API 面向 pre-1.0 Entities，与当前 Entities 1.4.8 大概率不兼容，已否决，未导入。

尚未安装任何寻路库。后续若要补齐，倾向的方向（未定案，需要设计后再实现）：
- 自建 Grid / Flow-Field 寻路：契合程序化噪声地形（生成地形时天然产出网格代价数据），ECS/Burst 原生实现，无外部依赖风险。
- 或重新评估届时是否有更新的 DOTS 寻路开源方案。

**AI 后续接手这块任务时**：不要假设寻路已经就位，需要先设计再实现；避障（ORCA）和战斗底层（Entities）已经就位，可以直接用。
