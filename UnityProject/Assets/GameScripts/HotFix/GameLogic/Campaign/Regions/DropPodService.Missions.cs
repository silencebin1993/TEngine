using System;
using System.Collections.Generic;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>FG8-DROP-01：一次投送 / 回收的预览（面板、战略地图悬停、自检读同一份；发射时按它校验）。</summary>
    public sealed class DropPreview
    {
        public bool Ok;
        /// <summary>拒绝原因码（null = 可以发射）。</summary>
        public string Code;
        /// <summary>拒绝原因（玩家看的一句，文本键）。</summary>
        public string Reason = string.Empty;
        public bool Recall;
        public float Distance;
        public double LegSeconds;
        public long FuelMl;
        public long FuelHaveMl;
        public int PodsNeeded;
        public int PodsReady;
        public int PodsCooling;
        public int PodsTotal;
        public double SoonestReadySeconds = -1;
        /// <summary>落点在敌方防空覆盖里（拒绝；地图上显示红色区域）。</summary>
        public bool AntiAir;
        /// <summary>拒绝原因是防空时覆盖落点的据点 ID（<see cref="DropPodService.RevealAntiAir(CampaignState, DropPreview)"/> 用它探明红色区域）。</summary>
        public string AntiAirOutpostId = string.Empty;
        /// <summary>要装舱 / 回收的机器里手上有工作单的台数（装舱时施工单交还待分配池，其余工单取消、货舱里的货落地，见 <see cref="DropPodService.Board"/>）。</summary>
        public int BusyMachines;
        public readonly List<int> Machines = new List<int>(8);
        public string CargoItem = string.Empty;
        public int CargoAmount;
        /// <summary>回收：附近还有几台机器这次装不下（舱不够）。</summary>
        public int Leftover;
        /// <summary>回收：附近地上的货物件数（多出来的舱装它）。</summary>
        public int GroundCargo;
    }

    public static partial class DropPodService
    {
        // ─────────────────────────────── 预览 ───────────────────────────────

        private static readonly List<MachineRecord> MachineScratch = new List<MachineRecord>(16);

        private static DropPreview Reject(DropPreview p, string code, string reason)
        {
            p.Ok = false;
            p.Code = code;
            p.Reason = reason ?? string.Empty;
            return p;
        }

        /// <summary>
        /// 发射前检查（FGR-EXP-014 / 016；负向：防空覆盖内拒绝、燃油不足、投送舱全部在冷却）。顺序：站点 → 静默夜 → 载荷 → 落点（迷雾 / 距离 / 平地 / 危害带中心 / 防空 / 据点）→ 舱 → 燃油。
        /// 只读，不改世界。<paramref name="machines"/> 里每台都要在站旁可装（<see cref="LoadableMachines"/>）；货物从仓库取（每舱 drop.cargo_units 个）。
        /// </summary>
        public static DropPreview Preview(CampaignState s, string stationId, Vector2 target, IReadOnlyList<int> machines, string cargoItem, int cargoAmount)
        {
            var p = new DropPreview();
            BuildingRecord b = HomeGridService.FindBuilding(s, stationId);
            DropStationRecord st = FindStation(s, stationId);
            FillPods(s, stationId, p);
            if (b == null || st == null || !IsStation(b))
            {
                return Reject(p, "no-station", GameText.Get("drop.reason.no_station"));
            }
            if (!IsUsable(b))
            {
                return Reject(p, "station-offline", StationOfflineReason(b));
            }
            if (Env.SilentNightService.IsActive(s))
            {
                return Reject(p, "silent-night", GameText.Format("env.silent.reject.drop", Env.CalendarService.ShortDuration(Env.SilentNightService.RemainingSeconds(s))));
            }
            LoadableMachines(s, b, MachineScratch);
            if (machines != null)
            {
                foreach (int id in machines)
                {
                    if (p.Machines.Contains(id))
                    {
                        continue;
                    }
                    if (!MachineScratch.Exists(m => m.LogicId == id))
                    {
                        return Reject(p, "machine-not-ready", GameText.Format("drop.reason.machine_not_ready", MachineNaming.Short(id), Mathf.RoundToInt(LoadRadius)));
                    }
                    p.Machines.Add(id);
                    p.BusyMachines += IsBusy(s, id) ? 1 : 0;
                }
            }
            MachineScratch.Clear();
            if (!string.IsNullOrEmpty(cargoItem) && cargoAmount > 0)
            {
                if (!ItemCatalog.TryGet(cargoItem, out ItemDef d) || d.Form != ItemForm.Solid || d.Id == PodItemId)
                {
                    return Reject(p, "cargo-invalid", GameText.Get("drop.reason.cargo_invalid"));
                }
                int have = HomeInventory.Stock(s, d);
                if (have < cargoAmount)
                {
                    return Reject(p, "cargo-short", GameText.Format("drop.reason.cargo_short", GameText.Get(d.NameKey), cargoAmount, have));
                }
                p.CargoItem = cargoItem;
                p.CargoAmount = cargoAmount;
            }
            p.PodsNeeded = p.Machines.Count + (p.CargoAmount + CargoUnits - 1) / CargoUnits;
            if (p.PodsNeeded == 0)
            {
                return Reject(p, "no-payload", GameText.Get("drop.reason.no_payload"));
            }
            string landing = TargetChecks(s, b, target, p);
            if (landing != null)
            {
                return p;
            }
            if (PodShortage(p) is string code)
            {
                return Reject(p, code, PodShortageReason(p));
            }
            p.FuelMl = FuelPerPodMl(p.Distance) * p.PodsNeeded;
            p.FuelHaveMl = FuelMl(st);
            if (p.FuelHaveMl < p.FuelMl)
            {
                return Reject(p, "fuel", GameText.Format("drop.reason.fuel", Liters(p.FuelMl), Liters(p.FuelHaveMl)));
            }
            p.Ok = true;
            p.Code = null;
            return p;
        }

        /// <summary>回收前检查（FGR-EXP-015）：站点 → 静默夜 → 回收点（迷雾 / 距离 / 着陆条件）→ 附近有没有要接的机器 / 货物 → 舱 → 燃油。舱不够时先接机器（按编号），写明还剩几台。</summary>
        public static DropPreview PreviewRecall(CampaignState s, string stationId, Vector2 point)
        {
            var p = new DropPreview { Recall = true };
            BuildingRecord b = HomeGridService.FindBuilding(s, stationId);
            DropStationRecord st = FindStation(s, stationId);
            FillPods(s, stationId, p);
            if (b == null || st == null || !IsStation(b))
            {
                return Reject(p, "no-station", GameText.Get("drop.reason.no_station"));
            }
            if (!IsUsable(b))
            {
                return Reject(p, "station-offline", StationOfflineReason(b));
            }
            if (Env.SilentNightService.IsActive(s))
            {
                return Reject(p, "silent-night", GameText.Format("env.silent.reject.drop", Env.CalendarService.ShortDuration(Env.SilentNightService.RemainingSeconds(s))));
            }
            if (TargetChecks(s, b, point, p) != null)
            {
                return p;
            }
            RecallableMachines(s, point, MachineScratch);
            p.GroundCargo = GroundCargoNear(s, point);
            int wanted = MachineScratch.Count + (p.GroundCargo + CargoUnits - 1) / CargoUnits;
            if (wanted == 0)
            {
                MachineScratch.Clear();
                return Reject(p, "nothing-to-recall", GameText.Format("drop.reason.nothing_to_recall", Mathf.RoundToInt(RecallRadius)));
            }
            if (p.PodsReady == 0)
            {
                MachineScratch.Clear();
                p.PodsNeeded = wanted;
                return Reject(p, PodShortage(p) ?? "no-pods", PodShortageReason(p));
            }
            int pods = Math.Min(p.PodsReady, wanted);
            for (int i = 0; i < MachineScratch.Count && p.Machines.Count < pods; i++)
            {
                p.Machines.Add(MachineScratch[i].LogicId);
                p.BusyMachines += IsBusy(s, MachineScratch[i].LogicId) ? 1 : 0;
            }
            p.Leftover = MachineScratch.Count - p.Machines.Count;
            MachineScratch.Clear();
            p.PodsNeeded = pods;
            p.FuelMl = FuelPerPodMl(p.Distance) * p.PodsNeeded;
            p.FuelHaveMl = FuelMl(st);
            if (p.FuelHaveMl < p.FuelMl)
            {
                return Reject(p, "fuel", GameText.Format("drop.reason.fuel", Liters(p.FuelMl), Liters(p.FuelHaveMl)));
            }
            p.Ok = true;
            p.Code = null;
            return p;
        }

        private static void FillPods(CampaignState s, string stationId, DropPreview p)
        {
            p.PodsTotal = PodCount(s, stationId);
            p.PodsReady = CountState(s, stationId, StateDocked);
            p.PodsCooling = CountState(s, stationId, StateCooling);
            p.SoonestReadySeconds = SoonestReadySeconds(s, stationId);
        }

        /// <summary>落点检查：距离（投送距离内）→ 着陆条件。拒绝时写进 <paramref name="p"/> 并返回原因码。</summary>
        private static string TargetChecks(CampaignState s, BuildingRecord b, Vector2 target, DropPreview p)
        {
            p.Distance = Vector2.Distance(b.Position, target);
            p.LegSeconds = LegSeconds(p.Distance);
            string code = CheckLanding(s, target.x, target.y, out string detail, out OutpostRecord aa);
            if (code == "unexplored")
            {
                Reject(p, code, detail);
                return code;
            }
            if (p.Distance > RangeCells)
            {
                Reject(p, "out-of-range", GameText.Format("drop.reason.out_of_range", Mathf.RoundToInt(p.Distance), Mathf.RoundToInt(RangeCells)));
                return p.Code;
            }
            if (code != null)
            {
                p.AntiAir = code == "antiair";
                p.AntiAirOutpostId = aa?.OutpostId ?? string.Empty;
                Reject(p, code, detail);
                return code;
            }
            return null;
        }

        private static string PodShortage(DropPreview p)
        {
            if (p.PodsReady >= p.PodsNeeded)
            {
                return null;
            }
            if (p.PodsTotal == 0)
            {
                return "no-pods";
            }
            if (p.PodsReady == 0 && p.PodsCooling > 0)
            {
                return "all-cooling";
            }
            return "not-enough-pods";
        }

        private static string PodShortageReason(DropPreview p)
        {
            switch (PodShortage(p))
            {
                case "no-pods": return GameText.Get("drop.reason.no_pods");
                case "all-cooling": return GameText.Format("drop.reason.all_cooling", p.PodsCooling, DropPodService.Duration(Math.Max(0, p.SoonestReadySeconds)));
                case "not-enough-pods": return GameText.Format("drop.reason.not_enough_pods", p.PodsNeeded, p.PodsReady, p.PodsCooling);
                default: return string.Empty;
            }
        }

        private static string StationOfflineReason(BuildingRecord b)
        {
            if (b.ConstructionState == BuildingConstructionState.Damaged || b.ConstructionState == BuildingConstructionState.Destroyed)
            {
                return GameText.Get("drop.reason.station_destroyed");
            }
            if (b.ConstructionState == BuildingConstructionState.Disabled)
            {
                return GameText.Get("drop.reason.station_disabled");
            }
            if (b.ConstructionState != BuildingConstructionState.Operational)
            {
                return GameText.Get("drop.reason.station_unbuilt");
            }
            return GameText.Get("drop.reason.station_unpowered");
        }

        public static string Liters(long ml) => Mathf.CeilToInt(ml / 1000f).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // ─────────────────────────────── 发射 / 回收 ───────────────────────────────

        /// <summary>发射（投送站面板“发射”）：按预览校验，通过后扣燃油、装舱（机器离开地面进舱、货物出库）、起飞。被拒时出拒绝音与原因，不改世界。</summary>
        public static bool TryLaunch(CampaignState s, string stationId, Vector2 target, IReadOnlyList<int> machines, string cargoItem, int cargoAmount, out DropPreview p) =>
            TryLaunch(s, stationId, target, machines, cargoItem, cargoAmount, false, out p);

        /// <summary>FG8-OUT-01：<paramref name="constructionPack"/> = 货物是施工包（着陆时先倒进落点附近还缺这种材料的施工现场，见 <see cref="WorldSim.OwnOutpostService.PourPack"/>）。</summary>
        public static bool TryLaunch(CampaignState s, string stationId, Vector2 target, IReadOnlyList<int> machines, string cargoItem, int cargoAmount, bool constructionPack,
            out DropPreview p)
        {
            p = Preview(s, stationId, target, machines, cargoItem, cargoAmount);
            if (!p.Ok)
            {
                RevealAntiAir(s, p);
                Denied(p.Reason);
                return false;
            }
            DropPodState d = StateOf(s);
            DropStationRecord st = FindStation(s, stationId);
            BuildingRecord b = HomeGridService.FindBuilding(s, stationId);
            TakeFuel(st, p.FuelMl);
            int group = d.NextGroupSerial++;
            long perPod = FuelPerPodMl(p.Distance);
            List<DropPodRecord> ready = ReadyPods(s, stationId);
            int k = 0;
            var boarded = new List<int>(p.Machines.Count);
            foreach (int id in p.Machines)
            {
                DropPodRecord pod = ready[k++];
                Board(s, pod, id);
                boarded.Add(id);
                Depart(pod, MissionDelivery, b.Position, target, perPod, group);
            }
            if (p.CargoAmount > 0 && ItemCatalog.TryGet(p.CargoItem, out ItemDef item))
            {
                int left = HomeInventory.RemoveUpTo(s, item, p.CargoAmount);
                while (left > 0 && k < ready.Count)
                {
                    DropPodRecord pod = ready[k++];
                    int take = Math.Min(CargoUnits, left);
                    pod.CargoItem = item.Id;
                    pod.CargoAmount = take;
                    pod.ConstructionPack = constructionPack;
                    left -= take;
                    Depart(pod, MissionDelivery, b.Position, target, perPod, group);
                }
            }
            ExpeditionMarchService.OnMembersBoardedPod(s, boarded);
            d.Launches++;
            st.Launches++;
            ScheduleEventCheck();
            Touch();
            HomeInventory.Touch();
            FeedbackCues.RaiseLocated(FeedbackCueId.CommandAck, target, GameText.Format("drop.cue.launched", p.PodsNeeded, DropPodService.Duration(p.LegSeconds)));
            GuidanceHooks.Raise(GuidanceHooks.DropFirstLaunch);
            Log.Info($"[DropPodService] 投送：{p.PodsNeeded} 舱（机器 {p.Machines.Count}，货物 {p.CargoAmount}）→ ({target.x:0},{target.y:0})，{p.Distance:0} 格，单程 {p.LegSeconds:0.0} 秒，燃油 {Liters(p.FuelMl)} 升。");
            return true;
        }

        /// <summary>呼叫回收（投送站面板 / 战略地图“呼叫回收”）：按预览校验，通过后扣燃油、空舱起飞去接。被拒时出拒绝音与原因，不改世界。</summary>
        public static bool TryRecall(CampaignState s, string stationId, Vector2 point, out DropPreview p)
        {
            p = PreviewRecall(s, stationId, point);
            if (!p.Ok)
            {
                RevealAntiAir(s, p);
                Denied(p.Reason);
                return false;
            }
            DropPodState d = StateOf(s);
            DropStationRecord st = FindStation(s, stationId);
            BuildingRecord b = HomeGridService.FindBuilding(s, stationId);
            TakeFuel(st, p.FuelMl);
            int group = d.NextGroupSerial++;
            long perPod = FuelPerPodMl(p.Distance);
            List<DropPodRecord> ready = ReadyPods(s, stationId);
            for (int i = 0; i < p.PodsNeeded; i++)
            {
                DropPodRecord pod = ready[i];
                pod.RecallMachineId = i < p.Machines.Count ? p.Machines[i] : 0;
                Depart(pod, MissionRecall, b.Position, point, perPod, group);
            }
            d.Recalls++;
            st.Launches++;
            ScheduleEventCheck();
            Touch();
            FeedbackCues.RaiseLocated(FeedbackCueId.CommandAck, point, GameText.Format("drop.cue.recall", p.PodsNeeded, DropPodService.Duration(p.LegSeconds)));
            GuidanceHooks.Raise(GuidanceHooks.DropFirstRecall);
            Log.Info($"[DropPodService] 呼叫回收：{p.PodsNeeded} 舱 → ({point.x:0},{point.y:0})，接机器 {p.Machines.Count} 台、地上货物 {p.GroundCargo} 件，{p.Distance:0} 格。");
            return true;
        }

        private static void Denied(string reason)
        {
            RecordReject(reason);
            FeedbackCues.Raise(FeedbackCueId.Denied, reason);
        }

        private static List<DropPodRecord> ReadyPods(CampaignState s, string stationId)
        {
            var list = new List<DropPodRecord>();
            foreach (DropPodRecord pod in Pods(s))
            {
                if (pod.StationId == stationId && pod.State == StateDocked)
                {
                    list.Add(pod);
                }
            }
            list.Sort((a, b) => a.Serial.CompareTo(b.Serial));
            return list;
        }

        /// <summary>这台机器手上有没有在办的工作单（面板 / 预览提示“装舱时手上的活会交还 / 取消”）。</summary>
        public static bool IsBusy(CampaignState s, int logicId) => HomeValleyWorkOrders.FindActiveOrderForMachine(s, logicId) != null;

        /// <summary>
        /// 机器进舱：写回内核里的实时状态（位置、热量、冷却）→ 中断手上的工作单（与远征出发同一事务 <see cref="ExpeditionDepartureService.InterruptWorkForDeparture"/>：
        /// 施工单交还待分配池，其余工单取消、货舱里搬运中的货在机器脚下落地、预留的维修件退回）→ 离开地面（家园对账时拿掉它的单位、接入自动断开）。
        /// 机器在天上时家园不会有工单被它推进或卡住；落地后是空闲的，能再派工。
        /// </summary>
        internal static void Board(CampaignState s, DropPodRecord pod, int logicId)
        {
            CombatSites.ExportMachine(logicId);
            if (IsBusy(s, logicId))
            {
                ExpeditionDepartureService.InterruptWorkForDeparture(s, new[] { logicId });
            }
            if (MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) && rec != null)
            {
                MachineRegistry.MoveToRegion(rec, InPodRegionId);
            }
            pod.MachineId = logicId;
        }

        private static void Depart(DropPodRecord pod, int mission, Vector2 from, Vector2 to, long fuelMl, int group)
        {
            pod.Mission = mission;
            pod.State = StateOutbound;
            pod.FromX = from.x;
            pod.FromY = from.y;
            pod.ToX = to.x;
            pod.ToY = to.y;
            pod.TargetX = to.x;
            pod.TargetY = to.y;
            pod.StartTick = GameClock.Ticks;
            pod.ArriveTick = GameClock.Ticks + Math.Max(1, GameClock.TicksFor(LegSeconds(Vector2.Distance(from, to))));
            pod.FuelMl = fuelMl;
            pod.Group = group;
            pod.Note = string.Empty;
        }

        // ─────────────────────────────── 位置（插值，只读）───────────────────────────────

        /// <summary>舱此刻的位置（在站里 = 站的位置；在飞 = 起点到终点按飞行进度插值，着陆的最后 drop.landing_seconds 秒停在终点上空）。</summary>
        public static Vector2 PositionOf(CampaignState s, DropPodRecord pod, long now)
        {
            if (pod.State != StateOutbound && pod.State != StateReturning)
            {
                BuildingRecord b = HomeGridService.FindBuilding(s, pod.StationId);
                return b != null ? b.Position : new Vector2(pod.ToX, pod.ToY);
            }
            long landing = GameClock.TicksFor(LandingSeconds);
            long flight = Math.Max(1, pod.ArriveTick - pod.StartTick - landing);
            float t = Mathf.Clamp01((now - pod.StartTick) / (float)flight);
            return Vector2.Lerp(new Vector2(pod.FromX, pod.FromY), new Vector2(pod.ToX, pod.ToY), t);
        }

        public static double SecondsLeft(DropPodRecord pod, long now) => Math.Max(0, pod.ArriveTick - now) / (double)Math.Max(1, GameClock.StepHz);
    }
}
