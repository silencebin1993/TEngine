# 权威切换与冲突表（2026-08-11）

> **用途**：给总控 / Claude Code 短读。禁止为「对账」整本通读旧 §17 与本夹全部文件。  
> **决策**：人要求先消双源，再实现新需求。

---

## 1. 当前权威（化学 / 代谢组合）

| 主题 | 唯一权威 | 何时读 |
|---|---|---|
| 冻结宪法（基元/字段/管道/禁令） | `代谢切片-冻结总案-基元与美术.md` | 接入游戏 / 定名词与种类时 |
| ChemEngine 独立库 | `化学引擎-ClaudeCode需求规格.md` | **实现窗唯一需求文件** |
| 卡牌/切片包装 | `细胞肉鸽-基元卡牌包装.md` | ChemEngine 绿后的下一窗 |
| 复玩三轴 | `复玩三轴-ClaudeCode需求规格.md` | 包装落地后 |
| 玩家白话名词 | `代谢切片-白话名词说明书.md` | 写 UI/文案时；程序窗默认不读 |
| 美术出图 | `美术AI静图出图规范.md` | 美术窗；程序窗禁止读 |

旧权威中的化学相关节：

| 旧节 | 状态 |
|---|---|
| `Cell_Stage_Spec.md` §17 | **Superseded**（正文保留作史，实现禁止照做） |
| `Game_Framework_Design.md` §5.8 | **Superseded** |
| `cell.ReactionRule` / `cell.CatalystRule` 表规划 | **Paused**；接 ChemEngine 时另定表方案 |
| epic `combat-alchemy` / sprint-006 | **Superseded / Paused**，禁止再派 Worker |

仍有效（非化学专章）：`Cell_Stage_Spec` 其它章、`Game_Framework` 热更边界 / AOT Sim / 模块框架等——**只按节读冲突处**。

---

## 2. 冲突摘要（旧 §17 路线 vs 冻结总案）

| ID | 旧（§17 / combat-alchemy） | 新（冻结总案 / ChemEngine） | 处置 |
|---|---|---|---|
| C1 | 状态×状态反应挂在 `StatusSystem.ApplyTimed` | 做法 C：基元改 `Packet`/`HitEvent`/`TagSet`，引擎管道叠加 | **废弃旧挂点设计**；接入时另定桥 |
| C2 | 不新增词缀/效果 Kind，只重组 32 词缀 | 新基元种类（器官/基因等）+ 反应注册表 | 新路线为准；旧「禁止新 Kind」仅适用于旧 alchemy epic |
| C3 | 催化卡 = 运行时给 EffectSpec 注入 Affix | 催化 = tag/状态反应表 + 正交修饰叠乘 | 勿实现旧 CatalystRule 故事 |
| C4 | `OnReaction` 订阅 ReactionEvent | ChemEngine 事件/正规形另定；游戏侧后接 | 旧 story-003 不做 |
| C5 | 内容仍是「卡牌+词缀」主循环 | 代谢切片 + 有向管 + 双系统（切片/储备囊）+ 三轴 | 产品形态以冻结总案为准；旧卡牌循环未整本废，但化学深度不靠 §17 |
| C6 | 热更层 `ReactionSystem` 直接做判定 | 核心库 **零 Unity**，游戏只写桥 | 先独立库，再桥；禁止把引擎写进 `GameLogic` 当核心 |

未冲突、仍守：热更 O(1)/事件驱动、判定不下沉 `Main/Sim`、Built-in RP / 无 Entities、资源 `GameRes/Raw`。

---

## 3. 已存在旧半成品代码（勿继续扩）

仓库内已有按旧 §17 开工痕迹（至少）：

- `GameLogic/Battle/ReactionSystem.cs`
- `GameLogic/Battle/ReactionRuleSpec.cs`
- 及相关 `StatusSystem` / `EffectDealDamage` / Luban 加载挂钩

**规则**：ChemEngine 实现窗 **禁止** 为「对齐」去读改这些文件。接入游戏时另开窗：审计 → 隔离或删除 → 桥接新库。

---

## 4. Claude Code 开场纪律（化学相关）

1. 一窗只喂上表「唯一权威」中的 **一份** 文件。  
2. 禁止整本 Read：`GDD_Starter_Pack`、FirstPlayable、Demo、`Archive/**`、整本旧 §17「对照实现」。  
3. 独立库阶段：禁止改 `GameLogic` / 禁止读美术规范。  
4. 深度把冻结总案写回 `Cell_Stage_Spec` 正文：等 **接入游戏** 那一轮，不在本切换完成。

---

## 5. 制作队列

| 项 | 状态 |
|---|---|
| `combat-alchemy` 001～005 | Superseded，不派工 |
| sprint-006 | Paused/Superseded |
| 下一实现焦点 | **ChemEngine 独立库**（见化学引擎规格）；文案已切换，story/epic 可后续再建 |
| sprint-005 剩余 003～009 | 仍 Paused；化学库完成前不自动插队回去，除非人改口 |
