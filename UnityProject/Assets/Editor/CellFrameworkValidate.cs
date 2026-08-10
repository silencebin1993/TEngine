using System;
using System.Linq;
using System.Text;
using BinGames.Sim;
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
                ValidateSpatialHash();
                ValidateDevourThreshold();
                ValidateStatusExpiry();
                ValidateMinionCap();
                ValidateDeathCauseKind();
                ValidateInheritance();
                ValidateBossPhase();
                ValidateShop();
                ValidateCodex();
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

            Expect(reg.AllCards.Count == 135, $"卡牌 135 张（实际 {reg.AllCards.Count}）");
            Expect(reg.Phases.Count == 6, $"生态时期 6 个（实际 {reg.Phases.Count}）");
            Expect(reg.EcoEvents.Count == 16, $"生态事件 16 个（实际 {reg.EcoEvents.Count}）");
            Expect(reg.Archetypes.Count == 12, $"行为原型 12 个（实际 {reg.Archetypes.Count}）");

            // TR-cell-011：首领 90（原核霸主）应配 3 个阶段
            var bossPhases = reg.GetBossPhases(90);
            Expect(bossPhases != null && bossPhases.Count == 3,
                $"首领 90 应有 3 个阶段（实际 {bossPhases?.Count ?? 0}）");

            // 抽样验证映射真的填对了字段，而不是全零
            var card = reg.GetCard(1001);
            if (card == null)
            {
                Fail("找不到卡牌 1001（裂齿口器）");
            }
            else
            {
                Expect(card.Name == "裂齿口器", $"卡 1001 名称（实际 '{card.Name}'）");
                Expect(card.Trigger == Cards.CardTrigger.OnHit,
                    $"卡 1001 触发时机应为 OnHit（实际 {card.Trigger}）");
                Expect(card.Effects.Count == 1, $"卡 1001 应有 1 条效果（实际 {card.Effects.Count}）");
                if (card.Effects.Count > 0)
                {
                    var ef = card.Effects[0];
                    Expect(ef.Status == SimStatus.Breached,
                        $"卡 1001 效果应施加 Breached（实际 {ef.Status}）");
                    Expect(ef.Duration > 0f, $"卡 1001 效果应有持续时间（实际 {ef.Duration}）");
                }
            }

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

            CardSpec keyCard = DataRegistry.Instance.GetCard(1001);
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
                    Fail("找不到卡 1001，无法验证定义性卡牌注入");
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
