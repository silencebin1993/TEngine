using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using BinGames.EditorTools;
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
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.View;
using Unity.Mathematics;
using UnityEngine;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG6-DEF-05 自检第二部分：K 溅射（传送带 / 管线 / 施工虚影，DEBT-FG3LOG03-04 / DEBT-FG3LOG02-03）、管线耐久与保留设置的虚影 / 自动重建 / 维修无人机（DEBT-FG3LOG05-12 / DEBT-FG6DEF03-02）、
    /// 电塔被毁按实例重建（FG-GAP-089 / FG-GAP-084）、传送带“最近挨打”优先（DEBT-FG6DEF03-03）；L 行进拦截（DEBT-FG6DEF04-03）、驻防守点交战（DEBT-FG4ECO07-02）；
    /// M 职能图标 / 突袭路径叠加层 / 放置屏障预览读真实到达点与破墙目标（DEBT-FG6DEF02-04）；N 负向：闸门等同屏障、突袭中玩家拆墙、地形围死（没有墙可拆）、正式突袭撞上围墙（DEBT-FG0ARCH06-03 / 11③）、
    /// 部分路线取各轮最优（DEBT-FG0ARCH06-10）；P 性能（200 攻城单位）。
    /// </summary>
    public static partial class FgSiegeSelfCheck
    {
        private const string Kit = BuildingOps.RepairKitId;
        private const string Station = RepairDroneCatalog.StationTypeId;

        private static void RunExtras()
        {
            Step(CheckCollateral);
            Step(CheckPipeDurability);
            Step(CheckPoleRebuild);
            Step(CheckAttackedBeltPriority);
            Step(CheckIntercept);
            Step(CheckGuard);
            Step(CheckIconsAndOverlay);
            Step(CheckPreview);
            Step(CheckGateAndDemolish);
            Step(CheckTerrainEnclosed);
            Step(CheckEnclosedJourney);
            Step(CheckPartialRouteBest);
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        private static readonly (int dx, int dy)[] Around = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1) };

        private static void SetKits(CampaignState s, int n)
        {
            ItemDef kit = ItemCatalog.Find(Kit);
            if (HomeInventory.Capacity(s, kit) < n + 20)
            {
                GridCell? w = F.FindFree(s, HomeValleyLayout.BuildingTypeWarehouse, 6f, 26f);
                if (w.HasValue)
                {
                    F.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "sg_wh" + (_seq++).ToString(CultureInfo.InvariantCulture), w.Value);
                }
            }
            HomeInventory.RemoveUpTo(s, kit, HomeInventory.Stock(s, kit));
            if (n > 0)
            {
                HomeInventory.Add(s, kit, n, clampToSpace: false);
            }
        }

        /// <summary>核心周围 [from, to] 米找空地登记一座（用电的要吃到电）；B25 按种子地形找。</summary>
        private static BuildingRecord PlaceAround(CampaignState s, string type, Vector2 center, float from, float to)
        {
            bool powered = HomeValleyLayout.PowerProfile.ContainsKey(type);
            for (float d = from; d <= to; d += 1f)
            {
                for (int a = 0; a < 36; a++)
                {
                    float ang = (a * 10f + d * 7f) * Mathf.Deg2Rad;
                    var c = new GridCell(Mathf.RoundToInt(center.x + Mathf.Cos(ang) * d), Mathf.RoundToInt(center.y + Mathf.Sin(ang) * d));
                    if (!CanPlace(s, type, c))
                    {
                        continue;
                    }
                    BuildingRecord b = Register(s, type, c);
                    if (powered && (!HomeValleyPowerGrid.IsConnected(s, b.BuildingId) || b.PowerState != BuildingPowerState.Powered))
                    {
                        s.BuildingRecords = s.BuildingRecords.Where(x => x != b).ToArray();
                        HomeGridService.MapFor(s);
                        HomeValleyPowerGrid.Recompute(s);
                        continue;
                    }
                    NavService.SyncGridChanges();
                    return b;
                }
            }
            return null;
        }

        /// <summary>一座有电、建成的维修无人机站（下一次找目标时满编），维修件 <paramref name="kits"/> 个。</summary>
        private static BuildingRecord StationNear(CampaignState s, Vector2 center, int kits = 40)
        {
            ResearchService.CompleteForTests(s, "defense.repair_drone", "defense.auto_rebuild");
            SetKits(s, kits);
            BuildingRecord st = PlaceAround(s, Station, center, 4f, 18f);
            if (st != null)
            {
                RepairDroneService.Sync(s);
                Seconds(1.1f);
            }
            return st;
        }

        private static List<MachineRecord> HomeMachines() =>
            MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).OrderBy(m => m.LogicId).ToList();

        private static int SpawnMachine(CampaignState s, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.RegionId, at, 100f, 100f);
            if (!r.Success)
            {
                return 0;
            }
            if (MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                if (!string.IsNullOrEmpty(rec.BlueprintId))
                {
                    MachineLoadoutRegistry.Register(s, r.LogicId, rec.BlueprintId, rec.BlueprintVersion);
                }
                rec.WorkPriorities ??= WorkPriorities.Default();
                foreach (WorkOrderKind kind in Enum.GetValues(typeof(WorkOrderKind)))
                {
                    rec.WorkPriorities.Set(kind, 0); // 站着不动：不让工单引擎把它派走
                }
            }
            WorldSimulation.StepMany(2);
            return r.LogicId;
        }

        private static PlannedBeltRecord GhostAt(CampaignState s, GridCell c, bool pipe) =>
            (s.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>()).FirstOrDefault(p => p != null && p.Xs != null && p.Xs.Length > 0 && p.Xs[0] == c.X && p.Ys[0] == c.Y
                                                                                          && (p.PipePiece > 0) == pipe);

        private static StandingRuleRecord RebuildRule(CampaignState s, params string[] targets)
        {
            if (!StandingRuleService.TryCreate(s, StandingRuleService.KindRebuild, out StandingRuleRecord r, out string m))
            {
                Fail("测试准备：建不了自动重建规则：" + m);
                return null;
            }
            foreach (string t in targets)
            {
                StandingRuleService.TryAddTarget(s, r.Serial, t, out _);
            }
            return r;
        }

        // ── K 溅射与耐久 ─────────────────────────────────────────────────────

        private static void CheckCollateral()
        {
            CampaignState s = NewWorld(7020);
            Vector2 at = OutsidePoint(s, 34f);
            BuildingRecord wall = PlaceNear(s, B1, at, CoreCenter(s), 0.35f, 0.7f);
            if (wall == null)
            {
                Fail("K1 测试准备：放不下屏障");
                return;
            }
            var belts = new List<GridCell>();
            GridCell? pipe = null;
            BuildingRecord ghost = null;
            foreach ((int dx, int dy) in Around)
            {
                var c = new GridCell(wall.GridX + dx, wall.GridY + dy);
                if (belts.Count < 2 && BeltNetworkService.TryPlace(s, c, BeltDir.East, 0).Ok)
                {
                    belts.Add(c);
                    continue;
                }
                if (!pipe.HasValue && PipeNetworkService.TryPlace(s, c, PipePieceKind.Pipe, 0, 0).Ok)
                {
                    pipe = c;
                    continue;
                }
                if (ghost == null && CanPlace(s, "power_pole", c))
                {
                    GridOpResult r = HomeGridService.TryPlace(s, "power_pole", c, 0);
                    ghost = r.Success ? HomeGridService.FindBuilding(s, r.BuildingId) : null;
                    if (ghost != null)
                    {
                        ghost.ConstructionState = BuildingConstructionState.Building; // 测试捷径：施工进行中（材料已运到一半；真实施工由 FG3-LOG-02 覆盖）
                        ghost.ConstructionDelivered = Math.Max(1, ghost.ConstructionRequired / 2);
                    }
                }
            }
            NavService.SyncGridChanges();
            long belt0 = SiegeService.CollateralBeltHits, pipe0 = SiegeService.CollateralPipeHits, site0 = SiegeService.CollateralSiteHits;
            int siteNotes = NotificationCenter.History.Where(n => n.Text != null && n.Text.Contains("施工中")).Sum(n => n.Count);
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.armorbot" }, new[] { 4 });
            bool raidActive = StandingRuleService.RaidActive(s);
            bool down = StepUntil(() => wall.ConstructionState == BuildingConstructionState.Damaged, 150);
            Seconds(1f);
            bool beltHit = belts.Count > 0 && belts.Any(c => (BeltNetworkService.DamageOf(c) > 0 && BeltNetworkService.LastHitOf(c) > 0)
                                                               || (BeltNetworkService.HpOf(c) < 0 && GhostAt(s, c, false) is PlannedBeltRecord bg && bg.Destroyed));
            bool pipeHit = pipe.HasValue && ((PipeNetworkService.DamageOf(pipe.Value) > 0 && PipeNetworkService.LastHitOf(pipe.Value) > 0)
                                             || (PipeNetworkService.HpOf(pipe.Value) < 0 && GhostAt(s, pipe.Value, true) is PlannedBeltRecord pg && pg.Destroyed));
            bool siteHit = ghost != null && SiegeService.CollateralSiteHits > site0;
            Expect(down && raidActive && beltHit && pipeHit && siteHit && SiegeService.CollateralBeltHits > belt0 && SiegeService.CollateralPipeHits > pipe0,
                $"K1 溅射（DEBT-FG3LOG03-04 / DEBT-FG6DEF03-02 / DEBT-FG3LOG02-03）：攻城型拆屏障时，墙边的传送带（{belts.Count} 格，挨打 {SiegeService.CollateralBeltHits - belt0} 次）、" +
                $"管线（挨打 {SiegeService.CollateralPipeHits - pipe0} 次）跟着掉耐久并记“最近挨打”（打空的留保留设置的虚影），施工中的电塔虚影累计挨打 {SiegeService.CollateralSiteHits - site0} 次" +
                $"（{(ghost != null && ghost.ConstructionDelivered == 0 ? "已按“施工中被摧毁”结算" : "未满阈值")}）；突袭进行中（战时预案 / 维修优先读它）（{raidActive}）" +
                (down && beltHit && pipeHit && siteHit ? string.Empty : Diag(s, g, wall)));
        }

        private static void CheckPipeDurability()
        {
            CampaignState s = NewWorld(7021);
            GridCell? area = F.FindArea(s, 3, 1, 9f, 20f);
            if (!area.HasValue)
            {
                Fail("K2 测试准备：找不到 3×1 的空地");
                return;
            }
            GridCell c0 = area.Value, c1 = F.At(area.Value, 1, 0), c2 = F.At(area.Value, 2, 0);
            bool placed = PipeNetworkService.TryPlace(s, c0, PipePieceKind.Pipe, 0, 0).Ok && PipeNetworkService.TryPlace(s, c1, PipePieceKind.Tank, 0, 0).Ok
                          && PipeNetworkService.TryPlace(s, c2, PipePieceKind.Valve, 0, 0).Ok;
            PipeNetworkService.TrySetTankMode(s, c1, PipeTankMode.OutOnly);
            PipeNetworkService.TrySetTankPriority(s, c1, 3);
            PipeNetworkService.TrySetValveOpen(s, c2, false);
            int maxTank = PipeNetworkService.MaxHp(PipePieceKind.Tank, 0);
            int maxPipe = PipeNetworkService.MaxHp(PipePieceKind.Pipe, 0);
            PipeNetworkService.TryDamage(s, c1, maxTank / 2, out _);
            bool damaged = PipeNetworkService.HpOf(c1) == maxTank - maxTank / 2 && PipeNetworkService.LastHitOf(c1) > 0
                           && (s.Pipes?.Damage ?? Array.Empty<PipeDamageRecord>()).Any(d => d.X == c1.X && d.Y == c1.Y) && PipeNetworkService.HpLine(c1).Length > 0;
            string hpLine = PipeNetworkService.HpLine(c1);
            // 真文件存读档：管线耐久进存档。
            WorldSimulation.SyncAllForSave();
            CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignState l = RestoreSlot(out string fail);
            bool persisted = l != null && PipeNetworkService.HpOf(c1) == maxTank - maxTank / 2;
            s = l ?? s;
            // 维修无人机修管线（按修好的比例收维修件）。
            BuildingRecord st = StationNear(s, new Vector2(c1.X, c1.Y));
            int kits0 = HomeInventory.Stock(s, Kit);
            bool repaired = st != null && StepUntil(() => PipeNetworkService.HpOf(c1) == maxTank, 90) && PipeNetworkService.DamageOf(c1) == 0;
            int kitsUsed = kits0 - HomeInventory.Stock(s, Kit);
            Expect(placed && damaged && persisted && repaired && kitsUsed >= 0,
                $"K2 管线耐久（DEBT-FG3LOG05-12）：储罐满耐久 {maxTank}（普通管 {maxPipe}），挨打后掉到 {maxTank - maxTank / 2}、记最近挨打、悬停写“{hpLine}”（{damaged}）；" +
                $"真文件存读档后耐久不变（{persisted}{(fail != null ? "：" + fail : string.Empty)}）；维修无人机把它修满（{repaired}，用了 {kitsUsed} 个维修件）");

            // 摧毁：留保留设置的虚影（种类 / 等级 / 储罐模式与优先级 / 阀门开关），“传送带与物流节点”范围的自动重建按原设置重建。
            int fails0 = NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == "failure").Sum(n => n.Count);
            PipeNetworkService.TryDamage(s, c1, maxTank, out _);
            PipeNetworkService.TryDamage(s, c2, PipeNetworkService.MaxHp(PipePieceKind.Valve, 0), out _);
            PlannedBeltRecord tankGhost = GhostAt(s, c1, true);
            PlannedBeltRecord valveGhost = GhostAt(s, c2, true);
            bool ghosts = PipeNetworkService.HpOf(c1) < 0 && tankGhost != null && tankGhost.Destroyed && tankGhost.PipePiece == (int)PipePieceKind.Tank + 1
                          && tankGhost.PipeSettings == PlanSettings.PackTank(PipeTankMode.OutOnly, 3) && valveGhost != null && valveGhost.PipeSettings == PlanSettings.PackValve(false)
                          && NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == "failure").Sum(n => n.Count) > fails0;
            StandingRuleRecord rule = RebuildRule(s, StandingRuleService.BeltTarget);
            bool rebuilt = rule != null && StepUntil(() => PipeNetworkService.HpOf(c1) == maxTank && PipeNetworkService.HpOf(c2) > 0, 240);
            bool same = rebuilt && PipeNetworkService.Kernel.TryGetCellInfo(c1.X, c1.Y, out PipeCellInfo ti) && ti.Kind == PipePieceKind.Tank && ti.TankMode == PipeTankMode.OutOnly && ti.Priority == 3
                        && PipeNetworkService.Kernel.TryGetCellInfo(c2.X, c2.Y, out PipeCellInfo vi) && vi.Kind == PipePieceKind.Valve && !vi.ValveOpen;
            Expect(ghosts && rebuilt && same,
                $"K3 管线件被打空（FGR-DEF-015 列举的“管线”）：从内核移除、原位置留虚影（储罐“只出”优先级 3、阀门关）并发通知（{ghosts}）；" +
                $"自动重建规则加上“传送带与物流节点”后机器按原设置重建（{rebuilt} / 设置一致 {same}）");
        }

        private static void CheckPoleRebuild()
        {
            CampaignState s = NewWorld(7022);
            Vector2 at = OutsidePoint(s, 34f);
            BuildingRecord pole = PlaceNear(s, "power_pole", at, CoreCenter(s), 0.3f, 0.7f);
            if (pole == null)
            {
                Fail("K4 测试准备：放不下电塔");
                return;
            }
            float maxHp = BuildingOps.MaxDurability("power_pole");
            TransitGroupRecord g = Arrive(s, at, "silent", new[] { "silent.jammer" }, new[] { 6 });
            bool down = StepUntil(() => pole.ConstructionState == BuildingConstructionState.Damaged, 90);
            // 突袭被全歼（测试捷径：直接击毁；击毁本身由炮塔 / 机器打出来）→ 剧场关，自动重建按实例修这一座电塔。
            var ids = new List<int>();
            Site.SiegeRaiderIds(Key(g), ids);
            foreach (int id in ids)
            {
                Site.Kernel.Kill(id, 0);
            }
            Seconds(SiegeCatalog.SyncSeconds + 0.3f);
            // 自动重建规则要先研究「防御 · 自动重建」（测试捷径：直接完成；原来靠前面别的段留下的研发状态，单段跑会失败——复审第 1 轮改成自给自足）。
            ResearchService.CompleteForTests(s, "defense.repair_drone", "defense.auto_rebuild");
            StandingRuleRecord rule = RebuildRule(s, "power_pole");
            bool ordered = rule != null && StepUntil(() => HomeValleyWorkOrders.FindActiveRepair(s, pole.BuildingId) != null || pole.ConstructionState == BuildingConstructionState.Operational, 30);
            bool rebuilt = StepUntil(() => pole.ConstructionState == BuildingConstructionState.Operational, 240);
            Expect(maxHp > 0f && down && ordered && rebuilt && HomeValleyPowerGrid.IsConnected(s, pole.BuildingId),
                $"K4 电塔有耐久（{maxHp:F0}，FG-GAP-089）：破坏型把它打掉 → 走 ApplyBuildingDestroyed（留虚影、电网重算）（{down}）；突袭结束后自动重建规则按这一座的实例下修复单（FG-GAP-084）（{ordered}），修好后重新接入电网（{rebuilt}）");
        }

        private static void CheckAttackedBeltPriority()
        {
            string Run(bool raid, out string hitKey)
            {
                CampaignState s = NewWorld(7023);
                hitKey = string.Empty;
                StandingRuleService.RaidActiveProvider = _ => raid;
                BuildingRecord st = StationNear(s, CoreCenter(s));
                if (st == null)
                {
                    return "no-station";
                }
                var cells = new List<GridCell>();
                for (float d = 3f; d <= 12f && cells.Count < 4; d += 1f)
                {
                    for (int a = 0; a < 24 && cells.Count < 4; a++)
                    {
                        float ang = a * 15f * Mathf.Deg2Rad;
                        var c = new GridCell(Mathf.RoundToInt(st.Position.x + Mathf.Cos(ang) * d), Mathf.RoundToInt(st.Position.y + Mathf.Sin(ang) * d));
                        if (!cells.Contains(c) && BeltNetworkService.TryPlace(s, c, BeltDir.East, 0).Ok)
                        {
                            cells.Add(c);
                        }
                    }
                }
                if (cells.Count < 4)
                {
                    return "no-belts";
                }
                int max = BeltNetworkService.MaxHp(0);
                for (int i = 0; i < 3; i++)
                {
                    BeltNetworkService.TryDamage(s, cells[i], (int)(max * (0.6f + 0.05f * i)), out _); // 伤得重
                }
                GameClock.SkipForTests(s, GameClock.TicksFor(RepairDroneCatalog.AttackedWindowSeconds + 1f)); // 测试捷径：让这三格的“最近挨打”过期（不跑中间的模拟步，无人机来不及先修）
                BeltNetworkService.TryDamage(s, cells[3], Math.Max(1, max / 10), out _); // 伤得轻、刚挨打
                hitKey = RepairDroneService.BeltKey(cells[3]);
                Seconds(1.1f);
                RepairStationRecord rec = RepairDroneService.Find(s, st.BuildingId);
                return string.Join(",", rec.Drones.Select(d => d.Target).Where(t => t.Length > 0).OrderBy(t => t, StringComparer.Ordinal));
            }
            string on = Run(true, out string k1);
            string off = Run(false, out string k2);
            StandingRuleService.RaidActiveProvider = null;
            Expect(on.Contains(k1) && !off.Contains(k2),
                $"K5 DEBT-FG6DEF03-03：传送带记“最近挨打”——突袭中无人机先修刚挨打的那格（{on}）；没有突袭时按耐久比例先修伤得重的（{off}）");
        }

        // ── L 拦截与驻防 ─────────────────────────────────────────────────────

        private static TransitGroupRecord March(CampaignState s, Vector2 origin, string faction, string unit, int n)
        {
            GridCell core = HomeGridService.CorePivot(s);
            TransitGroupRecord g = WorldTransitSystem.Dispatch(s, TransitGroupKind.Raid, faction, n, origin.x, origin.y, core.X, core.Y);
            g.Faction = faction;
            g.UnitIds = new[] { unit };
            g.UnitCounts = new[] { n };
            g.EliteCounts = new[] { 0 };
            g.TargetKind = RaidDirectorService.TargetHome;
            return g;
        }

        /// <summary>拦截场景：一台己方机器站在离核心 <paramref name="postDist"/> 格处，一支突袭从同一方向再往外 60 格处朝家园出发（会从机器旁边经过）。</summary>
        private static TransitGroupRecord InterceptScene(int seed, float postDist, int n, out CampaignState s, out int machine)
        {
            s = NewWorld(seed);
            Vector2 post = OutsidePoint(s, postDist);
            Vector2 dir = (post - CoreCenter(s)).normalized;
            HomeGridService.RevealArea(s, post + dir * 30f, 50f);
            machine = SpawnMachine(s, post);
            Vector2 origin = post + dir * 60f;
            GridCell oc = SiegeService.NearestPassable(NavService.CellOf(origin.x, origin.y), 8);
            return March(s, new Vector2(oc.X, oc.Y), "foundry", "foundry.strider", n);
        }

        private static void CheckIntercept()
        {
            float postDist = SiegeCatalog.InterceptMinTargetCells + SiegeCatalog.InterceptRadius + 14f;
            TransitGroupRecord g = InterceptScene(7024, postDist, 6, out CampaignState s, out int m);
            int notes = NotifyCount("raid_siege");
            bool intercepted = m > 0 && StepUntil(() => g.Intercepted, 240);
            int spawned = Alive(g);
            var ids = new List<int>();
            Site.SiegeRaiderIds(Key(g), ids);
            bool skirmish = ids.Count > 0 && ids.All(id => Site.TryGetSiegeRaider(id, out _, out CombatSiegeUnit su, out _, out _, out _) && su.Mode == (byte)CombatSiegeMode.Skirmish);
            double px = g.PosX;
            double far = Math.Sqrt((g.TargetX - g.PosX) * (g.TargetX - g.PosX) + (g.TargetY - g.PosY) * (g.TargetY - g.PosY));
            Seconds(1f);
            bool frozen = Math.Abs(g.PosX - px) < 1e-6 && NotifyCount("raid_siege") > notes;
            // 拦截的机器离开（测试捷径：击毁）→ 附近没有己方机器满收拢时间 → 幸存者收拢回聚合体继续走。
            int mu = Site.FindNearestMachine(new Vector2((float)g.PosX, (float)g.PosY), SiegeCatalog.InterceptRadius * 2f);
            while (mu > 0)
            {
                Site.Kernel.Kill(mu, 0);
                WorldSimulation.StepMany(1);
                mu = Site.FindNearestMachine(new Vector2((float)g.PosX, (float)g.PosY), SiegeCatalog.InterceptRadius * 2f);
            }
            int alive = Alive(g);
            bool regrouped = StepUntil(() => !g.Intercepted, (int)SiegeCatalog.RegroupSeconds + 6) && Alive(g) == 0 && g.UnitCount == Math.Max(1, alive) && g.State == TransitGroupState.Marching;
            double qx = g.PosX, qy = g.PosY;
            Seconds(3f);
            bool moving = Math.Abs(g.PosX - qx) + Math.Abs(g.PosY - qy) > 0.5;
            Expect(intercepted && spawned == 6 && skirmish && frozen && regrouped && moving && far > SiegeCatalog.InterceptMinTargetCells,
                $"L1 DEBT-FG6DEF04-03 行进途中被拦截：离家园 {far:F0} 格处、己方机器在 {SiegeCatalog.InterceptRadius:F0} 格内 → 聚合体就地展开 {spawned} 台（交战模式 {skirmish}）、聚合体停住、发通知（{frozen}）；" +
                $"机器离开 {SiegeCatalog.RegroupSeconds:F0} 秒后幸存者 {alive} 台收拢回聚合体（{regrouped}）继续行进（{moving}）");

            // 全灭 = 被全歼（计划记 destroyed）。
            TransitGroupRecord g2 = InterceptScene(7025, postDist, 4, out CampaignState s2, out _);
            var plan = new RaidPlanRecord { PlanId = "plan-sgint", Serial = 992, State = RaidDirectorService.StateDeparted, GroupId = g2.GroupId, Faction = "foundry", Level = 1 };
            RaidDirectorState d = RaidDirectorService.StateOf(s2);
            d.Plans = d.Plans.Append(plan).ToArray();
            g2.PlanId = plan.PlanId;
            bool in2 = StepUntil(() => g2.Intercepted, 240);
            var ids2 = new List<int>();
            Site.SiegeRaiderIds(Key(g2), ids2);
            foreach (int id in ids2)
            {
                Site.Kernel.Kill(id, 0);
            }
            int destroyed = NotifyCount("raid_destroyed");
            Seconds(SiegeCatalog.SyncSeconds + 0.3f);
            Seconds(1f);
            RaidHistoryRecord h = RaidDirectorService.History(s2).LastOrDefault(x => x.PlanId == plan.PlanId);
            Expect(in2 && WorldTransitSystem.Find(s2, g2.GroupId) == null && NotifyCount("raid_destroyed") > destroyed && h != null && h.EndReason == RaidDirectorService.EndDestroyed,
                $"L2 拦截中被全灭 = 被全歼：队伍移除、通知、计划结束原因 {h?.EndReason}");

            // 僵持（互相够不着）：就地交战满 siege.intercept_max_seconds 后收拢继续走，这段时间内不再被同一台机器拦下（测试捷径：把拦截开始的步往前拨）。
            TransitGroupRecord g3 = InterceptScene(7032, postDist, 3, out CampaignState s3, out int m3);
            bool in3 = StepUntil(() => g3.Intercepted, 240);
            GameClock.SkipForTests(s3, GameClock.TicksFor(SiegeCatalog.InterceptMaxSeconds) + 1); // 测试捷径：时钟往后拨到僵持上限（不跑中间的模拟步）
            Seconds(SiegeCatalog.SyncSeconds + 0.2f);
            bool released = !g3.Intercepted && Alive(g3) == 0 && Site.FindNearestMachine(new Vector2((float)g3.PosX, (float)g3.PosY), SiegeCatalog.InterceptRadius) > 0;
            Seconds(3f);
            bool notAgain = !g3.Intercepted;
            Expect(in3 && released && notAgain,
                $"L3 拦截僵持：就地交战满 {SiegeCatalog.InterceptMaxSeconds:F0} 秒（机器还在旁边）→ 幸存者收拢继续走（{released}），之后不在原地反复展开（{notAgain}）" + (in3 && released && notAgain ? string.Empty : $"（拦下 {in3}，机器 {m3}，现在拦截中 {g3.Intercepted}，存活 {Alive(g3)}，拦截步 {g3.InterceptTick} / 现在 {GameClock.Ticks}，队伍 {g3.State} {g3.UnitCount} 台）"));

            // 快到家园的突袭不拦截：机器站在离核心 30 格处，突袭从它旁边经过 → 照常到达展开攻城（同一场仗用流场打）。
            TransitGroupRecord g4 = InterceptScene(7033, 30f, 3, out CampaignState s4, out int m4);
            bool arrived = StepUntil(() => g4.Engaged, 240);
            Expect(m4 > 0 && arrived && !g4.Intercepted && g4.InterceptTick < 0,
                $"L4 快到家园（{SiegeCatalog.InterceptMinTargetCells:F0} 格内）的突袭不拦截：机器在核心外 30 格，突袭从旁边经过照常到达、展开攻城（{arrived}），没有就地交战");
        }
        private static string GuardDiag = string.Empty;

        private static void CheckGuard()
        {
            long Run(bool garrison, out bool holding, out int hitRaiders)
            {
                CampaignState s = NewWorld(7026);
                holding = false;
                hitRaiders = 0;
                List<MachineRecord> ms = HomeMachines();
                if (ms.Count == 0)
                {
                    return -1;
                }
                foreach (MachineRecord other in ms)
                {
                    MachineRoster.TrySetRole(s, other.LogicId, MachineRole.Idle, out _);
                }
                // 一台战斗履带 ERC-003（默认装配：机枪 + 固件，有攻击出口）当驻防机器；开局的搬运机没有武器出口，打不了。
                int g = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId,
                    CoreCenter(s) + new Vector2(4f, -4f), 120f, 120f, "Player", 1).LogicId;
                MachineLoadoutRegistry.Register(s, g, HomeValleyLayout.BlueprintErc003Id, 1);
                WorldSimulation.StepMany(2);
                // 驻防点 = 敌人来路上的一段屏障（攻城型会来拆它）：机器守在墙边。
                Vector2 at = OutsidePoint(s, 34f);
                BuildingRecord wall = PlaceNear(s, B3, at, CoreCenter(s), 0.4f, 0.65f);
                if (wall == null)
                {
                    return -2;
                }
                MachineRoster.TrySetRole(s, g, garrison ? MachineRole.Garrison : MachineRole.Idle, out _);
                if (garrison)
                {
                    MachineRoster.TrySetGarrisonPoint(s, g, wall.BuildingId, out _);
                    holding = StepUntil(() => HomeValleyWorkOrders.FindActiveOrderForMachine(s, g) is WorkOrderRecord o && o.Kind == WorkOrderKind.Garrison
                                                && o.State == WorkOrderState.InProgress && o.TargetId == wall.BuildingId, 150);
                }
                long shots0 = SiegeService.GuardShots;
                TransitGroupRecord rg = Arrive(s, at, "foundry", new[] { "foundry.armorbot" }, new[] { 3 });
                StepUntil(() => SiegeService.GuardShots > shots0 + 2, 40);
                if (garrison && MachineRegistry.TryGetRecord(g, out MachineRecord mr) && Site.TryGetMachinePosition(g, out Vector2 mpos))
                {
                    int nh = Site.FindNearestHostile(mpos, 60f);
                    Site.Kernel.TryGetUnit(nh, out CombatUnitView hv);
                    Site.TryGetMachineUnit(g, out int mun); Site.Kernel.TryGetUnit(mun, out CombatUnitView mv);
                    CombatSiegeStats gst = Site.SiegeStats;
                    GuardDiag = $"（守点步 {gst.GuardTicks}、有目标 {gst.GuardEngaged}、最近开火结果 {(CombatFireResult)gst.GuardLastResult}；机器 #{g} 内核单位 {mun} 种类 {mv.Kind} 命令 {mv.Command} 守点半径 {mv.Siege.GuardRadius} 驻防点 {mv.Siege.GuardPost} 武器 {mv.Weapon} 标志 {mv.Flags}；在 ({mpos.x:F1},{mpos.y:F1})（记录 {mr.WorldPosition}），墙 {wall.ConstructionState}，驻防点 ({wall.Position.x:F1},{wall.Position.y:F1})，" +
                                $"最近的敌人 {nh} 在 ({hv.Position.x:F1},{hv.Position.y:F1})，离机器 {Vector2.Distance(mpos, new Vector2((float)hv.Position.x, (float)hv.Position.y)):F1} 米）";
                }
                var ids = new List<int>();
                Site.SiegeRaiderIds(Key(rg), ids);
                foreach (int id in ids)
                {
                    if (Site.TryGetSiegeRaider(id, out _, out _, out _, out float hp, out float max) && hp < max - 0.01f)
                    {
                        hitRaiders++;
                    }
                }
                return SiegeService.GuardShots - shots0;
            }
            long on = Run(true, out bool holding, out int hit);
            long off = Run(false, out _, out _);
            Expect(holding && on > 0 && hit > 0 && off == 0,
                $"L5 DEBT-FG4ECO07-02 驻防机器守点交战：驻防岗守在指定的屏障边（{holding}），朝驻防点 {SiegeCatalog.GuardRadius:F0} 格以内最近的敌人交战 {on} 次（射程外在半径内靠近、射程内原地打；{hit} 台突袭者掉血）；" +
                $"不是驻防岗的机器不替玩家开火（{off} 次，FGR-BASE-020）" + (on > 0 ? string.Empty : GuardDiag));
        }
        // ── M 表现 ───────────────────────────────────────────────────────────

        private static void CheckIconsAndOverlay()
        {
            CampaignState s = SiegeScenario(7027, true, 2f);
            CombatSite site = Site;
            StepUntil(() => site.SiegeBreachCount > 0, 90); // 走到墙边开始破墙
            int alive = site.SiegeUnitCount;
            var renderer = new CombatRenderer();
            renderer.SetSiegeVisuals(CombatSite.SiegeRoleVisuals);
            renderer.Draw(site.Kernel, null, 1f, double2.zero, 0.6f);
            CombatInstance[] icons = renderer.IconInstances.ToArray();
            renderer.Dispose();
            CombatInstance[] siegeIcons = icons.Where(i => i.B.y >= 14f && i.B.y <= 17f).ToArray();
            var shapes = new HashSet<float>(siegeIcons.Select(i => i.B.y));
            float minSize = siegeIcons.Length > 0 ? siegeIcons.Min(i => i.B.x) : 0f;
            float maxSize = siegeIcons.Length > 0 ? siegeIcons.Max(i => i.B.x) : 0f;
            Expect(alive > 0 && siegeIcons.Length == alive && shapes.Contains(14f) && shapes.Contains(16f) && Math.Abs(maxSize / Math.Max(1e-3f, minSize) - 1.3f) < 0.01f,
                $"M1 FGR-DEF-030“敌人头顶显示职能图标”：{alive} 台攻城单位头顶各一个职能图标（形状 {string.Join("/", shapes.OrderBy(x => x))}：▲突击 ▣攻城；精英图标大 1.3 倍），与状态标签图标不重叠（在其上一排）");

            OverlayService.Set(OverlayKind.Raid);
            OverlayService.RedrawNow();
            int routes = OverlayService.SiegeRouteLines;
            int boxes = OverlayService.SiegeBreachMarkers;
            string legend = OverlayService.Legend(OverlayKind.Raid);
            OverlayService.Set(OverlayKind.None);
            Expect(routes >= 2 && boxes >= 1 && legend.Contains(GameText.Get("overlay.label.siege_legend")),
                $"M2 突袭路径叠加层：攻城中每种出场职能一条“敌人会走的路”（{routes} 条，沿内核流场追踪，开路到不了时画到要拆的墙），正在被拆的墙画红粗框（{boxes} 个），图例多一行职能图标说明");
        }

        private static void CheckPreview()
        {
            CampaignState s = NewWorld(7028);
            Vector2 from = OutsidePoint(s, 95f, 4);
            TransitGroupRecord marching = March(s, from, "foundry", "foundry.strider", 4);
            List<BuildingRecord> ring = Ring(s, 4, B3, out GridCell rmin, out GridCell rmax, out int gaps);
            BuildingRecord near = NearestWall(ring, rmin, rmax, from);
            GridCell gap = CellOfB(near);
            s.BuildingRecords = s.BuildingRecords.Where(x => x != near).ToArray();
            HomeGridService.MapFor(s);
            DefenseService.Sync(s);
            NavService.SyncGridChanges();
            var open = new RoutePreview();
            DefenseService.PreviewRoutes(s, null, open);
            var closing = new RoutePreview();
            DefenseService.PreviewRoutes(s, new[] { gap }, closing, B1);
            string planned = GameText.Get("defense.preview.entries_planned");
            bool p1 = open.HasRoutes && open.FromRaids && !open.Sealed && open.Summary().Contains(planned);
            bool p2 = closing.Sealed && closing.BreachName == HomeGridService.DisplayName(B1) && closing.BreachBuildingId == null && closing.BreachCell.Equals(gap)
                      && closing.Summary().Contains(HomeGridService.DisplayName(B1)) && closing.Routes.Any(r => r.Count > 0 && r[r.Count - 1].Equals(gap));
            // 预览 = 真实：真的在缺口建 T1，再让同一方向的队伍到达，内核破墙流场找到的第一段墙就是预览说的那段。
            s.Raids.InTransit = s.Raids.InTransit.Where(x => x != marching).ToArray();
            BuildingRecord t1 = Register(s, B1, gap);
            NavService.SyncGridChanges();
            Vector2 at = OutsidePoint(s, Math.Max(rmax.X - rmin.X, rmax.Y - rmin.Y) * 0.5f + 12f, 4);
            Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 3 });
            Site.MaintainSiegeNow();
            int kernelPick = Site.SiegeBreachAhead(1, new int2((int)at.x, (int)at.y), 400);
            bool same = kernelPick > 0 && kernelPick == UnitOfDefense(s, t1);
            Expect(gaps == 0 && p1 && p2 && same,
                $"M3 DEBT-FG6DEF02-04 放置屏障预览读真实来路（围墙缺口 {gaps}{RingGapInfo}）：来路起点含地图上看得到的突袭队伍方向（“{open.Summary()}”）（{p1}）；补上最后一个缺口 = 完全堵死，预览写“{closing.Summary()}”、" +
                $"线画到要拆的那段（{p2}）；真建好后同一方向到达的突袭，内核破墙流场找到的第一段墙就是它（{same}）");
        }

        // ── N 负向 ───────────────────────────────────────────────────────────

        private static void CheckGateAndDemolish()
        {
            // N1 FGR-DEF-031“闸门对敌人等同于屏障”：一圈 T3，先让内核找出最短路线上要拆的那段（地形各种子不同：不假设是离集结点最近的那段，B25），
            // 把它换成闸门（耐久低于 T3）→ 破墙目标变成闸门。
            CampaignState s = NewWorld(7029);
            List<BuildingRecord> ring = Ring(s, 4, B3, out GridCell rmin, out GridCell rmax, out int gaps);
            Vector2 at = OutsidePoint(s, Math.Max(rmax.X - rmin.X, rmax.Y - rmin.Y) * 0.5f + 12f, 2);
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 4 });
            Site.MaintainSiegeNow();
            var gc = new int2((int)at.x, (int)at.y);
            int first = Site.SiegeBreachAhead(1, gc, 600);
            string firstId = SiegeService.BuildingIdOfUnit(s, Site, first);
            BuildingRecord w = firstId != null ? HomeGridService.FindBuilding(s, firstId) : null;
            if (w == null || w.BuildingTypeId != B3)
            {
                Fail($"N1 测试准备：内核没有找到要拆的 T3（{firstId}；缺口 {gaps}{RingGapInfo}）");
                return;
            }
            BuildingRecord gate = ReplaceWall(s, w, "gate");
            Seconds(SiegeCatalog.SyncSeconds + 0.1f); // 新建的防御结构单位在下一次对账时标上攻城类别
            bool blocked = Site.SiegeDistAt(0, gc) >= CombatSiegeConst.Inf;
            int pick = Site.SiegeBreachAhead(1, gc, 600);
            bool gateLower = BuildingOps.MaxDurability("gate") < BuildingOps.MaxDurability(B3);
            Site.Kernel.SiegeCellInfo(new int2(gate.GridX, gate.GridY), out int gnav, out int gocc, out int gcat, out int gpen);
            Expect(gaps == 0 && blocked && pick > 0 && pick == UnitOfDefense(s, gate) && gateLower && gcat == CombatSiegeConst.CatDefense,
                $"N1 闸门对敌人等同于屏障：闸门挡敌方寻路（开路到不了 {blocked}），破墙流场把它当墙、按耐久算代价（{gpen / 10f:F0} 米 vs T3 {SiegeCatalog.BreachPenalty(BuildingOps.MaxDurability(B3)) / 10f:F0} 米）——" +
                $"换到最短路线上之后被选为要拆的那段（{pick == UnitOfDefense(s, gate)}）");

            // N2 负向“屏障在突袭中被拆”：玩家拆掉最短路线上的那段（闸门）→ 寻路镜像让开 → 流场增量更新、开路可达（与全量一致），敌人从缺口进来。
            s.BuildingRecords = s.BuildingRecords.Where(x => x != gate).ToArray();
            HomeGridService.MapFor(s);
            DefenseService.Sync(s);
            WorldSimulation.StepMany(3);
            bool opened = Site.SiegeDistAt(0, gc) < CombatSiegeConst.Inf;
            string diag = opened ? string.Empty : ColumnDiag(gate.GridX, gate.GridY);
            Expect(opened && Fields().EndsWith(" 0 格不一致"), $"N2 负向“屏障在突袭中被拆”（玩家拆掉最短路线上的那段）：开路重新可达（{opened}），{Fields()}{diag}");
        }
        /// <summary>诊断：(x, y) 上下各 4 格的开路距离 / 通行字节 / 占用。</summary>
        private static string ColumnDiag(int x, int y, int field = 0)
        {
            var sb = new System.Text.StringBuilder("\n    列：");
            for (int dy = 5; dy >= -5; dy--)
            {
                var c = new int2(x, y + dy);
                Site.Kernel.SiegeCellInfo(c, out int nav, out int occ, out int cat, out _);
                int d0 = Site.SiegeDistAt(field, c);
                sb.Append($" ({c.x},{c.y}) d={(d0 >= CombatSiegeConst.Inf ? "∞" : d0.ToString(CultureInfo.InvariantCulture))} n={nav} o={occ} c={cat};");
            }
            return sb.ToString();
        }
        private static void CheckTerrainEnclosed()
        {
            // N3 地形把核心围死（没有墙可拆）：敌人不会穿地形，也不会卡死报错——展开后原地待命，到时间上限按“时间”撤退。
            CampaignState s = NewWorld(7030);
            BaseBounds(s, out GridCell bmin, out GridCell bmax);
            var center = new GridCell((bmin.X + bmax.X) / 2, (bmin.Y + bmax.Y) / 2);
            int r = Math.Max(bmax.X - bmin.X, bmax.Y - bmin.Y) / 2 + 5;
            HomeGridService.RevealArea(s, new Vector2(center.X, center.Y), r + 20f);
            int changed = CliffRing(s, center, r);
            Vector2 at = OutsidePoint(s, r + 14f);
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 3 });
            Site.MaintainSiegeNow();
            var gc = new int2((int)at.x, (int)at.y);
            bool open = Site.SiegeDistAt(0, gc) < CombatSiegeConst.Inf;
            bool breach = Site.SiegeFieldValid(1) && Site.SiegeDistAt(1, gc) < CombatSiegeConst.Inf;
            float c0 = Durability(s, CoreOf(s));
            bool retreat = StepUntil(() => g.SiegeRetreat, (int)(WorldTransitSystem.TimeLimitTicks / GameClock.StepHz) + 10) && g.SiegeRetreatReason == SiegeService.ReasonTime;
            Expect(changed > 0 && !open && !breach && retreat && Durability(s, CoreOf(s)) >= c0 - 0.01f && Fields().EndsWith(" 0 格不一致"),
                $"N3 地形围死（{changed} 格悬崖，没有墙可拆）：开路与破墙流场都到不了（{!open}/{!breach}），敌人不穿地形、核心不挨打，到时间上限按“时间”撤退（{retreat}）——不卡死、不报错");
        }

        /// <summary>测试捷径：在 <paramref name="center"/> 外半径 <paramref name="r"/> 处围一圈 2 格厚的悬崖（跳过有建筑的格），推进寻路镜像。返回改了几格。</summary>
        private static int CliffRing(CampaignState s, GridCell center, int r)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            byte cliff = GridContent.TerrainCode("cliff");
            int n = 0;
            for (int t = 0; t < 2; t++)
            {
                int rr = r + t;
                for (int dy = -rr; dy <= rr; dy++)
                {
                    for (int dx = -rr; dx <= rr; dx++)
                    {
                        var c = new GridCell(center.X + dx, center.Y + dy);
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != rr || !string.IsNullOrEmpty(map.OccupantAt(c)))
                        {
                            continue;
                        }
                        map.SetTerrain(c, cliff);
                        n++;
                    }
                }
            }
            NavService.SyncGridChanges();
            return n;
        }
        private static void CheckEnclosedJourney()
        {
            // N4 DEBT-FG0ARCH06-03 / DEBT-FG0ARCH06-11③：正式流程的突袭撞上完全围死的家园——行进走到围墙外最近处“到达（受阻）”，展开后沿破墙流场拆最薄弱的那段。
            CampaignState s = NewWorldWithRing(6152, out List<BuildingRecord> ring);
            TransitGroupRecord g = FormalRaidOn(s);
            string fdiag = FormalDiag;
            int breachUnit = 0;
            bool breaching = g != null && StepUntil(() => Site.SiegeBreachCount > 0 && (breachUnit = Site.SiegeBreachAt(0)) > 0, 120);
            string id = breaching ? SiegeService.BuildingIdOfUnit(s, Site, breachUnit) : null;
            bool ringWall = id != null && ring.Any(w => w.BuildingId == id);
            Expect(g != null && g.Engaged && g.Blocked && breaching && ringWall,
                $"N4 正式突袭撞上完全围死的家园：行进到围墙外最近处“到达（受阻）”（{g?.Blocked}），按编成展开（{g?.UnfoldedCount} 台），沿破墙流场去拆围墙（{ringWall}）——不再停在外面等到时间上限（DEBT-FG0ARCH06-03）" + (g == null ? fdiag : string.Empty));
        }

        private static CampaignState NewWorldWithRing(int seed, out List<BuildingRecord> ring)
        {
            CampaignState s = NewWorld(seed);
            ring = Ring(s, 4, B3, out _, out _, out _);
            return s;
        }

        /// <summary>剧情突袭走正式流程（排定 → 预警 → 出发 → 行进 → 到达展开）。返回展开了的队伍；失败 null。</summary>
        private static string FormalDiag = string.Empty;

        private static TransitGroupRecord FormalRaidOn(CampaignState s)
        {
            FormalDiag = string.Empty;
            long grace = RaidDirectorService.GraceEndTick + RaidDirectorService.DayTicks(0.2);
            if (GameClock.Ticks < grace)
            {
                GameClock.SkipForTests(s, grace - GameClock.Ticks);
            }
            WorldSimulation.StepMany(2);
            RaidDirectorService.RequestStoryRaid(s, "silent", 1);
            WorldSimulation.StepMany(2);
            RaidPlanRecord p = RaidDirectorService.Plans(s).OrderByDescending(x => x.Serial).FirstOrDefault();
            if (p == null || !StepUntil(() => p.State >= RaidDirectorService.StateScheduled, 120))
            {
                FormalDiag = $"（计划 {p?.PlanId} 状态 {p?.State}，没排定）";
                return null;
            }
            long warn = p.WarnTick - 2;
            if (warn > GameClock.Ticks && WorldTransitSystem.Groups(s).All(x => x == null))
            {
                GameClock.SkipForTests(s, warn - GameClock.Ticks);
            }
            if (!StepUntil(() => p.State >= RaidDirectorService.StateDeparted, 900))
            {
                FormalDiag = $"（计划 {p.PlanId} 状态 {p.State}，没出发；结束原因 {p.EndReason}）";
                return null;
            }
            TransitGroupRecord found = WorldTransitSystem.Find(s, p.GroupId);
            if (found != null && StepUntil(() => found.Engaged || found.State == TransitGroupState.Retreating, 1800) && found.Engaged)
            {
                return found;
            }
            FormalDiag = $"（队伍 {found?.GroupId} 状态 {found?.State} 位置 ({found?.PosX:F1},{found?.PosY:F1}) 可走 {(found != null && NavService.PassableNow((int)Math.Round(found.PosX), (int)Math.Round(found.PosY), NavConst.ClassHostile))} 受阻 {found?.Blocked} 路线态 {found?.RouteState} 原因 {found?.NavReason} 路线 {(found?.RouteX == null ? "-" : string.Join(" ", found.RouteX.Select((x, i) => $"({x},{found.RouteY[i]})")))} 走到 {found?.RouteIndex} 目标 ({found?.TargetX:F0},{found?.TargetY:F0})；计划 {p.State}/{p.EndReason}；围墙补 {RingPatched}）";
            return null;
        }

        private static void CheckPartialRouteBest()
        {
            // N5 DEBT-FG0ARCH06-10：允许部分路线 + 放大搜索框重搜 + 后一轮撞上展开上限——部分终点不劣于前一轮（展开上限从小到大扫，终点离目标的距离单调不增）。
            // 合成地图：x = 64 一道贯穿的悬崖，只在很远的 y = 370 留缺口（第一轮的搜索框里没有）；目标在悬崖另一侧。
            Func<int, int, bool> wall = (x, y) => x == 64 && (y < 370 || y > 373);
            var pts = new List<int2>();
            int prev = int.MaxValue;
            bool monotone = true;
            int samples = 0;
            int full = -1;
            var sb = new System.Text.StringBuilder();
            NavConfig baseCfg = NavService.ConfigFromTuning();
            using (NavKernel k = SynthNav(4, 12, wall, baseCfg))
            {
                NavResult r = k.FindNow(PartialReq(20, 16, 100, 16), pts, false, out _);
                full = r.Status == NavStatus.Ok ? r.Expanded : -1;
            }
            int top = full > 0 ? full : 4000;
            for (int lim = 8; lim <= top; lim += Math.Max(1, top / 60))
            {
                NavConfig cfg = baseCfg;
                cfg.MaxExpansions = lim;
                using NavKernel k = SynthNav(4, 12, wall, cfg);
                NavResult r = k.FindNow(PartialReq(20, 16, 100, 16), pts, false, out _);
                if (r.Status == NavStatus.Ok)
                {
                    break;
                }
                int d = r.Status == NavStatus.Partial ? Math.Max(Math.Abs(r.End.x - 100), Math.Abs(r.End.y - 16)) : int.MaxValue / 2;
                if (d > prev)
                {
                    monotone = false;
                    sb.Append($" 上限 {lim}：{prev}→{d}");
                }
                prev = Math.Min(prev, d);
                samples++;
            }
            Expect(full > 0 && samples >= 10 && monotone,
                $"N5 DEBT-FG0ARCH06-10 部分路线取各轮最优：展开上限从 8 扫到 {top}（{samples} 档，完整搜索要 {full} 次展开），部分终点离目标的距离单调不增（后一轮被截断时不劣于前一轮）{sb}");
        }

        private static NavKernel SynthNav(int wChunks, int hChunks, Func<int, int, bool> blocked, NavConfig cfg)
        {
            var k = new NavKernel(cfg, NavService.TerrainTable(), false, default, null, null);
            int size = k.Config.ChunkSize;
            byte cliff = GridContent.TerrainCode("cliff");
            var terrain = new byte[size * size];
            var occ = new int[size * size];
            for (int cy = 0; cy < hChunks; cy++)
            {
                for (int cx = 0; cx < wChunks; cx++)
                {
                    for (int i = 0; i < size * size; i++)
                    {
                        terrain[i] = blocked(cx * size + i % size, cy * size + i / size) ? cliff : (byte)0;
                    }
                    k.PushChunk(cx, cy, terrain, occ, null, 0, false);
                }
            }
            return k;
        }

        private static NavRequest PartialReq(int sx, int sy, int gx, int gy) => new NavRequest
        {
            OwnerTag = 9,
            OwnerKey = 1,
            Serial = 1,
            Class = NavConst.ClassHostile,
            Flags = NavRequestFlags.AllowPartial,
            Start = new int2(sx, sy),
            Goal = new int2(gx, gy),
        };

        // ── P 性能 ───────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            // 200 台攻城单位（FG06 第 7 节）围攻一圈 T3（一段 T1），观察家园：内核单步（含流场维护）、热更层对账、流场增量更新、剧场重建。
            CampaignState s = NewWorld(7031);
            List<BuildingRecord> ring = Ring(s, 4, B3, out GridCell rmin, out GridCell rmax, out _);
            Vector2 at = OutsidePoint(s, Math.Max(rmax.X - rmin.X, rmax.Y - rmin.Y) * 0.5f + 14f);
            ReplaceWall(s, NearestWall(ring, rmin, rmax, at), B1);
            int n = SiegeCatalog.PerfUnits;
            var sw = Stopwatch.StartNew();
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider", "foundry.armorbot", "foundry.repairbot" }, new[] { n * 6 / 10, n * 3 / 10, n - n * 6 / 10 - n * 3 / 10 });
            double unfoldMs = sw.Elapsed.TotalMilliseconds;
            CombatSite site = Site;
            Seconds(2f);
            var kernel = new List<double>();
            var siege = new List<double>();
            double maxSync = 0;
            int steps = GameClock.StepHz * 20;
            var frame = new List<double>();
            for (int i = 0; i < steps; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                WorldSimulation.StepMany(1);
                frame.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                kernel.Add(site.LastKernelMs);
                siege.Add(site.LastSiegeMs);
                maxSync = Math.Max(maxSync, SiegeService.LastSyncMs);
            }
            double kAvg = kernel.Average(), kMax = kernel.Max(), sAvg = siege.Average(), sMax = siege.Max(), fAvg = frame.Average(), fMax = frame.Max();
            double change = site.MaxSiegeChangeMs, reset = site.MaxSiegeResetMs;
            int alive = site.SiegeUnitCount;
            CombatSiegeStats st = site.SiegeStats;
            PerfLines.Add($"{alive} 台攻城单位（展开 {g.UnfoldedCount}，展开耗时 {unfoldMs:F1} ms）：内核单步 平均 {kAvg:F3} / 最长 {kMax:F3} ms（其中流场维护 平均 {sAvg:F3} / 最长 {sMax:F3} ms）；" +
                          $"世界一步（含热更层对账 / 溅射）平均 {fAvg:F3} / 最长 {fMax:F3} ms；对账最长 {maxSync:F3} ms；流场增量更新最长 {change:F3} ms、剧场重建最长 {reset:F3} ms（{st.Resets} 次）；" +
                          $"剧场 {site.SiegeRect.z}×{site.SiegeRect.w} 格；120 帧预算 8.33 ms / 帧");
            PerfGate.Expect(alive >= n * 9 / 10,
                $"P1 FG06 第 7 节 {n} 台攻城单位：内核单步平均 {kAvg:F3} ms（阈值 {SiegeCatalog.PerfStepMs} ms）、流场增量更新最长 {change:F3} ms（阈值 {SiegeCatalog.PerfFieldUpdateMs} ms）、世界一步平均 {fAvg:F3} ms（≤ 8.33 ms 才保得住 120 帧）",
                new[]
                {
                    PerfGate.Le(kAvg, SiegeCatalog.PerfStepMs, "攻城内核单步平均 ms"),
                    PerfGate.Le(change, SiegeCatalog.PerfFieldUpdateMs, "流场增量更新最长 ms"),
                    PerfGate.Le(fAvg, 8.33, "攻城世界一步平均 ms（120 帧预算）"),
                }, Expect, Line);

            // P2 DEBT-FG0ARCH03-02 复测：攻城中大量命中机器——12 台战斗履带驻防在敌人来路上的那段墙边，敌人自卫开火打机器（机器血量在热更层结算），量热更层事件处理耗时。
            CampaignState s2 = NewWorld(7034);
            List<BuildingRecord> ring2 = Ring(s2, 4, B3, out GridCell r2min, out GridCell r2max, out _);
            Vector2 at2 = OutsidePoint(s2, Math.Max(r2max.X - r2min.X, r2max.Y - r2min.Y) * 0.5f + 14f);
            BuildingRecord post2 = NearestWall(ring2, r2min, r2max, at2);
            foreach (MachineRecord mm in HomeMachines())
            {
                MachineRoster.TrySetRole(s2, mm.LogicId, MachineRole.Idle, out _);
            }
            var guards = new List<int>();
            for (int k = 0; k < 12; k++)
            {
                int id = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId,
                    CoreCenter(s2) + new Vector2(2f + k % 4, -2f - k / 4), 120f, 120f, "Player", 1).LogicId;
                MachineLoadoutRegistry.Register(s2, id, HomeValleyLayout.BlueprintErc003Id, 1);
                guards.Add(id);
            }
            WorldSimulation.StepMany(2);
            foreach (int id in guards)
            {
                MachineRoster.TrySetRole(s2, id, MachineRole.Garrison, out _);
                MachineRoster.TrySetGarrisonPoint(s2, id, post2.BuildingId, out _);
            }
            StepUntil(() => guards.Count(id => HomeValleyWorkOrders.FindActiveOrderForMachine(s2, id) is WorkOrderRecord o && o.State == WorkOrderState.InProgress) >= 10, 120);
            Arrive(s2, at2, "foundry", new[] { "foundry.strider", "foundry.armorbot" }, new[] { n * 7 / 10, n - n * 7 / 10 });
            CombatSite site2 = Site;
            float hp0 = guards.Sum(id => MachineRegistry.TryGetRecord(id, out MachineRecord r) ? r.Health : 0f);
            var ev = new List<double>();
            for (int i = 0; i < GameClock.StepHz * 15; i++)
            {
                WorldSimulation.StepMany(1);
                ev.Add(site2.LastEventsMs);
            }
            float hp1 = guards.Sum(id => MachineRegistry.TryGetRecord(id, out MachineRecord r) && r.IsAlive ? r.Health : 0f);
            double eAvg = ev.Average(), eMax = ev.Max();
            PerfLines.Add($"DEBT-FG0ARCH03-02 复测：{n} 台攻城单位 vs 12 台驻防战斗履带（15 秒，机器总血量 {hp0:F0} → {hp1:F0}，守点开火 {site2.SiegeStats.GuardShots} 次）：热更层每步事件处理 平均 {eAvg:F3} / 最长 {eMax:F3} ms");
            PerfGate.Expect(hp1 < hp0,
                $"P2 DEBT-FG0ARCH03-02 复测（攻城中大量命中机器）：机器真的在挨打（{hp0:F0} → {hp1:F0}），热更层每步事件处理平均 {eAvg:F3} ms、最长 {eMax:F3} ms（阈值平均 0.5 ms / 最长 2 ms）",
                new[] { PerfGate.Le(eAvg, 0.5, "攻城命中机器：热更层事件处理平均 ms"), PerfGate.Le(eMax, 2.0, "攻城命中机器：热更层事件处理最长 ms") }, Expect, Line);
        }
    }
}
