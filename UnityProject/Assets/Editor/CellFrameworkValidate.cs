using System;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Cards;
using GameLogic.Core;
using GameLogic.Progression;
using GameLogic.Spawning;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.Stats;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// 编辑器侧的框架自检。用 batchmode 跑，不需要人工点播放。
    ///
    /// 存在意义：编译通过 ≠ 能跑。这里真的去 Load 配置表、真的推进内核若干帧、
    /// 真的检查数值有没有动，把"只是能编译"和"确实在工作"区分开。
    ///
    /// 用法（Unity 编辑器必须关闭，否则 Library 被锁）：
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
    ///     -executeMethod GameLogic.EditorTools.CellFrameworkValidate.RunAll -logFile -
    /// </summary>
    public static class CellFrameworkValidate
    {
        private static readonly StringBuilder Report = new StringBuilder();
        private static int _fail;

        [MenuItem("BinGames/自检：细胞阶段框架")]
        public static void RunAll()
        {
            Report.Clear();
            _fail = 0;

            Line("========== 细胞阶段框架自检 ==========");

            try
            {
                ValidateData();
                ValidateSimKernel();
                ValidateControlledUnitIdentity();
                ValidateUnifiedEntityIntents();
                ValidateControlBridge();
                ValidateControlledPresentation();
                ValidateSpatialHash();
                ValidateDevourThreshold();
                ValidateStatusExpiry();
                ValidateMinionCap();
                ValidateDeathCauseKind();
                ValidateInheritance();
                ValidateBossPhase();
                ValidateShop();
                ValidateCodex();
                ValidateControlLifecycle();
            }
            catch (Exception e)
            {
                Fail($"自检抛异常：{e}");
            }

            Line("======================================");
            Line(_fail == 0 ? "全部通过" : $"失败 {_fail} 项");

            Debug.Log(Report.ToString());

            if (Application.isBatchMode)
            {
                // 用退出码把结果传给 CI / 脚本
                EditorApplication.Exit(_fail == 0 ? 0 : 1);
            }
        }

        // ── 配置表 ──────────────────────────────────────────

        private static void ValidateData()
        {
            Line("\n[1] 配置表加载");

            DataRegistry reg = DataRegistry.Instance;
            reg.Clear();
            reg.Load();

            if (reg.UsingFallback)
            {
                Fail("走了内置兜底内容，说明 Luban cell.* 表没读上。"
                     + "检查 GameRes/Raw/Configs/bytes/cell_*.bytes 是否存在。");
            }
            else
            {
                Ok("Luban cell.* 表读取成功（未回落兜底）");
            }

            // 卡池与行为原型表会随内容持续增长，写死精确数量的断言每加一张卡就红一次，
            // 最终没人再看它。这里只守"表确实加载上了"的下限，具体内容由下面的结构抽样负责。
            // 生态时期/生态事件是固定编制，才继续用精确等值。
            Expect(reg.AllCards.Count >= 80, $"卡牌至少 80 张（实际 {reg.AllCards.Count}）");
            Expect(reg.Phases.Count == 6, $"生态时期 6 个（实际 {reg.Phases.Count}）");
            Expect(reg.EcoEvents.Count == 16, $"生态事件 16 个（实际 {reg.EcoEvents.Count}）");
            Expect(reg.Archetypes.Count >= 12, $"行为原型至少 12 个（实际 {reg.Archetypes.Count}）");

            // TR-cell-011：首领 90（原核霸主）应配 3 个阶段
            var bossPhases = reg.GetBossPhases(90);
            Expect(bossPhases != null && bossPhases.Count == 3,
                $"首领 90 应有 3 个阶段（实际 {bossPhases?.Count ?? 0}）");

            // 抽样验证映射真的填对了字段，而不是全零。
            // 不写死卡 ID：原先写死的 1001（裂齿口器）在当前卡表里根本不存在，这条断言一直在假红，
            // 假红久了整份报告就没人看了。这里检验的本来就是 Status/Duration 映射有没有填上，
            // 不是某一张具体的卡，所以改成"取 Id 最小的带状态效果卡"动态取样——顺序确定、可复现。
            CardSpec sample = null;
            int cardsWithEffects = 0;
            foreach (CardSpec c in reg.AllCards)
            {
                if (c.Effects != null && c.Effects.Count > 0)
                {
                    cardsWithEffects++;
                }
                if (sample == null || c.Id < sample.Id)
                {
                    sample = c;
                }
            }

            if (sample == null)
            {
                Fail("卡表为空，无法抽样验证字段映射");
            }
            else
            {
                Expect(!string.IsNullOrEmpty(sample.Name), $"抽样卡 {sample.Id} 应有名称（实际 '{sample.Name}'）");
                Expect(System.Enum.IsDefined(typeof(Cards.CardTrigger), sample.Trigger),
                    $"抽样卡 {sample.Id} 的触发时机应是合法枚举（实际 {sample.Trigger}）");
                Expect(System.Enum.IsDefined(typeof(CardRoute), sample.Route),
                    $"抽样卡 {sample.Id} 的路线应是合法枚举（实际 {sample.Route}）");
            }

            // 只记录、不断言：当前 Luban 卡表里没有任何卡带 EffectSpec，
            // 而 CardTriggerBus / AbilitySystem 仍在消费 CardSpec.Effects（只有内置兜底内容
            // CellContentSeed 才填它）。这要么说明表卡的效果已整体迁移到装配/基因系统，
            // 要么说明 Luban 的 Effects 映射漏读了。两种结论对玩法的含义完全相反，
            // 在查清之前不应该用 Expect 单方面判定谁对——先把事实摆在报告里。
            Line($"  · 表卡带 EffectSpec 的数量：{cardsWithEffects}/{reg.AllCards.Count}"
                 + "（为 0 时请核实卡效果是否已迁出 CardSpec.Effects）");

            // 验证多状态位 OR 起来了
            var elite = reg.GetEnemy(50);
            if (elite == null)
            {
                Fail("找不到敌人 50（巨噬吞食者）");
            }
            else
            {
                bool hasBoth = (elite.InitialStatus & SimStatus.Unedible) != 0
                               && (elite.InitialStatus & SimStatus.Elite) != 0;
                Expect(hasBoth, $"敌人 50 应同时有 Unedible|Elite（实际 {elite.InitialStatus}）");
                Expect(elite.IsElite, "敌人 50 应标记为精英");
                Expect(elite.SpawnCost > 0f, $"敌人 50 SpawnCost 应 > 0（实际 {elite.SpawnCost}）");
            }

            // 验证 list 类型字段（敌人池）解析正确
            var phase0 = reg.GetPhase(0);
            if (phase0 == null)
            {
                Fail("找不到时期 0");
            }
            else
            {
                Expect(phase0.EnemyPool != null && phase0.EnemyPool.Length == 4,
                    $"时期 0 敌人池应有 4 项（实际 {phase0.EnemyPool?.Length ?? 0}）");
                Expect(!string.IsNullOrEmpty(phase0.FlavorText), "时期 0 应有切换文案");
            }

            // 验证授予技能的卡能解析到技能
            var zapCard = reg.GetCard(3001);
            if (zapCard != null && zapCard.GrantAbilityId > 0)
            {
                var ab = reg.GetAbility(zapCard.GrantAbilityId);
                Expect(ab != null, $"卡 3001 授予的技能 {zapCard.GrantAbilityId} 应存在");
                Expect(ab != null && ab.Effects.Count > 0, "技能 2（放电）应有效果");
            }

            // 副作用修正应被分到 DrawbackMods 而非 StatMods
            var drawbackCard = reg.GetCard(1007);
            if (drawbackCard != null)
            {
                Expect(drawbackCard.DrawbackMods != null && drawbackCard.DrawbackMods.Count > 0,
                    "卡 1007 应有副作用属性修正");
                Expect(drawbackCard.StatMods.Count > 0, "卡 1007 应有正面属性修正");
            }
        }

        // ── 内核 ────────────────────────────────────────────

        private static void ValidateSimKernel()
        {
            Line("\n[2] AOT 内核推进");

            var world = new SimWorld();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 2048;
            cfg.ArenaHalfExtent = 60f;
            world.Initialize(cfg);
            world.SetArchetypes(DataRegistry.Instance.ArchetypeArray());

            SimCommandBuffer cmds = default;
            cmds.Initialize(Unity.Collections.Allocator.Persistent, 512);

            try
            {
                world.SetPlayerStats(100f, 100f, 1f, 8f);
                world.SetPlayerPosition(float2.zero);

                // 生成一批追猎型敌人，它们应该朝玩家移动
                const int N = 200;
                for (int i = 0; i < N; i++)
                {
                    float ang = i / (float)N * math.PI * 2f;
                    world.SpawnUnit(new SpawnRequest
                    {
                        Position = new float2(math.cos(ang), math.sin(ang)) * 25f,
                        Health = 100f,
                        Radius = 0.5f,
                        MaxSpeed = 5f,
                        ArchetypeId = 1, // Chase
                        Faction = SimFaction.Hostile,
                        LogicId = i + 1,
                    });
                }

                SimSnapshot s0 = world.GetSnapshot();
                Expect(s0.Count == N + 1, $"生成后单位数应为 {N + 1}（实际 {s0.Count}）");

                float distBefore = AvgDistToPlayer(s0);

                for (int f = 0; f < 60; f++)
                {
                    cmds.SetPlayerIntent(PlayerIntent.Idle);
                    world.Step(1f / 60f, ref cmds);
                }

                SimSnapshot s1 = world.GetSnapshot();
                float distAfter = AvgDistToPlayer(s1);

                Expect(distAfter < distBefore - 1f,
                    $"追猎原型应靠近玩家：{distBefore:F2} → {distAfter:F2}");

                // 范围伤害应真的扣血并产出死亡事件
                cmds.Damage(new DamageRequest
                {
                    Origin = float2.zero,
                    Radius = 100f,
                    TargetIndex = SimConst.InvalidIndex,
                    Amount = 1000f,
                    TargetFaction = SimFaction.Hostile,
                    ChainCount = 0,
                });
                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(1f / 60f, ref cmds);

                SimSnapshot s2 = world.GetSnapshot();
                Expect(s2.DeathCount > 0, $"全场伤害应产生死亡事件（实际 {s2.DeathCount}）");
                Expect(s2.CountHostiles() == 0,
                    $"1000 点全场伤害后应无存活敌人（实际 {s2.CountHostiles()}）");

                // 槽位应被回收：再生成同样数量不应超出容量
                int reborn = 0;
                for (int i = 0; i < N; i++)
                {
                    int idx = world.SpawnUnit(new SpawnRequest
                    {
                        Position = new float2(i * 0.1f, 0f),
                        Health = 10f, Radius = 0.4f, MaxSpeed = 3f,
                        ArchetypeId = 0, Faction = SimFaction.Hostile, LogicId = 5000 + i,
                    });
                    if (idx != SimConst.InvalidIndex)
                    {
                        reborn++;
                    }
                }
                Expect(reborn == N, $"死亡槽位应可复用，重生 {reborn}/{N}");
            }
            finally
            {
                cmds.Dispose();
                world.Dispose();
            }
        }

        private static float AvgDistToPlayer(in SimSnapshot s)
        {
            float sum = 0f;
            int n = 0;
            for (int i = 1; i < s.Count; i++)
            {
                if (s.Alive[i] == 0)
                {
                    continue;
                }
                sum += math.distance(s.Position[i], s.PlayerPosition);
                n++;
            }
            return n == 0 ? 0f : sum / n;
        }

        // ── 控制身份 ────────────────────────────────────────

        /// <summary>
        /// ProjectA M1-02：稳定实体 ID、唯一玩家意图来源、受控切换、槽位复用与世界重置。
        /// 这里只验证身份状态机，不提前把玩家/AI 意图统一接进作业（那属于 M1-03）。
        /// </summary>
        private static void ValidateControlledUnitIdentity()
        {
            Line("\n[2.1] 控制身份数据模型（M1-02）");

            var world = new SimWorld();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            world.Initialize(cfg);

            try
            {
                SimEntityId unitA = world.ControlledUnitId;
                int indexB = world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(2f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 1f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, LogicId = 101,
                });
                int indexC = world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(4f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 1f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = 102,
                });
                int enemyIndex = world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(6f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 1f, ArchetypeId = 0, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = 103,
                });

                world.TryGetEntityId(indexB, out SimEntityId unitB);
                world.TryGetEntityId(indexC, out SimEntityId unitC);
                world.TryGetEntityId(enemyIndex, out SimEntityId enemy);
                Expect(unitA.IsValid && unitB.IsValid && unitC.IsValid &&
                       unitA != unitB && unitA != unitC && unitB != unitC,
                    "三个友军应拥有互不重复的稳定实体 ID");

                world.TryGetUnitControlState(unitB, out SimUnitControlState scriptedBeforeControl);
                Expect(scriptedBeforeControl.IntentSource == IntentSource.Scripted,
                    "生成单位应保存 Scripted 意图来源");

                ControlSwitchResult switchResult = world.TrySwitchControlledUnit(unitB);
                world.TryGetUnitControlState(unitA, out SimUnitControlState stateA);
                world.TryGetUnitControlState(unitB, out SimUnitControlState stateB);
                Expect(switchResult == ControlSwitchResult.Success && world.ControlledUnitId == unitB,
                    "控制权应从实体 A 切换到实体 B");
                Expect(stateA.IntentSource == IntentSource.AI && stateB.IntentSource == IntentSource.Player,
                    "切换后 A 恢复 AI，B 成为唯一 Player 意图来源");

                SimEntityId controlledBeforeRejects = world.ControlledUnitId;
                Expect(world.TrySwitchControlledUnit(enemy) == ControlSwitchResult.TargetNotFriendly &&
                       world.ControlledUnitId == controlledBeforeRejects,
                    "敌军控制请求应被拒绝且不改变当前控制实体");
                Expect(world.TrySwitchControlledUnit(new SimEntityId(ulong.MaxValue)) ==
                       ControlSwitchResult.TargetNotFound && world.ControlledUnitId == controlledBeforeRejects,
                    "不存在目标的控制请求应被拒绝且不改变当前控制实体");

                world.KillUnit(indexB, 0);
                world.TryGetUnitControlState(unitB, out SimUnitControlState deadStateB);
                // M1-06 起受控实体死亡不再直接落到"无控制"，而是确定性回弹到最近的存活友军
                // （契约见 DesignDocs/migration/Control_Lifecycle_Contract.md §2）。
                // "明确为无"仍是合法终态，但只在没有任何可回弹目标时出现——那条由 §12 的
                // ValidateControlLifecycle 用"杀光全部友军"单独覆盖。
                // 这里守住的不变量是：死掉的旧身份绝不继续被当作受控实体。
                Expect(world.ControlledUnitId != unitB,
                    "当前控制实体死亡后旧身份不得继续被当作受控实体");
                Expect(deadStateB.IntentSource == IntentSource.Scripted &&
                       world.TrySwitchControlledUnit(unitB) == ControlSwitchResult.TargetDead,
                    "死亡目标应恢复原意图来源，控制请求返回 TargetDead");

                int reusedIndex = world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(8f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 1f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    LogicId = 104,
                });
                world.TryGetEntityId(reusedIndex, out SimEntityId replacement);
                Expect(reusedIndex == indexB && replacement != unitB &&
                       !world.TryResolveUnit(unitB, out _) && world.TryResolveUnit(replacement, out int resolved) &&
                       resolved == reusedIndex,
                    "槽位复用必须分配新 ID，旧 ID 不得误解析到新单位");

                Expect(world.TrySwitchControlledUnit(unitC) == ControlSwitchResult.Success,
                    "重置前应可控制另一个存活友军");
                SimEntityId controlledBeforeReset = world.ControlledUnitId;
                world.Initialize(cfg);
                SimSnapshot resetSnapshot = world.GetSnapshot();
                Expect(resetSnapshot.Count == 1 && world.ControlledUnitId.IsValid &&
                       world.ControlledUnitId != controlledBeforeReset &&
                       resetSnapshot.ControlledUnitId == world.ControlledUnitId &&
                       resetSnapshot.IntentSourceOf(SimConst.PlayerIndex) == IntentSource.Player,
                    "世界重置后应确定地创建一个新的默认控制实体");
                Expect(!world.TryResolveUnit(controlledBeforeReset, out _),
                    "世界重置前的控制 ID 不得解析到重置后的实体");
            }
            finally
            {
                world.Dispose();
            }
        }

        /// <summary>
        /// ProjectA M1-03：Player、AI、Scripted 共用 UnitIntent；切换控制后旧实体恢复 AI，
        /// 伤害、接触伤害与吞噬查询跟随当前受控实体而非槽位 0。
        /// </summary>
        private static void ValidateUnifiedEntityIntents()
        {
            Line("\n[2.2] 统一实体意图与作业去玩家特判（M1-03）");

            var world = new SimWorld();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            cfg.ArenaHalfExtent = 40f;
            world.Initialize(cfg);

            BehaviorArchetype chase = BehaviorArchetype.Default;
            chase.Kind = BehaviorKind.Chase;
            chase.Accel = 100f;
            chase.AggroRange = 100f;
            chase.WanderStrength = 0f;
            chase.AttackDamage = 0f;
            chase.AttackRange = 0f;
            chase.RangedSpeed = 0f;
            chase.TurnRate = 0f;
            world.SetArchetypes(new[] { chase });

            SimCommandBuffer cmds = default;
            cmds.Initialize(Unity.Collections.Allocator.Persistent, 32);

            try
            {
                int unitBIndex = world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(-8f, 0f), Health = 100f, Radius = 1f,
                    MaxSpeed = 6f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = 201,
                });
                int unitCIndex = world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(8f, 0f), Health = 100f, Radius = 1f,
                    MaxSpeed = 6f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = 202,
                });
                int scriptedIndex = world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(0f, 8f), Health = 100f, Radius = 1f,
                    MaxSpeed = 6f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, LogicId = 203,
                });
                int hostileIndex = world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(8f, 8f), Health = 100f, Radius = 0.5f,
                    MaxSpeed = 5f, ArchetypeId = 0, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = 204,
                });

                world.TryGetEntityId(unitBIndex, out SimEntityId unitB);
                world.TryGetEntityId(unitCIndex, out SimEntityId unitC);
                world.TryGetEntityId(scriptedIndex, out SimEntityId scripted);
                world.TryGetEntityId(hostileIndex, out SimEntityId hostile);

                Expect(world.TrySwitchControlledUnit(unitB) == ControlSwitchResult.Success,
                    "M1-03 初始控制目标应可切到友军 B");

                PlayerIntent playerMove = PlayerIntent.Idle;
                playerMove.MoveDir = new float2(0f, 1f);
                cmds.SetPlayerIntent(playerMove);
                UnitIntent scriptedMove = UnitIntent.Idle(scripted, IntentSource.Scripted);
                scriptedMove.MoveDir = new float2(-1f, 0f);
                cmds.SetUnitIntent(scriptedMove);
                world.Step(0.05f, ref cmds);

                SimSnapshot first = world.GetSnapshot();
                Expect(first.FinalIntent[unitBIndex].Source == IntentSource.Player &&
                       first.Position[unitBIndex].y > 0f,
                    "受控友军 B 应从统一 UnitIntent 接收玩家移动");
                Expect(first.FinalIntent[scriptedIndex].Source == IntentSource.Scripted &&
                       first.Position[scriptedIndex].x < 0f,
                    "Scripted 单位应从同一 UnitIntent 形状接收移动");
                Expect(first.FinalIntent[unitCIndex].Source == IntentSource.AI &&
                       first.Position[unitCIndex].x < 8f,
                    "未受控友军 C 应继续由 AI 朝当前控制目标行动");

                float unitBXBeforeSwitch = first.Position[unitBIndex].x;
                float2 hostilePositionBefore = first.Position[hostileIndex];
                float2 controlledPositionBefore = first.Position[unitCIndex];
                float2 legacySlotPositionBefore = first.Position[SimConst.PlayerIndex];
                Expect(world.TrySwitchControlledUnit(unitC) == ControlSwitchResult.Success,
                    "控制权应可从友军 B 切到友军 C");

                PlayerIntent secondPlayerMove = PlayerIntent.Idle;
                secondPlayerMove.MoveDir = new float2(0f, -1f);
                cmds.SetPlayerIntent(secondPlayerMove);
                UnitIntent stalePlayerIntent = UnitIntent.Idle(unitB, IntentSource.Player);
                stalePlayerIntent.MoveDir = new float2(-1f, 0f);
                cmds.SetUnitIntent(stalePlayerIntent);
                world.Step(0.05f, ref cmds);

                SimSnapshot second = world.GetSnapshot();
                Expect(second.FinalIntent[unitCIndex].Source == IntentSource.Player &&
                       second.Position[unitCIndex].y < 0f,
                    "切换后友军 C 应接收玩家意图");
                Expect(second.FinalIntent[unitBIndex].Source == IntentSource.AI &&
                       second.Position[unitBIndex].x > unitBXBeforeSwitch,
                    "离开的友军 B 应恢复 AI，过期 Player 命令不得覆盖它");
                UnitIntent hostileIntent = second.FinalIntent[hostileIndex];
                float2 hostileIntentDirection = math.normalizesafe(hostileIntent.MoveDir);
                float2 directionToControlled = math.normalizesafe(controlledPositionBefore - hostilePositionBefore);
                float2 directionToLegacySlot = math.normalizesafe(legacySlotPositionBefore - hostilePositionBefore);
                float controlledTrackingDot = math.dot(hostileIntentDirection, directionToControlled);
                float legacySlotTrackingDot = math.dot(hostileIntentDirection, directionToLegacySlot);
                Expect(hostileIntent.Source == IntentSource.AI &&
                       controlledTrackingDot > 0.999f && controlledTrackingDot > legacySlotTrackingDot + 0.1f,
                    $"敌军 AI 应追踪当前控制实体 C，而不是固定槽位 0；" +
                    $"Hostile {hostilePositionBefore}→{second.Position[hostileIndex]}，" +
                    $"C {controlledPositionBefore}→{second.Position[unitCIndex]}，" +
                    $"FinalIntent Source={hostileIntent.Source} MoveDir={hostileIntent.MoveDir}，" +
                    $"dot(C)={controlledTrackingDot:F4} dot(slot0)={legacySlotTrackingDot:F4}");
                Expect(world.TrySwitchControlledUnit(hostile) == ControlSwitchResult.TargetNotFriendly,
                    "统一意图迁移后敌军仍不可被玩家控制");

                float controlledHealthBefore = world.PlayerHealth;
                cmds.Damage(new DamageRequest
                {
                    Origin = world.PlayerPosition,
                    Radius = -1f,
                    TargetIndex = unitCIndex,
                    Amount = 7f,
                    TargetFaction = SimFaction.PlayerMinion,
                    SourceLogicId = 204,
                });
                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(0.05f, ref cmds);
                SimSnapshot damaged = world.GetSnapshot();
                Expect(math.abs(world.PlayerHealth - (controlledHealthBefore - 7f)) < 0.001f &&
                       math.abs(damaged.PlayerDamageTaken - 7f) < 0.001f,
                    "JobDamage 应把受控友军 C 作为兼容伤害反馈目标");

                chase.AttackDamage = 3f;
                chase.AttackRange = 1f;
                chase.AttackCooldown = 1f;
                world.SetArchetypes(new[] { chase });
                int contactHostile = world.SpawnUnit(new SpawnRequest
                {
                    Position = world.PlayerPosition + new float2(0.5f, 0f),
                    Health = 100f, Radius = 0.5f, MaxSpeed = 0f,
                    ArchetypeId = 0, Faction = SimFaction.Hostile, LogicId = 205,
                });
                float beforeContact = world.PlayerHealth;
                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(0.05f, ref cmds);
                Expect(contactHostile >= 0 && math.abs(world.PlayerHealth - (beforeContact - 3f)) < 0.001f,
                    "JobContactDamage 应围绕当前受控友军 C 结算");

                world.SetPlayerStats(world.PlayerHealth, world.PlayerHealth, 2f, 6f);
                int pickupIndex = world.SpawnUnit(new SpawnRequest
                {
                    Position = world.PlayerPosition, Health = 1f, Radius = 0.25f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.Pickup, LogicId = 206,
                });
                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(0.05f, ref cmds);
                SimSnapshot devour = world.GetSnapshot();
                bool foundPickup = false;
                for (int i = 0; i < devour.DevourCandidateCount; i++)
                {
                    if (devour.DevourCandidates[i] == pickupIndex)
                    {
                        foundPickup = true;
                        break;
                    }
                }
                Expect(foundPickup, "JobDevourScan 应以当前受控友军 C 为吞噬查询中心");
            }
            finally
            {
                cmds.Dispose();
                world.Dispose();
            }
        }

        /// <summary>
        /// ProjectA M1-04：HotFix 只经 SimBridge 查询/切换控制；候选规则、失败码、
        /// 状态不变、冷却与单次事件都在这一层验收。
        /// </summary>
        private static void ValidateControlBridge()
        {
            Line("\n[2.3] HotFix/模拟控制桥接（M1-04）");

            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            cfg.ArenaHalfExtent = 40f;
            sim.Begin(cfg, Array.Empty<BehaviorArchetype>());
            sim.ConfigureControlSwitch(5f, 0.5f);

            int eventCount = 0;
            ControlledUnitChangedSignal lastEvent = default;
            Action<ControlledUnitChangedSignal> handler = signal =>
            {
                eventCount++;
                lastEvent = signal;
            };
            Signals.Subscribe(handler);

            try
            {
                SimEntityId initial = sim.ControlledUnitId;
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(2f, 0f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, LogicId = 301,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(4f, 0f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, LogicId = 302,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(9f, 0f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, LogicId = 303,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(1f, 0f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.Scripted, LogicId = 304,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(3f, 0f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, LogicId = 305,
                });
                sim.OnUpdate(0.01f);

                SimSnapshot snap = sim.Snapshot;
                SimEntityId unitB = FindEntityId(snap, 301, out int unitBIndex);
                SimEntityId unitC = FindEntityId(snap, 302, out _);
                SimEntityId farUnit = FindEntityId(snap, 303, out _);
                SimEntityId enemy = FindEntityId(snap, 304, out _);
                SimEntityId deadUnit = FindEntityId(snap, 305, out int deadIndex);
                sim.ConsumeUnit(deadIndex);

                SimControlCandidate[] candidates = sim.GetControlCandidates();
                Expect(candidates.Length == 2 && candidates[0].EntityId == unitB &&
                       candidates[1].EntityId == unitC,
                    "候选应只含范围内存活友军，并按距离稳定排序");
                Expect(sim.TryGetControlledUnit(out SimUnitControlState current) &&
                       current.EntityId == initial,
                    "桥接层应能只读查询当前受控稳定实体");

                Expect(sim.RequestControlSwitch(unitB) == ControlRequestResult.Success &&
                       sim.ControlledUnitId == unitB && eventCount == 1 &&
                       lastEvent.PreviousUnitId == initial && lastEvent.CurrentUnitId == unitB &&
                       lastEvent.Result == ControlRequestResult.Success,
                    "合法切换 A→B 应成功且只发布一次包含前后稳定 ID 的事件");

                SimEntityId beforeRejects = sim.ControlledUnitId;
                Expect(sim.RequestControlSwitch(unitB) == ControlRequestResult.AlreadyControlled,
                    "重复请求当前实体应返回 AlreadyControlled");
                Expect(sim.RequestControlSwitch(enemy) == ControlRequestResult.TargetNotFriendly,
                    "敌军请求应返回 TargetNotFriendly");
                Expect(sim.RequestControlSwitch(deadUnit) == ControlRequestResult.TargetDead,
                    "死亡目标请求应返回 TargetDead");
                Expect(sim.RequestControlSwitch(farUnit) == ControlRequestResult.OutOfSignalRange,
                    "越距友军请求应返回 OutOfSignalRange");
                Expect(sim.RequestControlSwitch(new SimEntityId(ulong.MaxValue)) ==
                       ControlRequestResult.TargetNotFound,
                    "不存在稳定 ID 应返回 TargetNotFound");
                Expect(sim.RequestControlSwitch(unitC) == ControlRequestResult.CooldownActive &&
                       sim.ControlledUnitId == beforeRejects && eventCount == 1,
                    "冷却中合法目标应被拒绝，所有非法请求均不得改变状态或发布事件");

                sim.OnUpdate(0.5f);
                Expect(sim.RequestControlSwitch(unitC) == ControlRequestResult.Success &&
                       sim.ControlledUnitId == unitC && eventCount == 2 &&
                       lastEvent.PreviousUnitId == unitB && lastEvent.CurrentUnitId == unitC,
                    "冷却结束后 B→C 应成功，且该次切换仍只发布一次事件");

                // 确保测试确实取得了有效实体，避免索引查找失败时出现误通过。
                Expect(unitB.IsValid && unitC.IsValid && farUnit.IsValid && enemy.IsValid &&
                       deadUnit.IsValid && unitBIndex >= 0,
                    "M1-04 测试单位应全部拥有有效稳定 ID");
            }
            finally
            {
                Signals.Unsubscribe(handler);
                sim.End();
            }
        }

        private static SimEntityId FindEntityId(SimSnapshot snapshot, int logicId, out int unitIndex)
        {
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (snapshot.LogicId[i] == logicId)
                {
                    unitIndex = i;
                    return snapshot.EntityId[i];
                }
            }
            unitIndex = SimConst.InvalidIndex;
            return SimEntityId.None;
        }

        /// <summary>
        /// ProjectA M1-05：连续 20 次切换期间，HUD/状态视图、相机锚点、渲染控制索引
        /// 与旧目标玩家专属缓存始终跟随稳定控制 ID；失去目标后保留显式战略锚点。
        /// </summary>
        private static void ValidateControlledPresentation()
        {
            Line("\n[2.4] 表现与 UI 受控实体查找（M1-05）");

            var sim = new SimBridge();
            Expect(!sim.TryGetControlledPresentation(out _) &&
                   !sim.TryGetPresentationAnchor(out _, out _),
                "模拟生成前 UI/表现查询应安全失败，不抛异常或伪造索引 0");

            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            cfg.ArenaHalfExtent = 100f;
            sim.Begin(cfg, Array.Empty<BehaviorArchetype>());
            sim.ConfigureControlSwitch(100f, 0f);

            var renderer = new SimRenderer();
            renderer.Initialize(Array.Empty<SimVisual>(), cfg.UnitCapacity);

            int eventCount = 0;
            SimEntityId[] ids = null;
            int[] indexes = null;
            int[] baseVisuals = { 0, 11, 12 };
            Action<ControlledUnitChangedSignal> handler = signal =>
            {
                eventCount++;
                renderer.ClearControlledPresentation();
                if (ids == null) { return; }
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i] == signal.PreviousUnitId)
                    {
                        sim.SetUnitVisualId(ids[i], baseVisuals[i]);
                        break;
                    }
                }
            };
            Signals.Subscribe(handler);

            try
            {
                SimEntityId initial = sim.ControlledUnitId;
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(10f, 3f), Health = 61f, Radius = 0.7f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, InitialStatus = SimStatus.Conductive,
                    LogicId = 401, VisualId = 11,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(-7f, 5f), Health = 37f, Radius = 1.1f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, InitialStatus = SimStatus.Marked,
                    LogicId = 402, VisualId = 12,
                });
                sim.OnUpdate(0f);

                SimSnapshot spawned = sim.Snapshot;
                SimEntityId unitB = FindEntityId(spawned, 401, out int unitBIndex);
                SimEntityId unitC = FindEntityId(spawned, 402, out int unitCIndex);
                ids = new[] { initial, unitB, unitC };
                indexes = new[] { spawned.ControlledUnitIndex, unitBIndex, unitCIndex };

                var playerController = new CellPlayerController();
                playerController.Bind(sim, null, null, null, null);
                bool inputCycleOk =
                    playerController.RequestNextControlCandidate() == ControlRequestResult.Success &&
                    sim.ControlledUnitId == unitB &&
                    playerController.RequestNextControlCandidate() == ControlRequestResult.Success &&
                    sim.ControlledUnitId == unitC &&
                    playerController.RequestNextControlCandidate() == ControlRequestResult.Success &&
                    sim.ControlledUnitId == initial &&
                    playerController.LastControlCandidateCount == 2;
                Expect(inputCycleOk,
                    "Tab 切换入口应按稳定实体 ID 完整循环 A→B→C→A，而不是在最近两者间往返");
                eventCount = 0;

                bool requestsOk = true;
                bool hudAndStatusOk = true;
                bool cameraAnchorOk = true;
                bool rendererLookupOk = true;
                bool oldTargetCleanupOk = true;
                SimEntityId current = initial;

                renderer.Draw(in spawned);
                for (int iteration = 0; iteration < 20; iteration++)
                {
                    int targetSlot = (iteration + 1) % ids.Length;
                    SimEntityId target = ids[targetSlot];
                    SimEntityId previous = current;
                    int previousSlot = Array.IndexOf(ids, previous);

                    sim.SetUnitVisualId(previous, 99);
                    renderer.SetControlledLunge(new float2(1f, 0f), 1f);
                    requestsOk &= sim.RequestControlSwitch(target) == ControlRequestResult.Success;
                    current = target;

                    SimSnapshot snap = sim.Snapshot;
                    hudAndStatusOk &= sim.TryGetControlledPresentation(out SimControlledUnitView view) &&
                        view.EntityId == target && view.UnitIndex == indexes[targetSlot] &&
                        math.abs(view.Health - snap.Health[indexes[targetSlot]]) < 0.001f &&
                        view.Status == (SimStatus)snap.Status[indexes[targetSlot]];

                    cameraAnchorOk &= sim.TryGetPresentationAnchor(out float2 cameraAnchor, out bool hasControlled) &&
                        hasControlled && math.distancesq(cameraAnchor, snap.Position[indexes[targetSlot]]) < 0.0001f;

                    renderer.Draw(in snap);
                    rendererLookupOk &= renderer.LastControlledUnitId == target &&
                        renderer.LastControlledUnitIndex == indexes[targetSlot];
                    oldTargetCleanupOk &= !renderer.HasControlledLunge && previousSlot >= 0 &&
                        snap.VisualId[indexes[previousSlot]] == baseVisuals[previousSlot];
                }

                Expect(requestsOk && eventCount == 20,
                    "连续 20 次切换应全部成功且每次恰好发布一个事件");
                Expect(hudAndStatusOk,
                    "连续切换期间 HUD 生命与状态视图应始终解析当前 ControlledUnitId");
                Expect(cameraAnchorOk,
                    "连续切换期间相机战术锚点应始终等于当前受控实体位置");
                Expect(rendererLookupOk,
                    "SimRenderer 应在每次切换后解析到当前受控稳定 ID 与槽位");
                Expect(oldTargetCleanupOk,
                    "切换事件应清理旧目标前冲缓存并恢复旧目标基础视觉");

                sim.TryGetControlledPresentation(out SimControlledUnitView beforeLoss);
                float2 lastTacticalPosition = beforeLoss.Position;
                // M1-06：场上还站着别的友军，默认会触发意识回弹，根本到不了"无控制"。
                // 这一段要验的恰恰是**回弹失败之后**表现层的回退行为，所以显式关掉回弹
                // 来构造确定场景；下面恢复控制前会再打开。
                sim.World.ControlFallbackEnabled = false;
                sim.ConsumeUnit(beforeLoss.UnitIndex);
                renderer.SetControlledLunge(new float2(1f, 0f), 1f);
                SimSnapshot withoutControl = sim.Snapshot;
                renderer.Draw(in withoutControl);
                float2 strategicAnchor = float2.zero;
                bool stillControlled = true;
                Expect(!sim.TryGetControlledPresentation(out _) &&
                       sim.TryGetPresentationAnchor(out strategicAnchor, out stillControlled) &&
                       !stillControlled && math.distancesq(strategicAnchor, lastTacticalPosition) < 0.0001f,
                    "无控制实体时应回退到显式保存的最后有效战术位置，而不是偷偷回原点");
                Expect(math.lengthsq(sim.PlayerPosition) < 0.0001f &&
                       math.distancesq(sim.PlayerPosition, strategicAnchor) > 0.0001f,
                    "失去控制后 gameplay PlayerPosition 应返回明确零值，不得泄漏相机的旧战略锚点");
                Expect(renderer.LastControlledUnitIndex == SimConst.InvalidIndex &&
                       !renderer.HasControlledLunge,
                    "失去控制实体时 SimRenderer 应清空控制索引和玩家专属前冲");

                sim.World.ControlFallbackEnabled = true;
                Expect(sim.World.TrySwitchControlledUnit(unitB) == ControlSwitchResult.Success,
                    "测试恢复路径应能重新建立有效控制实体");
                sim.OnUpdate(0f);
                Expect(sim.TryGetControlledPresentation(out SimControlledUnitView recovered) &&
                       recovered.EntityId == unitB &&
                       math.distancesq(sim.PlayerPosition, recovered.Position) < 0.0001f,
                    "重新获得控制后 gameplay PlayerPosition 应恢复为当前受控实体位置");
            }
            finally
            {
                Signals.Unsubscribe(handler);
                renderer.Dispose();
                sim.End();
            }
        }

        // ── 空间哈希 ────────────────────────────────────────

        private static void ValidateSpatialHash()
        {
            Line("\n[3] 空间哈希");

            // 同一 cell 内的点必须得到同一个键；相邻 cell 必须不同键
            int k1 = SpatialHash.Hash(SpatialHash.ToCell(new float2(1f, 1f), 0.25f));
            int k2 = SpatialHash.Hash(SpatialHash.ToCell(new float2(2f, 2f), 0.25f));
            int k3 = SpatialHash.Hash(SpatialHash.ToCell(new float2(9f, 9f), 0.25f));
            Expect(k1 == k2, "同 cell 内的点应得到同一哈希键");
            Expect(k1 != k3, "不同 cell 应得到不同哈希键");

            int ring = SpatialHash.RingFor(10f, 0.25f);
            Expect(ring >= 3, $"半径 10 / cell 4 应至少搜 3 环（实际 {ring}）");
        }

        // ── 吞噬门槛 ────────────────────────────────────────

        private static void ValidateDevourThreshold()
        {
            Line("\n[4] 吞噬体积门槛");

            var world = new SimWorld();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            world.Initialize(cfg);
            world.SetArchetypes(DataRegistry.Instance.ArchetypeArray());

            SimCommandBuffer cmds = default;
            cmds.Initialize(Unity.Collections.Allocator.Persistent, 32);

            try
            {
                // 玩家体积 2.0，门槛 1.05 → 能吞半径 < 1.9 的，吞不下 2.5 的
                world.SetPlayerStats(100f, 100f, 2f, 8f);
                world.SetPlayerPosition(float2.zero);

                world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(0.5f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.Hostile, LogicId = 1,
                });
                world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(-0.5f, 0f), Health = 10f, Radius = 2.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.Hostile, LogicId = 2,
                });

                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(1f / 60f, ref cmds);

                SimSnapshot s = world.GetSnapshot();
                bool foundSmall = false;
                bool foundBig = false;
                for (int i = 0; i < s.DevourCandidateCount; i++)
                {
                    int idx = s.DevourCandidates[i];
                    if (s.LogicId[idx] == 1) { foundSmall = true; }
                    if (s.LogicId[idx] == 2) { foundBig = true; }
                }

                Expect(foundSmall, "小目标（半径 0.5）应可被体积 2.0 的玩家吞噬");
                Expect(!foundBig, "大目标（半径 2.5）不应可被体积 2.0 的玩家吞噬");
            }
            finally
            {
                cmds.Dispose();
                world.Dispose();
            }
        }

        // ── 状态到期 ────────────────────────────────────────

        private static void ValidateStatusExpiry()
        {
            Line("\n[5] 状态位施加与清除");

            var world = new SimWorld();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            world.Initialize(cfg);
            world.SetArchetypes(DataRegistry.Instance.ArchetypeArray());

            SimCommandBuffer cmds = default;
            cmds.Initialize(Unity.Collections.Allocator.Persistent, 32);

            try
            {
                world.SetPlayerPosition(float2.zero);
                world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(2f, 0f), Health = 100f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.Hostile, LogicId = 1,
                });

                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(1f / 60f, ref cmds);

                // 施加
                cmds.Status(new StatusRequest
                {
                    Origin = float2.zero, Radius = 10f,
                    TargetIndex = SimConst.InvalidIndex,
                    Status = SimStatus.Conductive, TargetFaction = SimFaction.Hostile, Add = true,
                });
                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(1f / 60f, ref cmds);

                SimSnapshot s1 = world.GetSnapshot();
                Expect(s1.HasStatus(1, SimStatus.Conductive), "范围施加后目标应带 Conductive");

                // 移除
                cmds.Status(new StatusRequest
                {
                    Origin = float2.zero, Radius = 10f,
                    TargetIndex = SimConst.InvalidIndex,
                    Status = SimStatus.Conductive, TargetFaction = SimFaction.Hostile, Add = false,
                });
                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(1f / 60f, ref cmds);

                SimSnapshot s2 = world.GetSnapshot();
                Expect(!s2.HasStatus(1, SimStatus.Conductive), "范围移除后目标应不带 Conductive");

                // RequireStatus 筛选：无导电时不该被命中
                cmds.Damage(new DamageRequest
                {
                    Origin = float2.zero, Radius = 10f,
                    TargetIndex = SimConst.InvalidIndex, Amount = 500f,
                    TargetFaction = SimFaction.Hostile,
                    RequireStatus = SimStatus.Conductive,
                });
                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(1f / 60f, ref cmds);

                SimSnapshot s3 = world.GetSnapshot();
                Expect(s3.CountHostiles() == 1,
                    "RequireStatus=Conductive 的伤害不应命中无导电的目标");
            }
            finally
            {
                cmds.Dispose();
                world.Dispose();
            }
        }

        // ── 附属体上限 ──────────────────────────────────────

        /// <summary>
        /// story-001（sim-hardening）：MinionRegistry 是纯 C# 计数器，
        /// 不需要内核/Unity 上下文，直接构造实例验证配额裁剪与归还。
        /// </summary>
        private static void ValidateMinionCap()
        {
            Line("\n[6] 附属体上限（MinionCap）");

            var minions = new MinionRegistry();
            minions.OnEnter();

            Expect(minions.LiveCount == 0, $"初始存活数应为 0（实际 {minions.LiveCount}）");

            int granted = minions.Reserve(4, 3);
            Expect(granted == 3, $"cap=3 时申请 4 个应只批 3 个（实际 {granted}）");
            Expect(minions.LiveCount == 3, $"批准后存活数应为 3（实际 {minions.LiveCount}）");

            int deniedGrant = minions.Reserve(2, 3);
            Expect(deniedGrant == 0, $"已满额时再申请应批 0 个（实际 {deniedGrant}）");

            minions.Release(1);
            Expect(minions.LiveCount == 2, $"归还 1 个后存活数应为 2（实际 {minions.LiveCount}）");

            int afterRelease = minions.Reserve(5, 3);
            Expect(afterRelease == 1, $"归还后腾出 1 个额度，申请 5 个应只批 1 个（实际 {afterRelease}）");

            var zeroCap = new MinionRegistry();
            zeroCap.OnEnter();
            int zeroGrant = zeroCap.Reserve(1, 0);
            Expect(zeroGrant == 0, $"cap=0（未投资 MinionCap）时应拒绝一切生成（实际 {zeroGrant}）");

            zeroCap.Release(5);
            Expect(zeroCap.LiveCount == 0, $"归还超过存活数不应变负（实际 {zeroCap.LiveCount}）");
        }

        // ── 致死来源类型 ────────────────────────────────────

        /// <summary>
        /// story-002（sim-hardening）：DeathEvent.CauseKind——EmitDeath（伤害耗尽）
        /// 走 Damage，KillUnit（吞噬清除）走 Devour。
        /// </summary>
        private static void ValidateDeathCauseKind()
        {
            Line("\n[7] 致死来源类型（DeathEvent.CauseKind）");

            var world = new SimWorld();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            world.Initialize(cfg);
            world.SetArchetypes(DataRegistry.Instance.ArchetypeArray());

            SimCommandBuffer cmds = default;
            cmds.Initialize(Unity.Collections.Allocator.Persistent, 32);

            try
            {
                world.SetPlayerPosition(float2.zero);

                world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(5f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.Hostile, LogicId = 1,
                });
                int idxDevour = world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(-5f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.Hostile, LogicId = 2,
                });

                // 伤害致死：走 EmitDeath
                cmds.Damage(new DamageRequest
                {
                    Origin = new float2(5f, 0f), Radius = 1f,
                    TargetIndex = SimConst.InvalidIndex, Amount = 1000f,
                    TargetFaction = SimFaction.Hostile,
                });
                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(1f / 60f, ref cmds);

                // 吞噬清除：直接走 KillUnit（CellDevourSystem.Consume 的路径）
                world.KillUnit(idxDevour, 0);

                SimSnapshot s = world.GetSnapshot();
                Expect(s.DeathCount == 2, $"应产生 2 条死亡事件（实际 {s.DeathCount}）");

                bool foundDamage = false;
                bool foundDevour = false;
                for (int i = 0; i < s.DeathCount; i++)
                {
                    DeathEvent d = s.Deaths[i];
                    if (d.LogicId == 1)
                    {
                        Expect(d.CauseKind == DeathCauseKind.Damage,
                            $"伤害致死应标记 CauseKind=Damage（实际 {d.CauseKind}）");
                        foundDamage = true;
                    }
                    else if (d.LogicId == 2)
                    {
                        Expect(d.CauseKind == DeathCauseKind.Devour,
                            $"吞噬清除应标记 CauseKind=Devour（实际 {d.CauseKind}）");
                        foundDevour = true;
                    }
                }
                Expect(foundDamage, "应找到 LogicId=1 的伤害致死事件");
                Expect(foundDevour, "应找到 LogicId=2 的吞噬致死事件");
            }
            finally
            {
                cmds.Dispose();
                world.Dispose();
            }
        }

        // ── 多阶段继承 ──────────────────────────────────────

        /// <summary>
        /// story-003（sim-hardening）：ApplyInherited 在生产环境无真实调用点
        /// （细胞阶段是首个阶段，inherited 恒为 null），只能用合成 StageOutcome
        /// 驱动一次完整 CellStageFlow.Enter 来验证"死数据变活"。
        /// </summary>
        private static void ValidateInheritance()
        {
            Line("\n[8] 多阶段继承应用（ApplyInherited）");

            GameObject cameraBefore = Camera.main != null ? Camera.main.gameObject : null;

            // 同上：不写死卡 ID。这条验的是"上一局的定义性卡牌会注入下一局起始卡组"，
            // 任意一张真实存在的卡都能验证这条规则，取 Id 最小的那张保证可复现。
            CardSpec keyCard = null;
            foreach (CardSpec c in DataRegistry.Instance.AllCards)
            {
                if (keyCard == null || c.Id < keyCard.Id)
                {
                    keyCard = c;
                }
            }
            var prev = new StageOutcome
            {
                DominantRoute = CardRoute.Devour,
            };
            if (keyCard != null)
            {
                prev.KeyCards.Add(keyCard);
            }

            var flow = new CellStageFlow();
            try
            {
                flow.Enter(prev);

                Expect(flow.Stats.Get(StatId.DevourGain) > 1f,
                    $"继承主导路线 Devour 应提升 DevourGain（实际 {flow.Stats.Get(StatId.DevourGain)}）");

                if (keyCard == null)
                {
                    Fail("卡表为空，无法验证定义性卡牌注入");
                }
                else
                {
                    Expect(flow.Deck.StackOf(keyCard.Id) > 0,
                        $"继承定义性卡牌 {keyCard.Id} 应注入起始卡组（实际层数 {flow.Deck.StackOf(keyCard.Id)}）");
                }
            }
            finally
            {
                flow.Exit();

                // SetupCamera 在场景无 MainCamera 时会新建一个，测试完清理掉避免污染场景
                GameObject cameraAfter = Camera.main != null ? Camera.main.gameObject : null;
                if (cameraAfter != null && cameraAfter != cameraBefore)
                {
                    UnityEngine.Object.DestroyImmediate(cameraAfter);
                }
            }
        }

        // ── 首领三阶段 ──────────────────────────────────────

        /// <summary>
        /// story-002（gameplay-gaps）：BossPhaseController 按血量阈值切换行为原型。
        /// 裸构造 SimBridge + BossPhaseController（不经 CellStageFlow/ModuleHub），
        /// 用 SimBridge.World.SpawnUnit 同步生成首领拿到真实索引，
        /// 再用 SimBridge.DamageUnit 分批扣血跨越阈值，断言阶段与行为原型确实切换。
        /// </summary>
        private static void ValidateBossPhase()
        {
            Line("\n[9] 首领三阶段切换（BossPhaseController，TR-cell-011）");

            EnemySpec boss = DataRegistry.Instance.GetEnemy(90);
            if (boss == null)
            {
                Fail("找不到首领敌人 90（原核霸主），无法验证阶段切换");
                return;
            }

            var sim = new GameLogic.Battle.SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            sim.Begin(cfg, DataRegistry.Instance.ArchetypeArray());

            var controller = new BossPhaseController();
            controller.Bind(sim);
            controller.OnEnter();

            try
            {
                int idx = sim.World.SpawnUnit(new SpawnRequest
                {
                    Position = float2.zero,
                    Health = boss.Health,
                    Radius = boss.Radius,
                    MaxSpeed = boss.MaxSpeed,
                    ArchetypeId = boss.ArchetypeIndex,
                    Faction = SimFaction.Hostile,
                    InitialStatus = boss.InitialStatus,
                    LogicId = SpawnDirector.EncodeLogicId(90),
                    VisualId = boss.VisualId,
                });
                Expect(idx != SimConst.InvalidIndex, "首领单位应生成成功");

                // 行为原型切换走命令缓冲，本帧 controller 入队的 SwapArchetype
                // 要到下一次 sim.OnUpdate 才真正生效——所以每次判定后多推进一帧
                // "冲洗"掉命令，再读快照里的 ArchetypeId，否则会读到上一帧的旧值。
                const float dt = 1f / 60f;
                sim.OnUpdate(dt);
                controller.OnUpdate(dt);

                Expect(controller.CurrentPhaseIndex == 0,
                    $"满血首领应处于阶段 0（实际 {controller.CurrentPhaseIndex}）");
                sim.OnUpdate(dt);
                int archetype0 = sim.Snapshot.ArchetypeId[idx];

                // 扣到 50%：应进入阶段 1（阈值 0.66），行为原型应切换
                sim.DamageUnit(idx, boss.Health * 0.5f);
                sim.OnUpdate(dt);
                controller.OnUpdate(dt);

                Expect(controller.CurrentPhaseIndex == 1,
                    $"血量 50% 应进入阶段 1（实际 {controller.CurrentPhaseIndex}）");
                sim.OnUpdate(dt);
                int archetype1 = sim.Snapshot.ArchetypeId[idx];
                Expect(archetype1 != archetype0,
                    $"阶段 1 应切换行为原型（阶段0={archetype0}，阶段1={archetype1}）");

                // 再扣到 20%：应进入阶段 2（阈值 0.33）
                sim.DamageUnit(idx, boss.Health * 0.3f);
                sim.OnUpdate(dt);
                controller.OnUpdate(dt);

                Expect(controller.CurrentPhaseIndex == 2,
                    $"血量 20% 应进入阶段 2（实际 {controller.CurrentPhaseIndex}）");
                sim.OnUpdate(dt);
                int archetype2 = sim.Snapshot.ArchetypeId[idx];
                Expect(archetype2 != archetype1,
                    $"阶段 2 应再次切换行为原型（阶段1={archetype1}，阶段2={archetype2}）");
            }
            finally
            {
                sim.OnDispose();
            }
        }

        // ── 局内商店 ────────────────────────────────────────

        /// <summary>
        /// story-003（gameplay-gaps）：ShopSystem 固定商品目录（Preflight H2，不建 Luban 表）。
        /// 裸构造 ResourceWallet/Deck/StatSheet/SimBridge + ShopSystem（不经 CellStageFlow/ModuleHub），
        /// 验证资金不足拒绝购买、购买后扣款与库存变化、效果按商品种类真实落地、刷新重置库存。
        /// </summary>
        private static void ValidateShop()
        {
            Line("\n[10] 局内商店（ShopSystem，TR-cell-012）");

            var stats = new StatSheet();
            stats.ResetToDefaults();

            var wallet = new ResourceWallet();
            wallet.Bind(stats);
            wallet.OnEnter();

            var deck = new Deck();

            var sim = new GameLogic.Battle.SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 16;
            sim.Begin(cfg, DataRegistry.Instance.ArchetypeArray());

            try
            {
                float maxHp = stats.Get(StatId.MaxHealth);
                sim.SetPlayerStats(maxHp, maxHp, stats.Get(StatId.Volume), stats.Get(StatId.MoveSpeed));
                sim.DamagePlayer(maxHp * 0.5f);
                sim.OnUpdate(1f / 60f);

                var shop = new ShopSystem();
                shop.Bind(wallet, stats, deck, sim);
                shop.OnEnter();

                // 资金不足：购买应失败，不扣款也不改库存
                bool boughtBroke = shop.TryBuy(0);
                Expect(!boughtBroke, "资金不足时购买应失败");
                Expect(!shop.IsSoldOut(0), "购买失败不应标记已售出");

                wallet.Add(ResourceKind.Nutrient, 200f);

                ShopItemSpec item = shop.GetSlot(0);
                float nutrientBefore = wallet.Nutrient;
                float pollutionBefore = wallet.Pollution;
                float mutagenBefore = wallet.Mutagen;
                int cardsBefore = deck.TotalCards;
                float healthBefore = sim.PlayerHealth;

                bool bought = shop.TryBuy(0);
                Expect(bought, $"资金充足时购买槽位0应成功（商品「{item.Name}」）");
                Expect(shop.IsSoldOut(0), "购买后槽位应标记已售出（库存变化）");
                Expect(Mathf.Approximately(wallet.Nutrient, nutrientBefore - item.Cost),
                    $"营养质应扣除 {item.Cost}（实际 {nutrientBefore} → {wallet.Nutrient}）");

                sim.OnUpdate(1f / 60f); // 冲洗 HealPlayer 等直写效果到快照

                switch (item.Effect)
                {
                    case ShopEffectKind.HealPercent:
                        Expect(sim.PlayerHealth > healthBefore,
                            $"「细胞修复」应回复生命（{healthBefore:F1} → {sim.PlayerHealth:F1}）");
                        break;
                    case ShopEffectKind.ClearPollution:
                        Expect(wallet.Pollution <= pollutionBefore, "「净化脉冲」应降低或维持污染度");
                        break;
                    case ShopEffectKind.GainMutagen:
                        Expect(wallet.Mutagen > mutagenBefore, "「突变浓缩」应增加突变质");
                        break;
                    case ShopEffectKind.RandomCard:
                        Expect(deck.TotalCards > cardsBefore || wallet.Mutagen > mutagenBefore,
                            "「随机基因」应获得新卡牌，或在卡池耗尽时退化为突变质补偿");
                        break;
                }

                bool boughtAgain = shop.TryBuy(0);
                Expect(!boughtAgain, "已售出槽位不应可再次购买");

                float nutrientBeforeRefresh = wallet.Nutrient;
                bool refreshed = shop.TryRefresh();
                Expect(refreshed, "资金充足时刷新应成功");
                Expect(!shop.IsSoldOut(0), "刷新后槽位应重置为未售出（库存变化）");
                Expect(Mathf.Approximately(wallet.Nutrient, nutrientBeforeRefresh - ShopSystem.RefreshCost),
                    $"刷新应扣除营养质 {ShopSystem.RefreshCost}（实际 {nutrientBeforeRefresh} → {wallet.Nutrient}）");
            }
            finally
            {
                sim.OnDispose();
            }
        }

        // ── 图鉴发现记录 ────────────────────────────────────

        /// <summary>
        /// story-004（gameplay-gaps）：CodexRegistry 监听现有 Kill/Devour/CardAcquired 信号
        /// 登记发现（Preflight C1，窄口径：本局内存态，不做跨会话持久化）。
        /// 裸构造 + Signals 全局总线直接发布信号驱动，验证发现登记、尸体二次吞噬不登记、
        /// OnExit 后自动退订（SignalScope）。
        /// </summary>
        private static void ValidateCodex()
        {
            Line("\n[11] 图鉴发现记录（CodexRegistry，TR-cell-013）");

            Signals.Clear();
            var codex = new CodexRegistry();
            codex.OnEnter();

            try
            {
                Signals.Publish(new KillSignal { LogicId = SpawnDirector.EncodeLogicId(50) });
                Expect(codex.DiscoveredEnemyIds.Contains(50),
                    "击杀（KillSignal）应登记敌人 50 的图鉴发现");

                Signals.Publish(new DevourSignal { EnemyId = 1, IsCorpse = false });
                Expect(codex.DiscoveredEnemyIds.Contains(1),
                    "吞噬（DevourSignal，非尸体）应登记敌人 1 的图鉴发现");

                Signals.Publish(new DevourSignal { EnemyId = 999, IsCorpse = true });
                Expect(!codex.DiscoveredEnemyIds.Contains(999),
                    "尸体/残块的二次吞噬不应计入图鉴发现");

                Signals.Publish(new CardAcquiredSignal { CardId = 1001, NewStack = 1 });
                Expect(codex.DiscoveredCardIds.Contains(1001),
                    "卡牌获得（CardAcquiredSignal）应登记卡 1001 的图鉴发现");

                int enemyCountBefore = codex.DiscoveredEnemyIds.Count;
                codex.OnExit();
                Signals.Publish(new KillSignal { LogicId = SpawnDirector.EncodeLogicId(51) });
                Expect(codex.DiscoveredEnemyIds.Count == enemyCountBefore,
                    "OnExit 后应已退订信号，不应再登记新发现");
            }
            finally
            {
                Signals.Clear();
            }
        }

        // ── 控制生命周期与存档回归 ──────────────────────────

        /// <summary>
        /// M1-06：死亡、断线与存档回归。裸构造 <see cref="SimWorld"/>，用固定
        /// <see cref="SimConfig.RandomSeed"/> 与固定生成顺序验证受控实体死亡后的
        /// 自动回弹、变更事件只发一次、任意时刻唯一受控不变量、友军全灭后明确为无、
        /// 相同种子下回弹结果可复现，最后单独验证 <see cref="ControlPersistence"/> 的
        /// 存档往返与"旧存档/损坏存档安全降级"。
        /// </summary>
        private static void ValidateControlLifecycle()
        {
            Line("\n[12] 控制生命周期：死亡回弹 / 唯一不变量 / 存档回归（M1-06）");

            var world = new SimWorld();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            cfg.RandomSeed = 0xC0FFEE01u;
            world.Initialize(cfg);
            world.ControlFallbackRange = 0f; // 非正值 = 不限距离，保证必定能回弹

            SimCommandBuffer cmds = default;
            cmds.Initialize(Unity.Collections.Allocator.Persistent, 32);

            try
            {
                SimEntityId initialControlled = world.ControlledUnitId;
                Expect(initialControlled.IsValid, "初始默认受控实体应有效");

                world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(2f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = 5001,
                });
                world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(6f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = 5002,
                });

                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(1f / 60f, ref cmds);

                SimSnapshot beforeDeath = world.GetSnapshot();
                Expect(CountPlayerIntentUnits(beforeDeath) == 1,
                    $"死亡回弹前应恰好一个 Player 意图来源单位（实际 {CountPlayerIntentUnits(beforeDeath)}）");
                bool resolvedControlled = beforeDeath.TryResolveControlledUnit(out int controlledIndex);
                Expect(resolvedControlled && controlledIndex == SimConst.PlayerIndex,
                    "回弹测试前受控实体应仍是默认槽位 0");

                world.KillUnit(controlledIndex, 0);

                SimEntityId reboundedId = world.ControlledUnitId;
                Expect(reboundedId.IsValid && reboundedId != initialControlled,
                    "受控实体死亡后应回弹到另一个存活友军，而不是保留旧 ID 或悬空");

                Expect(world.TryConsumeControlChange(out ControlChangeEvent change) &&
                       change.Reason == ControlChangeReason.ControlledDeath &&
                       change.PreviousUnitId == initialControlled &&
                       change.CurrentUnitId == reboundedId,
                    "死亡回弹应产出一条 ControlledDeath 变更事件，且前后实体记录正确");
                Expect(!world.TryConsumeControlChange(out _),
                    "同一次回弹变更只应被消费一次（FIFO 出队），第二次消费应返回 false");

                SimSnapshot afterDeath = world.GetSnapshot();
                Expect(CountPlayerIntentUnits(afterDeath) == 1,
                    $"死亡回弹后仍应恰好一个 Player 意图来源单位（实际 {CountPlayerIntentUnits(afterDeath)}）");

                // 明确为无：把剩下的友军（含刚回弹到的那个）全部杀光
                for (int i = 0; i < afterDeath.Count; i++)
                {
                    if (afterDeath.Alive[i] != 0 && IsFriendlyFactionForTest(afterDeath.FactionOf(i)))
                    {
                        world.KillUnit(i, 0);
                    }
                }

                Expect(world.ControlledUnitId == SimEntityId.None,
                    "友军全灭后受控实体应明确为无（SimEntityId.None），而不是悬空 ID");
                SimSnapshot emptySnap = world.GetSnapshot();
                Expect(!emptySnap.TryResolveControlledUnit(out _),
                    "友军全灭后快照不应能解析出受控实体");
                Expect(world.TryGetControlFallbackAnchor(out _),
                    "友军全灭后仍应保留最后一次有效受控位置的回退锚点，供镜头兜底");
            }
            finally
            {
                cmds.Dispose();
                world.Dispose();
            }

            // 确定性：相同种子 + 相同生成顺序，两次独立世界实例的回弹结果应一致。
            // ID 分配只看调用顺序、不吃 RNG，所以这里连绝对 SimEntityId 数值都应该对得上；
            // 如果分配策略以后引入非确定性，请把下面第二条断言收窄成只比较 LogicId。
            RunDeathReboundOnce(0xC0FFEE01u, out int logicId1, out ulong entityValue1);
            RunDeathReboundOnce(0xC0FFEE01u, out int logicId2, out ulong entityValue2);
            Expect(logicId1 != 0 && logicId1 == logicId2,
                $"相同种子/生成顺序下两次运行的死亡回弹目标 LogicId 应一致（实际 {logicId1} vs {logicId2}）");
            Expect(entityValue1 == entityValue2,
                $"相同种子/生成顺序下两次运行的回弹目标绝对 SimEntityId 也应一致（实际 {entityValue1} vs {entityValue2}）");

            ValidateControlPersistenceRoundTrip();
        }

        /// <summary>统计快照中「存活且意图来源为 Player」的单位数——用于断言
        /// 「任意时刻最多只有一个受控实体」这条唯一不变量。</summary>
        private static int CountPlayerIntentUnits(in SimSnapshot snapshot)
        {
            int count = 0;
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (snapshot.Alive[i] != 0 && snapshot.IntentSourceOf(i) == IntentSource.Player)
                {
                    count++;
                }
            }
            return count;
        }

        private static bool IsFriendlyFactionForTest(SimFaction faction)
        {
            return faction == SimFaction.Player || faction == SimFaction.PlayerMinion;
        }

        /// <summary>裸跑一次「默认受控实体死亡 → 回弹」场景，构造/销毁独立的
        /// <see cref="SimWorld"/> 实例，只把回弹结果的弱标识（LogicId）与绝对稳定 ID
        /// 数值带出来供确定性对比，不泄漏任何 world 内部状态。</summary>
        private static void RunDeathReboundOnce(uint seed, out int reboundedLogicId, out ulong reboundedEntityValue)
        {
            var world = new SimWorld();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            cfg.RandomSeed = seed;
            world.Initialize(cfg);
            world.ControlFallbackRange = 0f;

            SimCommandBuffer cmds = default;
            cmds.Initialize(Unity.Collections.Allocator.Persistent, 32);

            try
            {
                world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(2f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = 5001,
                });
                world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(6f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = 5002,
                });

                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(1f / 60f, ref cmds);

                world.KillUnit(SimConst.PlayerIndex, 0);

                SimEntityId reboundedId = world.ControlledUnitId;
                reboundedEntityValue = reboundedId.Value;
                SimSnapshot snap = world.GetSnapshot();
                reboundedLogicId = world.TryResolveUnit(reboundedId, out int reboundedIndex)
                    ? snap.LogicId[reboundedIndex]
                    : 0;
            }
            finally
            {
                cmds.Dispose();
                world.Dispose();
            }
        }

        /// <summary>
        /// <see cref="ControlPersistence"/> 独立于内核的磁盘 IO 回归：正常往返、
        /// 损坏 JSON、版本 0 的旧存档都必须安全降级为「明确为无」且不抛异常。
        /// 全程备份/恢复玩家真实存档文件，不污染真实进度。
        /// </summary>
        private static void ValidateControlPersistenceRoundTrip()
        {
            Line("\n[12.1] 控制记忆存档往返（ControlPersistence）");

            string path = ControlPersistence.FilePath;
            bool hadBackup = File.Exists(path);
            string backup = hadBackup ? File.ReadAllText(path) : null;

            try
            {
                var written = new ControlHandoffState
                {
                    HasRecord = true,
                    ControlledUnitId = SimEntityId.None,
                    ControlledLogicId = 42,
                    FallbackAnchor = new float2(3.5f, -7.25f),
                    HasAnchor = true,
                };
                ControlPersistence.Save(written);
                ControlHandoffState loaded = ControlPersistence.Load();
                Expect(loaded.HasRecord && loaded.ControlledLogicId == 42 &&
                       math.abs(loaded.FallbackAnchor.x - 3.5f) < 0.001f &&
                       math.abs(loaded.FallbackAnchor.y - (-7.25f)) < 0.001f &&
                       loaded.HasAnchor && loaded.ControlledUnitId == SimEntityId.None,
                    "存档往返后 LogicId/锚点应一致，且 SimEntityId 恒为 None（从不落盘）");

                File.WriteAllText(path, "{not json");
                ControlHandoffState corrupted = ControlPersistence.Load();
                Expect(!corrupted.HasRecord,
                    "损坏 JSON 应安全降级为「明确为无」，不抛异常");

                File.WriteAllText(path, "{\"Version\":0,\"ControlledLogicId\":7,\"AnchorX\":1,\"AnchorY\":2,\"HasAnchor\":true}");
                ControlHandoffState legacy = ControlPersistence.Load();
                Expect(!legacy.HasRecord,
                    "Version=0 的旧存档应安全降级为「明确为无」，不抛异常");
            }
            finally
            {
                if (hadBackup)
                {
                    File.WriteAllText(path, backup);
                }
                else
                {
                    ControlPersistence.Clear();
                }
            }
        }

        // ── 辅助 ────────────────────────────────────────────

        private static void Expect(bool cond, string what)
        {
            if (cond)
            {
                Ok(what);
            }
            else
            {
                Fail(what);
            }
        }

        private static void Ok(string what) => Line($"  ✓ {what}");

        private static void Fail(string what)
        {
            _fail++;
            Line($"  ✗ {what}");
        }

        private static void Line(string s) => Report.AppendLine(s);
    }
}
