using System;
using System.Collections.Generic;
using System.Linq;
using BinGames.Sim.Combat;
using BinGames.Sim.Logistics;
using BinGames.Sim.Nav;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using Unity.Mathematics;
using UnityEngine;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG6-DEF-05 自检第三部分（复审第 1 轮补的用例，全部起真实系统）：
    /// C5 攻城型打真实炮塔服务的炮塔（类别 / 占地 / 先打它、核心不挨打）、C6 反向对照（突击型路过同一座炮塔不站定拆）、C7 突击型路过路边仓库不站定拆、先打核心；
    /// N6 用规划 / 运料 / 施工中的普通建筑虚影围死核心，敌人照样打到核心（剧场里没有“挡路又拆不掉”的建筑格）；
    /// E4 敌人进墙后缺口被补上、再下撤退令 → 沿撤退破墙场拆出去离场收拢；E5 被地形困住、拆不出去 → 撤退兜底按离场收尾；
    /// L6 撤退途中被追击（拦截后仍是撤退中、继续回据点）；K6 管线件拆了会把两种流体接在一起 → 摧毁不了（停在 1 耐久）；M4 围墙里夹着普通建筑时放置预览的破墙目标 = 内核真实选择。
    /// </summary>
    public static partial class FgSiegeSelfCheck
    {
        private static void RunReview()
        {
            Step(CheckTurretTargets);
            Step(CheckAssaultIgnoresOther);
            Step(CheckGhostRing);
            Step(CheckRetreatBreakout);
            Step(CheckRetreatFallback);
            Step(CheckRetreatIntercept);
            Step(CheckPipeConflictDestroy);
            Step(CheckPreviewOrdinaryWall);
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        private static List<int> Raiders(TransitGroupRecord g)
        {
            var ids = new List<int>();
            Site?.SiegeRaiderIds(Key(g), ids);
            return ids;
        }

        /// <summary>这支队伍里有没有单位正站定打（职能目标 / 破墙目标，Hold = 1）满足 <paramref name="isTarget"/> 的单位。</summary>
        private static bool HoldsOn(TransitGroupRecord g, Func<int, bool> isTarget)
        {
            foreach (int id in Raiders(g))
            {
                if (Site.Kernel.TryGetUnit(id, out CombatUnitView v) && v.Siege.Hold != 0 && v.CommandTarget != 0 && isTarget(v.CommandTarget))
                {
                    return true;
                }
            }
            return false;
        }

        private static float NearestRaider(TransitGroupRecord g, Vector2 p)
        {
            float best = float.MaxValue;
            foreach (int id in Raiders(g))
            {
                if (Site.TryGetSiegeRaider(id, out Vector2 q, out _, out _, out _, out _))
                {
                    best = Mathf.Min(best, Vector2.Distance(p, q));
                }
            }
            return best;
        }

        private static int2 CellOfPos(Vector2 p)
        {
            GridCell c = NavService.CellOf(p.x, p.y);
            return new int2(c.X, c.Y);
        }

        private static int StructUnitOf(CampaignState s, BuildingRecord b)
        {
            SiegeStructureRecord r = b != null ? SiegeService.FindRecord(s, b.BuildingId) : null;
            return r != null && Site != null && Site.TryGetSiegeStructUnit(r.Serial, out int u) ? u : 0;
        }

        // ── C 按职能选目标（复审补）─────────────────────────────────────────

        private static void CheckTurretTargets()
        {
            // C5 FGR-DEF-030“攻城型优先攻击炮塔”：真实炮塔服务登记的轻型炮塔（没装蓝图 → 不还手，结果确定）放在来路上。
            // 剧场开着时炮塔单位标上“防御”类别与占地（SiegeService.MarkDefenseTargets → TryGetTurretUnit → SetUnitSiegeTarget），攻城型站定先打它，此前核心不挨打。
            CampaignState s = NewWorld(7040);
            BuildingRecord core = CoreOf(s);
            Vector2 at = OutsidePoint(s, 34f);
            BuildingRecord turret = PlaceNear(s, TurretCatalog.LightTypeId, at, CoreCenter(s), 0.35f, 0.65f);
            if (turret == null)
            {
                Fail("C5 测试准备：来路上放不下轻型炮塔");
                return;
            }
            Seconds(SiegeCatalog.SyncSeconds + 0.1f);
            TurretRecord tr = TurretService.Find(s, turret.BuildingId);
            int tu = tr != null && Site.TryGetTurretUnit(tr.Serial, out int u0) ? u0 : 0;
            float coreBefore = Durability(s, core);
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.armorbot" }, new[] { 4 });
            var foot = new List<GridCell>();
            HomeGridService.FootprintOf(turret, foot);
            var lo = new int2(foot.Min(c => c.X), foot.Min(c => c.Y));
            var hi = new int2(foot.Max(c => c.X), foot.Max(c => c.Y));
            bool tagged = tu > 0 && Site.Kernel.TryGetSiegeUnit(tu, out CombatSiegeUnit tsu) && tsu.Cat == CombatSiegeConst.CatDefense
                          && math.all(math.min(tsu.FootMin, tsu.FootMax) == lo) && math.all(math.max(tsu.FootMin, tsu.FootMax) == hi);
            float maxHp = tu > 0 && Site.Kernel.TryGetUnit(tu, out CombatUnitView tv0) ? tv0.MaxHealth : 0f;
            bool held = false;
            bool hit = tu > 0 && StepUntil(() =>
            {
                held |= HoldsOn(g, id => id == tu);
                bool alive = Site.Kernel.TryGetUnit(tu, out CombatUnitView tv) && tv.Alive;
                return !alive || tv.Health < maxHp - 20f || turret.ConstructionState == BuildingConstructionState.Damaged;
            }, 120);
            float coreAt = Durability(s, core);
            Expect(tagged && hit && held && coreAt >= coreBefore - 0.01f,
                $"C5 FGR-DEF-030 攻城型优先打炮塔：真实炮塔服务的轻型炮塔单位在剧场里标上“防御”类别与占地 [{lo.x},{lo.y}]–[{hi.x},{hi.y}]（{tagged}）；攻城型站定打它（{held}）、炮塔掉耐久 / 被毁（{hit}），" +
                $"此前核心没挨打（{coreBefore:F0} → {coreAt:F0}）" + (tagged && hit && held ? string.Empty : Diag(s, g, turret)));

            // C6 反向对照：同一座炮塔、同一条来路，突击型（目标位里没有“防御”）路过时不站定拆它，只边走边自卫，照样直扑核心。
            s = NewWorld(7040);
            core = CoreOf(s);
            at = OutsidePoint(s, 34f);
            turret = PlaceNear(s, TurretCatalog.LightTypeId, at, CoreCenter(s), 0.35f, 0.65f);
            Seconds(SiegeCatalog.SyncSeconds + 0.1f);
            tr = turret != null ? TurretService.Find(s, turret.BuildingId) : null;
            int tu2 = tr != null && Site.TryGetTurretUnit(tr.Serial, out int u2) ? u2 : 0;
            TransitGroupRecord g2 = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 4 });
            float c0 = Durability(s, core);
            bool held2 = false;
            float near = float.MaxValue;
            Vector2 tpos = turret != null ? turret.Position : Vector2.zero;
            bool coreHit = tu2 > 0 && StepUntil(() =>
            {
                held2 |= HoldsOn(g2, id => id == tu2);
                near = Mathf.Min(near, NearestRaider(g2, tpos));
                return Durability(s, core) < c0 - 20f;
            }, 90);
            Expect(tu2 > 0 && coreHit && !held2 && near < 16f,
                $"C6 反向对照：突击型路过同一座炮塔（最近 {near:F1} 米，在射程内）不站定拆它（站定 {held2}），照样打到核心（{coreHit}，{c0:F0} → {Durability(s, core):F0}）");
        }

        private static void CheckAssaultIgnoresOther()
        {
            // C7 复审修复（FGR-DEF-030“突击型直扑归还核心”；表注释“偏好越大越要顺路才去”）：突击型路过路边的仓库（“其它建筑”，偏好 120 米）——
            // 仓库在射程内也不站定拆，先打核心；仓库毫发无损（偏好 + 走过去的下界超过当前格的流场值 = 不是流场要带它去的地方）。
            CampaignState s = NewWorld(7041);
            BuildingRecord core = CoreOf(s);
            Vector2 at = OutsidePoint(s, 40f);
            Vector2 c = CoreCenter(s);
            Vector2 dir = (c - at).normalized;
            var side = new Vector2(-dir.y, dir.x);
            string type = HomeValleyLayout.BuildingTypeWarehouse;
            var houses = new List<BuildingRecord>();
            foreach (int sgn in new[] { 1, -1 })
            {
                BuildingRecord h = null;
                for (float f = 0.3f; f <= 0.6f + 1e-4f && h == null; f += 0.05f)
                {
                    for (int o = 5; o <= 9 && h == null; o++)
                    {
                        Vector2 p = Vector2.Lerp(at, c, f) + side * (o * sgn);
                        var cell = new GridCell(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
                        if (CanPlace(s, type, cell))
                        {
                            h = Register(s, type, cell);
                        }
                    }
                }
                if (h != null)
                {
                    houses.Add(h);
                }
            }
            NavService.SyncGridChanges();
            if (houses.Count == 0)
            {
                Fail("C7 测试准备：来路两侧放不下仓库");
                return;
            }
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 6 });
            var houseUnits = new HashSet<int>(houses.Select(h => StructUnitOf(s, h)).Where(u => u > 0));
            bool other = houses.All(h => SiegeCatalog.CategoryOf(h.BuildingTypeId) == CombatSiegeConst.CatOther);
            float c0 = Durability(s, core);
            bool held = false;
            float near = float.MaxValue;
            bool coreHit = StepUntil(() =>
            {
                held |= HoldsOn(g, houseUnits.Contains);
                foreach (BuildingRecord h in houses)
                {
                    near = Mathf.Min(near, NearestRaider(g, h.Position));
                }
                return Durability(s, core) < c0 - 20f;
            }, 90);
            bool intact = houses.All(h => Durability(s, h) >= BuildingOps.MaxDurability(h.BuildingTypeId) - 0.01f);
            Expect(other && houseUnits.Count == houses.Count && coreHit && !held && intact && near < 12f,
                $"C7 复审修复 FGR-DEF-030 突击直扑核心：路边 {houses.Count} 座仓库（其它建筑、偏好 120 米，进了内核 {houseUnits.Count} 座；突击型最近走到离仓库中心 {near:F1} 米，射程 14 米）不站定拆（{held}）、" +
                $"毫发无损（{intact}），先打到核心（{coreHit}，{c0:F0} → {Durability(s, core):F0}）" + (coreHit && !held ? string.Empty : Diag(s, g, houses[0])));
        }

        // ── N 负向（复审补）──────────────────────────────────────────────────

        private static void CheckGhostRing()
        {
            // N6 复审修复（FGR-DEF-031“不允许靠完美迷宫让敌人永远走不到”；卡片负向“完全迷宫”）：用一整圈规划中 / 运料中 / 施工中的普通建筑虚影（电塔，不出料）把核心围死。
            // 旧规则下虚影挡敌方寻路、又不是内核里能拆的结构单位 → 开路 / 破墙两张场都到不了、敌人原地待命到撤退；现在虚影不挡敌方类别（己方机器照旧绕开施工现场），敌人照常打到核心。
            CampaignState s = NewWorld(7042);
            foreach (MachineRecord m in HomeMachines())
            {
                MachineRoster.TrySetRole(s, m.LogicId, MachineRole.Idle, out _); // 不让机器把虚影建起来（这里测“虚影围墙”本身）
            }
            List<BuildingRecord> ring = Ring(s, 4, B3, out GridCell rmin, out GridCell rmax, out int gaps);
            List<GridCell> cells = ring.Select(CellOfB).ToList();
            s.BuildingRecords = s.BuildingRecords.Where(x => !ring.Contains(x)).ToArray();
            HomeGridService.MapFor(s);
            DefenseService.Sync(s);
            BuildingConstructionState[] states = { BuildingConstructionState.Planned, BuildingConstructionState.MaterialReserved, BuildingConstructionState.Building };
            var ghosts = new List<BuildingRecord>(cells.Count);
            for (int i = 0; i < cells.Count; i++)
            {
                BuildingRecord gb = Register(s, "power_pole", cells[i], sync: false, state: states[i % 3]);
                if (gb.ConstructionState == BuildingConstructionState.Building)
                {
                    gb.ConstructionRequired = 4;
                    gb.ConstructionDelivered = 2;
                }
                ghosts.Add(gb);
            }
            HomeValleyPowerGrid.Recompute(s);
            DefenseService.Sync(s);
            TurretService.Sync(s);
            NavService.SyncGridChanges();
            bool hostilePass = ghosts.All(b => NavService.PassableNow(b.GridX, b.GridY, NavConst.ClassHostile));
            bool playerBlocked = ghosts.All(b => !NavService.PassableNow(b.GridX, b.GridY, NavConst.ClassPlayer));
            Vector2 at = OutsidePoint(s, Math.Max(rmax.X - rmin.X, rmax.Y - rmin.Y) * 0.5f + 12f);
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 4 });
            Site.MaintainSiegeNow();
            var gc = new int2((int)at.x, (int)at.y);
            bool open = Site.SiegeDistAt(0, gc) < CombatSiegeConst.Inf;
            int unbreakable = UnbreakableBuildingCells(s, out int blocked, out string sample);
            BuildingRecord core = CoreOf(s);
            float c0 = Durability(s, core);
            bool hit = StepUntil(() => Durability(s, core) < c0 - 10f, 120);
            bool stillGhosts = ghosts.All(b => b.ConstructionState == BuildingConstructionState.Planned || b.ConstructionState == BuildingConstructionState.MaterialReserved
                                               || b.ConstructionState == BuildingConstructionState.Building);
            Expect(gaps == 0 && ghosts.Count > 0 && hostilePass && playerBlocked && open && unbreakable == 0 && hit && stillGhosts,
                $"N6 复审修复 负向“完全迷宫”（虚影变体）：{ghosts.Count} 座规划 / 运料 / 施工中的电塔虚影围成一圈（缺口 {gaps}），虚影不挡敌方类别（{hostilePass}）、己方机器照旧绕开（{playerBlocked}）；" +
                $"开路流场从集结点到得了核心（{open}），剧场里挡敌方寻路的建筑格 {blocked} 格全部是能拆的结构单位（拆不掉 {unbreakable} 格{sample}）；敌人打到核心（{hit}），虚影始终没建成（{stillGhosts}）");
        }

        // ── E 撤退（复审补）──────────────────────────────────────────────────

        private static void CheckRetreatBreakout()
        {
            // E4 复审修复（FGR-DEF-032 / B11）：敌人从缺口进了家园，玩家把缺口补上（T3，重新围死），之后到了撤退时间——
            // 撤退开路场到不了集结点 → 沿撤退破墙场拆开挡路的那段墙、走回集结点离场 → 并回行进队伍原路返回；不需要撤退兜底。
            CampaignState s = NewWorld(7043);
            List<BuildingRecord> ring = Ring(s, 4, B3, out GridCell rmin, out GridCell rmax, out int gaps);
            Vector2 at = OutsidePoint(s, Math.Max(rmax.X - rmin.X, rmax.Y - rmin.Y) * 0.5f + 12f);
            BuildingRecord near = NearestWall(ring, rmin, rmax, at);
            GridCell gap = CellOfB(near);
            s.BuildingRecords = s.BuildingRecords.Where(x => x != near).ToArray();
            HomeGridService.MapFor(s);
            DefenseService.Sync(s);
            NavService.SyncGridChanges();
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 6 });
            bool Inside(Vector2 p) => p.x > rmin.X + 0.5f && p.x < rmax.X - 0.5f && p.y > rmin.Y + 0.5f && p.y < rmax.Y - 0.5f;
            int InsideCount() => Raiders(g).Count(id => Site.TryGetSiegeRaider(id, out Vector2 p, out _, out _, out _, out _) && Inside(p));
            bool entered = StepUntil(() => InsideCount() >= 3, 120);
            // 玩家把缺口补上：进来的敌人被重新围在墙里。
            BuildingRecord refill = CanPlace(s, B3, gap) ? Register(s, B3, gap) : null;
            NavService.SyncGridChanges();
            Seconds(SiegeCatalog.SyncSeconds + 0.2f);
            int trapped = InsideCount();
            // 下撤退令（测试捷径：直接走对账里同一个入口 OrderRetreat；时间上限 / 损失的正式触发由 E1 / E2 覆盖）。
            int timeouts0 = SiegeService.RetreatTimeouts;
            var liveWalls = ring.Where(w => w != near && w.ConstructionState == BuildingConstructionState.Operational).ToList();
            if (refill != null)
            {
                liveWalls.Add(refill);
            }
            SiegeService.OrderRetreat(s, Site, g, SiegeService.ReasonTime, Alive(g), 0);
            bool ordered = g.SiegeRetreat && g.SiegeRetreatReason == SiegeService.ReasonTime && g.SiegeRetreatTick == GameClock.Ticks;
            WorldSimulation.StepMany(2);
            Site.MaintainSiegeNow();
            int retreatOpen = CombatSiegeConst.RoleRetreat * 2;
            bool sealedIn = Raiders(g).Any(id => Site.TryGetSiegeRaider(id, out Vector2 p, out _, out _, out _, out _) && Inside(p)
                                                 && Site.SiegeDistAt(retreatOpen, CellOfPos(p)) >= CombatSiegeConst.Inf);
            bool breaching = false;
            bool regrouped = StepUntil(() =>
            {
                foreach (int id in Raiders(g))
                {
                    if (Site.Kernel.TryGetUnit(id, out CombatUnitView v) && v.Siege.Mode == (byte)CombatSiegeMode.Retreat && v.Siege.Breach != 0)
                    {
                        breaching = true;
                    }
                }
                return g.State == TransitGroupState.Retreating;
            }, (int)SiegeCatalog.RetreatMaxSeconds - 10);
            bool wallDown = liveWalls.Any(w => w.ConstructionState == BuildingConstructionState.Damaged); // 下撤退令之后才被拆的墙（之前墙外的攻击者拆的不算）
            Expect(gaps == 0 && entered && refill != null && trapped > 0 && ordered && sealedIn && breaching && wallDown && regrouped
                   && SiegeService.RetreatTimeouts == timeouts0 && g.ExitedCount >= trapped,
                $"E4 复审修复 撤退中被重新围死：{trapped} 台敌人进墙后缺口被补上（{refill != null}），到时间撤退（{ordered}）时撤退开路场到不了集结点（{sealedIn}）→ " +
                $"撤退中的单位沿撤退破墙场拆墙（破墙目标 {breaching}，墙被拆 {wallDown}）、走回集结点离场 {g.ExitedCount} 台，队伍并回行进队伍原路返回（{g.State}）；没有用到撤退兜底（{SiegeService.RetreatTimeouts - timeouts0} 次）"
                + (regrouped ? string.Empty : Diag(s, g, refill)));
        }

        private static void CheckRetreatFallback()
        {
            // E5 复审修复（B11 软锁保底）：敌人打到核心边上后被地形困住（测试捷径：一圈 2 格厚的悬崖，没有可拆的墙），再下撤退令——
            // 撤退开路场与撤退破墙场都到不了集结点，单位走不出去；满 siege.retreat_max_seconds 后撤退兜底按离场收尾，队伍并回行进队伍原路返回、剧场关闭（不会永远开着）。
            CampaignState s = NewWorld(7044);
            BuildingRecord core = CoreOf(s);
            Vector2 cc = CoreCenter(s);
            var center = new GridCell(Mathf.RoundToInt(cc.x), Mathf.RoundToInt(cc.y));
            Vector2 at = OutsidePoint(s, 60f);
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 4 });
            float c0 = Durability(s, core);
            int Cheb(Vector2 p) => Math.Max(Math.Abs(Mathf.RoundToInt(p.x) - center.X), Math.Abs(Mathf.RoundToInt(p.y) - center.Y));
            int MaxRaider() => Raiders(g).Select(id => Site.TryGetSiegeRaider(id, out Vector2 p, out _, out _, out _, out _) ? Cheb(p) : 0).DefaultIfEmpty(0).Max();
            bool close = StepUntil(() => Durability(s, core) < c0 - 10f && MaxRaider() <= 22, 120);
            int gatherCheb = Math.Max(Math.Abs(g.GatherX - center.X), Math.Abs(g.GatherY - center.Y));
            HomeGridMap map = HomeGridService.MapFor(s);
            int r = -1;
            for (int rr = MaxRaider() + 2; rr + 1 < gatherCheb - 3 && r < 0; rr++)
            {
                bool free = true;
                for (int t = 0; t < 2 && free; t++)
                {
                    int q = rr + t;
                    for (int dy = -q; dy <= q && free; dy++)
                    {
                        for (int dx = -q; dx <= q && free; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == q && !string.IsNullOrEmpty(map.OccupantAt(new GridCell(center.X + dx, center.Y + dy))))
                            {
                                free = false;
                            }
                        }
                    }
                }
                if (free)
                {
                    r = rr;
                }
            }
            if (!close || r < 0)
            {
                Fail($"E5 测试准备：敌人没走到核心边上（{close}）或找不到一圈没有建筑的悬崖半径（敌人最远 {MaxRaider()} 格、集结点 {gatherCheb} 格）");
                return;
            }
            HomeGridService.RevealArea(s, cc, r + 4f);
            int cliffs = CliffRing(s, center, r);
            WorldSimulation.StepMany(2);
            int timeouts0 = SiegeService.RetreatTimeouts;
            SiegeService.OrderRetreat(s, Site, g, SiegeService.ReasonTime, Alive(g), 0); // 测试捷径：对账里同一个入口（正式触发由 E1 / E2 覆盖）
            bool ordered = g.SiegeRetreat && g.SiegeRetreatTick == GameClock.Ticks;
            Seconds(5f);
            int alive = Alive(g);
            Site.MaintainSiegeNow();
            int f6 = CombatSiegeConst.RoleRetreat * 2;
            bool stuck = alive > 0 && g.ExitedCount == 0 && Raiders(g).All(id => Site.TryGetSiegeRaider(id, out Vector2 p, out _, out _, out _, out _)
                                                                               && Site.SiegeDistAt(f6, CellOfPos(p)) >= CombatSiegeConst.Inf
                                                                               && Site.SiegeDistAt(f6 + 1, CellOfPos(p)) >= CombatSiegeConst.Inf);
            GameClock.SkipForTests(s, GameClock.TicksFor(SiegeCatalog.RetreatMaxSeconds)); // 测试捷径：时钟拨过撤退兜底时长（不跑中间的模拟步）
            Seconds(SiegeCatalog.SyncSeconds + 0.2f);
            bool fallback = SiegeService.RetreatTimeouts == timeouts0 + 1 && Alive(g) == 0 && g.ExitedCount == alive;
            bool regrouped = g.State == TransitGroupState.Retreating && g.UnitCount == alive;
            Seconds(SiegeCatalog.SyncSeconds + 0.2f);
            bool theaterOff = !SiegeService.StateOf(s).TheaterActive;
            Expect(cliffs > 0 && ordered && stuck && fallback && regrouped && theaterOff,
                $"E5 复审修复 撤退兜底（B11）：{alive} 台敌人在核心边上被 {cliffs} 格悬崖困住（半径 {r}），撤退开路 / 破墙场都到不了集结点（{stuck}）；" +
                $"下撤退令满 {SiegeCatalog.RetreatMaxSeconds:F0} 游戏秒后按离场收尾（兜底 {SiegeService.RetreatTimeouts - timeouts0} 次，离场 {g.ExitedCount} 台），队伍并回行进队伍原路返回（{g.State}），剧场关闭（{theaterOff}）");
        }

        // ── L 撤退途中被追击（复审补）──────────────────────────────────────────

        private static void CheckRetreatIntercept()
        {
            // L6 FGR-DEF-032“沿原路返回出发的据点，途中可以被追击”：撤退中的行进队伍经过己方机器 → 就地展开交战；机器离开后幸存者收拢，仍是“撤退中”，继续往据点走。
            float postDist = SiegeCatalog.InterceptMinTargetCells + SiegeCatalog.InterceptRadius + 14f;
            CampaignState s = NewWorld(7046);
            Vector2 post = OutsidePoint(s, postDist);
            Vector2 dir = (post - CoreCenter(s)).normalized;
            HomeGridService.RevealArea(s, post + dir * 30f, 50f);
            int m = SpawnMachine(s, post);
            GridCell oc = SiegeService.NearestPassable(NavService.CellOf(post.x + dir.x * 60f, post.y + dir.y * 60f), 8);
            GridCell sc = SiegeService.NearestPassable(NavService.CellOf(post.x - dir.x * 28f, post.y - dir.y * 28f), 8);
            TransitGroupRecord g = March(s, new Vector2(oc.X, oc.Y), "foundry", "foundry.strider", 4);
            // 测试捷径：这支队伍已经沿来路走到机器内侧 28 格处（拦截半径外），在这里开始撤退（撤退的正式触发由 E1 / E2 / E4 覆盖）。
            g.PosX = sc.X;
            g.PosY = sc.Y;
            g.RouteX = new[] { sc.X };
            g.RouteY = new[] { sc.Y };
            g.RouteIndex = 1;
            g.RouteState = WorldTransitSystem.RouteFollowing;
            WorldTransitSystem.BeginRetreat(s, g);
            bool retreating = g.State == TransitGroupState.Retreating;
            bool intercepted = m > 0 && StepUntil(() => g.Intercepted, 120);
            bool stillRetreat = g.State == TransitGroupState.Retreating;
            int spawned = Alive(g);
            int mu = Site.FindNearestMachine(new Vector2((float)g.PosX, (float)g.PosY), SiegeCatalog.InterceptRadius * 2f);
            while (mu > 0)
            {
                Site.Kernel.Kill(mu, 0); // 测试捷径：拦截的机器离开（击毁；击毁本身由突袭者打出来，见 L1）
                WorldSimulation.StepMany(1);
                mu = Site.FindNearestMachine(new Vector2((float)g.PosX, (float)g.PosY), SiegeCatalog.InterceptRadius * 2f);
            }
            int survivors = Alive(g);
            bool regrouped = StepUntil(() => !g.Intercepted, (int)SiegeCatalog.RegroupSeconds + 6) && Alive(g) == 0 && g.State == TransitGroupState.Retreating
                             && g.UnitCount == Math.Max(1, survivors);
            var origin = new Vector2(oc.X, oc.Y);
            float d0 = Vector2.Distance(new Vector2((float)g.PosX, (float)g.PosY), origin);
            Seconds(3f);
            TransitGroupRecord still = WorldTransitSystem.Find(s, g.GroupId);
            float d1 = still != null ? Vector2.Distance(new Vector2((float)still.PosX, (float)still.PosY), origin) : 0f;
            bool onward = still == null || d1 < d0 - 0.5f;
            Expect(retreating && intercepted && stillRetreat && spawned > 0 && regrouped && onward,
                $"L6 FGR-DEF-032 撤退途中被追击：撤退中的队伍经过己方机器 → 就地展开 {spawned} 台交战（{intercepted}），期间仍记为撤退中（{stillRetreat}）；机器离开后幸存者 {survivors} 台收拢（{regrouped}），" +
                $"继续往出发据点走（离据点 {d0:F0} → {d1:F0} 格，{onward}）");
        }

        // ── K 管线（复审补）──────────────────────────────────────────────────

        private static void CheckPipeConflictDestroy()
        {
            // K6 FGR-LOG-047 / DEBT-FG3LOG05-12 负向：摧毁一口地下管线口会让水与原油两口重新配对（把两种流体接在一起）→ 摧毁不了：耐久停在 1、件留在原地、不留虚影、流体没被改名。
            // 布局与 FgEnergySelfCheck U3 相同：水泵 → 管线 → A 朝东 ⇄ M 朝西；B 朝西被 M 挡住，地面一侧接原油泵。打 M。
            CampaignState s = NewWorld(7045);
            GridCell? o = F.FindArea(s, 12, 3, 8f, 22f);
            if (!o.HasValue || !PipeNetworkService.IsRunning)
            {
                Fail("K6 测试准备：找不到 12×3 的空地 / 管线未运行");
                return;
            }
            PipeKernel k = PipeNetworkService.Kernel;
            GridCell C(int dx) => F.At(o.Value, dx, 1);
            const PipePieceKind U = PipePieceKind.Underground;
            const int E = 1, W = 3;
            bool laid = k.Place(C(0).X, C(0).Y, PipePieceKind.Pump, 0, 0, 1) == PipeResult.Ok && k.Place(C(1).X, C(1).Y, PipePieceKind.Pipe, 0, 0, 0) == PipeResult.Ok
                        && k.Place(C(2).X, C(2).Y, U, 0, E, 0) == PipeResult.Ok && k.Place(C(5).X, C(5).Y, U, 0, W, 0) == PipeResult.Ok
                        && k.Place(C(10).X, C(10).Y, PipePieceKind.Pump, 0, 0, 2) == PipeResult.Ok && k.Place(C(9).X, C(9).Y, PipePieceKind.Pipe, 0, 0, 0) == PipeResult.Ok
                        && k.Place(C(8).X, C(8).Y, U, 0, W, 0) == PipeResult.Ok;
            int max = PipeNetworkService.MaxHp(U, 0);
            int destroyed0 = PipeNetworkService.DestroyedPieces;
            int resolved0 = k.FluidConflictsResolved;
            bool first = PipeNetworkService.TryDamage(s, C(5), max * 2, out PipeOpResult r1);
            int hpAfter = PipeNetworkService.HpOf(C(5));
            bool second = PipeNetworkService.TryDamage(s, C(5), max, out PipeOpResult r2);
            k.Step();
            bool kept = laid && !first && !second && k.HasCell(C(5).X, C(5).Y) && hpAfter == 1 && PipeNetworkService.HpOf(C(5)) == 1
                        && PipeNetworkService.DestroyedPieces == destroyed0 && GhostAt(s, C(5), true) == null
                        && r1.Code == PipeResult.FluidConflict && r2.Code == PipeResult.FluidConflict && k.FluidConflictsResolved == resolved0;
            Expect(kept,
                $"K6 负向“管线拆了会把两种流体接在一起”：地下管线口 M 被打空（{max * 2} 伤害）→ 摧毁不了（{r1.Code}），耐久停在 1（{hpAfter}）、件留在原地、不留虚影；再打一次照旧（{r2.Code}）；流体没被改名");
        }

        // ── M 放置预览（复审补）──────────────────────────────────────────────

        private static void CheckPreviewOrdinaryWall()
        {
            // M4 复审修复（与内核破墙场同一口径）：一圈 T3 里夹着一座建成的电塔（普通建筑，耐久 100，比 T3 薄得多），另一侧补上最后一个缺口 = 完全堵死。
            // 放置预览的破墙目标 = 这座电塔（普通建筑也是能拆的墙），真建好后同一方向到达的突袭，内核破墙流场找到的第一段也是它。
            CampaignState s = NewWorld(7047);
            Vector2 from = OutsidePoint(s, 95f, 4);
            TransitGroupRecord marching = March(s, from, "foundry", "foundry.strider", 4);
            List<BuildingRecord> ring = Ring(s, 4, B3, out GridCell rmin, out GridCell rmax, out int gaps);
            BuildingRecord near = NearestWall(ring, rmin, rmax, from);
            BuildingRecord pole = ReplaceWall(s, near, "power_pole");
            BuildingRecord far = ring.Where(w => w != near && s_live(w) && !((w.GridX == rmin.X || w.GridX == rmax.X) && (w.GridY == rmin.Y || w.GridY == rmax.Y)))
                .OrderByDescending(w => Vector2.SqrMagnitude(w.Position - from)).ThenBy(w => w.GridX).ThenBy(w => w.GridY).First();
            GridCell gap = CellOfB(far);
            s.BuildingRecords = s.BuildingRecords.Where(x => x != far).ToArray();
            HomeGridService.MapFor(s);
            DefenseService.Sync(s);
            NavService.SyncGridChanges();
            var closing = new RoutePreview();
            DefenseService.PreviewRoutes(s, new[] { gap }, closing, B3);
            string poleName = HomeGridService.DisplayName("power_pole");
            bool pv = closing.Sealed && closing.BreachBuildingId == pole.BuildingId && closing.Summary().Contains(poleName);
            s.Raids.InTransit = s.Raids.InTransit.Where(x => x != marching).ToArray();
            Register(s, B3, gap);
            NavService.SyncGridChanges();
            Vector2 at = OutsidePoint(s, Math.Max(rmax.X - rmin.X, rmax.Y - rmin.Y) * 0.5f + 12f, 4);
            Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 3 });
            Site.MaintainSiegeNow();
            int pick = Site.SiegeBreachAhead(1, new int2((int)at.x, (int)at.y), 400);
            int poleUnit = StructUnitOf(s, pole);
            string pickId = SiegeService.BuildingIdOfUnit(s, Site, pick);
            Expect(gaps == 0 && pv && poleUnit > 0 && pick == poleUnit,
                $"M4 复审修复 预览与内核同一口径：围墙里夹着一座建成的电塔（普通建筑），补上另一侧最后一个缺口 = 完全堵死，预览写“{closing.Summary()}”（破墙目标 = 电塔 {pv}）；" +
                $"真建好后内核破墙流场找到的第一段 = 同一座电塔（{pickId}，{pick == poleUnit}）");
        }
    }
}
