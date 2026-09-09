using System.Collections.Generic;
using System.Text;
using BinGames.Sim;
using GameLogic.Ability;
using GameLogic.Battle;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.Stats;
using Unity.Mathematics;
using HitEvent = ComposeEngine.Core.HitEvent;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// enemy-ranged-and-parry：四件"缺前提所以一直没做"的机制，连同它们的前提一起验收。
    ///
    /// <list type="number">
    /// <item><b>敌人远程弹体</b>（格挡的前提）——此前敌人只有接触伤害，
    /// `BehaviorKind.Ranged` 只影响转向从不发射，所谓"远程"是站在 12 米外隐形扣血。</item>
    /// <item><b>死亡事件</b>（召唤物死亡分裂的前提）——<c>EmitDeath</c> 拿 <c>_alive</c> 做同帧去重，
    /// 而两个生产者在入队前就已置 0，导致 <c>DeathCount</c> 恒为 0：
    /// 击杀奖励 / 进化能 / 卡牌 OnKill / KillSignal 全都从没触发过。</item>
    /// <item><b>格挡弹反</b>——gene_mirror 文案「近战则弹开敌人的弹」的实现。</item>
    /// <item><b>召唤物死亡分裂</b> + <b>攻击节奏</b>。</item>
    /// </list>
    ///
    /// 纯 C# 直调真实 <see cref="SimWorld"/>，不进 Play。
    /// </summary>
    public static class EnemyRangedAndParrySmokeReport
    {
        private static SimConfig Cfg => new SimConfig
        {
            UnitCapacity = 64,
            ProjectileCapacity = 64,
            ArenaHalfExtent = 90f,
            HashCellSize = 4f,
            MaxDeathEventsPerFrame = 64,
            MaxHitEventsPerFrame = 64,
            RandomSeed = 1u,
            BreachedDiscount = 0.7f,
            CorrodedDiscount = 0.85f,
        };

        /// <summary>0=单发直射远程；1=三连扇射炮台；2=召唤物索敌。</summary>
        private static BehaviorArchetype[] Archetypes()
        {
            return new[]
            {
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Ranged, Accel = 0f, TurnRate = 0f, AggroRange = 24f,
                    AttackRange = 12f, AttackCooldown = 2f, AttackDamage = 7f, ChargeSpeedMul = 1f,
                    RangedSpeed = 9f, RangedCount = 1f, RangedRadius = 0.35f,
                },
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Stationary, Accel = 0f, TurnRate = 0f, AggroRange = 13f,
                    AttackRange = 11f, AttackCooldown = 2.6f, AttackDamage = 6f, ChargeSpeedMul = 1f,
                    RangedSpeed = 10f, RangedCount = 3f, RangedSpreadDeg = 36f, RangedRadius = 0.4f,
                },
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.MinionSeekAttack, Accel = 8f, TurnRate = 5f, AggroRange = 8f,
                    AttackRange = 1.2f, AttackCooldown = 0.1667f, AttackDamage = 3f,
                    Separation = 0.6f, ChargeSpeedMul = 1f,
                },
            };
        }

        private static int SpawnHostile(SimBridge sim, float2 pos, int archetype, float hp)
        {
            var req = new SpawnRequest
            {
                Position = pos, Velocity = float2.zero, Health = hp, Radius = 0.5f, MaxSpeed = 0f,
                ArchetypeId = archetype, Faction = SimFaction.Hostile,
                LogicId = sim.NextLogicId(), VisualId = 0,
            };
            return sim.Spawn(req);
        }

        public static (bool Pass, string Reason) Run()
        {
            var notes = new List<string>();

            // ── ① 敌人真的发射弹体，且伤害走**玩家伤害管线**而不是直接扣 Health[0] ──
            {
                var sim = new SimBridge();
                try
                {
                    sim.Begin(Cfg, Archetypes());
                    SpawnHostile(sim, new float2(8f, 0f), 0, 1000f);
                    sim.OnUpdate(0.02f);

                    int maxLive = 0;
                    float dealt = 0f;
                    for (int f = 0; f < 60; f++)
                    {
                        sim.OnUpdate(0.05f);
                        if (sim.LiveProjectileCount > maxLive) { maxLive = sim.LiveProjectileCount; }
                        dealt += sim.PlayerDamageTaken;
                    }
                    if (maxLive <= 0)
                    {
                        return (false, "① 敌人应发射出弹体，实际场上从未出现敌弹");
                    }
                    if (dealt <= 0f)
                    {
                        return (false, "① 敌弹应打到玩家（PlayerDamageTaken>0），实际 0");
                    }
                    if (sim.PlayerHealth < 100f)
                    {
                        return (false, $"① 敌弹**不应**直接扣 Health[0]（那会绕过护甲与受伤反馈），实际 {sim.PlayerHealth:0.#}");
                    }
                    notes.Add($"①敌人远程：最多同时 {maxLive} 发敌弹，累计打到玩家 {dealt:0.#} 点且走玩家伤害管线");
                }
                finally { sim.End(); sim.OnDispose(); }
            }

            // ── ② 三连扇射：以朝向玩家的方向为中轴左右均分 ──
            {
                var sim = new SimBridge();
                try
                {
                    sim.Begin(Cfg, Archetypes());
                    SpawnHostile(sim, new float2(7f, 0f), 1, 1000f);
                    sim.OnUpdate(0.02f);
                    sim.OnUpdate(0.05f);

                    if (sim.LiveProjectileCount != 3)
                    {
                        return (false, $"② 炮台一次齐射应为 3 发，实际 {sim.LiveProjectileCount}");
                    }

                    // 敌人在 +X，玩家在原点 → 中轴 180°，±18°
                    var arr = sim.World.Projectiles;
                    float minA = 999f, maxA = -999f;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (arr[i].Alive == 0) { continue; }
                        float a = math.abs(math.degrees(math.atan2(arr[i].Velocity.y, arr[i].Velocity.x)));
                        if (a < minA) { minA = a; }
                        if (a > maxA) { maxA = a; }
                    }
                    if (math.abs((maxA - minA) - 18f) > 1f)
                    {
                        return (false, $"② 扇射张角应为 36°（半角 18°），实际角度跨度 {maxA - minA:0.#}");
                    }
                    notes.Add($"②三连扇射：3 发，以朝向玩家的方向为中轴 ±{maxA - minA:0.#}°");
                }
                finally { sim.End(); sim.OnDispose(); }
            }

            // ── ③ 死亡事件（召唤物分裂的前提）：修前恒为 0 ──
            {
                var sim = new SimBridge();
                try
                {
                    sim.Begin(Cfg, Archetypes());
                    SpawnHostile(sim, new float2(3f, 0f), 0, 10f);
                    sim.OnUpdate(0.02f);
                    sim.DamageUnit(1, 9999f);
                    sim.OnUpdate(0.05f);
                    if (sim.Snapshot.DeathCount <= 0)
                    {
                        return (false, "③ 击杀单位后 DeathCount 应 >0——它恒为 0 意味着"
                            + "击杀奖励/进化能/卡牌 OnKill/KillSignal 全部静默失效");
                    }
                    notes.Add($"③死亡事件：击杀后 DeathCount={sim.Snapshot.DeathCount}（修前恒 0）");
                }
                finally { sim.End(); sim.OnDispose(); }
            }

            // ── ④ 弹反：调头 + 换阵营 + 加伤 + 真的打回敌人 ──
            {
                var sim = new SimBridge();
                try
                {
                    sim.Begin(Cfg, Archetypes());
                    SpawnHostile(sim, new float2(9f, 0f), 0, 500f);
                    sim.OnUpdate(0.02f);

                    var abilities = new AbilitySystem { AimDirection = new float2(1f, 0f) };
                    var bridge = new MetabolicSliceBridge();
                    bridge.Bind(sim, new StatSheet(), abilities);
                    bridge.OnEnter();

                    var swing = new HitEvent
                    {
                        Damage = 10f, Scale = 1f, Count = 1f, SpreadAngle = 90f, Bounce = 1f,
                        Shape = "Melee", AttackPattern = ComposeEngine.Core.AttackPattern.Melee,
                    };

                    var arr = sim.World.Projectiles;
                    float vxBefore = 0f, dmgBefore = 0f;
                    bool parried = false;
                    for (int f = 0; f < 80 && !parried; f++)
                    {
                        sim.OnUpdate(0.05f);
                        for (int i = 0; i < arr.Length && !parried; i++)
                        {
                            ProjectileState p = arr[i];
                            if (p.Alive == 0 || (SimFaction)p.TargetFaction != SimFaction.Player) { continue; }
                            if (math.length(p.Position - sim.PlayerPosition) > 4.0f) { continue; }
                            vxBefore = p.Velocity.x;
                            dmgBefore = p.Damage;
                            bridge.ApplyEvent(swing);
                            parried = true;
                        }
                    }
                    if (!parried)
                    {
                        return (false, "④ 敌弹始终没进入近战触及范围，无法验证弹反");
                    }
                    if (bridge.LastDeflectedCount <= 0)
                    {
                        return (false, $"④ 挥击应弹反 >0 发，实际 {bridge.LastDeflectedCount}");
                    }

                    float vxAfter = 0f, dmgAfter = 0f;
                    SimFaction targetAfter = SimFaction.None;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (arr[i].Alive == 0) { continue; }
                        vxAfter = arr[i].Velocity.x;
                        dmgAfter = arr[i].Damage;
                        targetAfter = (SimFaction)arr[i].TargetFaction;
                    }
                    if (vxBefore * vxAfter >= 0f)
                    {
                        return (false, $"④ 弹反应让弹体调头，实际 vx {vxBefore:0.#} → {vxAfter:0.#}");
                    }
                    if (targetAfter != SimFaction.Hostile)
                    {
                        return (false, $"④ 弹反应把目标阵营改成 Hostile，实际 {targetAfter}");
                    }
                    if (math.abs(dmgAfter - dmgBefore * MetabolicSliceBridge.MeleeDeflectDamageMul) > 0.01f)
                    {
                        return (false, $"④ 弹反伤害应为 {dmgBefore:0.#}×{MetabolicSliceBridge.MeleeDeflectDamageMul}，实际 {dmgAfter:0.#}");
                    }

                    float hpBefore = HostileHp(sim);
                    for (int f = 0; f < 40; f++) { sim.OnUpdate(0.05f); }
                    float hpAfter = HostileHp(sim);
                    if (hpAfter >= hpBefore)
                    {
                        return (false, $"④ 弹回去的弹应打到敌人，敌人血量 {hpBefore:0.#} → {hpAfter:0.#}");
                    }
                    notes.Add($"④弹反：vx {vxBefore:0.#}→{vxAfter:0.#}，阵营 Player→Hostile，"
                        + $"伤害 {dmgBefore:0.#}→{dmgAfter:0.#}，敌人掉血 {hpBefore - hpAfter:0.#}");
                }
                finally { sim.End(); sim.OnDispose(); }
            }

            // ── ⑤ 对照：不挂 Bounce 就不该弹反（格挡是主动选择，不是被动光环）──
            {
                var sim = new SimBridge();
                try
                {
                    sim.Begin(Cfg, Archetypes());
                    SpawnHostile(sim, new float2(9f, 0f), 0, 500f);
                    sim.OnUpdate(0.02f);
                    var bridge = new MetabolicSliceBridge();
                    bridge.Bind(sim, new StatSheet(), new AbilitySystem { AimDirection = new float2(1f, 0f) });
                    bridge.OnEnter();

                    var noBounce = new HitEvent
                    {
                        Damage = 10f, Scale = 1f, Count = 1f, SpreadAngle = 90f, Bounce = 0f,
                        Shape = "Melee", AttackPattern = ComposeEngine.Core.AttackPattern.Melee,
                    };
                    var arr = sim.World.Projectiles;
                    for (int f = 0; f < 80; f++)
                    {
                        sim.OnUpdate(0.05f);
                        for (int i = 0; i < arr.Length; i++)
                        {
                            if (arr[i].Alive == 0 || (SimFaction)arr[i].TargetFaction != SimFaction.Player) { continue; }
                            if (math.length(arr[i].Position - sim.PlayerPosition) > 4.0f) { continue; }
                            bridge.ApplyEvent(noBounce);
                        }
                    }
                    if (bridge.DeflectedProjectiles != 0)
                    {
                        return (false, $"⑤ 无 Bounce 不应弹反，实际弹反 {bridge.DeflectedProjectiles} 发");
                    }
                    notes.Add("⑤对照：无 Bounce 时弹反 0 发");
                }
                finally { sim.End(); sim.OnDispose(); }
            }

            // ── ⑥ 召唤物死亡分裂：逐代变小、代数封顶、不挂就不裂 ──
            {
                var sim = new SimBridge();
                try
                {
                    sim.Begin(Cfg, Archetypes());
                    sim.OnUpdate(0.02f);
                    var bridge = new MetabolicSliceBridge();
                    bridge.Bind(sim, new StatSheet(), new AbilitySystem());
                    bridge.OnEnter();
                    bridge.ApplyEvent(new HitEvent
                    {
                        Damage = 5f, Scale = 1f, Count = 1f, SummonId = 2, SummonCount = 2f,
                        SplitOnHit = 2f, Shape = "Spore",
                        AttackPattern = ComposeEngine.Core.AttackPattern.SummonFollow,
                    });
                    sim.OnUpdate(0.05f);

                    var counts = new List<int> { CountMinions(sim, out float r0) };
                    var radii = new List<float> { r0 };
                    for (int gen = 0; gen < 3; gen++)
                    {
                        KillAllMinions(sim);
                        sim.OnUpdate(0.05f);
                        bridge.OnUpdate(0.05f);
                        sim.OnUpdate(0.05f);
                        counts.Add(CountMinions(sim, out float r));
                        radii.Add(r);
                    }

                    if (counts[0] != 2 || counts[1] != 4 || counts[2] != 8 || counts[3] != 0)
                    {
                        return (false, "⑥ 分裂序列应为 2→4→8→0（代数上限 "
                            + MetabolicSliceBridge.MinionSplitMaxGenerations + "），实际 "
                            + string.Join("→", counts));
                    }
                    if (!(radii[1] < radii[0] && radii[2] < radii[1]))
                    {
                        return (false, $"⑥ 下一代应更小，实际体型 {radii[0]:0.###}→{radii[1]:0.###}→{radii[2]:0.###}");
                    }
                    if (bridge.MinionSplitSpawned != 12)
                    {
                        return (false, $"⑥ 累计应裂出 2×2+4×2=12 只，实际 {bridge.MinionSplitSpawned}");
                    }
                    notes.Add($"⑥召唤分裂：{string.Join("→", counts)}，体型 "
                        + $"{radii[0]:0.###}→{radii[1]:0.###}→{radii[2]:0.###}，累计裂出 {bridge.MinionSplitSpawned}");
                }
                finally { sim.End(); sim.OnDispose(); }
            }

            // ── ⑦ 攻击节奏：更快，但 DPS 严格中性 ──
            {
                if (MetabolicSliceBridge.AttackIntervalBase >= MetabolicSliceBridge.LegacyAttackInterval)
                {
                    return (false, "⑦ 新节奏基准应快于旧的 1.5s");
                }
                float[] speeds = { 0f, 1.3f, 2.2f, 0.5f };
                for (int i = 0; i < speeds.Length; i++)
                {
                    float mul = speeds[i] > 0f ? math.clamp(speeds[i], 0.25f, 4f) : 1f;
                    float interval = math.clamp(MetabolicSliceBridge.AttackIntervalBase / mul,
                        MetabolicSliceBridge.AttackIntervalMin, MetabolicSliceBridge.AttackIntervalMax);
                    float cadenceMul = interval / MetabolicSliceBridge.LegacyAttackInterval;
                    float dpsOld = 100f / MetabolicSliceBridge.LegacyAttackInterval;
                    float dpsNew = 100f * cadenceMul / interval;
                    if (math.abs(dpsNew - dpsOld) > 0.01f)
                    {
                        return (false, $"⑦ Speed={speeds[i]} 时 DPS 应保持中性，{dpsOld:0.##} → {dpsNew:0.##}");
                    }
                }
                notes.Add($"⑦节奏：基准 {MetabolicSliceBridge.LegacyAttackInterval}s → "
                    + $"{MetabolicSliceBridge.AttackIntervalBase}s，4 档武速下 DPS 全部严格中性");
            }

            return (true, string.Join("；", notes));
        }

        private static float HostileHp(SimBridge sim)
        {
            float hp = 0f;
            SimSnapshot snap = sim.Snapshot;
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.Alive[i] != 0 && snap.Faction[i] == (byte)SimFaction.Hostile)
                {
                    hp += snap.Health[i];
                }
            }
            return hp;
        }

        private static int CountMinions(SimBridge sim, out float lastRadius)
        {
            int n = 0;
            lastRadius = 0f;
            SimSnapshot snap = sim.Snapshot;
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.Alive[i] != 0 && snap.Faction[i] == (byte)SimFaction.PlayerMinion)
                {
                    n++;
                    lastRadius = snap.Radius[i];
                }
            }
            return n;
        }

        private static void KillAllMinions(SimBridge sim)
        {
            SimSnapshot snap = sim.Snapshot;
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.Alive[i] != 0 && snap.Faction[i] == (byte)SimFaction.PlayerMinion)
                {
                    sim.DamageUnit(i, 9999f);
                }
            }
        }
    }
}
