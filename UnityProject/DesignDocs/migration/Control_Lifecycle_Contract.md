# 控制生命周期契约（M1-06 交付物）

**状态：** 已实现，随 M1-06 一同验收。
**适用范围：** 受控实体的死亡、卸载、暂停、场景卸载重进、存档与读档。
**单一规则真相：** 本文件 + `Main/Sim/SimWorld.cs` 的控制段落。任何其它地方对"控制权归谁"的
推断都是复制品，出现冲突以这里为准。

---

## 1. 不变量

任何时刻，整个世界满足**恰好一个受控实体，或明确为无**：

- `SimWorld.ControlledUnitId` 要么是一个存活友军的稳定身份，要么是 `SimEntityId.None`；
- 快照中 `Alive[i] != 0 && IntentSource[i] == IntentSource.Player` 的单位数 **≤ 1**；
- 不存在"ID 有效但指向死亡/已释放槽位"的中间态——槽位释放与控制权移交在
  `ReleaseSlot` 内同步完成，没有跨帧窗口。

`SimEntityId` 不编码数组槽位，因此槽位复用不会让旧身份指向另一个单位。
**不要**用 `SimConst.PlayerIndex`（索引 0）判断谁是玩家，它只是世界初始化的兼容槽位。

---

## 2. 死亡与卸载：意识回弹

受控实体消失时（战斗死亡、被吞噬、显式 Despawn），内核在 `FallbackControlAfterLoss` 里
按**确定性规则**移交控制权：

1. 候选 = 存活 + 友军阵营（`Player` / `PlayerMinion`）；
2. 距离**失控点**（旧躯体最后位置）不超过 `ControlFallbackRange`；
3. 排序：距离近者优先，距离并列时稳定实体 ID 小者优先。

与 `GetControlCandidates` 用的是同一把尺子，所以"看得到能切过去的人"和"死后能回弹到的人"
永远一致。选不到候选时控制权**明确为 None**，而不是留一个悬空 ID。

`ControlFallbackRange` 由 `SimBridge` 用当前信号范围写入；内核单独实例化（回归测试）时
落到 `SimConst.DefaultControlFallbackRange`。非正值 = 不限距离。

**确定性**：规则里没有任何随机源，同种子、同输入序列下回弹结果可复现——这是固定种子
自动回归成立的前提。

---

## 3. 变更事件：一次变更一次事件

内核不认识热更层的信号系统，所以控制权变更在 `SimWorld` 内排队
（`TryConsumeControlChange`，FIFO，消费即出队），由 `SimBridge` 转成一次
`ControlledUnitChangedSignal`。

**热更层不得自行发布该信号。** 手动切换与死亡回弹共用这一个出口，
"一次合法切换只产生一次事件"才不依赖各调用方自觉。

`ControlChangeReason` 区分四种来源：

| Reason | 含义 | 典型表现 |
|---|---|---|
| `PlayerRequest` | 玩家按键换人 | 「已切换至 #id · 可选 N」 |
| `ControlledDeath` | 受控实体死亡后回弹 | 「意识回弹至 #id」/「意识无处可去」 |
| `ControlledRemoved` | 显式卸载（非战斗死亡） | 「载体消失 · 转入 #id」 |
| `Restored` | 读档 / 重进场景恢复 | 「意识已恢复 · #id」 |

`CurrentUnitId` 可能为 `None`——表示回弹失败、控制权明确为无，此时只有
`FallbackAnchor` 可用。

---

## 4. 回退锚点：唯一真相在内核

`_controlFallbackAnchor` = 最后一个有效受控实体的位置。它在两处刷新：
每次 `GetSnapshot` 时（受控实体还活着）、以及 `ReleaseSlot` 抓取旧躯体位置时
（**必须在把坐标推出场地之前**抓）。

表现层通过 `SimBridge.TryGetPresentationAnchor` 读它，**不要在热更层再维护第二份锚点**——
两份锚点必然漂移，且死亡那一刻热更层已经来不及现算。世界卸载后内核问不到了，
那时才退到 `SimBridge` 的托管侧控制记忆。

---

## 5. 控制可用性：丢失 ≠ 暂不可用

`SimBridge.Availability`（`ControlAvailability`）：

- `Controlled`：有存活且可解析的受控实体；
- `Suspended`：持有恢复记录但目标当前解析不到，仍在宽限期内（默认 1.5s）。
  重进场景后单位尚未 Spawn 完的那几帧就是这个状态；
- `None`：确实没有，且没有待恢复的记录。

UI 必须区分 `Suspended`（「信号重连中…」）与 `None`（「控制目标丢失」），
否则每次重进场景都会闪一次假警报。

---

## 6. 存档：存什么、不存什么

`ControlHandoffState` 同时带两种标识，因为三种断点对身份的要求不同：

| 字段 | 跨帧 | 跨世界重建 | 跨进程读档 |
|---|:--:|:--:|:--:|
| `ControlledUnitId`（`SimEntityId`） | ✅ | ❌ | ❌ |
| `ControlledLogicId`（热更层分配） | ✅ | ✅ | ✅ |
| `FallbackAnchor` | ✅ | ✅ | ✅ |

**`SimEntityId` 绝不落盘。** 它只在同一个 `SimWorld` 实例内有意义，存进存档再读回来
会指向一个毫不相干的单位——那正是"串体"的经典成因。磁盘上只有 `ControlledLogicId` 与锚点。

恢复顺序（`SimBridge.RequestControlRestore`）：

1. 先认 `ControlledUnitId`（同一局内的暂停/重进，精确）；
2. 对不上则按 `ControlledLogicId` 在快照里找（跨世界重建，靠 `SpawnControlAllies` 的
   固定生成顺序保证 LogicId 可复现）；
3. 都对不上则在宽限期内每帧重试（目标可能还没 Spawn 完）；
4. 宽限期用完仍失败：放弃恢复，保留世界自带的默认受控实体，锚点仍保留。

**旧存档 / 损坏存档一律落到 `ControlHandoffState.None`**（`HasRecord == false`），
调用方不需要额外分支——世界自带的默认受控实体就是安全默认值。
`ControlPersistence` 遵循与生涯统计相同的 Reject-to-Safe 纪律：`Load`/`Save` 永不 throw。

---

## 7. 已知的临时装置

- `CellStageFlow.SpawnControlAllies()` 里两名友军的坐标与半径是写死常量，属 M1 固定房间的
  验收脚手架。要变成正式内容时必须挪进配置表——但**生成顺序不能变**，
  `ControlledLogicId` 的跨局可复现性依赖它。
- 信号范围与切换冷却仍是 `SimBridge` 里的可调常量，不是正式信号网络（M1-04 非目标）。
