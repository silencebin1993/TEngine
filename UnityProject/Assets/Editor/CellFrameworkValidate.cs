using System;
using System.Text;
using BinGames.Sim;
using GameLogic.Core;
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
            Expect(reg.Archetypes.Count == 10, $"行为原型 10 个（实际 {reg.Archetypes.Count}）");

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
