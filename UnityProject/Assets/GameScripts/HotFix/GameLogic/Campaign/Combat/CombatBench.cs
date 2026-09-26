using System.Collections.Generic;
using BinGames.Sim.Combat;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG0-ARCH-03：家园突袭的战斗原型——突袭者（敌方，扑向己方目标、弹体武器）与炮塔（己方，驻守开火、弹体武器），
    /// 以及 FG14 第 4 节的性能场景（200 个敌人、80 座炮塔、≥1,500 个弹体同时存在）。
    ///
    /// 正式的突袭导演、编成、攻城目标选择、结算属于 FG6-DEF-04～08（DEBT-FG0ARCH03-01）：它们在行进中的突袭到达时调用
    /// <see cref="SpawnRaidGroup"/> 把聚合队伍展开成内核单位；炮塔的放置、蓝图、目标模式、补给属于 FG6-DEF-01～03。
    /// 本 Story 只交付“逐单位 / 逐弹体逻辑在内核里跑、规模达标、存读档与后台一致”，数值来自 combat.bench.* / combat.perf.*（原型值）。
    /// </summary>
    public static class CombatBench
    {
        public struct Spec
        {
            public float RaiderHp, RaiderSpeed, RaiderRadius, RaiderSense, RaiderRange, RaiderDamage, RaiderCooldown, RaiderProjectileSpeed, RaiderProjectileRadius;
            public float TurretHp, TurretRadius, TurretRange, TurretDamage, TurretCooldown, TurretProjectileSpeed, TurretProjectileRadius;
        }

        /// <summary>突袭 / 炮塔原型数值（combat.bench.*）。</summary>
        public static Spec FromTuning() => new Spec
        {
            RaiderHp = CombatSite.Tuning("combat.bench.raider_hp", 120f),
            RaiderSpeed = CombatSite.Tuning("combat.bench.raider_speed", 3.5f),
            RaiderRadius = CombatSite.Tuning("combat.bench.raider_radius", 0.6f),
            RaiderSense = CombatSite.Tuning("combat.bench.raider_sense", 60f),
            RaiderRange = CombatSite.Tuning("combat.bench.raider_range", 16f),
            RaiderDamage = CombatSite.Tuning("combat.bench.raider_damage", 6f),
            RaiderCooldown = CombatSite.Tuning("combat.bench.raider_cooldown", 1.2f),
            RaiderProjectileSpeed = CombatSite.Tuning("combat.bench.raider_projectile_speed", 18f),
            RaiderProjectileRadius = CombatSite.Tuning("combat.bench.raider_projectile_radius", 0.15f),
            TurretHp = CombatSite.Tuning("combat.bench.turret_hp", 400f),
            TurretRadius = CombatSite.Tuning("combat.bench.turret_radius", 1f),
            TurretRange = CombatSite.Tuning("combat.bench.turret_range", 26f),
            TurretDamage = CombatSite.Tuning("combat.bench.turret_damage", 14f),
            TurretCooldown = CombatSite.Tuning("combat.bench.turret_cooldown", 0.5f),
            TurretProjectileSpeed = CombatSite.Tuning("combat.bench.turret_projectile_speed", 24f),
            TurretProjectileRadius = CombatSite.Tuning("combat.bench.turret_projectile_radius", 0.2f),
        };

        /// <summary>
        /// 性能场景的数值（combat.perf.*）：在原型数值上把开火更密、弹速更慢、耐久更高，
        /// 让“200 敌人 + 80 炮塔 + ≥1,500 弹体”在测量窗口里持续同时存在（不是一次性齐射的瞬时峰值）。
        /// </summary>
        public static Spec PerfSpec()
        {
            Spec s = FromTuning();
            float hpScale = CombatSite.Tuning("combat.perf.hp_scale", 25f);
            s.RaiderHp *= hpScale;
            s.TurretHp *= hpScale;
            s.TurretCooldown = CombatSite.Tuning("combat.perf.turret_cooldown", 0.25f);
            s.RaiderCooldown = CombatSite.Tuning("combat.perf.raider_cooldown", 0.5f);
            float speed = CombatSite.Tuning("combat.perf.projectile_speed", 8f);
            s.TurretProjectileSpeed = speed;
            s.RaiderProjectileSpeed = speed;
            s.TurretRange = CombatSite.Tuning("combat.perf.turret_range", 30f);
            s.RaiderRange = CombatSite.Tuning("combat.perf.raider_range", 20f);
            return s;
        }

        public static int TurretWeapon(CombatSite site, in Spec s) => site.WeaponIndex(new CombatWeapon
        {
            Mode = CombatWeaponMode.Projectile,
            HasOutput = 1,
            TargetMode = CombatTargetMode.Nearest,
            Range = s.TurretRange,
            Damage = s.TurretDamage,
            Cooldown = s.TurretCooldown,
            ProjectileSpeed = s.TurretProjectileSpeed,
            ProjectileRadius = s.TurretProjectileRadius,
        });

        public static int RaiderWeapon(CombatSite site, in Spec s) => site.WeaponIndex(new CombatWeapon
        {
            Mode = CombatWeaponMode.Projectile,
            HasOutput = 1,
            TargetMode = CombatTargetMode.Nearest,
            Range = s.RaiderRange,
            Damage = s.RaiderDamage,
            Cooldown = s.RaiderCooldown,
            ProjectileSpeed = s.RaiderProjectileSpeed,
            ProjectileRadius = s.RaiderProjectileRadius,
        });

        /// <summary>一座炮塔（己方、驻守开火、弹体武器、实例化绘制）。返回单位 ID。</summary>
        public static int SpawnTurret(CombatSite site, Vector2 at, in Spec s, int weapon)
        {
            site.MarkPlaceholderVisuals();
            return site.Kernel.Spawn(new CombatSpawn
            {
                ExtKey = -1,
                Kind = CombatUnitKind.Turret,
                Faction = CombatFaction.Player,
                Behavior = CombatBehavior.HoldFire,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled | CombatUnitFlags.Instanced | CombatUnitFlags.RemoveOnDeath,
                Position = new double2(at.x, at.y),
                Home = new double2(at.x, at.y),
                Radius = s.TurretRadius,
                Speed = 0f,
                Health = s.TurretHp,
                MaxHealth = s.TurretHp,
                Weapon = weapon,
                BehaviorProfile = -1,
                Priority = 1,
                ArmorHalfAngleDeg = 90f,
                ArmorFacing = new float2(0f, -1f),
            });
        }

        /// <summary>一个突袭者（敌方、扑向感知范围内的己方目标，否则朝 <paramref name="goal"/> 前进）。返回单位 ID。</summary>
        public static int SpawnRaider(CombatSite site, Vector2 at, Vector2 goal, in Spec s, int weapon, int profile)
        {
            site.MarkPlaceholderVisuals();
            return site.Kernel.Spawn(new CombatSpawn
            {
                ExtKey = -1,
                Kind = CombatUnitKind.Enemy,
                Faction = CombatFaction.Hostile,
                Behavior = CombatBehavior.Raider,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled | CombatUnitFlags.Instanced | CombatUnitFlags.RemoveOnDeath,
                Position = new double2(at.x, at.y),
                Home = new double2(goal.x, goal.y),
                Radius = s.RaiderRadius,
                Speed = s.RaiderSpeed,
                Health = s.RaiderHp,
                MaxHealth = s.RaiderHp,
                Weapon = weapon,
                BehaviorProfile = profile,
                Priority = 1,
                ArmorHalfAngleDeg = 90f,
                ArmorFacing = new float2(0f, -1f),
            });
        }

        public static int RaiderProfile(CombatSite site, in Spec s) => site.ProfileIndex(new CombatBehaviorProfile
        {
            Speed = s.RaiderSpeed,
            SenseRange = s.RaiderSense,
        });

        /// <summary>
        /// 把一支到达的突袭队伍展开成内核单位（FG6-DEF-05 在 <c>TransitGroupState.Arrived</c> 时调用；本 Story 的测试与冒烟用它作测试捷径）。
        /// 在 <paramref name="arrival"/> 附近按确定性的环形排布生成 <paramref name="count"/> 个突袭者，目标点 = <paramref name="goal"/>（归还核心）。
        /// </summary>
        public static List<int> SpawnRaidGroup(CombatSite site, Vector2 arrival, Vector2 goal, int count, in Spec s)
        {
            var ids = new List<int>(count);
            if (site == null || site.IsDisposed || count <= 0)
            {
                return ids;
            }
            int weapon = RaiderWeapon(site, s);
            int profile = RaiderProfile(site, s);
            for (int i = 0; i < count; i++)
            {
                float ring = 1.5f + 1.2f * (i / 16);
                float ang = (i % 16) / 16f * Mathf.PI * 2f + 0.37f * (i / 16);
                Vector2 at = arrival + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * ring;
                ids.Add(SpawnRaider(site, at, goal, s, weapon, profile));
            }
            return ids;
        }

        /// <summary>一圈炮塔（性能场景 / 测试捷径）。</summary>
        public static List<int> SpawnTurretRing(CombatSite site, Vector2 center, float radius, int count, in Spec s)
        {
            var ids = new List<int>(count);
            if (site == null || site.IsDisposed || count <= 0)
            {
                return ids;
            }
            int weapon = TurretWeapon(site, s);
            for (int i = 0; i < count; i++)
            {
                float ang = i / (float)count * Mathf.PI * 2f;
                ids.Add(SpawnTurret(site, center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * radius, s, weapon));
            }
            return ids;
        }

        /// <summary>
        /// FG14 第 4 节 FG0-ARCH-03 性能场景：<paramref name="center"/> 周围一圈 <paramref name="turrets"/> 座炮塔（半径 22 米），
        /// <paramref name="enemies"/> 个突袭者分四路从 45 米外压上来，目标点 = 中心。全部是内核单位（实例化绘制）。
        /// </summary>
        public static void SpawnPerfScenario(CombatSite site, Vector2 center, int enemies, int turrets, in Spec s)
        {
            SpawnTurretRing(site, center, 22f, turrets, s);
            int perGroup = Mathf.Max(1, enemies / 4);
            int spawned = 0;
            for (int g = 0; g < 4 && spawned < enemies; g++)
            {
                float ang = g * Mathf.PI * 0.5f + 0.4f;
                Vector2 arrival = center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 45f;
                int n = g == 3 ? enemies - spawned : Mathf.Min(perGroup, enemies - spawned);
                spawned += SpawnRaidGroup(site, arrival, center, n, s).Count;
            }
        }

        /// <summary>移除全部原型单位（突袭者、炮塔：外部键 -1）——冒烟 / 自检结束后清场用。遍历在内核里（一次压实）。</summary>
        public static int ClearPrototypeUnits(CombatSite site) => site == null || site.IsDisposed ? 0 : site.Kernel.DespawnAnonymous();
    }
}
