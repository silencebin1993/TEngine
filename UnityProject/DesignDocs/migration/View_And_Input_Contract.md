# 视角与输入所有权契约（M2-01 交付物）

**状态：** 已实现，随 M2-01 一同验收。
**适用范围：** 镜头状态、输入归属、键位冲突仲裁。
**单一规则真相：** 本文件 + `Core/InputRouter.cs` + `View/CameraDirector.cs`。
任何在别处自行判断"现在能不能读输入"的代码都是复制品，出现冲突以这里为准。

---

## 1. 三个镜头状态

| 状态 | 镜头行为 | 输入归属（`InputScope`） |
|---|---|---|
| `Direct` 直控 | 指数平滑跟随当前受控实体 | `Direct` |
| `Strategy` 战略 | 自由平移 + 缩放，不跟随任何单位 | `Strategy` |
| `Transition` 过渡 | smoothstep 插值，0.35s | `None`（谁都读不到） |

`Transition` **不是稳定态**，一定有去处（`_pendingMode`）。过渡落地时若目标已失效
（受控实体在这 0.35 秒里死了），直接落到 `Strategy`，不留悬空状态。

过渡到 `Direct` 期间**持续重算终点**——受控实体在这期间还在动，用固定终点会让镜头落地时再抽一下。

---

## 2. 输入所有权：为什么需要一个仲裁者

立这一层的直接原因是仓库里已经出现了**真实的双重输入**：`Tab` 同时被
`CellPlayerController`（切换控制目标）和 `BattleOverlayUIToolkit`（开卡组面板）监听，
两个消费者互不知情，按一次触发两件事。

战略视角进来之后这类冲突只会更多——平移的 WASD 与直控的 WASD 是同一组物理键。

### 纪律

- 玩法层与 UI 层**不得**再直接调 `Input.GetKeyDown` 读功能键，一律走 `InputRouter`；
- 每个功能键**同一帧只被消费一次**，第二个消费者拿到 `false`。
  「不产生双重输入」因此是结构上不可能，而不是靠各处自觉；
- 持续键（移动、平移）不做同帧唯一性——互斥由 `InputScope` 保证，
  `Direct` 与 `Strategy` 不可能同时成立。

### 两个模态来源故意分开存

`ModalUiOpen` 是只读合成属性 = `_modalUi || _gameplayPaused`：

| 来源 | 谁写 | 时机 |
|---|---|---|
| `_modalUi` | `BattleOverlayUIToolkit.SetPanel` | 面板开关时 |
| `_gameplayPaused` | `CellStageFlow.Update` | **每帧同步** |

**合成一个字段会互相覆盖**：选卡走 `CellStageFlow._paused` 直写、根本不经过面板开关；
而卡组/商店面板打开时 `_paused` 又是 false。谁后写谁赢的话，总有一条路径会把另一条抹掉。

玩法暂停用**每帧同步**而不是在写入点接线，是因为 `_paused` 有选卡、商店、暂停菜单、
GM 调试、放弃本局等多个写入点，逐个接线必然漏一个，而漏掉的那个会让输入
**永久卡在让位状态**——这种 bug 只在特定路径下才复现。

---

## 3. 键位仲裁结果（产品决策，可推翻）

| 键 | 归属 | 说明 |
|---|---|---|
| `Tab` | 切换控制目标（`Direct`） | M1 核心机制，里程碑出口标准写明"玩家能在三个真实友军间切换" |
| `Z` | 卡组面板 | **从 `Tab` 改过来**，为上一条让位；`Z` 在当前键位表未被占用 |
| `M` | 切换战略/直控视角 | 新增。语义取 Map；全局响应，过渡期间不接受 |
| `B` / `V` | 商店 / 图鉴 | 不变，改走 `ConsumeGlobalKeyDown` |
| `Esc` | 关面板 / 暂停菜单 | `allowDuringModal: true`——它正是用来关面板的 |
| `WASD` / 方向键 | `Direct` 下移动；`Strategy` 下平移镜头 | 同一组键两种含义，靠 `InputScope` 互斥 |
| 滚轮 | `Strategy` 下缩放 | 直控下不改视距 |

Tab 与卡组面板二选一是**产品决策**：M1 刚建立的控制切换是里程碑出口标准的一部分，
优先级高于面板快捷键。若后续要改回，改 `BattleOverlayUIToolkit` 里那一处即可。

---

## 4. 镜头为什么不是 GameModule

`CellStageFlow.Update` 的 `_paused` 早退会把整个 `_hub` 冻住，原先内联的 `FollowCamera`
也在早退之后，所以**暂停时镜头完全冻结**。而 M2-01 要求暂停下能选择目标。

因此 `CameraDirector` 刻意不注册进 `_hub`，由 `CellStageFlow` 在早退**之前**显式驱动，
位置与它此前内联 `FollowCamera` 的调用点对应。

它用 `Time.unscaledDeltaTime`：调试加速或慢放时镜头手感不该跟着变。
直控跟随在 `paused` 时直接吸附到目标位而不做平滑推进——dt 照常流逝会让镜头
在一个冻结的世界里继续爬。

---

## 5. 镜头绝不碰模拟状态

`CameraDirector` 只写 `Camera.transform` 与 `orthographicSize`，一个字都不碰模拟。
切视角是镜头的事，跟单位在哪、归谁控制无关。

回归断言里有一条专门守这个：视角切换全过程前后，快照里所有存活单位的 `Position`
必须逐个完全不变。

---

## 6. 无效目标回退，双向都要挡

- **自动**：`Direct` 下每帧校验锚点，受控实体没了就自动转 `Strategy`；
- **手动**：`RequestDirect()` 在没有有效受控实体时**返回 false 并停在战略视角**，
  不许切回一个不存在的目标。

只做自动那一半是不够的——玩家仍能手动按键切进一个空目标。

注意 `TryGetDirectAnchor` 只认 `hasControlled == true`，**回退锚点不算**：
那是战略视角的活（回退锚点的语义见 `Control_Lifecycle_Contract.md` §4）。

---

## 7. M2-01 覆盖程度的如实说明

验收项「允许暂停时选择目标」目前只完成**结构部分**：状态机在暂停下仍然运转
（`Tick` 照常被驱动、过渡能走完、`FocusStrategyOn` 可用）。

但当前仓库里**所有暂停都伴随模态面板**（选卡三选一 / 商店 / 暂停菜单），
所以 `_gameplayPaused` 一律夺走输入，行为上等价于"暂停时键盘不响应镜头"。

M2-02 引入「战略暂停下达命令」后，那条路径不开面板，届时要把
`SetGameplayPaused` 改成**按暂停原因区分**——战略暂停必须保留 `Strategy` 域输入，
否则暂停下根本没法选单位。这一条已在 `InputRouter.SetGameplayPaused` 的注释里标注。

---

## 8. 尚未收口的直接 Input 调用

以下仍在直接调 `Input.GetKeyDown`，它们是面板/调试键，不构成双重输入，留待 M2-02
（RTS 选择引入鼠标输入冲突时）统一收口：

- `BattleHudToolkit.cs`：U / N 键
- `BattleCarrierUIToolkit.cs`：O 键
- `BattleSandboxUIToolkit.cs`：L 键（调试）
- `StressTestToggle.cs` / `SimStressTest.cs`：F11（调试）
- `FirstPlayable/`：历史 demo，域外（仓规「勿加功能」）
