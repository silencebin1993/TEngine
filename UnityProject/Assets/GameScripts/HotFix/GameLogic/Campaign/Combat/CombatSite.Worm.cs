using System.Collections.Generic;
using BinGames.Sim.Combat;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>FG12-EVT-02 掘进蠕虫的门面（热更玩法代码只经 Campaign/Combat/ 碰内核，FG14 §5 硬约束 3）。只在事件开始 / 结束、每个世界步读一次链状态时调用。</summary>
    public sealed partial class CombatSite
    {
        /// <summary>蠕虫各节外部键的基数：ExtKey = −(基数 + 链号 × 16 + 节序号)。</summary>
        public const int WormKeyBase = 4000000;

        private readonly List<double2> _wormRoute = new List<double2>(CombatWormConst.MaxRoute);
        private readonly List<int> _wormSegs = new List<int>(CombatWormConst.MaxSegments);

        /// <summary>
        /// 生成一条蠕虫：<paramref name="segments"/> 节敌方单位（原地咬人的近战、实例化画出来、阵亡即移除）并登记成链，从路线弧长 <paramref name="s0"/> 处钻出地面。
        /// 返回生成的节数（0 = 失败，已生成的会撤掉）。
        /// </summary>
        public int SpawnWorm(int chainId, IReadOnlyList<Vector2> route, int segments, float segmentHp, float radius, float spacing, float speed,
            float surfaceSeconds, float diveSeconds, float dragMult, double s0, float biteDamage, float biteRange, float biteCooldown)
        {
            if (IsDisposed || chainId <= 0 || route == null || route.Count < 2 || segments < 1 || segments > CombatWormConst.MaxSegments)
            {
                return 0;
            }
            MarkPlaceholderVisuals();
            int weapon = WeaponIndex(new CombatWeapon
            {
                Mode = CombatWeaponMode.Projectile,
                HasOutput = 1,
                TargetMode = CombatTargetMode.Nearest,
                Range = Mathf.Max(0.5f, biteRange),
                Damage = Mathf.Max(0f, biteDamage),
                Cooldown = Mathf.Max(0.1f, biteCooldown),
                ProjectileSpeed = 40f,
                ProjectileRadius = 0.3f,
            });
            _wormRoute.Clear();
            foreach (Vector2 p in route)
            {
                _wormRoute.Add(new double2(p.x, p.y));
            }
            _wormSegs.Clear();
            Vector2 at = route[0];
            for (int k = 0; k < segments; k++)
            {
                int unit = Kernel.Spawn(new CombatSpawn
                {
                    ExtKey = -(WormKeyBase + chainId * 16 + k),
                    Kind = CombatUnitKind.Enemy,
                    Faction = CombatFaction.Hostile,
                    Behavior = CombatBehavior.HoldFire,
                    Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled | CombatUnitFlags.Instanced
                            | CombatUnitFlags.RemoveOnDeath | CombatUnitFlags.Elite,
                    Position = new double2(at.x, at.y),
                    Home = new double2(at.x, at.y),
                    Radius = Mathf.Max(0.2f, radius),
                    Speed = 0f,
                    Health = Mathf.Max(1f, segmentHp),
                    MaxHealth = Mathf.Max(1f, segmentHp),
                    Weapon = weapon,
                    BehaviorProfile = -1,
                    Priority = 1,
                });
                if (unit <= 0)
                {
                    foreach (int u in _wormSegs)
                    {
                        Kernel.Despawn(u);
                    }
                    return 0;
                }
                _wormSegs.Add(unit);
                SetUnitLabel(unit, "vibration.segment", (k + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (!Kernel.AddWorm(chainId, _wormRoute, _wormSegs, speed, spacing, surfaceSeconds, diveSeconds, dragMult, s0))
            {
                foreach (int u in _wormSegs)
                {
                    Kernel.Despawn(u);
                }
                return 0;
            }
            return segments;
        }

        /// <summary>这条蠕虫各节的单位 ID（按节序；已阵亡的照样列出）。</summary>
        public int CopyWormSegments(int chainId, List<int> into)
        {
            into?.Clear();
            return IsDisposed ? 0 : Kernel.CopyWormSegs(chainId, into);
        }

        public bool TryGetWorm(int chainId, out CombatWorm worm)
        {
            worm = default;
            return !IsDisposed && Kernel.TryGetWorm(chainId, out worm);
        }

        public bool RemoveWorm(int chainId, bool despawn)
        {
            if (IsDisposed || Kernel.CopyWormSegs(chainId, _wormSegs) == 0)
            {
                return false;
            }
            foreach (int u in _wormSegs)
            {
                ClearUnitLabel(u);
            }
            return Kernel.RemoveWorm(chainId, despawn);
        }

        public bool SetWormLeaving(int chainId) => !IsDisposed && Kernel.SetWormLeaving(chainId);

        /// <summary>蠕虫累计伤害够了：其余节一起倒下（阵亡事件照常，阵亡即移除）。</summary>
        public bool KillWormSegment(int unitId) => !IsDisposed && Kernel.Kill(unitId, 0);

        public int WormCount => IsDisposed ? 0 : Kernel.WormCount;

        public int WormIdAt(int index) => IsDisposed || index < 0 || index >= Kernel.WormCount ? 0 : Kernel.WormAt(index).Id;

        /// <summary>这条蠕虫路线上弧长 <paramref name="s"/> 处的点（取模路线长）。</summary>
        public static Vector2 WormPoint(in CombatWorm w, double s)
        {
            double2 p = CombatKernel.WormPointOf(w, s);
            return new Vector2((float)p.x, (float)p.y);
        }

        /// <summary>FG12-EVT-02：半径不小于多少格的爆炸算大规模（读档 / 建好内核后写一次）。</summary>
        public void SetBlastMinRadius(float radius)
        {
            if (!IsDisposed)
            {
                Kernel.BlastMinRadius = radius;
            }
        }

        public int DrainBlasts(List<float4> into) => IsDisposed ? 0 : Kernel.DrainBlasts(into);

        /// <summary>自检：在 <paramref name="at"/> 记一次爆炸（与内核爆炸点同一入口）。</summary>
        public void NoteBlastForTests(Vector2 at, float radius)
        {
            if (!IsDisposed)
            {
                Kernel.NoteBlastForTests(new double2(at.x, at.y), radius);
            }
        }
    }
}
