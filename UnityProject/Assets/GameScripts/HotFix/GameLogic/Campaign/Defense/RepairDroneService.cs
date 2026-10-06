using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>FG6-DEF-03：一座维修无人机站给面板 / 状态行 / 自检看的读数（同一份数据，界面不另算）。</summary>
    public struct RepairStationReadout
    {
        public string BuildingId;
        public string Name;
        public bool Built;
        public bool Active;
        public int Drones;
        public int Complement;
        public int Out;
        public int Repairing;
        public bool NoKits;
        public bool Respawning;
        public float RespawnSecondsLeft;
        public bool RespawnShortScrap;
        public float Range;
        public float KitCredit;
        public double Repaired;
        public int KitsUsed;
        public int Lost;
        public bool RaidPriority;
        /// <summary>正在修 / 正飞去修的目标（玩家名，按无人机顺序去重）。</summary>
        public List<string> Targets;
    }

    /// <summary>
    /// FG6-DEF-03（FG06 FGR-DEF-014“维修无人机站”；FG06 第 5 节负向“维修无人机站被毁”）：维修无人机的唯一业务入口。存档真相在 <see cref="DefenseState.Stations"/>。
    /// <para>站里常备 <see cref="RepairDroneCatalog.Count"/> 架无人机。站点运转且有电时，每 drone.scan_seconds 找一次目标：范围（drone.range，以站中心为圆心）内耐久没满的
    /// 建筑 / 炮塔 / 屏障 / 护盾 / 陷阱（目标中心在圈内；有机器维修单的让给机器，不重复花维修件）与受损的传送带格。排序：突袭进行中最近 drone.attacked_window_seconds 秒挨过打的在最前
    /// （FGR-DEF-014“优先修理正在受攻击的目标”；突袭中正修着不挨打目标的无人机会被调去修挨打的），其余按耐久比例低的先修、近的先修、ID 定序。一架无人机一个目标（不两架抢一处）。</para>
    /// <para>无人机直线飞过去（drone.speed），到目标边缘 drone.reach 内开始修（drone.repair_per_second），按修好的耐久比例从共用库存取维修件
    /// （修满一座 = 它在建筑通用表里的维修件数，与机器维修同价；传送带一格 = drone.belt_repair_kits），站里记零头余额，不丢不多扣。仓库没有维修件时无人机回站、写明原因、发一次警告。
    /// 修满或目标没了就返航（下一次找目标时可以直接换目标）；回到站里停靠、耐久补满。缺电 / 禁用：出动的无人机返航，不出动、不补充。</para>
    /// <para>出动的无人机是战斗内核里的己方单位（<see cref="CombatSite.SpawnDroneUnit"/>）：敌人的弹体打得中它，阵亡 = 被击落（拿掉、警告通知、失败提示音），
    /// 站点花 drone.respawn_seconds 秒（只在运转且有电时计时）、drone.respawn_scrap 废料补一架（开始补充时扣，不够就等并写明）。站点被摧毁：全部无人机坠毁（警告通知），
    /// 正在补充的那一架的废料全额退回；站点重建完工后无人机满编回来（含在重建造价里）。被摧毁的建筑只剩虚影——无人机不修虚影，重建交给机器 / 常驻规则“自动重建”。</para>
    /// <para>只在世界模拟步里推进（与镜头 / 观察无关，FGR-BASE-021）；每步 O(出动的无人机)，每次找目标 O(建筑数 + 受损传送带格数 + 站数 × 候选数)。不做玩家没要求的事（FGR-BASE-020）：
    /// 站点只修自己范围里的东西，玩家禁用 / 拆除站点立即停止。</para>
    /// </summary>
    public static partial class RepairDroneService
    {
        public const int StateDocked = 0;
        public const int StateOutbound = 1;
        public const int StateRepairing = 2;
        public const int StateReturning = 3;

        /// <summary>界面刷新用：任何站点 / 无人机状态变化 +1。</summary>
        public static int Revision { get; private set; } = 1;

        private static void Touch() => Revision++;

        // ── 自检读（本会话）──
        public static int ScanCount { get; private set; }
        public static int AssignCount { get; private set; }
        public static int LastCandidateCount { get; private set; }
        public static bool LastScanRaid { get; private set; }

        private static bool _builtHook;
        private static bool _repairHook;

        /// <summary>新会话（新建 / 读档 / 回主菜单）：运行时缓存清空（存档数据不动）。</summary>
        public static void ResetSessionState()
        {
            Candidates.Clear();
            Claims.Clear();
            KeyCache.Clear();
            TargetIdCache.Clear();
            ScanCount = 0;
            AssignCount = 0;
            LastCandidateCount = 0;
            LastScanRaid = false;
            _builtHook = false;
            _repairHook = false;
            Touch();
        }

        private static CombatSite HomeSite => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        // ─────────────────────────────── 存档域 ───────────────────────────────

        public static DefenseState StateOf(CampaignState s) => DefenseService.StateOf(s);

        public static IReadOnlyList<RepairStationRecord> All(CampaignState s) => StateOf(s)?.Stations ?? Array.Empty<RepairStationRecord>();

        /// <summary>读档 / 新档补全（<see cref="CampaignFgStateDomains.EnsureAll"/> 调）：数组补空、重复建筑只留第一条、坏值钳回、无人机序号重复的重编、序号计数补齐。</summary>
        public static void EnsureState(CampaignState s)
        {
            CombatSite.DroneEvent ??= OnKernelEvent;
            DefenseState st = StateOf(s);
            if (st == null)
            {
                return;
            }
            st.Stations ??= Array.Empty<RepairStationRecord>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenDrones = new HashSet<int>();
            var keep = new List<RepairStationRecord>(st.Stations.Length);
            int maxSerial = 0;
            float hpMax = RepairDroneCatalog.Hp;
            foreach (RepairStationRecord r in st.Stations)
            {
                if (r == null || string.IsNullOrEmpty(r.BuildingId) || !seenIds.Add(r.BuildingId))
                {
                    continue;
                }
                r.Drones ??= Array.Empty<DroneRecord>();
                var drones = new List<DroneRecord>(r.Drones.Length);
                foreach (DroneRecord d in r.Drones)
                {
                    if (d == null)
                    {
                        continue;
                    }
                    d.Target ??= string.Empty;
                    if (d.State < StateDocked || d.State > StateReturning)
                    {
                        d.State = StateDocked;
                    }
                    if (float.IsNaN(d.Hp) || d.Hp <= 0f || d.Hp > hpMax)
                    {
                        d.Hp = hpMax;
                    }
                    if (float.IsNaN(d.X) || float.IsNaN(d.Y))
                    {
                        d.X = 0f;
                        d.Y = 0f;
                        d.State = StateDocked;
                    }
                    if (float.IsNaN(d.Work) || d.Work < 0f)
                    {
                        d.Work = 0f;
                    }
                    if (d.Serial <= 0 || !seenDrones.Add(d.Serial))
                    {
                        d.Serial = 0;
                    }
                    maxSerial = Math.Max(maxSerial, d.Serial);
                    drones.Add(d);
                }
                if (drones.Count != r.Drones.Length)
                {
                    r.Drones = drones.ToArray();
                }
                if (float.IsNaN(r.KitCredit) || r.KitCredit < 0f || r.KitCredit > 1f)
                {
                    r.KitCredit = Mathf.Clamp01(float.IsNaN(r.KitCredit) ? 0f : r.KitCredit);
                }
                if (r.RespawnTicks < -1)
                {
                    r.RespawnTicks = -1;
                }
                r.RespawnPaid = Math.Max(0, r.RespawnPaid);
                r.Lost = Math.Max(0, r.Lost);
                r.KitsUsed = Math.Max(0, r.KitsUsed);
                if (double.IsNaN(r.Repaired) || r.Repaired < 0)
                {
                    r.Repaired = 0;
                }
                keep.Add(r);
            }
            if (st.NextDroneSerial <= maxSerial)
            {
                st.NextDroneSerial = maxSerial + 1;
            }
            if (st.NextDroneSerial < 1)
            {
                st.NextDroneSerial = 1;
            }
            foreach (RepairStationRecord r in keep)
            {
                foreach (DroneRecord d in r.Drones)
                {
                    if (d.Serial <= 0)
                    {
                        d.Serial = st.NextDroneSerial++;
                    }
                }
            }
            if (keep.Count != st.Stations.Length)
            {
                st.Stations = keep.ToArray();
            }
            st.TotalKitsUsed = Math.Max(0, st.TotalKitsUsed);
            st.TotalDronesLost = Math.Max(0, st.TotalDronesLost);
            if (double.IsNaN(st.TotalRepaired) || st.TotalRepaired < 0)
            {
                st.TotalRepaired = 0;
            }
        }

        public static RepairStationRecord Find(CampaignState s, string buildingId)
        {
            if (s == null || string.IsNullOrEmpty(buildingId))
            {
                return null;
            }
            foreach (RepairStationRecord r in All(s))
            {
                if (r != null && r.BuildingId == buildingId)
                {
                    return r;
                }
            }
            return null;
        }

        public static DroneRecord FindDrone(CampaignState s, int serial, out RepairStationRecord station)
        {
            station = null;
            foreach (RepairStationRecord r in All(s))
            {
                foreach (DroneRecord d in r.Drones)
                {
                    if (d.Serial == serial)
                    {
                        station = r;
                        return d;
                    }
                }
            }
            return null;
        }

        public static bool IsStation(BuildingRecord b) => b != null && RepairDroneCatalog.IsStationType(b.BuildingTypeId) && !HomeGridService.IsRelocationGhost(b);

        private static bool IsBuilt(BuildingRecord b) =>
            b != null && (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled)
                      && !HomeGridService.IsRelocationGhost(b);

        private static bool IsActive(BuildingRecord b) =>
            b != null && b.ConstructionState == BuildingConstructionState.Operational && b.PowerState == BuildingPowerState.Powered && !HomeGridService.IsRelocationGhost(b);

        // ─────────────────────────────── 世界模拟步 ───────────────────────────────

        /// <summary>
        /// 世界模拟的每个固定步（家园载入时，<see cref="WorldSimulation"/> 在家园内核一步与防御对账之后调）：每 drone.scan_seconds 对账记录 + 找目标；
        /// 每步推进出动的无人机（飞行 / 修理 / 返航）与补充计时。只看步序号，与观察无关；暂停不走；倍速按步。
        /// </summary>
        public static void WorldStep(CampaignState state, long ticksBefore, int worldHz)
        {
            if (state == null || worldHz <= 0)
            {
                return;
            }
            CombatSite.DroneEvent ??= OnKernelEvent;
            long every = Math.Max(1, (long)Math.Round(RepairDroneCatalog.ScanSeconds * worldHz));
            bool scan = ticksBefore % every == 0;
            if (scan)
            {
                Sync(state);
            }
            DefenseState st = StateOf(state);
            if (st == null || st.Stations.Length == 0)
            {
                return;
            }
            CombatSite site = HomeSite;
            if (site != null && site.IsDisposed)
            {
                site = null;
            }
            if (scan)
            {
                ScanCount++;
                BuildCandidates(state);
                BuildClaims(st);
            }
            float dt = 1f / worldHz;
            RepairStationRecord[] stations = st.Stations;
            for (int i = 0; i < stations.Length; i++)
            {
                StepStation(state, site, stations[i], scan, dt, worldHz);
            }
        }

        /// <summary>
        /// 对账（O(建筑数 + 站数 × 编制)）：记录 ↔ 站点建筑（新站补记录，等建成那一刻满编；拆掉的站拿掉记录与内核单位，不算击落）；内核里有、记录里没有的无人机单位拿掉；
        /// 记录说出动着、内核里却没有的（旧快照 / 家园刚载入）按记录的位置与耐久放回内核。
        /// </summary>
        public static void Sync(CampaignState state)
        {
            DefenseState st = StateOf(state);
            if (st == null)
            {
                return;
            }
            CombatSite site = HomeSite;
            if (site != null && site.IsDisposed)
            {
                site = null;
            }
            bool changed = false;
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (IsStation(b) && Find(state, b.BuildingId) == null)
                {
                    var rec = new RepairStationRecord { BuildingId = b.BuildingId, Wrecked = true };
                    var list = new List<RepairStationRecord>(st.Stations) { rec };
                    st.Stations = list.ToArray();
                    changed = true;
                }
            }
            bool anyGone = false;
            foreach (RepairStationRecord r in st.Stations)
            {
                if (!IsStation(HomeGridService.FindBuilding(state, r.BuildingId)))
                {
                    anyGone = true;
                    break;
                }
            }
            if (anyGone)
            {
                var keep = new List<RepairStationRecord>(st.Stations.Length);
                foreach (RepairStationRecord r in st.Stations)
                {
                    if (IsStation(HomeGridService.FindBuilding(state, r.BuildingId)))
                    {
                        keep.Add(r);
                        continue;
                    }
                    // 站点被拆除：无人机随站回收（不算击落）；正在补充的那一架的废料全额退回。
                    foreach (DroneRecord d in r.Drones)
                    {
                        site?.RemoveDroneUnit(d.Serial);
                    }
                    RefundRespawn(state, r);
                }
                st.Stations = keep.ToArray();
                changed = true;
            }
            if (site != null)
            {
                if (site.DroneUnitCount > 0)
                {
                    var known = new HashSet<int>();
                    foreach (RepairStationRecord r in st.Stations)
                    {
                        foreach (DroneRecord d in r.Drones)
                        {
                            if (d.State != StateDocked)
                            {
                                known.Add(d.Serial);
                            }
                        }
                    }
                    foreach (int serial in site.DroneSerials())
                    {
                        if (!known.Contains(serial))
                        {
                            site.RemoveDroneUnit(serial);
                        }
                    }
                }
                foreach (RepairStationRecord r in st.Stations)
                {
                    foreach (DroneRecord d in r.Drones)
                    {
                        if (d.State != StateDocked && !site.TryGetDroneUnit(d.Serial, out _))
                        {
                            SpawnUnit(site, d);
                        }
                    }
                }
            }
            if (changed)
            {
                Touch();
            }
        }

        private static void StepStation(CampaignState state, CombatSite site, RepairStationRecord rec, bool scan, float dt, int hz)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, rec.BuildingId);
            if (b == null || !IsStation(b))
            {
                return; // 下一次对账清掉
            }
            if (!IsBuilt(b))
            {
                if (b.ConstructionState == BuildingConstructionState.Damaged && !rec.Wrecked)
                {
                    Wreck(state, site, rec, b); // 兜底：摧毁入口之外的路径变成虚影（正常流程由 OnBuildingDestroyed 当场结算）
                }
                return;
            }
            if (rec.Wrecked)
            {
                Refill(state, rec, b);
            }
            bool active = IsActive(b);
            DroneRecord[] drones = rec.Drones;
            for (int i = 0; i < drones.Length; i++)
            {
                StepDrone(state, site, rec, b, drones[i], active, dt);
            }
            Respawn(state, rec, b, active, hz);
            if (scan && active)
            {
                // 玩家给正在修的目标派了机器维修单（维修件已预留）：无人机让给机器，返航（不重复花维修件）。每次找目标看一次工单（每步不扫）。
                foreach (DroneRecord d in rec.Drones)
                {
                    if ((d.State == StateOutbound || d.State == StateRepairing) && !TryTarget(state, d.Target, out _, checkOrders: true))
                    {
                        Claims.Remove(d.Target);
                        BeginReturn(d);
                        Touch();
                    }
                }
                Assign(state, site, rec, b, dt);
            }
        }

        /// <summary>新站建成 / 被摧毁的站重建完工：按编制满编（停在站里）。</summary>
        private static void Refill(CampaignState state, RepairStationRecord rec, BuildingRecord b)
        {
            DefenseState st = StateOf(state);
            int n = RepairDroneCatalog.Count;
            var list = new List<DroneRecord>(n);
            for (int i = 0; i < n; i++)
            {
                list.Add(new DroneRecord { Serial = st.NextDroneSerial++, State = StateDocked, X = b.Position.x, Y = b.Position.y, Hp = RepairDroneCatalog.Hp });
            }
            rec.Drones = list.ToArray();
            rec.Wrecked = false;
            rec.RespawnTicks = -1;
            rec.RespawnPaid = 0;
            rec.NoKits = false;
            if (!_builtHook)
            {
                _builtHook = true;
                GuidanceHooks.Raise(GuidanceHooks.DefenseRepairDroneFirstBuilt);
            }
            Touch();
        }

        // ─────────────────────────────── 无人机 ───────────────────────────────

        private static void StepDrone(CampaignState state, CombatSite site, RepairStationRecord rec, BuildingRecord station, DroneRecord d, bool active, float dt)
        {
            switch (d.State)
            {
                case StateDocked:
                    return;
                case StateOutbound:
                case StateRepairing:
                {
                    if (!active || !TryTarget(state, d.Target, out TargetInfo t, checkOrders: false) || !t.Worn)
                    {
                        BeginReturn(d);
                        Touch();
                        return;
                    }
                    if (d.State == StateOutbound)
                    {
                        if (MoveToward(d, t.Pos, t.Radius + RepairDroneCatalog.Reach, dt))
                        {
                            d.State = StateRepairing;
                            Touch();
                        }
                        site?.SetDronePosition(d.Serial, new Vector2(d.X, d.Y));
                        return;
                    }
                    RepairStep(state, rec, station, d, t, dt);
                    return;
                }
                case StateReturning:
                    if (MoveToward(d, station.Position, 0.05f, dt))
                    {
                        Dock(site, d, station);
                        Touch();
                        return;
                    }
                    site?.SetDronePosition(d.Serial, new Vector2(d.X, d.Y));
                    return;
            }
        }

        /// <summary>直线飞向 <paramref name="to"/>，停在离它 <paramref name="stop"/> 米处。返回是否已经到了。确定性（只看位置、速度、步长）。</summary>
        private static bool MoveToward(DroneRecord d, Vector2 to, float stop, float dt)
        {
            float dx = to.x - d.X;
            float dy = to.y - d.Y;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            if (dist <= stop + 1e-4f)
            {
                return true;
            }
            float step = Mathf.Min(RepairDroneCatalog.Speed * dt, dist - stop);
            d.X += dx / dist * step;
            d.Y += dy / dist * step;
            return dist - step <= stop + 1e-4f;
        }

        private static void BeginReturn(DroneRecord d)
        {
            d.State = StateReturning;
            d.Target = string.Empty;
            d.Work = 0f;
        }

        private static void Dock(CombatSite site, DroneRecord d, BuildingRecord station)
        {
            site?.RemoveDroneUnit(d.Serial);
            d.State = StateDocked;
            d.Target = string.Empty;
            d.Work = 0f;
            d.X = station.Position.x;
            d.Y = station.Position.y;
            d.Hp = RepairDroneCatalog.Hp; // 停靠时在站里修好
        }

        private static void SpawnUnit(CombatSite site, DroneRecord d)
        {
            if (site == null)
            {
                return;
            }
            int unit = site.SpawnDroneUnit(d.Serial, new Vector2(d.X, d.Y), RepairDroneCatalog.Radius, Mathf.Max(0.01f, d.Hp), RepairDroneCatalog.Hp);
            if (unit > 0)
            {
                site.SetUnitLabel(unit, "drone.label", null, null, false);
            }
        }

        private static void Launch(CombatSite site, DroneRecord d, BuildingRecord station)
        {
            d.X = station.Position.x;
            d.Y = station.Position.y;
            d.Hp = d.Hp > 0f ? d.Hp : RepairDroneCatalog.Hp;
            d.State = StateOutbound;
            SpawnUnit(site, d);
        }

        private static void RepairStep(CampaignState state, RepairStationRecord rec, BuildingRecord station, DroneRecord d, TargetInfo t, float dt)
        {
            float missing = t.IsBelt || t.IsPipe ? Mathf.Max(0f, t.Missing - d.Work) : t.Missing;
            float amount = Mathf.Min(RepairDroneCatalog.RepairPerSecond * dt, missing);
            if (amount <= 0f)
            {
                BeginReturn(d);
                Touch();
                return;
            }
            float cost = t.KitsPerFull > 0f ? amount * t.KitsPerFull / Mathf.Max(1f, t.MaxHp) : 0f;
            if (!PayKits(state, rec, station, cost))
            {
                BeginReturn(d); // 仓库没有维修件：回站（原因写在站点状态、发一次警告）
                Touch();
                return;
            }
            float done = ApplyRepair(state, t, d, amount);
            rec.Repaired += done;
            StateOf(state).TotalRepaired += done;
            if (!_repairHook && done > 0f)
            {
                _repairHook = true;
                GuidanceHooks.Raise(GuidanceHooks.DefenseRepairDroneFirstRepair);
            }
            // 修满了没有：按这一步修好的量算（不再查一遍目标，热更层每步每架只查一次）。
            bool full = t.IsBelt ? BeltNetworkService.DamageOf(t.Cell) <= 0 : t.IsPipe ? PipeNetworkService.DamageOf(t.Cell) <= 0 : t.Missing - done <= 0.01f;
            if (full)
            {
                BeginReturn(d); // 修满：返航（下一次找目标时可以直接换目标）
                Touch();
            }
        }

        /// <summary>按修好的耐久比例记维修件：余额不够时从共用库存取 1 件补进余额（记消耗统计）；仓库也没有 = 失败（标“缺维修件”）。</summary>
        private static bool PayKits(CampaignState state, RepairStationRecord rec, BuildingRecord station, float cost)
        {
            if (cost <= 0f)
            {
                return true;
            }
            while (rec.KitCredit + 1e-6f < cost)
            {
                ItemDef kit = ItemCatalog.Find(BuildingOps.RepairKitId);
                if (kit == null || !HomeInventory.TryRemove(state, kit, 1))
                {
                    SetNoKits(state, rec, station, true);
                    return false;
                }
                rec.KitCredit += 1f;
                rec.KitsUsed++;
                StateOf(state).TotalKitsUsed++;
                ProductionStats.RecordUnits(state, kit, 1, produced: false); // FG4-ECO-08：维修件消耗进统计
            }
            rec.KitCredit = Mathf.Max(0f, rec.KitCredit - cost);
            SetNoKits(state, rec, station, false);
            return true;
        }

        /// <summary>
        /// FG6-DEF-03 复审修复（P1“缺维修件时停工”）：仓库没有维修件时，站里的零头余额够不够这个目标修一步。
        /// 比 <see cref="PayKits"/> 的判定多留 1e-5 的余量（两处浮点算序不同），保证派出去的那一步一定扣得到——余额不够一步的目标不派，
        /// 维修件中途用完的无人机返航停靠后不会被再派出去来回空飞。仓库有维修件时恒为 true。
        /// </summary>
        private static bool CanAffordStep(int stock, RepairStationRecord rec, Candidate c, float dt)
        {
            if (stock > 0)
            {
                return true;
            }
            float cost = c.KitsPerHp * Mathf.Min(RepairDroneCatalog.RepairPerSecond * dt, c.Missing);
            return cost <= 0f || rec.KitCredit >= cost + 1e-5f;
        }

        private static void SetNoKits(CampaignState state, RepairStationRecord rec, BuildingRecord station, bool noKits)
        {
            if (rec.NoKits == noKits)
            {
                return;
            }
            rec.NoKits = noKits;
            Touch();
            if (!noKits || station == null)
            {
                return;
            }
            string text = GameText.Format("drone.notify.kits", BuildingOps.NameOf(station));
            if (GameClock.Ticks >= rec.NotifyTick)
            {
                rec.NotifyTick = GameClock.TickAfter(RepairDroneCatalog.NotifyCooldownSeconds);
                NotificationCenter.Post("drone_kits", text, new Vector3(station.Position.x, 0f, station.Position.y));
                Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.Failure, station.Position, text);
            }
        }

        // ─────────────────────────────── 目标 ───────────────────────────────

        private struct TargetInfo
        {
            public bool IsBelt;
            /// <summary>FG6-DEF-05（承接 DEBT-FG6DEF03-02）：管线件（键 "p:x,y"）。</summary>
            public bool IsPipe;
            public BuildingRecord B;
            public GridCell Cell;
            public Vector2 Pos;
            public float Radius;
            public float Hp;
            public float MaxHp;
            public float KitsPerFull;
            public float Missing => Mathf.Max(0f, MaxHp - Hp);
            public bool Worn => MaxHp - Hp > 0.01f;
        }

        /// <summary>建筑此刻的耐久（炮塔 / 防御建筑在内核里时读内核，其余读建筑记录）。</summary>
        public static float DurabilityOf(CampaignState state, BuildingRecord b)
        {
            if (b == null)
            {
                return 0f;
            }
            if (TurretService.IsTurret(b))
            {
                return TurretService.DurabilityOf(state, b);
            }
            if (DefenseService.IsDefense(b))
            {
                return DefenseService.DurabilityOf(state, b);
            }
            if (SiegeService.IsSiegeStructure(state, b))
            {
                return SiegeService.DurabilityOf(state, b); // FG6-DEF-05：攻城期间建筑在内核里
            }
            return BuildingOps.Durability(b);
        }

        /// <summary>
        /// FG6-DEF-08 复修（FGT-DEF-009）：建筑记录的耐久被别处直接改过之后调用——炮塔 / 防御建筑 / 攻城期间进了内核的建筑立即把记录推给内核（各自的 CommitRecord），
        /// 其余建筑没有内核单位，什么也不做。调用方：<see cref="BuildingOps.ApplyDamage"/>、机器维修工单完工（HomeValleyWorkOrders）。O(1)。
        /// </summary>
        public static void CommitRecordDurability(CampaignState state, BuildingRecord b)
        {
            if (state == null || b == null)
            {
                return;
            }
            if (TurretService.IsTurret(b))
            {
                TurretService.CommitRecord(state, b);
            }
            else if (DefenseService.IsDefense(b))
            {
                DefenseService.CommitRecord(state, b);
            }
            else if (SiegeService.IsSiegeStructure(state, b))
            {
                SiegeService.CommitRecord(state, b);
            }
        }

        /// <summary>目标键 → 建筑 ID（每步校验目标时不分配字符串）。</summary>
        private static readonly Dictionary<string, string> TargetIdCache = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <param name="checkOrders">找目标时 = true（有机器维修单的让给机器）；每步校验正在修的目标时 = false（只在找目标时看工单，热更层每步不扫工单）。</param>
        private static bool TryTarget(CampaignState state, string key, out TargetInfo t, bool checkOrders = true)
        {
            t = default;
            if (string.IsNullOrEmpty(key) || key.Length < 3)
            {
                return false;
            }
            if (key[0] == 'b')
            {
                if (!TargetIdCache.TryGetValue(key, out string id))
                {
                    id = key.Substring(2);
                    TargetIdCache[key] = id;
                }
                BuildingRecord b = HomeGridService.FindBuilding(state, id);
                return TryBuildingTarget(state, b, out t, checkOrders);
            }
            if (key[0] == 'c' || key[0] == 'p')
            {
                // 格坐标键（传送带 c: / 管线 p:）解析一次缓存起来：每步校验正在修的目标时不再切字符串（FG6-DEF-05 复测性能时顺带）。
                if (!CellKeyCache.TryGetValue(key, out GridCell cell))
                {
                    int comma = key.IndexOf(',');
                    if (comma < 0 || !int.TryParse(key.Substring(2, comma - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                        || !int.TryParse(key.Substring(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
                    {
                        return false;
                    }
                    cell = new GridCell(x, y);
                    if (CellKeyCache.Count > 4096)
                    {
                        CellKeyCache.Clear();
                    }
                    CellKeyCache[key] = cell;
                }
                return key[0] == 'c' ? TryBeltTarget(cell, out t) : TryPipeTarget(cell, out t);
            }
            return false;
        }

        private static readonly Dictionary<string, GridCell> CellKeyCache = new Dictionary<string, GridCell>(StringComparer.Ordinal);

        /// <summary>FG6-DEF-05（承接 DEBT-FG6DEF03-02“维修无人机可修管线”）：一格受损的管线件当维修目标（修满件数 = logistics.pipe.repair_kits）。</summary>
        private static bool TryPipeTarget(GridCell cell, out TargetInfo t)
        {
            t = default;
            int hp = PipeNetworkService.HpOf(cell);
            if (hp < 0)
            {
                return false;
            }
            t.IsPipe = true;
            t.Cell = cell;
            t.Pos = new Vector2(cell.X, cell.Y);
            t.Radius = 0.5f;
            t.Hp = hp;
            t.MaxHp = hp + PipeNetworkService.DamageOf(cell);
            t.KitsPerFull = PipeNetworkService.RepairKitsPerPiece;
            return true;
        }

        private static bool TryBuildingTarget(CampaignState state, BuildingRecord b, out TargetInfo t, bool checkOrders = true)
        {
            t = default;
            if (b == null || !IsBuilt(b))
            {
                return false; // 虚影 / 被摧毁：无人机不修虚影（重建交给机器 / 自动重建规则）
            }
            GameConfig.fg.BuildingService svc = BuildingOps.Service(b.BuildingTypeId);
            if (svc == null || svc.RepairKits <= 0)
            {
                return false;
            }
            if (checkOrders && HomeValleyWorkOrders.FindActiveRepair(state, b.BuildingId) != null)
            {
                return false; // 玩家派了机器维修（维修件已预留）：让给机器，不重复花维修件
            }
            GridContent.TryGetBuilding(b.BuildingTypeId, out GameConfig.fg.BuildingGrid g);
            t.B = b;
            t.Pos = b.Position;
            t.Radius = g != null ? Mathf.Min(g.FootprintW, g.FootprintH) * 0.5f : 0.5f;
            t.MaxHp = BuildingOps.MaxDurability(b.BuildingTypeId);
            t.Hp = DurabilityOf(state, b);
            t.KitsPerFull = svc.RepairKits;
            return true;
        }

        private static bool TryBeltTarget(GridCell cell, out TargetInfo t)
        {
            t = default;
            int hp = BeltNetworkService.HpOf(cell);
            if (hp < 0)
            {
                return false;
            }
            t.IsBelt = true;
            t.Cell = cell;
            t.Pos = new Vector2(cell.X, cell.Y);
            t.Radius = 0.5f;
            t.Hp = hp;
            t.MaxHp = hp + BeltNetworkService.DamageOf(cell);
            t.KitsPerFull = RepairDroneCatalog.BeltRepairKits;
            return true;
        }

        private static float ApplyRepair(CampaignState state, TargetInfo t, DroneRecord d, float amount)
        {
            if (t.IsBelt || t.IsPipe)
            {
                d.Work += amount;
                int pts = Mathf.FloorToInt(d.Work + 1e-4f);
                if (pts > 0)
                {
                    int fixedPts = t.IsPipe ? PipeNetworkService.TryRepair(state, t.Cell, pts) : BeltNetworkService.TryRepair(state, t.Cell, pts);
                    d.Work = fixedPts < pts ? 0f : Mathf.Max(0f, d.Work - pts);
                }
                return amount;
            }
            BuildingRecord b = t.B;
            float done;
            if (TurretService.IsTurret(b))
            {
                done = TurretService.Heal(state, b, amount);
            }
            else if (DefenseService.IsDefense(b))
            {
                done = DefenseService.Heal(state, b, amount);
            }
            else if (SiegeService.IsSiegeStructure(state, b))
            {
                done = SiegeService.Heal(state, b, amount); // FG6-DEF-05：攻城期间建筑在内核里，直接修内核耐久
            }
            else
            {
                float before = BuildingOps.Durability(b);
                float after = Mathf.Min(t.MaxHp, before + amount);
                b.Health = after;
                done = after - before;
            }
            BuildingVisualFeed.Mark(b);
            // 复审修复（P2 每步 Touch）：修理过程中只在跨过 10% 耐久档或修满时 +1 建筑修订号（规则面板 / 仓库警报按它重建），不再每步 +1。
            float max = Mathf.Max(1f, t.MaxHp);
            if (done > 0f && (Mathf.FloorToInt(t.Hp / max * 10f) != Mathf.FloorToInt((t.Hp + done) / max * 10f) || t.Missing - done <= 0.01f))
            {
                BuildingOps.Touch();
            }
            return done;
        }

        // ─────────────────────────────── 找目标 ───────────────────────────────

        private struct Candidate
        {
            public string Key;
            public Vector2 Pos;
            public float Frac;
            public bool Attacked;
            /// <summary>修 1 点耐久要的维修件（= 修满件数 / 最大耐久），与 <see cref="RepairStep"/> 的扣件口径一致。</summary>
            public float KitsPerHp;
            public float Missing;
        }

        private static readonly List<Candidate> Candidates = new List<Candidate>(64);
        private static readonly HashSet<string> Claims = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> KeyCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly List<Candidate> InRange = new List<Candidate>(16);
        private static readonly List<GridCell> BeltScratch = new List<GridCell>(16);

        private static string KeyOf(BuildingRecord b)
        {
            if (!KeyCache.TryGetValue(b.BuildingId, out string k))
            {
                k = "b:" + b.BuildingId;
                KeyCache[b.BuildingId] = k;
            }
            return k;
        }

        public static string BeltKey(GridCell c) => "c:" + c.X.ToString(CultureInfo.InvariantCulture) + "," + c.Y.ToString(CultureInfo.InvariantCulture);

        public static string PipeKey(GridCell c) => "p:" + c.X.ToString(CultureInfo.InvariantCulture) + "," + c.Y.ToString(CultureInfo.InvariantCulture);

        private static readonly List<GridCell> PipeScratch = new List<GridCell>(16);

        /// <summary>全家园受损、可以由无人机修的目标（每次找目标算一次，各站按范围筛）。突袭进行中标出最近挨过打的。</summary>
        private static void BuildCandidates(CampaignState state)
        {
            Candidates.Clear();
            bool raid = StandingRuleService.RaidActive(state);
            LastScanRaid = raid;
            long window = Math.Max(1, (long)Math.Round(RepairDroneCatalog.AttackedWindowSeconds * Math.Max(1, GameClock.StepHz)));
            long now = GameClock.Ticks;
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || !IsBuilt(b) || !TryBuildingTarget(state, b, out TargetInfo t) || !t.Worn)
                {
                    continue;
                }
                Candidates.Add(new Candidate
                {
                    Key = KeyOf(b),
                    Pos = t.Pos,
                    Frac = t.Hp / Mathf.Max(1f, t.MaxHp),
                    Attacked = raid && b.LastHitTick > 0 && now - b.LastHitTick <= window,
                    KitsPerHp = t.KitsPerFull / Mathf.Max(1f, t.MaxHp),
                    Missing = t.Missing,
                });
            }
            BeltNetworkService.DamagedCells(state, BeltScratch);
            foreach (GridCell c in BeltScratch)
            {
                if (TryBeltTarget(c, out TargetInfo t) && t.Worn)
                {
                    long hit = BeltNetworkService.LastHitOf(c);
                    Candidates.Add(new Candidate
                    {
                        Key = BeltKey(c),
                        Pos = t.Pos,
                        Frac = t.Hp / Mathf.Max(1f, t.MaxHp),
                        // FG6-DEF-05（承接 DEBT-FG6DEF03-03）：传送带记“最近挨打”，突袭中与建筑同样优先修。
                        Attacked = raid && hit > 0 && now - hit <= window,
                        KitsPerHp = t.KitsPerFull / Mathf.Max(1f, t.MaxHp),
                        Missing = t.Missing,
                    });
                }
            }
            PipeNetworkService.DamagedCells(state, PipeScratch);
            foreach (GridCell c in PipeScratch)
            {
                if (TryPipeTarget(c, out TargetInfo t) && t.Worn)
                {
                    long hit = PipeNetworkService.LastHitOf(c);
                    Candidates.Add(new Candidate
                    {
                        Key = PipeKey(c),
                        Pos = t.Pos,
                        Frac = t.Hp / Mathf.Max(1f, t.MaxHp),
                        Attacked = raid && hit > 0 && now - hit <= window,
                        KitsPerHp = t.KitsPerFull / Mathf.Max(1f, t.MaxHp),
                        Missing = t.Missing,
                    });
                }
            }
            LastCandidateCount = Candidates.Count;
        }

        private static void BuildClaims(DefenseState st)
        {
            Claims.Clear();
            foreach (RepairStationRecord r in st.Stations)
            {
                foreach (DroneRecord d in r.Drones)
                {
                    if (!string.IsNullOrEmpty(d.Target))
                    {
                        Claims.Add(d.Target);
                    }
                }
            }
        }

        private static int CompareCandidates(Candidate a, Candidate b, Vector2 from)
        {
            if (a.Attacked != b.Attacked)
            {
                return a.Attacked ? -1 : 1;
            }
            int f = a.Frac.CompareTo(b.Frac);
            if (f != 0)
            {
                return f;
            }
            int dd = (a.Pos - from).sqrMagnitude.CompareTo((b.Pos - from).sqrMagnitude);
            return dd != 0 ? dd : string.CompareOrdinal(a.Key, b.Key);
        }

        private static bool IsAttackedKey(string key)
        {
            foreach (Candidate c in Candidates)
            {
                if (c.Key == key)
                {
                    return c.Attacked;
                }
            }
            return false;
        }

        /// <summary>给停着 / 返航中的无人机派目标；突袭中把修着不挨打目标的无人机调去修挨打的（FGR-DEF-014）。</summary>
        private static void Assign(CampaignState state, CombatSite site, RepairStationRecord rec, BuildingRecord station, float dt)
        {
            int stock = HomeInventory.Stock(state, BuildingOps.RepairKitId);
            if (rec.NoKits && stock > 0)
            {
                SetNoKits(state, rec, station, false); // 复审修复：放回维修件后下一次找目标就撤掉“缺维修件”（不等真扣到件，范围里没目标也撤）
            }
            InRange.Clear();
            Vector2 at = station.Position;
            float range2 = RepairDroneCatalog.Range * RepairDroneCatalog.Range;
            bool unaffordable = false;
            foreach (Candidate c in Candidates)
            {
                if ((c.Pos - at).sqrMagnitude > range2 || Claims.Contains(c.Key))
                {
                    continue;
                }
                if (!CanAffordStep(stock, rec, c, dt))
                {
                    unaffordable = true; // 仓库没有维修件、零头余额也不够修这个目标一步：不派（停工，不来回空飞）
                    continue;
                }
                InRange.Add(c);
            }
            if (InRange.Count == 0)
            {
                if (unaffordable)
                {
                    SetNoKits(state, rec, station, true);
                }
                return;
            }
            InRange.Sort((a, b) => CompareCandidates(a, b, at));
            int next = 0;
            foreach (DroneRecord d in rec.Drones)
            {
                if (next >= InRange.Count)
                {
                    break;
                }
                if ((d.State != StateDocked && d.State != StateReturning) || !string.IsNullOrEmpty(d.Target))
                {
                    continue;
                }
                Candidate c = InRange[next++];
                d.Target = c.Key;
                Claims.Add(c.Key);
                if (d.State == StateDocked)
                {
                    Launch(site, d, station);
                }
                d.State = StateOutbound;
                AssignCount++;
                Touch();
            }
            // 突袭中：还没人管的挨打目标，从正修着不挨打目标的无人机里调（按无人机顺序，确定性）。
            for (; next < InRange.Count && InRange[next].Attacked; next++)
            {
                Candidate c = InRange[next];
                foreach (DroneRecord d in rec.Drones)
                {
                    if ((d.State == StateOutbound || d.State == StateRepairing) && !string.IsNullOrEmpty(d.Target) && !IsAttackedKey(d.Target))
                    {
                        Claims.Remove(d.Target);
                        d.Target = c.Key;
                        d.State = StateOutbound;
                        d.Work = 0f;
                        Claims.Add(c.Key);
                        AssignCount++;
                        Touch();
                        break;
                    }
                }
            }
        }

        // ─────────────────────────────── 补充 / 摧毁 / 击落 ───────────────────────────────

        private static void Respawn(CampaignState state, RepairStationRecord rec, BuildingRecord station, bool active, int hz)
        {
            if (rec.Drones.Length >= RepairDroneCatalog.Count)
            {
                if (rec.RespawnTicks >= 0)
                {
                    RefundRespawn(state, rec);
                }
                return;
            }
            if (!active)
            {
                return; // 缺电 / 禁用：补充暂停（已扣的废料留着，来电后接着补）
            }
            if (rec.RespawnTicks < 0)
            {
                int cost = RepairDroneCatalog.RespawnScrap;
                if (cost > 0)
                {
                    ItemDef scrap = ItemCatalog.Find(ItemCatalog.ScrapId);
                    if (scrap == null || !HomeInventory.TryRemove(state, scrap, cost))
                    {
                        return; // 废料不够：等着（状态行写明要多少），够了自动开始
                    }
                    ProductionStats.RecordUnits(state, scrap, cost, produced: false);
                }
                rec.RespawnTicks = 0;
                rec.RespawnPaid = cost;
                Touch();
            }
            rec.RespawnTicks++;
            if (rec.RespawnTicks >= RespawnTicksNeeded(hz))
            {
                DefenseState st = StateOf(state);
                var list = new List<DroneRecord>(rec.Drones)
                {
                    new DroneRecord { Serial = st.NextDroneSerial++, State = StateDocked, X = station.Position.x, Y = station.Position.y, Hp = RepairDroneCatalog.Hp },
                };
                rec.Drones = list.ToArray();
                rec.RespawnTicks = -1;
                rec.RespawnPaid = 0;
                Touch();
            }
        }

        private static int RespawnTicksNeeded(int hz) => Math.Max(1, (int)Math.Round(RepairDroneCatalog.RespawnSeconds * Math.Max(1, hz)));

        private static void RefundRespawn(CampaignState state, RepairStationRecord rec)
        {
            if (rec.RespawnPaid > 0)
            {
                HomeInventory.Add(state, ItemCatalog.ScrapId, rec.RespawnPaid, clampToSpace: false);
            }
            rec.RespawnPaid = 0;
            rec.RespawnTicks = -1;
        }

        /// <summary>
        /// 建筑刚被摧毁（<see cref="BuildingOps.OnBuildingDestroyed"/> 调）：是维修无人机站 = 全部无人机坠毁（内核单位拿掉、计入损失、警告通知与失败提示音），
        /// 正在补充的那一架的废料全额退回；站点留下虚影，重建完工后满编回来（FG06 第 5 节负向“维修无人机站被毁”）。别的建筑不处理（瞄着它的无人机下一步自己返航）。
        /// </summary>
        public static void OnBuildingDestroyed(CampaignState state, BuildingRecord b)
        {
            if (state == null || !IsStation(b))
            {
                return;
            }
            RepairStationRecord rec = Find(state, b.BuildingId);
            if (rec == null || rec.Wrecked)
            {
                return;
            }
            CombatSite site = HomeSite;
            Wreck(state, site != null && !site.IsDisposed ? site : null, rec, b);
        }

        private static void Wreck(CampaignState state, CombatSite site, RepairStationRecord rec, BuildingRecord b)
        {
            int deployed = 0;
            foreach (DroneRecord d in rec.Drones)
            {
                if (d.State != StateDocked)
                {
                    deployed++;
                }
                site?.RemoveDroneUnit(d.Serial);
            }
            int lost = rec.Drones.Length;
            rec.Lost += lost;
            StateOf(state).TotalDronesLost += lost;
            rec.Drones = Array.Empty<DroneRecord>();
            RefundRespawn(state, rec);
            rec.Wrecked = true;
            rec.NoKits = false;
            Touch();
            // 复审修复（P2 口径）：通知写全部坠毁的架数（= 计入损失的数），另注明其中出动中的几架；读数“损失”同口径。
            string text = GameText.Format("drone.notify.crashed", BuildingOps.NameOf(b), lost, deployed);
            NotificationCenter.Post("drone_crashed", text, new Vector3(b.Position.x, 0f, b.Position.y));
            Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.Failure, b.Position, text);
        }

        /// <summary>维修无人机阵亡（= 被击落）：拿掉内核单位、从站里去掉、计入损失；警告通知（同一座站有冷却、同类聚合、点击定位）与失败提示音；首次被击落引导钩子。O(站数 × 编制)。</summary>
        public static void OnKernelEvent(CombatSite site, int serial, CombatEvent e)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || e.Kind != CombatEventKind.Killed)
            {
                return;
            }
            site?.RemoveDroneUnit(serial);
            DroneRecord d = FindDrone(state, serial, out RepairStationRecord rec);
            if (d == null || rec == null)
            {
                return;
            }
            var list = new List<DroneRecord>(rec.Drones);
            list.Remove(d);
            rec.Drones = list.ToArray();
            rec.Lost++;
            StateOf(state).TotalDronesLost++;
            Claims.Remove(d.Target);
            Touch();
            BuildingRecord b = HomeGridService.FindBuilding(state, rec.BuildingId);
            string text = GameText.Format("drone.notify.lost", b != null ? BuildingOps.NameOf(b) : rec.BuildingId);
            Vector2 at = new Vector2(d.X, d.Y);
            RaidResultService.NoteLoss(state, RaidResultService.KindDrone, rec.BuildingId, at); // FG6-DEF-08：突袭结算的损失（记所属维修站）
            if (GameClock.Ticks >= rec.NotifyTick)
            {
                rec.NotifyTick = GameClock.TickAfter(RepairDroneCatalog.NotifyCooldownSeconds);
                NotificationCenter.Post("drone_lost", text, new Vector3(at.x, 0f, at.y));
            }
            Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.Failure, at, text);
            GuidanceHooks.Raise(GuidanceHooks.DefenseRepairDroneFirstLost);
        }

        /// <summary>存档前（<see cref="WorldSimulation.SyncAllForSave"/>）：出动中的无人机在内核里的位置与耐久写回记录（单位本身随内核快照）。</summary>
        public static void WriteTo(CampaignState state)
        {
            CombatSite site = HomeSite;
            if (state == null || site == null || site.IsDisposed)
            {
                return;
            }
            foreach (RepairStationRecord r in All(state))
            {
                foreach (DroneRecord d in r.Drones)
                {
                    if (d.State != StateDocked && site.TryGetDroneState(d.Serial, out _, out float hp, out _, out bool alive) && alive)
                    {
                        d.Hp = hp;
                    }
                }
            }
        }

        // ─────────────────────────────── 读数与状态（B05 / B06）───────────────────────────────

        public static bool TryGetReadout(CampaignState state, string buildingId, out RepairStationReadout ro)
        {
            ro = default;
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            if (!IsStation(b))
            {
                return false;
            }
            RepairStationRecord rec = Find(state, buildingId);
            ro.BuildingId = buildingId;
            ro.Name = BuildingOps.NameOf(b);
            ro.Built = IsBuilt(b);
            ro.Active = IsActive(b);
            ro.Complement = RepairDroneCatalog.Count;
            ro.Range = RepairDroneCatalog.Range;
            ro.Targets = new List<string>(4);
            ro.RaidPriority = StandingRuleService.RaidActive(state);
            if (rec == null || rec.Wrecked)
            {
                ro.Drones = ro.Built ? ro.Complement : 0; // 还没对账到的新站：建成那一刻满编
                return true;
            }
            ro.Drones = rec.Drones.Length;
            ro.NoKits = rec.NoKits;
            ro.KitCredit = rec.KitCredit;
            ro.Repaired = rec.Repaired;
            ro.KitsUsed = rec.KitsUsed;
            ro.Lost = rec.Lost;
            foreach (DroneRecord d in rec.Drones)
            {
                if (d.State != StateDocked)
                {
                    ro.Out++;
                }
                if (d.State == StateRepairing)
                {
                    ro.Repairing++;
                }
                if (!string.IsNullOrEmpty(d.Target))
                {
                    string name = TargetName(state, d.Target);
                    if (!ro.Targets.Contains(name))
                    {
                        ro.Targets.Add(name);
                    }
                }
            }
            if (rec.Drones.Length < ro.Complement)
            {
                if (rec.RespawnTicks >= 0)
                {
                    ro.Respawning = true;
                    int left = Math.Max(0, RespawnTicksNeeded(GameClock.StepHz) - rec.RespawnTicks);
                    ro.RespawnSecondsLeft = left / (float)Math.Max(1, GameClock.StepHz);
                }
                else if (ro.Active && HomeInventory.Stock(state, ItemCatalog.ScrapId) < RepairDroneCatalog.RespawnScrap)
                {
                    ro.RespawnShortScrap = true;
                }
            }
            return true;
        }

        /// <summary>目标的玩家名（建筑名 / “传送带（x, y）”）。</summary>
        public static string TargetName(CampaignState state, string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 3)
            {
                return string.Empty;
            }
            if (key[0] == 'b')
            {
                BuildingRecord b = HomeGridService.FindBuilding(state, key.Substring(2));
                return b != null ? BuildingOps.NameOf(b) : key.Substring(2);
            }
            int comma = key.IndexOf(',');
            return comma > 2 ? GameText.Format("drone.target.belt", key.Substring(2, comma - 2), key.Substring(comma + 1)) : key;
        }

        /// <summary>
        /// 维修无人机站的功能状态（<see cref="BuildingStatusService"/> 在通用状态之后调：缺电 / 禁用 / 虚影 / 被毁先报）：维修中（修谁、几架出动）/ 待命 / 缺维修件（怎么办）；
        /// 编制不满时另起一行写补充倒计时或缺多少废料；突袭中写“先修正在挨打的目标”。
        /// </summary>
        public static BuildingStatus StatusOf(CampaignState state, BuildingRecord b)
        {
            if (!TryGetReadout(state, b?.BuildingId, out RepairStationReadout ro))
            {
                return new BuildingStatus(BuildingStatusKind.Idle, "rds.unknown", string.Empty);
            }
            string range = Mathf.RoundToInt(ro.Range).ToString(CultureInfo.InvariantCulture);
            BuildingStatus head;
            if (ro.NoKits)
            {
                head = new BuildingStatus(BuildingStatusKind.NoMaterial, "rds.no_kits", GameText.Format("bs.reason.drone_no_kits", ro.Drones, ro.Complement));
            }
            else if (ro.Out > 0 && ro.Targets.Count > 0)
            {
                string what = ro.Targets.Count == 1 ? ro.Targets[0] : GameText.Format("drone.target.more", ro.Targets[0], ro.Targets.Count - 1);
                head = new BuildingStatus(BuildingStatusKind.Working, "rds.working", GameText.Format("bs.reason.drone_working", what, ro.Drones, ro.Complement, ro.Out, range));
            }
            else if (ro.Drones == 0)
            {
                head = new BuildingStatus(BuildingStatusKind.Idle, "rds.none", GameText.Get("bs.reason.drone_none"));
            }
            else
            {
                head = new BuildingStatus(BuildingStatusKind.Idle, "rds.idle", GameText.Format("bs.reason.drone_idle", ro.Drones, ro.Complement, range));
            }
            string extra = string.Empty;
            if (ro.Respawning)
            {
                extra += "\n" + GameText.Format("bs.reason.drone_respawn", Mathf.CeilToInt(ro.RespawnSecondsLeft).ToString(CultureInfo.InvariantCulture));
            }
            else if (ro.RespawnShortScrap)
            {
                extra += "\n" + GameText.Format("bs.reason.drone_respawn_scrap", RepairDroneCatalog.RespawnScrap, HomeInventory.Stock(state, ItemCatalog.ScrapId));
            }
            if (ro.RaidPriority && ro.Active)
            {
                extra += "\n" + GameText.Get("drone.readout.priority");
            }
            extra += "\n" + GameText.Format("drone.readout.stats", Mathf.RoundToInt((float)ro.Repaired), ro.KitsUsed, ro.Lost);
            return new BuildingStatus(head.Kind, head.ReasonCode, head.Reason + extra);
        }

        /// <summary>画面读：出动中的无人机（位置、目标位置、是否在修）。<paramref name="into"/> 先清空。</summary>
        public static void DronesForView(CampaignState state, List<(Vector2 Pos, Vector2 Target, bool Repairing)> into)
        {
            into.Clear();
            foreach (RepairStationRecord r in All(state))
            {
                foreach (DroneRecord d in r.Drones)
                {
                    if (d.State == StateDocked)
                    {
                        continue;
                    }
                    Vector2 target = new Vector2(d.X, d.Y);
                    if (TryTarget(state, d.Target, out TargetInfo t))
                    {
                        target = t.Pos;
                    }
                    into.Add((new Vector2(d.X, d.Y), target, d.State == StateRepairing));
                }
            }
        }
    }
}
