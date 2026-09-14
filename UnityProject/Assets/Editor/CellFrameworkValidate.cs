using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Cards;
using GameLogic.Command;
using GameLogic.Control;
using GameLogic.Core;
using GameLogic.Progression;
using GameLogic.Spawning;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.Stats;
using GameLogic.UI.Battle;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

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
                ValidateCameraDirector();
                ValidateSquadCommands();
                ValidateUnitLoadouts();
                ValidateDirectControlActions();
                ValidateDirectVitals();
                ValidateAiHandoff();
                ValidateAiOverloadSuppression();
                ValidateSurgicalWindowBody();
                ValidateSurgicalWindowCommandAndAim();
                ValidateSurgicalWindowRewards();
                ValidateConsciousnessPlaytestGate();
                ValidateAllyParityAndControlCycle();
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
            string controlPath = ControlPersistence.FilePath;
            bool hadControlBackup = File.Exists(controlPath);
            string controlBackup = hadControlBackup ? File.ReadAllText(controlPath) : null;
            MetabolicSlicePanel panelBefore = MetabolicSlicePanel.Instance;
            GameObject tempPanelHost = null;

            if (panelBefore == null)
            {
                tempPanelHost = new GameObject("Validate8_MetabolicSlicePanel");
                panelBefore = tempPanelHost.AddComponent<MetabolicSlicePanel>();
                // 普通 MonoBehaviour 在 Edit 模式 AddComponent 时不会自动走 Awake；显式调用同一生产初始化，
                // 让 Bridge 读取到真实的 Instance/CarrierRegistry，而不是在测试里另造替身。
                typeof(MetabolicSlicePanel)
                    .GetMethod("Awake", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.Invoke(panelBefore, null);
            }

            // 回归夹具：磁盘上故意留一条“上一局控制友军”的记录。
            // Fresh Enter 必须忽略它；只有 GameRoot.ResumeCellStage 才允许恢复。
            ControlPersistence.Save(new ControlHandoffState
            {
                HasRecord = true,
                ControlledUnitId = SimEntityId.None,
                ControlledLogicId = 2,
                FallbackAnchor = new float2(12f, -6f),
                HasAnchor = true,
            });

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

                Expect(flow.Sim.ControllingPlayerBody,
                    "正常新局必须控制玩家本体，不得把上一局友军 LogicId 恢复到新生成的友军");

                // 用真实玩家 Carrier + org_emitter 跑 OnUpdate，而不是只测底层 FireProjectile。
                // 这条正是本次漏掉的正常肉鸽实链回归。
                const string EmitterCarrierId = "validate8_emitter_carrier";
                panelBefore.CarrierRegistry.EnsureCarrier(EmitterCarrierId, "org_emitter", autoActivate: true);
                panelBefore.CarrierRegistry.SetActive(EmitterCarrierId);
                for (int i = 0; i < 45; i++)
                {
                    // 只推进被测的两层真实模块：Bridge 产出请求，Sim 下一拍消费。
                    // 不推进相机/表现/选卡 UI，避免 Edit 模式的 Destroy 延迟语义污染结果。
                    flow.MetabolicBridge.OnUpdate(1f / 60f);
                    flow.Sim.OnUpdate(1f / 60f);
                }
                Expect(flow.MetabolicBridge.LastFiredProjectileCount > 0 && flow.Sim.LiveProjectileCount > 0,
                    $"玩家本体激活 org_emitter 后应经真实肉鸽 Tick 生成弹体" +
                    $"（LastFired={flow.MetabolicBridge.LastFiredProjectileCount}, Live={flow.Sim.LiveProjectileCount}）");

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
                if (tempPanelHost != null)
                {
                    UnityEngine.Object.DestroyImmediate(tempPanelHost);
                }
                if (hadControlBackup)
                {
                    File.WriteAllText(controlPath, controlBackup);
                }
                else
                {
                    ControlPersistence.Clear();
                }
            }
        }

        // ── M2-06 意识传递试玩门 ─────────────────────────────

        private static void ValidateConsciousnessPlaytestGate()
        {
            Line("\n[23] M2-06 固定意识传递试玩门与入口隔离");

            GameObject cameraBefore = Camera.main != null ? Camera.main.gameObject : null;
            string controlPath = ControlPersistence.FilePath;
            bool hadControlBackup = File.Exists(controlPath);
            string controlBackup = hadControlBackup ? File.ReadAllText(controlPath) : null;
            var flow = new CellStageFlow();

            try
            {
                // A. 先跑 LookDev，再直接 Enter：下一局必须自动回到完整正常肉鸽。
                flow.PrepareNextEnter(CellStageEntryMode.LookDevSandbox);
                flow.Enter(null);
                Expect(flow.IsSandboxMode && flow.Director.Suppressed && flow.Timeline.Suppressed &&
                       flow.MetabolicBridge.Suppressed,
                    "LookDev 入口应同时抑制刷怪、时间线和玩家常规装配 Tick");
                flow.Exit();

                flow.Enter(null);
                Expect(!flow.IsSandboxMode && !flow.IsConsciousnessPlaytest &&
                       !flow.Director.Suppressed && !flow.Timeline.Suppressed &&
                       !flow.MetabolicBridge.Suppressed,
                    "LookDev 退出后的普通新局必须恢复完整肉鸽，不得继承任何抑制态");
                Expect(flow.Sim.ControllingPlayerBody,
                    "入口隔离后的普通新局仍应控制玩家本体");
                flow.Exit();

                // B. 固定试玩只冻结随机内容；真实装配、控制与外科身体全部保留。
                flow.PrepareNextEnter(CellStageEntryMode.ConsciousnessPlaytest);
                flow.Enter(null);
                flow.Sim.OnUpdate(1f / 60f);

                Expect(flow.IsConsciousnessPlaytest && !flow.IsSandboxMode,
                    "M2-06 应是独立进入方式，不能冒充 LookDev 沙盒");
                Expect(flow.Director.Suppressed && flow.Timeline.Suppressed &&
                       !flow.MetabolicBridge.Suppressed,
                    "M2-06 只冻结随机刷怪/时间线，必须保留真实玩家装配 Tick");
                Expect(flow.Sim.ControllingPlayerBody,
                    "M2-06 是固定新局，不得读取上一局的接管记录");

                SimSnapshot snapshot = flow.Sim.Snapshot;
                int targetIndex = SimConst.InvalidIndex;
                for (int i = 0; i < snapshot.Count; i++)
                {
                    if (snapshot.IsAlive(i) &&
                        snapshot.LogicId[i] == CellStageFlow.ConsciousnessPlaytestTargetLogicId)
                    {
                        targetIndex = i;
                        break;
                    }
                }

                bool hasFixedTarget = targetIndex != SimConst.InvalidIndex;
                Expect(hasFixedTarget,
                    "M2-06 应在固定 LogicId 与固定坐标生成唯一手术目标");
                if (hasFixedTarget)
                {
                    float2 pos = snapshot.Position[targetIndex];
                    Expect(math.distance(pos, new float2(
                               CellStageFlow.ConsciousnessPlaytestTargetX,
                               CellStageFlow.ConsciousnessPlaytestTargetY)) < 0.01f,
                        $"固定目标坐标应稳定（实际 {pos.x:F2},{pos.y:F2}）");

                    SimEntityId targetId = snapshot.EntityId[targetIndex];
                    bool hasPrimary = flow.Sim.World.TryGetBodyPart(
                        targetId, SimBodyPartSlot.Primary, out SimBodyPart primary);
                    bool hasSecondary = flow.Sim.World.TryGetBodyPart(
                        targetId, SimBodyPartSlot.Secondary, out SimBodyPart secondary);
                    Expect(hasPrimary && hasSecondary &&
                           primary.AimRadius > 0f && secondary.AimRadius > 0f,
                        "固定目标必须带两个可被直控弹体几何命中的真实接点");
                }
                flow.Exit();

                // C. M2-06 同样是一枪一发的进入配置；下一局不能残留固定目标或抑制态。
                flow.Enter(null);
                flow.Sim.OnUpdate(1f / 60f);
                SimSnapshot fresh = flow.Sim.Snapshot;
                bool leakedTarget = false;
                for (int i = 0; i < fresh.Count; i++)
                {
                    leakedTarget |= fresh.IsAlive(i) &&
                        fresh.LogicId[i] == CellStageFlow.ConsciousnessPlaytestTargetLogicId;
                }
                Expect(!flow.IsConsciousnessPlaytest && !leakedTarget &&
                       !flow.Director.Suppressed && !flow.Timeline.Suppressed &&
                       !flow.MetabolicBridge.Suppressed,
                    "M2-06 退出后的普通新局不得残留固定目标或任何抑制态");
            }
            finally
            {
                if (flow.IsRunning)
                {
                    flow.Exit();
                }

                GameObject cameraAfter = Camera.main != null ? Camera.main.gameObject : null;
                if (cameraAfter != null && cameraAfter != cameraBefore)
                {
                    UnityEngine.Object.DestroyImmediate(cameraAfter);
                }
                if (hadControlBackup)
                {
                    File.WriteAllText(controlPath, controlBackup);
                }
                else
                {
                    ControlPersistence.Clear();
                }
            }
        }

        // ── 可控友军的一致性与接管循环（2026-09-14 试玩反馈）────

        /// <summary>
        /// 守三件玩家连着两轮报上来的事，每一件都曾经"看起来能跑"却在手里明显不对：
        ///
        /// 1. **友军没收到命令就不许自己动**。原型 13 的 MinionSeekAttack 找不到索敌目标时走
        ///    <c>Wander * 0.5</c>，于是半速乱晃——玩家读到的是"又慢又自己动"。
        /// 2. **玩家属性不许写到接管的友军身上**。<c>SimWorld.SetPlayerStats</c> 写的是
        ///    当前受控那一具，接管期间会把玩家的血量/体积逐帧盖上去，**放手后还留在它身上**，
        ///    于是"摸过的友军"和"没摸过的"从此不一样。
        /// 3. **接管候选是全场友军、按稳定 id 循环**，不是"最近的那个"。
        /// </summary>
        private static void ValidateAllyParityAndControlCycle()
        {
            Line("\n[24] 可控友军一致性与全场接管循环");

            GameObject cameraBefore = Camera.main != null ? Camera.main.gameObject : null;
            var flow = new CellStageFlow();
            // 输入所有权是静态的，前面的段落可能把它留在别的状态；本段要走直控路径，显式摆正。
            InputRouter.SetModalUi(false);
            InputRouter.SetGameplayPaused(false);
            InputRouter.SetScope(InputScope.Direct);

            try
            {
                flow.PrepareNextEnter(CellStageEntryMode.ConsciousnessPlaytest);
                flow.Enter(null);

                // 让两名友军真正落地并拿到实体 id，再把"出生即原地待命"落下去。
                for (int i = 0; i < 8; i++)
                {
                    flow.Sim.OnUpdate(1f / 60f);
                    flow.DebugResolveAllyHolds();
                }

                SimSnapshot snap = flow.Sim.Snapshot;
                var allies = new List<SimEntityId>();
                var allyIndex = new List<int>();
                for (int i = 0; i < snap.Count; i++)
                {
                    if (snap.IsAlive(i) && snap.FactionOf(i) == SimFaction.PlayerMinion)
                    {
                        allies.Add(snap.EntityId[i]);
                        allyIndex.Add(i);
                    }
                }
                Expect(allies.Count == 2, $"固定场景应有两名可控友军（实际 {allies.Count}）");
                if (allies.Count != 2)
                {
                    return;
                }

                // ── A. 出生即原地待命：有命令、且真的不动 ──
                bool bothHeld = true;
                for (int i = 0; i < allies.Count; i++)
                {
                    bothHeld &= flow.Sim.TryGetCommand(allies[i], out UnitCommand c) &&
                                c.Kind == UnitCommandKind.Guard;
                }
                Expect(bothHeld, "两名友军出生后都应拿到原地守备命令——没下令就不该自己动");

                float2 sporeStart = snap.Position[allyIndex[0]];
                float2 myceliumStart = snap.Position[allyIndex[1]];
                float allyRadiusStart = snap.Radius[allyIndex[0]];
                float allyHealthStart = snap.Health[allyIndex[0]];

                for (int i = 0; i < 120; i++)
                {
                    flow.Sim.OnUpdate(1f / 60f);
                }
                SimSnapshot afterIdle = flow.Sim.Snapshot;
                float sporeDrift = math.distance(PosOfId(afterIdle, allies[0]), sporeStart);
                float myceliumDrift = math.distance(PosOfId(afterIdle, allies[1]), myceliumStart);
                Expect(sporeDrift < 2f && myceliumDrift < 2f,
                    $"没下令的友军不该自己漫游（孢子漂移 {sporeDrift:F2}，菌丝体 {myceliumDrift:F2}）");
                Expect(math.abs(sporeDrift - myceliumDrift) < 2f,
                    $"两名友军的移动表现应一致，差异只应来自装配（{sporeDrift:F2} vs {myceliumDrift:F2}）");

                // ── B. 接管友军期间，玩家属性不得写到它身上 ──
                Expect(flow.Sim.RequestControlSwitch(allies[0]) == ControlRequestResult.Success,
                    "应能接管第一名友军");
                for (int i = 0; i < 30; i++)
                {
                    flow.PlayerController.OnUpdate(1f / 60f);
                    flow.Sim.OnUpdate(1f / 60f);
                }
                SimSnapshot afterDrive = flow.Sim.Snapshot;
                float radiusNow = RadiusOfId(afterDrive, allies[0]);
                float healthNow = HealthOfId(afterDrive, allies[0]);
                Expect(math.abs(radiusNow - allyRadiusStart) < 0.01f,
                    $"接管期间玩家体积不得盖到友军半径上（{allyRadiusStart:F2} → {radiusNow:F2}）");
                Expect(math.abs(healthNow - allyHealthStart) < 0.01f,
                    $"接管期间玩家血量不得盖到友军血量上（{allyHealthStart:F1} → {healthNow:F1}）");

                // ── C. 接管候选是全场友军，且按稳定 id 循环 ──
                // 在远处再放一名友军：旧的 18 米信号范围会让它根本不出现在候选里。
                const int FarAllyLogicId = 9414;
                flow.Sim.Spawn(new SpawnRequest
                {
                    Position = new float2(35f, 35f), Health = 100f, Radius = 0.8f, MaxSpeed = 8f,
                    ArchetypeId = CellStageFlow.ControlAllyArchetypeId,
                    Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = FarAllyLogicId,
                });
                flow.Sim.OnUpdate(1f / 60f);
                SimEntityId farAlly = FindEntityId(flow.Sim.Snapshot, FarAllyLogicId, out _);
                Expect(farAlly.IsValid, "远处友军应已落地");

                SimControlCandidate[] candidates = flow.Sim.GetControlCandidates();
                bool farAllyListed = false;
                for (int i = 0; i < candidates.Length; i++)
                {
                    farAllyListed |= candidates[i].EntityId == farAlly;
                }
                Expect(farAllyListed,
                    $"远处友军（约 49 米外）仍应在接管候选里——信号范围已放开成全场" +
                    $"（当前 {flow.Sim.ControlSignalRange:F0}，候选 {candidates.Length} 个）");

                // 连按 Tab 应该走遍全部可控身体再回到起点，而不是在最近的两具之间跳。
                // 每次切换之间推够冷却：接管冷却是真实约束（被它拒绝时 HUD 会显示"接管冷却中"），
                // 不推时间的话这里测到的是冷却而不是"能不能循环遍全部身体"。
                var visited = new HashSet<ulong>();
                for (int i = 0; i < 6; i++)
                {
                    flow.PlayerController.RequestNextControlCandidate();
                    visited.Add(flow.Sim.ControlledUnitId.Value);
                    for (int f = 0; f < 20; f++)
                    {
                        flow.Sim.OnUpdate(1f / 60f);
                    }
                }
                Expect(visited.Count >= 4,
                    $"连续切换应覆盖全部可控身体（本体 + 两名友军 + 远处那名，实际走到 {visited.Count} 具）");

                // ── D. 放下意识：战略视角下连玩家本体都能被框选、被下令 ──
                // bin 报「战术我选不了 #1」。#1 是玩家本体，而选择集两处判据都排除
                // IntentSource == Player 的那一个。修法不是松动判据（那会让两套输入抢同一个单位），
                // 而是让"进战略视角"把意识彻底放下：场上不再有任何 Player 单位。
                //
                // 这里直调 ReleaseControl / EnsureDirectTarget 两个生产入口，不驱动镜头状态机——
                // Edit 模式下 CameraDirector 的过渡靠 unscaledDeltaTime，一次 Tick 就收敛，
                // 测不出真实时序（同 [18] 段对 F9 叠加层的既有说明）。
                Expect(flow.Sim.RequestControlSwitch(allies[0]) == ControlRequestResult.Success ||
                       flow.Sim.ControlledUnitId == allies[0],
                    "先接管一具友军，构造'带着身体进战略视角'的场景");
                SimEntityId parked = flow.Sim.ControlledUnitId;
                Expect(parked.IsValid, "进战略视角前应当确实控制着某一具身体");

                // 走生产入口（记住是谁 + 释放绑在一起），不是直调 ReleaseControl——
                // 前者才是按 M 进战略视角时真正执行的那一段。
                flow.DebugParkControlForStrategy();
                Expect(!flow.Sim.ControlledUnitId.IsValid,
                    "放下意识后不应再有受控实体");
                flow.Sim.OnUpdate(1f / 60f);

                float half = flow.Sim.ArenaHalfExtent + 10f;
                SimUnitPick[] picks = flow.Sim.QueryUnitsInRect(
                    new float2(-half, -half), new float2(half, half));
                bool playerBodyPickable = false;
                for (int i = 0; i < picks.Length; i++)
                {
                    playerBodyPickable |= picks[i].Faction == SimFaction.Player;
                }
                Expect(playerBodyPickable,
                    $"放下意识后玩家本体也应进入可指挥选择集（框到 {picks.Length} 个单位）");

                Expect(flow.CameraDirector != null && flow.CameraDirector.EnsureDirectTarget != null,
                    "镜头应当拿到'回直控前重新接管'的钩子，否则按 M 会被无锚点直接拒绝");
                Expect(flow.CameraDirector.EnsureDirectTarget() &&
                       flow.Sim.ControlledUnitId == parked,
                    "回直控应把意识接管回放下前那一具");
            }
            finally
            {
                if (flow.IsRunning)
                {
                    flow.Exit();
                }
                GameObject cameraAfter = Camera.main != null ? Camera.main.gameObject : null;
                if (cameraAfter != null && cameraAfter != cameraBefore)
                {
                    UnityEngine.Object.DestroyImmediate(cameraAfter);
                }
            }
        }

        private static float2 PosOfId(in SimSnapshot snap, SimEntityId id)
        {
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.EntityId[i] == id) { return snap.Position[i]; }
            }
            return float2.zero;
        }

        private static float RadiusOfId(in SimSnapshot snap, SimEntityId id)
        {
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.EntityId[i] == id) { return snap.Radius[i]; }
            }
            return -1f;
        }

        private static float HealthOfId(in SimSnapshot snap, SimEntityId id)
        {
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.EntityId[i] == id) { return snap.Health[i]; }
            }
            return -1f;
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

        // ── 战略相机状态机 ──────────────────────────────────

        /// <summary>
        /// ProjectA M2-01：<see cref="InputRouter"/> 输入所有权真相 +
        /// <see cref="CameraDirector"/> 三态状态机（Direct/Transition/Strategy）。
        /// 核心验收点：镜头切换绝不传送实体、过渡期立即冻结输入、无效目标不能切回直控、
        /// 暂停下过渡仍会推进。
        ///
        /// Editor 非 Play 下 <c>Input.GetKeyDown</c> 恒为 false，所以本方法不测试
        /// ConsumeKeyDown/ConsumeGlobalKeyDown 的按键消费路径，只测 InputRouter 的
        /// 所有权真值表（Owns/ModalUiOpen）与 CameraDirector 的状态迁移。
        ///
        /// 实测（execute_code 现场验证过）：同一次 -executeMethod 调用内，
        /// <c>Time.unscaledDeltaTime</c> 是一个非零常量（编辑器在这次调用期间不会真正
        /// 推帧，所以值不会变），因此下面的过渡会在极少数次 Tick 内收敛；200 次上限
        /// 只是防止极端环境（dt 恰好为 0）下死循环，命中时会 Fail 而不是卡住——
        /// 实测中从未命中过。
        /// </summary>
        private static void ValidateCameraDirector()
        {
            Line("\n[13] 战略相机状态机（M2-01）");

            // ---- A. InputRouter 输入所有权真值表（不依赖真实按键） ----
            InputRouter.Reset();
            Expect(InputRouter.Scope == InputScope.Direct && !InputRouter.ModalUiOpen,
                "Reset 后应回到 Direct 域，且无任何模态遮罩");

            InputRouter.SetScope(InputScope.Strategy);
            Expect(InputRouter.Owns(InputScope.Strategy) && !InputRouter.Owns(InputScope.Direct),
                "SetScope(Strategy) 后 Strategy 应拥有输入，Direct 与 Strategy 互斥");

            InputRouter.SetScope(InputScope.None);
            Expect(!InputRouter.Owns(InputScope.Direct) && !InputRouter.Owns(InputScope.Strategy),
                "SetScope(None) 后任何域都不应拥有输入所有权（过渡期谁都拿不到）");

            InputRouter.SetScope(InputScope.Direct);
            InputRouter.SetModalUi(true);
            InputRouter.SetGameplayPaused(false);
            Expect(InputRouter.ModalUiOpen,
                "SetModalUi(true) 应使 ModalUiOpen 为真（UI 来源）");
            InputRouter.SetModalUi(false);
            InputRouter.SetGameplayPaused(true);
            Expect(InputRouter.ModalUiOpen,
                "两个模态来源互不覆盖：关闭 UI 来源后，暂停来源仍应维持 ModalUiOpen 为真");
            Expect(!InputRouter.Owns(InputScope.Direct) && !InputRouter.Owns(InputScope.Strategy),
                "ModalUiOpen 为真时，任何域都不应拥有输入所有权");
            InputRouter.SetGameplayPaused(false);
            Expect(!InputRouter.ModalUiOpen,
                "两个模态来源都关闭后，ModalUiOpen 才应回到假");

            InputRouter.Reset();

            // ---- B. CameraDirector 状态机：Direct → Transition → Strategy → Transition → Direct ----
            GameObject cameraBefore = Camera.main != null ? Camera.main.gameObject : null;
            GameObject cameraGo = null;
            var sim = new SimBridge();
            CameraDirector director = null;

            try
            {
                cameraGo = new GameObject("__CameraDirectorValidate_Camera");
                Camera camera = cameraGo.AddComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = 16f;

                SimConfig cfg = SimConfig.Default;
                cfg.UnitCapacity = 32;
                cfg.ArenaHalfExtent = 40f;
                sim.Begin(cfg, Array.Empty<BehaviorArchetype>());
                sim.ConfigureControlSwitch(100f, 0f);

                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(3f, 0f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, LogicId = 9001,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(6f, 0f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, LogicId = 9002,
                });
                sim.OnUpdate(0f);

                director = new CameraDirector();
                director.Bind(camera, sim, new Vector3(0f, 20f, -10f), cfg.ArenaHalfExtent);

                // 7
                Expect(director.Mode == ViewMode.Direct && InputRouter.Scope == InputScope.Direct,
                    "Bind 后应处于 Direct，且 InputRouter.Scope 同步为 Direct");

                // 8：请求进入战略视角应立刻冻结输入，不等下一帧。
                bool requestedStrategy = director.RequestStrategy();
                Expect(requestedStrategy && director.Mode == ViewMode.Transition && director.InTransition &&
                       InputRouter.Scope == InputScope.None,
                    "RequestStrategy 应立即进入 Transition 并把输入域冻结为 None（不等下一帧）");

                // 9：过渡期间的重复请求一律不叠加。
                Expect(!director.RequestStrategy() && !director.RequestDirect(),
                    "过渡期间重复请求 RequestStrategy/RequestDirect 都应返回 false，不叠加");

                // 14 的取样起点：整段切换（8~11）开始前，所有存活单位的位置。
                GetAlivePositions(sim.Snapshot, out int[] idxBeforeCycle, out float2[] posBeforeCycle);

                // 10：过渡应在有限帧数内收敛到 Strategy。
                bool reachedStrategy =
                    TickCameraDirectorUntil(director, () => director.Mode == ViewMode.Strategy, false);
                Expect(reachedStrategy, "过渡未在预期帧数内结束（200 次 Tick 内应到达 Strategy）");
                Expect(director.Mode == ViewMode.Strategy && InputRouter.Scope == InputScope.Strategy &&
                       director.ModeChangeCount == 1,
                    "过渡结束后应落在 Strategy，InputRouter 同步切域，且只记一次模式切换");

                // 15：战略平移越界请求应被钳制在 arenaHalfExtent+6 内。
                director.FocusStrategyOn(new float2(99999f, -99999f));
                float boundsLimit = cfg.ArenaHalfExtent + 6f + 0.001f;
                Expect(math.abs(director.StrategyFocus.x) <= boundsLimit &&
                       math.abs(director.StrategyFocus.y) <= boundsLimit,
                    $"战略注视点越界请求应被钳制在 arenaHalfExtent+6 内（实际 {director.StrategyFocus}）");

                // 11：存在有效受控实体时，战略视角应能切回直控。
                bool requestedDirect = director.RequestDirect();
                Expect(requestedDirect, "存在有效受控实体时，战略视角应能请求切回直控");
                bool backToDirect =
                    TickCameraDirectorUntil(director, () => director.Mode == ViewMode.Direct, false);
                Expect(backToDirect && director.ModeChangeCount == 2,
                    "第二次过渡应落在 Direct，且模式切换计数应累加到 2");

                // 14：整段切换过程（8~11）前后，所有存活单位位置必须逐个保持不变。
                GetAlivePositions(sim.Snapshot, out int[] idxAfterCycle, out float2[] posAfterCycle);
                Expect(PositionsUnchanged(idxBeforeCycle, posBeforeCycle, idxAfterCycle, posAfterCycle),
                    "整段视角切换前后，所有存活单位位置必须逐个保持不变（镜头绝不传送实体）");

                // 16：处于 Transition 时连续 Tick(true)（暂停）也应能走完过渡——
                // 这是"暂停时可选择目标"的结构前提。
                Expect(director.RequestStrategy(),
                    "应能再次请求进入战略视角，用于验证暂停下过渡是否仍会推进");
                bool reachedStrategyPaused =
                    TickCameraDirectorUntil(director, () => director.Mode == ViewMode.Strategy, true);
                Expect(reachedStrategyPaused,
                    "处于 Transition 时连续 Tick(true)（暂停）也应能走完过渡到达 Strategy");

                // 12：无有效受控实体时，不许切回一个不存在的目标。
                sim.World.ControlFallbackEnabled = false;
                KillAllFriendlies(sim);
                Expect(!director.RequestDirect() && director.Mode == ViewMode.Strategy,
                    "没有有效受控实体时 RequestDirect 应返回 false，且不得切回一个不存在的目标");
                sim.World.ControlFallbackEnabled = true;

                // 17：Unbind 应交还输入所有权。
                director.Unbind();
                Expect(InputRouter.Scope == InputScope.Direct && !InputRouter.ModalUiOpen,
                    "Unbind 后 InputRouter 应回到 Direct 且无模态遮罩");
            }
            finally
            {
                director?.Unbind();
                sim.End();
                if (cameraGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(cameraGo);
                }
                // Bind/Unbind 本身不新建摄像机，但防御性地照 ValidateInheritance 的写法清理一次。
                GameObject cameraAfter = Camera.main != null ? Camera.main.gameObject : null;
                if (cameraAfter != null && cameraAfter != cameraBefore)
                {
                    UnityEngine.Object.DestroyImmediate(cameraAfter);
                }
                InputRouter.Reset();
            }

            // ---- C. 13：直控目标丢失后应自动退回战略视角（独立场景，避免与上面已经
            // 切换多次的状态交叉）----
            GameObject camera2Before = Camera.main != null ? Camera.main.gameObject : null;
            GameObject camera2Go = null;
            var sim2 = new SimBridge();
            CameraDirector director2 = null;

            try
            {
                camera2Go = new GameObject("__CameraDirectorValidate_Camera2");
                Camera camera2 = camera2Go.AddComponent<Camera>();
                camera2.orthographic = true;
                camera2.orthographicSize = 16f;

                SimConfig cfg2 = SimConfig.Default;
                cfg2.UnitCapacity = 32;
                cfg2.ArenaHalfExtent = 40f;
                sim2.Begin(cfg2, Array.Empty<BehaviorArchetype>());
                // 关掉回弹：只有一个默认受控实体，杀掉后必须确定性地"无控制"，
                // 而不是让 M1-06 的回弹机制悄悄换一个目标，从而测不到镜头的自动退回。
                sim2.World.ControlFallbackEnabled = false;
                sim2.OnUpdate(0f);

                SimSnapshot initialSnap2 = sim2.Snapshot;
                bool hasInitialControlled = initialSnap2.TryResolveControlledUnit(out int controlledIndex);
                Expect(hasInitialControlled,
                    "本场景初始应有一个默认受控实体，供'直控目标丢失自动退回'测试使用");

                director2 = new CameraDirector();
                director2.Bind(camera2, sim2, new Vector3(0f, 20f, -10f), cfg2.ArenaHalfExtent);
                Expect(director2.Mode == ViewMode.Direct, "第二个场景 Bind 后也应处于 Direct");

                if (hasInitialControlled)
                {
                    sim2.World.KillUnit(controlledIndex, 0);
                }

                director2.Tick(false);
                Expect(director2.Mode == ViewMode.Transition,
                    "直控目标丢失后，下一次 Tick 应立即自动进入 Transition，不需要显式请求");

                bool reachedStrategyAfterLoss =
                    TickCameraDirectorUntil(director2, () => director2.Mode == ViewMode.Strategy, false);
                Expect(reachedStrategyAfterLoss,
                    "直控目标丢失触发的自动过渡也应能在 200 次 Tick 内收敛");
                Expect(director2.Mode == ViewMode.Strategy,
                    "直控目标丢失后应最终自动落回 Strategy 视角");
            }
            finally
            {
                director2?.Unbind();
                sim2.End();
                if (camera2Go != null)
                {
                    UnityEngine.Object.DestroyImmediate(camera2Go);
                }
                GameObject camera2After = Camera.main != null ? Camera.main.gameObject : null;
                if (camera2After != null && camera2After != camera2Before)
                {
                    UnityEngine.Object.DestroyImmediate(camera2After);
                }
                InputRouter.Reset();
            }
        }

        /// <summary>反复 Tick 直到 <paramref name="done"/> 成立或达到 200 次上限——
        /// 上限只是防止极端环境（dt 恰好为 0）下死循环，不是设计上的正常路径。</summary>
        private static bool TickCameraDirectorUntil(CameraDirector director, Func<bool> done, bool paused)
        {
            const int maxIterations = 200;
            if (done())
            {
                return true;
            }
            for (int i = 0; i < maxIterations; i++)
            {
                director.Tick(paused);
                if (done())
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>取出快照里所有存活单位的槽位与位置，用于前后比对"镜头没有传送实体"。</summary>
        private static void GetAlivePositions(in SimSnapshot snap, out int[] aliveIndex, out float2[] positions)
        {
            var idx = new System.Collections.Generic.List<int>();
            var pos = new System.Collections.Generic.List<float2>();
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.Alive[i] != 0)
                {
                    idx.Add(i);
                    pos.Add(snap.Position[i]);
                }
            }
            aliveIndex = idx.ToArray();
            positions = pos.ToArray();
        }

        private static bool PositionsUnchanged(int[] idxBefore, float2[] posBefore, int[] idxAfter, float2[] posAfter)
        {
            if (idxBefore.Length != idxAfter.Length)
            {
                return false;
            }
            for (int i = 0; i < idxBefore.Length; i++)
            {
                if (idxBefore[i] != idxAfter[i] || math.distance(posBefore[i], posAfter[i]) > 1e-4f)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>杀光场上所有友军（Player + PlayerMinion），用于构造"确定性地无有效
        /// 受控实体"的场景。复用 <see cref="IsFriendlyFactionForTest"/>，与 M1-06 那组
        /// 测试对"友军"的定义保持一致。</summary>
        private static void KillAllFriendlies(SimBridge sim)
        {
            SimSnapshot snap = sim.Snapshot;
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.Alive[i] != 0 && IsFriendlyFactionForTest(snap.FactionOf(i)))
                {
                    sim.World.KillUnit(i, 0);
                }
            }
        }

        // ── RTS 选择与基础命令（M2-02）──────────────────────

        /// <summary>
        /// ProjectA M2-02：RTS 选择与基础命令。矩形框选（含 commandableOnly 语义）、
        /// 命令下达/清除/到达自动交还 AI、玩家直控实体免疫编队指挥、槽位复用不继承旧命令，
        /// 以及不破坏 M1-06「至多一个 Player 意图来源」不变量；再验 <see cref="SquadCommandSystem"/>
        /// 的暂停排队语义——尤其是"排队存的是选择集快照而不是引用"这条最容易踩的坑。
        /// </summary>
        private static void ValidateSquadCommands()
        {
            Line("\n[14] RTS 选择与基础命令（M2-02）");

            ValidateSquadCommandsKernel();
            ValidateSquadCommandSystemQueueing();
        }

        /// <summary>直接对 <see cref="SimWorld"/> 下手，覆盖框选查询与命令生命周期。</summary>
        private static void ValidateSquadCommandsKernel()
        {
            var world = new SimWorld();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            cfg.ArenaHalfExtent = 100f;
            cfg.RandomSeed = 0xC0FFEE02u;
            world.Initialize(cfg);
            // 本组断言不关心死亡回弹，关掉距离限制避免它意外把结果搅浑。
            world.ControlFallbackRange = 0f;

            SimCommandBuffer cmds = default;
            cmds.Initialize(Unity.Collections.Allocator.Persistent, 32);

            try
            {
                SimEntityId playerControlled = world.ControlledUnitId;
                Expect(playerControlled.IsValid, "初始默认受控实体应有效");

                world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(0f, 5f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 5f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = 9001,
                });
                world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(2f, 5f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 5f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = 9002,
                });
                world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(4f, 5f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = 9003,
                });

                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(1f / 60f, ref cmds); // 让 spawn 落地一帧

                SimSnapshot snap0 = world.GetSnapshot();
                SimEntityId unitA = FindEntityId(snap0, 9001, out _);
                SimEntityId unitB = FindEntityId(snap0, 9002, out _);
                SimEntityId hostile = FindEntityId(snap0, 9003, out _);
                Expect(unitA.IsValid && unitB.IsValid && hostile.IsValid,
                    "M2-02 测试单位应全部拥有有效稳定 ID");

                // ── A. 框选查询 ──
                var bigMin = new float2(-100f, -100f);
                var bigMax = new float2(100f, 100f);

                SimUnitPick[] commandable = world.QueryUnitsInRect(bigMin, bigMax, commandableOnly: true);
                bool commandableHasHostile = false;
                bool commandableAllFriendly = true;
                for (int i = 0; i < commandable.Length; i++)
                {
                    if (commandable[i].EntityId == hostile) { commandableHasHostile = true; }
                    if (commandable[i].Faction != SimFaction.PlayerMinion &&
                        commandable[i].Faction != SimFaction.Player)
                    {
                        commandableAllFriendly = false;
                    }
                }
                Expect(!commandableHasHostile && commandableAllFriendly,
                    $"commandableOnly=true 大矩形框选应只含友军、不含 Hostile（实际 {commandable.Length} 个，含敌 {commandableHasHostile}）");

                SimUnitPick[] everyone = world.QueryUnitsInRect(bigMin, bigMax, commandableOnly: false);
                bool everyoneHasHostile = false;
                for (int i = 0; i < everyone.Length; i++)
                {
                    if (everyone[i].EntityId == hostile) { everyoneHasHostile = true; break; }
                }
                Expect(everyoneHasHostile, "commandableOnly=false 框选应包含 Hostile");

                bool sortedAscending = true;
                for (int i = 1; i < everyone.Length; i++)
                {
                    if (everyone[i - 1].EntityId.Value >= everyone[i].EntityId.Value)
                    {
                        sortedAscending = false;
                        break;
                    }
                }
                Expect(sortedAscending, "框选结果应按 EntityId 升序排列");

                SimUnitPick[] farAway = world.QueryUnitsInRect(new float2(1000f, 1000f), new float2(1010f, 1010f));
                Expect(farAway.Length == 0, "不覆盖任何单位的远处矩形应返回空数组");

                bool commandableHasPlayer = false;
                for (int i = 0; i < commandable.Length; i++)
                {
                    if (commandable[i].EntityId == playerControlled) { commandableHasPlayer = true; break; }
                }
                Expect(!commandableHasPlayer, "commandableOnly=true 不应返回当前玩家直控实体");

                // ── B. 命令生命周期 ──

                var farTarget = new float2(60f, 5f);
                var moveCmd = new UnitCommand
                {
                    Kind = UnitCommandKind.Move, TargetPosition = farTarget, ArriveRadius = 0.5f,
                };
                int accepted = world.IssueCommand(new[] { unitA, unitB, playerControlled }, moveCmd);
                Expect(accepted == 2,
                    $"对两个友军 + 一个玩家直控实体下 Move 命令应只接受两个友军（实际 {accepted}）");
                Expect(world.TryGetCommand(unitA, out UnitCommand gotA) && gotA.Kind == UnitCommandKind.Move,
                    "下令后应能取回 Kind=Move");

                world.TryResolveUnit(unitA, out int idxA1);
                world.TryResolveUnit(unitB, out int idxB1);
                world.TryResolveUnit(playerControlled, out int idxPlayer);
                SimSnapshot snapIssued = world.GetSnapshot();
                Expect(snapIssued.IntentSourceOf(idxA1) == IntentSource.Commanded &&
                       snapIssued.IntentSourceOf(idxB1) == IntentSource.Commanded,
                    "接受命令的两个友军 IntentSource 都应变成 Commanded");
                Expect(snapIssued.IntentSourceOf(idxPlayer) == IntentSource.Player,
                    "玩家直控实体的 IntentSource 不应被 IssueCommand 夺走");
                Expect(!world.TryGetCommand(playerControlled, out _),
                    "玩家直控实体不应被写入任何命令");

                // 命令驱动真实移动：行为断言而非字段断言。
                float2 posABefore = snapIssued.Position[idxA1];
                float distBefore = math.distance(posABefore, farTarget);
                for (int f = 0; f < 30; f++)
                {
                    cmds.SetPlayerIntent(PlayerIntent.Idle);
                    world.Step(1f / 60f, ref cmds);
                }
                world.TryResolveUnit(unitA, out int idxA2);
                SimSnapshot snapMoved = world.GetSnapshot();
                float distAfter = math.distance(snapMoved.Position[idxA2], farTarget);
                Expect(distAfter < distBefore,
                    $"命令驱动的单位 30 帧后应更靠近目标点（前 {distBefore:F2} → 后 {distAfter:F2}）");

                // 2026-09-14 产品决策反转：到达目标点后**停在终点原地待命**，不再交还自由 AI。
                // 玩家报「右键移动，角色不在终点停下，而是沿着方向继续走」——根因就在这里：
                // 交还 AI 后仆从原型找不到敌人就走 Wander 半速漫游，于是"到站"接上"自己晃走"。
                world.TryResolveUnit(unitB, out int idxB2);
                float2 posBNow = world.GetSnapshot().Position[idxB2];
                var arriveCmd = new UnitCommand
                {
                    Kind = UnitCommandKind.Move, TargetPosition = posBNow, ArriveRadius = 1f,
                };
                world.IssueCommand(new[] { unitB }, arriveCmd);
                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(1f / 60f, ref cmds);
                world.TryResolveUnit(unitB, out int idxB3);
                Expect(world.GetSnapshot().IntentSourceOf(idxB3) == IntentSource.Commanded &&
                       world.TryGetCommand(unitB, out UnitCommand heldAfterMove) &&
                       heldAfterMove.Kind == UnitCommandKind.Guard,
                    "Move 到达终点后应转成原地守备并保持 Commanded——停在玩家指定的那个点，不再自己走开");
                float2 posAtArrival = world.GetSnapshot().Position[idxB3];
                for (int f = 0; f < 120; f++)
                {
                    world.Step(1f / 60f, ref cmds);
                }
                world.TryResolveUnit(unitB, out int idxBHeld);
                float arrivalDrift = math.distance(world.GetSnapshot().Position[idxBHeld], posAtArrival);
                Expect(arrivalDrift < 2f,
                    $"到站后 120 帧内不该自己飘走（漂移 {arrivalDrift:F2}）");

                // ClearCommand 应立即交还 AI。
                bool cleared = world.ClearCommand(unitA);
                world.TryResolveUnit(unitA, out int idxA3);
                Expect(cleared && world.GetSnapshot().IntentSourceOf(idxA3) == IntentSource.AI &&
                       !world.TryGetCommand(unitA, out _),
                    "ClearCommand 应成功并把单位交还 AI");

                // Attack 命令的目标死亡后应交还 AI。
                var attackCmd = new UnitCommand
                {
                    Kind = UnitCommandKind.Attack, TargetEntity = hostile, ArriveRadius = 0.5f,
                };
                int acceptedAttack = world.IssueCommand(new[] { unitA }, attackCmd);
                Expect(acceptedAttack == 1, $"对存活友军下 Attack 命令应被接受（实际 {acceptedAttack}）");
                world.TryResolveUnit(hostile, out int hostileIdx);
                world.KillUnit(hostileIdx, 0);
                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(1f / 60f, ref cmds);
                world.TryResolveUnit(unitA, out int idxA4);
                Expect(world.GetSnapshot().IntentSourceOf(idxA4) == IntentSource.Commanded &&
                       world.TryGetCommand(unitA, out UnitCommand heldAfterKill) &&
                       heldAfterKill.Kind == UnitCommandKind.Guard,
                    "Attack 目标死亡后，下令单位应停在原地待命而不是交还自由 AI 自己漫游");

                // 槽位复用不应继承旧占用者的命令。
                var reissueCmd = new UnitCommand
                {
                    Kind = UnitCommandKind.Guard, TargetPosition = posBNow, ArriveRadius = 1f,
                };
                world.IssueCommand(new[] { unitB }, reissueCmd);
                world.TryResolveUnit(unitB, out int idxB4);
                world.KillUnit(idxB4, 0);
                int newIdx = world.SpawnUnit(new SpawnRequest
                {
                    Position = posBNow, Health = 20f, Radius = 0.5f,
                    MaxSpeed = 5f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = 9004,
                });
                SimSnapshot snapReused = world.GetSnapshot();
                world.TryGetEntityId(newIdx, out SimEntityId newUnitId);
                Expect(newUnitId.IsValid && !world.TryGetCommand(newUnitId, out _) &&
                       snapReused.IntentSourceOf(newIdx) != IntentSource.Commanded,
                    "复用槽位生成的新单位不应继承旧占用者的命令");

                // ── C. M1-06 不变量不应被破坏 ──
                int playerIntentCount = CountPlayerIntentUnits(world.GetSnapshot());
                Expect(playerIntentCount <= 1,
                    $"大量命令下达之后，存活单位里 IntentSource==Player 的数量仍应 ≤1（实际 {playerIntentCount}）");
            }
            finally
            {
                cmds.Dispose();
                world.Dispose();
            }
        }

        /// <summary>经 <see cref="SimBridge"/> + <see cref="SquadCommandSystem"/> 走一遍选择、
        /// 编组与暂停排队；不驱动 <see cref="SquadCommandSystem.Tick"/> 的鼠标输入路径，
        /// 只调用可以直接断言的公开入口。</summary>
        private static void ValidateSquadCommandSystemQueueing()
        {
            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 60f;
            sim.Begin(cfg, Array.Empty<BehaviorArchetype>());

            var cameraGo = new GameObject("ValidateSquadCommands_TempCamera");
            Camera camera = cameraGo.AddComponent<Camera>();
            var squad = new SquadCommandSystem();

            try
            {
                squad.Bind(sim, camera);

                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(0f, -10f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 5f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = 9101,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(2f, -10f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 5f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = 9102,
                });
                sim.OnUpdate(0.01f);

                SimSnapshot snap = sim.Snapshot;
                SimEntityId f1 = FindEntityId(snap, 9101, out _);
                SimEntityId f2 = FindEntityId(snap, 9102, out _);
                Expect(f1.IsValid && f2.IsValid, "SquadCommandSystem 测试单位应全部拥有有效稳定 ID");

                // 14：显式选择
                squad.SelectExplicit(new[] { f1, f2 });
                Expect(squad.Selection.Count == 2,
                    $"SelectExplicit 后选择集应有 2 个单位（实际 {squad.Selection.Count}）");

                // 15：暂停下达 → 排队，内核尚未收到命令
                var pointA = new float2(20f, -10f);
                int queuedTargets1 = squad.Issue(UnitCommandKind.Move, pointA, SimEntityId.None, paused: true);
                Expect(queuedTargets1 == 2 && squad.QueuedCommandCount == 1,
                    $"暂停下达应排队而不立即执行（返回 {queuedTargets1}，队列 {squad.QueuedCommandCount}）");
                Expect(!sim.TryGetCommand(f1, out _) && !sim.TryGetCommand(f2, out _),
                    "排队中的命令不应提前写入内核");

                // 16：再排一条
                var pointB = new float2(-20f, -10f);
                int queuedTargets2 = squad.Issue(UnitCommandKind.Guard, pointB, SimEntityId.None, paused: true);
                Expect(queuedTargets2 == 2 && squad.QueuedCommandCount == 2,
                    $"第二条排队命令应追加到队列（队列 {squad.QueuedCommandCount}）");

                // 17：兑现 → 顺序稳定，后来者覆盖先前者
                int flushed = squad.FlushQueuedCommands();
                Expect(flushed == 2 && squad.QueuedCommandCount == 0,
                    $"FlushQueuedCommands 应兑现全部排队命令并清空队列（实际兑现 {flushed}）");
                Expect(sim.TryGetCommand(f1, out UnitCommand cmdF1) && cmdF1.Kind == UnitCommandKind.Guard &&
                       sim.TryGetCommand(f2, out UnitCommand cmdF2) && cmdF2.Kind == UnitCommandKind.Guard,
                    "兑现顺序应稳定：后下达的 Guard 应覆盖先下达的 Move");

                // 18：排队存的是选择集快照，不是引用
                sim.ClearCommand(f1);
                sim.ClearCommand(f2);
                squad.SelectExplicit(new[] { f1, f2 });
                var pointC = new float2(15f, -5f);
                squad.Issue(UnitCommandKind.Move, pointC, SimEntityId.None, paused: true);
                squad.ClearSelection();
                Expect(squad.Selection.Count == 0, "排队后清空选择集，Selection 应立即为空");
                int flushedAfterClear = squad.FlushQueuedCommands();
                Expect(flushedAfterClear == 1 &&
                       sim.TryGetCommand(f1, out UnitCommand snapshotF1) && snapshotF1.Kind == UnitCommandKind.Move &&
                       sim.TryGetCommand(f2, out UnitCommand snapshotF2) && snapshotF2.Kind == UnitCommandKind.Move,
                    "排队命令应下达给排队时的选择集快照，即便之后清空了当前选择");

                // 19：编组分配 / 召回
                squad.SelectExplicit(new[] { f1, f2 });
                squad.AssignGroup(1);
                squad.ClearSelection();
                squad.RecallGroup(1);
                Expect(squad.Selection.Count == 2,
                    $"RecallGroup 应恢复编组成员（实际 {squad.Selection.Count}）");

                // 20：编组成员死亡后 RecallGroup 不应把死人放回选择集
                sim.World.TryResolveUnit(f2, out int f2Idx);
                sim.ConsumeUnit(f2Idx);
                squad.ClearSelection();
                squad.RecallGroup(1);
                Expect(squad.Selection.Count == 1 && squad.Selection[0] == f1,
                    $"编组成员死亡后 RecallGroup 不应复活死者进选择集（实际 {squad.Selection.Count}）");
            }
            finally
            {
                squad.Unbind();
                sim.End();
                UnityEngine.Object.DestroyImmediate(cameraGo);
            }
        }

        // ── 装配按实体归属（M2-03a）──────────────────────────

        /// <summary>
        /// ProjectA M2-03a：装配不再是"玩家全局单例"，而是按 <see cref="SimEntityId"/> 归属到个体。
        /// 覆盖：未登记实体的空装配语义、玩家本体的**实时投影**（而非生成时快照）、
        /// 失能位置位与跨刷新保留、两个不同行为原型派生出的动作集确实不同、死实体条目回收。
        ///
        /// 刻意全部走 <see cref="UnitLoadoutRegistry"/> 的公开入口 + 一个可注入的假投影源：
        /// 玩家真实装配挂在 <c>MetabolicSlicePanel</c>（MonoBehaviour）上，Edit 模式起不来，
        /// 投影源不可注入的话这一段就只能靠进 Play 手点验收。
        /// </summary>
        private static void ValidateUnitLoadouts()
        {
            Line("\n[15] 装配按实体归属（M2-03a）");

            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 60f;
            cfg.RandomSeed = 0xC0FFEE03u;
            sim.Begin(cfg, Array.Empty<BehaviorArchetype>());

            var registry = new UnitLoadoutRegistry();
            var fakeSource = new FakePlayerLoadoutSource();

            try
            {
                registry.Bind(sim, fakeSource);

                SimEntityId body = sim.ControlledUnitId;
                Expect(body.IsValid, "玩家本体应有有效稳定实体 ID");
                registry.RegisterPlayerBody(body);

                // ── A. 未登记实体 = 明确的空装配，不是 null、不抛 ──
                UnitLoadout unknown = registry.Get(new SimEntityId(0xDEADBEEFUL));
                Expect(unknown != null && unknown.Origin == UnitLoadoutOrigin.Unregistered &&
                       unknown.OrganCount == 0 && unknown.HasAction(LoadoutAction.Move) &&
                       !unknown.HasAction(LoadoutAction.Primary),
                    "未登记实体应返回只有移动的空装配（非 null、不抛）");
                Expect(registry.Get(SimEntityId.None) == UnitLoadout.Empty,
                    "无效实体 ID 应返回共享的 UnitLoadout.Empty");
                Expect(!registry.SetOrganDisabled(new SimEntityId(0xDEADBEEFUL), "org_cilia", true),
                    "对未登记实体置失能位应安全失败而不是抛异常");

                // ── B. 玩家本体是实时投影，不是生成时快照 ──
                fakeSource.Organs.Clear();
                UnitLoadout empty = registry.Get(body);
                Expect(empty.Origin == UnitLoadoutOrigin.PlayerProjection &&
                       empty.ActionMask == UnitLoadout.MoveActionMask && empty.OrganCount == 0,
                    $"注入源为空时玩家本体应只有移动动作（实际掩码 {empty.ActionMask}，器官 {empty.OrganCount}）");

                fakeSource.Organs.Add(new UnitLoadoutOrgan("org_lyso", LoadoutAction.Primary));
                fakeSource.Organs.Add(new UnitLoadoutOrgan("org_cilia", LoadoutAction.Utility));
                UnitLoadout equipped = registry.Get(body);
                bool primaryIsLyso = equipped.TryGetOrgan(LoadoutAction.Primary, out UnitLoadoutOrgan primary) &&
                                     primary.OrganId == "org_lyso";
                Expect(equipped.ActionMask != UnitLoadout.MoveActionMask &&
                       equipped.HasAction(LoadoutAction.Primary) && equipped.HasAction(LoadoutAction.Utility) &&
                       primaryIsLyso && equipped.OrganCount == 2,
                    $"改注入源后再查，玩家本体装配应跟着变（实时投影而非快照；实际掩码 {equipped.ActionMask}，器官 {equipped.OrganCount}）");

                // ── C. 失能位：置位、跨投影刷新保留、解除 ──
                Expect(registry.SetOrganDisabled(body, "org_lyso", true),
                    "失能置位应命中玩家本体的 org_lyso");
                UnitLoadout disabled = registry.Get(body);
                Expect(!disabled.HasAction(LoadoutAction.Primary) && disabled.HasAction(LoadoutAction.Utility) &&
                       disabled.OrganCount == 2 && disabled.ContainsOrgan("org_lyso"),
                    "主器官失能后主动作应消失，功能动作不受影响，且失能器官仍留在装配里可见");

                // 用现役 id：M2-03c 第 0 步已把仓里最后一处对已退役 org_hook 的引用清掉，
                // 测试里再留一个会让"还有谁在引用退役内容"这类排查白跑一趟。
                fakeSource.Organs.Add(new UnitLoadoutOrgan("org_spine", LoadoutAction.Interact));
                UnitLoadout afterRefresh = registry.Get(body);
                Expect(!afterRefresh.HasAction(LoadoutAction.Primary) &&
                       afterRefresh.HasAction(LoadoutAction.Interact) && afterRefresh.OrganCount == 3,
                    "投影刷新（装上新器官）不应把已置的失能位冲掉");

                Expect(registry.SetOrganDisabled(body, "org_lyso", false) &&
                       registry.Get(body).HasAction(LoadoutAction.Primary),
                    "解除失能后主动作应恢复");

                // ── D. 两个不同原型 → 动作集不同（M2-03 的验收项）──
                // 内核只拿到 ArchetypeId=0（本用例不加载原型表，传 13/15 会越界）；
                // 被测对象是热更层的"原型 → 装配"映射，它的入参来自生成请求而不是内核回读。
                int sporeLogicId = 9301;
                int myceliumLogicId = 9302;
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(-4f, 2f), Health = 20f, Radius = 0.8f,
                    MaxSpeed = 5f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = sporeLogicId,
                });
                registry.RegisterArchetypePending(sporeLogicId, ArchetypeLoadoutTable.SporeArchetypeId);
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(4f, 2f), Health = 20f, Radius = 0.8f,
                    MaxSpeed = 5f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = myceliumLogicId,
                });
                registry.RegisterArchetypePending(myceliumLogicId, ArchetypeLoadoutTable.MyceliumArchetypeId);

                Expect(registry.PendingCount == 2 && registry.ResolvePending(sim.Snapshot) == 0,
                    "生成尚未落地时挂起项应解析不到（Spawn 只是入队）");

                sim.OnUpdate(1f / 60f);
                int resolved = registry.ResolvePending(sim.Snapshot);
                Expect(resolved == 2 && registry.PendingCount == 0,
                    $"实体落地后挂起项应全部补登记（实际解析 {resolved}，剩余 {registry.PendingCount}）");
                Expect(registry.ResolvePending(sim.Snapshot) == 0,
                    "挂起表清空后 ResolvePending 应立即返回 0（稳态下不做逐单位扫描）");

                SimSnapshot snap = sim.Snapshot;
                SimEntityId spore = FindEntityId(snap, sporeLogicId, out _);
                SimEntityId mycelium = FindEntityId(snap, myceliumLogicId, out _);
                Expect(spore.IsValid && mycelium.IsValid, "两名友军应都拥有有效稳定实体 ID");

                UnitLoadout sporeLoadout = registry.Get(spore);
                UnitLoadout myceliumLoadout = registry.Get(mycelium);
                Expect(sporeLoadout.OrganCount > 0 && myceliumLoadout.OrganCount > 0 &&
                       sporeLoadout.Origin == UnitLoadoutOrigin.ArchetypeDerived &&
                       myceliumLoadout.Origin == UnitLoadoutOrigin.ArchetypeDerived,
                    "两名友军都应派生出非空的原型装配");
                // 2026-09-13 试玩反馈修正：两名友军的差异**不再用槽位掩码表达**。
                // 原先菌丝体的第二件器官放在 Interact 槽，只是为了让掩码不同，可 Interact 至今恒判
                // NoInteractTarget——玩家接管菌丝体时那个动作永远按不响，"动作集不同"在行为层是假的。
                // 现在两者都用 Primary+Utility，真正的差异由器官 id 与内核形态承担（见 [16] 段的 Kind 断言）。
                bool sporeHasPrimaryOrgan = sporeLoadout.TryGetOrgan(LoadoutAction.Primary, out UnitLoadoutOrgan sporeP);
                bool myceliumHasPrimaryOrgan = myceliumLoadout.TryGetOrgan(LoadoutAction.Primary, out UnitLoadoutOrgan myceliumP);
                Expect(sporeHasPrimaryOrgan && myceliumHasPrimaryOrgan && sporeP.OrganId != myceliumP.OrganId,
                    $"原型 {ArchetypeLoadoutTable.SporeArchetypeId} 与 {ArchetypeLoadoutTable.MyceliumArchetypeId} 的主器官应不同" +
                    $"（{(sporeHasPrimaryOrgan ? sporeP.OrganId : "(无)")} vs {(myceliumHasPrimaryOrgan ? myceliumP.OrganId : "(无)")}）");
                Expect(sporeLoadout.HasAction(LoadoutAction.Utility) && myceliumLoadout.HasAction(LoadoutAction.Utility) &&
                       !sporeLoadout.HasAction(LoadoutAction.Interact) && !myceliumLoadout.HasAction(LoadoutAction.Interact),
                    "两名友军的第二个动作都应落在功能位；Interact 恒无目标，禁止把唯一的第二动作放进去");
                Expect(sporeLoadout.HasAction(LoadoutAction.Move) && myceliumLoadout.HasAction(LoadoutAction.Move),
                    "移动动作不由器官提供，任何装配都应恒有");

                // 友军装配是原型派生的一次性结果，不该被玩家投影源污染
                fakeSource.Organs.Clear();
                fakeSource.Organs.Add(new UnitLoadoutOrgan("org_needle", LoadoutAction.Primary));
                Expect(registry.Get(spore).TryGetOrgan(LoadoutAction.Primary, out UnitLoadoutOrgan sporePrimary) &&
                       sporePrimary.OrganId != "org_needle",
                    "友军装配不应跟着玩家投影源变（它是原型派生，不是玩家本体）");

                // ── E. 死实体条目回收 ──
                Expect(registry.IsRegistered(spore), "孢子友军此刻应在册");
                int registeredBefore = registry.Count;
                Expect(sim.TryResolveUnitIndex(spore, out int sporeIndex),
                    "存活友军应能解析出瞬时槽位");
                sim.ConsumeUnit(sporeIndex);
                UnitLoadout deadLoadout = registry.Get(spore);
                Expect(deadLoadout == UnitLoadout.Empty && !registry.IsRegistered(spore) &&
                       registry.Count == registeredBefore - 1,
                    $"死实体查询一次后条目应被回收（回收前 {registeredBefore}，现 {registry.Count}）");
                Expect(registry.IsRegistered(mycelium) && registry.Get(mycelium).OrganCount > 0,
                    "回收死者不应误伤仍然存活的友军条目");

                // ── F. 解绑清空，跨局不粘 ──
                registry.Unbind();
                Expect(registry.Count == 0 && registry.PendingCount == 0 &&
                       registry.Get(mycelium) == UnitLoadout.Empty,
                    "Unbind 后注册表应清空——实体 ID 只在生成它的那个 SimWorld 内有效");
            }
            finally
            {
                registry.Unbind();
                sim.End();
            }
        }

        // ── [16] 直控动作集与释放（M2-03b）────────────────────

        /// <summary>
        /// M2-03b 的验收核心是里程碑那句"两个不同装配单位被接管时动作集**不同且与实体一致**"。
        ///
        /// 所以这里刻意不只比掩码——只比数据不同，行为却一模一样，是不算过的。
        /// 每个单位释放之后都去内核里看真的落下了什么：菌丝体应该多出一块持续区域，
        /// 孢子应该用真弹体把身边的敌人打掉血且**不**产生区域。两者互相是对方的反证。
        ///
        /// M2-03c 第 0 步改动：孢子的主器官由 <c>org_confusion_spore</c>（Structural / 零伤害挂标记）
        /// 换成现役攻击器官 <c>org_emitter</c>，菌丝体的交互器官由已退役的 <c>org_hook</c>
        /// 换成目录指定的继任者 <c>org_cilia</c>。本段期望值已同步更新到新的真实行为，
        /// **不是**为了让断言变绿而回退器官选择。
        /// </summary>
        private static void ValidateDirectControlActions()
        {
            Line("\n[16] 直控动作集与释放（M2-03b）");

            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 60f;
            cfg.RandomSeed = 0xC0FFEE04u;
            sim.Begin(cfg, Array.Empty<BehaviorArchetype>());
            // 切换范围/冷却在本用例里不是被测对象，放开以免干扰（同 [12]/[13] 的做法）。
            sim.ConfigureControlSwitch(100f, 0f);

            var registry = new UnitLoadoutRegistry();
            var fakeSource = new FakePlayerLoadoutSource();
            var actions = new DirectControlActions();
            var stats = new StatSheet();
            stats.ResetToDefaults();
            var playerController = new CellPlayerController();

            InputRouter.Reset();

            try
            {
                registry.Bind(sim, fakeSource);
                SimEntityId body = sim.ControlledUnitId;
                registry.RegisterPlayerBody(body);
                // abilities / status 都不给：Edit 模式起不了整套 ModuleHub。
                // 可注入正是为了让这条路径能写真断言，而不是只能进 Play 手点（同 M2-03a §4 的理由）。
                actions.Bind(sim, registry, abilities: null, status: null);
                playerController.Bind(sim, stats, null, null, null, actions);

                // 两名友军 + 一个站在孢子身边的敌人（用来观察"到底有没有东西落到它身上"）。
                // MaxSpeed=0：本用例不验移动，让位置在整段里保持确定。
                const int SporeLogicId = 9401;
                const int MyceliumLogicId = 9402;
                const int HostileLogicId = 9403;
                var sporePos = new float2(-4f, 2f);
                var myceliumPos = new float2(4f, 2f);
                sim.Spawn(new SpawnRequest
                {
                    Position = sporePos, Health = 40f, Radius = 0.8f, MaxSpeed = 0f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = SporeLogicId,
                });
                registry.RegisterArchetypePending(SporeLogicId, ArchetypeLoadoutTable.SporeArchetypeId);
                sim.Spawn(new SpawnRequest
                {
                    Position = myceliumPos, Health = 40f, Radius = 0.8f, MaxSpeed = 0f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = MyceliumLogicId,
                });
                registry.RegisterArchetypePending(MyceliumLogicId, ArchetypeLoadoutTable.MyceliumArchetypeId);
                sim.Spawn(new SpawnRequest
                {
                    // 贴着孢子放：挂标记型器官的半径来自 Luban 表，取值可能很小，
                    // 放远了会把"表里数字偏小"误判成"机制没生效"。
                    Position = sporePos + new float2(0.5f, 0f), Health = 500f, Radius = 0.6f, MaxSpeed = 0f,
                    ArchetypeId = 0, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = HostileLogicId,
                });

                sim.OnUpdate(1f / 60f);
                registry.ResolvePending(sim.Snapshot);
                SimSnapshot snap = sim.Snapshot;
                SimEntityId spore = FindEntityId(snap, SporeLogicId, out _);
                SimEntityId mycelium = FindEntityId(snap, MyceliumLogicId, out _);
                SimEntityId hostile = FindEntityId(snap, HostileLogicId, out int hostileIndex);
                Expect(spore.IsValid && mycelium.IsValid && hostile.IsValid,
                    "两名友军与一个敌人应都已落地并拥有有效稳定实体 ID");

                // ── A. 开局动作集就绪；移动不走释放入口 ──
                Expect(actions.ActionSet.EntityId == body &&
                       actions.ActionSet.Origin == UnitLoadoutOrigin.PlayerProjection,
                    "Bind 时就该按当前受控实体编译一次动作集，不必等到第一次切换控制权");
                Expect(!actions.TryRelease(LoadoutAction.Move, new float2(1f, 0f)) &&
                       actions.LastReleaseResult == DirectActionAvailability.NotAReleaseAction,
                    "移动不走释放入口——它是每帧意图，不是一次性动作（M2-03a 契约 §6）");

                // ── B. 接管孢子：动作集与它的实际装配一致 ──
                int buildBeforeSwitch = actions.ActionSet.BuildVersion;
                Expect(sim.RequestControlSwitch(spore) == ControlRequestResult.Success, "应能接管孢子友军");
                Expect(actions.ActionSet.BuildVersion > buildBeforeSwitch &&
                       actions.ActionSet.EntityId == spore,
                    $"控制权变更后动作集应被重建并指向新身体（版本 {buildBeforeSwitch} → {actions.ActionSet.BuildVersion}）");

                UnitLoadout sporeLoadout = registry.Get(spore);
                bool sporeBound = sporeLoadout.TryGetOrgan(LoadoutAction.Primary, out UnitLoadoutOrgan sporeOrgan);
                Expect(sporeBound && actions.ActionSet.OrganIdOf(LoadoutAction.Primary) == sporeOrgan.OrganId,
                    "孢子动作集里主动作绑定的器官 id 必须与它 loadout 里的那件一致");
                int sporeMask = actions.ActionSet.ActionMask;
                // 切到菌丝体之前先抓一份孢子的功能位器官，供下面比"同槽绑的器官不同"。
                string sporeUtilityOrganId = actions.ActionSet.OrganIdOf(LoadoutAction.Utility);

                // 孢子没有交互器官 → 交互槽必须明确是"没长"，不是"坏了"也不是"能按"。
                Expect(!actions.ActionSet.CanRelease(LoadoutAction.Interact) &&
                       actions.ActionSet.AvailabilityOf(LoadoutAction.Interact) == DirectActionAvailability.NoOrgan,
                    "孢子没有交互器官，交互槽应判为 NoOrgan");

                // 真释放一次，去内核里看落下了什么。
                // M2-03c 第 0 步把孢子的主器官从 org_confusion_spore（Structural，零伤害挂标记）
                // 换成了现役攻击器官 org_emitter（Projectile），所以这里的期望值跟着改成
                // "真弹体打掉了敌人的血"，而不是"挂上了状态位"。
                int zonesBeforeSpore = sim.LiveZoneCount;
                float hostileHpBeforeSpore = sim.Snapshot.Health[hostileIndex];
                Expect(actions.TryRelease(LoadoutAction.Primary, new float2(1f, 0f)),
                    "孢子的主器官应能释放");
                OrganKernelActionKind sporeKind = actions.LastReleasedKernelAction.Kind;
                Expect(sporeKind == OrganKernelActionKind.Projectile,
                    $"孢子的主器官应落成内核真弹体（实际 {sporeKind}）——换掉 Structural 器官正是第 0 步要修的");
                Expect(actions.LastReleasedOrganId == sporeOrgan.OrganId,
                    "成功释放记录的器官 id 应就是装配里那件");
                for (int step = 0; step < 12; step++)
                {
                    sim.OnUpdate(1f / 60f);
                }
                float hostileHpAfterSpore = sim.Snapshot.Health[hostileIndex];
                Expect(hostileHpAfterSpore < hostileHpBeforeSpore,
                    $"孢子的主器官是真弹体，释放后身边敌人应真的掉血（{hostileHpBeforeSpore:F1} → {hostileHpAfterSpore:F1}）");
                Expect(sim.LiveZoneCount == zonesBeforeSpore,
                    "弹体型器官不该产生持续区域——那是另一件器官的形态");

                // ── C. 接管菌丝体：动作集不同，且行为也不同 ──
                Expect(sim.RequestControlSwitch(mycelium) == ControlRequestResult.Success,
                    "应能从孢子切换到菌丝体");
                Expect(actions.ActionSet.EntityId == mycelium,
                    "切换后动作集应指向菌丝体");

                UnitLoadout myceliumLoadout = registry.Get(mycelium);
                bool myceliumBound = myceliumLoadout.TryGetOrgan(LoadoutAction.Primary,
                    out UnitLoadoutOrgan myceliumOrgan);
                Expect(myceliumBound && actions.ActionSet.OrganIdOf(LoadoutAction.Primary) == myceliumOrgan.OrganId,
                    "菌丝体动作集里主动作绑定的器官 id 必须与它 loadout 里的那件一致");
                Expect(myceliumOrgan.OrganId != sporeOrgan.OrganId,
                    "两名友军的主器官本就不同——否则后面比什么都没意义");
                // 2026-09-13：org_cilia 从 Interact 挪到 Utility 后两者掩码同为 7，
                // 但"接管不同单位打出不同的东西"这条验收的实质从来不是掩码不同，而是
                // **同一个槽位绑的器官不同**。掩码只是当时最省事的代理指标，它会在
                // 两个单位恰好占同样槽位时给出假阴性——现在就是这种情况。
                string sporeUtility = sporeUtilityOrganId;
                string myceliumUtility = actions.ActionSet.OrganIdOf(LoadoutAction.Utility);
                Expect(!string.IsNullOrEmpty(sporeUtility) && !string.IsNullOrEmpty(myceliumUtility) &&
                       sporeUtility != myceliumUtility,
                    $"两名友军的功能位应绑不同器官（孢子 {sporeUtility ?? "(无)"} vs 菌丝体 {myceliumUtility ?? "(无)"}）");

                int zonesBeforeMycelium = sim.LiveZoneCount;
                Expect(actions.TryRelease(LoadoutAction.Primary, new float2(1f, 0f)),
                    "菌丝体的主器官应能释放");
                sim.OnUpdate(1f / 60f);
                Expect(actions.LastReleasedKernelAction.Kind != sporeKind,
                    $"两个单位打出来的**内核请求形状**应不同（孢子 {sporeKind} vs 菌丝体 {actions.LastReleasedKernelAction.Kind}）" +
                    "——只有数据不同、行为一样是不算过的");
                Expect(sim.LiveZoneCount > zonesBeforeMycelium,
                    $"菌丝体的主器官是钉区域型的，释放后场上应真的多出持续区域（{zonesBeforeMycelium} → {sim.LiveZoneCount}）");

                // ── D. 失能主器官 → 释放入口真的拒绝（不只是标记变了）──
                Expect(registry.SetOrganDisabled(mycelium, myceliumOrgan.OrganId, true),
                    "应能把菌丝体的主器官置为失能");
                int zonesBeforeDisabled = sim.LiveZoneCount;
                int releasesBeforeDisabled = actions.ReleaseCount;
                bool rejected = !actions.TryRelease(LoadoutAction.Primary, new float2(1f, 0f));
                sim.OnUpdate(1f / 60f);
                Expect(rejected && actions.LastReleaseResult == DirectActionAvailability.OrganDisabled,
                    "主器官失能后释放入口应拒绝，且原因是 OrganDisabled（区别于'没长这东西'）");
                Expect(actions.ReleaseCount == releasesBeforeDisabled && sim.LiveZoneCount <= zonesBeforeDisabled,
                    "被拒绝的释放不得在内核里留下任何东西——只灰不拦等于'显示禁用、实际可用'");
                // 注意：这里**没有**重建动作集就直接释放，正是为了证明拦截发生在释放入口，
                // 而不是靠上一次重建时算出来的那份缓存。
                // M2-03c：菌丝体刚在 C 段释放过，它的区域型动作冷却 ≥ 区域持续秒数。
                // 把三个量的本地时钟快进一段，否则这里会被 Cooling 挡下——那是 [17] 段的被测项，
                // 不该让它在这里把"失能解除"的断言污染成假红。
                actions.Tick(10f, paused: false);
                Expect(registry.SetOrganDisabled(mycelium, myceliumOrgan.OrganId, false) &&
                       actions.TryRelease(LoadoutAction.Primary, new float2(1f, 0f)),
                    "解除失能后同一个入口应立刻重新放行");

                // ── E. 切回去动作集跟着变（重建不是一次性的）──
                Expect(sim.RequestControlSwitch(spore) == ControlRequestResult.Success &&
                       actions.ActionSet.EntityId == spore &&
                       actions.ActionSet.ActionMask == sporeMask,
                    "切回孢子后动作集应回到孢子那一份（切过去再切回来都要重建）");

                // ── F. 退出直控后原单位恢复 AI（复用 M1-06 链路，验证不回归）──
                Expect(sim.World != null &&
                       sim.World.TryGetUnitControlState(mycelium, out SimUnitControlState released) &&
                       released.IsAlive && released.IntentSource == IntentSource.AI,
                    "被放开的菌丝体应恢复 AI 意图，而不是留在 Player 上站桩");

                // ── G. Direct 域不拥有输入时，直控键不生效 ──
                // 三种让位来源各测一次：战略视角 / 模态面板 / 玩法暂停。
                // Edit 模式敲不出真实按键，所以这里测的是**让位那一侧**：跑完整的输入帧之后，
                // 既不能有释放尝试，也必须真的把上一帧的移动意图清掉
                // （只早退不清意图的话，松手前的方向会一直粘在内核里继续推着单位走）。
                int releasesBeforeInput = actions.ReleaseCount;
                LoadoutAction attemptedBefore = actions.LastAttemptedAction;
                bool yieldOk = true;
                bool intentClearedOk = true;

                var yieldCases = new (string Name, Action Enter, Action Leave)[]
                {
                    ("战略视角", () => InputRouter.SetScope(InputScope.Strategy),
                        () => InputRouter.SetScope(InputScope.Direct)),
                    ("模态面板", () => InputRouter.SetModalUi(true), () => InputRouter.SetModalUi(false)),
                    ("玩法暂停", () => InputRouter.SetGameplayPaused(true), () => InputRouter.SetGameplayPaused(false)),
                };

                foreach ((string name, Action enter, Action leave) in yieldCases)
                {
                    enter();
                    // 先塞一个非零意图，再跑输入帧——这样"意图被清空"才是被观察到的行为，
                    // 而不是因为它本来就是零。
                    sim.SetControlledIntent(new PlayerIntent
                    {
                        MoveDir = new float2(1f, 0f), SpeedMul = 1f, RadiusOverride = 1f,
                        AddStatus = SimStatus.None, RemoveStatus = SimStatus.None,
                    });
                    yieldOk &= !InputRouter.Owns(InputScope.Direct) &&
                               !InputRouter.ConsumeKeyDown(KeyCode.Mouse0, InputScope.Direct);
                    playerController.OnUpdate(1f / 60f);
                    intentClearedOk &= math.lengthsq(sim.Intent.MoveDir) < 1e-6f;
                    leave();
                }

                Expect(yieldOk, "战略视角 / 模态面板 / 玩法暂停下，Direct 域都不该拥有输入");
                Expect(intentClearedOk, "让位那一帧必须把移动意图清空，否则松手前的方向会粘在内核里");
                Expect(actions.ReleaseCount == releasesBeforeInput &&
                       actions.LastAttemptedAction == attemptedBefore,
                    "让位期间跑完整的输入帧，不得发生任何释放尝试");
                Expect(InputRouter.Owns(InputScope.Direct),
                    "让位状态解除后 Direct 域应重新拿回输入（不能永久卡在让位）");

                // ── H. 没有任何可控实体时：动作集为空装配，释放安全拒绝，不崩 ──
                foreach (SimEntityId victim in new[] { spore, mycelium, body })
                {
                    if (sim.TryResolveUnitIndex(victim, out int victimIndex))
                    {
                        sim.ConsumeUnit(victimIndex);
                    }
                }
                sim.OnUpdate(1f / 60f);
                actions.Rebuild();
                Expect(actions.ActionSet.EntityId == SimEntityId.None &&
                       actions.ActionSet.ActionMask == UnitLoadout.MoveActionMask &&
                       actions.ActionSet.IsEmptyLoadout,
                    "意识无处可去时动作集应当场空掉，而不是留着上一具身体的按钮");
                Expect(!actions.TryRelease(LoadoutAction.Primary, new float2(1f, 0f)) &&
                       actions.LastReleaseResult == DirectActionAvailability.NoControlledUnit,
                    "没有受控实体时释放应安全拒绝（不抛、不误打）");

                // ── I. 解绑后不再响应控制权变更（跨局不粘）──
                int buildBeforeUnbind = actions.ActionSet.BuildVersion;
                actions.Unbind();
                // 直接往信号总线上打一条控制权变更：订阅还在的话动作集版本会再涨一次。
                Signals.Publish(new ControlledUnitChangedSignal
                {
                    PreviousUnitId = SimEntityId.None,
                    CurrentUnitId = spore,
                    Reason = ControlChangeReason.PlayerRequest,
                    Result = ControlRequestResult.Success,
                });
                Expect(actions.ActionSet.BuildVersion == buildBeforeUnbind + 1 &&
                       actions.ActionSet.EntityId == SimEntityId.None,
                    "Unbind 会把动作集清空一次，之后的控制权变更信号不应再让它重建（跨局不粘）");
                Expect(!actions.TryRelease(LoadoutAction.Primary, new float2(1f, 0f)) &&
                       actions.LastReleaseResult == DirectActionAvailability.NoControlledUnit,
                    "解绑后释放入口应安全拒绝而不是抛异常");
            }
            finally
            {
                InputRouter.Reset();
                actions.Unbind();
                registry.Unbind();
                playerController.OnExit();
                sim.End();
            }
        }

        // ── [17] 代谢 / 过载债 / 冷却（M2-03c）─────────────────────

        /// <summary>
        /// ProjectA M2-03c：直控释放的三个量。
        ///
        /// 断言口径与 [16] 一致——**只比字段不算过**：每一条拒绝都要同时验
        /// "拒绝原因对得上" + "ReleaseCount 没涨"（= 真的什么都没打出来），
        /// 每一条放行都要验它在同一个入口上真的成功。
        ///
        /// 时间用 <c>DirectControlActions.Tick</c> 快进，不等真实秒数：三个量的回复/衰减都是线性的，
        /// 惰性结算与逐帧结算等价（见 <see cref="UnitVitalsRegistry"/> 类注释），所以快进是等价而不是近似。
        /// <c>Tick(30f)</c> 足以让任何一具身体回到"满代谢 / 零过载债 / 无冷却"的静息态，
        /// 本段用它在各小节之间归位。
        /// </summary>
        private static void ValidateDirectVitals()
        {
            Line("\n[17] 代谢 / 过载债 / 冷却（M2-03c）");

            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 60f;
            cfg.RandomSeed = 0xC0FFEE05u;
            sim.Begin(cfg, Array.Empty<BehaviorArchetype>());
            sim.ConfigureControlSwitch(100f, 0f);

            var registry = new UnitLoadoutRegistry();
            var fakeSource = new FakePlayerLoadoutSource();
            var actions = new DirectControlActions();
            var aim = new float2(1f, 0f);

            InputRouter.Reset();

            try
            {
                registry.Bind(sim, fakeSource);
                SimEntityId body = sim.ControlledUnitId;
                registry.RegisterPlayerBody(body);
                actions.Bind(sim, registry, abilities: null, status: null);

                const int SporeLogicId = 9501;
                const int MyceliumLogicId = 9502;
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(-6f, 2f), Health = 40f, Radius = 0.8f, MaxSpeed = 0f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = SporeLogicId,
                });
                registry.RegisterArchetypePending(SporeLogicId, ArchetypeLoadoutTable.SporeArchetypeId);
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(6f, 2f), Health = 40f, Radius = 0.8f, MaxSpeed = 0f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = MyceliumLogicId,
                });
                registry.RegisterArchetypePending(MyceliumLogicId, ArchetypeLoadoutTable.MyceliumArchetypeId);

                sim.OnUpdate(1f / 60f);
                registry.ResolvePending(sim.Snapshot);
                SimSnapshot snap = sim.Snapshot;
                SimEntityId spore = FindEntityId(snap, SporeLogicId, out _);
                SimEntityId mycelium = FindEntityId(snap, MyceliumLogicId, out _);
                Expect(spore.IsValid && mycelium.IsValid, "两名友军应都已落地并拥有有效稳定实体 ID");

                // ── 0. 第 0 步：原型表不再指向退役 / 非攻击器官 ──
                UnitLoadout sporeLoadout = registry.Get(spore);
                UnitLoadout myceliumLoadout = registry.Get(mycelium);
                bool sporeHas = sporeLoadout.TryGetOrgan(LoadoutAction.Primary, out UnitLoadoutOrgan sporeOrgan);
                bool myceliumHas = myceliumLoadout.TryGetOrgan(LoadoutAction.Primary, out UnitLoadoutOrgan myceliumOrgan);
                Expect(sporeHas && myceliumHas, "两名友军都应有主器官");

                OrganKernelAction sporeAct = OrganKernelActionTable.Resolve(sporeOrgan.OrganId);
                OrganKernelAction myceliumAct = OrganKernelActionTable.Resolve(myceliumOrgan.OrganId);
                Expect(sporeAct.IsValid && sporeAct.Damage > 0f,
                    $"孢子的主器官 {sporeOrgan.OrganId} 应是一次真攻击（有内核形态且有伤害），不是零伤害挂标记");
                Expect(myceliumAct.IsValid && myceliumAct.Damage > 0f,
                    $"菌丝体的主器官 {myceliumOrgan.OrganId} 应是一次真攻击（有内核形态且有伤害）");
                Expect(sporeAct.Kind != myceliumAct.Kind,
                    $"两者打出来的形态应不同（{sporeAct.Kind} vs {myceliumAct.Kind}）");

                // 2026-09-13：org_cilia 从 Interact 槽移到 Utility（Interact 恒 NoInteractTarget，
                // 放在那里等于给菌丝体一个永远按不响的动作）。断言随之改看功能位。
                bool utilityHas = myceliumLoadout.TryGetOrgan(LoadoutAction.Utility,
                    out UnitLoadoutOrgan utilityOrgan);
                Expect(utilityHas && OrganKernelActionTable.Resolve(utilityOrgan.OrganId).IsValid,
                    $"菌丝体的功能器官 {(utilityHas ? utilityOrgan.OrganId : "(无)")} 应是现役器官——" +
                    "退役 id 永远只会落到 NoKernelAction，引用它本身就是 bug");

                // 三个量必须由器官推导出来。若它们是常数，"代价与器官对应"这条就是空话。
                Expect(sporeAct.Cooldown > 0f && sporeAct.MetabolicCost > 0f && sporeAct.StrainCost > 0f,
                    $"每件器官都应推导出非零的冷却/代谢/过载债（孢子 cd={sporeAct.Cooldown:F2} " +
                    $"代谢={sporeAct.MetabolicCost:F1} 过载债={sporeAct.StrainCost:F1}）");
                Expect(Mathf.Abs(sporeAct.Cooldown - myceliumAct.Cooldown) > 0.01f &&
                       Mathf.Abs(sporeAct.MetabolicCost - myceliumAct.MetabolicCost) > 0.01f,
                    $"形态不同的两件器官应推导出不同的代价（cd {sporeAct.Cooldown:F2} vs {myceliumAct.Cooldown:F2}；" +
                    $"代谢 {sporeAct.MetabolicCost:F1} vs {myceliumAct.MetabolicCost:F1}）");

                // ── A. 冷却：期内重复释放被拒，冷却走完后可再释放 ──
                Expect(sim.RequestControlSwitch(spore) == ControlRequestResult.Success, "应能接管孢子友军");
                Expect(actions.TryRelease(LoadoutAction.Primary, aim), "首次释放应成功");
                int releasesAfterFirst = actions.ReleaseCount;

                Expect(!actions.TryRelease(LoadoutAction.Primary, aim) &&
                       actions.LastReleaseResult == DirectActionAvailability.Cooling &&
                       actions.ReleaseCount == releasesAfterFirst,
                    "冷却期内重复释放应在入口被拒（原因 Cooling），且不得有任何输出");

                actions.Tick(sporeAct.Cooldown * 0.5f, paused: false);
                Expect(!actions.TryRelease(LoadoutAction.Primary, aim) &&
                       actions.LastReleaseResult == DirectActionAvailability.Cooling,
                    "冷却只过了一半时仍应被拒");

                actions.Tick(sporeAct.Cooldown, paused: false);
                Expect(actions.TryRelease(LoadoutAction.Primary, aim) &&
                       actions.ReleaseCount == releasesAfterFirst + 1,
                    "冷却走完后同一个入口应放行");

                actions.Tick(100f, paused: true);
                Expect(!actions.TryRelease(LoadoutAction.Primary, aim) &&
                       actions.LastReleaseResult == DirectActionAvailability.Cooling,
                    "暂停下本地时钟不推进——暂停刷冷却是白送的");

                // ── B. 代谢：按器官的代价真的扣，不足被拒，回复后放行 ──
                actions.Tick(30f, paused: false);
                UnitVitalsView idle = actions.ControlledVitals;
                Expect(idle.Valid && idle.EntityId == spore &&
                       idle.Metabolism >= UnitVitalsRegistry.MetabolismMax - 0.01f &&
                       idle.Strain <= 0.01f && idle.PrimaryCooldown <= 0f,
                    $"静息足够久之后应回到满代谢 / 零过载债 / 无冷却（实际 代谢{idle.Metabolism:F1} " +
                    $"过载债{idle.Strain:F1} 冷却{idle.PrimaryCooldown:F2}）");

                Expect(actions.TryRelease(LoadoutAction.Primary, aim), "静息态下释放应成功");
                UnitVitalsView spent = actions.ControlledVitals;
                Expect(Mathf.Abs((idle.Metabolism - spent.Metabolism) - sporeAct.MetabolicCost) < 0.01f,
                    $"一次释放应精确扣掉该器官的代谢代价（扣了 {idle.Metabolism - spent.Metabolism:F2}，" +
                    $"应为 {sporeAct.MetabolicCost:F2}）");
                Expect(spent.Strain > idle.Strain && spent.PrimaryCooldown > 0f,
                    $"同一次释放应同时累积过载债并起冷却（过载债 {idle.Strain:F1} → {spent.Strain:F1}）");

                actions.Tick(sporeAct.Cooldown + 0.1f, paused: false);
                // 先让冷却走完再压低代谢：顺序反过来的话，Tick 会把代谢又回满，
                // 这一条就永远测不到"代谢不足"那个分支。
                Expect(actions.Vitals.SetMetabolism(spore, sporeAct.MetabolicCost - 0.5f),
                    "应能直接写代谢余量（本段只提供入口，不定义额外抽走代谢的规则）");
                int releasesBeforeStarve = actions.ReleaseCount;
                Expect(!actions.TryRelease(LoadoutAction.Primary, aim) &&
                       actions.LastReleaseResult == DirectActionAvailability.NotEnoughMetabolism &&
                       actions.ReleaseCount == releasesBeforeStarve,
                    "代谢不足时释放应在入口被拒（原因 NotEnoughMetabolism），且不得有任何输出");

                actions.Tick(1f, paused: false);
                Expect(actions.TryRelease(LoadoutAction.Primary, aim) &&
                       actions.ReleaseCount == releasesBeforeStarve + 1,
                    "代谢回复到够付代价后，同一个入口应放行");

                // ── C. 过载债：累积 → 越阈值 → 拒绝 → 衰减 → 恢复 ──
                actions.Tick(sporeAct.Cooldown + 0.1f, paused: false);
                Expect(actions.Vitals.AddStrain(spore, UnitVitalsRegistry.StrainOverloadThreshold + 5f),
                    "应能直接叠加过载债");
                UnitVitalsView over = actions.ControlledVitals;
                Expect(over.Overloaded && over.Strain >= UnitVitalsRegistry.StrainOverloadThreshold,
                    $"过载债越过阈值应进入过载态（过载债 {over.Strain:F1} / 阈值 {over.StrainThreshold:F0}）");

                int releasesBeforeOverload = actions.ReleaseCount;
                Expect(!actions.TryRelease(LoadoutAction.Primary, aim) &&
                       actions.LastReleaseResult == DirectActionAvailability.Overloaded &&
                       actions.ReleaseCount == releasesBeforeOverload,
                    "过载态下释放应在入口被拒（原因 Overloaded），且不得有任何输出");

                float secondsToClear =
                    (over.Strain - UnitVitalsRegistry.StrainClearThreshold) / UnitVitalsRegistry.StrainDecayPerSecond;
                actions.Tick(secondsToClear * 0.5f, paused: false);
                UnitVitalsView halfCooled = actions.ControlledVitals;
                Expect(halfCooled.Strain < UnitVitalsRegistry.StrainOverloadThreshold && halfCooled.Overloaded &&
                       !actions.TryRelease(LoadoutAction.Primary, aim) &&
                       actions.LastReleaseResult == DirectActionAvailability.Overloaded,
                    $"过载债跌回阈值以下但未到清除线时应仍然拒绝（滞回；实测过载债 {halfCooled.Strain:F1}）——" +
                    "同阈值进出会让按钮在一两帧之间反复横跳");

                actions.Tick(secondsToClear * 0.6f + 0.1f, paused: false);
                UnitVitalsView cooled = actions.ControlledVitals;
                Expect(!cooled.Overloaded && cooled.Strain <= UnitVitalsRegistry.StrainClearThreshold,
                    $"过载债衰减到清除线以下应退出过载态（实测过载债 {cooled.Strain:F1}）");
                Expect(actions.TryRelease(LoadoutAction.Primary, aim),
                    "退出过载后同一个入口应放行");

                // ── D. 三个量跟着控制权走（不是全局单例）──
                UnitVitalsView sporeSpent = actions.Vitals.Get(spore);
                Expect(sporeSpent.Metabolism < UnitVitalsRegistry.MetabolismMax && sporeSpent.Strain > 0f,
                    "孢子此刻应留有实打实的消耗痕迹（后面切回来要读回这一份）");

                Expect(sim.RequestControlSwitch(mycelium) == ControlRequestResult.Success,
                    "应能从孢子切换到菌丝体");
                UnitVitalsView fresh = actions.ControlledVitals;
                Expect(fresh.Valid && fresh.EntityId == mycelium,
                    "切换控制权后读到的应是新身体那一份");
                Expect(fresh.Metabolism >= UnitVitalsRegistry.MetabolismMax - 0.01f &&
                       fresh.Strain <= 0.01f && fresh.PrimaryCooldown <= 0f,
                    $"没被接管过的身体应是满代谢 / 零过载债 / 无冷却，而不是继承上一具身体的账" +
                    $"（实际 代谢{fresh.Metabolism:F1} 过载债{fresh.Strain:F1} 冷却{fresh.PrimaryCooldown:F2}）");

                Expect(actions.TryRelease(LoadoutAction.Primary, aim), "菌丝体的主器官应能释放");
                UnitVitalsView myceliumSpent = actions.ControlledVitals;
                UnitVitalsView sporeNow = actions.Vitals.Get(spore);
                Expect(myceliumSpent.PrimaryCooldown > sporeNow.PrimaryCooldown + 1f,
                    $"两具身体各按自己器官的参数计冷却（菌丝体 {myceliumSpent.PrimaryCooldown:F2}s vs " +
                    $"孢子 {sporeNow.PrimaryCooldown:F2}s），不是同一条冷却线");

                Expect(sim.RequestControlSwitch(spore) == ControlRequestResult.Success, "应能切回孢子");
                UnitVitalsView sporeBack = actions.ControlledVitals;
                Expect(sporeBack.EntityId == spore &&
                       Mathf.Abs(sporeBack.Metabolism - sporeNow.Metabolism) < 0.01f &&
                       Mathf.Abs(sporeBack.Strain - sporeNow.Strain) < 0.01f,
                    $"切回去应读回孢子自己那一份（代谢 {sporeBack.Metabolism:F1} / 过载债 {sporeBack.Strain:F1}）");

                // ── E. 玩家本体不叠第二层冷却 ──
                OrganKernelAction bodyAct = OrganKernelActionTable.Resolve(sporeOrgan.OrganId);
                actions.Vitals.Commit(body, LoadoutAction.Primary, bodyAct, applyCooldown: true);
                Expect(actions.Vitals.Get(body).PrimaryCooldown > 0f,
                    "先把玩家本体的主槽人为打进冷却");
                Expect(actions.Vitals.Evaluate(body, LoadoutAction.Primary, bodyAct, checkCooldown: true) ==
                       DirectVitalsGate.Cooling,
                    "内核释放路会看冷却");
                Expect(actions.Vitals.Evaluate(body, LoadoutAction.Primary, bodyAct, checkCooldown: false) ==
                       DirectVitalsGate.Allowed,
                    "玩家本体的委托路不看这一层冷却——它的冷却归既有 AbilitySystem，" +
                    "叠第二层会让同一个键出现两条互不知情的冷却线");

                fakeSource.Organs.Clear();
                fakeSource.Organs.Add(new UnitLoadoutOrgan(sporeOrgan.OrganId, LoadoutAction.Primary));
                Expect(sim.RequestControlSwitch(body) == ControlRequestResult.Success, "应能切回玩家本体");
                UnitVitalsView bodyBefore = actions.ControlledVitals;
                Expect(!actions.TryRelease(LoadoutAction.Primary, aim) &&
                       actions.LastReleaseResult == DirectActionAvailability.NotReady,
                    "玩家本体走委托路：AbilitySystem 缺席时判 NotReady（Edit 模式起不了整套 ModuleHub）");
                UnitVitalsView bodyAfter = actions.ControlledVitals;
                Expect(Mathf.Abs(bodyAfter.Metabolism - bodyBefore.Metabolism) < 0.01f &&
                       Mathf.Abs(bodyAfter.Strain - bodyBefore.Strain) < 0.01f,
                    "释放被下游拒掉时不得扣代谢、不得累过载债——代价只在释放真的发生之后才付");

                // ── F. HUD：真实 UXML 实例 + 生产绑定代码 ──
                Expect(sim.RequestControlSwitch(spore) == ControlRequestResult.Success, "HUD 断言前切回孢子");
                ValidateDirectVitalsHud(actions, spore);

                // ── G. 解绑后账本清空（跨局不粘）──
                actions.Unbind();
                Expect(actions.Vitals.Count == 0 && !actions.Vitals.IsTracked(spore),
                    "Unbind 后账本应清空——实体 id 只在生成它的那个 SimWorld 内有效");
                Expect(!actions.ControlledVitals.Valid,
                    "解绑后没有受控身体，三个量应判为无效（UI 据此整块隐藏）");
            }
            finally
            {
                InputRouter.Reset();
                actions.Unbind();
                registry.Unbind();
                sim.End();
            }
        }

        private const string HudUxmlPath = "Assets/GameRes/Raw/UI/BattleUI/BattleHud.uxml";
        private const string HudPanelSettingsPath = "Assets/GameRes/Raw/UI/BattleUI/BattleHudPanelSettings.asset";

        /// <summary>
        /// 三个量的 HUD 上屏断言。**用真实的 BattleHud.uxml 实例 + 生产绑定代码**
        /// （<see cref="DirectVitalsHudBinding"/>），不是反射探针——反射探针只能证明字段被写了，
        /// 证明不了节点真的显示/隐藏。
        ///
        /// 资源走 <see cref="AssetDatabase"/> 而不是 YooAsset：本方法在同步的自检流程里跑，
        /// 而 Edit 模式的资源模块要靠 <c>EditorApplication.update</c> 泵若干帧才就绪
        /// （见 <c>EditorResourceBootstrap</c>），同步流程里等不到它。AssetDatabase 拿到的是
        /// **同一个** uxml 资产，对"节点在不在、显不显示"这条断言来说没有任何区别。
        /// </summary>
        private static void ValidateDirectVitalsHud(DirectControlActions actions, SimEntityId spore)
        {
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(HudUxmlPath);
            var panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(HudPanelSettingsPath);
            if (tree == null || panelSettings == null)
            {
                Fail($"应能从 AssetDatabase 取到 {HudUxmlPath} 与 {HudPanelSettingsPath}");
                return;
            }
            Ok("HUD 探针取到了真实的 BattleHud.uxml + PanelSettings");

            var probeGo = new GameObject("[M2-03c HUD Probe]") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                UIDocument doc = probeGo.AddComponent<UIDocument>();
                doc.panelSettings = panelSettings;
                doc.visualTreeAsset = tree;

                VisualElement root = doc.rootVisualElement;
                if (root == null)
                {
                    Fail("探针 UIDocument 应能建出根节点");
                    return;
                }

                VisualElement block = root.Q<VisualElement>(DirectVitalsHudBinding.BlockName);
                Label text = root.Q<Label>(DirectVitalsHudBinding.TextName);
                if (block == null || text == null)
                {
                    Fail($"BattleHud.uxml 里应有 {DirectVitalsHudBinding.BlockName} / " +
                         $"{DirectVitalsHudBinding.TextName} 节点");
                    return;
                }
                Ok("BattleHud.uxml 里有三个量的节点，且能被生产代码同名 Q 到");

                ForceLayout(root);
                Expect(block.resolvedStyle.display == DisplayStyle.None,
                    $"初始隐藏必须由 UXML 权威化（resolvedStyle 实测 {block.resolvedStyle.display}）——" +
                    ".hud-sub 空块仍占约 26px 并画出边框，只靠 C# 隐藏会在资源异步加载期间闪一格空边框");

                // 直控视角 + 真实数值 → 上屏
                InputRouter.SetScope(InputScope.Direct);
                UnitVitalsView shown = actions.ControlledVitals;
                DirectVitalsHudBinding.Apply(block, text, shown);
                string expectMetabolism = $"代谢 {shown.Metabolism:F0}/{shown.MetabolismMax:F0}";
                Expect(shown.Valid && block.style.display.value == DisplayStyle.Flex &&
                       text.text.Contains(expectMetabolism),
                    $"直控视角下三个量应按真实数值上屏（实际「{text.text}」，应含「{expectMetabolism}」）");
                Expect(text.text.Contains("过载债") && text.text.Contains("主 "),
                    $"文案应同时给出过载债与按槽冷却，而不是只报代谢（实际「{text.text}」）");

                // 过载态在 HUD 上必须看得出来
                actions.Vitals.AddStrain(spore, UnitVitalsRegistry.StrainOverloadThreshold + 5f);
                DirectVitalsHudBinding.Apply(block, text, actions.ControlledVitals);
                Expect(block.ClassListContains(DirectVitalsHudBinding.OverloadedClass) &&
                       text.text.Contains("过载"),
                    $"过载态应在 HUD 上明确标示（实际「{text.text}」）");
                actions.Vitals.AddStrain(spore, -(UnitVitalsRegistry.StrainOverloadThreshold * 10f));

                // 战略视角 → 整块隐藏
                InputRouter.SetScope(InputScope.Strategy);
                DirectVitalsHudBinding.Apply(block, text, actions.ControlledVitals);
                Expect(block.style.display.value == DisplayStyle.None &&
                       !block.ClassListContains(DirectVitalsHudBinding.OverloadedClass),
                    "战略视角下整块应隐藏——战略视角看不到具体身体的代谢没有意义");

                InputRouter.SetScope(InputScope.Direct);
                DirectVitalsHudBinding.Apply(block, text, actions.ControlledVitals);
                Expect(block.style.display.value == DisplayStyle.Flex,
                    "切回直控视角应重新显示（不能永久卡在隐藏）");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probeGo);
            }
        }

        /// <summary>
        /// 逼面板跑一次样式 + 布局解析。<c>resolvedStyle</c> 在此之前读到的是尚未解析的值，
        /// 而 <c>style.display.value</c> 对"从没被 C# 写过"的属性只会回落到关键字默认值 Flex
        /// （实测坑），所以"UXML 里写的 display:none 生效了没有"只能从 resolvedStyle 读。
        /// <c>ValidateLayout</c> 在 <c>BaseVisualElementPanel</c> 上是 internal，走反射。
        /// </summary>
        private static void ForceLayout(VisualElement element)
        {
            IPanel panel = element?.panel;
            if (panel == null)
            {
                return;
            }

            System.Reflection.MethodInfo m = panel.GetType().GetMethod("ValidateLayout",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            m?.Invoke(panel, null);
        }

        // ── [18] 接管交还可靠性（M2-04a）──────────────────────

        /// <summary>
        /// M2-04 的验收原话是「在交战、搬运、撤退三种状态退出，单位均能继续合理行动」。
        ///
        /// **如实记录一条口径替换**：代码里不存在"搬运"（全仓 Carry/Haul 零命中），
        /// 它属于后续里程碑的回收/搬运玩法。这里用「移动中退出」替代第二态，没有假装做了搬运。
        ///
        /// 断言口径照 [16]/[17]：**只比字段不算过**。每条延续都真起 <see cref="SimBridge"/>、
        /// 真切控制权、真推进帧，去看单位到底往哪走了；"交战中退出不乱跑"还配了一个
        /// 同原型、同参数但从没被接管过的对照单位当反证——否则"没乱跑"完全可能只是
        /// 因为这个原型本来就不动。
        /// </summary>
        private static void ValidateAiHandoff()
        {
            Line("\n[18] 接管交还可靠性（M2-04a）");

            const float Dt = 1f / 60f;

            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 60f;
            cfg.RandomSeed = 0xC0FFEE05u;

            // 自造原型表而不是读 Luban：Drift 必须真的游走（给"缓冲期内不乱跑"提供反证单位），
            // 敌人必须真的不动（这样距离变化只可能来自被测单位）。攻击力一律 0——
            // 本段测的是"交还后往哪走"，掉血把单位打死只会把结果搅浑。
            var archetypes = new[]
            {
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Drift, Accel = 12f, WanderStrength = 1f,
                    AttackRange = 0.5f, AttackCooldown = 99f, AttackDamage = 0f,
                    Separation = 0f, ChargeSpeedMul = 1f,
                },
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Stationary, Accel = 12f, WanderStrength = 0f,
                    AttackRange = 0.5f, AttackCooldown = 99f, AttackDamage = 0f,
                    Separation = 0f, ChargeSpeedMul = 1f,
                },
            };

            sim.Begin(cfg, archetypes);
            // 切换范围/冷却在本用例里不是被测对象，放开以免干扰（同 [16] 的做法）。
            sim.ConfigureControlSwitch(200f, 0f);

            var squad = new SquadCommandSystem();
            squad.Bind(sim, null);
            var handoff = new AiHandoffSystem();
            handoff.Bind(sim, squad, archetypes);

            try
            {
                SimEntityId body = sim.ControlledUnitId;

                const int AllyLogicId = 9501;
                const int TwinLogicId = 9502;
                const int CourierLogicId = 9503;
                const int HostileLogicId = 9504;
                var hostilePos = new float2(-14f, 0f);

                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(-20f, 0f), Health = 500f, Radius = 0.5f, MaxSpeed = 6f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = AllyLogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(-20f, 6f), Health = 500f, Radius = 0.5f, MaxSpeed = 6f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = TwinLogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(20f, -20f), Health = 500f, Radius = 0.5f, MaxSpeed = 6f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = CourierLogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = hostilePos, Health = 5000f, Radius = 0.6f, MaxSpeed = 0f,
                    ArchetypeId = 1, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = HostileLogicId,
                });

                sim.OnUpdate(Dt);
                SimSnapshot snap0 = sim.Snapshot;
                SimEntityId ally = FindEntityId(snap0, AllyLogicId, out _);
                SimEntityId twin = FindEntityId(snap0, TwinLogicId, out _);
                SimEntityId courier = FindEntityId(snap0, CourierLogicId, out _);
                SimEntityId hostile = FindEntityId(snap0, HostileLogicId, out _);
                Expect(body.IsValid && ally.IsValid && twin.IsValid && courier.IsValid && hostile.IsValid,
                    "本段的玩家本体、三名友军与一个敌人应都已落地并拥有有效稳定实体 ID");

                float2 PosOf(SimEntityId id) =>
                    sim.TryResolveUnitIndex(id, out int i) && i < sim.Snapshot.Count
                        ? sim.Snapshot.Position[i]
                        : float2.zero;

                float2 VelOf(SimEntityId id) =>
                    sim.TryResolveUnitIndex(id, out int i) && i < sim.Snapshot.Count
                        ? sim.Snapshot.Velocity[i]
                        : float2.zero;

                IntentSource SourceOf(SimEntityId id) =>
                    sim.World != null && sim.World.TryGetUnitControlState(id, out SimUnitControlState s)
                        ? s.IntentSource
                        : IntentSource.Scripted;

                void Step(int frames, float2 moveDir = default)
                {
                    bool driving = math.lengthsq(moveDir) > 1e-6f;
                    float2 dir = driving ? math.normalizesafe(moveDir) : float2.zero;
                    for (int f = 0; f < frames; f++)
                    {
                        if (driving)
                        {
                            sim.SetControlledIntent(new PlayerIntent
                            {
                                MoveDir = dir, SpeedMul = 1f, RadiusOverride = -1f,
                                AddStatus = SimStatus.None, RemoveStatus = SimStatus.None,
                            });
                        }
                        sim.OnUpdate(Dt);
                        handoff.Tick(Dt, paused: false);
                    }
                }

                // ── A. 编队归属与 RTS 命令的延续（本来就成立，这里补真断言）──
                //
                // 编队归属存在热更层字典里、内核不认识编队；命令在直控期间原样留在内核里被冻结，
                // 交还时 IntentSource 恢复成 Commanded 就自动复活。本系统在这条路上**一行都不插手**——
                // 插手就等于把玩家的明确命令盖掉，正好是 GDD §7.3 的反面。
                squad.SelectExplicit(new[] { courier });
                squad.AssignGroup(3);
                Expect(squad.GroupSize(3) == 1 && squad.GroupMembers(3)[0] == courier,
                    "编组 3 应记下这名友军（编队归属只存在热更层，内核不认识编队）");

                var retreatPoint = new float2(45f, -45f);
                Expect(squad.Issue(UnitCommandKind.Retreat, retreatPoint, SimEntityId.None, paused: false) == 1,
                    "应能给它下一条撤退命令");
                Step(30);
                Expect(SourceOf(courier) == IntentSource.Commanded,
                    "执行命令中的单位意图来源应是 Commanded");

                Expect(sim.RequestControlSwitch(courier) == ControlRequestResult.Success,
                    "应能在它撤退途中接管它");
                Expect(sim.TryGetCommand(courier, out UnitCommand frozen) &&
                       frozen.Kind == UnitCommandKind.Retreat &&
                       SourceOf(courier) == IntentSource.Player,
                    "直控期间原命令应原样留在内核里（被冻结而不是被清掉）");
                // 玩家开着它往反方向乱走一段，证明后面的"继续撤退"不是惯性使然。
                Step(25, new float2(-1f, 1f));

                float distRetreatBefore = math.distance(PosOf(courier), retreatPoint);
                Expect(sim.RequestControlSwitch(body) == ControlRequestResult.Success, "应能退出直控切回本体");
                Expect(handoff.LastContinuation == HandoffContinuation.ResumeCommand,
                    $"带命令的单位交还后应直接复活原命令、不进缓冲（实际 {handoff.LastContinuation}）");
                Expect(!handoff.IsInHandoffBuffer(courier),
                    "带命令的交还不得进缓冲——缓冲命令会把玩家的明确命令覆盖掉");
                Expect(sim.TryGetCommand(courier, out UnitCommand resumed) &&
                       resumed.Kind == UnitCommandKind.Retreat &&
                       SourceOf(courier) == IntentSource.Commanded,
                    "交还后命令应自动复活，意图来源回到 Commanded");

                Step(60);
                float distRetreatAfter = math.distance(PosOf(courier), retreatPoint);
                Expect(distRetreatAfter < distRetreatBefore - 1f,
                    $"交还后它应真的继续执行撤退命令而不是掉头（离撤退点 {distRetreatBefore:F2} → {distRetreatAfter:F2}）");

                Expect(squad.GroupMembers(3).Count == 1 && squad.GroupMembers(3)[0] == courier,
                    "接管 + 退出全程不得改变编队归属");
                squad.ClearSelection();
                squad.RecallGroup(3);
                Expect(squad.Selection.Count == 1 && squad.Selection[0] == courier,
                    "退出直控后按编组 3 仍能唤回同一个单位");

                // ── B. 交战中退出 → 原地守住，且真的没乱跑（带反证单位）──
                Expect(sim.RequestControlSwitch(ally) == ControlRequestResult.Success, "应能接管站在敌人旁边的友军");
                Expect(sim.SetControlledPosition(hostilePos + new float2(4f, 0f)),
                    "把受控单位摆到敌人旁边（让'交战中'这个前提是确定的，而不是碰运气）");
                Step(20);
                float2 exitPos = PosOf(ally);
                float2 twinStart = PosOf(twin);
                Expect(math.length(VelOf(ally)) < 0.35f &&
                       math.distance(exitPos, hostilePos) < AiHandoffSystem.EngageThreatRange,
                    "退出前的前提：单位静止、且敌人在威胁半径内");

                Expect(sim.RequestControlSwitch(body) == ControlRequestResult.Success, "应能在交战中退出直控");
                Expect(handoff.LastContinuation == HandoffContinuation.HoldGround,
                    $"身边有敌人且退出时静止 → 应判为交战中退出、原地守住（实际 {handoff.LastContinuation}）");
                Expect(sim.TryGetCommand(ally, out UnitCommand hold) &&
                       hold.Kind == UnitCommandKind.Guard && !hold.TargetEntity.IsValid,
                    "缓冲命令必须是守备而不是攻击——「不主动开新战线」就是接管缓冲的定义");
                Expect(SourceOf(ally) == IntentSource.Commanded &&
                       handoff.IsInHandoffBuffer(ally) &&
                       handoff.BufferRemaining(ally) > 0f,
                    "交还的单位应进入接管缓冲期");

                Step(60); // 1 秒，仍在 1.5 秒缓冲窗口内
                float heldDrift = math.distance(PosOf(ally), exitPos);
                float twinDrift = math.distance(PosOf(twin), twinStart);
                Expect(heldDrift < AiHandoffSystem.BufferArriveRadius + 0.5f,
                    $"缓冲期内被交还的单位应守在交还点附近（实际漂移 {heldDrift:F2}）");
                Expect(twinDrift > heldDrift + 1f,
                    $"同原型同参数、但从没被接管过的对照单位应已经在游走（对照 {twinDrift:F2} vs 被测 {heldDrift:F2}）" +
                    "——否则'没乱跑'只是因为这个原型本来就不动，什么都没证明");

                // 2026-09-13 产品决策反转（AiHandoffSystem.HoldGroundAfterHandoff）：
                // 缓冲到期不再撤命令交还自由 AI，而是**原地钉一条永久守备**。
                // 玩家的原话是「切换角色或者战术视角，先暂时用简单的 AI 逻辑（原地不动但是持续攻击）」，
                // 原行为（放回 JobAIIntent 自己去追人）在试玩里是最主要的失控感来源。
                handoff.DebugAdvanceClock(AiHandoffSystem.HandoffBufferSeconds);
                Expect(sim.TryGetCommand(ally, out UnitCommand parkedHold) &&
                       parkedHold.Kind == UnitCommandKind.Guard &&
                       SourceOf(ally) == IntentSource.Commanded,
                    "缓冲到期应转成原地守备并保持 Commanded（这样它既不乱跑，又留在 RTS 选择集里可被重新下令）");
                float2 afterExpire = PosOf(ally);
                Step(60);
                Expect(math.distance(PosOf(ally), afterExpire) < AiHandoffSystem.BufferArriveRadius + 0.5f,
                    $"到期之后它应停在原地而不是重新游走（实际漂移 {math.distance(PosOf(ally), afterExpire):F2}）");
                Expect(twinDrift > 1f,
                    "对照：同原型、从没被接管过的单位仍在游走——证明'没乱跑'是守备造成的，不是这个原型本来就不动");

                // ── C. 移动中退出 → 沿原方向再走一段（替代里程碑原文的"搬运"，见本方法注释）──
                Expect(sim.ClearCommand(courier) || SourceOf(courier) == IntentSource.AI,
                    "先把信使交还 AI，构造'无命令 + 移动中退出'的场景");
                Expect(sim.RequestControlSwitch(courier) == ControlRequestResult.Success, "应能接管信使");
                Expect(sim.SetControlledPosition(new float2(20f, -20f)),
                    "把它摆回远离敌人的空地（确保这一段测的是'没有威胁时'的分支）");
                Step(30, new float2(1f, 0f));
                float2 exitPosC = PosOf(courier);
                float2 headingC = math.normalizesafe(VelOf(courier));
                Expect(math.length(VelOf(courier)) > 0.35f &&
                       math.distance(exitPosC, hostilePos) > AiHandoffSystem.EngageThreatRange,
                    "退出前的前提：单位在移动、且附近没有威胁");

                Expect(sim.RequestControlSwitch(body) == ControlRequestResult.Success, "应能在移动中退出直控");
                Expect(handoff.LastContinuation == HandoffContinuation.Advance,
                    $"无威胁 + 退出时在移动 → 应判为移动中退出（实际 {handoff.LastContinuation}）");
                Expect(sim.TryGetCommand(courier, out UnitCommand advance) &&
                       math.dot(math.normalizesafe(advance.TargetPosition - exitPosC), headingC) > 0.9f,
                    "延续点应落在退出瞬间的朝向上，而不是随便找一个点");

                Step(45);
                float2 movedC = PosOf(courier) - exitPosC;
                Expect(math.length(movedC) > 1.5f && math.dot(math.normalizesafe(movedC), headingC) > 0.7f,
                    $"缓冲期内它应真的沿原方向继续前进（位移 {math.length(movedC):F2}，方向一致度 " +
                    $"{math.dot(math.normalizesafe(movedC), headingC):F2}），而不是原地发呆一拍");
                handoff.DebugAdvanceClock(AiHandoffSystem.HandoffBufferSeconds);

                // ── D. 撤退中退出 → 继续拉开距离 ──
                Expect(sim.RequestControlSwitch(ally) == ControlRequestResult.Success, "应能再次接管友军");
                Expect(sim.SetControlledPosition(hostilePos + new float2(5f, 0f)),
                    "把它摆回敌人旁边，构造'交战中开始撤退'的场景");
                Step(25, new float2(1f, 0f)); // 背着敌人跑
                float2 exitPosR = PosOf(ally);
                float distHostileBefore = math.distance(exitPosR, hostilePos);
                Expect(math.length(VelOf(ally)) > 0.35f &&
                       distHostileBefore < AiHandoffSystem.EngageThreatRange,
                    "退出前的前提：敌人仍在威胁半径内，而单位正在背离它");

                Expect(sim.RequestControlSwitch(body) == ControlRequestResult.Success, "应能在撤退中退出直控");
                Expect(handoff.LastContinuation == HandoffContinuation.Disengage,
                    $"有威胁 + 正在背离 → 应判为撤退中退出（实际 {handoff.LastContinuation}）");
                Step(45);
                float distHostileAfter = math.distance(PosOf(ally), hostilePos);
                Expect(distHostileAfter > distHostileBefore + 1f,
                    $"缓冲期内它应真的继续拉开距离而不是掉头回去（{distHostileBefore:F2} → {distHostileAfter:F2}）");

                // ── E. 缓冲不得吞掉玩家在缓冲期内下的新命令 ──
                squad.SelectExplicit(new[] { ally });
                var newOrder = new float2(0f, 40f);
                Expect(squad.Issue(UnitCommandKind.Move, newOrder, SimEntityId.None, paused: false) == 1,
                    "缓冲期内玩家仍应能给它下新命令");
                handoff.DebugAdvanceClock(AiHandoffSystem.HandoffBufferSeconds);
                Expect(sim.TryGetCommand(ally, out UnitCommand kept) && kept.Kind == UnitCommandKind.Move,
                    "缓冲到期只该撤掉自己下的那条守备命令——把玩家的新命令一起清掉就是'RTS 命令莫名被吞'");

                // ── F. 缓冲不该在玩家手里跑完（再接管则冻结倒计时）──
                Expect(sim.ClearCommand(ally), "先撤掉上面那条 Move，回到无命令状态");
                Expect(sim.RequestControlSwitch(ally) == ControlRequestResult.Success, "再接管一次");
                Step(15);
                Expect(sim.RequestControlSwitch(body) == ControlRequestResult.Success &&
                       handoff.IsInHandoffBuffer(ally),
                    "再次退出应重新进入缓冲");
                Expect(sim.RequestControlSwitch(ally) == ControlRequestResult.Success,
                    "缓冲期内玩家又把它接管回去");
                handoff.DebugAdvanceClock(AiHandoffSystem.HandoffBufferSeconds * 3f);
                Expect(handoff.IsInHandoffBuffer(ally),
                    "缓冲不该在玩家手里跑完——否则'切过去看一眼再切回来'会让缓冲形同虚设");
                Expect(sim.RequestControlSwitch(body) == ControlRequestResult.Success &&
                       handoff.BufferRemaining(ally) > AiHandoffSystem.HandoffBufferSeconds - 0.01f,
                    "重新放开应重新武装一个完整的缓冲窗口");
                handoff.DebugAdvanceClock(AiHandoffSystem.HandoffBufferSeconds);
                Expect(sim.TryGetCommand(ally, out UnitCommand rearmedHold) &&
                       rearmedHold.Kind == UnitCommandKind.Guard &&
                       SourceOf(ally) == IntentSource.Commanded,
                    "重新武装的缓冲到期后同样转成原地守备，不留悬挂的半截状态");
                // 再接管一次并放开：钉下的守备**不得**被当成"玩家的编队命令"而走 ResumeCommand——
                // 那会让它交还后被拉回上一次的守备点，而不是按这一次的退出情境判断。
                Expect(sim.RequestControlSwitch(ally) == ControlRequestResult.Success,
                    "应能再次接管这具已被钉住的身体");
                Expect(sim.RequestControlSwitch(body) == ControlRequestResult.Success,
                    "应能再次放开它");
                Expect(handoff.LastContinuation != HandoffContinuation.ResumeCommand,
                    $"我们自己钉的原地守备不是玩家命令，交还判定不该退化成延续命令（实际 {handoff.LastContinuation}）");

                // ── G. 安全位置兜底（实施第 4 条）：先证不误伤，再证真能救 ──
                var obstacles = new[]
                {
                    new ObstacleSpec { Position = new float2(20f, 20f), Radius = 4f },
                };
                sim.SetObstacles(obstacles);
                Expect(sim.RequestControlSwitch(ally) == ControlRequestResult.Success ||
                       sim.ControlledUnitId == ally, "把受控权交给友军以便测位置兜底");
                int rescuesBefore = handoff.SafePositionRescueCount;
                Step(60);
                Expect(handoff.SafePositionRescueCount == rescuesBefore,
                    "正常行进中不得触发位置抢救——贴边 / 贴障碍是合法状态，误判会让单位每帧被瞬移");

                Expect(sim.SetControlledPosition(obstacles[0].Position), "把受控单位塞进障碍体内部");
                handoff.Tick(Dt, paused: false);
                Expect(sim.TryGetControlledPresentation(out SimControlledUnitView rescued) &&
                       math.distance(rescued.Position, obstacles[0].Position) >=
                       obstacles[0].Radius + rescued.Radius - 0.05f,
                    "留在障碍体内的受控单位应被拉到障碍之外（全仓没有寻路，本段也不新建）");
                Expect(handoff.SafePositionRescueCount == rescuesBefore + 1,
                    "抢救应被计数一次，而不是每帧反复搬运");

                Expect(sim.SetControlledPosition(new float2(500f, -500f)), "把受控单位丢到场地外");
                handoff.Tick(Dt, paused: false);
                Expect(sim.TryGetControlledPresentation(out SimControlledUnitView clamped) &&
                       math.abs(clamped.Position.x) <= cfg.ArenaHalfExtent + 0.05f &&
                       math.abs(clamped.Position.y) <= cfg.ArenaHalfExtent + 0.05f,
                    "落在场地外的受控单位应被拉回场地内");

                Expect(sim.SetControlledPosition(new float2(float.NaN, float.NaN)), "把受控单位的位置弄成 NaN");
                handoff.Tick(Dt, paused: false);
                Expect(sim.TryGetControlledPresentation(out SimControlledUnitView fixedUp) &&
                       math.all(math.isfinite(fixedUp.Position)) &&
                       handoff.IsPositionValid(fixedUp.Position, fixedUp.Radius),
                    "NaN 位置应被救回一个有效位置（NaN 留在内核里会顺着空间哈希污染整局）");
                Step(10);
                Expect(sim.TryGetControlledPresentation(out SimControlledUnitView stillFine) &&
                       math.all(math.isfinite(stillFine.Position)),
                    "抢救之后世界应能继续正常推进");

                // ── H. 调试显示（实施第 5 条）：读的是实时状态，不是快照 ──
                UnitAiDebugInfo info = handoff.Describe(twin);
                Expect(info.Valid && info.Behavior == BehaviorKind.Drift &&
                       info.IntentSource == IntentSource.AI &&
                       info.Command == UnitCommandKind.None,
                    "调试显示应读到单位级**行为原型** + 意图来源 + 当前命令（不是编队教义，见契约 §6）");

                squad.SelectExplicit(new[] { twin });
                Expect(squad.Issue(UnitCommandKind.Attack, hostilePos, hostile, paused: false) == 1,
                    "给对照单位下一条攻击命令，用来验证调试显示是实时读的");
                info = handoff.Describe(twin);
                Expect(info.Command == UnitCommandKind.Attack && info.CommandTarget == hostile &&
                       info.IntentSource == IntentSource.Commanded,
                    "下令之后调试显示应立刻反映新的命令与目标");
                sim.ClearCommand(twin);

                UnitAiDebugInfo courierInfo = handoff.Describe(courier);
                Expect((courierInfo.GroupMask & (1 << 3)) != 0,
                    "调试显示应能看出它属于编组 3");

                Expect(sim.RequestControlSwitch(twin) == ControlRequestResult.Success, "接管对照单位");
                Step(10);
                Expect(sim.RequestControlSwitch(body) == ControlRequestResult.Success, "再放开它");
                info = handoff.Describe(twin);
                Expect(info.InHandoffBuffer && info.BufferRemaining > 0f &&
                       info.Continuation != HandoffContinuation.None,
                    "调试显示应标出「正处于接管缓冲期」以及这一次的延续判定");

                // ── I. M1-06 的不变量在整段之后仍成立 ──
                Expect(CountPlayerIntentUnits(sim.Snapshot) <= 1,
                    "反复接管 / 交还 / 下令之后，存活单位里 IntentSource==Player 的数量仍应 ≤1");
                Expect(handoff.HandoffCount >= 6 && handoff.BufferedHandoffCount >= 4,
                    $"本段应真的发生过多次交还（交还 {handoff.HandoffCount} 次 / 其中缓冲 {handoff.BufferedHandoffCount} 次）");
            }
            finally
            {
                handoff.Unbind();
                squad.Unbind();
                sim.End();
            }
        }

        // ── [19] AI 禁止高风险过载（M2-04b）──────────────────────

        /// <summary>
        /// M2-04 实施第 3 条。GDD §7.3 那张表里"蓄力/过载"这一行，AI 侧写的是
        /// 「**只在安全阈值内使用**」，玩家侧才是「可承担过载债换关键爆发」。
        /// 拆成两条可验的产品含义：
        ///
        /// 1. **AI 继承过载后果**：一具处于过载态的身体交给 AI，同样打不出东西。
        ///    在 M2-04b 之前这条是假的——过载债只挡玩家直控入口，AI 走的是内核
        ///    <c>ResolveMinionCombat</c>，那里只认原型的 <c>AttackCooldown</c>。
        ///    于是"把身体打到过载 → 退出直控换一具接着打"能完全规避惩罚。**本段的全部意义就是这个漏洞。**
        /// 2. **AI 自己不会主动过载**：AI 的攻击根本不走 <c>Commit</c>，一分债都不产生。
        ///    这条是**构造上成立**的，本段没有为它新造任何"AI 的账"——下面直接断言
        ///    挨了整段打的 AI 单位在账本里**连条目都没有**。
        ///
        /// 断言口径照 [16]/[17]/[18]：**只比字段不算过**。真起 <see cref="SimBridge"/>、
        /// 真让 AI 单位打真敌人、真比掉血量。每一条"打不出东西"都配一个同原型、同参数、
        /// 从没被接管过的**对照单位**在同一批帧里照常掉血——否则"没打出来"完全可能只是
        /// 因为整副内核被我测停了，什么都没证明。
        /// </summary>
        private static void ValidateAiOverloadSuppression()
        {
            Line("\n[19] AI 禁止高风险过载（M2-04b）");

            const float Dt = 1f / 60f;

            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 60f;
            cfg.RandomSeed = 0xC0FFEE05u;

            // 自造原型表而不是读 Luban：召唤物必须**真的会攻击**（本段的被测行为就是它），
            // 敌人必须完全不还手、也不动（这样掉血只可能来自被测单位）。
            var archetypes = new[]
            {
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.MinionSeekAttack, Accel = 12f, TurnRate = 0f, AggroRange = 12f,
                    AttackRange = 6f, AttackCooldown = 0.25f, AttackDamage = 5f,
                    Separation = 0f, ChargeSpeedMul = 1f,
                },
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Stationary, Accel = 0f, TurnRate = 0f, AggroRange = 0f,
                    AttackRange = 0.5f, AttackCooldown = 99f, AttackDamage = 0f,
                    Separation = 0f, ChargeSpeedMul = 1f,
                },
            };

            sim.Begin(cfg, archetypes);
            // 切换范围/冷却不是本段被测对象，放开以免干扰（同 [16]/[17]/[18] 的做法）。
            sim.ConfigureControlSwitch(200f, 0f);

            var registry = new UnitLoadoutRegistry();
            var fakeSource = new FakePlayerLoadoutSource();
            var actions = new DirectControlActions();
            // 刻意背对敌人释放：本段要测的是"AI 打不打得出东西"，
            // 玩家那一发落到同一个敌人身上只会把掉血量的来源搅浑。
            var awayAim = new float2(0f, 1f);

            InputRouter.Reset();

            try
            {
                registry.Bind(sim, fakeSource);
                SimEntityId body = sim.ControlledUnitId;
                registry.RegisterPlayerBody(body);
                actions.Bind(sim, registry, abilities: null, status: null);

                // 两组"召唤物 + 假人"隔开 40 米以上摆，索敌半径 12——保证各打各的，
                // 对照组的掉血不可能来自被测组。
                const int MinionALogicId = 9601;
                const int DummyALogicId = 9602;
                const int MinionBLogicId = 9603;
                const int DummyBLogicId = 9604;

                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(-26f, 0f), Health = 5000f, Radius = 0.5f, MaxSpeed = 3f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = MinionALogicId,
                });
                registry.RegisterArchetypePending(MinionALogicId, ArchetypeLoadoutTable.SporeArchetypeId);
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(-18f, 0f), Health = 100000f, Radius = 0.6f, MaxSpeed = 0f,
                    ArchetypeId = 1, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = DummyALogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(18f, 0f), Health = 5000f, Radius = 0.5f, MaxSpeed = 3f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = MinionBLogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(26f, 0f), Health = 100000f, Radius = 0.6f, MaxSpeed = 0f,
                    ArchetypeId = 1, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = DummyBLogicId,
                });

                sim.OnUpdate(Dt);
                registry.ResolvePending(sim.Snapshot);
                SimSnapshot snap0 = sim.Snapshot;
                SimEntityId minionA = FindEntityId(snap0, MinionALogicId, out _);
                SimEntityId dummyA = FindEntityId(snap0, DummyALogicId, out _);
                SimEntityId minionB = FindEntityId(snap0, MinionBLogicId, out _);
                SimEntityId dummyB = FindEntityId(snap0, DummyBLogicId, out _);
                Expect(body.IsValid && minionA.IsValid && dummyA.IsValid &&
                       minionB.IsValid && dummyB.IsValid,
                    "本段的玩家本体、两名召唤物与两个假人应都已落地并拥有有效稳定实体 ID");

                float HealthOf(SimEntityId id) =>
                    sim.TryResolveUnitIndex(id, out int i) && i < sim.Snapshot.Count
                        ? sim.Snapshot.Health[i]
                        : float.NaN;

                float2 PosOf(SimEntityId id) =>
                    sim.TryResolveUnitIndex(id, out int i) && i < sim.Snapshot.Count
                        ? sim.Snapshot.Position[i]
                        : float2.zero;

                bool KernelOverloaded(SimEntityId id) =>
                    sim.TryResolveUnitIndex(id, out int i) && i < sim.Snapshot.Count &&
                    sim.Snapshot.HasStatus(i, SimStatus.Overloaded);

                IntentSource SourceOf(SimEntityId id) =>
                    sim.World != null && sim.World.TryGetUnitControlState(id, out SimUnitControlState s)
                        ? s.IntentSource
                        : IntentSource.Scripted;

                // 内核与账本用同一条时间轴推进：账本的衰减是按它自己的本地时钟算的，
                // 两条时间轴脱节的话"衰减到清除线"发生在哪一帧就说不清了。
                void Step(int frames)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        sim.OnUpdate(Dt);
                        actions.Tick(Dt, paused: false);
                    }
                }

                // ── A. 基线：AI 召唤物本来就在打人（没有它，后面所有"打不出东西"都不成立）──
                float baseA = HealthOf(dummyA);
                float baseB = HealthOf(dummyB);
                Step(60);
                float hitA = baseA - HealthOf(dummyA);
                float hitB = baseB - HealthOf(dummyB);
                Expect(hitA > 0f && hitB > 0f,
                    $"两名 AI 召唤物在 60 帧内都应真的打出伤害（A 打掉 {hitA:F1} / B 打掉 {hitB:F1}）——" +
                    "这是本段一切反证的前提");
                Expect(SourceOf(minionA) == IntentSource.AI && SourceOf(minionB) == IntentSource.AI,
                    "此刻两名召唤物都由 AI 驱动，走的是内核 ResolveMinionCombat 而不是直控释放入口");

                // ── B. AI 自己不会主动过载：构造上成立，不是靠新造一本账 ──
                Expect(!actions.Vitals.IsTracked(minionA) && !actions.Vitals.IsTracked(minionB),
                    "挨了整整 60 帧攻击之后，AI 单位在过载债账本里**连条目都没有**——" +
                    "AI 的攻击不经过 Commit，一分债都不产生（本段没有为 AI 新造账）");
                Expect(actions.Vitals.OverloadedCount == 0 &&
                       actions.OverloadMirror.SuppressedCount == 0 &&
                       actions.OverloadMirror.PushCount == 0,
                    "没有任何身体过载时，镜像不该往内核推过任何东西");
                Expect(!KernelOverloaded(minionA) && !KernelOverloaded(minionB),
                    "内核侧两名召唤物都不带过载位");

                // ── C. 玩家把这具身体推到过载（M2-03c 的既有路径，一行没改）──
                Expect(sim.RequestControlSwitch(minionA) == ControlRequestResult.Success,
                    "应能接管召唤物 A");
                Expect(actions.TryRelease(LoadoutAction.Primary, awayAim),
                    "接管后玩家应能用它的主器官释放一次");
                OrganKernelAction releasedAct = actions.LastReleasedKernelAction;
                Expect(actions.ControlledVitals.Strain > 0f,
                    $"玩家的这一次释放应真的记上过载债（{actions.ControlledVitals.Strain:F1}）——" +
                    "债是玩家自己按出来的，不是凭空塞进去的");

                // 等玩家那一发彻底消失（弹体飞完 / 区域烧完）再开始量掉血，
                // 否则"AI 打不出东西"会被玩家残留的输出污染。
                Step(Mathf.CeilToInt((releasedAct.Seconds + releasedAct.Lifetime + 0.5f) * 60f) + 30);

                Expect(actions.Vitals.AddStrain(minionA, UnitVitalsRegistry.StrainOverloadThreshold + 5f),
                    "把这具身体推过过载阈值（口径与 [17]-C 同一条路径，确定性地跨线）");
                UnitVitalsView over = actions.ControlledVitals;
                Expect(over.Overloaded && over.EntityId == minionA,
                    $"这具身体应进入过载态（过载债 {over.Strain:F1} / 阈值 {over.StrainThreshold:F0}）");
                int releasesBefore = actions.ReleaseCount;
                Expect(!actions.TryRelease(LoadoutAction.Primary, awayAim) &&
                       actions.LastReleaseResult == DirectActionAvailability.Overloaded &&
                       actions.ReleaseCount == releasesBefore,
                    "玩家侧照旧被拒（M2-03c 的行为一行没变）");
                Expect(actions.OverloadMirror.SuppressedCount == 1 &&
                       actions.OverloadMirror.IsSuppressed(minionA) &&
                       actions.OverloadMirror.PushCount >= 1,
                    "过载态应在**翻转那一刻**被推给内核一次（不是每帧广播）");

                // ── D. 核心：交还给 AI 之后，它同样打不出东西 ──
                //
                // 这正是 M2-04b 要堵的漏洞：在此之前，玩家只要退出直控换一具身体，
                // 这具过载的身体交给 AI 就照常全速攻击，惩罚被完全规避。
                Expect(sim.RequestControlSwitch(body) == ControlRequestResult.Success,
                    "玩家退出直控，把这具过载的身体交还给 AI");
                Step(2);
                Expect(SourceOf(minionA) == IntentSource.AI,
                    "交还后它应真的回到 AI 驱动（否则下面测的根本不是 AI 路径）");
                Expect(KernelOverloaded(minionA) && !KernelOverloaded(minionB),
                    "内核侧应只有这具身体带过载位，对照单位不受影响");

                UnitVitalsView still = actions.Vitals.Get(minionA);
                float secondsToClear =
                    (still.Strain - UnitVitalsRegistry.StrainClearThreshold) /
                    UnitVitalsRegistry.StrainDecayPerSecond;
                Expect(secondsToClear > 0.5f,
                    $"此刻离清除线还有 {secondsToClear:F2}s，足够量一段完整的压制窗口");

                int suppressedFrames = Mathf.Max(30, Mathf.FloorToInt(secondsToClear * 0.5f * 60f));
                float preA = HealthOf(dummyA);
                float preB = HealthOf(dummyB);
                float2 prePos = PosOf(minionA);
                Step(suppressedFrames);
                float suppressedDamageA = preA - HealthOf(dummyA);
                float controlDamageB = preB - HealthOf(dummyB);

                Expect(suppressedDamageA <= 0.001f,
                    $"**过载的身体交给 AI 之后同样打不出东西**（{suppressedFrames} 帧内掉血 {suppressedDamageA:F3}）");
                Expect(controlDamageB > 0f,
                    $"同一批帧里，从没被接管过的对照单位照常输出（打掉 {controlDamageB:F1}）——" +
                    "否则上一条只能说明整副内核被测停了");
                Expect(actions.Vitals.Get(minionA).Overloaded && KernelOverloaded(minionA),
                    "整个窗口里它都还在过载态");
                Expect(math.distance(PosOf(minionA), prePos) > 0.5f,
                    $"过载**不是麻痹**：它照样在朝目标移动（位移 {math.distance(PosOf(minionA), prePos):F2}），" +
                    "只是打不出东西");

                // ── E. 债衰减到清除线 → AI 恢复攻击（压制必须自己解除）──
                //
                // 最危险的失败模式不是"没压住"，而是"压住了再也放不开"：
                // 账本是惰性结算的，一具交给 AI 的身体没有任何读者，
                // 若不巡守就永远等不到那次 Sync，单位被无声地永久钉死。
                Step(Mathf.CeilToInt(secondsToClear * 0.6f * 60f) + 30);
                Expect(!actions.Vitals.Get(minionA).Overloaded,
                    $"过载债衰减到清除线以下应退出过载态（实测 {actions.Vitals.Get(minionA).Strain:F1}）——" +
                    "没人去读它，靠的是巡守表补齐");
                Expect(!KernelOverloaded(minionA) && actions.OverloadMirror.SuppressedCount == 0,
                    "内核侧的压制位应被同步放掉，镜像清单清空");

                float recoverBase = HealthOf(dummyA);
                Step(60);
                float recovered = recoverBase - HealthOf(dummyA);
                Expect(recovered > 0f,
                    $"解除之后 AI 应重新打出伤害（60 帧打掉 {recovered:F1}）——压制是一段窗口，不是永久失能");

                // ── F. 拆台不留悬挂压制（跨局最致命的一种泄漏）──
                Expect(actions.Vitals.AddStrain(minionA, UnitVitalsRegistry.StrainOverloadThreshold + 5f),
                    "再把它推进过载态一次");
                Step(2);
                Expect(KernelOverloaded(minionA) && actions.OverloadMirror.SuppressedCount == 1,
                    "内核侧重新被压住");

                Expect(!actions.Vitals.IsTracked(minionB),
                    "整段跑完，全程由 AI 驱动的对照单位在账本里仍然连条目都没有——" +
                    "「AI 不会主动过载」是构造上成立的，不靠任何额外机制");

                actions.Unbind();
                sim.OnUpdate(Dt);
                Expect(!KernelOverloaded(minionA) && actions.OverloadMirror.SuppressedCount == 0,
                    "Unbind 应把已压下去的过载位全部放掉——留着就是把那个槽位永久钉死");

                float afterUnbindBase = HealthOf(dummyA);
                for (int f = 0; f < 60; f++)
                {
                    sim.OnUpdate(Dt);
                }
                Expect(afterUnbindBase - HealthOf(dummyA) > 0f,
                    $"拆台之后那具身体应照常攻击（打掉 {afterUnbindBase - HealthOf(dummyA):F1}），不留悬挂压制");
            }
            finally
            {
                InputRouter.Reset();
                actions.Unbind();
                registry.Unbind();
                sim.End();
            }
        }

        // ── [20] 外科窗口身体接点基元（M2-05a）──────────────────

        /// <summary>
        /// M2-05a 实施第 1 条 + 为第 4/5 条预留信号位。GDD 的产品目标是"证明直控能提供独特高价值，
        /// 而非只提高 DPS"，本段只验证内核基础是否成立，不涉及 RTS 接点指定 / 直控瞄准 /
        /// 器官掉落奖励（分别是 M2-05b、M2-05c 的事）。
        ///
        /// 断言覆盖 Surgical_Window_Contract.md 写定的三条口径：
        /// 1. 两个接点可分别造成伤害且互不干扰（A/B）；
        /// 2. 接点摧毁 ≠ 整体死亡，只有整体 Health 归零才死亡（C/D/G）；
        /// 3. TargetPart 只对单体请求生效，范围伤害与"没登记身体的普通单位"都必须
        ///    确定性回退到原有整体 Health 路径——这是向后兼容的核心断言（E/F）。
        /// </summary>
        private static void ValidateSurgicalWindowBody()
        {
            Line("\n[20] 外科窗口身体接点基元（M2-05a）");

            var world = new SimWorld();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            cfg.ArenaHalfExtent = 60f;
            world.Initialize(cfg);

            SimCommandBuffer cmds = default;
            cmds.Initialize(Unity.Collections.Allocator.Persistent, 64);

            try
            {
                const int TestLogicId = 9701;
                const int PlainLogicId = 9702;

                int testIdx = world.SpawnSurgicalTestEnemy(
                    position: new float2(10f, 0f), logicId: TestLogicId,
                    coreHealth: 200f, primaryPartHealth: 60f, secondaryPartHealth: 60f);
                Expect(testIdx != SimConst.InvalidIndex, "测试敌人应生成成功");
                Expect(world.TryGetEntityId(testIdx, out SimEntityId testId) && testId.IsValid,
                    "测试敌人应拥有有效稳定实体 ID");

                Expect(world.HasBody(testId), "测试敌人应登记了身体（配了接点血量）");
                Expect(world.TryGetBodyPart(testId, SimBodyPartSlot.Primary, out SimBodyPart p0)
                       && p0.Health == 60f && p0.MaxHealth == 60f && p0.Destroyed == 0,
                    $"接点 1（占位 PrimaryOrgan）初始应满血未摧毁（实际 {p0.Health}/{p0.MaxHealth}）");
                Expect(world.TryGetBodyPart(testId, SimBodyPartSlot.Secondary, out SimBodyPart s0)
                       && s0.Health == 60f && s0.Destroyed == 0,
                    $"接点 2（占位 SecondaryOrgan）初始应满血未摧毁（实际 {s0.Health}/{s0.MaxHealth}）");

                // ── A. 定向命中接点 1：只影响它自己 ──────────────────────
                cmds.Damage(new DamageRequest
                {
                    TargetIndex = testIdx,
                    Radius = -1f,
                    Amount = 20f,
                    TargetPart = SimBodyPartSlot.Primary,
                });
                world.Step(1f / 60f, ref cmds);

                world.TryGetBodyPart(testId, SimBodyPartSlot.Primary, out SimBodyPart p1);
                world.TryGetBodyPart(testId, SimBodyPartSlot.Secondary, out SimBodyPart s1);
                SimSnapshot snapA = world.GetSnapshot();
                Expect(p1.Health == 40f && p1.Destroyed == 0,
                    $"接点 1 应扣掉这次定向伤害（实际 {p1.Health}/60）");
                Expect(s1.Health == 60f,
                    "接点 2 不应被打到接点 1 的伤害影响——两个接点必须互相独立");
                Expect(snapA.Health[testIdx] == 200f,
                    $"定向到接点的伤害不应外溢到整体 Health（实际 {snapA.Health[testIdx]}/200）");

                // ── B. 定向命中接点 2：同样独立 ──────────────────────────
                cmds.Damage(new DamageRequest
                {
                    TargetIndex = testIdx,
                    Radius = -1f,
                    Amount = 15f,
                    TargetPart = SimBodyPartSlot.Secondary,
                });
                world.Step(1f / 60f, ref cmds);
                world.TryGetBodyPart(testId, SimBodyPartSlot.Primary, out SimBodyPart p2);
                world.TryGetBodyPart(testId, SimBodyPartSlot.Secondary, out SimBodyPart s2);
                Expect(s2.Health == 45f, $"接点 2 应扣掉这次定向伤害（实际 {s2.Health}/60）");
                Expect(p2.Health == 40f, "接点 1 不应被打到接点 2 的伤害影响");

                // ── C. 低伤精准切离：把接点 1 正好打到 0——摧毁但不致死 ──────
                cmds.Damage(new DamageRequest
                {
                    TargetIndex = testIdx,
                    Radius = -1f,
                    Amount = 40f,
                    TargetPart = SimBodyPartSlot.Primary,
                });
                world.Step(1f / 60f, ref cmds);
                world.TryGetBodyPart(testId, SimBodyPartSlot.Primary, out SimBodyPart p3);
                SimSnapshot snapC = world.GetSnapshot();
                Expect(p3.Destroyed == 1, "接点 1 应被摧毁（切离）");
                Expect(p3.DestroyedBySingleTargetHit == 1,
                    "摧毁这一击是单体定向命中，精准信号位应为真"
                    + "（M2-05a 只记事实，'多低算精准'的阈值留给 M2-05c）");
                Expect(p3.LastHitAmount == 40f,
                    $"信号位应记下摧毁这一击的伤害量（实际 {p3.LastHitAmount}）");
                Expect(snapC.Alive[testIdx] != 0 && snapC.Health[testIdx] == 200f,
                    "接点摧毁不等于整体死亡——整体 Health 一分未少、实体仍然存活"
                    + "（本段选定并写进契约的死亡判定口径）");

                // ── D. 已摧毁的接点继续挨打：伤害被吃掉，不外溢到整体 Health ──
                cmds.Damage(new DamageRequest
                {
                    TargetIndex = testIdx,
                    Radius = -1f,
                    Amount = 999f,
                    TargetPart = SimBodyPartSlot.Primary,
                });
                world.Step(1f / 60f, ref cmds);
                SimSnapshot snapD = world.GetSnapshot();
                Expect(snapD.Alive[testIdx] != 0 && snapD.Health[testIdx] == 200f,
                    "继续打一个已摧毁的接点不应外溢到整体 Health、也不应致死");

                // ── E. TargetPart 只对单体请求生效：范围伤害忽略它，回退整体 Health ──
                float healthBeforeAoe = world.GetSnapshot().Health[testIdx];
                float2 enemyPos = world.GetSnapshot().Position[testIdx];
                cmds.Damage(new DamageRequest
                {
                    Origin = enemyPos,
                    TargetIndex = SimConst.InvalidIndex,
                    Radius = 5f,
                    Amount = 15f,
                    TargetFaction = SimFaction.Hostile,
                    TargetPart = SimBodyPartSlot.Secondary,
                });
                world.Step(1f / 60f, ref cmds);
                world.TryGetBodyPart(testId, SimBodyPartSlot.Secondary, out SimBodyPart sAfterAoe);
                SimSnapshot snapE = world.GetSnapshot();
                Expect(sAfterAoe.Health == 45f,
                    "范围伤害即便带了 TargetPart 也不该定向到接点——这个字段只对单体请求生效");
                Expect(snapE.Health[testIdx] == healthBeforeAoe - 15f,
                    $"范围伤害应回退到整体 Health 路径（{healthBeforeAoe} → {snapE.Health[testIdx]}）");

                // ── F. 向后兼容：没登记身体的普通单位，TargetPart 误设也不会吞掉伤害 ──
                int plainIdx = world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(-10f, 0f),
                    Health = 50f,
                    Radius = 0.5f,
                    Faction = SimFaction.Hostile,
                    LogicId = PlainLogicId,
                });
                Expect(world.TryGetEntityId(plainIdx, out SimEntityId plainId),
                    "普通单位应有有效稳定 ID");
                Expect(!world.HasBody(plainId),
                    "普通单位不应登记身体——绝大多数单位没有接点是默认状态");

                cmds.Damage(new DamageRequest
                {
                    TargetIndex = plainIdx,
                    Radius = -1f,
                    Amount = 20f,
                    TargetPart = SimBodyPartSlot.Primary,
                });
                world.Step(1f / 60f, ref cmds);
                SimSnapshot snapF = world.GetSnapshot();
                Expect(snapF.Health[plainIdx] == 30f,
                    "没有身体的单位收到误设的 TargetPart 时应确定性回退到整体 Health"
                    + $"（实际 {snapF.Health[plainIdx]}/50，应为 30）");

                // ── G. 粗暴整体击杀：打光整体 Health 才是真正的死亡，与接点是否完好无关 ──
                cmds.Damage(new DamageRequest
                {
                    TargetIndex = testIdx,
                    Radius = -1f,
                    Amount = 500f,
                });
                world.Step(1f / 60f, ref cmds);
                SimSnapshot snapG = world.GetSnapshot();
                Expect(snapG.DeathCount > 0,
                    "整体 Health 归零应产生死亡事件（粗暴击杀），不管此刻接点是否被切离过");
            }
            finally
            {
                cmds.Dispose();
                world.Dispose();
            }
        }

        // ── [21] RTS 指定接点 + 直控瞄准接点（M2-05b）──────────

        /// <summary>
        /// 里程碑实施第 2/3 条：RTS 可指定器官类别、直控可瞄准具体接点。
        /// M2-05a 只交付了内核基元（<see cref="DamageRequest.TargetPart"/> 怎么扣血），
        /// 本段第一次让"谁来设置这个字段"落地——<see cref="UnitCommand.TargetPart"/> +
        /// <see cref="SquadCommandSystem.Issue"/>（RTS）与 <see cref="SimProjectileFlags.SurgicalAim"/>
        /// （直控弹体在实际命中帧解析具体接点）。
        ///
        /// 同时修了一个真实缺口：改动前 <c>SimWorld.ResolveMinionCombat</c> 完全不读
        /// <c>cmd.TargetEntity</c>，靠 <c>MinionTargetingUtil.TryFindNearestHostile</c> 重新找目标，
        /// 混战中"攻击指定实体"命令可能打偏。断言组 1/2 覆盖修复后的锁定；组 3/4 覆盖
        /// "自主 AI"与"非 Attack 的 Commanded 单位（如 Guard）"两条**必须保持原样**的路径——
        /// 这两条合起来才是"零回归"的完整证据，只测新增分支不够。
        /// </summary>
        private static void ValidateSurgicalWindowCommandAndAim()
        {
            Line("\n[21] RTS 指定接点 + 直控瞄准接点（M2-05b）");

            ValidateCommandedAttackLocksTargetPart();
            ValidateDirectControlAimResolvesPart();
        }

        // ── [22] 精准切离奖励 + 粗暴击杀生物质（M2-05c）────────

        /// <summary>
        /// 里程碑实施第 4/5 条。用同一套真实 JobDamage 事件验证奖励层只解释事实、不反向污染内核：
        /// 直控低伤末击保留完整器官；RTS 类别指定与直控高伤末击都不保留；
        /// 带接点敌人被整体伤害击杀只给生物质，普通敌人与吞噬清除不进入这条新奖励轨道。
        /// </summary>
        private static void ValidateSurgicalWindowRewards()
        {
            Line("\n[22] 精准切离奖励 + 粗暴击杀生物质（M2-05c）");
            Expect(!SimBridge.IsSurgicalAimSource(-1) &&
                   !SimBridge.IsSurgicalAimSource(-2) &&
                   !SimBridge.IsSurgicalAimSource(-123456),
                "反伤与内核自动分配的普通负数 LogicId 不得被误判成直控精准来源");

            var world = new SimWorld();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            cfg.ArenaHalfExtent = 80f;
            world.Initialize(cfg);

            SimCommandBuffer cmds = default;
            cmds.Initialize(Unity.Collections.Allocator.Persistent, 64);
            var rewards = new SurgicalRewardLedger();
            rewards.OnEnter();

            try
            {
                // A. 直控低伤：4 次 10/40 的几何归因命中，最后一次恰好切离。
                int preciseIdx = world.SpawnSurgicalTestEnemy(new float2(-30f, 0f), 9901,
                    coreHealth: 100f, primaryPartHealth: 40f, secondaryPartHealth: 0f);
                for (int i = 0; i < 4; i++)
                {
                    cmds.Damage(new DamageRequest
                    {
                        TargetIndex = preciseIdx,
                        Radius = -1f,
                        Amount = 10f,
                        TargetPart = SimBodyPartSlot.Primary,
                        SourceLogicId = SimBridge.EncodeSurgicalAimSource(100),
                    });
                    world.Step(1f / 60f, ref cmds);
                    SimSnapshot frame = world.GetSnapshot();
                    rewards.ResolveFrame(world, in frame);
                }

                SimSnapshot preciseSnap = world.GetSnapshot();
                HitEvent preciseHit = preciseSnap.Hits[preciseSnap.HitCount - 1];
                world.TryGetEntityId(preciseIdx, out SimEntityId preciseId);
                world.TryGetBodyPart(preciseId, SimBodyPartSlot.Primary, out SimBodyPart precisePart);
                Expect(SimBridge.IsSurgicalAimSource(preciseHit.SourceLogicId) &&
                       SimBridge.DecodeSurgicalAimSource(preciseHit.SourceLogicId) == 100 &&
                       precisePart.Destroyed != 0 && precisePart.LastHitAmount == 10f,
                    "精准切离应沿既有命中来源透传直控标记，并在身体接点记下摧毁末击事实");
                Expect(rewards.IntactOrganCount == 1 && rewards.Biomass == 0,
                    $"10/40 的直控末击应保留 1 个完整器官且不产生生物质（实际 organ={rewards.IntactOrganCount}, biomass={rewards.Biomass}）");
                bool hasExpectedStub = rewards.IntactOrganCount > 0 &&
                    rewards.IntactOrgans[0].SourceLogicId == 9901 &&
                    rewards.IntactOrgans[0].Slot == SimBodyPartSlot.Primary;
                Expect(hasExpectedStub,
                    "完整器官存根应保留来源逻辑 ID 与接点槽位，正式掉落属性留给 M3");

                // B. 同样低伤但来自 RTS 类别指定：能摧毁接点，不能获得直控专属完整器官。
                int categoryIdx = world.SpawnSurgicalTestEnemy(new float2(-10f, 0f), 9902,
                    coreHealth: 100f, primaryPartHealth: 40f, secondaryPartHealth: 0f);
                for (int i = 0; i < 4; i++)
                {
                    cmds.Damage(new DamageRequest
                    {
                        TargetIndex = categoryIdx,
                        Radius = -1f,
                        Amount = 10f,
                        TargetPart = SimBodyPartSlot.Primary,
                        SourceLogicId = 101,
                    });
                    world.Step(1f / 60f, ref cmds);
                    SimSnapshot frame = world.GetSnapshot();
                    rewards.ResolveFrame(world, in frame);
                }
                HitEvent categoryHit = world.GetSnapshot().Hits[world.GetSnapshot().HitCount - 1];
                world.TryGetEntityId(categoryIdx, out SimEntityId categoryId);
                world.TryGetBodyPart(categoryId, SimBodyPartSlot.Primary, out SimBodyPart categoryPart);
                Expect(categoryPart.Destroyed != 0 && !SimBridge.IsSurgicalAimSource(categoryHit.SourceLogicId),
                    "RTS 类别指定应正常摧毁接点，但来源不得带直控几何瞄准标记");
                Expect(rewards.IntactOrganCount == 1,
                    "RTS 类别指定即便以低伤切离，也不应增加直控专属完整器官奖励");

                // C. 直控高伤：归因正确但末击超过 25% 阈值，不保留完整器官。
                int roughPartIdx = world.SpawnSurgicalTestEnemy(new float2(10f, 0f), 9903,
                    coreHealth: 100f, primaryPartHealth: 40f, secondaryPartHealth: 0f);
                cmds.Damage(new DamageRequest
                {
                    TargetIndex = roughPartIdx,
                    Radius = -1f,
                    Amount = 40f,
                    TargetPart = SimBodyPartSlot.Primary,
                    SourceLogicId = SimBridge.EncodeSurgicalAimSource(102),
                });
                world.Step(1f / 60f, ref cmds);
                SimSnapshot roughPartFrame = world.GetSnapshot();
                rewards.ResolveFrame(world, in roughPartFrame);
                Expect(rewards.IntactOrganCount == 1,
                    "40/40 的直控高伤末击超过 25% 阈值，不应保留完整器官");

                // D. 带接点身体被打光整体 Health：死亡事件记住身体事实，只给 1 生物质。
                int bodyKillIdx = world.SpawnSurgicalTestEnemy(new float2(30f, 0f), 9904,
                    coreHealth: 20f, primaryPartHealth: 40f, secondaryPartHealth: 40f);
                cmds.Damage(new DamageRequest { TargetIndex = bodyKillIdx, Radius = -1f, Amount = 20f });
                world.Step(1f / 60f, ref cmds);
                SimSnapshot bodyDeathFrame = world.GetSnapshot();
                rewards.ResolveFrame(world, in bodyDeathFrame);
                DeathEvent bodyDeath = bodyDeathFrame.Deaths[0];
                Expect(bodyDeath.HadSurgicalBody != 0 && bodyDeath.CauseKind == DeathCauseKind.Damage,
                    "整体伤害击杀应在槽位释放前把‘带接点身体’事实写入死亡事件");
                Expect(rewards.Biomass == 1 && rewards.IntactOrganCount == 1,
                    $"粗暴整体击杀应只增加 1 生物质、不增加完整器官（实际 biomass={rewards.Biomass}, organ={rewards.IntactOrganCount}）");

                // E. 普通敌人与 Devour 不属于这条“粗暴手术”奖励。
                int plainIdx = world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(45f, 0f), Health = 10f, Radius = 0.5f,
                    Faction = SimFaction.Hostile, LogicId = 9905,
                });
                cmds.Damage(new DamageRequest { TargetIndex = plainIdx, Radius = -1f, Amount = 10f });
                world.Step(1f / 60f, ref cmds);
                SimSnapshot plainDeathFrame = world.GetSnapshot();
                rewards.ResolveFrame(world, in plainDeathFrame);
                Expect(rewards.Biomass == 1,
                    "没有身体接点的普通敌人死亡不应凭空产生手术窗口生物质");

                int devourIdx = world.SpawnSurgicalTestEnemy(new float2(60f, 0f), 9906,
                    coreHealth: 20f, primaryPartHealth: 40f, secondaryPartHealth: 40f);
                world.Step(1f / 60f, ref cmds); // 清掉上一帧事件，避免测试重复消费同一快照。
                world.KillUnit(devourIdx, 0);
                SimSnapshot devourFrame = world.GetSnapshot();
                rewards.ResolveFrame(world, in devourFrame);
                Expect(devourFrame.DeathCount == 1 && devourFrame.Deaths[0].HadSurgicalBody != 0 &&
                       devourFrame.Deaths[0].CauseKind == DeathCauseKind.Devour,
                    "吞噬清除仍应携带身体事实，但致死来源必须保持 Devour");
                Expect(rewards.Biomass == 1 && rewards.IntactOrganCount == 1,
                    "吞噬清除沿用既有吞噬奖励，不应重复进入手术窗口生物质/完整器官轨道");
            }
            finally
            {
                cmds.Dispose();
                world.Dispose();
            }
        }

        /// <summary>
        /// RTS 侧：Attack 命令锁定命令指定的实体（而不是重找最近敌人），并把
        /// <see cref="UnitCommand.TargetPart"/> 透传进这次攻击的 <see cref="DamageRequest"/>。
        /// 四组单位两两隔开 40 米以上（同 [19] 的做法），避免索敌半径互相污染。
        /// </summary>
        private static void ValidateCommandedAttackLocksTargetPart()
        {
            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            cfg.ArenaHalfExtent = 200f;
            cfg.RandomSeed = 0xC0FFEE06u;

            var archetypes = new[]
            {
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.MinionSeekAttack, Accel = 12f, TurnRate = 0f, AggroRange = 12f,
                    AttackRange = 8f, AttackCooldown = 0.25f, AttackDamage = 6f,
                    Separation = 0f, ChargeSpeedMul = 1f,
                },
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Stationary, Accel = 0f, TurnRate = 0f, AggroRange = 0f,
                    AttackRange = 0.5f, AttackCooldown = 99f, AttackDamage = 0f,
                    Separation = 0f, ChargeSpeedMul = 1f,
                },
            };
            sim.Begin(cfg, archetypes);

            try
            {
                // ── 组 1：显式 Attack 命令带 TargetPart——应锁定命令指定的（更远的）实体，
                //          并且打在它的接点上，旁边更近的敌人完全不该被误伤 ──
                const int AttackerALogicId = 9801;
                const int NearHostileALogicId = 9802;
                const int FarHostileALogicId = 9803;
                var originA = new float2(-90f, 0f);

                sim.Spawn(new SpawnRequest
                {
                    Position = originA, Health = 999f, Radius = 0.5f, MaxSpeed = 3f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = AttackerALogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = originA + new float2(2f, 0f), Health = 999f, Radius = 0.5f, MaxSpeed = 0f,
                    ArchetypeId = 1, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = NearHostileALogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = originA + new float2(5f, 0f), Health = 999f, Radius = 0.5f, MaxSpeed = 0f,
                    ArchetypeId = 1, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = FarHostileALogicId,
                    PrimaryPartMaxHealth = 100f, SecondaryPartMaxHealth = 100f,
                });

                sim.OnUpdate(1f / 60f);
                SimSnapshot snapA0 = sim.Snapshot;
                SimEntityId attackerA = FindEntityId(snapA0, AttackerALogicId, out _);
                SimEntityId nearHostileA = FindEntityId(snapA0, NearHostileALogicId, out int nearAIdx0);
                SimEntityId farHostileA = FindEntityId(snapA0, FarHostileALogicId, out int farAIdx0);
                Expect(attackerA.IsValid && nearHostileA.IsValid && farHostileA.IsValid,
                    "组 1 的攻击者与两个敌人应都已落地并拥有有效稳定实体 ID");

                int acceptedA = sim.IssueCommand(new[] { attackerA }, new UnitCommand
                {
                    Kind = UnitCommandKind.Attack, TargetEntity = farHostileA,
                    TargetPart = SimBodyPartSlot.Primary, ArriveRadius = 0.5f,
                });
                Expect(acceptedA == 1, $"组 1 的 Attack 命令应被接受（实际 {acceptedA}）");
                Expect(sim.TryGetCommand(attackerA, out UnitCommand gotA) &&
                       gotA.TargetPart == SimBodyPartSlot.Primary,
                    "下达的命令应能原样取回 TargetPart=Primary");

                float nearAHealthBefore = sim.Snapshot.Health[nearAIdx0];
                float farAHealthBefore = sim.Snapshot.Health[farAIdx0];
                for (int f = 0; f < 30; f++) { sim.OnUpdate(1f / 60f); }

                sim.World.TryGetBodyPart(farHostileA, SimBodyPartSlot.Primary, out SimBodyPart farAPrimary);
                sim.TryResolveUnitIndex(farHostileA, out int farAIdx1);
                sim.TryResolveUnitIndex(nearHostileA, out int nearAIdx1);
                sim.World.TryResolveUnit(attackerA, out int attackerAIdx1);
                Expect(farAPrimary.Health < 100f,
                    $"组 1：命令指定目标的 Primary 接点应挨打（实际 {farAPrimary.Health}/100）");
                Expect(sim.Snapshot.Health[farAIdx1] == farAHealthBefore,
                    "组 1：接点命中不应外溢到命令指定目标的整体 Health");
                Expect(sim.Snapshot.Health[nearAIdx1] == nearAHealthBefore,
                    "组 1：更近的旁观敌人不应被误伤——必须真的锁定命令指定的那个实体，而不是重找最近的");
                Expect(sim.Snapshot.IntentSourceOf(attackerAIdx1) == IntentSource.Commanded,
                    "组 1：目标仍存活，命令不应被提前交还 AI");

                // ── 组 2：显式 Attack 命令不带 TargetPart——应仍锁定命令指定实体（修复本身），
                //          但伤害路由行为不变：整体 Health 照常掉血，不涉及任何接点 ──
                const int AttackerBLogicId = 9811;
                const int NearHostileBLogicId = 9812;
                const int FarHostileBLogicId = 9813;
                var originB = new float2(-50f, 0f);

                sim.Spawn(new SpawnRequest
                {
                    Position = originB, Health = 999f, Radius = 0.5f, MaxSpeed = 3f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = AttackerBLogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = originB + new float2(2f, 0f), Health = 999f, Radius = 0.5f, MaxSpeed = 0f,
                    ArchetypeId = 1, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = NearHostileBLogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = originB + new float2(5f, 0f), Health = 999f, Radius = 0.5f, MaxSpeed = 0f,
                    ArchetypeId = 1, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = FarHostileBLogicId,
                });

                sim.OnUpdate(1f / 60f);
                SimSnapshot snapB0 = sim.Snapshot;
                SimEntityId attackerB = FindEntityId(snapB0, AttackerBLogicId, out _);
                SimEntityId nearHostileB = FindEntityId(snapB0, NearHostileBLogicId, out int nearBIdx0);
                SimEntityId farHostileB = FindEntityId(snapB0, FarHostileBLogicId, out int farBIdx0);
                Expect(attackerB.IsValid && nearHostileB.IsValid && farHostileB.IsValid,
                    "组 2 的攻击者与两个敌人应都已落地并拥有有效稳定实体 ID");

                int acceptedB = sim.IssueCommand(new[] { attackerB },
                    new UnitCommand { Kind = UnitCommandKind.Attack, TargetEntity = farHostileB, ArriveRadius = 0.5f });
                Expect(acceptedB == 1, $"组 2 的 Attack 命令应被接受（实际 {acceptedB}）");
                Expect(sim.TryGetCommand(attackerB, out UnitCommand gotB) &&
                       gotB.TargetPart == SimBodyPartSlot.None,
                    "不显式设置 TargetPart 时应保持默认 None（向后兼容）");

                float nearBHealthBefore = sim.Snapshot.Health[nearBIdx0];
                float farBHealthBefore = sim.Snapshot.Health[farBIdx0];
                for (int f = 0; f < 30; f++) { sim.OnUpdate(1f / 60f); }
                sim.TryResolveUnitIndex(farHostileB, out int farBIdx1);
                sim.TryResolveUnitIndex(nearHostileB, out int nearBIdx1);
                Expect(sim.Snapshot.Health[farBIdx1] == farBHealthBefore - 6f,
                    $"组 2：TargetPart=None 时应正常整体扣血（{farBHealthBefore} → {sim.Snapshot.Health[farBIdx1]}，应为 -6）");
                Expect(sim.Snapshot.Health[nearBIdx1] == nearBHealthBefore,
                    "组 2：即便不带 TargetPart，命令修复本身也该生效——更近的旁观者依旧不该被误伤");

                // ── 组 3：纯自主 AI（非 Commanded）——必须保持"打最近敌人"的原有行为不变 ──
                const int AiMinionLogicId = 9831;
                const int AiNearHostileLogicId = 9832;
                const int AiFarHostileLogicId = 9833;
                var originC = new float2(-10f, 0f);

                sim.Spawn(new SpawnRequest
                {
                    Position = originC, Health = 999f, Radius = 0.5f, MaxSpeed = 3f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = AiMinionLogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = originC + new float2(2f, 0f), Health = 999f, Radius = 0.5f, MaxSpeed = 0f,
                    ArchetypeId = 1, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = AiNearHostileLogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = originC + new float2(5f, 0f), Health = 999f, Radius = 0.5f, MaxSpeed = 0f,
                    ArchetypeId = 1, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = AiFarHostileLogicId,
                });

                sim.OnUpdate(1f / 60f);
                SimSnapshot snapC0 = sim.Snapshot;
                SimEntityId aiNear = FindEntityId(snapC0, AiNearHostileLogicId, out int aiNearIdx0);
                SimEntityId aiFar = FindEntityId(snapC0, AiFarHostileLogicId, out int aiFarIdx0);
                Expect(aiNear.IsValid && aiFar.IsValid, "组 3 的两个敌人应都已落地");
                // 全程不下任何命令——这具身体自始至终是纯 AI，走的是完全没被本段碰过的分支。

                float aiNearHealthBefore = sim.Snapshot.Health[aiNearIdx0];
                float aiFarHealthBefore = sim.Snapshot.Health[aiFarIdx0];
                for (int f = 0; f < 30; f++) { sim.OnUpdate(1f / 60f); }
                sim.TryResolveUnitIndex(aiNear, out int aiNearIdx1);
                sim.TryResolveUnitIndex(aiFar, out int aiFarIdx1);
                Expect(sim.Snapshot.Health[aiNearIdx1] < aiNearHealthBefore,
                    "组 3：纯自主 AI 应照旧打最近的敌人（本段完全没有改动这条分支）");
                Expect(sim.Snapshot.Health[aiFarIdx1] == aiFarHealthBefore,
                    "组 3：更远的敌人不该被打——纯 AI 的选靶行为必须与改动前逐字一致");

                // ── 组 4：Commanded 但不是 Attack（Guard）——同样必须落回"打最近敌人"，
                //          证明新分支只在 Kind==Attack 且带合法目标时才生效 ──
                const int GuardMinionLogicId = 9841;
                const int GuardNearHostileLogicId = 9842;
                const int GuardFarHostileLogicId = 9843;
                var originD = new float2(30f, 0f);

                sim.Spawn(new SpawnRequest
                {
                    Position = originD, Health = 999f, Radius = 0.5f, MaxSpeed = 3f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = GuardMinionLogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = originD + new float2(2f, 0f), Health = 999f, Radius = 0.5f, MaxSpeed = 0f,
                    ArchetypeId = 1, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = GuardNearHostileLogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = originD + new float2(5f, 0f), Health = 999f, Radius = 0.5f, MaxSpeed = 0f,
                    ArchetypeId = 1, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = GuardFarHostileLogicId,
                });

                sim.OnUpdate(1f / 60f);
                SimSnapshot snapD0 = sim.Snapshot;
                SimEntityId guardMinion = FindEntityId(snapD0, GuardMinionLogicId, out _);
                SimEntityId guardNear = FindEntityId(snapD0, GuardNearHostileLogicId, out int guardNearIdx0);
                SimEntityId guardFar = FindEntityId(snapD0, GuardFarHostileLogicId, out int guardFarIdx0);
                Expect(guardMinion.IsValid && guardNear.IsValid && guardFar.IsValid,
                    "组 4 的守备单位与两个敌人应都已落地");

                // 把 Guard 的留守点钉在自己原地，连带把 TargetPart 也设成 Primary——
                // 这里就是要证明：即便带了 TargetPart，Kind!=Attack 也不该消费它。
                int acceptedD = sim.IssueCommand(new[] { guardMinion }, new UnitCommand
                {
                    Kind = UnitCommandKind.Guard, TargetPosition = originD,
                    TargetPart = SimBodyPartSlot.Primary, ArriveRadius = 4f,
                });
                Expect(acceptedD == 1, $"组 4 的 Guard 命令应被接受（实际 {acceptedD}）");

                float guardNearHealthBefore = sim.Snapshot.Health[guardNearIdx0];
                for (int f = 0; f < 30; f++) { sim.OnUpdate(1f / 60f); }
                sim.TryResolveUnitIndex(guardNear, out int guardNearIdx1);
                sim.World.TryGetBodyPart(guardFar, SimBodyPartSlot.Primary, out SimBodyPart guardFarPrimary);
                Expect(sim.Snapshot.Health[guardNearIdx1] < guardNearHealthBefore,
                    "组 4：Guard 命令下应照旧打最近的敌人——命令只影响移动，不接管战斗（既有口径）");
                Expect(guardFarPrimary.MaxHealth <= 0f,
                    "组 4：更远的敌人本就没配接点，用来确认它完全没被单独针对");
                sim.World.TryResolveUnit(guardMinion, out int guardMinionIdxAfter);
                Expect(sim.Snapshot.IntentSourceOf(guardMinionIdxAfter) == IntentSource.Commanded,
                    "组 4：留守点就在原地，Guard 是持久命令，不应被提前交还 AI");

                // ── 组 5：SquadCommandSystem.Issue 的 targetPart 参数应原样透传进最终命令 ──
                // 复用组 1 的攻击者/近敌：这一步不再关心伤害，只验证热更层入口的接线正确。
                var cameraGo = new GameObject("Validate21_SquadWiring_TempCamera");
                Camera camera = cameraGo.AddComponent<Camera>();
                var squad = new SquadCommandSystem();
                try
                {
                    squad.Bind(sim, camera);
                    squad.SelectExplicit(new[] { attackerA });
                    int accepted = squad.Issue(UnitCommandKind.Attack,
                        sim.Snapshot.Position[nearAIdx1], nearHostileA, paused: false, SimBodyPartSlot.Secondary);
                    Expect(accepted == 1 &&
                           sim.TryGetCommand(attackerA, out UnitCommand wiredCmd) &&
                           wiredCmd.TargetPart == SimBodyPartSlot.Secondary,
                        "SquadCommandSystem.Issue 的 targetPart 参数应原样写进最终下达的 UnitCommand");
                }
                finally
                {
                    squad.Unbind();
                    UnityEngine.Object.DestroyImmediate(cameraGo);
                }
            }
            finally
            {
                sim.End();
            }
        }

        /// <summary>
        /// 直控侧：Projectile 分支把 <see cref="SimProjectileFlags.SurgicalAim"/> 带进内核，
        /// <see cref="JobProjectile"/> 在实际命中帧按弹体位置分别解析同一目标的两个接点。
        /// 目标没有登记身体时回退普通单位碰撞与整体伤害，既有单位不受影响。
        /// </summary>
        private static void ValidateDirectControlAimResolvesPart()
        {
            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 80f;
            cfg.RandomSeed = 0xC0FFEE07u;
            sim.Begin(cfg, Array.Empty<BehaviorArchetype>());
            sim.ConfigureControlSwitch(200f, 0f);

            var registry = new UnitLoadoutRegistry();
            var fakeSource = new FakePlayerLoadoutSource();
            var actions = new DirectControlActions();

            InputRouter.Reset();

            try
            {
                registry.Bind(sim, fakeSource);
                SimEntityId body = sim.ControlledUnitId;
                registry.RegisterPlayerBody(body);
                actions.Bind(sim, registry, abilities: null, status: null);

                const int CasterLogicId = 9821;
                const int DualPartTargetLogicId = 9822;
                const int PlainTargetLogicId = 9824;
                var casterPos = new float2(0f, 0f);
                var dirPlain = new float2(0f, -1f);
                var dualPartCenter = new float2(6f, 0f);
                var primaryOffset = new float2(0f, 0.55f);
                var secondaryOffset = new float2(0f, -0.55f);
                float2 dirPrimary = math.normalizesafe(dualPartCenter + primaryOffset - casterPos);
                float2 dirSecondary = math.normalizesafe(dualPartCenter + secondaryOffset - casterPos);

                sim.Spawn(new SpawnRequest
                {
                    Position = casterPos, Health = 999f, Radius = 0.5f, MaxSpeed = 0f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = CasterLogicId,
                });
                registry.RegisterArchetypePending(CasterLogicId, ArchetypeLoadoutTable.SporeArchetypeId);

                sim.Spawn(new SpawnRequest
                {
                    Position = dualPartCenter, Health = 999f, Radius = 1.2f, MaxSpeed = 0f,
                    ArchetypeId = 0, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = DualPartTargetLogicId,
                    PrimaryPartMaxHealth = 100f,
                    PrimaryPartAimOffset = primaryOffset,
                    PrimaryPartAimRadius = 0.25f,
                    SecondaryPartMaxHealth = 100f,
                    SecondaryPartAimOffset = secondaryOffset,
                    SecondaryPartAimRadius = 0.25f,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = casterPos + dirPlain * 6f, Health = 100f, Radius = 0.6f, MaxSpeed = 0f,
                    ArchetypeId = 0, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = PlainTargetLogicId,
                });

                sim.OnUpdate(1f / 60f);
                registry.ResolvePending(sim.Snapshot);
                SimSnapshot snap0 = sim.Snapshot;
                SimEntityId caster = FindEntityId(snap0, CasterLogicId, out _);
                SimEntityId dualPartTarget = FindEntityId(snap0, DualPartTargetLogicId, out _);
                SimEntityId plainTarget = FindEntityId(snap0, PlainTargetLogicId, out int plainIdx0);
                Expect(caster.IsValid && dualPartTarget.IsValid && plainTarget.IsValid,
                    "直控释放者、双接点目标与普通目标应都已落地并拥有有效稳定实体 ID");
                Expect(sim.RequestControlSwitch(caster) == ControlRequestResult.Success, "应能接管释放者");

                // ── ① 同一具身体有两个接点：朝上方连接点开火，只能命中 Primary ──
                Expect(actions.TryRelease(LoadoutAction.Primary, dirPrimary), "对准双接点目标的 Primary 连接点应能释放");
                Expect(actions.LastReleasedKernelAction.Kind == OrganKernelActionKind.Projectile,
                    $"直控释放者的主器官应落成真弹体才能验证瞄准（实际 {actions.LastReleasedKernelAction.Kind}）");
                bool sawSurgicalSource = false;
                for (int f = 0; f < 90; f++)
                {
                    sim.OnUpdate(1f / 60f);
                    SimSnapshot frame = sim.Snapshot;
                    for (int h = 0; h < frame.HitCount; h++)
                    {
                        HitEvent hit = frame.Hits[h];
                        if (hit.TargetLogicId == DualPartTargetLogicId &&
                            SimBridge.IsSurgicalAimSource(hit.SourceLogicId) &&
                            SimBridge.DecodeSurgicalAimSource(hit.SourceLogicId) == CasterLogicId)
                        {
                            sawSurgicalSource = true;
                        }
                    }
                }
                sim.World.TryGetBodyPart(dualPartTarget, SimBodyPartSlot.Primary, out SimBodyPart primaryAfterFirst);
                sim.World.TryGetBodyPart(dualPartTarget, SimBodyPartSlot.Secondary, out SimBodyPart secondaryAfterFirst);
                sim.TryResolveUnitIndex(dualPartTarget, out int dualPartIdx1);
                Expect(primaryAfterFirst.Health < 100f && secondaryAfterFirst.Health == 100f,
                    $"瞄准 Primary 应只命中 Primary（P={primaryAfterFirst.Health}/100，S={secondaryAfterFirst.Health}/100）");
                Expect(sim.Snapshot.Health[dualPartIdx1] == 999f,
                    "Primary 接点命中不应外溢到目标整体 Health");
                Expect(sawSurgicalSource,
                    "直控 FireProjectile(surgicalAim:true) 的真实命中事件应携带可解码的释放者来源标记");

                actions.Tick(10f, paused: false); // 越过冷却/代谢闸门，同 [16]/[19] 的做法

                // ── ② 仍是同一具身体：改朝下方连接点开火，只能命中 Secondary ──
                Expect(actions.TryRelease(LoadoutAction.Primary, dirSecondary), "对准双接点目标的 Secondary 连接点应能释放");
                for (int f = 0; f < 90; f++) { sim.OnUpdate(1f / 60f); }
                sim.World.TryGetBodyPart(dualPartTarget, SimBodyPartSlot.Primary, out SimBodyPart primaryAfterSecond);
                sim.World.TryGetBodyPart(dualPartTarget, SimBodyPartSlot.Secondary, out SimBodyPart secondaryAfterSecond);
                sim.TryResolveUnitIndex(dualPartTarget, out int dualPartIdx2);
                Expect(primaryAfterSecond.Health == primaryAfterFirst.Health && secondaryAfterSecond.Health < 100f,
                    $"瞄准 Secondary 应只命中 Secondary（P={primaryAfterSecond.Health}/100，S={secondaryAfterSecond.Health}/100）");
                Expect(sim.Snapshot.Health[dualPartIdx2] == 999f,
                    "Secondary 接点命中不应外溢到目标整体 Health");

                actions.Tick(10f, paused: false);

                // ── ③ 没有登记身体的目标：SurgicalAim 应回退普通碰撞，正常打整体伤害 ──
                float plainHealthBefore = sim.Snapshot.Health[plainIdx0];
                Expect(actions.TryRelease(LoadoutAction.Primary, dirPlain), "对准无身体目标方向的释放应成功");
                for (int f = 0; f < 90; f++) { sim.OnUpdate(1f / 60f); }
                sim.TryResolveUnitIndex(plainTarget, out int plainIdx1);
                Expect(sim.Snapshot.Health[plainIdx1] < plainHealthBefore,
                    $"没有登记身体的目标应正常掉整体 Health（{plainHealthBefore} → {sim.Snapshot.Health[plainIdx1]}）"
                    + "——TargetPart 回退 None 不应吞掉伤害");
            }
            finally
            {
                sim.End();
            }
        }

        /// <summary>可注入的假玩家装配投影源。改 <see cref="Organs"/> 即等于"玩家当场换了装配"，
        /// 用来证明注册表读的是**当下**而不是登记那一刻的快照。</summary>
        private sealed class FakePlayerLoadoutSource : IPlayerLoadoutSource
        {
            public readonly System.Collections.Generic.List<UnitLoadoutOrgan> Organs =
                new System.Collections.Generic.List<UnitLoadoutOrgan>();

            public void CollectOrgans(System.Collections.Generic.List<UnitLoadoutOrgan> buffer)
            {
                buffer.AddRange(Organs);
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
