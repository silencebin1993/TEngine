using BinGames.Sim;
using GameLogic.Command.Formation;
using GameLogic.Core;
using Unity.Mathematics;

namespace GameLogic.Battle
{
    /// <summary>
    /// M4-06：多线固定遭遇。权威规格见仓库根
    /// <c>production/session-state/preflight-decisions.md</c>（"M4-06 多线固定遭遇"节）D1~D6 与
    /// 验收映射——本类型是那份决策的直接落地，不是通用关卡编排框架（D6 明确不做）。
    ///
    /// ── 场景（D1）──
    /// 一撮固定敌对单位（<see cref="NestMemberCount"/> 个，<see cref="IntentSource.Scripted"/>，
    /// 站桩不动）代表"巢"，放在 <see cref="NestOrigin"/>；一个 <see cref="CheckpointPosition"/>
    /// 世界坐标代表"搬运线检查点"（不实体化，只是 Guard 命令的 TargetPosition）。
    ///
    /// ── 两种策略（D2）──
    /// <see cref="RunSplitStrategy"/>：两支独立编队，一支 Attack 打巢、一支 Guard 守检查点，
    /// 两队同时推进。<see cref="RunGeneralistStrategy"/>：一支编队（成员是两队之和），先打巢，
    /// 巢清空后再去守检查点，顺序执行、不并行。
    ///
    /// ── 胜负判定（D3）──
    /// 编队命令层（<see cref="Formation"/>）不提供 Attack/Guard 到 <see cref="SimBridge"/> 的自动
    /// 翻译（<see cref="FormationMovementDriver"/> 只翻译 Move/Retreat），所以本类型自己在两层都
    /// 下达命令：<see cref="Formation.IssueCommand"/> 只做编队侧记账（供 UI/自检读取状态），真正
    /// 驱动战斗与移动靠直接调用 <see cref="SimBridge.IssueCommand"/>（现成 API，未改内核）。
    /// "巢清空"= 巢的全部 <see cref="SimEntityId"/> 都不再能查到位置（<see cref="SimBridge.TryGetPosition"/>
    /// 对死亡实体返回 false）——不依赖/不误读 <see cref="FormationCommandEntry.State"/> 字面值
    /// （M4-02 已锁定"Attack 目标死亡自动 Fail"，见 <see cref="FormationRegistry.HandleMemberDeath"/>
    /// 文档；本类型完全不调用它，用 SimBridge 的存活查询自己判断）。
    /// "线守住"退化为结构性断言：守护编队成员全程存活、抵达后一直在检查点半径内。
    ///
    /// ── maxTicks 不是玩法倒计时（D4）──
    /// 调用方传入的 <c>maxTicks</c> 只是 headless 自检的超时保护（防止真出 bug 时测试卡死），
    /// 不是游戏内机制；"遭遇完成"判定成立的那一刻就退出循环并记录 tick 数，不会跑满上限。
    /// </summary>
    public static class FormationEncounterScenario
    {
        // ── D1：固定场景常量 ──────────────────────────────────────────────
        public const int NestMemberCount = 4;
        public const float NestMemberHealth = 10f;
        public const float NestMemberSpacing = 1.6f;
        public static readonly float2 NestOrigin = new float2(32f, 0f);
        public static readonly float2 CheckpointPosition = new float2(0f, 32f);
        public const float CheckpointHoldRadius = 3f;

        private const float UnitRadius = 0.5f;
        private const float UnitMaxSpeed = 6f;
        private const float UnitHealth = 40f;
        private const float ArriveRadius = 1.2f;

        private const int AttackerArchetypeId = 0;
        private const int NestArchetypeId = 1;

        public enum StrategyKind
        {
            Split,
            Generalist,
        }

        /// <summary>一次 headless 推进的结果。<see cref="CompletionTick"/> = -1 表示在 maxTicks 内
        /// 未达成"遭遇完成"（自检超时保护触发，视为该次运行失败，不代表玩法失败）。</summary>
        public sealed class RunResult
        {
            public bool NestCleared;
            public bool LineHeld;
            public bool EncounterComplete => NestCleared && LineHeld;
            public int CompletionTick = -1;
            public int MaxTicks;
        }

