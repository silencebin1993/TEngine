using System.Collections.Generic;
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
    /// enemy-mechanics-parity：敌人补齐到与玩家对等的机制，外加三件旧的"明确未做"。
    ///
    /// <list type="number">
    /// <item><b>玩家掉血下沉进内核</b>——此前是"内核累加 → 热更层某个系统读走再回头扣"，
    /// 少一个消费者敌人的伤害就静默消失。</item>
    /// <item><b>持续区域成为内核实体</b>（前提）——毒坑/光环原本只活在热更层的玩家专属结构里，
    /// 敌人根本用不上。</item>
    /// <item><b>敌人放毒 / 贴身毒环 / 孵化</b>。</item>
    /// <item><b>光环弹反</b>与 <b>gene_receptor 受体记忆</b>。</item>
    /// </list>
    /// </summary>
    public static class EnemyMechanicsParitySmokeReport
    {
        private static SimConfig Cfg => new SimConfig
        {
            UnitCapacity = 64, ProjectileCapacity = 64, ZoneCapacity = 64,
            ArenaHalfExtent = 90f, HashCellSize = 4f,
            MaxDeathEventsPerFrame = 64, MaxHitEventsPerFrame = 64, RandomSeed = 1u,
            BreachedDiscount = 0.7f, CorrodedDiscount = 0.85f,
        };

        /// <summary>0=贴身毒环 1=酸沼投手（远程+落点毒沼） 2=孵化巢 3=被孵出的小怪 4=纯远程。</summary>
        private static BehaviorArchetype[] Archetypes()
        {
            return new[]
            {
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Drift, Accel = 0f, AttackRange = 0.3f, AttackCooldown = 1.5f,
                    ChargeSpeedMul = 1f, ZoneMode = 3f, ZoneRadius = 3.2f, ZoneSeconds = 2f,
                    ZoneDamagePerTick = 2f, ZoneTickInterval = 0.5f, ZoneCooldown = 1.8f,
                },
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Ranged, Accel = 0f, AggroRange = 24f, AttackRange = 13f,
                    AttackCooldown = 3f, AttackDamage = 5f, ChargeSpeedMul = 1f,
                    RangedSpeed = 12f, RangedCount = 1f, RangedRadius = 0.35f,
                    ZoneMode = 1f, ZoneRadius = 3f, ZoneSeconds = 4f,
                    ZoneDamagePerTick = 3f, ZoneTickInterval = 0.6f, ZoneCooldown = 3f,
                },
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Stationary, Accel = 1f, AggroRange = 14f, AttackRange = 2f,
                    AttackCooldown = 1.6f, AttackDamage = 0f, ChargeSpeedMul = 1f,
                    SummonArchetypeId = 3f, SummonCount = 2f, SummonCooldown = 2f, SummonHealth = 8f,
                },
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Swarm, Accel = 7f, TurnRate = 5f, AggroRange = 30f,
                    AttackRange = 0.35f, AttackCooldown = 0.9f, AttackDamage = 3f, ChargeSpeedMul = 1f,
                },
                new BehaviorArchetype
                {
                    Kind = BehaviorKind.Ranged, Accel = 0f, AggroRange = 24f, AttackRange = 12f,
                    AttackCooldown = 2f, AttackDamage = 7f, ChargeSpeedMul = 1f,
                    RangedSpeed = 9f, RangedCount = 1f, RangedRadius = 0.35f,
                },
            };
        }

        private static void Spawn(SimBridge sim, float2 pos, int archetype, float hp, float radius = 0.5f)
        {
            var req = new SpawnRequest
            {
                Position = pos, Velocity = float2.zero, Health = hp, Radius = radius, MaxSpeed = 0f,
                ArchetypeId = archetype, Faction = SimFaction.Hostile,
                LogicId = sim.NextLogicId(), VisualId = 0,
            };
            sim.Spawn(req);
        }

        private static int CountHostiles(SimBridge sim)
        {
            int n = 0;
            SimSnapshot snap = sim.Snapshot;
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.Alive[i] != 0 && snap.Faction[i] == (byte)SimFaction.Hostile) { n++; }
            }
            return n;
        }

        public static (bool Pass, string Reason) Run()
        {
            var notes = new List<string>();

            // ── ① 贴身毒环：靠近持续掉血，走远就不掉；且**内核自己扣了血** ──
            {
                var near = new SimBridge();
                var far = new SimBridge();
                try
                {
                    near.Begin(Cfg, Archetypes());
                    Spawn(near, new float2(2f, 0f), 0, 1000f);
                    near.OnUpdate(0.02f);
                    float nearDmg = 0f;
                    int zonePeak = 0;
                    for (int f = 0; f < 60; f++)
                    {
                        near.OnUpdate(0.05f);
                        nearDmg += near.PlayerDamageTaken;
                        if (near.LiveZoneCount > zonePeak) { zonePeak = near.LiveZoneCount; }
                    }

                    far.Begin(Cfg, Archetypes());
                    Spawn(far, new float2(40f, 0f), 0, 1000f);
                    far.OnUpdate(0.02f);
                    float farDmg = 0f;
                    for (int f = 0; f < 60; f++)
                    {
                        far.OnUpdate(0.05f);
                        farDmg += far.PlayerDamageTaken;
                    }

                    if (zonePeak <= 0)
                    {
                        return (false, "① 贴身毒环应在场上产生持续区域，实际 LiveZoneCount 峰值 0");
                    }
                    if (nearDmg <= 0f)
                    {
                        return (false, "① 站在毒环里应持续掉血，实际 0");
                    }
                    if (farDmg > 0f)
                    {
                        return (false, $"① 站在毒环外不该掉血，实际 {farDmg:0.#}");
                    }
                    // 关键：血量是**内核**扣的，不依赖任何热更层系统去消费快照。
                    if (near.PlayerHealth >= 100f)
                    {
                        return (false, $"① 玩家血量应由内核直接扣减，实际仍是 {near.PlayerHealth:0.#}");
                    }
                    notes.Add($"①贴身毒环：圈内 3 秒受伤 {nearDmg:0.#}（血量 100→{near.PlayerHealth:0.#}，内核直接扣），"
                        + $"圈外 {farDmg:0.#}，区域峰值 {zonePeak}");
                }
                finally
                {
                    near.End(); near.OnDispose();
                    far.End(); far.OnDispose();
                }
            }

            // ── ② 酸沼投手：远程弹 + 在玩家脚下留沼 ──
            {
                var sim = new SimBridge();
                try
                {
                    sim.Begin(Cfg, Archetypes());
                    Spawn(sim, new float2(10f, 0f), 1, 1000f);
                    sim.OnUpdate(0.02f);
                    float dmg = 0f;
                    int zonePeak = 0, projPeak = 0;
                    for (int f = 0; f < 80; f++)
                    {
                        sim.OnUpdate(0.05f);
                        dmg += sim.PlayerDamageTaken;
                        if (sim.LiveZoneCount > zonePeak) { zonePeak = sim.LiveZoneCount; }
                        if (sim.LiveProjectileCount > projPeak) { projPeak = sim.LiveProjectileCount; }
                    }
                    if (zonePeak <= 0 || projPeak <= 0)
                    {
                        return (false, $"② 酸沼投手应同时有弹体与酸沼，实际 弹体峰值 {projPeak} 酸沼峰值 {zonePeak}");
                    }
                    if (dmg <= 0f)
                    {
                        return (false, "② 酸沼应打到站着不动的玩家，实际 0");
                    }
                    notes.Add($"②酸沼投手：弹体峰值 {projPeak}，酸沼峰值 {zonePeak}，玩家累计受伤 {dmg:0.#}");
                }
                finally { sim.End(); sim.OnDispose(); }
            }

            // ── ③ 孵化巢：周期孵小怪，不清掉就一直加压 ──
            {
                var sim = new SimBridge();
                try
                {
                    sim.Begin(Cfg, Archetypes());
                    Spawn(sim, new float2(8f, 0f), 2, 1000f, 1f);
                    sim.OnUpdate(0.02f);
                    var series = new List<int>();
                    for (int round = 0; round < 3; round++)
                    {
                        for (int f = 0; f < 50; f++) { sim.OnUpdate(0.05f); }
                        series.Add(CountHostiles(sim));
                    }
                    if (!(series[0] > 1 && series[1] > series[0] && series[2] > series[1]))
                    {
                        return (false, "③ 孵化巢应持续孵出小怪（敌人总数单调增），实际 "
                            + string.Join("→", series));
                    }
                    notes.Add($"③孵化巢：敌人总数 {string.Join("→", series)}（不清掉会一直加压）");
                }
                finally { sim.End(); sim.OnDispose(); }
            }

            // ── ④ 光环弹反：贴身圈也能弹弹幕，但覆盖面与回报都弱于近战主动格挡 ──
            {
                if (MetabolicSliceBridge.AuraDeflectRadiusMul >= 1f)
                {
                    return (false, "④ 光环弹反不该覆盖整圈——站桩全挡会让弹幕机制直接失效");
                }
                if (MetabolicSliceBridge.AuraDeflectDamageMul >= MetabolicSliceBridge.MeleeDeflectDamageMul)
                {
                    return (false, "④ 光环弹反（被动）的回报应低于近战格挡（主动挥出去的扇形）");
                }

                var sim = new SimBridge();
                try
                {
                    sim.Begin(Cfg, Archetypes());
                    Spawn(sim, new float2(9f, 0f), 4, 500f);
                    sim.OnUpdate(0.02f);
                    var bridge = new MetabolicSliceBridge();
                    bridge.Bind(sim, new StatSheet(), new AbilitySystem { AimDirection = new float2(1f, 0f) });
                    bridge.OnEnter();

                    var auraSwing = new HitEvent
                    {
                        Damage = 8f, Scale = 1f, Count = 1f, AuraRadius = 6f, Bounce = 1f,
                        Shape = "Field", AttackPattern = ComposeEngine.Core.AttackPattern.Aura,
                    };
                    NativeArrayProbe(sim, bridge, auraSwing);

                    if (bridge.DeflectedProjectiles <= 0)
                    {
                        return (false, $"④ 光环应弹反冲自己来的弹，实际 {bridge.DeflectedProjectiles} 发");
                    }
                    notes.Add($"④光环弹反：弹回 {bridge.DeflectedProjectiles} 发，"
                        + $"覆盖圈内 {MetabolicSliceBridge.AuraDeflectRadiusMul:P0}、伤害 ×{MetabolicSliceBridge.AuraDeflectDamageMul}"
                        + $"（近战主动格挡是整扇形 ×{MetabolicSliceBridge.MeleeDeflectDamageMul}）");
                }
                finally { sim.End(); sim.OnDispose(); }
            }

            // ── ⑤ gene_receptor 受体记忆：命中留 Marked，之后的弹越过近目标去追老目标 ──
            {
                var evt = new HitEvent
                {
                    Damage = 10f, Scale = 1f, Count = 1f, Homing = 0.5f, Speed = 1f,
                    Shape = "Bolt", AttackPattern = ComposeEngine.Core.AttackPattern.Projectile,
                };
                evt.Tags.Add("ReceptorMemory");
                ProjectileRequest withMemory = CombatBallistics.Build(
                    evt, float2.zero, new float2(1f, 0f), 0, 1, 1f, 1, 0u);

                var plain = new HitEvent
                {
                    Damage = 10f, Scale = 1f, Count = 1f, Homing = 0.5f, Speed = 1f,
                    Shape = "Bolt", AttackPattern = ComposeEngine.Core.AttackPattern.Projectile,
                };
                ProjectileRequest noMemory = CombatBallistics.Build(
                    plain, float2.zero, new float2(1f, 0f), 0, 1, 1f, 1, 0u);

                if ((withMemory.ApplyStatus & SimStatus.Marked) == 0)
                {
                    return (false, "⑤ 受体记忆的弹应给命中目标挂 Marked（留下记号），实际没有");
                }
                if ((withMemory.Flags & SimProjectileFlags.PreferMarked) == 0)
                {
                    return (false, "⑤ 受体记忆的弹应带 PreferMarked（认得记号），实际没有");
                }
                if ((noMemory.ApplyStatus & SimStatus.Marked) != 0
                    || (noMemory.Flags & SimProjectileFlags.PreferMarked) != 0)
                {
                    return (false, "⑤ 对照：不带 ReceptorMemory 的弹不该有标记行为");
                }
                notes.Add($"⑤受体记忆：命中挂 Marked + 选靶偏好 PreferMarked（已标记目标距离 ×{JobProjectile.MarkedTargetBias}，"
                    + "等效搜敌距离翻倍），对照组两者皆无");
            }

            return (true, string.Join("；", notes));
        }

        /// <summary>让敌人开一枪、等它飞进光环、然后开一次光环。</summary>
        private static void NativeArrayProbe(SimBridge sim, MetabolicSliceBridge bridge, HitEvent auraEvt)
        {
            var arr = sim.World.Projectiles;
            for (int f = 0; f < 80; f++)
            {
                sim.OnUpdate(0.05f);
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i].Alive == 0 || (SimFaction)arr[i].TargetFaction != SimFaction.Player) { continue; }
                    // 光环弹反只覆盖内层，等它真的飞进去再开。
                    if (math.length(arr[i].Position - sim.PlayerPosition) > 3f) { continue; }
                    bridge.ApplyEvent(auraEvt);
                    return;
                }
            }
        }
    }
}
