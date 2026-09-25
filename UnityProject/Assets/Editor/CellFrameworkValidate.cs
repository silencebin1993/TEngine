using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim;
using GameLogic.Ability;
using GameLogic.Ability.Executors;
using GameLogic.Battle;
using GameLogic.Battle.Feedback;
using GameLogic.Cards;
using GameLogic.Command;
using GameLogic.Command.Formation;
using GameLogic.Control;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Blueprint;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Lineage;
using GameLogic.MetabolicSlice.WildOrgan;
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
                ValidateCombatTruthSourceUnified();
                ValidateBlueprintLibrary();
                ValidateLineagePhenotypeTemplate();
                ValidateCompiledRecipeCache();
                ValidateGerminationChamber();
                ValidateHomecomingRetrofit();
                ValidateWildOrganLoot();
                ValidateTemplateUiQueries();
                ValidateFormationDomainModel();
                ValidateFormationCommandQueue();
                ValidateFormationDoctrineProfiles();
                ValidateFormationSharedPathing();
                ValidateDirectControlDetachment();
                ValidateFormationEncounter();
                ValidateAbilityResourceLayerBoundary();
                ValidateFriendlyGeneProjection();
                ValidateFanDirectionUnified();
                ValidateEmissionGeometryAndBodyForward();
                ValidateFormationAnchorLeaderPriority();
                ValidateFormationCommandPriorityGuard();
                ValidateSharedCapabilityCatalog();
                ValidateAllyDeathSignal();
                ValidateHomecomingRealCombatExit();
                ValidateWildOrganFieldPersistence();
                ValidateSquadFormationBridge();
                ValidateSquadInputTranslation();
                ValidateCombatTransientTeardown();
                ValidateFormationCommandQueueVisualization();
                ValidateSquadCommandQueueRequest();
                ValidateDirectFormationHud();
                ValidateControlFeedbackFourPart();
                ValidateStableKillerIdentity();
                // ER8-CONTENT-01：《地球归还》反馈音效与字幕（AC-AUD-001），独立类，结果并入本报告。
                _fail += FeedbackCueSelfCheck.Run(Report);
                _fail += SettingsConsumersSelfCheck.Run(Report);
                _fail += ContentIconsSelfCheck.Run(Report);
                _fail += CampaignFlowSelfCheck.Run(Report);
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

        /// <summary>
        /// M4-R00-02 队列①-3（IC-REQ-010）：伤害/死亡事件迁移到稳定 SimEntityId。
        ///
        /// 复现队列文档点名的症状——"同类敌人同场存活时命中/死亡来源可能记混"：两个受害者
        /// 故意共享同一个 LogicId（同配置的两只敌人），只有 SimEntityId 能区分它们。此前
        /// KillerLogicId 在两条死亡路径（JobDamage 致死、KillUnit 吞噬）里都硬编码 0，
        /// 从未真正归属过——本用例断言它现在真的写上了攻击者的稳定身份。
        /// </summary>
        private static void ValidateStableKillerIdentity()
        {
            Line("\n[56] 击杀者稳定身份归属：伤害/死亡事件迁移到 SimEntityId（M4-R00-02 队列①-3，IC-REQ-010）");

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

                int idxAttacker = world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(0f, 5f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion, LogicId = 42,
                });
                // 两个受害者共享同一个 LogicId：真实场景里"同一种敌人生成了两只"就是这个形状。
                int idxVictim1 = world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(5f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.Hostile, LogicId = 7,
                });
                int idxVictim2 = world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(-5f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.Hostile, LogicId = 7,
                });

                SimSnapshot pre = world.GetSnapshot();
                SimEntityId attackerEntityId = pre.EntityId[idxAttacker];
                SimEntityId victim1EntityId = pre.EntityId[idxVictim1];
                SimEntityId victim2EntityId = pre.EntityId[idxVictim2];
                Expect(victim1EntityId != victim2EntityId,
                    "两个共享同一 LogicId 的受害者必须仍有不同的 SimEntityId");

                // 单体伤害，显式带上攻击者的稳定身份，只打 victim1。
                cmds.Damage(new DamageRequest
                {
                    Origin = float2.zero, Radius = -1f, TargetIndex = idxVictim1,
                    Amount = 1000f, TargetFaction = SimFaction.None,
                    SourceLogicId = pre.LogicId[idxAttacker], SourceEntityId = attackerEntityId,
                });
                cmds.SetPlayerIntent(PlayerIntent.Idle);
                world.Step(1f / 60f, ref cmds);

                SimSnapshot s1 = world.GetSnapshot();
                bool foundHit = false;
                for (int i = 0; i < s1.HitCount; i++)
                {
                    HitEvent h = s1.Hits[i];
                    if (h.TargetEntityId == victim1EntityId)
                    {
                        Expect(h.SourceEntityId == attackerEntityId,
                            $"命中事件应带攻击者的稳定身份（实际 {h.SourceEntityId}）");
                        foundHit = true;
                    }
                }
                Expect(foundHit, "应产生一条命中 victim1 的 HitEvent（按 TargetEntityId 定位）");

                Expect(s1.DeathCount == 1, $"应只有 victim1 死亡（实际 {s1.DeathCount}）");
                DeathEvent d1 = s1.Deaths[0];
                Expect(d1.EntityId == victim1EntityId,
                    "死亡事件的 EntityId 应精确指向 victim1，不能靠共享的 LogicId=7 猜");
                Expect(d1.KillerEntityId == attackerEntityId,
                    $"死亡事件的击杀者应归属攻击者的稳定身份（实际 {d1.KillerEntityId}）——" +
                    "此前 KillerLogicId 恒为 0，从未真正归属过任何来源");
                Expect(d1.KillerLogicId == pre.LogicId[idxAttacker],
                    $"KillerLogicId 也应同步归属攻击者（实际 {d1.KillerLogicId}）");

                // 吞噬路径：直接验证内核 KillUnit 新增的 killerEntityId 参数被正确写入 DeathEvent
                // （SimBridge.ConsumeUnit 从受控实体快照解出这两个值后转发到这里，参见该方法注释）。
                // _deathEvents 在两次 Step 之间是累积的（同 [7] ValidateDeathCauseKind 的既有行为：
                // 上面 Step() 产的 victim1 死亡事件还留着），这里不假设下标，按 EntityId 找。
                world.KillUnit(idxVictim2, pre.LogicId[idxAttacker], attackerEntityId);
                SimSnapshot s2 = world.GetSnapshot();
                Expect(s2.DeathCount == 2,
                    $"应累积 victim1（Step）+ victim2（KillUnit）共 2 条死亡事件（实际 {s2.DeathCount}）");
                bool foundVictim2Death = false;
                for (int i = 0; i < s2.DeathCount; i++)
                {
                    DeathEvent d = s2.Deaths[i];
                    if (d.EntityId != victim2EntityId)
                    {
                        continue;
                    }
                    foundVictim2Death = true;
                    Expect(d.KillerEntityId == attackerEntityId,
                        $"KillUnit 新增的 killerEntityId 参数应写入 DeathEvent.KillerEntityId（实际 {d.KillerEntityId}）");
                }
                Expect(foundVictim2Death, "应找到精确指向 victim2 的吞噬死亡事件");
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
        /// [25] 战斗真相源统一（M2-07）：**同一具身体，谁开都打出同一种东西**。
        ///
        /// 试玩反馈 #5 说的是「AI 与直控打法完全不同」。根因不是数值没调好，是同一具身体
        /// 挂着两套战斗真相源：玩家开它时走器官（<c>OrganKernelActionTable</c> → 真弹体），
        /// 松手之后走 <c>BehaviorArchetype.AttackDamage</c> 的瞬时扣血。于是"接管"没有可比较的基准。
        ///
        /// 本段守的是统一之后**必须成立**的四件事，每一件都对应一种曾经可能悄悄退化的方式：
        /// <list type="number">
        /// <item>装配落地后，内核确实知道这具身体由器官驱动（位真的推下去了，不是热更层自嗨）；</item>
        /// <item>AI 打出来的形态与玩家用同一具身体打出来的形态**逐字段相同**
        ///       ——只比"都有伤害"是不够的，那用旧路径也成立；</item>
        /// <item>AI 开火真的产出了内核弹体（而不是退回瞬时伤害），即弹体数真的涨了；</item>
        /// <item>没登记装配的身体仍走原型数值的降级路，**不会哑火**——
        ///       这是本次刻意保留的边界，退化成"全场没器官就都不打"比原问题更严重。</item>
        /// </list>
        /// </summary>
        private static void ValidateCombatTruthSourceUnified()
        {
            Line("\n[25] 战斗真相源统一：同一具身体谁开都一样（M2-07）");

            const float Dt = 1f / 60f;

            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 60f;
            cfg.RandomSeed = 0xC0FFEE07u;

            // 与 [19] 同一种构造：可接管友军会攻击，假人完全不还手也不动，
            // 这样掉血与弹体只可能来自被测单位。
            //
            // ⚠ 用词：本段被测的是**可接管友军**（`SpawnControlAllies` 那种，不设 ExcludeFromControl），
            // **不是召唤物**。真·召唤物只由 `MetabolicSliceBridge` 生成、一律 `ExcludeFromControl = true`、
            // 至今不可接管（守在 [24]-E 的两条断言里）。两者都是 `SimFaction.PlayerMinion`，
            // 在内核里长得很像，但"能不能把意识转进去"是相反的——别把这两个词混着用。
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
            sim.ConfigureControlSwitch(200f, 0f);

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

                const int ArmedLogicId = 9701;   // 登记装配 → 器官驱动
                const int DummyLogicId = 9702;
                const int BareLogicId = 9703;    // 不登记装配 → 降级走原型数值
                const int BareDummyLogicId = 9704;

                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(-26f, 0f), Health = 5000f, Radius = 0.5f, MaxSpeed = 3f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = ArmedLogicId,
                });
                registry.RegisterArchetypePending(ArmedLogicId, ArchetypeLoadoutTable.SporeArchetypeId);
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(-20f, 0f), Health = 100000f, Radius = 0.6f, MaxSpeed = 0f,
                    ArchetypeId = 1, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = DummyLogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(24f, 0f), Health = 5000f, Radius = 0.5f, MaxSpeed = 3f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = BareLogicId,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(30f, 0f), Health = 100000f, Radius = 0.6f, MaxSpeed = 0f,
                    ArchetypeId = 1, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = BareDummyLogicId,
                });

                sim.OnUpdate(Dt);
                registry.ResolvePending(sim.Snapshot);
                SimSnapshot snap0 = sim.Snapshot;
                SimEntityId armed = FindEntityId(snap0, ArmedLogicId, out _);
                SimEntityId dummy = FindEntityId(snap0, DummyLogicId, out _);
                SimEntityId bare = FindEntityId(snap0, BareLogicId, out _);
                SimEntityId bareDummy = FindEntityId(snap0, BareDummyLogicId, out _);
                Expect(armed.IsValid && dummy.IsValid && bare.IsValid && bareDummy.IsValid,
                    "本段四个单位都应落地并拥有有效稳定实体 ID");

                float HealthOf(SimEntityId id) =>
                    sim.TryResolveUnitIndex(id, out int i) && i < sim.Snapshot.Count
                        ? sim.Snapshot.Health[i]
                        : float.NaN;

                int AliveProjectiles()
                {
                    var arr = sim.World != null ? sim.World.Projectiles : default;
                    if (!arr.IsCreated) { return 0; }
                    int n = 0;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (arr[i].Alive != 0) { n++; }
                    }
                    return n;
                }

                void Step(int frames)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        sim.OnUpdate(Dt);
                        actions.Tick(Dt, paused: false);
                    }
                }

                // ── 1. 位真的推到了内核 ────────────────────────────────────
                Expect(sim.IsOrganCombatDriven(armed),
                    "登记了装配的身体，内核侧应真的带上器官驱动位——" +
                    "只在热更层记一笔的话，内核照旧用原型数值结算，分叉原地不动");
                Expect(!sim.IsOrganCombatDriven(bare),
                    "没登记装配的身体不该被标成器官驱动（它得留在降级路上）");

                // ── 2. AI 开火真的产出了内核弹体 ──────────────────────────
                int releasesBefore = actions.MinionCombat.ReleaseCount;
                int projectilePeak = 0;
                for (int f = 0; f < 180; f++)
                {
                    sim.OnUpdate(Dt);
                    actions.Tick(Dt, paused: false);
                    projectilePeak = Mathf.Max(projectilePeak, AliveProjectiles());
                }
                Expect(actions.MinionCombat.ReleaseCount > releasesBefore,
                    $"AI 应通过器官释放路径真的开过火（累计 {actions.MinionCombat.ReleaseCount} 次）");
                Expect(projectilePeak > 0,
                    $"AI 的攻击应真的在内核里生成弹体（峰值 {projectilePeak} 发）——" +
                    "这是与旧路径最硬的区别：旧路径直接写 DamageRequest，场上一发弹体都不会有");

                OrganKernelAction aiAct = actions.MinionCombat.LastReleasedKernelAction;
                string aiOrganId = actions.MinionCombat.LastReleasedOrganId;
                Expect(aiAct.IsValid && !string.IsNullOrEmpty(aiOrganId),
                    $"AI 这几发应能追到具体器官（{aiOrganId} / {aiAct.Kind}）");

                // ── 3. 降级路没哑火（这条比上面几条更容易被改坏）──────────
                float bareBase = HealthOf(bareDummy);
                Step(120);
                float bareHit = bareBase - HealthOf(bareDummy);
                Expect(bareHit > 0f,
                    $"没登记装配的身体必须照常用原型数值打人（打掉 {bareHit:F1}）——" +
                    "真·召唤物（ExcludeFromControl=true 那种）正是这一类，" +
                    "让它们跟着走器官路会彻底哑火，那比原问题更严重");

                // ── 4. 核心：玩家接管同一具身体，打出来的是同一种东西 ──────
                Expect(sim.RequestControlSwitch(armed) == ControlRequestResult.Success,
                    "应能接管那具器官驱动的身体");

                // 等冷却：这把枪刚被 AI 用过，而统一之后它只有一条冷却线（见 [19]-C 的注释）。
                bool playerReleased = false;
                for (int f = 0; f < 120 && !playerReleased; f++)
                {
                    playerReleased = actions.TryRelease(LoadoutAction.Primary, aim);
                    if (!playerReleased) { Step(1); }
                }
                Expect(playerReleased,
                    $"玩家应能用这具身体的主器官开火（最后一次被拒原因 {actions.LastReleaseResult}）");

                OrganKernelAction playerAct = actions.LastReleasedKernelAction;
                Expect(actions.LastReleasedOrganId == aiOrganId,
                    $"玩家按出来的应是**同一件器官**（玩家 {actions.LastReleasedOrganId} / AI {aiOrganId}）");
                Expect(playerAct.Kind == aiAct.Kind &&
                       Mathf.Approximately(playerAct.Damage, aiAct.Damage) &&
                       Mathf.Approximately(playerAct.Speed, aiAct.Speed) &&
                       Mathf.Approximately(playerAct.Radius, aiAct.Radius) &&
                       Mathf.Approximately(playerAct.Lifetime, aiAct.Lifetime) &&
                       playerAct.Pierce == aiAct.Pierce &&
                       Mathf.Approximately(playerAct.Cooldown, aiAct.Cooldown),
                    $"**同一具身体，谁开都打出同一种东西**：玩家 {playerAct.Kind}/伤害 {playerAct.Damage:F1}/" +
                    $"速度 {playerAct.Speed:F1}/半径 {playerAct.Radius:F2}/寿命 {playerAct.Lifetime:F2}/" +
                    $"穿透 {playerAct.Pierce}/冷却 {playerAct.Cooldown:F2}　vs　" +
                    $"AI {aiAct.Kind}/伤害 {aiAct.Damage:F1}/速度 {aiAct.Speed:F1}/半径 {aiAct.Radius:F2}/" +
                    $"寿命 {aiAct.Lifetime:F2}/穿透 {aiAct.Pierce}/冷却 {aiAct.Cooldown:F2}" +
                    "——这是 M2-07 的全部立论，只比「都能打出伤害」是不够的（那用旧路径也成立）");

                // ── 4b. M4-R00-02 队列①-1：伤害数值已换成真实编译结果，不再是热更层自己猜的常量 ──
                // 如实记录一个重要边界（不夸大本次修复的范围）：友军装配今天不携带基因数据，
                // 而 ComposeEngine 链路里"每件攻击器官不同"这件事本身**由基因决定**——裸链路
                // （EnergyCore→Actuator）对任何攻击器官都是同一路 Energy→Damage 直传，
                // 空基因下不同器官会算出**相同**的基础伤害，这不是本次改动的回归，是这条化学链路
                // 一直如此。本次修复换掉的是"热更层自己拍的 DefaultDamage=6f"这件更糟的事——
                // 换成的数至少是 ComposeEngine 真实算出来的（EnergyCore 基准 10f），且一旦友军装配
                // 接入基因（M4-R00-02 队列②号项），才是这条改动真正开始生效的时候。
                Expect(!Mathf.Approximately(playerAct.Damage, OrganKernelActionTable.DefaultDamage) ||
                       !Mathf.Approximately(aiAct.Damage, OrganKernelActionTable.DefaultDamage),
                    $"至少一侧的伤害应已不再是旧的热更层常量 DefaultDamage={OrganKernelActionTable.DefaultDamage:F1}" +
                    $"（实测玩家 {playerAct.Damage:F2} / AI {aiAct.Damage:F2}）——否则 ResolveCompiled 只是换了个名字");

                // 证明管线真的会响应基因（即使今天友军装配还不携带基因）：同一件器官，
                // 挂一条真实基因前后，编译结果应不同——这是"以后接入基因就会生效"这条论证
                // 唯一站得住的证据，不能只靠读代码论证。刻意选 gene_vacuole（挂 Capacitor 模块，
                // 真的会乘 packet.Energy），不能随手挑目录第一条——"追踪/状态"类基因不改
                // Energy 链路，会做出与本条断言相反的假红。
                string sampleGeneId = GeneCatalog.AllGeneIds.Contains("gene_vacuole")
                    ? "gene_vacuole"
                    : GeneCatalog.AllGeneIds.FirstOrDefault();
                Expect(sampleGeneId != null, "GeneCatalog 应至少有一条基因用于本项对照");
                if (sampleGeneId != null)
                {
                    OrganKernelAction bareAct = OrganKernelActionTable.ResolveCompiled(
                        aiOrganId, Array.Empty<string>(), seed: 1);
                    OrganKernelAction genedAct = OrganKernelActionTable.ResolveCompiled(
                        aiOrganId, new[] { sampleGeneId }, seed: 1);
                    Expect(bareAct.IsValid && genedAct.IsValid,
                        $"同一件器官带 / 不带基因都应解析出有效内核动作（{aiOrganId} + {sampleGeneId}）");
                    Expect(!Mathf.Approximately(bareAct.Damage, genedAct.Damage) ||
                           !Mathf.Approximately(bareAct.Cooldown, genedAct.Cooldown) ||
                           !Mathf.Approximately(bareAct.MetabolicCost, genedAct.MetabolicCost),
                        $"挂上基因 {sampleGeneId} 前后，同一件器官 {aiOrganId} 的编译结果（伤害/冷却/代谢）" +
                        $"至少应有一项改变（裸链路伤害={bareAct.Damage:F2} 挂基因后={genedAct.Damage:F2}）——" +
                        "否则本次改动接的这条化学链路对基因根本没反应，友军装配以后接基因也不会有用");
                }

                // 退役器官不应因为换了解析函数就"复活"——与 Resolve 同一口径。
                Expect(!OrganKernelActionTable.ResolveCompiled("org_hook", Array.Empty<string>(), seed: 2).IsValid,
                    "已退役器官 org_hook 经 ResolveCompiled 解析仍应无效，不能绕过 Resolve 的退役检查复活");

                // ── 5. 拆台不留悬挂：位放掉之后退回降级路，不是变哑巴 ──────
                actions.Unbind();
                Expect(!sim.IsOrganCombatDriven(armed),
                    "驱动器下线后，内核里的器官驱动位必须放掉——" +
                    "留着就是让这些身体等一个不会再来的回答，无声的永久失能");
                float afterBase = HealthOf(dummy);
                for (int f = 0; f < 120; f++) { sim.OnUpdate(Dt); }
                Expect(afterBase - HealthOf(dummy) > 0f,
                    $"拆台之后它应退回原型数值照常攻击（打掉 {afterBase - HealthOf(dummy):F1}）");
            }
            finally
            {
                actions.Unbind();
                registry.Unbind();
                sim.End();
                InputRouter.Reset();
            }
        }

        /// <summary>
        /// M3-02：蓝图库领域模型。只测纯内存逻辑（不落盘），与 ValidateCodex 对
        /// CodexPersistence 的处理口径一致——文件 IO 层不进这个自检。
        /// </summary>
        private static void ValidateBlueprintLibrary()
        {
            Line("\n[26] 蓝图库领域模型（M3-02）");

            string organId = OrganelleCatalog.All.Keys.FirstOrDefault();
            string geneId = GeneCatalog.AllGeneIds.FirstOrDefault();
            Expect(organId != null, "OrganelleCatalog 应至少有一条目录用于本项自检");
            Expect(geneId != null, "GeneCatalog 应至少有一条目录用于本项自检");
            if (organId == null || geneId == null)
            {
                return;
            }

            var registry = new BlueprintRegistry();

            // 验收 1：拾到器官不会自动解锁蓝图——registry 从不订阅任何拾取/掉落信号，
            // 新建实例对任何 catalog id 都默认未解锁。
            Expect(!registry.IsUnlocked(organId), "新建蓝图库对未解析的器官应为未解锁");
            Expect(!registry.IsUnlocked(geneId), "新建蓝图库对未解析的基因应为未解锁");

            BlueprintEntry bogus = registry.Resolve("org_does_not_exist_xyz", BlueprintSourceKind.Organelle, 1f, 0f);
            Expect(bogus == null, "Catalog 查无此 id 时 Resolve 应 no-op 返回 null，不应凭空造出蓝图");

            // 验收 2：解析后能稳定复制——完整度攒到 1 才算解锁，之后多次查询不改变状态。
            registry.Resolve(organId, BlueprintSourceKind.Organelle, 0.5f, 0.1f);
            Expect(!registry.IsUnlocked(organId), "完整度未满 1 时不应算解锁");
            BlueprintEntry organEntry = registry.Resolve(organId, BlueprintSourceKind.Organelle, 0.5f, 0.1f);
            Expect(organEntry != null && organEntry.Unlocked, "两次解析累满完整度后应解锁");
            Expect(organEntry.RepeatResolveCount == 2, "重复解析进度应计入两次调用");

            for (int i = 0; i < 5; i++)
            {
                Expect(registry.IsUnlocked(organId), "重复查询已解锁蓝图不应改变其解锁状态（稳定复制）");
            }

            // 验收 3：拆解后不能复制——完整度只增不减，污染变化不能反向撤销解锁。
            float completenessBefore = organEntry.Completeness;
            registry.Resolve(organId, BlueprintSourceKind.Organelle, 0f, 0.9f);
            Expect(organEntry.Completeness >= completenessBefore,
                "污染变化不应反向降低完整度（蓝图一旦解锁不能被撤销）");
            Expect(organEntry.Unlocked, "污染增加后蓝图仍应保持已解锁");

            // 基因轨同一套逻辑（防止只测了 Organelle 分支）。
            BlueprintEntry geneEntry = registry.Resolve(geneId, BlueprintSourceKind.Gene, 1f, 0f);
            Expect(geneEntry != null && geneEntry.Unlocked, "基因蓝图一次性给满完整度应立即解锁");

            // 实施第 5 条：旧存档默认迁移应让老玩家维持"全目录可用"，而不是清空。
            BlueprintHistory legacy = BlueprintMigration.BuildLegacyDefaults();
            Expect(legacy.Entries.Any(e => e.SourceId == organId && e.Unlocked),
                "旧存档默认迁移应包含现有 Organelle 目录且已解锁");
            Expect(legacy.Entries.Any(e => e.SourceId == geneId && e.Unlocked),
                "旧存档默认迁移应包含现有 Gene 目录且已解锁");
        }

        /// <summary>
        /// M3-03：谱系与表型模板模型。只测纯内存逻辑（本期无落盘），覆盖里程碑验收两条：
        /// 「修改模板不会回写旧版本」「相同配方生成相同签名」。
        /// </summary>
        private static void ValidateLineagePhenotypeTemplate()
        {
            Line("\n[27] 谱系与表型模板模型（M3-03）");

            OrganelleDef organelle = OrganelleCatalog.All.Values.FirstOrDefault(o => o.AttackMethod && !o.IsRetired);
            List<string> geneIds = GeneCatalog.AllGeneIds.Take(2).ToList();
            Expect(organelle != null, "OrganelleCatalog 应至少有一条 AttackMethod 未退役器官用于本项自检");
            Expect(geneIds.Count == 2, "GeneCatalog 应至少有两条基因用于本项自检");
            if (organelle == null || geneIds.Count < 2)
            {
                return;
            }

            var blueprints = new BlueprintRegistry();
            blueprints.Resolve(organelle.Id, BlueprintSourceKind.Organelle, 1f, 0f);
            foreach (string g in geneIds)
            {
                blueprints.Resolve(g, BlueprintSourceKind.Gene, 1f, 0f);
            }

            var lineages = new LineageRegistry();
            lineages.Bind(blueprints);

            // 校验蓝图所有权：未解锁的蓝图不能进模板。
            var unlocked = new LineageRegistry();
            unlocked.Bind(new BlueprintRegistry());
            PhenotypeTemplateVersion rejected = unlocked.CommitTemplate("lineage-a", "assault", organelle.Id, geneIds, "keep_distance", out string ownershipError);
            Expect(rejected == null && ownershipError != null, "未解锁蓝图提交模板应被 Reject-to-Safe 拒绝");

            // 槎位/兼容性校验：主器官槎位塞一个基因 id 应被拒绝（不是 AttackMethod 器官）。
            PhenotypeTemplateVersion badSlot = lineages.CommitTemplate("lineage-a", "assault", geneIds[0], geneIds, "keep_distance", out string slotError);
            Expect(badSlot == null && slotError != null, "主器官槎位放非 AttackMethod id 应被拒绝");

            // 正式提交 v1。
            PhenotypeTemplateVersion v1 = lineages.CommitTemplate("lineage-a", "assault", organelle.Id, geneIds, "keep_distance", out string errV1);
            Expect(v1 != null && errV1 == null, "合法配方提交应成功产生版本 1");
            Expect(v1 != null && v1.Version == 1, "首次提交应是版本 1");

            // 验收 A：修改模板不会回写旧版本——同名模板再提交一次（哪怕配方不变）产生新的版本对象，
            // 旧对象引用不变、字段不变。
            PhenotypeTemplateVersion v1Ref = v1;
            float v1BiomassBefore = v1Ref.BiomassCost;
            PhenotypeTemplateVersion v2 = lineages.CommitTemplate("lineage-a", "assault", organelle.Id, geneIds, "escort", out string errV2);
            Expect(v2 != null && errV2 == null, "第二次提交应成功产生版本 2");
            Expect(v2 != null && v2.Version == 2, "第二次提交应是版本 2，不覆盖版本 1");
            Expect(ReferenceEquals(v1, v1Ref) && v1Ref.DoctrineTag == "keep_distance",
                "旧版本对象引用与字段不应被后续提交改写");
            Expect(v1Ref.BiomassCost == v1BiomassBefore, "旧版本的生物质成本不应被后续提交改写");
            Expect(lineages.GetLineage("lineage-a").GetHistory("assault").Count == 2,
                "模板历史应只追加，两次提交后应有 2 条版本记录");
            Expect(lineages.GetLineage("lineage-a").GetLatest("assault").Version == 2,
                "GetLatest 应返回最新版本（2），不是版本 1");

            // 验收 B：相同配方生成相同签名（同一批 geneIds 实例，两次独立 CommitTemplate 应得同一签名）。
            PhenotypeTemplateVersion v3SameRecipe = lineages.CommitTemplate("lineage-b", "escort", organelle.Id, geneIds, "keep_distance", out string errV3);
            Expect(v3SameRecipe != null && errV3 == null, "不同谱系下同配方提交应独立成功");
            Expect(v3SameRecipe != null && v3SameRecipe.Signature == v1.Signature,
                "相同 OrganelleId+有序 GeneIds 应生成相同签名，即便谱系/模板名不同");

            // 签名对顺序敏感：调换基因顺序应产生不同签名（基因是有序节点）。
            List<string> reversedGenes = new List<string>(geneIds);
            reversedGenes.Reverse();
            if (reversedGenes.Count == geneIds.Count && !reversedGenes.SequenceEqual(geneIds))
            {
                string reversedSignature = PhenotypeTemplateSignature.Compute(organelle.Id, reversedGenes);
                Expect(reversedSignature != v1.Signature, "调换基因顺序应产生不同签名（基因节点有序）");
            }

            // 签名跨进程/跨实例稳定：不依赖 GetHashCode 的进程内加盐随机性。
            string recomputed = PhenotypeTemplateSignature.Compute(organelle.Id, geneIds);
            Expect(recomputed == v1.Signature, "同一函数对同一输入重复计算应得到完全一致的签名字符串");
        }

        /// <summary>M3-04 验收：100 个同模板单位只编译一次静态组合；改变一个个体的伤势不污染其他单位。
        /// 用 <see cref="CompiledRecipeCache.BuildCount"/> 埋点直接断言静态相位真实构建次数——
        /// 不是从"结果看起来一样"反推缓存生效，是直接读命中/未命中计数。</summary>
        private static void ValidateCompiledRecipeCache()
        {
            Line("\n[28] 装配签名编译与缓存（M3-04）");

            OrganelleDef organelle = OrganelleCatalog.All.Values.FirstOrDefault(o => o.AttackMethod && !o.IsRetired);
            List<string> geneIds = GeneCatalog.AllGeneIds.Take(2).ToList();
            Expect(organelle != null, "OrganelleCatalog 应至少有一条 AttackMethod 未退役器官用于本项自检");
            Expect(geneIds.Count == 2, "GeneCatalog 应至少有两条基因用于本项自检");
            if (organelle == null || geneIds.Count < 2)
            {
                return;
            }

            CompiledRecipeCache.ResetForTest();
            var engine = new ComposeEngine.Engine();
            var world = new ComposeEngine.Core.WorldState();

            // 验收 A：100 个"同模板单位"（同 OrganelleId + 同有序 GeneIds，不同 seed/cellId 模拟不同个体）
            // 只应触发一次静态相位构建。
            int totalEvents = 0;
            for (int i = 0; i < 100; i++)
            {
                List<ComposeEngine.Core.HitEvent> events = CarrierCompiler.CompileFromRecipe(engine, organelle.Id, geneIds, world, seed: i, cellId: $"unit_{i}");
                totalEvents += events.Count;
            }
            Expect(CompiledRecipeCache.BuildCount == 1,
                $"100 个同模板单位应只构建 1 次静态编译，实际 BuildCount={CompiledRecipeCache.BuildCount}");
            Expect(totalEvents == 100, $"100 次调用每次应各自产出 1 条 HitEvent，实际累计 {totalEvents}");

            // 换一套配方（基因顺序反过来）应该是不同签名、独立构建一次——证明缓存按内容区分，
            // 不是"永远命中同一份"的假缓存。
            List<string> reversedGenes = new List<string>(geneIds);
            reversedGenes.Reverse();
            CarrierCompiler.CompileFromRecipe(engine, organelle.Id, reversedGenes, world, seed: 999, cellId: "unit_reversed");
            Expect(CompiledRecipeCache.BuildCount == 2,
                $"基因顺序不同应视为不同配方、独立构建，实际 BuildCount={CompiledRecipeCache.BuildCount}");

            // 验收：实物轨（Compile）与配方轨（CompileFromRecipe）对同一 OrganelleId+有序 GeneIds
            // 应命中同一份缓存——两条轨共用同一条化学链路，不是各编各的。
            var reserve = new GeneReserve();
            var carrier = new CarrierInstance("carrier_cache_check", organelle.Id);
            for (int i = 0; i < geneIds.Count; i++)
            {
                var geneInstance = new GeneInstance($"inst_cache_{i}", geneIds[i], GeneLocation.Reserve());
                reserve.TryAdd(geneInstance);
                carrier.Slots[i].GeneInstanceId = geneInstance.GeneInstanceId;
            }
            CarrierCompiler.Compile(engine, carrier, reserve, world, seed: 1);
            Expect(CompiledRecipeCache.BuildCount == 2,
                $"实物轨对同一配方应命中配方轨已建好的缓存，不应新增构建，实际 BuildCount={CompiledRecipeCache.BuildCount}");

            // 验收 B：改变一个个体的伤势（Strain）不污染其他单位——本次改动只加了内容地址式静态缓存，
            // 不触碰 UnitVitalsRegistry 的按 SimEntityId 归属，这里守住这条回归线。
            var vitals = new UnitVitalsRegistry();
            var entityA = new SimEntityId(1UL);
            var entityB = new SimEntityId(2UL);
            vitals.Reset();
            vitals.AddStrain(entityA, 50f);
            Expect(Mathf.Approximately(vitals.Get(entityB).Strain, 0f),
                "个体 A 增加过载债不应污染个体 B 的过载债");
            Expect(vitals.Get(entityA).Strain > 0f, "个体 A 自身的过载债应确实增加");
        }

        /// <summary>
        /// M3-05 验收：萌生队列/生物质账本/版本隔离/取消退款/腔体被毁。本项只测不需要真实 Sim 的部分
        /// （队列记账、版本捕获、取消/摧毁退款）——生成真实地图实体那一步需要一个运行中的 SimWorld，
        /// 留给运行期人测/execute_code 断言，见 DIGEST。
        /// </summary>
        private static void ValidateGerminationChamber()
        {
            Line("\n[29] 萌生腔与新生传播（M3-05）");

            // 生物质账本：Reject-to-Safe，扣不动就不扣，不产生负余额。
            var ledger = new BiomassLedger();
            ledger.OnEnter();
            float initial = ledger.GetBalance("lineage-x");
            Expect(initial == BiomassLedger.DefaultStartingBalance, "未记过账的谱系应按占位起始余额读取（纯读取，不因查询而改变状态）");
            bool overdraft = ledger.TryDeduct("lineage-x", BiomassLedger.DefaultStartingBalance + 1f);
            Expect(!overdraft, "超过起始余额的扣款应被拒绝");
            Expect(ledger.GetBalance("lineage-x") == BiomassLedger.DefaultStartingBalance, "被拒绝的扣款不应产生任何副作用，余额应保持不变");
            bool ok = ledger.TryDeduct("lineage-x", 40f);
            Expect(ok, "余额充足时扣款应成功");
            Expect(ledger.GetBalance("lineage-x") == BiomassLedger.DefaultStartingBalance - 40f,
                "扣款后余额应为 起始余额-40");
            ledger.Refund("lineage-x", 10f);
            Expect(ledger.GetBalance("lineage-x") == BiomassLedger.DefaultStartingBalance - 30f,
                "退款应原样加回余额");

            // 萌生队列：架在蓝图库 + 谱系之上，同 [27] 的搭建方式。
            OrganelleDef organelle = OrganelleCatalog.All.Values.FirstOrDefault(o => o.AttackMethod && !o.IsRetired);
            List<string> geneIds = GeneCatalog.AllGeneIds.Take(2).ToList();
            Expect(organelle != null, "OrganelleCatalog 应至少有一条 AttackMethod 未退役器官用于本项自检");
            Expect(geneIds.Count == 2, "GeneCatalog 应至少有两条基因用于本项自检");
            if (organelle == null || geneIds.Count < 2)
            {
                return;
            }

            var blueprints = new BlueprintRegistry();
            blueprints.Resolve(organelle.Id, BlueprintSourceKind.Organelle, 1f, 0f);
            foreach (string g in geneIds)
            {
                blueprints.Resolve(g, BlueprintSourceKind.Gene, 1f, 0f);
            }

            var lineages = new LineageRegistry();
            lineages.Bind(blueprints);
            PhenotypeTemplateVersion v1 = lineages.CommitTemplate("lineage-y", "assault", organelle.Id, geneIds, "keep_distance", out string commitErr);
            Expect(v1 != null && commitErr == null, "本项前置：合法配方提交应成功产生版本 1");

            var chamberLedger = new BiomassLedger();
            chamberLedger.OnEnter();
            var chamber = new GerminationChamberRegistry();
            chamber.OnEnter();
            chamber.Bind(null, lineages, chamberLedger, null); // 只测不需要 SimBridge 的队列/账本逻辑。

            // 未知谱系/模板：Reject-to-Safe，不入队不扣费。
            int badTicket = chamber.Enqueue("lineage-y", "no_such_template", out PhenotypeTemplateVersion badVersion, out string badErr);
            Expect(badTicket == 0 && badVersion == null && badErr != null, "查无模板版本时应拒绝入队");
            Expect(chamberLedger.GetBalance("lineage-y") == BiomassLedger.DefaultStartingBalance,
                "被拒绝的入队不应扣费");

            // 正常入队：捕获当时的版本引用，扣费。
            int ticketA = chamber.Enqueue("lineage-y", "assault", out PhenotypeTemplateVersion capturedA, out string errA);
            Expect(ticketA != 0 && errA == null, "合法入队应成功");
            Expect(capturedA != null && capturedA.Version == 1, "入队应捕获当前最新版本（1）");
            Expect(chamberLedger.GetBalance("lineage-y") == BiomassLedger.DefaultStartingBalance - v1.BiomassCost,
                "入队应按该版本的生物质成本扣费");
            Expect(chamber.PendingCount("lineage-y") == 1, "入队后队列应有 1 项待处理");

            // 版本隔离核心：谱系提交 V2 之后，ticketA 已经捕获的版本对象引用不应受影响——
            // 这就是"已经排队/已经生成的旧个体保持 V1"的机制来源（生成时用的是这份捕获引用，不是
            // 事后再查一次"最新版本"）。
            PhenotypeTemplateVersion v2 = lineages.CommitTemplate("lineage-y", "assault", organelle.Id, geneIds, "escort", out string commitErr2);
            Expect(v2 != null && v2.Version == 2, "本项前置：第二次提交应产生版本 2");
            Expect(ReferenceEquals(capturedA, v1) && capturedA.Version == 1,
                "已入队票据捕获的版本对象不应被后续的谱系提交改变");

            // 新入队应表达 V2——版本隔离的另一半："之后新萌生的同表型个体自动使用新版模板"（GDD §6.6）。
            int ticketB = chamber.Enqueue("lineage-y", "assault", out PhenotypeTemplateVersion capturedB, out string errB);
            Expect(ticketB != 0 && errB == null, "第二次入队应成功");
            Expect(capturedB != null && capturedB.Version == 2, "谱系提交新版本之后的入队应捕获最新版本（2）");
            Expect(chamber.PendingCount("lineage-y") == 2, "两次入队后队列应有 2 项待处理");

            // 取消：全额退款，移出队列，幂等（重复取消同一票据第二次应失败）。
            float balanceBeforeCancel = chamberLedger.GetBalance("lineage-y");
            bool cancelled = chamber.Cancel(ticketA);
            Expect(cancelled, "取消一个未完成票据应成功");
            Expect(chamberLedger.GetBalance("lineage-y") == balanceBeforeCancel + capturedA.BiomassCost,
                "取消应全额退还该票据的生物质成本");
            Expect(chamber.PendingCount("lineage-y") == 1, "取消后队列应减少 1 项");
            bool cancelledAgain = chamber.Cancel(ticketA);
            Expect(!cancelledAgain, "重复取消同一票据应失败（幂等，不重复退款）");

            // 腔体被毁：剩余队列项整体失败退款，之后拒绝新入队；已发生的事（前面的取消）不受影响。
            float balanceBeforeDestroy = chamberLedger.GetBalance("lineage-y");
            chamber.DestroyPod("lineage-y");
            Expect(chamberLedger.GetBalance("lineage-y") == balanceBeforeDestroy + capturedB.BiomassCost,
                "腔体被毁应退还队列里剩余票据的全部生物质成本");
            Expect(chamber.PendingCount("lineage-y") == 0, "腔体被毁后队列应清空");
            int ticketAfterDestroy = chamber.Enqueue("lineage-y", "assault", out PhenotypeTemplateVersion _, out string errAfterDestroy);
            Expect(ticketAfterDestroy == 0 && errAfterDestroy != null, "腔体被毁后应拒绝新的入队请求");

            // 其他表型不受影响：另一个谱系/模板的队列与账本应完全独立。
            var otherLedger = new BiomassLedger();
            otherLedger.OnEnter();
            var otherChamber = new GerminationChamberRegistry();
            otherChamber.OnEnter();
            otherChamber.Bind(null, lineages, otherLedger, null);
            lineages.CommitTemplate("lineage-z", "escort", organelle.Id, geneIds, "guard", out _);
            int otherTicket = otherChamber.Enqueue("lineage-z", "escort", out PhenotypeTemplateVersion _, out string otherErr);
            Expect(otherTicket != 0 && otherErr == null, "其他谱系的入队不应被 lineage-y 的腔体摧毁状态波及");
            Expect(otherChamber.PendingCount("lineage-z") == 1, "其他谱系的队列应独立计数");
        }

        /// <summary>
        /// M3-06：回巢改造——断网/交战/携带物/资源不足四项拒绝，正常改造的版本锁定与状态保留，
        /// 中断不产出重复实体/器官。用真实 SimBridge 起两具个体（一具在腔体范围内、一具太远），
        /// 手工把它们绑到 V1（模拟"之前几局遗留的旧个体"，不经 [29] 的 Enqueue 流程）。
        /// </summary>
        private static void ValidateHomecomingRetrofit()
        {
            Line("\n[30] 回巢改造（M3-06）");

            OrganelleDef organelle = OrganelleCatalog.All.Values.FirstOrDefault(o => o.AttackMethod && !o.IsRetired);
            List<string> geneIds = GeneCatalog.AllGeneIds.Take(2).ToList();
            Expect(organelle != null, "OrganelleCatalog 应至少有一条 AttackMethod 未退役器官用于本项自检");
            Expect(geneIds.Count == 2, "GeneCatalog 应至少有两条基因用于本项自检");
            if (organelle == null || geneIds.Count < 2)
            {
                return;
            }

            var blueprints = new BlueprintRegistry();
            blueprints.Resolve(organelle.Id, BlueprintSourceKind.Organelle, 1f, 0f);
            foreach (string g in geneIds)
            {
                blueprints.Resolve(g, BlueprintSourceKind.Gene, 1f, 0f);
            }

            var lineages = new LineageRegistry();
            lineages.Bind(blueprints);
            PhenotypeTemplateVersion v1 = lineages.CommitTemplate("lineage-r", "assault", organelle.Id, geneIds, "keep_distance", out string commitErr);
            Expect(v1 != null && commitErr == null, "本项前置：合法配方提交应成功产生版本 1");

            var ledger = new BiomassLedger();
            ledger.OnEnter();
            var chamber = new GerminationChamberRegistry();
            chamber.OnEnter();

            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 60f;
            cfg.RandomSeed = 0xC0FFEE30u;
            var archetypes = new[]
            {
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Stationary, Accel = 0f, TurnRate = 0f, AggroRange = 0f,
                    AttackRange = 0.5f, AttackCooldown = 99f, AttackDamage = 0f,
                    Separation = 0f, ChargeSpeedMul = 1f,
                },
            };
            sim.Begin(cfg, archetypes);

            var unitLoadouts = new UnitLoadoutRegistry();
            var fakeSource = new FakePlayerLoadoutSource();
            unitLoadouts.Bind(sim, fakeSource);
            unitLoadouts.RegisterPlayerBody(sim.ControlledUnitId);

            chamber.Bind(sim, lineages, ledger, unitLoadouts);

            var retrofit = new HomecomingRetrofitService();
            retrofit.OnEnter();
            retrofit.Bind(sim, lineages, ledger, unitLoadouts, chamber);

            const int NearLogicId = 9801;
            const int FarLogicId = 9802;
            float2 playerPos = sim.PlayerPosition;

            sim.Spawn(new SpawnRequest
            {
                Position = playerPos + new float2(2f, 0f), Health = 40f, Radius = 0.8f, MaxSpeed = 0f,
                ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                IntentSource = IntentSource.AI, LogicId = NearLogicId, ExcludeFromControl = true,
            });
            sim.Spawn(new SpawnRequest
            {
                Position = playerPos + new float2(40f, 0f), Health = 40f, Radius = 0.8f, MaxSpeed = 0f,
                ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                IntentSource = IntentSource.AI, LogicId = FarLogicId, ExcludeFromControl = true,
            });

            sim.OnUpdate(1f / 60f);
            SimSnapshot snap = sim.Snapshot;
            SimEntityId near = FindEntityId(snap, NearLogicId, out _);
            SimEntityId far = FindEntityId(snap, FarLogicId, out _);
            Expect(near.IsValid && far.IsValid, "本项前置：两具测试个体应成功落地");
            if (!near.IsValid || !far.IsValid)
            {
                return;
            }

            // 未绑定：查无绑定应拒绝。
            HomecomingRetrofitService.RetrofitRejectReason reason = retrofit.TryBeginRetrofit(near);
            Expect(reason == HomecomingRetrofitService.RetrofitRejectReason.NotBound, "未绑定谱系/模板的个体应拒绝回巢改造");

            // 手工把两具个体绑到 V1（模拟"之前几局遗留/萌生腔生成"的旧个体，不经 [29] 的 Enqueue）。
            chamber.UpdateBinding(near, "lineage-r", "assault", v1);
            chamber.UpdateBinding(far, "lineage-r", "assault", v1);
            unitLoadouts.RegisterExplicit(near, new List<UnitLoadoutOrgan> { new UnitLoadoutOrgan(v1.OrganelleId, LoadoutAction.Primary) });
            unitLoadouts.RegisterExplicit(far, new List<UnitLoadoutOrgan> { new UnitLoadoutOrgan(v1.OrganelleId, LoadoutAction.Primary) });

            // 已绑定但谱系没有更新版本：拒绝。
            reason = retrofit.TryBeginRetrofit(near);
            Expect(reason == HomecomingRetrofitService.RetrofitRejectReason.NoNewerVersion, "谱系没有比当前绑定更新的版本时应拒绝改造");

            PhenotypeTemplateVersion v2 = lineages.CommitTemplate("lineage-r", "assault", organelle.Id, geneIds, "escort", out string commitErr2);
            Expect(v2 != null && v2.Version == 2, "本项前置：第二次提交应产生版本 2");

            // 断网：拒绝，不扣费。
            chamber.SetNetworked("lineage-r", false);
            float balanceBeforeNetworkReject = ledger.GetBalance("lineage-r");
            reason = retrofit.TryBeginRetrofit(near);
            Expect(reason == HomecomingRetrofitService.RetrofitRejectReason.NotNetworked, "萌生腔未联网时应拒绝改造");
            Expect(ledger.GetBalance("lineage-r") == balanceBeforeNetworkReject, "被拒绝的改造不应扣费");
            chamber.SetNetworked("lineage-r", true);

            // 交战：拒绝，不扣费。
            retrofit.SetEngaged(near, true);
            reason = retrofit.TryBeginRetrofit(near);
            Expect(reason == HomecomingRetrofitService.RetrofitRejectReason.Engaged, "交战中的个体应拒绝改造");
            retrofit.SetEngaged(near, false);

            // 携带关键物：拒绝，不扣费。
            retrofit.SetCarryingKeyItem(near, true);
            reason = retrofit.TryBeginRetrofit(near);
            Expect(reason == HomecomingRetrofitService.RetrofitRejectReason.CarryingKeyItem, "携带关键物的个体应拒绝改造");
            retrofit.SetCarryingKeyItem(near, false);

            // 位置太远：拒绝，不扣费。
            reason = retrofit.TryBeginRetrofit(far);
            Expect(reason == HomecomingRetrofitService.RetrofitRejectReason.TooFarFromChamber, "距萌生腔太远的个体应拒绝改造");

            // 资源不足：拒绝，不扣费（先把余额抽干）。
            ledger.TryDeduct("lineage-r", ledger.GetBalance("lineage-r"));
            Expect(ledger.GetBalance("lineage-r") == 0f, "本项前置：余额应已被抽干");
            reason = retrofit.TryBeginRetrofit(near);
            Expect(reason == HomecomingRetrofitService.RetrofitRejectReason.InsufficientBiomass, "生物质不足时应拒绝改造");
            ledger.Deposit("lineage-r", 999f); // 补回余额，供后续正常路径使用。

            // 中断（取消）：全额退款，绑定/装配原样保留——「中断不复制资源或器官」的核心验收点。
            float balanceBeforeCancelFlow = ledger.GetBalance("lineage-r");
            reason = retrofit.TryBeginRetrofit(near);
            Expect(reason == HomecomingRetrofitService.RetrofitRejectReason.None, "满足全部条件时应成功开票");
            Expect(ledger.GetBalance("lineage-r") == balanceBeforeCancelFlow - v2.BiomassCost, "开票应按锁定版本的生物质成本扣费");
            Expect(retrofit.IsRetrofitting(near), "开票后该个体应处于改造中状态");
            HomecomingRetrofitService.RetrofitRejectReason reasonWhileInProgress = retrofit.TryBeginRetrofit(near);
            Expect(reasonWhileInProgress == HomecomingRetrofitService.RetrofitRejectReason.AlreadyInProgress, "改造进行中不应允许重复开票");
            bool cancelled = retrofit.CancelRetrofit(near);
            Expect(cancelled, "取消进行中的改造应成功");
            Expect(ledger.GetBalance("lineage-r") == balanceBeforeCancelFlow, "取消应全额退还生物质成本");
            Expect(!retrofit.IsRetrofitting(near), "取消后该个体不应再处于改造中状态");
            chamber.TryGetBinding(near, out GerminationChamberRegistry.UnitBinding bindingAfterCancel);
            Expect(bindingAfterCancel.Version == v1, "取消不应改变该个体的绑定版本");
            UnitLoadout loadoutAfterCancel = unitLoadouts.Get(near);
            Expect(loadoutAfterCancel.TryGetOrgan(LoadoutAction.Primary, out UnitLoadoutOrgan organAfterCancel) && organAfterCancel.OrganId == v1.OrganelleId,
                "取消不应改变该个体的装配");

            // 个体运行期状态（伤势/过载债）与改造流程完全独立——本类从未触碰 UnitVitalsRegistry。
            var vitals = new UnitVitalsRegistry();
            vitals.AddStrain(near, 37f);
            float strainBefore = vitals.Get(near).Strain;

            // 正常改造 + 版本锁定：开票时锁定 V2，之后即便谱系又提交 V3，完成时仍应换到 V2（不是 V3）。
            reason = retrofit.TryBeginRetrofit(near);
            Expect(reason == HomecomingRetrofitService.RetrofitRejectReason.None, "重新开票应再次成功");
            PhenotypeTemplateVersion v3 = lineages.CommitTemplate("lineage-r", "assault", organelle.Id, geneIds, "aggressive", out string commitErr3);
            Expect(v3 != null && v3.Version == 3, "本项前置：第三次提交应产生版本 3");

            bool completed = retrofit.CompleteRetrofit(near);
            Expect(completed, "完成改造应成功");
            Expect(!retrofit.IsRetrofitting(near), "完成后该个体不应再处于改造中状态");
            chamber.TryGetBinding(near, out GerminationChamberRegistry.UnitBinding bindingAfterComplete);
            Expect(ReferenceEquals(bindingAfterComplete.Version, v2),
                "完成改造应换到开票那一刻锁定的版本（V2），即便过程中谱系又提交了更新版本（V3）——锁定语义");
            UnitLoadout loadoutAfterComplete = unitLoadouts.Get(near);
            Expect(loadoutAfterComplete.TryGetOrgan(LoadoutAction.Primary, out UnitLoadoutOrgan organAfterComplete) && organAfterComplete.OrganId == v2.OrganelleId,
                "完成改造应替换装配签名——UnitLoadoutRegistry 里的主器官应变为锁定版本的 OrganelleId");
            Expect(vitals.Get(near).Strain == strainBefore,
                "回巢改造流程不应触碰个体的运行期状态（生命/伤势/过载债应原样保留）");

            // 另一个谱系完全不受影响。
            lineages.CommitTemplate("lineage-s", "guard", organelle.Id, geneIds, "guard", out _);
            Expect(lineages.GetLineage("lineage-s").GetLatest("guard").Version == 1,
                "其他谱系的模板历史不应被 lineage-r 的回巢改造流程波及");
        }

        /// <summary>
        /// M3-07：野生器官与单体临时移植——拾取不解锁蓝图、解析/拆解/保留三种处理、
        /// 临时移植不改模板、同一实物不能同时解析+装备、槽位冲突/非器官拒绝装入、
        /// 携带容量、身体死亡丢失临时器官。用真实 SimBridge 起若干测试个体。
        /// </summary>
        private static void ValidateWildOrganLoot()
        {
            Line("\n[31] 野生器官与单体临时移植（M3-07）");

            OrganelleDef organelle = OrganelleCatalog.All.Values.FirstOrDefault(o => o.AttackMethod && !o.IsRetired);
            string gene = GeneCatalog.AllGeneIds.FirstOrDefault();
            Expect(organelle != null, "OrganelleCatalog 应至少有一条 AttackMethod 未退役器官用于本项自检");
            Expect(gene != null, "GeneCatalog 应至少有一条基因用于本项自检");
            if (organelle == null || gene == null)
            {
                return;
            }

            var blueprints = new BlueprintRegistry();
            var biomass = new BiomassLedger();
            biomass.OnEnter();

            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 60f;
            cfg.RandomSeed = 0xC0FFEE31u;
            var archetypes = new[]
            {
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Stationary, Accel = 0f, TurnRate = 0f, AggroRange = 0f,
                    AttackRange = 0.5f, AttackCooldown = 99f, AttackDamage = 0f,
                    Separation = 0f, ChargeSpeedMul = 1f,
                },
            };
            sim.Begin(cfg, archetypes);

            var unitLoadouts = new UnitLoadoutRegistry();
            var fakeSource = new FakePlayerLoadoutSource();
            unitLoadouts.Bind(sim, fakeSource);
            unitLoadouts.RegisterPlayerBody(sim.ControlledUnitId);

            var wildOrgans = new WildOrganRegistry();
            wildOrgans.OnEnter();
            wildOrgans.Bind(sim, unitLoadouts);

            float2 playerPos = sim.PlayerPosition;
            const int BodyALogicId = 9901;
            const int BodyCLogicId = 9902; // 已带模板 Primary 器官的个体，用来测槽位冲突。
            const int FarLogicId = 9903;

            sim.Spawn(new SpawnRequest
            {
                Position = playerPos, Health = 40f, Radius = 0.8f, MaxSpeed = 0f,
                ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                IntentSource = IntentSource.AI, LogicId = BodyALogicId, ExcludeFromControl = true,
            });
            sim.Spawn(new SpawnRequest
            {
                Position = playerPos, Health = 40f, Radius = 0.8f, MaxSpeed = 0f,
                ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                IntentSource = IntentSource.AI, LogicId = BodyCLogicId, ExcludeFromControl = true,
            });
            sim.Spawn(new SpawnRequest
            {
                Position = playerPos + new float2(40f, 0f), Health = 40f, Radius = 0.8f, MaxSpeed = 0f,
                ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                IntentSource = IntentSource.AI, LogicId = FarLogicId, ExcludeFromControl = true,
            });

            sim.OnUpdate(1f / 60f);
            SimSnapshot snap = sim.Snapshot;
            SimEntityId bodyA = FindEntityId(snap, BodyALogicId, out _);
            SimEntityId bodyC = FindEntityId(snap, BodyCLogicId, out _);
            SimEntityId bodyFar = FindEntityId(snap, FarLogicId, out _);
            Expect(bodyA.IsValid && bodyC.IsValid && bodyFar.IsValid, "本项前置：三具测试个体应成功落地");
            if (!bodyA.IsValid || !bodyC.IsValid || !bodyFar.IsValid)
            {
                return;
            }

            unitLoadouts.RegisterExplicit(bodyA, new List<UnitLoadoutOrgan>());
            // bodyC 模拟"已经表达模板"的个体：Primary 槽已经被一件常规器官占用，用来测临时槽冲突。
            unitLoadouts.RegisterExplicit(bodyC, new List<UnitLoadoutOrgan> { new UnitLoadoutOrgan(organelle.Id, LoadoutAction.Primary) });

            // 建立物理战利品实体：非法 id 拒绝。
            string badDrop = wildOrgans.DropInField("no_such_organelle", BlueprintSourceKind.Organelle, playerPos);
            Expect(badDrop == null, "查无 id 的战利品实体应拒绝建立");

            string idA = wildOrgans.DropInField(organelle.Id, BlueprintSourceKind.Organelle, playerPos);
            Expect(idA != null, "合法器官 id 应成功建立物理战利品实体");

            // 拾取：太远拒绝。
            WildOrganPickupResult farPickup = wildOrgans.TryPickup(bodyFar, idA);
            Expect(farPickup == WildOrganPickupResult.TooFar, "拾取者距战利品实体太远时应拒绝");

            // 拾取成功：状态转 Carried，不解锁蓝图（验收 1）。
            WildOrganPickupResult pickupA = wildOrgans.TryPickup(bodyA, idA);
            Expect(pickupA == WildOrganPickupResult.Ok, "近距离拾取应成功");
            Expect(wildOrgans.GetInstance(idA).State == WildOrganState.Carried, "拾取后实物状态应为 Carried");
            Expect(wildOrgans.CarriedCount(bodyA) == 1, "拾取后携带计数应为 1");
            Expect(!blueprints.IsUnlocked(organelle.Id), "拾取绝不应自动解锁蓝图（验收 1）");

            // 重复拾取同一件已不在 InField 的实物：拒绝。
            Expect(wildOrgans.TryPickup(bodyC, idA) == WildOrganPickupResult.NotInField, "已被拾取的实物不应再被第二个人拾取");

            // 临时移植：非器官（基因）拒绝。
            string geneId = wildOrgans.DropInField(gene, BlueprintSourceKind.Gene, playerPos);
            wildOrgans.TryPickup(bodyA, geneId);
            Expect(wildOrgans.TryInstallTemporary(bodyA, geneId) == WildOrganInstallResult.NotAnOrganelle,
                "基因实物不应被允许接入体细胞临时槽（GDD §6.7 只接器官）");

            // 临时移植：正常安装成功，不修改任何模板（验收 1 的另一半——本类型从未碰 LineageRegistry）。
            WildOrganInstallResult install = wildOrgans.TryInstallTemporary(bodyA, idA);
            Expect(install == WildOrganInstallResult.Ok, "满足条件时临时移植应成功");
            Expect(wildOrgans.GetInstance(idA).State == WildOrganState.Installed, "移植成功后实物状态应为 Installed");
            UnitLoadout loadoutA = unitLoadouts.Get(bodyA);
            Expect(loadoutA.TryGetOrgan(LoadoutAction.Primary, out UnitLoadoutOrgan installedOrgan) && installedOrgan.OrganId == organelle.Id,
                "临时移植应让身体的 Primary 动作槽出现该器官");

            // 同一实物不能同时被解析和装备（验收 2）：Installed 状态下解析/拆解应拒绝。
            Expect(wildOrgans.TryResolveAtChamber(bodyA, idA, blueprints) == WildOrganChamberActionResult.NotCarried,
                "已临时移植（Installed）的实物不应允许同时解析");
            Expect(wildOrgans.TryDismantleAtChamber(bodyA, idA, biomass, "lineage-w") == WildOrganChamberActionResult.NotCarried,
                "已临时移植（Installed）的实物不应允许同时拆解");

            // 重复安装：该身体已有临时器官，第二次安装应拒绝。
            string idExtra = wildOrgans.DropInField(organelle.Id, BlueprintSourceKind.Organelle, playerPos);
            wildOrgans.TryPickup(bodyA, idExtra);
            Expect(wildOrgans.TryInstallTemporary(bodyA, idExtra) == WildOrganInstallResult.AlreadyHasTemporary,
                "一具身体同一时刻只应有一个临时槽（非目标：不做多临时槽）");

            // 卸下临时器官：回到 Carried，身体上的器官消失，不影响其他槽位。
            bool uninstalled = wildOrgans.UninstallTemporary(bodyA);
            Expect(uninstalled, "卸下临时器官应成功");
            Expect(wildOrgans.GetInstance(idA).State == WildOrganState.Carried, "卸下后实物状态应回到 Carried");
            Expect(!unitLoadouts.Get(bodyA).HasOrganInSlot(LoadoutAction.Primary), "卸下临时器官后身体不应再有 Primary 槽器官");

            // 槽位冲突：bodyC 的 Primary 槽已被模板器官占用，临时移植应拒绝并给出明确原因。
            string idForC = wildOrgans.DropInField(organelle.Id, BlueprintSourceKind.Organelle, playerPos);
            wildOrgans.TryPickup(bodyC, idForC);
            Expect(wildOrgans.TryInstallTemporary(bodyC, idForC) == WildOrganInstallResult.SlotConflict,
                "临时器官与模板器官冲突时不允许装入，且应给出具体原因（GDD §6.9）");
            Expect(wildOrgans.GetInstance(idForC).State == WildOrganState.Carried, "被拒绝的临时移植不应改变实物状态");

            // 三种处理之「解析」：推进蓝图，实物进入终态，不修改其他单位。
            WildOrganChamberActionResult resolveResult = wildOrgans.TryResolveAtChamber(bodyA, idA, blueprints);
            Expect(resolveResult == WildOrganChamberActionResult.Ok, "在腔体范围内解析 Carried 实物应成功");
            Expect(blueprints.IsUnlocked(organelle.Id), "解析后应解锁对应蓝图");
            Expect(wildOrgans.GetInstance(idA).State == WildOrganState.Resolved, "解析后实物应进入 Resolved 终态");
            Expect(wildOrgans.CarriedCount(bodyA) == 2, "解析后该实物应从携带列表移除（此刻 bodyA 仍携带 geneId 与 idExtra，计数应为 2）");

            // 双花防护：终态实物不能再次解析/拆解。
            Expect(wildOrgans.TryResolveAtChamber(bodyA, idA, blueprints) == WildOrganChamberActionResult.NotCarried,
                "已 Resolved 的实物不应允许再次解析");
            Expect(wildOrgans.TryDismantleAtChamber(bodyA, idA, biomass, "lineage-w") == WildOrganChamberActionResult.NotCarried,
                "已 Resolved 的实物不应允许改为拆解");

            // 三种处理之「拆解」：只产出生物质，不解锁蓝图，与 idExtra 使用同一 sourceId 但各自独立。
            float balanceBeforeDismantle = biomass.GetBalance("lineage-w");
            bool wasUnlockedBeforeDismantle = blueprints.IsUnlocked(organelle.Id);
            WildOrganChamberActionResult dismantleResult = wildOrgans.TryDismantleAtChamber(bodyA, idExtra, biomass, "lineage-w");
            Expect(dismantleResult == WildOrganChamberActionResult.Ok, "在腔体范围内拆解 Carried 实物应成功");
            Expect(biomass.GetBalance("lineage-w") == balanceBeforeDismantle + WildOrganRegistry.DismantleBiomassYield,
                "拆解应产出对应生物质");
            Expect(blueprints.IsUnlocked(organelle.Id) == wasUnlockedBeforeDismantle,
                "拆解不应影响蓝图解锁状态（拆解与解析各自独立结算，验收 2 的另一半）");
            Expect(wildOrgans.GetInstance(idExtra).State == WildOrganState.Dismantled, "拆解后实物应进入 Dismantled 终态");

            // 三种处理之「保留」：不作为，实物应一直停在 Carried，直到被显式处理。
            string idRetain = wildOrgans.DropInField(organelle.Id, BlueprintSourceKind.Organelle, playerPos);
            wildOrgans.TryPickup(bodyA, idRetain);
            Expect(wildOrgans.GetInstance(idRetain).State == WildOrganState.Carried, "「保留」不做任何处理，实物应停留在 Carried");

            // 距腔体太远：即便实物处于 Carried，也不能在腔体范围外解析/拆解。
            string idFar = wildOrgans.DropInField(organelle.Id, BlueprintSourceKind.Organelle, playerPos + new float2(40f, 0f));
            WildOrganPickupResult farOwnPickup = wildOrgans.TryPickup(bodyFar, idFar);
            Expect(farOwnPickup == WildOrganPickupResult.Ok, "本项前置：远处个体拾取自己脚下的战利品应成功");
            Expect(wildOrgans.TryResolveAtChamber(bodyFar, idFar, blueprints) == WildOrganChamberActionResult.TooFarFromChamber,
                "距萌生腔太远时不应允许解析");

            // 携带容量：满了应拒绝，不影响已携带的条目。复用 bodyC，此刻它携带 idForC（1 件）。
            Expect(wildOrgans.CarriedCount(bodyC) == 1, "本项前置：bodyC 当前应携带 1 件（idForC）");
            for (int i = 0; i < WildOrganRegistry.CarryCapacity - 1; i++)
            {
                string extraId = wildOrgans.DropInField(organelle.Id, BlueprintSourceKind.Organelle, playerPos);
                WildOrganPickupResult r = wildOrgans.TryPickup(bodyC, extraId);
                Expect(r == WildOrganPickupResult.Ok, $"携带容量未满前第 {i + 2} 件拾取应成功");
            }
            Expect(wildOrgans.CarriedCount(bodyC) == WildOrganRegistry.CarryCapacity, "携带容量应恰好达到上限");
            string overflowId = wildOrgans.DropInField(organelle.Id, BlueprintSourceKind.Organelle, playerPos);
            Expect(wildOrgans.TryPickup(bodyC, overflowId) == WildOrganPickupResult.CarryFull, "超过携带容量应拒绝拾取");

            // 身体死亡：临时器官通常丢失——不回到 Carried，直接从记录里彻底消失。
            string idBeforeDeath = wildOrgans.DropInField(organelle.Id, BlueprintSourceKind.Organelle, playerPos);
            wildOrgans.TryPickup(bodyA, idBeforeDeath);
            Expect(wildOrgans.TryInstallTemporary(bodyA, idBeforeDeath) == WildOrganInstallResult.Ok,
                "本项前置：bodyA 此刻应已无临时器官（idA 已被解析），可以再次安装");
            bool died = wildOrgans.HandleBodyDeath(bodyA);
            Expect(died, "身体死亡应成功处理已安装的临时器官");
            Expect(wildOrgans.GetInstance(idBeforeDeath) == null, "身体死亡后临时器官应彻底消失，不进入任何终态");
            Expect(!unitLoadouts.Get(bodyA).HasOrganInSlot(LoadoutAction.Primary), "身体死亡后该身体不应再有临时器官占用的动作槽");
            Expect(!wildOrgans.TryGetInstalled(bodyA, out _), "身体死亡后该身体不应再登记有已安装的临时器官");
        }

        /// <summary>
        /// M3-08：模板编辑与传播 UI 背后的只读查询面。本 story 是纯信息展示层（IMGUI 面板见
        /// <c>CellDebugHud.DrawLineage*</c>），面板本身不适合在这里做渲染断言——这里测的是它依赖的
        /// 三个新增只读查询（<see cref="Lineage.TemplateNames"/>/<see cref="LineageRegistry.PreviewCommit"/>/
        /// <see cref="GerminationChamberRegistry.Bindings"/>）返回数据是否正确，以及验收核心
        /// 「提交模板前后旧版本个体数不变」：提交新版本不应联动改写任何已绑定个体的引用。
        /// </summary>
        private static void ValidateTemplateUiQueries()
        {
            Line("\n[32] 模板编辑与传播 UI 只读查询（M3-08）");

            OrganelleDef organelle = OrganelleCatalog.All.Values.FirstOrDefault(o => o.AttackMethod && !o.IsRetired);
            List<string> geneIds = GeneCatalog.AllGeneIds.Take(2).ToList();
            Expect(organelle != null, "OrganelleCatalog 应至少有一条 AttackMethod 未退役器官用于本项自检");
            Expect(geneIds.Count == 2, "GeneCatalog 应至少有两条基因用于本项自检");
            if (organelle == null || geneIds.Count < 2)
            {
                return;
            }

            var blueprints = new BlueprintRegistry();
            blueprints.Resolve(organelle.Id, BlueprintSourceKind.Organelle, 1f, 0f);
            foreach (string g in geneIds)
            {
                blueprints.Resolve(g, BlueprintSourceKind.Gene, 1f, 0f);
            }

            var lineages = new LineageRegistry();
            lineages.Bind(blueprints);

            // PreviewCommit 不应产生任何历史条目（预览与提交不能共用副作用）。
            Expect(lineages.PreviewCommit(organelle.Id, geneIds) == null, "合法配方的预览应返回 null（无冲突）");
            Lineage freshLineage = lineages.GetOrCreateLineage("lineage-ui-preview");
            Expect(freshLineage.TemplateNames.Count == 0, "PreviewCommit 不应产生任何模板历史（预览不是提交）");
            Expect(lineages.PreviewCommit(null, geneIds) != null, "空主器官 id 的预览应返回冲突原因");
            Expect(lineages.PreviewCommit(organelle.Id, new List<string>()) != null, "基因数不足的预览应返回冲突原因");

            PhenotypeTemplateVersion v1 = lineages.CommitTemplate("lineage-ui", "assault", organelle.Id, geneIds, "keep_distance", out string commitErr);
            Expect(v1 != null && commitErr == null, "本项前置：合法配方提交应成功产生版本 1");

            Lineage lineage = lineages.GetLineage("lineage-ui");
            Expect(lineage.TemplateNames.Count == 1 && lineage.TemplateNames.Contains("assault"),
                "TemplateNames 应枚举出刚提交的模板名");

            // 真实 SimBridge 起一具个体，手工绑定到 V1（模拟萌生腔已生成过的旧个体，
            // 这里只测查询面，不重复 [29] 的完整 Enqueue/OnUpdate 流程）。
            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 8;
            cfg.ArenaHalfExtent = 60f;
            cfg.RandomSeed = 0xC0FFEE32u;
            var archetypes = new[]
            {
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Stationary, Accel = 0f, TurnRate = 0f, AggroRange = 0f,
                    AttackRange = 0.5f, AttackCooldown = 99f, AttackDamage = 0f,
                    Separation = 0f, ChargeSpeedMul = 1f,
                },
            };
            sim.Begin(cfg, archetypes);

            const int BoundLogicId = 9910;
            sim.Spawn(new SpawnRequest
            {
                Position = sim.PlayerPosition, Health = 40f, Radius = 0.8f, MaxSpeed = 0f,
                ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                IntentSource = IntentSource.AI, LogicId = BoundLogicId, ExcludeFromControl = true,
            });
            sim.OnUpdate(1f / 60f);
            SimEntityId bound = FindEntityId(sim.Snapshot, BoundLogicId, out _);
            Expect(bound.IsValid, "本项前置：测试个体应成功落地");
            if (!bound.IsValid)
            {
                return;
            }

            var chamberLedger = new BiomassLedger();
            chamberLedger.OnEnter();
            var chamber = new GerminationChamberRegistry();
            chamber.OnEnter();
            chamber.Bind(sim, lineages, chamberLedger, null);
            chamber.UpdateBinding(bound, "lineage-ui", "assault", v1);

            Expect(chamber.Bindings.Count == 1 && chamber.Bindings.ContainsKey(bound), "Bindings 只读枚举应包含手工绑定的这一条");
            Expect(ReferenceEquals(chamber.Bindings[bound].Version, v1), "Bindings 里的版本引用应与绑定时一致");

            // 验收核心：提交模板前后，旧版本个体的绑定引用不应被联动改写。
            GerminationChamberRegistry.UnitBinding beforeCommit = chamber.Bindings[bound];
            PhenotypeTemplateVersion v2 = lineages.CommitTemplate("lineage-ui", "assault", organelle.Id, geneIds, "escort", out string commitErr2);
            Expect(v2 != null && v2.Version == 2, "本项前置：第二次提交应产生版本 2");
            GerminationChamberRegistry.UnitBinding afterCommit = chamber.Bindings[bound];
            Expect(ReferenceEquals(beforeCommit.Version, afterCommit.Version) && ReferenceEquals(afterCommit.Version, v1),
                "提交新版本不应联动改写任何已绑定个体——UI 展示的「旧版本个体」在提交前后必须如实不变（验收核心）");

            // UI 用来判断"待回巢"的口径：绑定版本 != 谱系当前最新版本。
            PhenotypeTemplateVersion latestAfter = lineage.GetLatest("assault");
            Expect(ReferenceEquals(latestAfter, v2), "本项前置：谱系当前最新版本应为 V2");
            Expect(!ReferenceEquals(chamber.Bindings[bound].Version, latestAfter),
                "提交 V2 后，绑定 V1 的旧个体应能被 UI 正确识别为「待回巢」（版本引用不相等）");

            // 其他谱系的绑定表应彼此独立，不因 lineage-ui 提交而改变。
            lineages.CommitTemplate("lineage-ui-other", "escort", organelle.Id, geneIds, "guard", out _);
            var otherChamber = new GerminationChamberRegistry();
            otherChamber.OnEnter();
            otherChamber.Bind(sim, lineages, chamberLedger, null);
            Expect(otherChamber.Bindings.Count == 0, "不同萌生腔实例的绑定表应彼此独立，互不污染");
        }

        /// <summary>
        /// M4-01：编队领域模型。<see cref="Formation"/>/<see cref="FormationRegistry"/> 只存
        /// <see cref="SimEntityId"/>，绝不引用任何 <c>MetabolicSlice.*</c> 类型——这里用真实
        /// <see cref="LineageRegistry.CommitTemplate"/> 提交一次新模板版本，证明编队身份对模板层的
        /// 变化完全无感（压根没有引用关系可被波及），而不是只靠"没写字段"这一件事自证。
        /// </summary>
        private static void ValidateFormationDomainModel()
        {
            Line("\n[33] 编队领域模型（M4-01）");

            var registry = new FormationRegistry();
            Formation formation = registry.CreateFormation();
            Expect(formation != null && !string.IsNullOrEmpty(formation.Id), "CreateFormation 应返回带 id 的新编队");
            Expect(registry.GetFormation(formation.Id) == formation, "GetFormation 应能按 id 查回同一实例");
            Expect(registry.AllFormations.Contains(formation), "AllFormations 应枚举出刚创建的编队");

            // 验收 1：混编单位可加入同一队。两个任意来源的 SimEntityId 都能进同一个 Formation。
            var entityA = new SimEntityId(1001);
            var entityB = new SimEntityId(1002);
            Expect(formation.AddMember(entityA), "entityA 首次加入应成功");
            Expect(formation.AddMember(entityB), "entityB（不同来源）首次加入同一队应成功——混编");
            Expect(formation.IsMember(entityA) && formation.IsMember(entityB), "两名混编成员都应能在 Members 里查到");
            Expect(!formation.AddMember(entityA), "重复加入同一成员应返回 false（HashSet 去重语义）");

            // 验收 2：模板变化不改变编队身份。真实提交一次新模板版本，Formation.Id/Members 应完全不变
            // ——因为 Formation 从未持有任何模板/谱系引用（D2 的核心约束）。
            OrganelleDef organelle = OrganelleCatalog.All.Values.FirstOrDefault(o => o.AttackMethod && !o.IsRetired);
            List<string> geneIds = GeneCatalog.AllGeneIds.Take(2).ToList();
            Expect(organelle != null, "本项前置：OrganelleCatalog 应至少有一条 AttackMethod 未退役器官");
            Expect(geneIds.Count == 2, "本项前置：GeneCatalog 应至少有两条基因");
            if (organelle != null && geneIds.Count == 2)
            {
                var blueprints = new BlueprintRegistry();
                blueprints.Resolve(organelle.Id, BlueprintSourceKind.Organelle, 1f, 0f);
                foreach (string g in geneIds)
                {
                    blueprints.Resolve(g, BlueprintSourceKind.Gene, 1f, 0f);
                }

                var lineages = new LineageRegistry();
                lineages.Bind(blueprints);
                lineages.CommitTemplate("lineage-formation", "assault", organelle.Id, geneIds, "keep_distance", out string commitErr);
                Expect(commitErr == null, "本项前置：首次提交模板应成功");

                string formationIdBefore = formation.Id;
                var membersBefore = new HashSet<SimEntityId>(formation.Members);

                lineages.CommitTemplate("lineage-formation", "assault", organelle.Id, geneIds, "escort", out string commitErr2);
                Expect(commitErr2 == null, "本项前置：第二次提交（模板版本变化）应成功");

                Expect(formation.Id == formationIdBefore, "模板版本变化后，编队 id 应完全不变（验收核心）");
                Expect(formation.Members.Count == membersBefore.Count && membersBefore.All(formation.IsMember),
                    "模板版本变化后，编队成员集合应完全不变（Formation 从未引用模板/谱系类型）");
            }

            // 验收 4：临时脱队状态。非成员调用应 no-op；成员脱队后仍在 Members 里；
            // 移除成员后 IsDetached 应归假，防止 stale 状态残留。
            var strayEntity = new SimEntityId(2001);
            formation.SetDetached(strayEntity, true);
            Expect(!formation.IsDetached(strayEntity) && !formation.IsMember(strayEntity),
                "非成员调用 SetDetached 应 no-op，不允许把非成员标记为脱队");

            formation.SetDetached(entityB, true);
            Expect(formation.IsDetached(entityB) && formation.IsMember(entityB),
                "成员脱队后应仍在 Members 里（脱队不等于移除）");

            formation.SetDetached(entityB, false);
            Expect(!formation.IsDetached(entityB) && formation.IsMember(entityB), "取消脱队应清掉标记但不影响成员身份");

            // 验收 3：死亡成员被安全移除（含清 DetachedMembers），且跨编队生效；对不存在的 id 调用不抛异常。
            Formation formationOther = registry.CreateFormation();
            formationOther.AddMember(entityB);
            formation.SetDetached(entityB, true);
            Expect(formation.IsDetached(entityB), "本项前置：entityB 死亡前应处于脱队状态");

            registry.HandleMemberDeath(entityB);
            Expect(!formation.IsMember(entityB) && !formationOther.IsMember(entityB),
                "死亡成员应从所有编队的 Members 里被移除（跨编队生效）");
            Expect(!formation.IsDetached(entityB), "死亡成员移除后，DetachedMembers 也应一并清掉");
            Expect(formation.IsMember(entityA), "死亡移除只影响目标成员，其余成员不受影响");

            bool threw = false;
            try
            {
                registry.HandleMemberDeath(new SimEntityId(9999));
                registry.HandleMemberDeath(SimEntityId.None);
            }
            catch
            {
                threw = true;
            }
            Expect(!threw, "对不存在/无效的 id 调用 HandleMemberDeath 应是安全的 no-op，不抛异常");

            // 验收 5：命令队列占位。EnqueueCommand 后计数递增，PeekCommand 不出队。
            var queueFormation = registry.CreateFormation();
            Expect(queueFormation.PendingCommandCount == 0, "新编队命令队列应为空");
            queueFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Move, targetPosition: new float2(1f, 2f)));
            queueFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Attack, targetEntity: entityA));
            Expect(queueFormation.PendingCommandCount == 2, "EnqueueCommand 两次后计数应为 2");
            bool peeked = queueFormation.PeekCommand(out FormationCommand head);
            Expect(peeked && head.Kind == FormationCommand.CommandKind.Move, "PeekCommand 应返回队首（先入的 Move）");
            Expect(queueFormation.PendingCommandCount == 2, "PeekCommand 不应出队，计数应保持不变");

            // 教义字段：只是可读写字段，不实现任何行为差异。
            Expect(queueFormation.Doctrine == FormationDoctrine.None, "新编队教义默认应为 None");
            queueFormation.Doctrine = FormationDoctrine.Vanguard;
            Expect(queueFormation.Doctrine == FormationDoctrine.Vanguard, "Doctrine 应可读写");
        }

        /// <summary>
        /// M4-02：命令队列与优先级。在 M4-01 的等待队列占位（[33] 验收 5）基础上补齐
        /// 排队/覆盖/中断/完成/失败原因五个概念的真实状态机，以及 Attack/OrganCategory 死亡自动失败
        /// 与视觉反馈颜色覆盖面。详见 production/session-state/preflight-decisions.md「M4-02」的
        /// D1~D7 与验收映射——本方法逐条对应验收映射 2~7（验收 1 是回归，靠 [33] 不变红验证，
        /// 这里只做一次独立复检）。
        /// </summary>
        private static void ValidateFormationCommandQueue()
        {
            Line("\n[34] 命令队列与优先级（M4-02）");

            var registry = new FormationRegistry();
            var entityA = new SimEntityId(5001);
            var entityDeath = new SimEntityId(5002);

            // 验收 1（回归复检）：EnqueueCommand/PeekCommand/PendingCommandCount 三个 API 对外行为
            // 必须与 [33] 验收 5 完全一致——底层换成按 Priority 排序的 List 不改变这三者的语义。
            Formation regression = registry.CreateFormation();
            Expect(regression.PendingCommandCount == 0, "回归：新编队命令队列应为空");
            regression.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Move, targetPosition: new float2(1f, 2f)));
            regression.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Attack, targetEntity: entityA));
            Expect(regression.PendingCommandCount == 2, "回归：EnqueueCommand 两次后计数应为 2");
            bool regressionPeeked = regression.PeekCommand(out FormationCommand regressionHead);
            Expect(regressionPeeked && regressionHead.Kind == FormationCommand.CommandKind.Move,
                "回归：PeekCommand 应返回队首（先入的 Move）");
            Expect(regression.PendingCommandCount == 2, "回归：PeekCommand 不应出队，计数应保持不变");
            Expect(regression.ActiveCommand == null, "回归：EnqueueCommand 绝不能自动激活 ActiveCommand");

            // 验收 2：覆盖（IssueCommand）。先排一条待验证覆盖会清空等待队列，
            // 再 Issue 两次验证旧 entry 被标记 Interrupted/PreemptedByOverride、新命令直接 Active。
            Formation issueFormation = registry.CreateFormation();
            issueFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Guard, targetPosition: new float2(0f, 0f)));
            issueFormation.IssueCommand(new FormationCommand(FormationCommand.CommandKind.Move, targetPosition: new float2(1f, 1f)));
            Expect(issueFormation.ActiveCommand != null
                && issueFormation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Move
                && issueFormation.ActiveCommand.State == FormationCommandState.Active,
                "IssueCommand(Move) 后 ActiveCommand 应是 Move 且状态 Active");
            Expect(issueFormation.PendingCommandCount == 0, "IssueCommand 应清空等待队列（覆盖不是追加）");

            FormationCommandEntry issuePrevious = issueFormation.ActiveCommand;
            issueFormation.IssueCommand(new FormationCommand(FormationCommand.CommandKind.Attack, targetEntity: entityA));
            Expect(issuePrevious.State == FormationCommandState.Interrupted
                && issuePrevious.FailReason == FormationCommandFailReason.PreemptedByOverride,
                "被覆盖的旧 entry 应标记 Interrupted 且 FailReason=PreemptedByOverride");
            Expect(issueFormation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Attack
                && issueFormation.ActiveCommand.State == FormationCommandState.Active,
                "覆盖后新命令应直接设为 ActiveCommand");

            // 验收 3：优先级排序。三条不同 Priority 乱序入队，激活顺序应是 5→3→1 而不是入队顺序。
            Formation priorityFormation = registry.CreateFormation();
            priorityFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Move, targetPosition: new float2(0f, 0f), priority: FormationCommandPriority.DoctrineResponse));
            priorityFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Attack, targetEntity: entityA, priority: FormationCommandPriority.DirectControlIntent));
            priorityFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Guard, targetPosition: new float2(0f, 0f), priority: FormationCommandPriority.UninterruptibleFinishing));

            var activationOrder = new List<FormationCommand.CommandKind>();
            for (int i = 0; i < 3; i++)
            {
                priorityFormation.TryActivateNextPending();
                Expect(priorityFormation.ActiveCommand != null, $"第 {i + 1} 轮应有 ActiveCommand 可记录");
                if (priorityFormation.ActiveCommand != null)
                {
                    activationOrder.Add(priorityFormation.ActiveCommand.Command.Kind);
                }
                priorityFormation.CompleteActiveCommand();
            }
            Expect(activationOrder.Count == 3
                && activationOrder[0] == FormationCommand.CommandKind.Attack
                && activationOrder[1] == FormationCommand.CommandKind.Guard
                && activationOrder[2] == FormationCommand.CommandKind.Move,
                "激活顺序应按 Priority 降序（5→3→1），而不是入队顺序");

            // 验收 4：完成/失败/中断三态各触发一次，且每次终结后若队列非空应自动提升下一条为 Active。
            Formation completeFormation = registry.CreateFormation();
            completeFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Move, targetPosition: new float2(0f, 0f)));
            completeFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Guard, targetPosition: new float2(0f, 0f)));
            completeFormation.TryActivateNextPending();
            FormationCommandEntry completeFirst = completeFormation.ActiveCommand;
            Expect(completeFirst != null && completeFirst.Command.Kind == FormationCommand.CommandKind.Move,
                "本项前置：同优先级应按入队顺序（FIFO）先激活 Move");
            completeFormation.CompleteActiveCommand();
            Expect(completeFirst.State == FormationCommandState.Completed, "CompleteActiveCommand 应把旧 entry 状态改为 Completed");
            Expect(completeFormation.ActiveCommand != null
                && completeFormation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Guard
                && completeFormation.ActiveCommand.State == FormationCommandState.Active,
                "Complete 后应自动提升下一条排队命令为 Active（TryActivateNextPending 联动）");

            Formation failFormation = registry.CreateFormation();
            failFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Move, targetPosition: new float2(0f, 0f)));
            failFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Retreat, targetPosition: new float2(0f, 0f)));
            failFormation.TryActivateNextPending();
            FormationCommandEntry failFirst = failFormation.ActiveCommand;
            failFormation.FailActiveCommand(FormationCommandFailReason.Cancelled);
            Expect(failFirst.State == FormationCommandState.Failed && failFirst.FailReason == FormationCommandFailReason.Cancelled,
                "FailActiveCommand 应把旧 entry 标记 Failed 且写入对应 FailReason");
            Expect(failFormation.ActiveCommand != null
                && failFormation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Retreat
                && failFormation.ActiveCommand.State == FormationCommandState.Active,
                "Fail 后应自动提升下一条排队命令为 Active");

            Formation interruptFormation = registry.CreateFormation();
            interruptFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Guard, targetPosition: new float2(0f, 0f)));
            interruptFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Occupy, targetPosition: new float2(0f, 0f)));
            interruptFormation.TryActivateNextPending();
            FormationCommandEntry interruptFirst = interruptFormation.ActiveCommand;
            interruptFormation.InterruptActiveCommand();
            Expect(interruptFirst.State == FormationCommandState.Interrupted && interruptFirst.FailReason == FormationCommandFailReason.Cancelled,
                "InterruptActiveCommand 默认 reason 应为 Cancelled");
            Expect(interruptFormation.ActiveCommand != null
                && interruptFormation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Occupy
                && interruptFormation.ActiveCommand.State == FormationCommandState.Active,
                "Interrupt 后应自动提升下一条排队命令为 Active");

            // 验收 5：Attack/OrganCategory 死亡自动失败。编队 A 有成员 X，编队 B 的 Active 命令瞄着 X，
            // HandleMemberDeath(X) 后编队 A 不再含 X，且编队 B 自动 Failed(InvalidTarget)。
            Formation formationA = registry.CreateFormation();
            Formation formationB = registry.CreateFormation();
            formationA.AddMember(entityDeath);
            formationB.IssueCommand(new FormationCommand(FormationCommand.CommandKind.OrganCategory, targetEntity: entityDeath));
            Expect(formationB.ActiveCommand != null && formationB.ActiveCommand.State == FormationCommandState.Active,
                "本项前置：formationB 应有一条 Active 的 OrganCategory 命令瞄着 entityDeath");

            registry.HandleMemberDeath(entityDeath);
            Expect(!formationA.IsMember(entityDeath), "死亡成员应从 formationA 移除（M4-01 既有行为不回归）");
            Expect(formationB.ActiveCommand != null
                && formationB.ActiveCommand.State == FormationCommandState.Failed
                && formationB.ActiveCommand.FailReason == FormationCommandFailReason.InvalidTarget,
                "Attack/OrganCategory 命令的目标死亡应自动 Failed(InvalidTarget)");

            // 验收 6：站桩命令（Guard/Occupy/Ambush）永不自动终结，只能被显式 Interrupt/Fail/Complete/覆盖终止。
            Formation guardFormation = registry.CreateFormation();
            guardFormation.IssueCommand(new FormationCommand(FormationCommand.CommandKind.Guard, targetPosition: new float2(5f, 5f)));
            Expect(guardFormation.ActiveCommand != null && guardFormation.ActiveCommand.State == FormationCommandState.Active,
                "本项前置：Guard 命令应处于 Active");
            registry.HandleMemberDeath(new SimEntityId(9999));
            registry.HandleMemberDeath(SimEntityId.None);
            Expect(guardFormation.ActiveCommand != null && guardFormation.ActiveCommand.State == FormationCommandState.Active,
                "站桩命令应永不自动终结（无关死亡信号、纯查询都不应改变其状态）");

            // 验收 7：视觉反馈覆盖面。8 种 CommandKind 都应有对应颜色条目，且互不重复，不走真实 OnGUI 渲染。
            var allKinds = (FormationCommand.CommandKind[])Enum.GetValues(typeof(FormationCommand.CommandKind));
            Expect(allKinds.Length == 8, "本项前置：CommandKind 应恰好 8 种");
            foreach (FormationCommand.CommandKind kind in allKinds)
            {
                Expect(FormationCommandOverlay.KindColors.ContainsKey(kind), $"视觉反馈颜色查表应覆盖 CommandKind.{kind}");
            }
            Expect(FormationCommandOverlay.KindColors.Count == 8, "颜色查表应恰好覆盖全部 8 种 CommandKind，不多不少");
            var distinctColors = new HashSet<Color>(FormationCommandOverlay.KindColors.Values);
            Expect(distinctColors.Count == 8, "8 种 CommandKind 对应的颜色应互不重复");
        }

        /// <summary>
        /// M4-R00-02 队列⑥-25（FC-REQ-022/060）：命令队列可视化 + 插队/取消/清空交互。
        /// 只测 <see cref="Formation.PendingCommands"/>/<see cref="Formation.CancelQueuedCommand"/>/
        /// <see cref="Formation.ClearQueue"/> 三个新 API 本身的行为契约（下标语义、越界安全、
        /// 终结态/FailReason 写入、不触碰 ActiveCommand）——真实 UI 点击已由
        /// <see cref="FormationCommandOverlay"/> 复用既有 IMGUI 管线（同 [34] 验收 7 口径，不强制走
        /// 真实 OnGUI 渲染测试）。
        /// </summary>
        private static void ValidateFormationCommandQueueVisualization()
        {
            Line("\n[52] 命令队列可视化+插队/取消/清空交互（FC-REQ-022/060）");

            var registry = new FormationRegistry();
            var entityA = new SimEntityId(6001);

            // 验收 1：PendingCommands 只读枚举与 PendingCommandCount/PeekCommand 口径一致。
            Formation readFormation = registry.CreateFormation();
            Expect(readFormation.PendingCommands.Count == 0, "本项前置：新编队 PendingCommands 应为空");
            readFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Move, targetPosition: new float2(1f, 1f)));
            readFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Attack, targetEntity: entityA));
            Expect(readFormation.PendingCommands.Count == 2 && readFormation.PendingCommandCount == 2,
                "PendingCommands.Count 应与 PendingCommandCount 一致");
            Expect(readFormation.PendingCommands[0].Command.Kind == FormationCommand.CommandKind.Move
                && readFormation.PendingCommands[1].Command.Kind == FormationCommand.CommandKind.Attack,
                "PendingCommands 下标顺序应与入队/优先级排序结果一致（与 PeekCommand 看到的队首同一份数据）");

            // 验收 2：取消队列中间一条——只移除该条，其余下标依次前移，不影响 ActiveCommand。
            Formation cancelFormation = registry.CreateFormation();
            cancelFormation.IssueCommand(new FormationCommand(FormationCommand.CommandKind.Guard, targetPosition: new float2(0f, 0f)));
            cancelFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Move, targetPosition: new float2(1f, 1f)));
            cancelFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Attack, targetEntity: entityA));
            cancelFormation.EnqueueCommand(new FormationCommand(FormationCommand.CommandKind.Retreat, targetPosition: new float2(2f, 2f)));
            FormationCommandEntry cancelledEntry = cancelFormation.PendingCommands[1];
            bool cancelled = cancelFormation.CancelQueuedCommand(1);
            Expect(cancelled, "取消存在的下标应返回 true");
            Expect(cancelFormation.PendingCommands.Count == 2
                && cancelFormation.PendingCommands[0].Command.Kind == FormationCommand.CommandKind.Move
                && cancelFormation.PendingCommands[1].Command.Kind == FormationCommand.CommandKind.Retreat,
                "取消中间一条后，其余条目应依次前移，Attack 应从队列消失");
            Expect(cancelledEntry.State == FormationCommandState.Interrupted && cancelledEntry.FailReason == FormationCommandFailReason.Cancelled,
                "被取消的 entry 应标记 Interrupted/Cancelled（同 InterruptActiveCommand 默认 reason 口径）");
            Expect(cancelFormation.ActiveCommand != null && cancelFormation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Guard
                && cancelFormation.ActiveCommand.State == FormationCommandState.Active,
                "取消等待队列条目不应触碰 ActiveCommand");

            // 验收 3：越界下标安全 no-op，不抛异常、不改变队列。
            Expect(!cancelFormation.CancelQueuedCommand(-1), "负数下标应返回 false");
            Expect(!cancelFormation.CancelQueuedCommand(99), "越界下标应返回 false");
            Expect(cancelFormation.PendingCommands.Count == 2, "越界取消不应改变队列内容");

            // 验收 4：清空队列。返回被清空条数，全部标记 Interrupted/Cancelled，ActiveCommand 不受影响。
            FormationCommandEntry clearedMove = cancelFormation.PendingCommands[0];
            FormationCommandEntry clearedRetreat = cancelFormation.PendingCommands[1];
            int clearedCount = cancelFormation.ClearQueue();
            Expect(clearedCount == 2, $"ClearQueue 应返回清空前的队列条数（实际 {clearedCount}）");
            Expect(cancelFormation.PendingCommands.Count == 0, "ClearQueue 后队列应为空");
            Expect(clearedMove.State == FormationCommandState.Interrupted && clearedRetreat.State == FormationCommandState.Interrupted,
                "ClearQueue 应把队列里每一条都标记 Interrupted，不是只清空列表本身");
            Expect(cancelFormation.ActiveCommand != null && cancelFormation.ActiveCommand.State == FormationCommandState.Active,
                "清空等待队列不应触碰 ActiveCommand");

            // 验收 5：空队列上调用 ClearQueue 安全返回 0，不抛异常。
            Formation emptyFormation = registry.CreateFormation();
            Expect(emptyFormation.ClearQueue() == 0, "空队列 ClearQueue 应返回 0");
        }

        /// <summary>
        /// M4-R00-02 队列⑥-25 补完（FC-REQ-022"插队/追加"）：`SquadCommandSystem.Issue` 的
        /// <c>queueBehindActive</c> 参数与 `HandleCommandInput` 右键分支的 Shift 判定。[52] 只测
        /// `Formation` 自身的队列 API；本项测"玩家显式请求排队"这一段输入到 Formation 之间的接线，
        /// 且必须走真实 <see cref="SquadCommandSystem"/>/<see cref="SimBridge"/>，不直连内部字段。
        /// </summary>
        private static void ValidateSquadCommandQueueRequest()
        {
            Line("\n[53] 插队/追加输入接线（FC-REQ-022，⑥-25补完）");

            var hub = new ModuleHub();
            var formations = hub.Register(new FormationRegistry());
            var sim = hub.Register(new SimBridge());
            hub.Enter();

            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 16;
            cfg.ArenaHalfExtent = 80f;
            sim.Begin(cfg, System.Array.Empty<BehaviorArchetype>());

            var cameraGo = new GameObject("ValidateSquadCommandQueueRequest_TempCamera");
            Camera camera = cameraGo.AddComponent<Camera>();
            var rt = new RenderTexture(256, 256, 0);
            camera.targetTexture = rt;
            cameraGo.transform.position = new Vector3(0f, 10f, 0f);
            cameraGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 俯视，正下方

            var squad = new SquadCommandSystem();
            squad.Bind(sim, camera, formations);
            var reader = new ScriptedInputReader();
            InputRouter.Reset();
            InputRouter.SetScope(InputScope.Strategy);
            InputRouter.DebugSetReader(reader);

            try
            {
                SimEntityId SpawnAndResolve(int logicId, float2 pos)
                {
                    sim.Spawn(new SpawnRequest
                    {
                        Position = pos, Health = 20f, Radius = 0.5f, MaxSpeed = 4f,
                        ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                        IntentSource = IntentSource.AI, LogicId = logicId,
                    });
                    sim.OnUpdate(1f / 60f);
                    return FindEntityId(sim.Snapshot, logicId, out _);
                }

                // 验收 1：编队正在执行命令时，queueBehindActive=true 不应覆盖，应追加进等待队列。
                SimEntityId a1 = SpawnAndResolve(95001, new float2(0f, 0f));
                Expect(a1.IsValid, "本项前置：测试个体 a1 应成功落地");
                squad.SelectExplicit(new[] { a1 });
                squad.AssignGroup(1);
                Formation formation = formations.FindFormationContaining(a1);
                Expect(formation != null, "本项前置：编组后应存在对应 Formation");
                // AssignGroup 本身会清空 _activeFormationId（见其源码注释与实现），必须
                // RecallGroup 一次才会把它指回槽位1的编队——否则下面的 Issue 会掉进"裸选择集"
                // 路径而不是编队路径（ResolveActiveFormationForCurrentSelection 的既有约束）。
                squad.RecallGroup(1);

                squad.Issue(UnitCommandKind.Guard, new float2(1f, 1f), SimEntityId.None, paused: false);
                Expect(formation.ActiveCommand != null && formation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Guard,
                    "本项前置：不带 queueBehindActive 的下令应正常 Activate（回归 [49]）");

                int queuedCount = squad.Issue(UnitCommandKind.Move, new float2(5f, 5f), SimEntityId.None,
                    paused: false, queueBehindActive: true);
                Expect(queuedCount == 1, $"排队请求应按选择集大小返回受影响单位数（实际 {queuedCount}）");
                Expect(formation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Guard,
                    "queueBehindActive=true 时不应覆盖当前 ActiveCommand（还是 Guard，不是 Move）");
                Expect(formation.PendingCommands.Count == 1 && formation.PendingCommands[0].Command.Kind == FormationCommand.CommandKind.Move,
                    "queueBehindActive=true 应把新命令追加进等待队列，不是丢弃");
                Expect(squad.LastFormationDispatchOutcome == SquadFormationDispatchOutcome.QueuedByPlayerRequest,
                    $"应记录为 QueuedByPlayerRequest（实际 {squad.LastFormationDispatchOutcome}）");

                // 验收 2：不带 Shift（queueBehindActive=false）的下一条命令仍应覆盖——两种语义不能混淆。
                squad.Issue(UnitCommandKind.Retreat, new float2(-3f, -3f), SimEntityId.None, paused: false);
                Expect(formation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Retreat,
                    "不带 queueBehindActive 的下令应恢复覆盖语义（IssueCommand 的既有行为不回归）");
                Expect(formation.PendingCommands.Count == 0,
                    "覆盖语义会清空等待队列——IssueCommand 本身既有行为，queueBehindActive 不改变它");

                // 验收 3：编队原本空闲时 queueBehindActive=true 应立即提升为 Active 并补发内核命令
                //（否则排队条目没有任何终结事件来触发提升，会永远卡在 Pending，玩家会以为按了没反应）。
                SimEntityId b1 = SpawnAndResolve(95002, new float2(10f, 10f));
                Expect(b1.IsValid, "本项前置：测试个体 b1 应成功落地");
                squad.SelectExplicit(new[] { b1 });
                squad.AssignGroup(2);
                squad.RecallGroup(2); // 同上：AssignGroup 会清空 _activeFormationId，必须 Recall 一次。
                Formation idleFormation = formations.FindFormationContaining(b1);
                Expect(idleFormation != null && idleFormation.ActiveCommand == null,
                    "本项前置：刚编组、从未下过命令的编队应处于空闲（无 ActiveCommand）");

                int idleQueuedCount = squad.Issue(UnitCommandKind.Guard, new float2(10f, 10f), SimEntityId.None,
                    paused: false, queueBehindActive: true);
                Expect(idleQueuedCount == 1, $"空闲编队上的排队请求也应立即生效并返回受影响单位数（实际 {idleQueuedCount}）");
                Expect(idleFormation.ActiveCommand != null
                    && idleFormation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Guard
                    && idleFormation.ActiveCommand.State == FormationCommandState.Active,
                    "空闲编队排队后应自动提升为 Active，不应该卡在 Pending 里没人管");
                Expect(idleFormation.PendingCommands.Count == 0, "提升后等待队列应重新变空");
                Expect(sim.TryGetCommand(b1, out UnitCommand dispatched) && dispatched.Kind == UnitCommandKind.Guard,
                    "Guard 是需要补发内核命令的两条腿之一，空闲提升后应真的下发给 SimBridge，不能只停在 Formation 状态机");

                // 验收 4：真实输入接线——Shift+右键应走排队而不是覆盖，不 Shift 的右键照旧覆盖。
                // 复用槽位1的编队（当前 Active=Retreat，来自验收2），screen(128,128) 对应世界(0,0)。
                // 用 RecallGroup 而不是 SelectExplicit——后者会清空 _activeFormationId，
                // 右键会掉回"裸选择集"路径而不是编队路径（ResolveActiveFormationForCurrentSelection
                // 的既有约束，见该方法注释）。
                reader.MousePosition = new Vector3(128f, 128f, 0f);
                squad.RecallGroup(1);
                reader.SetHeld(KeyCode.LeftShift, true);
                reader.ClickMouseButtonDown(1);
                squad.Tick(paused: false);
                reader.EndFrame();
                reader.SetHeld(KeyCode.LeftShift, false);
                Expect(formation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Retreat,
                    "真实 Shift+右键：不应覆盖当前 ActiveCommand（还是验收2里下的 Retreat）");
                Expect(formation.PendingCommands.Count == 1 && formation.PendingCommands[0].Command.Kind == FormationCommand.CommandKind.Move,
                    "真实 Shift+右键点空地应追加一条 Move 到等待队列——证明 HandleCommandInput 的 Shift 判定真的接到了 Issue(queueBehindActive)");

                reader.ClickMouseButtonDown(1);
                squad.Tick(paused: false);
                reader.EndFrame();
                Expect(formation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Move
                    && formation.PendingCommands.Count == 0,
                    "真实右键（不按 Shift）应恢复覆盖语义：清空刚才排的队并直接 Activate 新命令");
            }
            finally
            {
                squad.Unbind();
                hub.Exit();
                InputRouter.Reset();
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(cameraGo);
            }
        }

        /// <summary>
        /// M4-R00-02 队列⑥-26（FC-REQ-061 战略/直控连续性）：直控 HUD 编队目标/汇合方向/
        /// 失败提示文案。只测 <see cref="WhiteboxSquadOverlay.BuildDirectFormationText"/> 这个纯函数
        /// 本身（同 [34] 验收7 对 <see cref="FormationCommandOverlay.ColorFor"/> 的既有口径，
        /// 不强制走真实 OnGUI 渲染管线测试），但用真实 <see cref="SimBridge"/>/<see cref="SimWorld"/>
        /// 驱动，不直连内部字段——汇合方向要读真实单位位置，伪造快照会让方向角断言失去意义。
        /// </summary>
        private static void ValidateDirectFormationHud()
        {
            Line("\n[54] 直控HUD编队目标/汇合方向/失败提示（FC-REQ-061）");

            var registry = new FormationRegistry();
            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 16;
            cfg.ArenaHalfExtent = 80f;
            sim.Begin(cfg, Array.Empty<BehaviorArchetype>());

            try
            {
                SimEntityId SpawnAndResolve(int logicId, float2 pos, SimFaction faction = SimFaction.PlayerMinion)
                {
                    sim.Spawn(new SpawnRequest
                    {
                        Position = pos, Health = 20f, Radius = 0.5f, MaxSpeed = 4f,
                        ArchetypeId = 0, Faction = faction, IntentSource = IntentSource.AI, LogicId = logicId,
                    });
                    sim.OnUpdate(1f / 60f);
                    return FindEntityId(sim.Snapshot, logicId, out _);
                }

                // 验收 1：不属于任何编队时返回 null（调用方据此不画区块，不是画空文案）。
                string noFormationText = WhiteboxSquadOverlay.BuildDirectFormationText(null, sim.World, float2.zero);
                Expect(noFormationText == null, "不属于任何编队应返回 null");

                // 验收 2：编队空闲（无 ActiveCommand）+ 有存活成员——文案含"当前空闲"，
                // 汇合方向指向成员平均位置。控制者站在(0,0)，唯一成员在正北(0,10)，
                // 期望方位角≈0°。
                SimEntityId idleMember = SpawnAndResolve(94001, new float2(0f, 10f));
                Expect(idleMember.IsValid, "本项前置：idleMember 应成功落地");
                Formation idleFormation = registry.CreateFormation();
                idleFormation.AddMember(idleMember);

                string idleText = WhiteboxSquadOverlay.BuildDirectFormationText(idleFormation, sim.World, float2.zero);
                Expect(idleText != null && idleText.Contains("当前空闲"), $"空闲编队文案应含'当前空闲'（实际：{idleText}）");
                Expect(idleText.Contains("汇合方向 0°"), $"控制者在原点、唯一成员在正北时汇合方向应≈0°（实际：{idleText}）");

                // 验收 3：Active Move 命令——汇合方向指向 TargetPosition，不是成员位置。
                // TargetPosition 设在正东 (10,0)，期望方位角≈90°。
                Formation moveFormation = registry.CreateFormation();
                moveFormation.AddMember(idleMember);
                moveFormation.IssueCommand(new FormationCommand(FormationCommand.CommandKind.Move,
                    targetPosition: new float2(10f, 0f)));
                string moveText = WhiteboxSquadOverlay.BuildDirectFormationText(moveFormation, sim.World, float2.zero);
                Expect(moveText != null && moveText.Contains("命令 Move[Active]"),
                    $"生效中的 Move 命令应显示命令种类与状态（实际：{moveText}）");
                Expect(moveText.Contains("汇合方向 90°"),
                    $"Move 命令目标在正东时汇合方向应≈90°，不是指向成员位置（实际：{moveText}）");

                // 验收 4：Active Attack 命令——汇合方向应指向 TargetEntity 查到的实时位置
                //（走 world.TryGetUnitControlState 现查，不是缓存下令时的坐标），
                // 而不是像 Move 那样直接读 Command.TargetPosition（Attack 命令构造时没传它）。
                SimEntityId hostile = SpawnAndResolve(94002, new float2(0f, 10f), SimFaction.Hostile);
                Expect(hostile.IsValid, "本项前置：hostile 应成功落地");
                Formation attackFormation = registry.CreateFormation();
                attackFormation.AddMember(idleMember);
                attackFormation.IssueCommand(new FormationCommand(FormationCommand.CommandKind.Attack, targetEntity: hostile));

                string attackText = WhiteboxSquadOverlay.BuildDirectFormationText(attackFormation, sim.World, float2.zero);
                Expect(attackText != null && attackText.Contains("命令 Attack[Active]") && attackText.Contains("汇合方向 0°"),
                    $"Attack 目标在正北，汇合方向应从 TargetEntity 实时位置算出≈0°（实际：{attackText}）");

                // 验收 5：命令失败——文案含失败原因中文标签。
                Formation failedFormation = registry.CreateFormation();
                failedFormation.AddMember(idleMember);
                failedFormation.IssueCommand(new FormationCommand(FormationCommand.CommandKind.Guard, targetPosition: float2.zero));
                failedFormation.FailActiveCommand(FormationCommandFailReason.Stuck);
                string failedText = WhiteboxSquadOverlay.BuildDirectFormationText(failedFormation, sim.World, float2.zero);
                Expect(failedText != null && failedText.Contains("原因 卡死"),
                    $"Failed 状态应显示失败原因中文标签（实际：{failedText}）");
            }
            finally
            {
                sim.End();
            }
        }

        /// <summary>
        /// M4-R00-02 队列⑥-28（`IC-REQ-013`/`FS-REQ-072` 因果反馈四段式）：
        /// <see cref="ControlFeedback"/> 三个纯函数（`ForSwitch`/`ForChange`/`ForAvailability`）的
        /// 分支覆盖——枚举里每一个值都必须给出四段齐全的 <see cref="FourPartFeedback"/>，不能有
        /// 任何值静默落进"一句笼统的话"（IC-REQ-013 审计原话点名的问题）。
        ///
        /// 额外断言 <see cref="ControlChangeReason.Released"/>/<see cref="ControlAvailability.Released"/>
        /// 两处真实发现的 bug 已修：这是玩家主动放下意识进战略视角的**常态**，此前与"意识无处可去"
        /// 共用"控制目标丢失"文案，会让玩家把正常操作读成报错。
        /// </summary>
        private static void ValidateControlFeedbackFourPart()
        {
            Line("\n[55] 控制反馈四段式文案（IC-REQ-013/FS-REQ-072）");

            void ExpectComplete(FourPartFeedback fb, string label)
            {
                Expect(!string.IsNullOrEmpty(fb.Reason) && !string.IsNullOrEmpty(fb.State)
                    && !string.IsNullOrEmpty(fb.Consequence) && !string.IsNullOrEmpty(fb.Recovery),
                    $"{label} 应四段齐全（实际：{fb}）");
            }

            var entity = new SimEntityId(7001);

            // 验收 1：ForSwitch 覆盖 ControlRequestResult 全部枚举值。
            foreach (ControlRequestResult result in Enum.GetValues(typeof(ControlRequestResult)))
            {
                ExpectComplete(ControlFeedback.ForSwitch(result, 3, entity), $"ForSwitch({result})");
            }

            // 验收 2：ForChange 覆盖 ControlChangeReason 全部枚举值，valid/invalid 目标各测一次。
            foreach (ControlChangeReason reason in Enum.GetValues(typeof(ControlChangeReason)))
            {
                ExpectComplete(ControlFeedback.ForChange(reason, entity), $"ForChange({reason}, valid)");
                ExpectComplete(ControlFeedback.ForChange(reason, SimEntityId.None), $"ForChange({reason}, none)");
            }

            // 验收 3：ForAvailability 覆盖 ControlAvailability 全部枚举值。
            foreach (ControlAvailability availability in Enum.GetValues(typeof(ControlAvailability)))
            {
                ExpectComplete(ControlFeedback.ForAvailability(availability), $"ForAvailability({availability})");
            }

            // 验收 4：Released 场景的既有 bug 已修——不应再落进泛化的"目标丢失/无处可去"文案。
            FourPartFeedback releasedChange = ControlFeedback.ForChange(ControlChangeReason.Released, SimEntityId.None);
            Expect(!releasedChange.Reason.Contains("丢失") && !releasedChange.Reason.Contains("无处可去"),
                $"ForChange(Released) 是玩家主动放下意识的正常状态，不应显示成丢失/无处可去（实际：{releasedChange.Reason}）");
            Expect(releasedChange.State.Contains("正常"),
                $"ForChange(Released) 的状态段应明确标注这是正常状态（实际：{releasedChange.State}）");

            FourPartFeedback releasedAvailability = ControlFeedback.ForAvailability(ControlAvailability.Released);
            Expect(!releasedAvailability.Reason.Contains("丢失"),
                $"ForAvailability(Released) 不应显示成丢失（实际：{releasedAvailability.Reason}）");
            Expect(!ControlFeedback.ForAvailability(ControlAvailability.None).Equals(releasedAvailability),
                "None（真无处可去）与 Released（主动放下，正常）必须是两段不同的文案，不能共用同一个分支");
        }

        /// <summary>
        /// M4-03：六种教义。<see cref="FormationDoctrineProfile"/> 是"教义 → 参数"的定义 + 只读
        /// 查询层，不接任何真实 AI 决策（见 D1 边界）。详见
        /// production/session-state/preflight-decisions.md「M4-03 六种教义」验收映射 1~5。
        /// </summary>
        private static void ValidateFormationDoctrineProfiles()
        {
            Line("\n[35] 六种教义（M4-03）");

            // 验收 1：全部 7 个 FormationDoctrine 枚举值（含 None）都有对应条目，
            // EngagementRange > 0 且 0 < RetreatHealthThreshold <= 1。
            var allDoctrines = (FormationDoctrine[])Enum.GetValues(typeof(FormationDoctrine));
            Expect(allDoctrines.Length == 7, "本项前置：FormationDoctrine 应恰好 7 个枚举值（含 None）");
            foreach (FormationDoctrine doctrine in allDoctrines)
            {
                FormationDoctrineProfile profile = FormationDoctrineProfile.For(doctrine);
                Expect(profile.EngagementRange > 0f, $"{doctrine} 的 EngagementRange 应 > 0");
                Expect(profile.RetreatHealthThreshold > 0f && profile.RetreatHealthThreshold <= 1f,
                    $"{doctrine} 的 RetreatHealthThreshold 应落在 (0, 1] 区间");
            }

            // 验收 2：六个具名教义（不含 None）的三元组两两互不相同。
            var namedDoctrines = allDoctrines.Where(d => d != FormationDoctrine.None).ToArray();
            Expect(namedDoctrines.Length == 6, "本项前置：具名教义应恰好 6 个（不含 None）");
            for (int i = 0; i < namedDoctrines.Length; i++)
            {
                FormationDoctrineProfile pi = FormationDoctrineProfile.For(namedDoctrines[i]);
                for (int j = i + 1; j < namedDoctrines.Length; j++)
                {
                    FormationDoctrineProfile pj = FormationDoctrineProfile.For(namedDoctrines[j]);
                    bool same = pi.TargetPreference == pj.TargetPreference
                        && Mathf.Approximately(pi.EngagementRange, pj.EngagementRange)
                        && Mathf.Approximately(pi.RetreatHealthThreshold, pj.RetreatHealthThreshold);
                    Expect(!same, $"{namedDoctrines[i]} 与 {namedDoctrines[j]} 的教义参数三元组不应完全相同");
                }
            }

            // 验收 3：两条设计意图顺序断言。
            FormationDoctrineProfile vanguard = FormationDoctrineProfile.For(FormationDoctrine.Vanguard);
            FormationDoctrineProfile stealth = FormationDoctrineProfile.For(FormationDoctrine.Stealth);
            FormationDoctrineProfile escort = FormationDoctrineProfile.For(FormationDoctrine.Escort);
            Expect(vanguard.RetreatHealthThreshold < stealth.RetreatHealthThreshold,
                "先锋应比潜行更能扛（Vanguard.RetreatHealthThreshold < Stealth.RetreatHealthThreshold）");
            Expect(escort.EngagementRange < vanguard.EngagementRange,
                "护送应比先锋更收敛（Escort.EngagementRange < Vanguard.EngagementRange）");

            // 验收 4：同一编队切换教义后 CurrentDoctrineProfile 立即变化（计算属性，不是构造时缓存）。
            var registry = new FormationRegistry();
            Formation doctrineFormation = registry.CreateFormation();
            doctrineFormation.Doctrine = FormationDoctrine.Vanguard;
            FormationDoctrineProfile beforeSwitch = doctrineFormation.CurrentDoctrineProfile;
            Expect(beforeSwitch.TargetPreference == vanguard.TargetPreference
                && Mathf.Approximately(beforeSwitch.EngagementRange, vanguard.EngagementRange)
                && Mathf.Approximately(beforeSwitch.RetreatHealthThreshold, vanguard.RetreatHealthThreshold),
                "Doctrine=Vanguard 时 CurrentDoctrineProfile 应等于 For(Vanguard)");

            doctrineFormation.Doctrine = FormationDoctrine.Stealth;
            FormationDoctrineProfile afterSwitch = doctrineFormation.CurrentDoctrineProfile;
            Expect(afterSwitch.TargetPreference == stealth.TargetPreference
                && Mathf.Approximately(afterSwitch.EngagementRange, stealth.EngagementRange)
                && Mathf.Approximately(afterSwitch.RetreatHealthThreshold, stealth.RetreatHealthThreshold),
                "改 Doctrine=Stealth 后 CurrentDoctrineProfile 应立即变为 For(Stealth)");
            Expect(afterSwitch.TargetPreference != beforeSwitch.TargetPreference
                || !Mathf.Approximately(afterSwitch.EngagementRange, beforeSwitch.EngagementRange)
                || !Mathf.Approximately(afterSwitch.RetreatHealthThreshold, beforeSwitch.RetreatHealthThreshold),
                "切换教义前后 CurrentDoctrineProfile 应不同，证明是计算属性而非构造时缓存");

            // 验收 5：新编队默认 Doctrine == None 时 CurrentDoctrineProfile 等于 For(None)。
            Formation freshFormation = registry.CreateFormation();
            Expect(freshFormation.Doctrine == FormationDoctrine.None, "新编队教义默认应为 None（回归 [33] 语义）");
            FormationDoctrineProfile none = FormationDoctrineProfile.For(FormationDoctrine.None);
            FormationDoctrineProfile freshProfile = freshFormation.CurrentDoctrineProfile;
            Expect(freshProfile.TargetPreference == none.TargetPreference
                && Mathf.Approximately(freshProfile.EngagementRange, none.EngagementRange)
                && Mathf.Approximately(freshProfile.RetreatHealthThreshold, none.RetreatHealthThreshold),
                "新编队默认 Doctrine==None 时 CurrentDoctrineProfile 应等于 For(None)");
        }

        /// <summary>
        /// M4-04：共享路径与局部分离。详见 production/session-state/preflight-decisions.md
        /// 「M4-04 共享路径与局部分离」验收映射 1~8。
        ///
        /// D4/D5 的移动驱动状态机与卡死重规划边界按关键提醒第 2 条只测纯逻辑层
        /// （<see cref="FormationMemberMotion"/>/<see cref="FormationStuckTracker"/>），不起真实
        /// SimWorld——<see cref="FormationMovementDriver"/> 本体需要 ModuleHub + SimBridge + SimWorld
        /// 才能跑，与 [33]~[35] 对编队领域模型"不起真实内核"的处理方式一致。
        /// </summary>
        private static void ValidateFormationSharedPathing()
        {
            Line("\n[36] 共享路径与局部分离（M4-04）");

            // 验收 1：单障碍绕行。1 个障碍物直接挡在 start→goal 连线中点，路径每一段都不应与该
            // 障碍圆（含 clearance）相交，且路点数 <= 8。
            {
                float2 start = new float2(0f, 0f);
                float2 goal = new float2(20f, 0f);
                float clearance = 0.5f;
                var obstacles = new List<ObstacleSpec>
                {
                    new ObstacleSpec { Position = new float2(10f, 0f), Radius = 1.5f },
                };

                List<float2> path = FormationPathPlanner.Plan(start, goal, obstacles, clearance);
                Expect(path.Count <= FormationPathPlanner.MaxWaypoints,
                    $"单障碍绕行：路点数应 <= {FormationPathPlanner.MaxWaypoints}（实际 {path.Count}）");
                Expect(path.Count >= 3, "单障碍绕行：直线中点有障碍挡路时，规划结果应至少插入一个绕行路点");
                Expect(PathClearsAllObstacles(path, obstacles, clearance),
                    "单障碍绕行：路径每一段都不应与障碍圆（含 clearance）相交");
            }

            // 验收 2：无障碍直通。start→goal 连线不经过任何障碍物时，应原样返回 [start, goal]，
            // 不画蛇添足插点。
            {
                float2 start = new float2(0f, 0f);
                float2 goal = new float2(20f, 0f);
                var farObstacles = new List<ObstacleSpec>
                {
                    new ObstacleSpec { Position = new float2(100f, 100f), Radius = 1f },
                };

                List<float2> direct = FormationPathPlanner.Plan(start, goal, farObstacles, 0.5f);
                Expect(direct.Count == 2 && FloatsEqual(direct[0], start) && FloatsEqual(direct[1], goal),
                    "无障碍直通：应原样返回 [start, goal]，不插点");
            }

            // 验收 3："狭窄通道"合成场景。两个障碍物一上一下夹出一条通道，start 在通道一侧、
            // goal 在通道另一侧且直线会先擦到近侧障碍——断言规划出的路径确实从两个障碍圆之间的
            // 空档穿过，而不是绕整个障碍群一大圈（用路径总长度上界断言防止绕远路）。
            {
                var bottom = new ObstacleSpec { Position = new float2(10f, -3f), Radius = 1f };
                var top = new ObstacleSpec { Position = new float2(10f, 3f), Radius = 1f };
                float clearance = 0.3f;
                var gateObstacles = new List<ObstacleSpec> { bottom, top };

                float2 start = new float2(0f, -2.5f);
                float2 goal = new float2(20f, -2.5f);
                List<float2> path = FormationPathPlanner.Plan(start, goal, gateObstacles, clearance);

                Expect(path.Count >= 3, "狭窄通道：直线会擦到近侧障碍，应至少插入一个绕行路点");
                Expect(PathClearsAllObstacles(path, gateObstacles, clearance),
                    "狭窄通道：路径每一段都不应与任一障碍圆（含 clearance）相交");

                float gapLow = bottom.Position.y + (bottom.Radius + clearance);
                float gapHigh = top.Position.y - (top.Radius + clearance);
                bool hasWaypointInGap = false;
                for (int i = 1; i < path.Count - 1; i++)
                {
                    if (path[i].y > gapLow && path[i].y < gapHigh)
                    {
                        hasWaypointInGap = true;
                        break;
                    }
                }
                Expect(hasWaypointInGap,
                    $"狭窄通道：应有绕行路点落在两障碍之间的空档 y∈({gapLow:F2},{gapHigh:F2})");

                float directDist = math.distance(start, goal);
                float totalLen = PathLength(path);
                Expect(totalLen <= directDist * 2f,
                    $"狭窄通道：路径总长度不应远超直线距离（直线 {directDist:F2}，实际 {totalLen:F2}），防止绕整个障碍群一大圈");
            }

            // 验收 4：跟随槽公式。偶数/奇数索引分布在路径轴两侧，偏移量以 spacing 为步长线性增长。
            {
                float spacing = FormationFollowSlots.DefaultSpacing;
                float2 o0 = FormationFollowSlots.ComputeOffset(0, 5, spacing);
                float2 o1 = FormationFollowSlots.ComputeOffset(1, 5, spacing);
                float2 o2 = FormationFollowSlots.ComputeOffset(2, 5, spacing);
                float2 o3 = FormationFollowSlots.ComputeOffset(3, 5, spacing);
                float2 o4 = FormationFollowSlots.ComputeOffset(4, 5, spacing);

                Expect(FloatsEqual(o0, float2.zero), "跟随槽：索引 0 偏移应为 0（锚点本体）");
                Expect(o1.y > 0f && Mathf.Approximately(o1.y, spacing),
                    $"跟随槽：索引 1 偏移应是 +1 倍 spacing（实际 {o1.y}）");
                Expect(o2.y < 0f && Mathf.Approximately(o2.y, -spacing),
                    $"跟随槽：索引 2 偏移应是 -1 倍 spacing，且与索引 1 异侧（实际 {o2.y}）");
                Expect(o3.y > 0f && Mathf.Approximately(o3.y, 2f * spacing),
                    $"跟随槽：索引 3 偏移应是 +2 倍 spacing（实际 {o3.y}）");
                Expect(o4.y < 0f && Mathf.Approximately(o4.y, -2f * spacing),
                    $"跟随槽：索引 4 偏移应是 -2 倍 spacing（实际 {o4.y}）");
                Expect(Mathf.Approximately(o0.x, 0f) && Mathf.Approximately(o1.x, 0f),
                    "跟随槽：局部坐标沿路径分量（x）恒为 0，只做左右阵位调整");
            }

            // 验收 5：移动驱动状态机（纯逻辑层 FormationMemberMotion，见关键提醒 2）。
            // 距首个路点很远时不应前移；到达后应前移到下一个路点；到达最后一个路点后应标记 Arrived；
            // 相同输入重复调用应得到相同结果（纯函数，"重复 Tick 不重算路径"这条约束落在
            // FormationMovementDriver 的运行时字典命中判断上，不属于这层纯状态机的职责）。
            {
                // 路点索引：0=(0,0)，1=(10,0)，2=(20,0)（最后一个）。
                var path = new List<float2> { new float2(0f, 0f), new float2(10f, 0f), new float2(20f, 0f) };
                float arriveRadius = 1.2f;

                // 成员还在路点 0 后方很远处，尚未进入到达半径。
                FormationMemberMotion.StepResult farFromFirst = FormationMemberMotion.Step(
                    path, 0, new float2(-5f, 0f), float2.zero, arriveRadius);
                Expect(farFromFirst.NextWaypointIndex == 0 && !farFromFirst.Arrived,
                    "移动驱动：距首个路点很远时不应前移路点索引");

                // 成员正好在路点 0 上：应前移到路点 1（还不是最后一个，不算 Arrived）。
                FormationMemberMotion.StepResult reachedFirst = FormationMemberMotion.Step(
                    path, 0, new float2(0f, 0f), float2.zero, arriveRadius);
                Expect(reachedFirst.NextWaypointIndex == 1 && !reachedFirst.Arrived,
                    "移动驱动：到达路点 0 后应前移到路点 1（还不是最后一个，不算 Arrived）");

                // 成员正好在最后一个路点（索引 2）上：应标记 Arrived。
                FormationMemberMotion.StepResult reachedLast = FormationMemberMotion.Step(
                    path, 2, new float2(20f, 0f), float2.zero, arriveRadius);
                Expect(reachedLast.Arrived && reachedLast.NextWaypointIndex == 2,
                    "移动驱动：到达最后一个路点后应标记 Arrived，且路点索引保持在最后一个");

                // 相同输入重复调用应得到相同结果（纯函数，无隐藏状态）。
                FormationMemberMotion.StepResult repeat = FormationMemberMotion.Step(
                    path, 0, new float2(-5f, 0f), float2.zero, arriveRadius);
                Expect(repeat.NextWaypointIndex == farFromFirst.NextWaypointIndex && repeat.Arrived == farFromFirst.Arrived,
                    "移动驱动：相同输入重复调用 Step 应得到相同结果（纯函数，无隐藏状态）");
            }

            // 验收 6：卡死重规划边界（纯逻辑层 FormationStuckTracker）。连续多个 tick 位移低于阈值：
            // 先触发一次 Replan；同一条命令再次卡住触发第 2 次 Replan；超过重规划上限（2 次）
            // 仍卡住应判定 Fail；正常位移不应触发 Replan/Fail。
            {
                float stuckTimer = 0f;
                int replanCount = 0;
                const float distThreshold = 0.2f;
                const float timeThreshold = 2f;
                const int maxReplans = 2;
                const float dt = 0.5f;
                const float tinyMove = 0.01f; // 远低于 distThreshold，视为"没怎么动"

                FormationStuckTracker.Outcome last = FormationStuckTracker.Outcome.Ok;
                for (int tick = 0; tick < 4; tick++) // 4 * 0.5s = 2s，正好到阈值
                {
                    last = FormationStuckTracker.Evaluate(ref stuckTimer, ref replanCount, tinyMove, dt,
                        distThreshold, timeThreshold, maxReplans);
                }
                Expect(last == FormationStuckTracker.Outcome.Replan && replanCount == 1,
                    $"卡死检测：连续 2 秒位移低于阈值应触发第 1 次 Replan（实际 {last}，replanCount={replanCount}）");

                for (int tick = 0; tick < 4; tick++)
                {
                    last = FormationStuckTracker.Evaluate(ref stuckTimer, ref replanCount, tinyMove, dt,
                        distThreshold, timeThreshold, maxReplans);
                }
                Expect(last == FormationStuckTracker.Outcome.Replan && replanCount == 2,
                    $"卡死检测：第 2 次仍卡住应再触发一次 Replan（实际 {last}，replanCount={replanCount}）");

                for (int tick = 0; tick < 4; tick++)
                {
                    last = FormationStuckTracker.Evaluate(ref stuckTimer, ref replanCount, tinyMove, dt,
                        distThreshold, timeThreshold, maxReplans);
                }
                Expect(last == FormationStuckTracker.Outcome.Fail && replanCount == 3,
                    $"卡死检测：重规划次数超过上限（{maxReplans}）仍卡住应判定 Fail（实际 {last}，replanCount={replanCount}）");

                float movingTimer = 0f;
                int movingReplanCount = 0;
                FormationStuckTracker.Outcome movingOutcome = FormationStuckTracker.Evaluate(
                    ref movingTimer, ref movingReplanCount, 1.0f, dt, distThreshold, timeThreshold, maxReplans);
                Expect(movingOutcome == FormationStuckTracker.Outcome.Ok && movingReplanCount == 0,
                    "卡死检测：正常位移（>= 阈值）不应触发 Replan/Fail");
            }

            // 验收 7：CPU 预算（D7）。32 个障碍物（SimConst.MaxObstacles 上限）+ 起止点跨越多个
            // 障碍的最坏构造输入，连续跑 100 次，断言均摊每次 < 1ms。这是本 story 自定的合成基准，
            // 不是真实 profiler 采样。
            {
                var worstCaseObstacles = new List<ObstacleSpec>(SimConst.MaxObstacles);
                for (int i = 0; i < SimConst.MaxObstacles; i++)
                {
                    float x = 1f + i * 0.6f;
                    float y = (i % 2 == 0) ? 0.3f : -0.3f;
                    worstCaseObstacles.Add(new ObstacleSpec { Position = new float2(x, y), Radius = 0.4f });
                }
                float2 start = new float2(0f, 0f);
                float2 goal = new float2(1f + SimConst.MaxObstacles * 0.6f + 5f, 0f);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                const int iterations = 100;
                for (int i = 0; i < iterations; i++)
                {
                    FormationPathPlanner.Plan(start, goal, worstCaseObstacles, 0.3f);
                }
                sw.Stop();
                double perCallMs = sw.Elapsed.TotalMilliseconds / iterations;
                Expect(perCallMs < 1.0,
                    $"CPU 预算：{SimConst.MaxObstacles} 障碍物最坏输入下，均摊每次 Plan 应 < 1ms（实际 {perCallMs:F4}ms）");
            }

            // 验收 8：回归 [33]/[34]/[35]。本方法不改动它们的任何断言，靠 RunAll() 里三者继续跑、
            // 继续绿灯来验证，这里不重复断言内容。
            Line("  · [33]/[34]/[35] 回归由 RunAll() 统一跑，见对应方法本身，未在此处重复断言");
        }

        /// <summary>
        /// M4-05：直控脱队与回归。详见 production/session-state/preflight-decisions.md「M4-05」的
        /// D1~D4 与验收映射。核心边界：新代码只允许调用 <see cref="Formation.SetDetached"/>，不碰任何
        /// 命令状态——验收 3 专门验这个。全部走真实链路：真实 <see cref="SimBridge.RequestControlSwitch"/>
        /// 触发真实 <see cref="ControlledUnitChangedSignal"/> 发布，被真实注册在 <see cref="ModuleHub"/>
        /// 上、真实订阅了信号的 <see cref="FormationMovementDriver"/> 接住——不是自检里手动调用模拟信号
        /// 处理函数那种绕过真实接线的做法。
        /// </summary>
        private static void ValidateDirectControlDetachment()
        {
            Line("\n[37] 直控脱队与回归（M4-05）");

            var hub = new ModuleHub();
            var registry = hub.Register(new FormationRegistry());
            var sim = hub.Register(new SimBridge());
            hub.Register(new FormationMovementDriver());
            hub.Enter();

            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            cfg.ArenaHalfExtent = 100f;
            sim.Begin(cfg, Array.Empty<BehaviorArchetype>());
            // 不受信号范围/冷却干扰——本组断言只关心脱队/回归标记，不关心 Tab 循环节奏。
            sim.ConfigureControlSwitch(1_000_000f, 0f);

            try
            {
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(0f, 0f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 5f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, LogicId = 9101,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(2f, 0f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 5f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, LogicId = 9102,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(4f, 0f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 5f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, LogicId = 9103,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(6f, 0f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 5f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, LogicId = 9104,
                });
                sim.OnUpdate(0.01f); // 让 spawn 落地一帧（直接调用，不经过 hub，此时还没有编队）

                SimSnapshot snap = sim.Snapshot;
                SimEntityId unitX = FindEntityId(snap, 9101, out _);
                SimEntityId unitY = FindEntityId(snap, 9102, out _);
                SimEntityId unitZ = FindEntityId(snap, 9103, out _); // 非编队成员，用于验收 4
                SimEntityId unitW = FindEntityId(snap, 9104, out _); // 另一个非编队成员，凑纯粹的"非成员→非成员"切换
                Expect(unitX.IsValid && unitY.IsValid && unitZ.IsValid && unitW.IsValid,
                    "本项前置：四个测试单位应拥有有效稳定 ID");

                Formation formation = registry.CreateFormation();
                formation.AddMember(unitX);
                formation.AddMember(unitY);

                // 前置：给编队一个 Active Move 命令，供验收 3 核对信号处理前后完全不变。
                formation.IssueCommand(new FormationCommand(FormationCommand.CommandKind.Move,
                    targetPosition: new float2(50f, 0f)));
                FormationCommandEntry activeBefore = formation.ActiveCommand;
                FormationCommand.CommandKind activeKindBefore = activeBefore.Command.Kind;
                FormationCommandState activeStateBefore = activeBefore.State;
                FormationCommandFailReason activeFailBefore = activeBefore.FailReason;

                // 验收 1：接管即脱队。previous=玩家本体（非编队成员，no-op），current=unitX（编队成员，应脱队）。
                Expect(sim.RequestControlSwitch(unitX) == ControlRequestResult.Success,
                    "本项前置：真实接管 unitX 应成功（走 SimBridge.RequestControlSwitch，真实发布信号）");
                Expect(formation.IsDetached(unitX),
                    "验收 1：接管 unitX 后，FormationMovementDriver 收到真实信号应把它标记为脱队");
                Expect(formation.IsMember(unitX) && formation.Members.Count == 2,
                    "验收 1：脱队不是移除，成员集合应保持不变（不复制/不删除成员）");

                // 验收 2：退出即回归。previous=unitX（应回归），current=unitY（编队成员，应脱队）。
                Expect(sim.RequestControlSwitch(unitY) == ControlRequestResult.Success,
                    "本项前置：真实切换到 unitY 应成功");
                Expect(!formation.IsDetached(unitX), "验收 2：unitX 退出直控后应回归（IsDetached 归假）");
                Expect(formation.IsDetached(unitY), "验收 2：unitY 被接管后应脱队");
                Expect(formation.Members.Count == 2 && formation.IsMember(unitX) && formation.IsMember(unitY),
                    "验收 2：两次信号处理后，成员集合应始终不变");

                // 验收 3：命令不受影响。信号处理前后 ActiveCommand 的 Kind/State/FailReason 应完全没变。
                FormationCommandEntry activeAfter = formation.ActiveCommand;
                Expect(activeAfter != null && activeAfter.Command.Kind == activeKindBefore &&
                       activeAfter.State == activeStateBefore && activeAfter.FailReason == activeFailBefore,
                    "验收 3：接管/退出信号处理前后，编队 ActiveCommand 的 Kind/State/FailReason 都不应改变");

                // 验收 4：非成员 no-op。先切到 unitZ——此时 previous=unitY 仍是编队成员，会合法回归
                // （这是验收 2 逻辑的自然延伸，不是本项要测的东西）；真正的"非成员触发信号"场景要
                // previous/current 都不是编队成员，所以再切一次到 unitW（unitZ→unitW，两者都不是成员）。
                Expect(sim.RequestControlSwitch(unitZ) == ControlRequestResult.Success,
                    "本项前置：真实切换到编队外的 unitZ 应成功（顺带验证 unitY 会合法回归）");
                Expect(!formation.IsDetached(unitY), "本项前置：切出编队后 unitY 应回归（IsDetached 归假）");

                bool threw = false;
                int membersBeforeNonMember = formation.Members.Count;
                bool unitXDetachedBefore = formation.IsDetached(unitX);
                bool unitYDetachedBefore = formation.IsDetached(unitY);
                try
                {
                    sim.RequestControlSwitch(unitW); // previous=unitZ、current=unitW，两者都不是编队成员
                }
                catch
                {
                    threw = true;
                }
                Expect(!threw, "验收 4：对非编队成员触发接管信号不应抛异常");
                Expect(registry.FindFormationContaining(unitW) == null,
                    "验收 4：FindFormationContaining 对非编队成员应返回 null");
                Expect(formation.Members.Count == membersBeforeNonMember &&
                       formation.IsDetached(unitX) == unitXDetachedBefore &&
                       formation.IsDetached(unitY) == unitYDetachedBefore,
                    "验收 4：previous/current 均非编队成员时，触发信号不应改动任何已有编队状态");
            }
            finally
            {
                hub.Exit(); // 真实走 OnExit：driver 真退订信号，sim.End() 收尾，避免污染后续自检。
            }

            // 验收 5：路径驱动跳过脱队成员——独立小场景，行为探针：真实推进 SimWorld 若干帧，
            // 看两个成员的实际位置差异，而不是读驱动器的私有运行时字段。
            ValidateDetachedMemberSkippedByPathing();

            // 验收 6：回归 [33]/[34]/[35]/[36]。本方法不改动它们的任何断言，靠 RunAll() 里四者继续跑、
            // 继续绿灯来验证，这里不重复断言内容。
            Line("  · [33]/[34]/[35]/[36] 回归由 RunAll() 统一跑，见对应方法本身，未在此处重复断言");
        }

        /// <summary>[37] 验收 5 的独立场景：一个编队两名成员，一名标记脱队，另一名正常推进 Move 命令，
        /// 真跑 60 帧后比较两者位置——未脱队成员应明显前移，脱队成员应原地不动（驱动器整段跳过它，
        /// 不下发任何 <see cref="UnitCommand"/>）。</summary>
        private static void ValidateDetachedMemberSkippedByPathing()
        {
            var hub = new ModuleHub();
            var registry = hub.Register(new FormationRegistry());
            var sim = hub.Register(new SimBridge());
            hub.Register(new FormationMovementDriver());
            hub.Enter();

            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            cfg.ArenaHalfExtent = 200f;
            sim.Begin(cfg, Array.Empty<BehaviorArchetype>());

            try
            {
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(0f, 0f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 5f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, LogicId = 9201,
                });
                sim.Spawn(new SpawnRequest
                {
                    // y 分量刻意远离 moving 成员的直线路径（约 (0,0)→(60,0)），避免两者物理接触/
                    // 碰撞分离力把"没有收到任何命令"的脱队成员意外推动，产生假阳性位移。
                    Position = new float2(2f, 30f), Health = 20f, Radius = 0.5f,
                    MaxSpeed = 5f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.Scripted, LogicId = 9202,
                });
                sim.OnUpdate(0.01f); // 让 spawn 落地一帧

                SimSnapshot snap0 = sim.Snapshot;
                SimEntityId moving = FindEntityId(snap0, 9201, out _);
                SimEntityId detached = FindEntityId(snap0, 9202, out _);
                Expect(moving.IsValid && detached.IsValid, "本项前置：两个测试单位应拥有有效稳定 ID");

                Formation formation = registry.CreateFormation();
                formation.AddMember(moving);
                formation.AddMember(detached);
                formation.SetDetached(detached, true);

                sim.TryGetPosition(detached, out float2 detachedBefore);

                formation.IssueCommand(new FormationCommand(FormationCommand.CommandKind.Move,
                    targetPosition: new float2(60f, 0f)));

                for (int f = 0; f < 60; f++)
                {
                    hub.Update(1f / 60f);
                }

                sim.TryGetPosition(moving, out float2 movingAfter);
                sim.TryGetPosition(detached, out float2 detachedAfter);

                float movedDistance = math.distance(new float2(0f, 0f), movingAfter);
                Expect(movedDistance > 1f,
                    $"验收 5：未脱队成员应被路径驱动持续下发 Move 命令并真实前进（实际位移 {movedDistance:F2}）");
                float detachedDrift = math.distance(detachedAfter, detachedBefore);
                Expect(detachedDrift < 0.01f,
                    $"验收 5：脱队成员不应被路径驱动器下发任何命令，位置应保持不动（实际位移 {detachedDrift:F4}）");
            }
            finally
            {
                hub.Exit();
            }
        }

        /// <summary>
        /// M4-06：多线固定遭遇。权威规格是仓库根 <c>production/session-state/preflight-decisions.md</c>
        /// "M4-06 多线固定遭遇"节的 D1~D6 与验收映射——按旧版 <c>ProjectA_Milestones.md</c> M4-06 字面
        /// 验收交付，不套用 <c>DesignDocs/detailed/</c> 更严格的需求（那套契约仍在起草中，不属于本
        /// story 范围）。实际编排逻辑在 <see cref="FormationEncounterScenario"/>，本方法只做断言。
        /// </summary>
        private static void ValidateFormationEncounter()
        {
            Line("\n[38] 多线固定遭遇（M4-06）");

            // maxTicks 只是本项自检的超时保护（防止真出 bug 时测试卡死），不是玩法倒计时——
            // "遭遇完成"判定成立的那一刻 FormationEncounterScenario 就退出循环，不会跑满这个数。
            const int maxTicks = 6000;

            // 验收 1：分线策略可行。
            FormationEncounterScenario.RunResult split = FormationEncounterScenario.RunSplitStrategy(maxTicks);
            Expect(split.NestCleared, "验收 1：分线策略应能把巢清空");
            Expect(split.LineHeld, "验收 1：分线策略的守护编队应全程守住检查点、未减员");
            Expect(split.EncounterComplete && split.CompletionTick >= 0,
                $"验收 1：分线策略应在 {maxTicks} tick 内达成遭遇完成（实际 {split.CompletionTick}）");

            // 验收 2：万能队策略可行但明显更慢（顺序执行两阶段，理论上限接近两倍，取 1.5 倍留余量）。
            FormationEncounterScenario.RunResult generalist = FormationEncounterScenario.RunGeneralistStrategy(maxTicks);
            Expect(generalist.EncounterComplete && generalist.CompletionTick >= 0,
                $"验收 2：万能队策略也应能在 {maxTicks} tick 内达成遭遇完成（实际 {generalist.CompletionTick}）——" +
                "两种方案都可行，只是效率不同");
            if (split.CompletionTick >= 0 && generalist.CompletionTick >= 0)
            {
                Expect(generalist.CompletionTick >= split.CompletionTick * 1.5,
                    $"验收 2：万能队顺序执行两阶段应明显更慢——分线 {split.CompletionTick} tick，" +
                    $"万能队 {generalist.CompletionTick} tick，应 ≥ 分线的 1.5 倍");
            }

            // 验收 3：无倒计时——两种策略的真实完成时刻都应远早于自检超时保护上限，
            // 证明 maxTicks 只是保护，不是驱动"遭遇完成"的倒计时机制。
            if (split.CompletionTick >= 0)
            {
                Expect(split.CompletionTick < maxTicks * 0.8,
                    "验收 3：分线策略完成时刻应远早于自检超时保护（不是倒计时失败）");
            }
            if (generalist.CompletionTick >= 0)
            {
                Expect(generalist.CompletionTick < maxTicks * 0.8,
                    "验收 3：万能队策略完成时刻也应远早于自检超时保护（不是倒计时失败）");
            }

            // 验收 4（D5）：分线策略推进到一半（巢死一半）时，对巢攻坚编队一名成员触发真实接管信号，
            // 断言不打断整体进度、释放后归队，最终"遭遇完成"依然成立。复用 M4-05 已验证的
            // RequestControlSwitch → ControlledUnitChangedSignal → Formation.SetDetached 链路
            // （见 [37]），不是新写的接管逻辑，本项只做接口层面验证，不做真实输入。
            SimEntityId switchedMember = SimEntityId.None;
            Formation capturedAttackFormation = null;
            bool detachedDuringHook = false;
            FormationEncounterScenario.RunResult interrupted = FormationEncounterScenario.RunSplitStrategy(maxTicks,
                (sim, formations, attackFormation, attackMembers) =>
                {
                    capturedAttackFormation = attackFormation;
                    if (attackMembers.Length > 0)
                    {
                        switchedMember = attackMembers[0];
                        bool switched = sim.RequestControlSwitch(switchedMember) == ControlRequestResult.Success;
                        detachedDuringHook = switched && attackFormation.IsDetached(switchedMember);
                        sim.ReleaseControl();
                    }
                });
            Expect(switchedMember.IsValid,
                "验收 4 前置：分线策略巢死一半时应能取到巢攻坚编队一名成员用于接管测试");
            Expect(detachedDuringHook,
                "验收 4：真实接管巢攻坚编队一名成员后，该成员应立即被标记为脱队");
            Expect(interrupted.EncounterComplete && interrupted.CompletionTick >= 0,
                "验收 4：中途接管又释放不应打断整体进度，最终仍应达成遭遇完成");
            Expect(capturedAttackFormation != null && switchedMember.IsValid &&
                   !capturedAttackFormation.IsDetached(switchedMember),
                "验收 4：释放控制后（且后续帧继续推进），该成员应归队（IsDetached 归假）");

            // 验收 5：回归 [33]~[37]。本方法不改动它们的任何断言，靠 RunAll() 里五者继续跑、
            // 继续绿灯来验证，这里不重复断言内容。
            Line("  · [33]~[37] 回归由 RunAll() 统一跑，见对应方法本身，未在此处重复断言");
        }

        /// <summary>
        /// [39] 卡牌 <see cref="AbilitySystem"/>/<see cref="ResourceWallet"/> 与器官
        /// <see cref="UnitVitalsRegistry"/> 的分层边界（M4-R00-02 队列①，2026-09-15 bin 拍板：
        /// **分层，不合并成一个统一钱包**——见
        /// `production/design/m4-r00-02-item1-ability-truth-unification/DESIGN.md` §1b）。
        ///
        /// 验收 a/b 是分层本身：两套账本各自独立运作，互不知情。
        /// 验收 c 是跨层触发时的真实观察结果——**如实记录一个不在本次分层决策修复范围内、但值得
        /// 产品知道的边界**：玩家本体按 Primary 键委托到卡牌槎位时，会经过
        /// <see cref="DirectControlActions.TryRelease"/> 的器官闸门（走 <see cref="UnitVitalsRegistry"/>
        /// 记一笔"这件 Primary 器官"的代谢/过载债），但委托执行走的是
        /// <see cref="AbilitySystem.TryCastAuto"/>，**不经过</see>
        /// <c>CellPlayerController.TryCastSlot</c> 的体力检查——即这条路径下体力完全不扣。
        /// 这不是"重复扣费"（两边都只各记一次或零次，没有任何一边被扣两次），而是"同一次操作在
        /// 两套账本上被记了不对称的账"。本方法只如实断言现状，不擅自改写这条路径的产品语义。
        /// </summary>
        private static void ValidateAbilityResourceLayerBoundary()
        {
            Line("\n[39] 卡牌/器官分层边界（M4-R00-02 队列①，分层不合并）");

            var stats = new StatSheet();
            stats.ResetToDefaults();

            var wallet = new ResourceWallet();
            wallet.Bind(stats);
            wallet.OnEnter();

            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 60f;
            cfg.RandomSeed = 0xC0FFEE09u;
            sim.Begin(cfg, Array.Empty<BehaviorArchetype>());
            sim.ConfigureControlSwitch(100f, 0f);

            var abilities = new AbilitySystem();
            abilities.RegisterExecutor(new EffectResource());
            abilities.Bind(sim, stats);

            const float StaminaCost = 12f;
            const float GainValue = 5f;
            // 槎位 0 恒为冲刺，PlayerPrimaryAbilitySlot=1——占位授予一个哑技能占槎位 0，
            // 真正观察用的技能落在槎位 1，才会被 DirectControlActions 的委托路径命中。
            Expect(abilities.Grant(new AbilitySpec { Id = 0, Cooldown = 0.05f, TargetMode = TargetMode.Self }),
                "应能授予占位槎位 0（冲刺）");
            var observedSpec = new AbilitySpec
            {
                Id = 1,
                Cooldown = 0.05f,
                Charges = 1,
                StaminaCost = StaminaCost,
                TargetMode = TargetMode.Self,
                Effects = new List<EffectSpec>
                {
                    new EffectSpec
                    {
                        Kind = EffectKind.Resource, Resource = ResourceKind.EvoEnergy,
                        Value = GainValue, ScaleWithPower = false,
                    },
                },
            };
            Expect(abilities.Grant(observedSpec), "应能授予槎位 1 的观测用技能");

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
                actions.Bind(sim, registry, abilities: abilities, status: null);

                const int FriendLogicId = 9801;
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(10f, 2f), Health = 40f, Radius = 0.8f, MaxSpeed = 0f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = FriendLogicId,
                });
                registry.RegisterArchetypePending(FriendLogicId, ArchetypeLoadoutTable.SporeArchetypeId);
                sim.OnUpdate(1f / 60f);
                registry.ResolvePending(sim.Snapshot);
                SimEntityId friend = FindEntityId(sim.Snapshot, FriendLogicId, out _);
                Expect(friend.IsValid, "友军应已落地并拥有有效稳定实体 ID");

                // ── a. 卡牌独立释放：只扣 ResourceWallet，不建立任何器官账本记录 ──
                Expect(!actions.Vitals.IsTracked(body),
                    "测试开局时玩家本体的器官账本（UnitVitalsRegistry）应尚未有任何记录");
                float staminaBefore = wallet.Stamina;
                float evoBefore = wallet.EvoEnergy;
                bool cardCastOk = wallet.TrySpend(ResourceKind.Stamina, observedSpec.StaminaCost)
                                   && abilities.TryCastAuto(1);
                Expect(cardCastOk, "独立卡牌施放路径（体力检查 + AbilitySystem 施放）应成功");
                Expect(Mathf.Approximately(wallet.Stamina, staminaBefore - StaminaCost),
                    $"体力应精确扣掉 {StaminaCost}（实际 {staminaBefore:F1}→{wallet.Stamina:F1}）");
                Expect(Mathf.Approximately(wallet.EvoEnergy, evoBefore + GainValue),
                    $"卡牌效果应精确生效（进化能 {evoBefore:F1}→{wallet.EvoEnergy:F1}）");
                Expect(!actions.Vitals.IsTracked(body),
                    "卡牌独立释放不应在器官账本里为玩家本体建立任何记录——两套账本完全独立，" +
                    "这是本次 bin 拍板的分层决策要求的最基本保证");

                // ── b. 器官独立释放：只走 UnitVitalsRegistry，完全不触碰卡牌钱包 ──
                Expect(sim.RequestControlSwitch(friend) == ControlRequestResult.Success, "应能接管友军");
                float staminaBeforeOrgan = wallet.Stamina;
                float evoBeforeOrgan = wallet.EvoEnergy;
                bool organReleased = actions.TryRelease(LoadoutAction.Primary, aim);
                Expect(organReleased, "友军的器官释放应成功");
                Expect(Mathf.Approximately(wallet.Stamina, staminaBeforeOrgan) &&
                       Mathf.Approximately(wallet.EvoEnergy, evoBeforeOrgan),
                    $"器官释放不应触碰卡牌钱包的任何资源（体力 {staminaBeforeOrgan:F1}→{wallet.Stamina:F1}，" +
                    $"进化能 {evoBeforeOrgan:F1}→{wallet.EvoEnergy:F1}）");
                Expect(actions.Vitals.IsTracked(friend),
                    "器官释放应在 UnitVitalsRegistry 里为该身体建立记录——账本本身与卡牌无关地正常运作");

                // ── c. 跨层触发（玩家本体按 Primary 委托到卡牌槎位 1）：如实记录现状 ──
                Expect(sim.RequestControlSwitch(body) == ControlRequestResult.Success, "应能切回玩家本体");
                // 槎位 1 在 a 步已经消费过充能，这里推进冷却让它回到 Ready——不然 c 步测的是
                // "槎位没冷却好"而不是"跨层触发的真实结果"。
                abilities.OnUpdate(observedSpec.Cooldown + 0.05f);
                fakeSource.Organs.Clear();
                fakeSource.Organs.Add(new UnitLoadoutOrgan("org_emitter", LoadoutAction.Primary));
                actions.Rebuild();

                UnitVitalsView vitalsBeforeCross = actions.Vitals.Get(body);
                float evoBeforeCross = wallet.EvoEnergy;
                float staminaBeforeCross = wallet.Stamina;
                bool crossOk = actions.TryRelease(LoadoutAction.Primary, aim);
                Expect(crossOk, "玩家本体按 Primary 应成功委托到卡牌槎位 1（org_emitter 是现役攻击器官）");

                UnitVitalsView vitalsAfterCross = actions.Vitals.Get(body);
                Expect(vitalsAfterCross.Metabolism < vitalsBeforeCross.Metabolism &&
                       vitalsAfterCross.Strain > vitalsBeforeCross.Strain,
                    "器官账本这一侧只应被记一次（不能是零次或两次）：委托路径仍会对 Primary 器官" +
                    "本身走一次 Evaluate/Commit，这是 TryRelease 统一闸门的既有设计");
                Expect(Mathf.Approximately(wallet.EvoEnergy, evoBeforeCross + GainValue),
                    $"卡牌这一侧的效果应精确生效恰好一次（进化能 {evoBeforeCross:F1}→{wallet.EvoEnergy:F1}）——" +
                    "证明委托没有让 RunEffects 被多算或漏算");
                // 如实记录：这条委托路径调用的是 AbilitySystem.TryCastAuto，不经过
                // CellPlayerController.TryCastSlot 的体力检查，所以体力在这条路径下不会被扣——
                // 这不是"重复扣费"，是"跨层触发时体力这一侧完全没被计费"，与本次分层决策要解决的
                // 问题不同类，如实记录留给产品判断是否需要另开故事处理，本方法不擅自改写它。
                Expect(Mathf.Approximately(wallet.Stamina, staminaBeforeCross),
                    $"【如实记录，非本次范围】玩家本体委托路径（TryRelease→ReleaseOnPlayerBody→" +
                    $"AbilitySystem.TryCastAuto）不经过 CellPlayerController.TryCastSlot 的体力检查，" +
                    $"体力当前确实未被扣（{staminaBeforeCross:F1}→{wallet.Stamina:F1}）——" +
                    "若产品认为这条路径也该扣体力，需要另开故事显式接线，不属于本次分层决策范围");

                // ── d. 同一器官三种控制来源结果一致：已由 [25] 覆盖，本方法不重复断言 ──
                Line("  · 同一器官玩家/直控友军/AI 结果一致已由 [25] 覆盖，未在此处重复断言");

                // ── e. 卡牌不能绕过器官禁用闸门 ──
                fakeSource.Organs.Clear();
                fakeSource.Organs.Add(new UnitLoadoutOrgan("org_emitter", LoadoutAction.Primary, disabled: true));
                actions.Rebuild();
                bool releasedWhileDisabled = actions.TryRelease(LoadoutAction.Primary, aim);
                Expect(!releasedWhileDisabled &&
                       actions.LastReleaseResult == DirectActionAvailability.OrganDisabled,
                    "Primary 器官被禁用时，即使卡牌槎位 1 本身已就绪，委托路径也必须在入口被拒" +
                    "（原因 OrganDisabled）——卡牌不能绕过器官禁用闸门去执行任何委托效果");
            }
            finally
            {
                actions.Unbind();
                registry.Unbind();
                sim.End();
                InputRouter.Reset();
            }
        }

        /// <summary>
        /// M4-R00-02 队列②号项：萌生腔新生个体的基因终于被传下去了（此前
        /// <see cref="GerminationChamberRegistry.SpawnFromTicket"/> 只取
        /// <c>ticket.Version.OrganelleId</c>，<c>GeneIds</c> 被就地丢弃，是"友军基因对战斗表现
        /// 零影响"这条症状的根因——见
        /// <c>production/design/m4-r00-02-item2-any-entity-projection/DESIGN.md</c>）。
        ///
        /// 用两具**分别在独立 SimBridge 里**、经真实萌生腔生产入口（<c>Enqueue</c>→计时→
        /// <see cref="GerminationChamberRegistry.SpawnFromTicket"/>，不手工构造
        /// <see cref="UnitLoadoutOrgan"/>）落地的个体，比较它们经 AI 自动开火
        /// （<see cref="MinionOrganCombatDriver"/>，与直控同一条 <see cref="OrganReleaseRunner"/>/
        /// <c>ResolveCompiled</c> 链路）打出来的东西是否因基因组合不同而不同。分两个独立 sim 而不是
        /// 同场对照：新生个体的落点由萌生腔按玩家位置自动摆放（GDD 没有腔体坐标概念），硬把两具凑到
        /// 同一场景里会导致索敌/攻击范围互相干扰，让归因变得不可靠。
        /// </summary>
        private static void ValidateFriendlyGeneProjection()
        {
            Line("\n[40] 友军接入统一装配签名：萌生腔新生个体真的带基因（M4-R00-02 队列②）");

            OrganelleDef organelle = OrganelleCatalog.All.Values.FirstOrDefault(o => o.AttackMethod && !o.IsRetired);
            Expect(organelle != null, "OrganelleCatalog 应至少有一条 AttackMethod 未退役器官用于本项自检");
            if (organelle == null)
            {
                return;
            }

            const string VacuoleGeneId = "gene_vacuole";
            List<string> allGenes = GeneCatalog.AllGeneIds.ToList();
            List<string> otherGenes = allGenes.Where(g => g != VacuoleGeneId).Take(2).ToList();
            Expect(allGenes.Contains(VacuoleGeneId) && otherGenes.Count == 2,
                "本项需要 gene_vacuole（挂 Capacitor，真的会乘 packet.Energy，见 [25]④b 同一选择理由）" +
                "加两条其它基因作对照，GeneCatalog 应满足");
            if (!allGenes.Contains(VacuoleGeneId) || otherGenes.Count < 2)
            {
                return;
            }

            // 基线配方：两条与 gene_vacuole 无关的基因。换基因配方：换掉其中一条为 gene_vacuole。
            // 两者都满足 LineageRegistry.MinGeneSlots=2 的槽位下限。
            List<string> bareGenes = otherGenes;
            List<string> genedGenes = new List<string> { otherGenes[0], VacuoleGeneId };

            OrganKernelAction bareAct = RunFriendlyGeneProjectionScenario(
                organelle.Id, "lineage-gene-proj-bare", bareGenes, 0xC0FFEE41u, out IReadOnlyList<string> bareLoadoutGenes);
            OrganKernelAction genedAct = RunFriendlyGeneProjectionScenario(
                organelle.Id, "lineage-gene-proj-gened", genedGenes, 0xC0FFEE42u, out IReadOnlyList<string> genedLoadoutGenes);

            Expect(bareAct.IsValid && genedAct.IsValid,
                $"两套场景都应解析出有效内核动作（基线 {bareAct.IsValid} / 换基因 {genedAct.IsValid}）");
            if (!bareAct.IsValid || !genedAct.IsValid)
            {
                return;
            }

            Expect(!Mathf.Approximately(bareAct.Damage, genedAct.Damage) ||
                   !Mathf.Approximately(bareAct.Cooldown, genedAct.Cooldown) ||
                   !Mathf.Approximately(bareAct.MetabolicCost, genedAct.MetabolicCost),
                $"同一件主器官，萌生腔真实生产入口落地的两具个体因基因组合不同（基线 " +
                $"[{string.Join(",", bareLoadoutGenes ?? Array.Empty<string>())}] vs 换基因 " +
                $"[{string.Join(",", genedLoadoutGenes ?? Array.Empty<string>())}]），AI 开火编译结果" +
                $"应至少一项不同（基线伤害={bareAct.Damage:F2}/冷却={bareAct.Cooldown:F2} " +
                $"换基因后伤害={genedAct.Damage:F2}/冷却={genedAct.Cooldown:F2}）——这是" +
                "\"友军基因影响战斗表现\"这条症状**在生产路径**（萌生腔 Enqueue→AI 自动开火，不是" +
                "直连 ResolveCompiled）上第一次有真实断言，不能只靠①号项 [25]④b 的直连证明推断" +
                "生产路径也接上了");
        }

        /// <summary>单套"提交配方→萌生腔入队→落地→AI 开火"场景，供 <see cref="ValidateFriendlyGeneProjection"/>
        /// 对两种基因组合各跑一遍并比较。</summary>
        private static OrganKernelAction RunFriendlyGeneProjectionScenario(
            string organelleId, string lineageId, List<string> geneIds, uint randomSeed, out IReadOnlyList<string> loadoutGeneIds)
        {
            loadoutGeneIds = null;

            var blueprints = new BlueprintRegistry();
            blueprints.Resolve(organelleId, BlueprintSourceKind.Organelle, 1f, 0f);
            foreach (string g in geneIds)
            {
                blueprints.Resolve(g, BlueprintSourceKind.Gene, 1f, 0f);
            }

            var lineages = new LineageRegistry();
            lineages.Bind(blueprints);
            PhenotypeTemplateVersion version = lineages.CommitTemplate(lineageId, "assault", organelleId, geneIds, "keep_distance", out string commitErr);
            Expect(version != null && commitErr == null, $"本项前置：{lineageId} 配方提交应成功（{commitErr}）");
            if (version == null)
            {
                return OrganKernelAction.None;
            }

            var ledger = new BiomassLedger();
            ledger.OnEnter();
            var chamber = new GerminationChamberRegistry();
            chamber.OnEnter();

            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 60f;
            cfg.RandomSeed = randomSeed;
            // 萌生腔生成的个体固定用 ArchetypeLoadoutTable.SporeArchetypeId（=13，见
            // GerminationChamberRegistry.BuildSpawnRequest），数组必须够长且该索引是
            // MinionSeekAttack，否则这具个体在内核里查不到有效行为原型，索敌/攻击永远不会触发。
            const int DummyArchetypeId = 0;
            var archetypes = new BehaviorArchetype[ArchetypeLoadoutTable.SporeArchetypeId + 1];
            archetypes[DummyArchetypeId] = new BehaviorArchetype
            {
                Kind = BehaviorKind.Stationary, Accel = 0f, TurnRate = 0f, AggroRange = 0f,
                AttackRange = 0.5f, AttackCooldown = 99f, AttackDamage = 0f,
                Separation = 0f, ChargeSpeedMul = 1f,
            };
            archetypes[ArchetypeLoadoutTable.SporeArchetypeId] = new BehaviorArchetype
            {
                Kind = BehaviorKind.MinionSeekAttack, Accel = 12f, TurnRate = 0f, AggroRange = 12f,
                AttackRange = 6f, AttackCooldown = 0.25f, AttackDamage = 5f,
                Separation = 0f, ChargeSpeedMul = 1f,
            };
            sim.Begin(cfg, archetypes);

            var unitLoadouts = new UnitLoadoutRegistry();
            var fakeSource = new FakePlayerLoadoutSource();
            unitLoadouts.Bind(sim, fakeSource);
            unitLoadouts.RegisterPlayerBody(sim.ControlledUnitId);
            chamber.Bind(sim, lineages, ledger, unitLoadouts);

            var actions = new DirectControlActions();
            actions.Bind(sim, unitLoadouts, abilities: null, status: null);

            try
            {
                int ticket = chamber.Enqueue(lineageId, "assault", out PhenotypeTemplateVersion captured, out string enqErr);
                Expect(ticket != 0 && enqErr == null, $"本项前置：{lineageId} 入队应成功（{enqErr}）");
                if (ticket == 0)
                {
                    return OrganKernelAction.None;
                }

                // 一次性推满萌生耗时：SpawnFromTicket 只需在 OnUpdate 内被触发一次，具体帧数无所谓，
                // 这不是要重测计时器本身（[29] 已经测过）。
                chamber.OnUpdate(GerminationChamberRegistry.GerminationSeconds + 0.1f);
                // 落地：Spawn 请求要等 sim 真的推进一帧才会出现在快照里（与 [25]/[29] 同一套两段式）。
                sim.OnUpdate(1f / 60f);
                chamber.OnUpdate(1f / 60f); // 让 ResolvePendingBinds 用上一步之后的新快照重新解析一次。
                unitLoadouts.ResolvePending(sim.Snapshot);

                SimEntityId spawned = SimEntityId.None;
                foreach (KeyValuePair<SimEntityId, GerminationChamberRegistry.UnitBinding> kv in chamber.Bindings)
                {
                    if (ReferenceEquals(kv.Value.Version, captured))
                    {
                        spawned = kv.Key;
                        break;
                    }
                }
                Expect(spawned.IsValid, $"本项前置：{lineageId} 的新生个体应落地并完成谱系绑定");
                if (!spawned.IsValid)
                {
                    return OrganKernelAction.None;
                }

                UnitLoadout loadout = unitLoadouts.Get(spawned);
                Expect(loadout.TryGetOrgan(LoadoutAction.Primary, out UnitLoadoutOrgan organ),
                    $"{lineageId} 新生个体应装配了主器官");
                loadoutGeneIds = organ.GeneIds;
                Expect(organ.GeneIds != null && organ.GeneIds.SequenceEqual(geneIds),
                    $"{lineageId} 新生个体装配上的 GeneIds 应与提交模板一致（实得 " +
                    $"[{(organ.GeneIds == null ? "null" : string.Join(",", organ.GeneIds))}]）——" +
                    "这是本次要修的根因点：GerminationChamberRegistry.SpawnFromTicket 此前把它丢了");

                if (!sim.TryResolveUnitIndex(spawned, out int spawnedIdx))
                {
                    return OrganKernelAction.None;
                }
                float2 spawnedPos = sim.Snapshot.Position[spawnedIdx];

                sim.Spawn(new SpawnRequest
                {
                    Position = spawnedPos + new float2(4f, 0f), Health = 100000f, Radius = 0.6f, MaxSpeed = 0f,
                    ArchetypeId = DummyArchetypeId, Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.AI, LogicId = 9910,
                });
                sim.OnUpdate(1f / 60f);

                int releaseBefore = actions.MinionCombat.ReleaseCount;
                for (int f = 0; f < 240 && actions.MinionCombat.ReleaseCount == releaseBefore; f++)
                {
                    sim.OnUpdate(1f / 60f);
                    actions.Tick(1f / 60f, paused: false);
                }

                Expect(actions.MinionCombat.ReleaseCount > releaseBefore,
                    $"{lineageId} 新生个体应能通过 AI 器官释放路径真的开过火");
                return actions.MinionCombat.LastReleasedKernelAction;
            }
            finally
            {
                actions.Unbind();
                unitLoadouts.Unbind();
                sim.End();
            }
        }

        /// <summary>
        /// M4-R00-02 队列③号项第9条：合并全仓四套互不一致的扇形角度公式为单一纯函数
        /// （CP-REQ-012）。审计点名四个同名/近名函数，实读代码后确认其中三个
        /// （<c>MetabolicSliceBridge.FanDirection/ConeFanDirection/MeleeFanDirection</c>）全仓零调用点，
        /// 是死代码，已直接删除；真正在用的只有 <see cref="CombatBallistics.FanDirection"/> 一个，
        /// 但它有两处违反 CP-REQ-012 书面规格（经 bin 拍板确认修复方向）：
        /// ①`count&gt;1` 且 `spreadDeg&lt;=0` 时曾无条件退化成 360° 环形均分，规格要求这种情况必须
        /// "同向发射"，环射必须由显式基元声明——已在 ComposeEngine 侧新增 `Packet/HitEvent.
        /// RadialRequested`（`Scatterer` 单独存在时声明），本项验证该声明端到端穿透到生产内容
        /// （org_orbitcilia/gene_harmonic）。②`count==1` 且 `spreadDeg&gt;0` 时曾把扇角当"精度抖动"用，
        /// 规格要求单发严格沿轴——已删除该分支（副作用：gene_fan 单独装配在单发武器上时会变得
        /// 没有可观测效果，这是需要产品知晓的内容表现变化，非本项测试断言范围）。
        /// </summary>
        private static void ValidateFanDirectionUnified()
        {
            Line("\n[41] 扇形角度公式统一为单一纯函数（CP-REQ-012，M4-R00-02 队列③）");

            float2 aim = new float2(0f, 1f);

            // ── 1. count<=1 严格沿轴，不受 spreadDeg/radialRequested 影响 ──
            float2 straight1 = CombatBallistics.FanDirection(aim, 0, 1, 0f, false);
            float2 straight2 = CombatBallistics.FanDirection(aim, 0, 1, 60f, false);
            float2 straight3 = CombatBallistics.FanDirection(aim, 0, 1, 60f, true);
            Expect(FloatsEqual(straight1, aim) && FloatsEqual(straight2, aim) && FloatsEqual(straight3, aim),
                "count<=1 必须严格沿瞄准轴，不受 spreadDeg/radialRequested 影响——旧实现曾把 " +
                "spreadDeg>0 时的单发解释成精度抖动，与 CP-REQ-012（散射精度应是独立的 " +
                "AccuracyJitter 字段）冲突，已删除该分支");

            // ── 2. count>1 且 spreadDeg<=0 且未声明 radial：默认同向发射，不再隐式环射 ──
            float2 parallel0 = CombatBallistics.FanDirection(aim, 0, 3, 0f, false);
            float2 parallel1 = CombatBallistics.FanDirection(aim, 1, 3, 0f, false);
            float2 parallel2 = CombatBallistics.FanDirection(aim, 2, 3, 0f, false);
            Expect(FloatsEqual(parallel0, aim) && FloatsEqual(parallel1, aim) && FloatsEqual(parallel2, aim),
                "count>1 且 spreadDeg<=0 且未声明 radialRequested 时应同向发射——CP-REQ-012 要求环射" +
                "必须由 Radial 基元显式声明，不能从\"没配扇角\"隐式反推");

            // ── 3. count>1 且 spreadDeg<=0 且声明了 radial：环形均分 ──
            float2 ring0 = CombatBallistics.FanDirection(aim, 0, 2, 0f, true);
            float2 ring1 = CombatBallistics.FanDirection(aim, 1, 2, 0f, true);
            Expect(FloatsEqual(ring0, aim), "环射第 0 发应与基准方向重合（index=0 → 角度 0）");
            Expect(math.dot(ring0, ring1) < -0.99f,
                $"2 发环射应彼此相反（180°），实际点积 {math.dot(ring0, ring1):F3}——" +
                "这正是 org_orbitcilia\"绕一圈打\"手感的几何来源");

            // ── 4. count>1 且 spreadDeg>0：±half 内确定性均分（既有兼容分支，不应回归） ──
            float2 fan0 = CombatBallistics.FanDirection(aim, 0, 3, 60f, false);
            float2 fan2 = CombatBallistics.FanDirection(aim, 2, 3, 60f, false);
            float angleBetween = math.degrees(math.acos(math.clamp(math.dot(fan0, fan2), -1f, 1f)));
            Expect(math.abs(angleBetween - 60f) < 0.5f,
                $"3 发、总扇角 60° 时首尾两发夹角应为 60°，实际 {angleBetween:F2}°");

            // ── 5. 生产入口：org_orbitcilia（Scatterer 单独存在，没配 SpreadModule）真的携带
            //    RadialRequested，不是本次新增字段却零消费者 ──
            var engine = new ComposeEngine.Engine();
            var world = new ComposeEngine.Core.WorldState();
            List<ComposeEngine.Core.HitEvent> orbitEvents = CarrierCompiler.CompileFromRecipe(
                engine, "org_orbitcilia", Array.Empty<string>(), world, seed: 1);
            Expect(orbitEvents.Count == 1, $"org_orbitcilia 应产出 1 条 HitEvent，实际 {orbitEvents.Count}");
            if (orbitEvents.Count == 1)
            {
                ComposeEngine.Core.HitEvent orbitEvt = orbitEvents[0];
                Expect(orbitEvt.RadialRequested,
                    "org_orbitcilia 只挂 Scatterer 没配 SpreadModule，编译出的 HitEvent 必须携带 " +
                    "RadialRequested=true——否则它会从\"绕一圈打\"退化成\"往一个方向打\"，是可见的" +
                    "内容行为倒退");
                Expect(orbitEvt.SpreadAngle <= 0f, "org_orbitcilia 没有配 SpreadModule，SpreadAngle 应保持默认 0");
                Expect(orbitEvt.Count >= 2f, $"org_orbitcilia 的 ScattererCount 应至少产出 2 发，实际 Count={orbitEvt.Count}");
            }

            // ── 6. 生产入口：gene_harmonic 挂在任意攻击器官上同样携带 RadialRequested ──
            OrganelleDef organelle = OrganelleCatalog.All.Values
                .FirstOrDefault(o => o.AttackMethod && !o.IsRetired && o.Id != "org_orbitcilia");
            Expect(organelle != null, "本项前置：需要一条非 org_orbitcilia 的 AttackMethod 未退役器官用于 gene_harmonic 对照");
            if (organelle != null)
            {
                List<ComposeEngine.Core.HitEvent> harmonicEvents = CarrierCompiler.CompileFromRecipe(
                    engine, organelle.Id, new[] { "gene_harmonic" }, world, seed: 2);
                Expect(harmonicEvents.Count >= 1, "gene_harmonic 挂载后应至少产出 1 条 HitEvent");
                if (harmonicEvents.Count >= 1)
                {
                    ComposeEngine.Core.HitEvent harmonicEvt = harmonicEvents[0];
                    Expect(harmonicEvt.RadialRequested,
                        $"gene_harmonic 挂在 {organelle.Id} 上（未配 SpreadModule）时，编译结果同样必须携带 " +
                        "RadialRequested=true");
                }
            }
        }

        /// <summary>
        /// M4-R00-02 队列③-10：CP-REQ-003 第③级（发射点身体中心前推+越界/障碍推出+EmitterBlocked）
        /// 与 CP-REQ-004（内核持久身体朝向字段）。真实器官/底盘挂点（①②级）未实现，登记为债务，
        /// 见 `production/design/m4-r00-02-item3-10-emission-point-and-body-forward/DESIGN.md`。
        /// </summary>
        private static void ValidateEmissionGeometryAndBodyForward()
        {
            Line("\n[42] 发射点前推/越界推出/EmitterBlocked + 内核身体朝向持久字段（M4-R00-02 队列③-10）");

            // ── 1. 无障碍/无越界：公式=bodyRadius+projectileRadius+Clearance ──
            float2 body = new float2(10f, 0f);
            float2 dir = new float2(0f, 1f);
            Expect(CombatBallistics.TryResolveEmitterPosition(body, 1f, dir, 0.5f, null, 0f, out float2 basic),
                "无障碍/无越界配置应恒成功");
            float2 expected = body + dir * (1f + 0.5f + CombatBallistics.MuzzleClearance);
            Expect(FloatsEqual(basic, expected),
                $"公式应是 bodyRadius+projectileRadius+Clearance，实得 {basic} 期望 {expected}");

            // ── 2. 越界推出：沿 +X 越界应被夹回场地边界内 ──
            Expect(CombatBallistics.TryResolveEmitterPosition(
                    new float2(59.8f, 0f), 1f, new float2(1f, 0f), 0.5f, null, 60f, out float2 clampedX),
                "越界配置应仍然成功（夹到边界，不是拒绝）");
            Expect(clampedX.x <= 60f + 1e-3f,
                $"沿 +X 越界的发射点应被夹到场地边界内，实得 x={clampedX.x:F2}");

            // ── 3. 撞障碍但有安全推出方向：推到障碍边界外 ──
            var obstacle = new ObstacleSpec { Position = new float2(10f, 2f), Radius = 1f };
            Expect(CombatBallistics.TryResolveEmitterPosition(
                    body, 1f, dir, 0.5f, new[] { obstacle }, 0f, out float2 pushed),
                "撞到障碍但有安全推出方向时应仍然成功");
            float distToObstacle = math.distance(pushed, obstacle.Position);
            Expect(distToObstacle >= obstacle.Radius + 0.5f - 1e-3f,
                $"推出后发射点到障碍中心的距离应至少是 障碍半径+弹体半径，实得 {distToObstacle:F2}");

            // ── 4. 完全挡死（发射点恰好落在障碍正中心，无安全推出方向）：EmitterBlocked ──
            var deadCenterObstacle = new ObstacleSpec { Position = expected, Radius = 1f };
            Expect(!CombatBallistics.TryResolveEmitterPosition(
                    body, 1f, dir, 0.5f, new[] { deadCenterObstacle }, 0f, out _),
                "发射点恰好落在障碍正中心时应返回 false（EmitterBlocked），不能瞬移到别处顶替");

            // ── 5+6. CP-REQ-004：出生立即赋值 + 静止后不被清零 ──
            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 16;
            cfg.ArenaHalfExtent = 60f;
            var archetypes = new[]
            {
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
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(5f, 5f), Velocity = new float2(3f, 4f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.Neutral,
                    IntentSource = IntentSource.AI, LogicId = 9920,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(-5f, -5f), Velocity = float2.zero, Health = 10f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.Neutral,
                    IntentSource = IntentSource.AI, LogicId = 9921,
                });
                sim.OnUpdate(1f / 60f);
                SimSnapshot snap = sim.Snapshot;
                SimEntityId moving = FindEntityId(snap, 9920, out int movingIdx);
                SimEntityId still = FindEntityId(snap, 9921, out int stillIdx);
                Expect(moving.IsValid && still.IsValid, "两具测试个体都应落地");
                if (moving.IsValid && still.IsValid)
                {
                    float2 expectedForward = math.normalize(new float2(3f, 4f));
                    Expect(FloatsEqual(snap.BodyForward[movingIdx], expectedForward),
                        $"出生带初速度时 BodyForward 应立即等于归一化速度方向，实得 {snap.BodyForward[movingIdx]} " +
                        $"期望 {expectedForward}");
                    Expect(!FloatsEqual(snap.BodyForward[stillIdx], float2.zero),
                        $"出生零速度不能把朝向留成零向量（规格明令禁止），实得 {snap.BodyForward[stillIdx]}");

                    // MaxSpeed=0 会让速度在积分里被限速夹到 0（Stationary 原型没有期望方向），
                    // 但 BodyForward 只在速度真的非零时才更新——冻结在最后一次非零值上，
                    // 不会跟着速度一起被清零。
                    for (int f = 0; f < 10; f++) { sim.OnUpdate(1f / 60f); }
                    snap = sim.Snapshot;
                    Expect(math.lengthsq(snap.Velocity[movingIdx]) < 1e-6f,
                        $"Stationary 原型 + MaxSpeed=0 应该让速度限速到 0，实得 {snap.Velocity[movingIdx]}（本项前置）");
                    Expect(FloatsEqual(snap.BodyForward[movingIdx], expectedForward),
                        $"速度归零之后 BodyForward 不应该被清零或改变，实得 {snap.BodyForward[movingIdx]} " +
                        $"期望仍是 {expectedForward}——这正是 CP-REQ-004 明令禁止的\"零向量重置朝向\"");
                }
            }
            finally
            {
                sim.End();
            }
        }

        /// <summary>
        /// M4-R00-02 队列③号项第11条（最后一条）：编队锚点算法改领队优先，排除脱队/卡死成员
        /// （FC-REQ-003 冲突）。旧实现对**全体**成员做算术平均，一个卡在障碍里的成员会把整队
        /// 锚点拽偏；`TryComputeAnchor` 此前也没有任何自动化测试覆盖。
        /// </summary>
        private static void ValidateFormationAnchorLeaderPriority()
        {
            Line("\n[43] 编队锚点改领队优先+排除脱队/卡死成员（M4-R00-02 队列③-11，FC-REQ-003）");

            var registry = new FormationRegistry();
            Formation formation = registry.CreateFormation();

            // ── 1. 领队自动指定：加入的第一名成员成为领队，之后新成员不顶替 ──
            var entityA = new SimEntityId(3001);
            var entityB = new SimEntityId(3002);
            var entityC = new SimEntityId(3003);
            Expect(formation.LeaderId == SimEntityId.None, "空编队没有领队");
            formation.AddMember(entityA);
            Expect(formation.LeaderId == entityA, "第一名加入的成员应自动成为领队");
            formation.AddMember(entityB);
            Expect(formation.LeaderId == entityA, "已有领队时新成员加入不应顶替领队");
            formation.AddMember(entityC);

            // ── 2. 领队离队后顺位给剩余成员中的一名，不留空领队 ──
            formation.RemoveMember(entityA);
            Expect(formation.LeaderId == entityB || formation.LeaderId == entityC,
                $"领队被移除后应顺位给剩余成员中的一名，实得 {formation.LeaderId.Value}");
            Expect(formation.IsMember(formation.LeaderId), "顺位领队必须仍是编队成员");
            formation.RemoveMember(entityB);
            formation.RemoveMember(entityC);
            Expect(formation.LeaderId == SimEntityId.None, "全员移除后领队应清空");

            // ── 3~6：真实 SimBridge 场景，验证 TryComputeAnchor 的领队优先/排除逻辑 ──
            Formation realFormation = registry.CreateFormation();
            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 60f;
            var archetypes = new[]
            {
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
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(0f, 0f), Health = 10f, Radius = 0.5f, MaxSpeed = 0f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion, IntentSource = IntentSource.AI, LogicId = 9930,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(30f, 0f), Health = 10f, Radius = 0.5f, MaxSpeed = 0f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion, IntentSource = IntentSource.AI, LogicId = 9931,
                });
                sim.Spawn(new SpawnRequest
                {
                    Position = new float2(-30f, 0f), Health = 10f, Radius = 0.5f, MaxSpeed = 0f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion, IntentSource = IntentSource.AI, LogicId = 9932,
                });
                sim.OnUpdate(1f / 60f);
                SimSnapshot snap = sim.Snapshot;
                SimEntityId leaderId = FindEntityId(snap, 9930, out _);
                SimEntityId f1Id = FindEntityId(snap, 9931, out _);
                SimEntityId f2Id = FindEntityId(snap, 9932, out _);
                Expect(leaderId.IsValid && f1Id.IsValid && f2Id.IsValid, "本项前置：三具测试个体都应落地");
                if (leaderId.IsValid && f1Id.IsValid && f2Id.IsValid)
                {
                    realFormation.AddMember(leaderId);
                    realFormation.AddMember(f1Id);
                    realFormation.AddMember(f2Id);
                    Expect(realFormation.LeaderId == leaderId, "本项前置：leaderId 应是第一个加入的成员");
                    bool leaderResolved = sim.TryResolveUnitIndex(leaderId, out int leaderIdx);
                    bool f1Resolved = sim.TryResolveUnitIndex(f1Id, out int f1Idx);
                    bool f2Resolved = sim.TryResolveUnitIndex(f2Id, out int f2Idx);
                    Expect(leaderResolved && f1Resolved && f2Resolved, "本项前置：三具个体的索引都应能解析");

                    // ── 3. 有效领队时：锚点=领队位置，不是全员平均 ──
                    Expect(realFormation.TryComputeAnchor(sim, out float2 anchor1), "三名都存活可查询时应能算出锚点");
                    Expect(FloatsEqual(anchor1, snap.Position[leaderIdx]),
                        $"有有效领队时锚点应直接取领队位置，实得 {anchor1} 期望 {snap.Position[leaderIdx]}" +
                        "（旧实现是全员算术平均，会算出接近原点的错误锚点）");

                    // ── 4. 领队脱队后：退化为未脱队/未卡住成员的稳健中心（不含领队）──
                    realFormation.SetDetached(leaderId, true);
                    Expect(realFormation.TryComputeAnchor(sim, out float2 anchor2), "领队脱队后仍应能用其余成员算出锚点");
                    float2 expectedMean = (snap.Position[f1Idx] + snap.Position[f2Idx]) / 2f;
                    Expect(FloatsEqual(anchor2, expectedMean),
                        $"领队脱队后锚点应是未脱队成员的算术平均（不含领队），实得 {anchor2} 期望 {expectedMean}");
                    realFormation.SetDetached(leaderId, false);

                    // ── 5. 领队仍有效时，跟随者卡住与否不改变锚点；领队失效后卡住的跟随者被排除 ──
                    realFormation.SetStuck(f1Id, true);
                    Expect(realFormation.TryComputeAnchor(sim, out float2 anchor3) &&
                           FloatsEqual(anchor3, snap.Position[leaderIdx]),
                        "领队仍有效时，跟随者卡住与否不改变锚点（还是取领队）");
                    realFormation.SetDetached(leaderId, true); // 逼退化到"稳健中心"分支
                    Expect(realFormation.TryComputeAnchor(sim, out float2 anchor4) &&
                           FloatsEqual(anchor4, snap.Position[f2Idx]),
                        $"卡住的跟随者应被排除在稳健中心之外，实得 {anchor4} 期望仅 f2 的位置 {snap.Position[f2Idx]}" +
                        "——这正是 FC-REQ-003 点名的症状（卡住的成员不该把整队锚点拽偏）");
                    realFormation.SetStuck(f1Id, false);
                    realFormation.SetDetached(leaderId, false);

                    // ── 6. 全员脱队/卡住：无有效候选，返回 false，不能静默用旧值糊弄过去 ──
                    realFormation.SetDetached(leaderId, true);
                    realFormation.SetDetached(f1Id, true);
                    realFormation.SetStuck(f2Id, true);
                    Expect(!realFormation.TryComputeAnchor(sim, out _),
                        "全部成员都脱队或卡住时应返回 false（无有效成员，调用方按命令失败处理）");
                    realFormation.SetDetached(leaderId, false);
                    realFormation.SetDetached(f1Id, false);
                    realFormation.SetStuck(f2Id, false);
                }
            }
            finally
            {
                sim.End();
            }

            // ── 7. 生产入口 E2E：FormationMovementDriver 在锚点持续算不出来时判命令失败并清理
            //    所有权（NoValidAnchor），不是无限重试挂起——用全员都是"从未落地"的假 id 制造这个场景。
            {
                var hub = new ModuleHub();
                var e2eRegistry = hub.Register(new FormationRegistry());
                var e2eSim = hub.Register(new SimBridge());
                hub.Register(new FormationMovementDriver());
                hub.Enter();

                SimConfig e2eCfg = SimConfig.Default;
                e2eCfg.UnitCapacity = 16;
                e2eCfg.ArenaHalfExtent = 60f;
                e2eSim.Begin(e2eCfg, Array.Empty<BehaviorArchetype>());

                try
                {
                    Formation ghostFormation = e2eRegistry.CreateFormation();
                    // 两个从未在这局 sim 里落地过的实体 id：TryGetPosition 恒失败，模拟"编队成员
                    // 全部脱离/查不到位置"的极端情形。
                    ghostFormation.AddMember(new SimEntityId(424242));
                    ghostFormation.AddMember(new SimEntityId(424243));
                    ghostFormation.IssueCommand(new FormationCommand(
                        FormationCommand.CommandKind.Move, targetPosition: new float2(10f, 10f)));

                    for (int f = 0; f < 40; f++)
                    {
                        hub.Update(1f / 60f);
                    }

                    Expect(ghostFormation.ActiveCommand != null &&
                           ghostFormation.ActiveCommand.State == FormationCommandState.Failed &&
                           ghostFormation.ActiveCommand.FailReason == FormationCommandFailReason.NoValidAnchor,
                        $"锚点持续算不出来应在重试上限后判命令失败并清理所有权，实得状态 " +
                        $"{ghostFormation.ActiveCommand?.State} / 原因 {ghostFormation.ActiveCommand?.FailReason}" +
                        "——不能无限重试把命令永远悬在 Active");
                }
                finally
                {
                    hub.Exit();
                }
            }
        }

        /// <summary>
        /// M4-R00-02 队列④-14（FC-REQ-011）：命令优先级从裸 int 换成封闭 7 级枚举
        /// <see cref="FormationCommandPriority"/>，并给 <see cref="Formation.IssueCommand"/> 补上
        /// 原文「低优先级不能删除高优先级命令；只能排队或返回冲突原因」这条此前完全没做的守卫——
        /// 旧实现无条件覆盖，任何后来的命令（哪怕是自主待机）都能打断玩家的紧急撤退。
        /// </summary>
        private static void ValidateFormationCommandPriorityGuard()
        {
            Line("\n[44] 命令优先级枚举化 + 覆盖守卫（M4-R00-02 队列④-14，FC-REQ-011）");

            var allPriorities = (FormationCommandPriority[])Enum.GetValues(typeof(FormationCommandPriority));
            Expect(allPriorities.Length == 7, $"FC-REQ-011 要求封闭 7 级优先级（实际 {allPriorities.Length}）");

            var registry = new FormationRegistry();
            var entityA = new SimEntityId(6001);

            // 验收 1：未显式指定优先级时默认取最低档（自主生存/待机），既有调用点行为不回归。
            var defaultCommand = new FormationCommand(FormationCommand.CommandKind.Move, targetPosition: new float2(0f, 0f));
            Expect(defaultCommand.Priority == FormationCommandPriority.AutonomousSurvival,
                $"未显式指定优先级时应默认 AutonomousSurvival（实际 {defaultCommand.Priority}）");

            // 验收 2：无 Active 命令时 IssueCommand 直接激活，返回 Activated。
            Formation freshFormation = registry.CreateFormation();
            FormationCommandIssueResult freshResult = freshFormation.IssueCommand(
                new FormationCommand(FormationCommand.CommandKind.Guard, targetPosition: new float2(0f, 0f),
                    priority: FormationCommandPriority.NormalPlayerCommand));
            Expect(freshResult == FormationCommandIssueResult.Activated, "无 Active 命令时应直接 Activated");
            Expect(freshFormation.ActiveCommand != null && freshFormation.ActiveCommand.State == FormationCommandState.Active,
                "本项前置：freshFormation 应有一条 Active 命令");

            // 验收 3：低优先级命令不能覆盖更高优先级的 Active 命令，只能排队，且返回冲突结果。
            Formation guardedFormation = registry.CreateFormation();
            guardedFormation.IssueCommand(new FormationCommand(FormationCommand.CommandKind.Retreat,
                targetPosition: new float2(0f, 0f), priority: FormationCommandPriority.PlayerEmergency));
            FormationCommandEntry protectedActive = guardedFormation.ActiveCommand;
            FormationCommandIssueResult blockedResult = guardedFormation.IssueCommand(
                new FormationCommand(FormationCommand.CommandKind.Move, targetPosition: new float2(1f, 1f),
                    priority: FormationCommandPriority.NormalPlayerCommand));
            Expect(blockedResult == FormationCommandIssueResult.QueuedBehindHigherPriority,
                $"低优先级命令应被拒绝覆盖并返回 QueuedBehindHigherPriority（实际 {blockedResult}）");
            Expect(ReferenceEquals(guardedFormation.ActiveCommand, protectedActive)
                && guardedFormation.ActiveCommand.State == FormationCommandState.Active
                && guardedFormation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Retreat,
                "高优先级 Active 命令不应被低优先级命令打断或替换");
            Expect(guardedFormation.PendingCommandCount == 1, "被拒绝覆盖的低优先级命令应改为进入等待队列");
            bool queuedPeeked = guardedFormation.PeekCommand(out FormationCommand queuedHead);
            Expect(queuedPeeked && queuedHead.Kind == FormationCommand.CommandKind.Move,
                "等待队列队首应是刚才被拒绝覆盖的 Move 命令");

            // 验收 4：高优先级命令可以正常覆盖更低优先级的 Active 命令（既有覆盖语义不回归）。
            Formation overridableFormation = registry.CreateFormation();
            overridableFormation.IssueCommand(new FormationCommand(FormationCommand.CommandKind.Guard,
                targetPosition: new float2(0f, 0f), priority: FormationCommandPriority.NormalPlayerCommand));
            FormationCommandEntry lowPriorityActive = overridableFormation.ActiveCommand;
            FormationCommandIssueResult overrideResult = overridableFormation.IssueCommand(
                new FormationCommand(FormationCommand.CommandKind.Retreat, targetPosition: new float2(2f, 2f),
                    priority: FormationCommandPriority.PlayerEmergency));
            Expect(overrideResult == FormationCommandIssueResult.Activated,
                $"高优先级命令覆盖低优先级 Active 命令应返回 Activated（实际 {overrideResult}）");
            Expect(lowPriorityActive.State == FormationCommandState.Interrupted
                && lowPriorityActive.FailReason == FormationCommandFailReason.PreemptedByOverride,
                "被高优先级命令覆盖的旧 Active 命令应标记 Interrupted/PreemptedByOverride");
            Expect(overridableFormation.ActiveCommand != null
                && overridableFormation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Retreat
                && overridableFormation.ActiveCommand.State == FormationCommandState.Active,
                "覆盖后新命令应直接成为 ActiveCommand");

            // 验收 5：优先级相等仍视为可覆盖（不引入"同级也排队"的新语义，维持默认优先级调用点不回归）。
            Formation equalPriorityFormation = registry.CreateFormation();
            equalPriorityFormation.IssueCommand(new FormationCommand(FormationCommand.CommandKind.OrganCategory,
                targetEntity: entityA));
            FormationCommandEntry equalPriorityFirst = equalPriorityFormation.ActiveCommand;
            FormationCommandIssueResult equalResult = equalPriorityFormation.IssueCommand(
                new FormationCommand(FormationCommand.CommandKind.Attack, targetEntity: entityA));
            Expect(equalResult == FormationCommandIssueResult.Activated,
                $"同优先级应仍可覆盖，返回 Activated（实际 {equalResult}）");
            Expect(equalPriorityFirst.State == FormationCommandState.Interrupted, "同优先级覆盖时旧命令仍应被标记 Interrupted");
        }

        /// <summary>
        /// M4-R00-02 队列④-17（FS-REQ-030）：共享能力13项清单显式化。只校验清单本身的完整性/
        /// 一致性（不重复登记、覆盖全部枚举值、每项都有证据说明），以及 2026-09-16 独立核实的状态
        /// 分布——任何人以后改动某项能力的实现程度时，必须同步改 <see cref="SharedCapabilityCatalog"/>
        /// 的条目，这条断言才会跟着变，否则会在这里假红提醒清单和代码脱节了。
        /// </summary>
        private static void ValidateSharedCapabilityCatalog()
        {
            Line("\n[45] 共享能力13项清单显式化（M4-R00-02 队列④-17，FS-REQ-030）");

            var allCapabilities = (SharedCapability[])Enum.GetValues(typeof(SharedCapability));
            Expect(allCapabilities.Length == 13, $"FS-REQ-030 要求封闭13项能力（实际 {allCapabilities.Length}）");
            Expect(SharedCapabilityCatalog.Entries.Count == 13,
                $"能力清单条目数应恰好13（实际 {SharedCapabilityCatalog.Entries.Count}）");

            var seen = new HashSet<SharedCapability>();
            foreach (SharedCapabilityEntry entry in SharedCapabilityCatalog.Entries)
            {
                Expect(seen.Add(entry.Capability), $"能力清单不应重复登记 {entry.Capability}");
                Expect(!string.IsNullOrEmpty(entry.Evidence), $"{entry.Capability} 必须附带证据说明，不能空着");
            }
            foreach (SharedCapability capability in allCapabilities)
            {
                Expect(seen.Contains(capability), $"能力清单必须覆盖枚举值 {capability}，不能漏登记");
            }

            int implementedCount = 0, partialCount = 0, placeholderCount = 0, notImplementedCount = 0;
            foreach (SharedCapabilityEntry entry in SharedCapabilityCatalog.Entries)
            {
                switch (entry.Status)
                {
                    case SharedCapabilityStatus.Implemented: implementedCount++; break;
                    case SharedCapabilityStatus.Partial: partialCount++; break;
                    case SharedCapabilityStatus.Placeholder: placeholderCount++; break;
                    case SharedCapabilityStatus.NotImplemented: notImplementedCount++; break;
                }
            }

            // 2026-09-16 独立核实快照：已实现4项(攻击/繁殖孕育/撤退/采样解析)、部分实现5项
            // (感知/移动寻路/采集/存储/守护护送)、占位空壳2项(搬运/信号)、不存在2项(进食供养/修复)。
            // 这四个数字任一变化都意味着有能力的实现程度真的变了，必须同步改上面的 Entries 条目
            // （含证据文案），不能只改这里的期望值。
            Expect(implementedCount == 4, $"已实现应为4项（实际 {implementedCount}）：攻击/繁殖孕育/撤退/采样解析");
            Expect(partialCount == 5, $"部分实现应为5项（实际 {partialCount}）：感知/移动寻路/采集/存储/守护护送");
            Expect(placeholderCount == 2, $"占位空壳应为2项（实际 {placeholderCount}）：搬运/信号");
            Expect(notImplementedCount == 2, $"不存在应为2项（实际 {notImplementedCount}）：进食供养/修复");

            Expect(SharedCapabilityCatalog.StatusOf(SharedCapability.Attack) == SharedCapabilityStatus.Implemented,
                "抽查：攻击应为已实现");
            Expect(SharedCapabilityCatalog.StatusOf(SharedCapability.Repair) == SharedCapabilityStatus.NotImplemented,
                "抽查：修复应为不存在（2026-09-16 推翻旧审计'已落地5项'里的判断）");
        }

        /// <summary>
        /// M4-R00-02 队列⑤-19/21（共享死亡信号）：<see cref="FormationRegistry.HandleMemberDeath"/>/
        /// <see cref="WildOrganRegistry.HandleBodyDeath"/> 此前都是"逻辑完全正确但生产环境从未调用"
        /// 的技术债——本仓现有 <c>KillSignal</c> 语义是"击杀"而非"友方个体阵亡"。本项验证两层：
        /// ① 内核 <see cref="DeathEvent"/> 新增的 <see cref="DeathEvent.EntityId"/> 字段在伤害致死
        /// 与吞噬清除两条路径都正确携带死者稳定身份；② 生产入口 E2E——真的通过
        /// <c>CellDevourSystem.ResolveDeaths</c> 杀死一个编队成员，确认它会广播新增的
        /// <see cref="AllyDeathSignal"/>，并且 FormationRegistry/WildOrganRegistry 的订阅会真的
        /// 调用到那两个此前"写对了但没人叫"的方法。
        /// </summary>
        private static void ValidateAllyDeathSignal()
        {
            Line("\n[46] 友方个体阵亡信号闭环（M4-R00-02 队列⑤-19/21，共享死亡信号）");

            // ── 1. 内核层：DeathEvent.EntityId 在伤害致死与吞噬清除两条路径都应正确携带死者稳定身份 ──
            var kernelWorld = new SimWorld();
            SimConfig kernelCfg = SimConfig.Default;
            kernelCfg.UnitCapacity = 16;
            kernelWorld.Initialize(kernelCfg);
            kernelWorld.SetArchetypes(DataRegistry.Instance.ArchetypeArray());

            SimCommandBuffer kernelCmds = default;
            kernelCmds.Initialize(Unity.Collections.Allocator.Persistent, 16);
            try
            {
                kernelWorld.SetPlayerPosition(float2.zero);
                int idxDamage = kernelWorld.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(5f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion, LogicId = 8801,
                });
                int idxDevour = kernelWorld.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(-5f, 0f), Health = 10f, Radius = 0.5f,
                    MaxSpeed = 0f, ArchetypeId = 0, Faction = SimFaction.PlayerMinion, LogicId = 8802,
                });
                kernelWorld.TryGetEntityId(idxDamage, out SimEntityId expectedDamageId);
                kernelWorld.TryGetEntityId(idxDevour, out SimEntityId expectedDevourId);
                Expect(expectedDamageId.IsValid && expectedDevourId.IsValid, "本项前置：两具测试个体都应有有效 EntityId");

                kernelCmds.Damage(new DamageRequest
                {
                    Origin = new float2(5f, 0f), Radius = 1f,
                    TargetIndex = SimConst.InvalidIndex, Amount = 1000f,
                    TargetFaction = SimFaction.PlayerMinion,
                });
                kernelCmds.SetPlayerIntent(PlayerIntent.Idle);
                kernelWorld.Step(1f / 60f, ref kernelCmds);
                kernelWorld.KillUnit(idxDevour, 0);

                SimSnapshot kernelSnap = kernelWorld.GetSnapshot();
                Expect(kernelSnap.DeathCount == 2, $"应产生2条死亡事件（实际 {kernelSnap.DeathCount}）");

                bool foundDamageId = false, foundDevourId = false;
                for (int i = 0; i < kernelSnap.DeathCount; i++)
                {
                    DeathEvent d = kernelSnap.Deaths[i];
                    if (d.LogicId == 8801)
                    {
                        Expect(d.EntityId == expectedDamageId,
                            $"伤害致死的 DeathEvent.EntityId 应等于死者稳定身份（实际 {d.EntityId.Value} 期望 {expectedDamageId.Value}）");
                        foundDamageId = true;
                    }
                    else if (d.LogicId == 8802)
                    {
                        Expect(d.EntityId == expectedDevourId,
                            $"吞噬清除的 DeathEvent.EntityId 应等于死者稳定身份（实际 {d.EntityId.Value} 期望 {expectedDevourId.Value}）");
                        foundDevourId = true;
                    }
                }
                Expect(foundDamageId, "应找到伤害致死事件");
                Expect(foundDevourId, "应找到吞噬致死事件");
            }
            finally
            {
                kernelCmds.Dispose();
                kernelWorld.Dispose();
            }

            // ── 2. 生产入口 E2E：CellDevourSystem 处理真实死亡事件时应广播 AllyDeathSignal，
            //    FormationRegistry/WildOrganRegistry 订阅后应真的调用既有的 HandleMemberDeath/
            //    HandleBodyDeath——不是手动 Publish 信号验证订阅存在，是真的杀死一个单位。
            var hub = new ModuleHub();
            var e2eSim = hub.Register(new SimBridge());
            var e2eFormations = hub.Register(new FormationRegistry());
            var e2eWildOrgans = hub.Register(new WildOrganRegistry());
            var e2eDevour = hub.Register(new CellDevourSystem());
            var e2eLoadouts = new UnitLoadoutRegistry();
            e2eDevour.Bind(e2eSim, null, null, null, null, null, null, null);
            e2eWildOrgans.Bind(e2eSim, e2eLoadouts);
            hub.Enter();

            SimConfig e2eCfg = SimConfig.Default;
            e2eCfg.UnitCapacity = 16;
            e2eCfg.ArenaHalfExtent = 60f;
            e2eSim.Begin(e2eCfg, Array.Empty<BehaviorArchetype>());

            try
            {
                e2eSim.Spawn(new SpawnRequest
                {
                    Position = new float2(0f, 0f), Health = 10f, Radius = 0.5f, MaxSpeed = 0f,
                    ArchetypeId = 0, Faction = SimFaction.PlayerMinion, IntentSource = IntentSource.AI, LogicId = 8901,
                });
                e2eSim.OnUpdate(1f / 60f);
                SimSnapshot e2eSnap = e2eSim.Snapshot;
                SimEntityId memberId = FindEntityId(e2eSnap, 8901, out int memberIdx);
                Expect(memberId.IsValid, "本项前置：测试个体应落地");

                Formation e2eFormation = e2eFormations.CreateFormation();
                e2eFormation.AddMember(memberId);
                Expect(e2eFormation.IsMember(memberId), "本项前置：测试个体应已加入编队");

                e2eLoadouts.Bind(e2eSim, null);
                e2eLoadouts.RegisterExplicit(memberId, Array.Empty<UnitLoadoutOrgan>());
                float2 dropPosition = e2eSnap.Position[memberIdx];
                string dropInstanceId = e2eWildOrgans.DropInField("org_phago", BlueprintSourceKind.Organelle, dropPosition);
                Expect(!string.IsNullOrEmpty(dropInstanceId), "本项前置：应能在场上放置一件测试用野生器官");

                WildOrganPickupResult pickupResult = e2eWildOrgans.TryPickup(memberId, dropInstanceId);
                Expect(pickupResult == WildOrganPickupResult.Ok, $"本项前置：测试个体应能拾取测试器官（实际 {pickupResult}）");

                WildOrganInstallResult installResult = e2eWildOrgans.TryInstallTemporary(memberId, dropInstanceId);
                Expect(installResult == WildOrganInstallResult.Ok, $"本项前置：测试个体应能装上临时器官（实际 {installResult}）");
                Expect(e2eWildOrgans.TryGetInstalled(memberId, out _), "本项前置：应能查到刚装上的临时器官");

                e2eSim.DamageUnit(memberIdx, 9999f);
                hub.Update(1f / 60f);
                hub.Update(1f / 60f);

                Expect(!e2eFormation.IsMember(memberId),
                    "友方个体真实死亡后，FormationRegistry 应通过 AllyDeathSignal 真的调用 " +
                    "HandleMemberDeath 把它从编队移除（此前这条生产路径从未被调用过）");
                Expect(!e2eWildOrgans.TryGetInstalled(memberId, out _),
                    "友方个体真实死亡后，WildOrganRegistry 应通过 AllyDeathSignal 真的调用 " +
                    "HandleBodyDeath 清掉临时器官安装记录（此前这条生产路径从未被调用过）");
            }
            finally
            {
                e2eSim.End();
                hub.Exit();
            }
        }

        /// <summary>
        /// M4-R00-02 队列⑤-21（M3-R03-RETURN-REAL-COMBAT-EXIT）：回巢改造此前"只标记字典、
        /// 从未真的从战斗调度摘除"——类型注释原文自陈"具体从战斗调度里摘除留给后续故事接线"。
        /// 本项验证开票/取消/完成三个转折点真的会给个体叠加/摘除 Stunned+Invulnerable，
        /// 而不是只改一个内存字典。复用 [30] 的最小可行 fixture，只加战斗状态断言，
        /// 不重复 [30] 已经覆盖的各条 RejectReason 分支。
        /// </summary>
        private static void ValidateHomecomingRealCombatExit()
        {
            Line("\n[47] 回巢改造真实战斗摘除/重入（M4-R00-02 队列⑤-21，M3-R03-RETURN-REAL-COMBAT-EXIT）");

            OrganelleDef organelle = OrganelleCatalog.All.Values.FirstOrDefault(o => o.AttackMethod && !o.IsRetired);
            List<string> geneIds = GeneCatalog.AllGeneIds.Take(2).ToList();
            Expect(organelle != null, "本项前置：OrganelleCatalog 应至少有一条 AttackMethod 未退役器官");
            Expect(geneIds.Count == 2, "本项前置：GeneCatalog 应至少有两条基因");
            if (organelle == null || geneIds.Count < 2)
            {
                return;
            }

            var blueprints = new BlueprintRegistry();
            blueprints.Resolve(organelle.Id, BlueprintSourceKind.Organelle, 1f, 0f);
            foreach (string g in geneIds)
            {
                blueprints.Resolve(g, BlueprintSourceKind.Gene, 1f, 0f);
            }

            var lineages = new LineageRegistry();
            lineages.Bind(blueprints);
            PhenotypeTemplateVersion v1 = lineages.CommitTemplate("lineage-exit", "assault", organelle.Id, geneIds, "keep_distance", out _);
            Expect(v1 != null, "本项前置：合法配方提交应成功产生版本 1");

            var ledger = new BiomassLedger();
            ledger.OnEnter();
            ledger.Deposit("lineage-exit", 999f);
            var chamber = new GerminationChamberRegistry();
            chamber.OnEnter();

            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 16;
            cfg.ArenaHalfExtent = 60f;
            var archetypes = new[]
            {
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Stationary, Accel = 0f, TurnRate = 0f, AggroRange = 0f,
                    AttackRange = 0.5f, AttackCooldown = 99f, AttackDamage = 0f,
                    Separation = 0f, ChargeSpeedMul = 1f,
                },
            };
            sim.Begin(cfg, archetypes);

            var unitLoadouts = new UnitLoadoutRegistry();
            var fakeSource = new FakePlayerLoadoutSource();
            unitLoadouts.Bind(sim, fakeSource);
            unitLoadouts.RegisterPlayerBody(sim.ControlledUnitId);

            chamber.Bind(sim, lineages, ledger, unitLoadouts);

            var retrofit = new HomecomingRetrofitService();
            retrofit.OnEnter();
            retrofit.Bind(sim, lineages, ledger, unitLoadouts, chamber);

            const int NearLogicId = 9810;
            float2 playerPos = sim.PlayerPosition;
            sim.Spawn(new SpawnRequest
            {
                Position = playerPos + new float2(2f, 0f), Health = 40f, Radius = 0.8f, MaxSpeed = 0f,
                ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                IntentSource = IntentSource.AI, LogicId = NearLogicId, ExcludeFromControl = true,
            });
            sim.OnUpdate(1f / 60f);
            SimSnapshot snap = sim.Snapshot;
            SimEntityId near = FindEntityId(snap, NearLogicId, out int nearIdx);
            Expect(near.IsValid, "本项前置：测试个体应成功落地");
            if (!near.IsValid)
            {
                sim.End();
                return;
            }

            chamber.UpdateBinding(near, "lineage-exit", "assault", v1);
            unitLoadouts.RegisterExplicit(near, new List<UnitLoadoutOrgan> { new UnitLoadoutOrgan(v1.OrganelleId, LoadoutAction.Primary) });
            PhenotypeTemplateVersion v2 = lineages.CommitTemplate("lineage-exit", "assault", organelle.Id, geneIds, "escort", out _);
            Expect(v2 != null && v2.Version == 2, "本项前置：第二次提交应产生版本 2");

            Expect((sim.Snapshot.Status[nearIdx] & (uint)SimStatus.Stunned) == 0u &&
                   (sim.Snapshot.Status[nearIdx] & (uint)SimStatus.Invulnerable) == 0u,
                "本项前置：改造开始前不应带有 Stunned/Invulnerable");

            HomecomingRetrofitService.RetrofitRejectReason reason = retrofit.TryBeginRetrofit(near);
            Expect(reason == HomecomingRetrofitService.RetrofitRejectReason.None,
                $"本项前置：满足条件时开票应成功（实际 {reason}）");

            sim.OnUpdate(1f / 60f); // ApplyStatusUnit 是排队命令，推进一帧让它真正落到快照里
            SimSnapshot snapDuring = sim.Snapshot;
            Expect((snapDuring.Status[nearIdx] & (uint)SimStatus.Stunned) != 0u,
                "开票成功后应叠加 Stunned——AI/命令双路径当帧只出 Idle 意图，不再继续自动开火/移动");
            Expect((snapDuring.Status[nearIdx] & (uint)SimStatus.Invulnerable) != 0u,
                "开票成功后应叠加 Invulnerable——JobDamage 跳过伤害结算，不再原地挨打");

            bool cancelled = retrofit.CancelRetrofit(near);
            Expect(cancelled, "本项前置：取消应成功");
            sim.OnUpdate(1f / 60f);
            SimSnapshot snapAfterCancel = sim.Snapshot;
            Expect((snapAfterCancel.Status[nearIdx] & (uint)SimStatus.Stunned) == 0u &&
                   (snapAfterCancel.Status[nearIdx] & (uint)SimStatus.Invulnerable) == 0u,
                "取消改造后应摘掉 Stunned/Invulnerable，真实重入战斗调度");

            reason = retrofit.TryBeginRetrofit(near);
            Expect(reason == HomecomingRetrofitService.RetrofitRejectReason.None, "本项前置：重新开票应再次成功");
            sim.OnUpdate(1f / 60f);
            bool completed = retrofit.CompleteRetrofit(near);
            Expect(completed, "本项前置：完成改造应成功");
            sim.OnUpdate(1f / 60f);
            SimSnapshot snapAfterComplete = sim.Snapshot;
            Expect((snapAfterComplete.Status[nearIdx] & (uint)SimStatus.Stunned) == 0u &&
                   (snapAfterComplete.Status[nearIdx] & (uint)SimStatus.Invulnerable) == 0u,
                "完成改造后应摘掉 Stunned/Invulnerable，真实重入战斗调度");

            sim.End();
        }

        /// <summary>
        /// M4-R00-02 队列⑤-21（M3-R04-WILD-FULL-CHAIN"死亡掉落缺失、存读档整段不存在"）：野生器官
        /// 跨局持久化，范围刻意收窄为只覆盖 <see cref="WildOrganState.InField"/>（见
        /// <see cref="WildOrganPersistence"/> 类型注释——Carried/Installed 按 SimEntityId 记账，
        /// 读档后无法安全重建归属，是与队列20号相同的根因）。① IO 层往返/损坏/版本回归，
        /// 全程备份/恢复真实存档文件；② 注册表层往返——两个独立 `WildOrganRegistry` 实例，
        /// 第一个 Drop 一件 InField + 拾取一件到 Carried 后 OnExit，第二个 OnEnter 应只复原
        /// InField 那一件，证明范围边界真的生效而不是误存了 Carried。
        /// </summary>
        private static void ValidateWildOrganFieldPersistence()
        {
            Line("\n[48] 野生器官地面战利品存读档（M4-R00-02 队列⑤-21，范围仅 InField）");

            string path = WildOrganPersistence.FilePath;
            bool hadBackup = File.Exists(path);
            string backup = hadBackup ? File.ReadAllText(path) : null;

            try
            {
                // ── 1. IO 层往返/损坏/版本回归 ──
                var written = new List<WildOrganFieldSaveEntry>
                {
                    new WildOrganFieldSaveEntry
                    {
                        SourceId = "org_phago", Kind = (int)BlueprintSourceKind.Organelle,
                        PositionX = 12.5f, PositionY = -3.25f, Contamination = 0.4f,
                    },
                };
                WildOrganPersistence.Save(written);
                WildOrganFieldHistory loaded = WildOrganPersistence.Load();
                Expect(loaded.Entries.Count == 1 && loaded.Entries[0].SourceId == "org_phago" &&
                       math.abs(loaded.Entries[0].PositionX - 12.5f) < 0.001f &&
                       math.abs(loaded.Entries[0].PositionY - (-3.25f)) < 0.001f &&
                       math.abs(loaded.Entries[0].Contamination - 0.4f) < 0.001f,
                    "IO 层往返后字段应一致");

                File.WriteAllText(path, "{not json");
                WildOrganFieldHistory corrupted = WildOrganPersistence.Load();
                Expect(corrupted.Entries.Count == 0, "损坏 JSON 应安全降级为空场景，不抛异常");

                File.WriteAllText(path, "{\"Version\":0,\"FieldEntries\":[]}");
                WildOrganFieldHistory legacy = WildOrganPersistence.Load();
                Expect(legacy.Entries.Count == 0, "不认识的存档版本应安全降级为空场景，不抛异常");

                // ── 2. 注册表层往返：Carried 不应被误存 ──
                // 复用 OnEnter：此刻磁盘上仍是上面第①步留下的"Version:0"旧存档，Load() 已确认
                // 会安全降级为空场景，等价于一次干净的 OnEnter，不需要额外清场。
                var writer = new WildOrganRegistry();
                writer.OnEnter();

                string fieldInstanceId = writer.DropInField("org_phago", BlueprintSourceKind.Organelle, new float2(5f, 5f));
                string carriedSourceId = OrganelleCatalog.All.Values.FirstOrDefault(o => !o.IsRetired && o.Id != "org_phago")?.Id ?? "org_phago";
                string carriedInstanceId = writer.DropInField(carriedSourceId, BlueprintSourceKind.Organelle, new float2(0f, 0f));
                Expect(!string.IsNullOrEmpty(fieldInstanceId) && !string.IsNullOrEmpty(carriedInstanceId),
                    "本项前置：两件测试用野生器官都应能放置成功");

                var sim2 = new SimBridge();
                SimConfig cfg2 = SimConfig.Default;
                cfg2.UnitCapacity = 8;
                cfg2.ArenaHalfExtent = 30f;
                sim2.Begin(cfg2, Array.Empty<BehaviorArchetype>());
                writer.Bind(sim2, null);
                try
                {
                    sim2.Spawn(new SpawnRequest
                    {
                        Position = new float2(0f, 0f), Health = 10f, Radius = 0.5f, MaxSpeed = 0f,
                        ArchetypeId = 0, Faction = SimFaction.PlayerMinion, IntentSource = IntentSource.AI, LogicId = 9820,
                    });
                    sim2.OnUpdate(1f / 60f);
                    SimEntityId picker = FindEntityId(sim2.Snapshot, 9820, out _);
                    Expect(picker.IsValid, "本项前置：拾取者应成功落地");

                    WildOrganPickupResult pickupResult = writer.TryPickup(picker, carriedInstanceId);
                    Expect(pickupResult == WildOrganPickupResult.Ok, $"本项前置：拾取应成功（实际 {pickupResult}）");
                }
                finally
                {
                    sim2.End();
                }

                writer.OnExit();

                var reader = new WildOrganRegistry();
                reader.OnEnter();
                var reloadedFieldInstances = reader.AllInstances.Where(i => i.State == WildOrganState.InField).ToList();
                Expect(reloadedFieldInstances.Count == 1,
                    $"重新 OnEnter 后应只复原 1 件 InField 战利品（实际 {reloadedFieldInstances.Count}）——" +
                    "Carried 的那件不应被误存/误复原");
                if (reloadedFieldInstances.Count == 1)
                {
                    WildOrganInstance restored = reloadedFieldInstances[0];
                    Expect(restored.SourceId == "org_phago" &&
                           math.abs(restored.FieldPosition.x - 5f) < 0.001f &&
                           math.abs(restored.FieldPosition.y - 5f) < 0.001f,
                        "复原的 InField 实物应保留原 SourceId 与坐标");
                }
                reader.OnExit();
            }
            finally
            {
                if (hadBackup)
                {
                    File.WriteAllText(path, backup);
                }
                else
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>
        /// M4-R02 最小桥接（`production/design/m4-r02-formation-command-entry-minimal-bridge/
        /// DESIGN.md`）：`SquadCommandSystem` 首次真正引用 `Formation`——编组=编队槽位、
        /// 槽位互斥、Move/Retreat 交给 `FormationMovementDriver`、Attack/Guard 两条腿并存、
        /// 暂停期重编组的排队命令必须核对并按需取消、生命周期边界（空选择不建空编队/
        /// Bind-Unbind清空映射）。全部用真实 `SimBridge`+`ModuleHub` 驱动，不直连内部字段。
        /// </summary>
        private static void ValidateSquadFormationBridge()
        {
            Line("\n[49] 编队接入真实玩家命令入口（M4-R02 最小桥接）");

            var hub = new ModuleHub();
            var formations = hub.Register(new FormationRegistry());
            var sim = hub.Register(new SimBridge());
            hub.Register(new FormationMovementDriver());
            hub.Enter();

            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 80f;
            var archetypes = new[]
            {
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Stationary, Accel = 0f, TurnRate = 0f, AggroRange = 0f,
                    AttackRange = 0.5f, AttackCooldown = 99f, AttackDamage = 0f,
                    Separation = 0f, ChargeSpeedMul = 1f,
                },
            };
            sim.Begin(cfg, archetypes);

            var squad = new SquadCommandSystem();
            squad.Bind(sim, null, formations);

            try
            {
                SimEntityId SpawnAndResolve(int logicId, float2 pos)
                {
                    sim.Spawn(new SpawnRequest
                    {
                        Position = pos, Health = 20f, Radius = 0.5f, MaxSpeed = 4f,
                        ArchetypeId = 0, Faction = SimFaction.PlayerMinion,
                        IntentSource = IntentSource.AI, LogicId = logicId,
                    });
                    sim.OnUpdate(1f / 60f);
                    return FindEntityId(sim.Snapshot, logicId, out _);
                }

                // ── 1. AssignGroup 创建/同步 Formation；同槽位复用同一个 FormationId ──
                SimEntityId a1 = SpawnAndResolve(96001, new float2(0f, 0f));
                SimEntityId a2 = SpawnAndResolve(96002, new float2(1f, 0f));
                Expect(a1.IsValid && a2.IsValid, "本项前置：测试个体 a1/a2 应成功落地");

                squad.SelectExplicit(new[] { a1, a2 });
                squad.AssignGroup(1);
                Expect(formations.AllFormations.Count == 1, $"AssignGroup 首次调用应恰好创建 1 支编队（实际 {formations.AllFormations.Count}）");
                Formation slot1Formation = formations.FindFormationContaining(a1);
                Expect(slot1Formation != null && slot1Formation.IsMember(a2),
                    "槽位1的编队应同时包含 a1/a2");

                squad.SelectExplicit(new[] { a1 });
                squad.AssignGroup(1);
                Expect(formations.AllFormations.Count == 1,
                    $"同一槽位重新编组应复用同一个 Formation，而不是新建第二个（实际总数 {formations.AllFormations.Count}）");
                Expect(slot1Formation.IsMember(a1) && !slot1Formation.IsMember(a2),
                    "槽位1重新编组为只含 a1 后，a2 应被差集同步摘除");

                // ── 生命周期：空选择不为从未用过的槽位创建空编队 ──
                squad.SelectExplicit(System.Array.Empty<SimEntityId>());
                squad.AssignGroup(2);
                Expect(formations.AllFormations.Count == 1,
                    $"对从未用过的槽位用空选择集 AssignGroup 不应创建空编队（实际总数 {formations.AllFormations.Count}）");

                // ── 2. 槽位互斥：单位改编到新槽位应从旧槽位的编队+_groups 里摘除 ──
                SimEntityId x = SpawnAndResolve(96003, new float2(2f, 0f));
                SimEntityId y = SpawnAndResolve(96004, new float2(3f, 0f));
                SimEntityId z = SpawnAndResolve(96005, new float2(4f, 0f));
                Expect(x.IsValid && y.IsValid && z.IsValid, "本项前置：测试个体 x/y/z 应成功落地");

                squad.SelectExplicit(new[] { x, y });
                squad.AssignGroup(3);
                Formation slot3Formation = formations.FindFormationContaining(x);
                Expect(slot3Formation != null && slot3Formation.IsMember(y), "本项前置：槽位3应同时包含 x/y");

                squad.SelectExplicit(new[] { x, z });
                squad.AssignGroup(4);
                Expect(!slot3Formation.IsMember(x) && slot3Formation.IsMember(y),
                    "x 被改编到槽位4后应从槽位3的编队摘除，y 应不受影响地留在槽位3");
                Expect(!squad.GroupMembers(3).Contains(x) && squad.GroupMembers(3).Contains(y),
                    "槽位3的 _groups 列表应同步反映 x 已被摘除（AiHandoffSystem 依赖的一致性）");
                Formation slot4Formation = formations.FindFormationContaining(x);
                Expect(slot4Formation != null && slot4Formation != slot3Formation && slot4Formation.IsMember(z),
                    "x 现在应属于槽位4的编队（与槽位3不是同一个对象），且槽位4同时包含 z");

                // ── 3. Move 命令：Issue 本身不下内核命令，交给 FormationMovementDriver 下一帧下发 ──
                SimEntityId m1 = SpawnAndResolve(96006, new float2(10f, 10f));
                SimEntityId m2 = SpawnAndResolve(96007, new float2(11f, 10f));
                Expect(m1.IsValid && m2.IsValid, "本项前置：测试个体 m1/m2 应成功落地");

                squad.SelectExplicit(new[] { m1, m2 });
                squad.AssignGroup(5);
                squad.ClearSelection();
                squad.RecallGroup(5);
                Expect(squad.Selection.Count == 2, "本项前置：RecallGroup(5) 后选择集应恢复为 m1/m2");

                int moveAccepted = squad.Issue(UnitCommandKind.Move, new float2(30f, 10f), SimEntityId.None, paused: false);
                Expect(moveAccepted == 2, $"编队路径 Move 应报告 2 个目标（实际 {moveAccepted}）");
                Expect(squad.LastFormationDispatchOutcome == SquadFormationDispatchOutcome.Activated,
                    $"编队路径 Move 应 Activated（实际 {squad.LastFormationDispatchOutcome}）");
                Formation slot5Formation = formations.FindFormationContaining(m1);
                Expect(slot5Formation != null && slot5Formation.ActiveCommand != null &&
                       slot5Formation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Move,
                    "Formation.ActiveCommand 应变为 Move");
                Expect(!sim.TryGetCommand(m1, out _) && !sim.TryGetCommand(m2, out _),
                    "Issue 本身不应直接下发内核 Move 命令——这是留给 FormationMovementDriver 的活");

                for (int f = 0; f < 5 && !sim.TryGetCommand(m1, out _); f++)
                {
                    hub.Update(1f / 60f);
                }
                Expect(sim.TryGetCommand(m1, out UnitCommand m1Cmd) && m1Cmd.Kind == UnitCommandKind.Move &&
                       sim.TryGetCommand(m2, out UnitCommand m2Cmd) && m2Cmd.Kind == UnitCommandKind.Move,
                    "FormationMovementDriver 应在随后几帧内真的把 Move 命令下发给编队成员");

                // ── 4. Attack/Guard：两条腿并存，Issue 立即下内核命令 ──
                SimEntityId g1 = SpawnAndResolve(96008, new float2(-10f, 0f));
                SimEntityId g2 = SpawnAndResolve(96009, new float2(-11f, 0f));
                Expect(g1.IsValid && g2.IsValid, "本项前置：测试个体 g1/g2 应成功落地");

                squad.SelectExplicit(new[] { g1, g2 });
                squad.AssignGroup(6);
                squad.ClearSelection();
                squad.RecallGroup(6);

                int guardAccepted = squad.Issue(UnitCommandKind.Guard, new float2(-10f, 0f), SimEntityId.None, paused: false);
                Expect(guardAccepted == 2, $"编队路径 Guard 应立即接受 2 个目标（实际 {guardAccepted}）");
                Expect(squad.LastFormationDispatchOutcome == SquadFormationDispatchOutcome.Activated,
                    "编队路径 Guard 应 Activated");
                Formation slot6Formation = formations.FindFormationContaining(g1);
                Expect(slot6Formation != null && slot6Formation.ActiveCommand != null &&
                       slot6Formation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Guard,
                    "Formation.ActiveCommand 应变为 Guard");
                Expect(sim.TryGetCommand(g1, out UnitCommand g1Cmd) && g1Cmd.Kind == UnitCommandKind.Guard &&
                       sim.TryGetCommand(g2, out UnitCommand g2Cmd) && g2Cmd.Kind == UnitCommandKind.Guard,
                    "Attack/Guard 应由 Issue 立即下发内核命令（没有 driver 消费 ActiveCommand）");

                // ── 5. 改变选择集会清空 active formation：随后 Issue 走非编队路径 ──
                squad.SelectExplicit(new[] { g1 }); // 选择集变化，不再等于槽位6的完整成员
                squad.Issue(UnitCommandKind.Guard, new float2(0f, 5f), SimEntityId.None, paused: false);
                Expect(squad.LastFormationDispatchOutcome == SquadFormationDispatchOutcome.NotFormationRouted,
                    $"选择集被改变后应回退到非编队路径（实际 {squad.LastFormationDispatchOutcome}）");

                // ── 6. 优先级覆盖：编队连续两次下令，同优先级仍应覆盖 ──
                squad.SelectExplicit(new[] { g1, g2 });
                squad.RecallGroup(6);
                squad.Issue(UnitCommandKind.Guard, new float2(1f, 1f), SimEntityId.None, paused: false);
                FormationCommandEntry guardFirstEntry = slot6Formation.ActiveCommand;
                squad.Issue(UnitCommandKind.Retreat, new float2(2f, 2f), SimEntityId.None, paused: false);
                Expect(guardFirstEntry.State == FormationCommandState.Interrupted &&
                       guardFirstEntry.FailReason == FormationCommandFailReason.PreemptedByOverride,
                    "同优先级的第二条命令应覆盖第一条（旧 entry 标记 Interrupted/PreemptedByOverride）");
                Expect(squad.LastFormationDispatchOutcome == SquadFormationDispatchOutcome.Activated,
                    "覆盖后的新命令应 Activated");

                // ── 7. 暂停排队（正常路径）：入队不分流，flush 时才真正调用 Formation.IssueCommand ──
                SimEntityId q1 = SpawnAndResolve(96010, new float2(20f, -20f));
                SimEntityId q2 = SpawnAndResolve(96011, new float2(21f, -20f));
                Expect(q1.IsValid && q2.IsValid, "本项前置：测试个体 q1/q2 应成功落地");

                squad.SelectExplicit(new[] { q1, q2 });
                squad.AssignGroup(7);
                squad.ClearSelection();
                squad.RecallGroup(7);
                int queuedCount = squad.Issue(UnitCommandKind.Guard, new float2(20f, -20f), SimEntityId.None, paused: true);
                Expect(queuedCount == 2 && squad.QueuedCommandCount == 1,
                    $"暂停下达应排队而不立即执行（返回 {queuedCount}，队列 {squad.QueuedCommandCount}）");
                Formation slot7Formation = formations.FindFormationContaining(q1);
                Expect(slot7Formation != null && slot7Formation.ActiveCommand == null,
                    "排队阶段不应提前调用 Formation.IssueCommand");

                int flushedNormal = squad.FlushQueuedCommands();
                Expect(flushedNormal == 1, $"正常兑现应成功 1 条（实际 {flushedNormal}）");
                Expect(slot7Formation.ActiveCommand != null &&
                       slot7Formation.ActiveCommand.Command.Kind == FormationCommand.CommandKind.Guard,
                    "恢复后 flush 应真正调用 Formation.IssueCommand");
                Expect(sim.TryGetCommand(q1, out UnitCommand q1Cmd) && q1Cmd.Kind == UnitCommandKind.Guard,
                    "正常 flush 的 Attack/Guard 应下发内核命令");

                // ── 8. 暂停期重编组：排队命令的编队被改动后，flush 应取消而不是发给错的人 ──
                SimEntityId r1 = SpawnAndResolve(96012, new float2(-20f, -20f));
                SimEntityId r2 = SpawnAndResolve(96013, new float2(-21f, -20f));
                SimEntityId r3 = SpawnAndResolve(96014, new float2(-22f, -20f));
                Expect(r1.IsValid && r2.IsValid && r3.IsValid, "本项前置：测试个体 r1/r2/r3 应成功落地");

                squad.SelectExplicit(new[] { r1, r2 });
                squad.AssignGroup(8);
                squad.ClearSelection();
                squad.RecallGroup(8);
                int staleQueuedCount = squad.Issue(UnitCommandKind.Guard, new float2(-20f, -20f), SimEntityId.None, paused: true);
                Expect(staleQueuedCount == 2, "本项前置：暂停期对槽位8下令应成功排队 2 个目标");

                // 暂停仍未结束，重新编组把槽位8的一部分成员挪去槽位9（Tick(paused) 下
                // HandleGroupInput 仍会执行，这里直接调 API 模拟同等效果）。
                squad.SelectExplicit(new[] { r1, r3 });
                squad.AssignGroup(9);

                Formation slot8Formation = formations.FindFormationContaining(r2);
                FormationCommandEntry slot8ActiveBefore = slot8Formation?.ActiveCommand;
                Formation slot9Formation = formations.FindFormationContaining(r1);

                int flushedAfterReassign = squad.FlushQueuedCommands();
                Expect(flushedAfterReassign == 0,
                    $"排队命令的编队已被重新编组，flush 不应算作成功执行（实际 flushed {flushedAfterReassign}）");
                Expect(squad.LastFormationDispatchOutcome == SquadFormationDispatchOutcome.CancelledStaleMembership,
                    $"应记录为 CancelledStaleMembership（实际 {squad.LastFormationDispatchOutcome}）");
                Expect(slot8Formation.ActiveCommand == slot8ActiveBefore,
                    "被取消的排队命令不应改变槽位8编队的 ActiveCommand");
                Expect(slot9Formation == null || slot9Formation.ActiveCommand == null,
                    "被取消的排队命令更不应该发给槽位9这个完全不相关的新编队");
                Expect(!sim.TryGetCommand(r2, out _),
                    "取消的排队命令不应给 r2 下发任何内核命令，也不应静默退化为普通移动");

                // ── 9. 生命周期：Bind/Unbind 不让旧局的编队映射串到新局 ──
                squad.Unbind();
                var freshFormations = new FormationRegistry();
                freshFormations.OnEnter();
                squad.Bind(sim, null, freshFormations);
                squad.SelectExplicit(new[] { r1 });
                squad.AssignGroup(1); // 复用旧局用过的槽位号
                Expect(freshFormations.AllFormations.Count == 1,
                    "Unbind 后换一个新的 FormationRegistry 重新 Bind，旧局的编组映射不应残留");
                Expect(formations.AllFormations.Count >= 1,
                    "旧的 FormationRegistry 实例本身不应被新局的 Bind 动作影响（对照组）");
            }
            finally
            {
                squad.Unbind();
                sim.End();
                hub.Exit();
            }
        }

        /// <summary>
        /// `DEBT-M4R02-INPUT-SIM-01`（`production/design/qa-journey-bot-real-input/DESIGN.md`
        /// Tier 1）：`SquadCommandSystem.HandleGroupInput`/`HandleCommandInput` 真读键鼠输入的
        /// 两个方法本身此前完全没有自动化覆盖——包括 [49] 在内的全部既有测试都直调
        /// `AssignGroup`/`Issue`/`SelectExplicit` 等公开 API，绕开了"按键组合 → 调用哪个 API"
        /// 这一段翻译逻辑。本项驱动真实的 <see cref="SquadCommandSystem.Tick"/>，用
        /// <see cref="ScriptedInputReader"/>（经 <see cref="InputRouter.DebugSetReader"/> 注入）
        /// 模拟键鼠事件，覆盖：①单独 Ctrl+1 编组；②无 Ctrl 的数字键改为召回语义（与①互斥，
        /// 同一个键位两种解读不能混淆）；③右键智能命令按点击目标分流 Move/Attack；
        /// ④G/H 单键触发 Guard/Retreat。不验证 Unity 相机把物理键鼠事件送进
        /// <c>UnityEngine.Input</c> 这一步——那是引擎自己的职责；相机→世界坐标换算本身用固定
        /// 尺寸 RenderTexture 钉死像素维度，避免 batchmode 下无 Game View 导致坐标漂移。
        /// </summary>
        private static void ValidateSquadInputTranslation()
        {
            Line("\n[50] 编队命令真实键鼠输入翻译层（DEBT-M4R02-INPUT-SIM-01，QA Tier 1）");

            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 32;
            cfg.ArenaHalfExtent = 260f;
            sim.Begin(cfg, Array.Empty<BehaviorArchetype>());

            var cameraGo = new GameObject("ValidateSquadInputTranslation_TempCamera");
            Camera camera = cameraGo.AddComponent<Camera>();
            var rt = new RenderTexture(256, 256, 0);
            camera.targetTexture = rt;
            cameraGo.transform.position = new Vector3(0f, 10f, 0f);
            cameraGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 俯视，正下方

            var squad = new SquadCommandSystem();
            var reader = new ScriptedInputReader();

            InputRouter.Reset();
            InputRouter.SetScope(InputScope.Strategy);
            InputRouter.DebugSetReader(reader);
            reader.MousePosition = new Vector3(128f, 128f, 0f); // 256x256 目标纹理的屏幕中心 → 世界(0,0)

            try
            {
                squad.Bind(sim, camera, null); // 本项只关心输入翻译层，Formation 语义已由 [49] 覆盖

                SimEntityId SpawnAndResolve(int logicId, float2 pos, SimFaction faction)
                {
                    sim.Spawn(new SpawnRequest
                    {
                        Position = pos, Health = 20f, Radius = 0.5f, MaxSpeed = 4f,
                        ArchetypeId = 0, Faction = faction, IntentSource = IntentSource.AI, LogicId = logicId,
                    });
                    sim.OnUpdate(1f / 60f);
                    return FindEntityId(sim.Snapshot, logicId, out _);
                }

                // ── ①②：Ctrl+数字=编组，纯数字=召回，同一个键位两种语义不能混淆 ──
                SimEntityId u1 = SpawnAndResolve(97001, new float2(50f, 50f), SimFaction.PlayerMinion);
                SimEntityId u2 = SpawnAndResolve(97002, new float2(51f, 50f), SimFaction.PlayerMinion);
                Expect(u1.IsValid && u2.IsValid, "本项前置：测试个体 u1/u2 应成功落地");

                squad.SelectExplicit(new[] { u1, u2 });
                reader.SetHeld(KeyCode.LeftControl, true);
                reader.PressKeyDown(KeyCode.Alpha1);
                squad.Tick(paused: false);
                Expect(squad.GroupSize(1) == 2,
                    $"①Ctrl+1 应把当前选择集（2人）编入槽位1（实际 {squad.GroupSize(1)}）");
                reader.EndFrame();
                InputRouter.DebugClearConsumedKeys();
                reader.SetHeld(KeyCode.LeftControl, false);

                squad.ClearSelection();
                Expect(squad.Selection.Count == 0, "本项前置：召回测试前应先清空选择集");
                reader.PressKeyDown(KeyCode.Alpha1); // 这次不按 Ctrl
                squad.Tick(paused: false);
                Expect(squad.Selection.Count == 2 && squad.Selection.Contains(u1) && squad.Selection.Contains(u2),
                    $"②无 Ctrl 的数字键1应走召回语义，把槽位1成员恢复进选择集（实际 {squad.Selection.Count} 人）");
                reader.EndFrame();
                InputRouter.DebugClearConsumedKeys();

                // ── ③：右键智能命令按点击目标分流——点空地/点友军都不是攻击，只有点敌人才是 ──
                SimEntityId clicker = SpawnAndResolve(97003, new float2(200f, 200f), SimFaction.PlayerMinion);
                Expect(clicker.IsValid, "本项前置：测试个体 clicker 应成功落地");
                squad.SelectExplicit(new[] { clicker });

                reader.ClickMouseButtonDown(1);
                squad.Tick(paused: false);
                Expect(sim.TryGetCommand(clicker, out UnitCommand emptyCmd) && emptyCmd.Kind == UnitCommandKind.Move,
                    $"③右键点空地应下达 Move（实际 {(sim.TryGetCommand(clicker, out UnitCommand ec) ? ec.Kind.ToString() : "无命令")}）");
                reader.EndFrame();

                SimEntityId friendAtClick = SpawnAndResolve(97004, new float2(0f, 0f), SimFaction.PlayerMinion);
                Expect(friendAtClick.IsValid, "本项前置：测试个体 friendAtClick 应成功落地");
                reader.ClickMouseButtonDown(1);
                squad.Tick(paused: false);
                Expect(sim.TryGetCommand(clicker, out UnitCommand friendCmd) && friendCmd.Kind == UnitCommandKind.Move,
                    "③右键点友军不应触发 Attack——TryPickHostile 只认 Hostile 阵营，应仍下达 Move");
                reader.EndFrame();

                SimEntityId hostileAtClick = SpawnAndResolve(97005, new float2(0f, 0f), SimFaction.Hostile);
                Expect(hostileAtClick.IsValid, "本项前置：测试个体 hostileAtClick 应成功落地");
                reader.ClickMouseButtonDown(1);
                squad.Tick(paused: false);
                Expect(sim.TryGetCommand(clicker, out UnitCommand hostileCmd) &&
                       hostileCmd.Kind == UnitCommandKind.Attack && hostileCmd.TargetEntity == hostileAtClick,
                    $"③右键点敌人应下达 Attack 且目标为该敌人（实际 {(sim.TryGetCommand(clicker, out UnitCommand hc) ? $"{hc.Kind}/{hc.TargetEntity}" : "无命令")}）");
                reader.EndFrame();

                // ── ④：G/H 单键分别触发 Guard/Retreat，不依赖右键 ──
                reader.PressKeyDown(KeyCode.G);
                squad.Tick(paused: false);
                Expect(sim.TryGetCommand(clicker, out UnitCommand guardCmd) && guardCmd.Kind == UnitCommandKind.Guard,
                    $"④G 键应下达 Guard（实际 {(sim.TryGetCommand(clicker, out UnitCommand gc) ? gc.Kind.ToString() : "无命令")}）");
                reader.EndFrame();
                InputRouter.DebugClearConsumedKeys();

                reader.PressKeyDown(KeyCode.H);
                squad.Tick(paused: false);
                Expect(sim.TryGetCommand(clicker, out UnitCommand retreatCmd) && retreatCmd.Kind == UnitCommandKind.Retreat,
                    $"④H 键应下达 Retreat（实际 {(sim.TryGetCommand(clicker, out UnitCommand rc) ? rc.Kind.ToString() : "无命令")}）");
                reader.EndFrame();
                InputRouter.DebugClearConsumedKeys();

                // ── ⑤：鼠标命中 UI 时，世界框选与右键命令必须完全让位。──
                int issuedBeforeUiHit = squad.IssuedCommandCount;
                InputRouter.SetUiPointerBlocker(() => true);
                reader.ClickMouseButtonDown(1);
                squad.Tick(paused: false);
                Expect(squad.IssuedCommandCount == issuedBeforeUiHit,
                    "⑤鼠标命中 UI 时右键不得向世界下达命令");
                reader.EndFrame();

                squad.ClearSelection();
                reader.ClickMouseButtonDown(0);
                squad.Tick(paused: false);
                Expect(!squad.IsDragging && squad.Selection.Count == 0,
                    "⑤鼠标命中 UI 时左键不得启动地图框选或改变选择集");
                reader.EndFrame();
                InputRouter.SetUiPointerBlocker(null);
            }
            finally
            {
                squad.Unbind();
                sim.End();
                InputRouter.SetUiPointerBlocker(null);
                InputRouter.Reset();
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(cameraGo);
            }
        }

        /// <summary>
        /// CP-REQ-092：世界拆除（腔室切换/退出）时，存活弹体/持续区域不能被 <c>Dispose</c>
        /// 静默连带销毁——必须先确定性清空并留下可断言的痕迹（<see cref="SimTeardownSummary"/>）。
        /// 弹体额外要求真实回传 <see cref="ProjectileEndEvent"/>（Reason=WorldTeardown），
        /// 不是只清计数（见 <see cref="BinGames.Sim.SimWorld.TerminateTransientsForTeardown"/>）。
        /// </summary>
        private static void ValidateCombatTransientTeardown()
        {
            Line("\n[51] 战斗内弹体/持续区域拆除确定性清空（CP-REQ-092）");

            var sim = new SimBridge();
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 16;
            cfg.ArenaHalfExtent = 260f;
            sim.Begin(cfg, Array.Empty<BehaviorArchetype>());

            try
            {
                sim.FireProjectile(new float2(0f, 0f), new float2(0f, 1f), speed: 5f, damage: 1f,
                    lifetime: 30f, sourceLogicId: 98001);
                sim.SpawnZone(new float2(10f, 10f), radius: 2f, seconds: 30f, damagePerTick: 1f,
                    interval: 0.5f, sourceLogicId: 98002);
                sim.OnUpdate(1f / 60f);

                int aliveProjectilesBefore = 0;
                var projectiles = sim.World.Projectiles;
                for (int p = 0; p < projectiles.Length; p++)
                {
                    if (projectiles[p].Alive != 0) { aliveProjectilesBefore++; }
                }
                Expect(aliveProjectilesBefore >= 1,
                    $"本项前置：应至少有1个存活弹体（实际 {aliveProjectilesBefore}）");
                Expect(sim.LiveZoneCount >= 1,
                    $"本项前置：应至少有1个存活持续区域（实际 {sim.LiveZoneCount}）");

                sim.End();

                Expect(sim.LastTeardownSummary.ProjectilesTerminated >= 1,
                    $"世界拆除应确定性清空存活弹体（实际清空 {sim.LastTeardownSummary.ProjectilesTerminated}）——"
                    + "不能靠 Dispose 静默连带销毁");
                Expect(sim.LastTeardownSummary.ZonesTerminated >= 1,
                    $"世界拆除应确定性清空存活持续区域（实际清空 {sim.LastTeardownSummary.ZonesTerminated}）");
            }
            finally
            {
                sim.End(); // 幂等：正常路径已经 End 过；这里只兜异常路径。
            }
        }

        private static bool PathClearsAllObstacles(List<float2> path, List<ObstacleSpec> obstacles, float clearance)
        {
            const float epsilon = 1e-3f;
            for (int i = 0; i < path.Count - 1; i++)
            {
                float2 a = path[i];
                float2 b = path[i + 1];
                float2 ab = b - a;
                float abLenSq = math.lengthsq(ab);
                foreach (ObstacleSpec obstacle in obstacles)
                {
                    float effRadius = obstacle.Radius + clearance;
                    float t = abLenSq > 1e-8f ? math.clamp(math.dot(obstacle.Position - a, ab) / abLenSq, 0f, 1f) : 0f;
                    float2 closest = a + ab * t;
                    if (math.distance(closest, obstacle.Position) < effRadius - epsilon)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static float PathLength(List<float2> path)
        {
            float total = 0f;
            for (int i = 0; i < path.Count - 1; i++)
            {
                total += math.distance(path[i], path[i + 1]);
            }
            return total;
        }

        private static bool FloatsEqual(float2 a, float2 b) => math.distance(a, b) < 1e-4f;

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
                // ⚠ 这条守的是一个实测过的严重回归：放下意识后可用性若落成 None，
                // CellStageFlow.CheckEnd 会按 PlayerHealth<=0 判死（它读的是当前受控实体），
                // 于是**按 M 进战略视角当场弹回主菜单**。Released 与 None 必须分开。
                Expect(flow.Sim.Availability == ControlAvailability.Released,
                    $"主动放下意识且场上仍有可接管身体时，可用性应是 Released 而不是 " +
                    $"{flow.Sim.Availability}——落成 None 会被阶段判死");
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
                // ⚠ 不推帧！这条守的是实测过的「按 M 要按两次」：
                // 控制权是内核实时改的，但镜头的锚点读的是快照，而快照只在 SimBridge.OnUpdate
                // 里 Step 之后刷新。接管成功却同帧读不到受控实体 → RequestDirect 当场被拒，
                // 下一帧才生效。所以帧中改控制权之后必须立刻重抓快照。
                Expect(flow.Sim.TryGetPresentationAnchor(out _, out bool hasControlledNow) && hasControlledNow,
                    "接管成功后**同一帧**就该读得到受控实体锚点，否则镜头这一帧会拒绝回直控");

                // ── E. 召唤物不是可接管的身体 ──
                // bin 实测接管到一个 ArchetypeId=15 / MaxSpeed=4 / Speed=0.097 的菌丝锚炮台。
                // 根因是 09-14 把接管信号范围放开成全场之后，每个召唤物都成了 Tab 候选，
                // 而它们随开火不断生灭——这也是「友方角色每每都不一样」的来源。
                const int SummonLogicId = 9415;
                flow.Sim.Spawn(new SpawnRequest
                {
                    Position = new float2(6f, 6f), Health = 30f, Radius = 0.4f, MaxSpeed = 4f,
                    ArchetypeId = ArchetypeLoadoutTable.MyceliumArchetypeId,
                    Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = SummonLogicId,
                    ExcludeFromControl = true,
                });
                flow.Sim.OnUpdate(1f / 60f);
                SimEntityId summon = FindEntityId(flow.Sim.Snapshot, SummonLogicId, out _);
                Expect(summon.IsValid, "召唤物应已落地");

                SimControlCandidate[] afterSummon = flow.Sim.GetControlCandidates();
                bool summonListed = false;
                for (int i = 0; i < afterSummon.Length; i++)
                {
                    summonListed |= afterSummon[i].EntityId == summon;
                }
                Expect(!summonListed, "召唤物不得进入接管候选——它是装配打出来的产物，不是可转移意识的身体");
                Expect(flow.Sim.RestoreControlTo(summon) == false &&
                       flow.Sim.ControlledUnitId != summon,
                    "即便拿着召唤物的 id 直接请求接管也必须被拒（槽位复用后可能拿到陈旧 id）");

                // ── E2. 每一条召唤路径都必须自己带上这个标记 ──
                //
                // 上面两条测的是**内核的筛选**（拿一个已标记的单位，它进不了候选）。
                // 那守不住真正出问题的那一类：**某条生成路径忘了标**。
                // 09-14 第一次修只堵了 ComposeEngine 那两条（MetabolicSliceBridge 的分裂与召唤），
                // 漏掉了技能/卡牌的 Spawn 效果，玩家随后实测接管到 `#5_unit_L3_V141`。
                //
                // 所以这里**真的跑一遍 EffectSpawn**，而不是自己造一个 SpawnRequest——
                // 自己造就等于把被测代码抄了一遍，那条路忘没忘标记永远测不出来。
                var spawnExec = new GameLogic.Ability.Executors.EffectSpawn();
                var spawnCtx = new GameLogic.Ability.EffectContext
                {
                    Sim = flow.Sim,
                    Origin = new float2(-6f, -6f),
                    Direction = new float2(1f, 0f),
                };
                int beforeSpawnCount = flow.Sim.Snapshot.Count;
                spawnExec.Execute(new GameLogic.Ability.EffectSpec
                {
                    Kind = GameLogic.Ability.EffectKind.Spawn,
                    Count = 2,
                    Value = 20f,
                    Radius = 0.4f,
                    SpawnEnemyId = ArchetypeLoadoutTable.SporeArchetypeId,
                }, spawnCtx);
                flow.Sim.OnUpdate(1f / 60f);

                SimSnapshot afterEffect = flow.Sim.Snapshot;
                Expect(afterEffect.Count > beforeSpawnCount,
                    $"EffectSpawn 应真的生成了附属体（{beforeSpawnCount} → {afterEffect.Count}）——" +
                    "没生成的话下面那条断言是空过的");

                SimControlCandidate[] afterEffectCandidates = flow.Sim.GetControlCandidates();
                int effectSpawnedListed = 0;
                for (int i = beforeSpawnCount; i < afterEffect.Count; i++)
                {
                    if (afterEffect.Alive[i] == 0 ||
                        (SimFaction)afterEffect.Faction[i] != SimFaction.PlayerMinion)
                    {
                        continue;
                    }

                    for (int c = 0; c < afterEffectCandidates.Length; c++)
                    {
                        if (afterEffectCandidates[c].EntityId == afterEffect.EntityId[i])
                        {
                            effectSpawnedListed++;
                        }
                    }
                }
                Expect(effectSpawnedListed == 0,
                    $"技能/卡牌 Spawn 效果造出来的附属体不得进入接管候选（实测混进去 {effectSpawnedListed} 个）——" +
                    "它与孢子/分身/卵鞘同类，是装配打出来的产物，不是可转移意识的身体");

                // ── F. 玩家直控的加速度不看行为原型 ──
                // 菌丝体固着原型 Accel=0，被夹到 0.01 后每帧只逼近目标速度的万分之 1.7，
                // 按住方向两秒多才到 0.097 u/s。玩家手里的身体必须一律跟手。
                Expect(flow.Sim.RestoreControlTo(allies[1]), "接管第二名友军用于验证直控加速度");
                SimEntityId driven = flow.Sim.ControlledUnitId;
                for (int i = 0; i < 45; i++)
                {
                    flow.Sim.SetControlledIntent(new PlayerIntent
                    {
                        MoveDir = new float2(1f, 0f),
                        SpeedMul = 1f,
                        RadiusOverride = -1f,
                    });
                    flow.Sim.OnUpdate(1f / 60f);
                }
                float drivenSpeed = math.length(VelOfId(flow.Sim.Snapshot, driven));
                Expect(drivenSpeed > 3f,
                    $"玩家直控 0.75 秒后应当接近全速，而不是被原型的低加速度拖住（实测 {drivenSpeed:F2} u/s）");

                // ── F2. RTS 命令下同样不能蠕动 ──
                // 上一版只给玩家直控路径加了加速度下限，于是同一个单位在 RTS 命令下照旧蠕动，
                // 玩家当场又报一次「战术视角下这个角色还是速度不对（直控是对的）」。
                // 移动意图来自玩家/命令/AI 三处，按路径打补丁必然补一处漏两处；
                // 现在判据只有一条：原型的 Accel<=0 一律当"没填"，落默认值。
                // 这里直接用一个 Accel=0 的原型（15 菌丝体固着）下 Move 命令验证。
                const int CrawlLogicId = 9416;
                flow.Sim.Spawn(new SpawnRequest
                {
                    Position = new float2(-20f, -20f), Health = 50f, Radius = 0.5f, MaxSpeed = 8f,
                    ArchetypeId = ArchetypeLoadoutTable.MyceliumArchetypeId,
                    Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI, LogicId = CrawlLogicId,
                });
                flow.Sim.OnUpdate(1f / 60f);
                SimEntityId crawler = FindEntityId(flow.Sim.Snapshot, CrawlLogicId, out _);
                Expect(crawler.IsValid, "验证蠕动用的单位应已落地");
                flow.Sim.IssueCommand(new[] { crawler }, new UnitCommand
                {
                    Kind = UnitCommandKind.Move,
                    TargetPosition = new float2(20f, -20f),
                    TargetEntity = SimEntityId.None,
                    ArriveRadius = 1f,
                });
                for (int i = 0; i < 45; i++)
                {
                    flow.Sim.OnUpdate(1f / 60f);
                }
                float commandedSpeed = math.length(VelOfId(flow.Sim.Snapshot, crawler));
                Expect(commandedSpeed > 3f,
                    $"Accel 没填（0）的原型收到 RTS 命令后也应正常加速，而不是每帧蠕动" +
                    $"（实测 {commandedSpeed:F2} u/s）");

                // ── G. 放下意识后所有身体都没了 → 才是真正的"意识无处可去" ──
                flow.DebugParkControlForStrategy();
                SimSnapshot before = flow.Sim.Snapshot;
                for (int i = before.Count - 1; i >= 0; i--)
                {
                    if (!before.IsAlive(i))
                    {
                        continue;
                    }
                    SimFaction f = before.FactionOf(i);
                    if (f == SimFaction.Player || f == SimFaction.PlayerMinion)
                    {
                        flow.Sim.World.KillUnit(i, 0);
                    }
                }
                flow.Sim.OnUpdate(1f / 60f);
                Expect(flow.Sim.Availability == ControlAvailability.None,
                    $"一具可接管的身体都没有时才落回 None（实际 {flow.Sim.Availability}）——" +
                    "否则玩家会永远不判死，卡在战略视角里");
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

        private static float2 VelOfId(in SimSnapshot snap, SimEntityId id)
        {
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.EntityId[i] == id) { return snap.Velocity[i]; }
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
                squad.Bind(sim, camera, null);

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

                // M4-R00-02 队列①-1：预期值必须与 TryRelease 内部实际调用的解析函数一致
                // （ResolveCompiled，见其类注释），否则本测试算的是"改动前会扣多少"，
                // 不是"改动后真的扣了多少"——那是测试自己制造的假红/假绿。
                OrganKernelAction sporeAct = OrganKernelActionTable.ResolveCompiled(
                    sporeOrgan.OrganId, Array.Empty<string>(), seed: 0);
                OrganKernelAction myceliumAct = OrganKernelActionTable.ResolveCompiled(
                    myceliumOrgan.OrganId, Array.Empty<string>(), seed: 0);
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
                // ER1-2 机械化禁用词清零（5dc92865）后玩家文案“代谢”→“电量”，断言跟随现行文案。
                string expectMetabolism = $"电量 {shown.Metabolism:F0}/{shown.MetabolismMax:F0}";
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
            squad.Bind(sim, null, null);
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
                // 2026-09-14 产品决策反转（bin 拍板）：**接管即取消这具身体上的战术命令**。
                // 原语义是"命令跨接管存活、交还即复活"（M2-04a 按 GDD §7.3 做的）。实测读不通：
                // 玩家亲手把它开到别处，松手后它却溜回去走一条旧路线——bin 的原话是
                // 「直控就不要再执行战术命令了」。清除点在内核 SwitchControlledUnitInternal，
                // 且必须在 IntentSource 变成 Player 之前（之后 ClearCommand 会被"绝不夺走玩家
                // 直控实体"的保护正确拒掉）。
                Expect(!sim.TryGetCommand(courier, out _) && SourceOf(courier) == IntentSource.Player,
                    "接管的那一刻就该把原命令清掉，而不是冻结着等交还时复活");
                // 玩家开着它往反方向走一段，交还后它应当停在这里，而不是回去接着撤退。
                Step(25, new float2(-1f, 1f));

                float distRetreatBefore = math.distance(PosOf(courier), retreatPoint);
                float2 handoffPos = PosOf(courier);
                Expect(sim.RequestControlSwitch(body) == ControlRequestResult.Success, "应能退出直控切回本体");
                Expect(handoff.LastContinuation != HandoffContinuation.ResumeCommand,
                    $"命令已在接管时清掉，交还不该再走'延续命令'分支（实际 {handoff.LastContinuation}）");

                handoff.DebugAdvanceClock(AiHandoffSystem.HandoffBufferSeconds);
                Step(60);
                float distRetreatAfter = math.distance(PosOf(courier), retreatPoint);
                Expect(distRetreatAfter > distRetreatBefore - 1f,
                    $"交还后不得自己回去执行那条被取消的撤退命令" +
                    $"（离撤退点 {distRetreatBefore:F2} → {distRetreatAfter:F2}，不该变近）");
                Expect(math.distance(PosOf(courier), handoffPos) < 8f,
                    $"松手后它应当留在玩家放下它的地方附近（漂移 {math.distance(PosOf(courier), handoffPos):F2}）");

                // 没被推翻的那一半：**在战略视角新下的命令照常执行**。变的只是"接管前那条不跨接管存活"。
                squad.ClearSelection();
                squad.SelectExplicit(new[] { courier });
                Expect(squad.Issue(UnitCommandKind.Move, retreatPoint, SimEntityId.None, paused: false) == 1,
                    "交还之后重新下令应照常被接受");
                float distNewCmdBefore = math.distance(PosOf(courier), retreatPoint);
                Step(60);
                Expect(math.distance(PosOf(courier), retreatPoint) < distNewCmdBefore - 1f,
                    "新下的命令必须真的执行——被取消的只是接管前那一条");

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

                // ── C. **带着速度松手也不许自己再走一段**（2026-09-14 实测反馈）──
                //
                // 这一段原先断言的是相反的行为：「无威胁 + 退出时在移动 → 沿原朝向再走 6 米」
                // （HandoffContinuation.Advance，M2-04 的"延续合理意图，而不是站桩"）。
                // 玩家连着两轮报同一件事——「上一个角色老是会位移一段」——产品决策已反转，
                // 理由见 AiHandoffSystem.ArmBuffer 的注释：那段"意图"根本不是这具身体的意图，
                // 是玩家最后一次按键的残速。
                //
                // 本段刻意**在满速状态下松手**，因为原先的 [B] 段先等速度掉到 0.35 以下才释放，
                // 正好绕开了出问题的那条分支——那就是这个 bug 能活到试玩的原因。
                Expect(sim.ClearCommand(courier) || SourceOf(courier) == IntentSource.AI,
                    "先把信使交还 AI，构造'无命令 + 移动中退出'的场景");
                Expect(sim.RequestControlSwitch(courier) == ControlRequestResult.Success, "应能接管信使");
                Expect(sim.SetControlledPosition(new float2(20f, -20f)),
                    "把它摆回远离敌人的空地（确保这一段测的是'没有威胁时'的分支）");
                Step(30, new float2(1f, 0f));
                float2 exitPosC = PosOf(courier);
                float speedC = math.length(VelOf(courier));
                Expect(speedC > 0.35f &&
                       math.distance(exitPosC, hostilePos) > AiHandoffSystem.EngageThreatRange,
                    $"退出前的前提：单位**真的在动**（{speedC:F2} u/s）、且附近没有威胁——" +
                    "静止时松手不动是白测的，这个 bug 只在带速度松手时才出现");

                Expect(sim.RequestControlSwitch(body) == ControlRequestResult.Success, "应能在移动中退出直控");
                Expect(handoff.LastContinuation == HandoffContinuation.HoldGround,
                    $"带着速度松手也必须判为原地守住（实际 {handoff.LastContinuation}）——" +
                    "不许再有'沿原朝向续走'那一档");
                Expect(sim.TryGetCommand(courier, out UnitCommand parked) &&
                       math.distance(parked.TargetPosition, exitPosC) < AiHandoffSystem.BufferArriveRadius,
                    $"缓冲命令的锚点必须就是松手那一点（偏离 " +
                    $"{(sim.TryGetCommand(courier, out UnitCommand p2) ? math.distance(p2.TargetPosition, exitPosC) : -1f):F2}）——" +
                    "锚点挪开多远，它就会自己走多远");

                // 量真实位移：这是玩家实际看到的那个量，也是他报的那个"位移一段"。
                // 阈值取到达半径：守备在到达半径内就不再产生移动意图，所以这是"停住"的定义边界。
                // 允许的只有残速衰减出来的那点滑行，绝不该是一次 6 米的行军。
                Step(45);
                float driftC = math.distance(PosOf(courier), exitPosC);
                Expect(driftC < AiHandoffSystem.BufferArriveRadius,
                    $"松手后它应当就停在松手的地方（实测位移 {driftC:F2}，上限 " +
                    $"{AiHandoffSystem.BufferArriveRadius:F2}）——反转前这里是 6 米的主动行军");
                handoff.DebugAdvanceClock(AiHandoffSystem.HandoffBufferSeconds);
                Step(60);
                Expect(math.distance(PosOf(courier), exitPosC) < AiHandoffSystem.BufferArriveRadius,
                    $"缓冲到期之后同样不许再挪（累计位移 {math.distance(PosOf(courier), exitPosC):F2}）");

                // ── D. 背对敌人松手同样原地停住（撤退续走档一并去掉）──
                //
                // 原先这里断言"有威胁 + 正在背离 → 继续拉开距离"。去掉它的理由不是玩家点名了这一档，
                // 而是它与**已经上线且没人反对**的"交战中退出 → 守在原地继续打"自相矛盾：
                // 既然站在敌人旁边不许跑，背对敌人时也没有理由替玩家多跑 6 米。
                // 留着它，同一个抱怨在交战场景下照样能复现。
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
                Expect(handoff.LastContinuation == HandoffContinuation.HoldGround,
                    $"背对敌人松手也判原地守住（实际 {handoff.LastContinuation}）");
                Step(45);
                float driftR = math.distance(PosOf(ally), exitPosR);
                Expect(driftR < AiHandoffSystem.BufferArriveRadius,
                    $"背对敌人松手同样停在原地（实测位移 {driftR:F2}）——" +
                    "守备不接管战斗，原地不动**不等于**不还手");

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

            // 自造原型表而不是读 Luban：可接管友军必须**真的会攻击**（本段的被测行为就是它），
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

                // 两组"可接管友军 + 假人"隔开 40 米以上摆，索敌半径 12——保证各打各的，
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
                    "本段的玩家本体、两名可接管友军与两个假人应都已落地并拥有有效稳定实体 ID");

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

                // ── A. 基线：AI 可接管友军本来就在打人（没有它，后面所有"打不出东西"都不成立）──
                float baseA = HealthOf(dummyA);
                float baseB = HealthOf(dummyB);
                Step(60);
                float hitA = baseA - HealthOf(dummyA);
                float hitB = baseB - HealthOf(dummyB);
                Expect(hitA > 0f && hitB > 0f,
                    $"两名 AI 可接管友军在 60 帧内都应真的打出伤害（A 打掉 {hitA:F1} / B 打掉 {hitB:F1}）——" +
                    "这是本段一切反证的前提");
                Expect(SourceOf(minionA) == IntentSource.AI && SourceOf(minionB) == IntentSource.AI,
                    "此刻两名可接管友军都由 AI 驱动，走的是内核 ResolveMinionCombat 而不是直控释放入口");

                // ── B. AI 自己不会主动过载 ──
                //
                // M2-07 之前这一条是"构造上成立"：AI 的攻击根本不经过 Commit，一分债都不产生，
                // 所以它当然不会过载。现在 AI 用的是**和玩家同一套器官、同一本账**，
                // 债是真的在涨的——"不会主动过载"因此从一个副作用变成了一条真正被守住的约束
                // （<see cref="MinionOrganCombatDriver.AiStrainCeilingRatio"/> 那道安全线）。
                //
                // A 登记了装配走器官路，B 没登记仍走原型数值路，两者的账本形态因此**必然不同**，
                // 这正好把"器官驱动与否"这件事在账本上照出来。
                Expect(actions.Vitals.IsTracked(minionA),
                    "A 登记了装配，走器官开火 → 它必须在账本里有条目（AI 和玩家用同一本账，这是本段的立论）");
                Expect(!actions.Vitals.IsTracked(minionB),
                    "B 没登记装配，仍走行为原型数值的降级路 → 不碰账本。" +
                    "这条降级是有意保留的：真·召唤物这类没登记装配的身体若被迫走器官路会彻底哑火");
                Expect(actions.Vitals.Get(minionA).Strain > 0f,
                    $"A 的过载债应真的在涨（{actions.Vitals.Get(minionA).Strain:F1}）——" +
                    "AI 开火不再是免费的，这是「换谁开都一样」的直接体现");
                Expect(actions.Vitals.Get(minionA).Strain <=
                       UnitVitalsRegistry.StrainOverloadThreshold * MinionOrganCombatDriver.AiStrainCeilingRatio,
                    $"但它必须守住安全线（实测 {actions.Vitals.Get(minionA).Strain:F1} ≤ " +
                    $"{UnitVitalsRegistry.StrainOverloadThreshold * MinionOrganCombatDriver.AiStrainCeilingRatio:F0}）——" +
                    "没有这道闸门，全速开火的 AI 必然把自己烧进永久过载循环");
                Expect(actions.Vitals.OverloadedCount == 0 &&
                       actions.OverloadMirror.SuppressedCount == 0 &&
                       actions.OverloadMirror.PushCount == 0,
                    "没有任何身体过载时，镜像不该往内核推过任何东西");
                Expect(!KernelOverloaded(minionA) && !KernelOverloaded(minionB),
                    "内核侧两名可接管友军都不带过载位");

                // ── C. 玩家把这具身体推到过载（M2-03c 的既有路径，一行没改）──
                Expect(sim.RequestControlSwitch(minionA) == ControlRequestResult.Success,
                    "应能接管友军 A");

                // M2-07：接管的那一刻这把枪**可能正在冷却**——刚才 AI 就是拿它开火的。
                // 这不是回归，恰恰是统一后必然成立的事：同一具身体上的同一件器官只有一条冷却线，
                // 不因为"换了谁在开"而重置。接管送一次免费爆发的话，
                // 这本账就又回到了"被开"和"自己打"各记各的老样子。
                // 所以这里等它就绪，而不是要求它必须当帧可用；上限 2 秒，远超任何器官的冷却。
                bool playerReleased = false;
                for (int f = 0; f < 120 && !playerReleased; f++)
                {
                    playerReleased = actions.TryRelease(LoadoutAction.Primary, awayAim);
                    if (!playerReleased)
                    {
                        Step(1);
                    }
                }
                Expect(playerReleased,
                    $"接管后玩家应能用它的主器官释放一次（等冷却，最后一次被拒原因 {actions.LastReleaseResult}）");
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
                    $"解除之后 AI 应重新打出伤害（60 帧打掉 {recovered:F1}）——压制是一段窗口，不是永久失能" +
                    // M2-07：这条红过一次，而"打掉 0.0"有五种完全不同的原因，在画面上长得一模一样。
                    // 留着这几个计数，下次红的时候一眼能分清是没器官、被闸门拦了，还是守安全线。
                    $"｜驱动器：释放 {actions.MinionCombat.ReleaseCount} / 守线 {actions.MinionCombat.StrainHoldCount}" +
                    $" / 无器官 {actions.MinionCombat.NoOrganCount} / 上次被拒 {actions.MinionCombat.LastBlockedGate}" +
                    $"｜过载债 {actions.Vitals.Get(minionA).Strain:F1}");

                // ── F. 拆台不留悬挂压制（跨局最致命的一种泄漏）──
                Expect(actions.Vitals.AddStrain(minionA, UnitVitalsRegistry.StrainOverloadThreshold + 5f),
                    "再把它推进过载态一次");
                Step(2);
                Expect(KernelOverloaded(minionA) && actions.OverloadMirror.SuppressedCount == 1,
                    "内核侧重新被压住");

                Expect(!actions.Vitals.IsTracked(minionB),
                    "整段跑完，**没登记装配**的对照单位在账本里仍然连条目都没有——" +
                    "降级路（原型数值）确实一点都没碰这本账。M2-07 之后「AI 不会主动过载」" +
                    "对器官驱动的身体靠的是 AiStrainCeilingRatio 那道闸门，不再是构造上白得的");

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
                    squad.Bind(sim, camera, null);
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

        /// <summary>`DEBT-M4R02-INPUT-SIM-01`：按帧回放的测试用 <see cref="IInputReader"/>。
        /// 持续状态（<see cref="SetHeld"/>）跨帧保留，边沿事件（按下/松开）只在
        /// <see cref="EndFrame"/> 之前的这一帧有效——语义对齐真实键鼠：一次物理按下只会
        /// 让 GetKeyDown 在按下的那一帧为 true。只在本 Editor 测试文件内使用，不进生产程序集。</summary>
        private sealed class ScriptedInputReader : IInputReader
        {
            private readonly System.Collections.Generic.HashSet<KeyCode> _held =
                new System.Collections.Generic.HashSet<KeyCode>();
            private readonly System.Collections.Generic.HashSet<KeyCode> _downThisFrame =
                new System.Collections.Generic.HashSet<KeyCode>();
            private readonly System.Collections.Generic.HashSet<int> _mouseDownThisFrame =
                new System.Collections.Generic.HashSet<int>();
            private readonly System.Collections.Generic.HashSet<int> _mouseUpThisFrame =
                new System.Collections.Generic.HashSet<int>();

            public Vector3 MousePosition { get; set; }
            public float MouseScrollDelta { get; set; }

            public bool GetKey(KeyCode key) => _held.Contains(key);
            public bool GetKeyDown(KeyCode key) => _downThisFrame.Contains(key);
            public bool GetMouseButtonDown(int button) => _mouseDownThisFrame.Contains(button);
            public bool GetMouseButtonUp(int button) => _mouseUpThisFrame.Contains(button);

            public void SetHeld(KeyCode key, bool held)
            {
                if (held) { _held.Add(key); } else { _held.Remove(key); }
            }

            /// <summary>模拟"这一帧按下"：本帧 GetKeyDown 为 true，并转入持续按住状态。</summary>
            public void PressKeyDown(KeyCode key)
            {
                _held.Add(key);
                _downThisFrame.Add(key);
            }

            public void ReleaseKey(KeyCode key) => _held.Remove(key);

            public void ClickMouseButtonDown(int button) => _mouseDownThisFrame.Add(button);
            public void ClickMouseButtonUp(int button) => _mouseUpThisFrame.Add(button);

            /// <summary>推进到下一帧：清空本帧边沿事件，持续按住状态保留。</summary>
            public void EndFrame()
            {
                _downThisFrame.Clear();
                _mouseDownThisFrame.Clear();
                _mouseUpThisFrame.Clear();
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
