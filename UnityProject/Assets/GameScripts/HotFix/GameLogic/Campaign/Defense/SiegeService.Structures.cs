using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>
    /// FG6-DEF-05：攻城剧场里的建筑结构单位。
    /// 炮塔 / 防御建筑之外、已建成、占地与剧场相交的建筑进内核当攻城目标（类别按 fg.TbSiegeCategory：核心 / 发电 / 信号（含电塔，FG-GAP-089）/ 监听站（FG-GAP-103）/ 其它），
    /// 炮塔与防御结构单位只标类别与占地。耐久双向对账（与 DefenseService 同一做法）：建筑记录被别处改过（维修 / 天气）→ 推给内核；否则内核的受伤写回建筑并记“最近挨打”（维修无人机优先修）。
    /// 归还核心带“耐久下限”（不在内核里阵亡；被打空的后果在 FG6-DEF-08）。
    /// </summary>
    public static partial class SiegeService
    {
        private sealed class StructRt
        {
            public float LastPushedHp = float.NaN;
        }

        private static readonly Dictionary<int, StructRt> Rt = new Dictionary<int, StructRt>();
        private static readonly HashSet<string> IdScratch = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<SiegeStructureRecord> RecScratch = new List<SiegeStructureRecord>(64);

        private static StructRt RuntimeOf(int serial)
        {
            if (!Rt.TryGetValue(serial, out StructRt rt))
            {
                rt = new StructRt();
                Rt[serial] = rt;
            }
            return rt;
        }

        /// <summary>这座建筑由攻城服务放进了内核（读耐久要问内核）。</summary>
        public static bool IsSiegeStructure(CampaignState state, BuildingRecord b) => FindRecord(state, b?.BuildingId) != null && HomeSite != null;

        public static SiegeStructureRecord FindRecord(CampaignState state, string buildingId)
        {
            SiegeState st = StateOf(state);
            if (st?.Structures == null || string.IsNullOrEmpty(buildingId))
            {
                return null;
            }
            foreach (SiegeStructureRecord r in st.Structures)
            {
                if (r != null && r.BuildingId == buildingId)
                {
                    return r;
                }
            }
            return null;
        }

        private static SiegeStructureRecord FindBySerial(CampaignState state, int serial)
        {
            foreach (SiegeStructureRecord r in StateOf(state)?.Structures ?? Array.Empty<SiegeStructureRecord>())
            {
                if (r != null && r.Serial == serial)
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>建筑此刻的耐久（在内核里时读内核）。</summary>
        public static float DurabilityOf(CampaignState state, BuildingRecord b)
        {
            SiegeStructureRecord r = FindRecord(state, b?.BuildingId);
            CombatSite site = HomeSite;
            if (r != null && site != null && site.TryGetSiegeStructHealth(r.Serial, out float hp, out _, out bool alive) && alive)
            {
                return hp;
            }
            return BuildingOps.Durability(b);
        }

        /// <summary>维修（无人机 / 自检）：建筑在内核里时直接加内核耐久并写回记录，返回实际修了多少。</summary>
        public static float Heal(CampaignState state, BuildingRecord b, float amount)
        {
            SiegeStructureRecord r = FindRecord(state, b?.BuildingId);
            CombatSite site = HomeSite;
            if (r == null || site == null || !site.TryGetSiegeStructHealth(r.Serial, out float hp, out float max, out bool alive) || !alive)
            {
                float before = BuildingOps.Durability(b);
                float after = Mathf.Min(BuildingOps.MaxDurability(b.BuildingTypeId), before + Mathf.Max(0f, amount));
                b.Health = after;
                return after - before;
            }
            float next = Mathf.Min(max, hp + Mathf.Max(0f, amount));
            site.SetSiegeStructHealth(r.Serial, next, max);
            b.Health = next;
            RuntimeOf(r.Serial).LastPushedHp = next;
            return next - hp;
        }

        private static bool InTheater(SiegeState st, BuildingRecord b, out int2 lo, out int2 hi)
        {
            FootScratch.Clear();
            HomeGridService.FootprintOf(b, FootScratch);
            lo = new int2(int.MaxValue, int.MaxValue);
            hi = new int2(int.MinValue, int.MinValue);
            foreach (GridCell c in FootScratch)
            {
                lo = math.min(lo, new int2(c.X, c.Y));
                hi = math.max(hi, new int2(c.X, c.Y));
            }
            if (FootScratch.Count == 0)
            {
                return false;
            }
            return hi.x >= st.MinX && lo.x <= st.MaxX && hi.y >= st.MinY && lo.y <= st.MaxY;
        }

        /// <summary>
        /// 对账（剧场开着时每 siege.sync_seconds、展开时立即）：记录 ↔ 建筑（补记录、清掉不再合格的）、内核单位、耐久双向对账、搬迁换位置；
        /// 炮塔 / 防御结构单位标类别与占地。O(建筑数)。
        /// </summary>
        public static void SyncStructures(CampaignState state, CombatSite site)
        {
            SiegeState st = StateOf(state);
            if (st == null || !st.TheaterActive || site == null || site.IsDisposed)
            {
                return;
            }
            BuildingRecord[] buildings = state.BuildingRecords ?? Array.Empty<BuildingRecord>();
            IdScratch.Clear();
            RecScratch.Clear();
            bool changed = false;
            foreach (SiegeStructureRecord r in st.Structures)
            {
                if (r == null)
                {
                    changed = true;
                    continue;
                }
                BuildingRecord b = HomeGridService.FindBuilding(state, r.BuildingId);
                if (!Eligible(b) || !InTheater(st, b, out _, out _))
                {
                    // 不再合格（拆了 / 变虚影 / 换成炮塔 / 移出剧场）：耐久写回后移出内核。
                    if (b != null && IsBuilt(b) && site.TryGetSiegeStructHealth(r.Serial, out float hp, out _, out bool alive) && alive)
                    {
                        b.Health = hp;
                    }
                    site.RemoveSiegeStructure(r.Serial);
                    Rt.Remove(r.Serial);
                    changed = true;
                    continue;
                }
                IdScratch.Add(r.BuildingId);
                RecScratch.Add(r);
            }
            foreach (BuildingRecord b in buildings)
            {
                if (!Eligible(b) || IdScratch.Contains(b.BuildingId) || !InTheater(st, b, out _, out _))
                {
                    continue;
                }
                RecScratch.Add(new SiegeStructureRecord { Serial = st.NextSerial++, BuildingId = b.BuildingId });
                IdScratch.Add(b.BuildingId);
                changed = true;
            }
            if (changed)
            {
                st.Structures = RecScratch.ToArray();
            }
            // 内核里有、记录里没有的建筑结构单位（读档对账）：拿掉。
            if (site.SiegeStructUnitCount != st.Structures.Length)
            {
                var serials = new HashSet<int>();
                foreach (SiegeStructureRecord r in st.Structures)
                {
                    serials.Add(r.Serial);
                }
                foreach (int s in site.SiegeStructSerials())
                {
                    if (!serials.Contains(s))
                    {
                        site.RemoveSiegeStructure(s);
                    }
                }
            }
            foreach (SiegeStructureRecord r in st.Structures)
            {
                SyncOne(state, site, st, r, HomeGridService.FindBuilding(state, r.BuildingId));
            }
            MarkDefenseTargets(state, site, st);
        }

        /// <summary>能当攻城目标的建筑：已建成、不是搬迁 / 升级目标虚影、不是炮塔 / 防御建筑（它们有自己的内核单位）。</summary>
        private static bool Eligible(BuildingRecord b) =>
            b != null && b.RegionId == HomeValleyLayout.RegionId && IsBuilt(b) && !TurretService.IsTurret(b) && !DefenseService.IsDefense(b);

        private static void SyncOne(CampaignState state, CombatSite site, SiegeState st, SiegeStructureRecord r, BuildingRecord b)
        {
            if (b == null || !InTheater(st, b, out int2 lo, out int2 hi))
            {
                return;
            }
            StructRt rt = RuntimeOf(r.Serial);
            float maxHp = BuildingOps.MaxDurability(b.BuildingTypeId);
            byte cat = SiegeCatalog.CategoryOf(b.BuildingTypeId);
            bool core = b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore;
            if (!site.TryGetSiegeStructUnit(r.Serial, out int unit))
            {
                GridContent.TryGetBuilding(b.BuildingTypeId, out GameConfig.fg.BuildingGrid g);
                float radius = g != null ? Mathf.Min(g.FootprintW, g.FootprintH) * 0.45f : 0.45f;
                float hp = Mathf.Clamp(BuildingOps.Durability(b), core ? CombatSiegeConst.HealthFloor : 1f, maxHp);
                unit = site.SpawnSiegeStructure(r.Serial, b.Position, radius, hp, maxHp, cat, lo, hi, core);
                site.SetUnitLabel(unit, "reaction.log.structure_named", BuildingOps.NameOf(b));
                rt.LastPushedHp = BuildingOps.Durability(b);
                return;
            }
            site.SetSiegeStructPlace(r.Serial, b.Position, cat, lo, hi);
            if (!site.TryGetSiegeStructHealth(r.Serial, out float cur, out float kernelMax, out bool alive) || !alive)
            {
                return;
            }
            float recHp = BuildingOps.Durability(b);
            if (Mathf.Abs(kernelMax - maxHp) > 0.01f)
            {
                float hp = Mathf.Clamp(cur / Mathf.Max(1f, kernelMax) * maxHp, core ? CombatSiegeConst.HealthFloor : 1f, maxHp);
                site.SetSiegeStructHealth(r.Serial, hp, maxHp);
                rt.LastPushedHp = hp;
                b.Health = hp;
                BuildingOps.Touch();
            }
            else if (!float.IsNaN(rt.LastPushedHp) && Mathf.Abs(recHp - rt.LastPushedHp) > 0.01f)
            {
                // 记录被别处改过（维修 / 天气 / 维修无人机修到记录）→ 推给内核。
                float hp = Mathf.Clamp(recHp, core ? CombatSiegeConst.HealthFloor : 1f, maxHp);
                site.SetSiegeStructHealth(r.Serial, hp, maxHp);
                rt.LastPushedHp = hp;
                if (Mathf.Abs(recHp - hp) > 0.01f)
                {
                    b.Health = hp;
                }
            }
            else if (Mathf.Abs(cur - recHp) > 0.01f)
            {
                // 内核的受伤写回建筑；耐久下降 = 这段时间挨过打（维修无人机突袭中先修它，FG6-DEF-03）。
                if (cur < recHp - 0.01f)
                {
                    b.LastHitTick = GameClock.Ticks;
                    if (recHp >= maxHp - 0.01f)
                    {
                        GuidanceHooks.Raise(GuidanceHooks.BuildingFirstDamaged);
                    }
                }
                b.Health = core && cur <= CombatSiegeConst.HealthFloor + 1e-6f ? BuildingOps.CoreFloorHealth : cur;
                rt.LastPushedHp = b.Health;
                BuildingVisualFeed.Mark(b);
            }
        }

        /// <summary>炮塔 / 防御结构单位标上攻城类别与占地（剧场开着时）；剧场关时清掉。</summary>
        private static void MarkDefenseTargets(CampaignState state, CombatSite site, SiegeState st)
        {
            foreach (TurretRecord t in TurretService.All(state))
            {
                BuildingRecord b = t != null ? HomeGridService.FindBuilding(state, t.BuildingId) : null;
                if (b == null || !site.TryGetTurretUnit(t.Serial, out int unit))
                {
                    continue;
                }
                bool inside = InTheater(st, b, out int2 lo, out int2 hi);
                site.SetUnitSiegeTarget(unit, inside ? SiegeCatalog.CategoryOf(b.BuildingTypeId) : (byte)0, lo, hi);
            }
            foreach (DefenseRecord d in DefenseService.All(state))
            {
                BuildingRecord b = d != null ? HomeGridService.FindBuilding(state, d.BuildingId) : null;
                if (b == null || !site.TryGetDefenseUnit(d.Serial, out int unit))
                {
                    continue;
                }
                bool inside = InTheater(st, b, out int2 lo, out int2 hi);
                site.SetUnitSiegeTarget(unit, inside ? SiegeCatalog.CategoryOf(b.BuildingTypeId) : (byte)0, lo, hi);
            }
        }

        private static void ClearDefenseTargets(CampaignState state, CombatSite site)
        {
            foreach (TurretRecord t in TurretService.All(state))
            {
                if (t != null && site.TryGetTurretUnit(t.Serial, out int unit))
                {
                    site.SetUnitSiegeTarget(unit, 0, int2.zero, int2.zero);
                }
            }
            foreach (DefenseRecord d in DefenseService.All(state))
            {
                if (d != null && site.TryGetDefenseUnit(d.Serial, out int unit))
                {
                    site.SetUnitSiegeTarget(unit, 0, int2.zero, int2.zero);
                }
            }
        }

        /// <summary>建筑结构单位阵亡 = 建筑被摧毁（FGR-ECO-013 留虚影、电网重算、自动重建规则排队；废墟不再挡路）。</summary>
        private static void OnStructKilled(CombatSite site, int serial, CombatEvent e)
        {
            CampaignState state = CampaignSession.Current;
            SiegeStructureRecord r = FindBySerial(state, serial);
            site.RemoveSiegeStructure(serial);
            Rt.Remove(serial);
            if (state == null || r == null)
            {
                return;
            }
            BuildingRecord b = HomeGridService.FindBuilding(state, r.BuildingId);
            SiegeState st = StateOf(state);
            var keep = new List<SiegeStructureRecord>(st.Structures.Length);
            foreach (SiegeStructureRecord x in st.Structures)
            {
                if (x != null && x.Serial != serial)
                {
                    keep.Add(x);
                }
            }
            st.Structures = keep.ToArray();
            if (b == null || !IsBuilt(b))
            {
                return;
            }
            b.Health = 0f;
            b.LastHitTick = GameClock.Ticks;
            if (HomeValleyPowerGrid.ApplyBuildingDestroyed(state, b.BuildingId))
            {
                st.TotalDestroyedBuildings++;
            }
        }

        // ─────────────────────────────── 破墙通知（FG06 第 5 章“玩家看得到敌人在拆哪段墙”）───────────────────────────────

        private static readonly List<string> BreachIds = new List<string>(8);

        /// <summary>内核里正在被破墙的结构单位 → 建筑；还没报过的发一条警告（同一座只报一次，冷却内只报第一座；点击定位）。</summary>
        private static void NotifyBreaches(CampaignState state, CombatSite site)
        {
            SiegeState st = StateOf(state);
            BreachIds.Clear();
            int n = site.SiegeBreachCount;
            for (int i = 0; i < n; i++)
            {
                string id = BuildingIdOfUnit(state, site, site.SiegeBreachAt(i));
                if (!string.IsNullOrEmpty(id) && !BreachIds.Contains(id))
                {
                    BreachIds.Add(id);
                }
            }
            if (BreachIds.Count == 0)
            {
                return;
            }
            long cooldown = GameClock.TicksFor(SiegeCatalog.BreachNotifySeconds);
            foreach (string id in BreachIds)
            {
                if (Array.IndexOf(st.NotifiedBreaches, id) >= 0)
                {
                    continue;
                }
                if (st.LastBreachNotifyTick >= 0 && GameClock.Ticks - st.LastBreachNotifyTick < cooldown)
                {
                    break;
                }
                BuildingRecord b = HomeGridService.FindBuilding(state, id);
                if (b == null)
                {
                    continue;
                }
                var list = new List<string>(st.NotifiedBreaches) { id };
                st.NotifiedBreaches = list.ToArray();
                st.LastBreachNotifyTick = GameClock.Ticks;
                Hook(GuidanceHooks.SiegeFirstBreach);
                NotificationCenter.Post("raid_breach", GameText.Format("siege.notify.breach", BuildingOps.NameOf(b)), new Vector3(b.Position.x, 0f, b.Position.y));
                break;
            }
        }

        /// <summary>内核结构单位 ID → 建筑 ID（攻城建筑 / 防御建筑 / 炮塔）。</summary>
        public static string BuildingIdOfUnit(CampaignState state, CombatSite site, int unitId)
        {
            if (unitId <= 0 || site == null)
            {
                return null;
            }
            int s = site.SiegeStructSerialOf(unitId);
            if (s > 0)
            {
                return FindBySerial(state, s)?.BuildingId;
            }
            s = site.DefenseSerialOf(unitId);
            if (s > 0)
            {
                return DefenseService.FindBySerial(state, s)?.BuildingId;
            }
            s = site.TurretSerialOf(unitId);
            if (s > 0)
            {
                return TurretService.FindBySerial(state, s)?.BuildingId;
            }
            return null;
        }
    }
}
