# 任务三：召唤机制

> 2026-08-23（one-shot-prompt.txt 全量执行）

## 架构发现（影响实现路径）

`carddata.py` 头注释确认：旧 135 张 Route/Trigger/grantAbilityId 战斗卡池已随
metabolic-playerization-004 整体 Delist，不再参与生成——`cell.Card` 表现在只有 28 张代谢
内容卡（器官/基因）。这意味着"新增召唤技能卡"字面上的"卡"入口已不存在于真实抽卡池。
`AbilitySystem`/`AbilitySpec` 基础设施本身仍在跑（dash 通过 `GrantStarterAbilities()` 直接
授予，不经过卡池），故采用与 dash 相同的直接授予路径，而不是挂一张永远抽不到的卡。

## 改动

**Sim 核心**（`BinGames.Sim`，Main 层）：
- `SimTypes.cs`：`BehaviorKind` 新增 `MinionSeekAttack=10`/`MinionSeekExplode=11`。
- 新增 `Jobs/MinionTargetingUtil.cs`：Burst 兼容的空间哈希最近 Hostile 查询，`JobSteering`
  （并行 Job）与 `SimWorld.ResolveMinionCombat`（主线程）共用同一份逻辑。
- `JobSteering.cs`：新增 `Hash`/`InvCellSize` 字段，`MinionSeekAttack/Explode` 分支索敌最近
  Hostile（找不到则退化 Wander），不改动其余既有原型分支（Hostile 阵营行为零改动）。
- `SimWorld.cs`：新增 `ResolveMinionCombat(dt)`——PlayerMinion 阵营专属，主线程线性扫描
  （数量恒被 MinionCap 卡在个位数，无需额外 Burst job）。命中/爆炸走标准 `DamageRequest`→
  `_damageScratch`→`JobDamage` 管线，不新开死亡/命中事件路径；`MinionSeekExplode` 额外拼一条
  自伤 999 血的 `DamageRequest` 触发自毁，与其余单位死亡路径完全一致。新增
  `SetPlayerVisualId`（配合任务二）。

**内容数据**（`tools/cell_tables/`，改脚本 + `python gen_all.py` + Luban codegen，
未手改 xlsx）：
- `enums.py`：`BEHAVIOR_KIND` 补 10/11。
- `step2_small.py`：`ARCHETYPES` 新增 13"孢子仆从索敌"（MinionSeekAttack，索敌半径8/攻速
  1/6.67s/伤害3）、14"噬菌体追爆"（MinionSeekExplode，attackRange1.0/伤害15/
  preferredRange2.0 复用为爆炸半径）、15"菌丝体固着"（Stationary）。
- `step3_enemy_ability.py`：新增 Ability 29"召唤共生体"（Cooldown12/StaminaCost25/Self），
  4 条 AbilityEffect：Spawn×3（archetype 13/14/15 各 1，Value 承载生成血量/半径）+
  Area（Circle，Value2/Radius3/Duration10/Status=Slowed/Faction=Hostile——复用既有
  `EffectArea`→`AreaZoneSystem`→`ZoneKind.Roots` 管线做菌丝体"减速+持续伤害"光环，
  0.4s tick×Value2×0.4=0.8/tick≈2/s，Sim 核心零改动）。

**热更层**：
- `StatSheet.cs`：`MinionCap` 基线 0→3——原基线 0 下唯一能加到 3 的旧卡"神经副脑"已随卡池
  Delist 不可达，MinionCap 曾永久卡 0（连 molt/孢子爆发等既有 Spawn 效果也静默生不出单位，
  顺带修好这个既有死代码路径）。
- `CellStageFlow.GrantStarterAbilities()`：追加授予 Ability 29（同 dash 先例）。

## 验收（execute_code，真实调用全链路）

```
cell.Abilities.TryCast(召唤共生体所在槽位) → 一次 cast 拼出 1 孢子仆从(VisualId13) +
  1 噬菌体(VisualId14) + 1 菌丝体(VisualId15)，共享 MinionCap=3 全部用满
snap.Faction[i] == PlayerMinion 且数量=3（首次 cast，全部存活）
数秒真实时间后复测：噬菌体已自主索敌→接战→爆炸→自毁（minionCount 降至 2，仅剩
  spore+mycelium），全程 read_console 0 error——证明索敌/接战/爆炸/自毁管线端到端可用，
  不是摆设
```

截图：
- `summon-minion-clean.png`/`summon-minion-mycelium.png`：菌丝体（扇形丝网造型）在场景中
  可见，与玩家胶囊体本体形状明显区分。

## Unity 真编译 + Play 验证

`refresh_unity` 0 error。Play 模式全程 `read_console(types=[error,warning])` 0 条。