        /// <summary>D5：分线策略巢死一半时触发一次的钩子，供调用方（自检）做真实接管信号验证。
        /// <paramref name="attackMembers"/> 是巢攻坚编队的成员快照（构建时确定，不随运行变化）。</summary>
        public delegate void MidRunHook(SimBridge sim, FormationRegistry formations, Formation attackFormation,
            SimEntityId[] attackMembers);

        private static BehaviorArchetype[] BuildArchetypes()
        {
            return new[]
            {
                // 索引 0：攻坚/守备友军共用的原型。Kind 只影响 IntentSource == AI 时的自主索敌
                // 与 ResolveMinionCombat 的战斗资格判定；实际移动全部由本类型下达的 UnitCommand 驱动。
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.MinionSeekAttack,
                    Accel = 12f,
                    TurnRate = 0f,
                    AggroRange = 8f,
                    AttackRange = 1.5f,
                    AttackCooldown = 0.2f,
                    AttackDamage = 15f,
                    Separation = 1f,
                },
                // 索引 1：巢。IntentSource.Scripted 不吃 JobAIIntent/JobCommandIntent 任何一条，
                // Kind = Stationary 只是文档意义上的准确标注，实际站桩靠 Scripted 本身。
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Stationary,
                    Separation = 1f,
                },
            };
        }

        private static (ModuleHub hub, FormationRegistry formations, SimBridge sim) BuildWorld()
        {
            var hub = new ModuleHub();
            FormationRegistry formations = hub.Register(new FormationRegistry());
            SimBridge sim = hub.Register(new SimBridge());
            hub.Register(new FormationMovementDriver());
            hub.Enter();

            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 64;
            cfg.ArenaHalfExtent = 120f;
            sim.Begin(cfg, BuildArchetypes());
            // D5 需要在同一帧内连续 RequestControlSwitch/ReleaseControl，去掉冷却与信号范围限制，
            // 与 [37] 的处理方式一致——本组断言只关心接管/释放的结构性效果，不关心节奏参数。
            sim.ConfigureControlSwitch(1_000_000f, 0f);
            return (hub, formations, sim);
        }

        private static SimEntityId[] SpawnNest(SimBridge sim, int logicIdBase)
        {
            for (int i = 0; i < NestMemberCount; i++)
            {
                sim.Spawn(new SpawnRequest
                {
                    Position = NestOrigin + new float2(i * NestMemberSpacing, 0f),
                    Health = NestMemberHealth,
                    Radius = UnitRadius,
                    MaxSpeed = 0f,
                    ArchetypeId = NestArchetypeId,
                    Faction = SimFaction.Hostile,
                    IntentSource = IntentSource.Scripted,
                    LogicId = logicIdBase + i,
                });
            }
            sim.OnUpdate(0.01f); // 让 spawn 落地一帧，才能按 LogicId 反查稳定 SimEntityId。

            SimSnapshot snap = sim.Snapshot;
            var ids = new SimEntityId[NestMemberCount];
            for (int i = 0; i < NestMemberCount; i++)
            {
                ids[i] = FindEntityId(snap, logicIdBase + i);
            }
            return ids;
        }

        private static SimEntityId[] SpawnFriendlies(SimBridge sim, int count, float2 origin, int logicIdBase)
        {
            for (int i = 0; i < count; i++)
            {
                sim.Spawn(new SpawnRequest
                {
                    Position = origin + new float2(0f, i * 1.2f),
                    Health = UnitHealth,
                    Radius = UnitRadius,
                    MaxSpeed = UnitMaxSpeed,
                    ArchetypeId = AttackerArchetypeId,
                    Faction = SimFaction.PlayerMinion,
                    IntentSource = IntentSource.AI,
                    LogicId = logicIdBase + i,
                });
            }
            sim.OnUpdate(0.01f);

            SimSnapshot snap = sim.Snapshot;
            var ids = new SimEntityId[count];
            for (int i = 0; i < count; i++)
            {
                ids[i] = FindEntityId(snap, logicIdBase + i);
            }
            return ids;
        }

        private static SimEntityId FindEntityId(SimSnapshot snapshot, int logicId)
        {
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (snapshot.LogicId[i] == logicId)
                {
                    return snapshot.EntityId[i];
                }
            }
            return SimEntityId.None;
        }

        private static bool IsAlive(SimBridge sim, SimEntityId id) => sim.TryGetPosition(id, out _);

        private static bool TryFindLivingNestMember(SimBridge sim, SimEntityId[] nest, out SimEntityId id)
        {
            foreach (SimEntityId n in nest)
            {
                if (IsAlive(sim, n))
                {
                    id = n;
                    return true;
                }
            }
            id = SimEntityId.None;
            return false;
        }

        /// <summary>D2/D3：编队侧记账（<see cref="Formation.IssueCommand"/>）与内核侧真实驱动
        /// （<see cref="SimBridge.IssueCommand"/>）分开下达——见类型注释"胜负判定"一节。</summary>
        private static void IssueAttack(SimBridge sim, Formation formation, SimEntityId[] members, SimEntityId target)
        {
            formation.IssueCommand(new FormationCommand(FormationCommand.CommandKind.Attack, targetEntity: target));
            sim.IssueCommand(members, new UnitCommand
            {
                Kind = UnitCommandKind.Attack,
                TargetEntity = target,
                ArriveRadius = ArriveRadius,
            });
        }

        private static void IssueGuard(SimBridge sim, Formation formation, SimEntityId[] members, float2 checkpoint)
        {
            formation.IssueCommand(new FormationCommand(FormationCommand.CommandKind.Guard, targetPosition: checkpoint));
            sim.IssueCommand(members, new UnitCommand
            {
                Kind = UnitCommandKind.Guard,
                TargetPosition = checkpoint,
                ArriveRadius = CheckpointHoldRadius,
            });
        }

        private static bool AllWithinCheckpoint(SimBridge sim, SimEntityId[] members)
        {
            foreach (SimEntityId m in members)
            {
                if (!sim.TryGetPosition(m, out float2 pos))
                {
                    return false;
                }
                if (math.distance(pos, CheckpointPosition) > CheckpointHoldRadius)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>D2 分线策略：巢攻坚编队与守线编队同时推进。</summary>
        public static RunResult RunSplitStrategy(int maxTicks, MidRunHook midRunHook = null)
        {
            (ModuleHub hub, FormationRegistry formations, SimBridge sim) = BuildWorld();
            var result = new RunResult { MaxTicks = maxTicks };
            try
            {
                SimEntityId[] nest = SpawnNest(sim, 481000);
                SimEntityId[] attackers = SpawnFriendlies(sim, 2, new float2(-4f, 0f), 482000);
                SimEntityId[] guards = SpawnFriendlies(sim, 2, new float2(4f, 0f), 483000);

                Formation attackFormation = formations.CreateFormation();
                foreach (SimEntityId a in attackers) attackFormation.AddMember(a);
                Formation guardFormation = formations.CreateFormation();
                foreach (SimEntityId g in guards) guardFormation.AddMember(g);

                TryFindLivingNestMember(sim, nest, out SimEntityId currentTarget);
                IssueAttack(sim, attackFormation, attackers, currentTarget);
                IssueGuard(sim, guardFormation, guards, CheckpointPosition);

                bool nestCleared = false;
                bool lineBroken = false;
                bool hookFired = false;
                const float dt = 1f / 60f;

                for (int tick = 0; tick < maxTicks; tick++)
                {
                    hub.Update(dt);

                    if (!nestCleared && !IsAlive(sim, currentTarget))
                    {
                        if (TryFindLivingNestMember(sim, nest, out SimEntityId next))
                        {
                            currentTarget = next;
                            IssueAttack(sim, attackFormation, attackers, currentTarget);
                        }
                        else
                        {
                            nestCleared = true;
                            attackFormation.CompleteActiveCommand();
                        }
                    }

                    // D3 退化版"线守住"：守护编队一旦有人阵亡即判定线被破，且不可逆。
                    foreach (SimEntityId g in guards)
                    {
                        if (!IsAlive(sim, g))
                        {
                            lineBroken = true;
                            break;
                        }
                    }

                    if (!hookFired && midRunHook != null)
                    {
                        int deadCount = 0;
                        foreach (SimEntityId n in nest)
                        {
                            if (!IsAlive(sim, n)) deadCount++;
                        }
                        if (deadCount * 2 >= NestMemberCount)
                        {
                            hookFired = true;
                            midRunHook(sim, formations, attackFormation, attackers);
                        }
                    }

                    bool guardHolding = !lineBroken && AllWithinCheckpoint(sim, guards);
                    if (nestCleared && guardHolding)
                    {
                        result.CompletionTick = tick;
                        break;
                    }
                }

                result.NestCleared = nestCleared;
                result.LineHeld = !lineBroken && AllWithinCheckpoint(sim, guards);
            }
            finally
            {
                hub.Exit();
            }
            return result;
        }

        /// <summary>D2 万能队策略：单一编队先清巢、巢清空后再顺序去守检查点。</summary>
        public static RunResult RunGeneralistStrategy(int maxTicks)
        {
            (ModuleHub hub, FormationRegistry formations, SimBridge sim) = BuildWorld();
            var result = new RunResult { MaxTicks = maxTicks };
            try
            {
                SimEntityId[] nest = SpawnNest(sim, 491000);
                SimEntityId[] half1 = SpawnFriendlies(sim, 2, new float2(-4f, 0f), 492000);
                SimEntityId[] half2 = SpawnFriendlies(sim, 2, new float2(4f, 0f), 493000);
                var all = new SimEntityId[half1.Length + half2.Length];
                for (int i = 0; i < half1.Length; i++) all[i] = half1[i];
                for (int i = 0; i < half2.Length; i++) all[half1.Length + i] = half2[i];

                Formation formation = formations.CreateFormation();
                foreach (SimEntityId m in all) formation.AddMember(m);

                TryFindLivingNestMember(sim, nest, out SimEntityId currentTarget);
                IssueAttack(sim, formation, all, currentTarget);

                bool nestCleared = false;
                bool guardIssued = false;
                bool lineBroken = false;
                const float dt = 1f / 60f;

                for (int tick = 0; tick < maxTicks; tick++)
                {
                    hub.Update(dt);

                    if (!nestCleared && !IsAlive(sim, currentTarget))
                    {
                        if (TryFindLivingNestMember(sim, nest, out SimEntityId next))
                        {
                            currentTarget = next;
                            IssueAttack(sim, formation, all, currentTarget);
                        }
                        else
                        {
                            nestCleared = true;
                            formation.CompleteActiveCommand();
                            // D2：顺序执行——巢清空之后才下达 Guard，不与攻坚并行。
                            IssueGuard(sim, formation, all, CheckpointPosition);
                            guardIssued = true;
                        }
                    }

                    if (guardIssued)
                    {
                        foreach (SimEntityId m in all)
                        {
                            if (!IsAlive(sim, m))
                            {
                                lineBroken = true;
                                break;
                            }
                        }
                    }

                    bool guardHolding = guardIssued && !lineBroken && AllWithinCheckpoint(sim, all);
                    if (nestCleared && guardHolding)
                    {
                        result.CompletionTick = tick;
                        break;
                    }
                }

                result.NestCleared = nestCleared;
                result.LineHeld = guardIssued && !lineBroken && AllWithinCheckpoint(sim, all);
            }
            finally
            {
                hub.Exit();
            }
            return result;
        }
    }
}
