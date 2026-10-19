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
    public static partial class DropPodService
    {
        // ─────────────────────────────── 世界模拟步 ───────────────────────────────

        /// <summary>
        /// 世界模拟的每个固定步（家园载入时，<see cref="WorldSim.WorldSimulation"/> 在家园内核与炮塔之后调）：每 drop.sync_seconds 对账（站点记录、管线燃油、自动补满、返程改降）；
        /// 到了下一个事件步（着陆 / 冷却结束）处理到期的舱。只看步序号，与观察无关；暂停不走；倍速按步。
        /// </summary>
        public static void WorldStep(CampaignState state, long ticks, int worldHz)
        {
            DropPodState d = StateOf(state);
            if (d == null || worldHz <= 0)
            {
                return;
            }
            long every = Math.Max(1, (long)Math.Round(SyncSeconds * worldHz));
            if (ticks % every == 0)
            {
                Sync(state);
            }
            if (d.Pods.Length == 0 || ticks < _nextEventTick)
            {
                return;
            }
            ProcessDue(state, ticks);
        }

        private static readonly List<GridCell> RingScratch = new List<GridCell>(32);
        private static readonly List<GridCell> FootScratch = new List<GridCell>(32);
        private static readonly HashSet<GridCell> FootSet = new HashSet<GridCell>();

        /// <summary>
        /// 对账（O(建筑数 + 站数 + 舱数)）：站点建筑 ↔ 记录（建成的站补记录；拆掉的站：站里的舱退回仓库、记录拿掉）；燃油管线接点；自动补满；返程中归属站被摧毁 / 拆掉的舱改降（只缺电 / 禁用不改降；静默夜不改降）。
        /// </summary>
        public static void Sync(CampaignState state)
        {
            DropPodState d = StateOf(state);
            if (d == null)
            {
                return;
            }
            SyncCount++;
            bool changed = false;
            // 站点记录。
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (IsBuilt(b) && FindStation(state, b.BuildingId) == null)
                {
                    var list = new List<DropStationRecord>(d.Stations) { new DropStationRecord { BuildingId = b.BuildingId, X = b.Position.x, Y = b.Position.y } };
                    d.Stations = list.ToArray();
                    changed = true;
                    RaiseBuiltHookOnce();
                }
            }
            if (d.Stations.Length > 0)
            {
                var keep = new List<DropStationRecord>(d.Stations.Length);
                foreach (DropStationRecord st in d.Stations)
                {
                    BuildingRecord b = HomeGridService.FindBuilding(state, st.BuildingId);
                    if (b == null || !IsStation(b))
                    {
                        RemoveStation(state, st, b);
                        changed = true;
                        continue;
                    }
                    keep.Add(st);
                }
                if (keep.Count != d.Stations.Length)
                {
                    d.Stations = keep.ToArray();
                }
            }
            foreach (DropStationRecord st in d.Stations)
            {
                BuildingRecord b = HomeGridService.FindBuilding(state, st.BuildingId);
                if (b != null)
                {
                    st.X = b.Position.x;
                    st.Y = b.Position.y;
                }
                SyncFuel(state, st, b);
                changed |= AutoFill(state, st, b);
            }
            // 返程改降（FG08 第 8 节负向“投送站在远征期间被突袭摧毁”）。
            if (!Env.SilentNightService.IsActive(state))
            {
                long now = GameClock.Ticks;
                foreach (DropPodRecord pod in d.Pods)
                {
                    if (pod.State != StateReturning)
                    {
                        continue;
                    }
                    BuildingRecord home = HomeGridService.FindBuilding(state, pod.StationId);
                    if (!IsLost(home))
                    {
                        continue; // 只缺电 / 被禁用：照常飞回原站（接收舱不需要电），等恢复
                    }
                    Vector2 at = PositionOf(state, pod, now);
                    BuildingRecord alt = NearestUsableStation(state, at, pod.StationId);
                    if (alt == null)
                    {
                        continue; // 没有别的投送站：按原定地点降落（被毁的站旁）
                    }
                    Divert(state, pod, alt, at, now);
                    changed = true;
                }
            }
            if (changed)
            {
                Touch();
            }
        }

        /// <summary>拆掉的站（建筑记录没了）：在站 / 冷却中的舱当作物品退回仓库（放不下的落在原地）；管线消费者撤掉，站里的燃油随建筑一起没了（与拆除其他储油建筑一致）。</summary>
        private static void RemoveStation(CampaignState state, DropStationRecord st, BuildingRecord b)
        {
            if (st.ConsumerId > 0 && PipeNetworkService.IsRunning)
            {
                PipeNetworkService.Kernel.RemoveConsumer(st.ConsumerId, out _);
            }
            st.ConsumerId = 0;
            DropPodState d = StateOf(state);
            var pods = new List<DropPodRecord>(d.Pods.Length);
            int returned = 0;
            foreach (DropPodRecord p in d.Pods)
            {
                if (p.StationId == st.BuildingId && (p.State == StateDocked || p.State == StateCooling) && p.MachineId == 0 && p.CargoAmount == 0)
                {
                    returned++;
                    continue;
                }
                pods.Add(p);
            }
            if (returned > 0)
            {
                d.Pods = pods.ToArray();
                ReturnPodItems(state, returned, b?.Position ?? new Vector2(st.X, st.Y));
            }
        }

        private static void ReturnPodItems(CampaignState state, int count, Vector2 at)
        {
            if (!ItemCatalog.TryGet(PodItemId, out ItemDef pod) || count <= 0)
            {
                return;
            }
            int added = HomeInventory.Add(state, pod, count);
            if (added < count)
            {
                HomeValleyCargo.SpawnGroundItem(state, HomeValleyLayout.RegionId, at, PodItemId, count - added, "drop:pods:" + GameClock.Ticks + ":" + at.x.ToString("0") + ":" + at.y.ToString("0"));
            }
            HomeInventory.Touch();
        }

        /// <summary>站边一格的管线接燃油（与炮塔同一套接法：优先装着燃油的管线格，其次空网络）；被摧毁 / 虚影时撤掉消费者（缓存留在站里）。消费者缓存就是站的油箱（drop.station.fuel_tank_liters）。</summary>
        private static void SyncFuel(CampaignState state, DropStationRecord st, BuildingRecord b)
        {
            bool pipes = PipeNetworkService.IsRunning;
            bool working = b != null && (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled);
            int fuel = pipes ? PipeNetworkService.FluidId("fuel") : 0;
            PipeConsumerInfo info = default;
            bool alive = st.ConsumerId > 0 && pipes && PipeNetworkService.Kernel.TryGetConsumer(st.ConsumerId, out info);
            if (st.ConsumerId > 0 && !alive)
            {
                st.ConsumerId = 0; // 管线内核换了（读档 / 重建）：消费者不在了，缓存已随旧内核清掉
            }
            if (!working || !pipes || fuel <= 0)
            {
                ReleaseConsumer(st);
                return;
            }
            GridCell? attach = FindPipe(b, fuel);
            if (alive && (attach == null || attach.Value.X != info.X || attach.Value.Y != info.Y))
            {
                ReleaseConsumer(st);
                alive = false;
            }
            if (!alive && attach != null)
            {
                long capMl = (long)Math.Round(TankLiters * 1000.0);
                int id = PipeNetworkService.Kernel.AddConsumer(attach.Value.X, attach.Value.Y, fuel, FuelLpm, BinGames.Sim.Logistics.PipeConst.PriorityMin + 2);
                if (id > 0)
                {
                    long held = st.HeldFuelMl;
                    PipeNetworkService.Kernel.SetConsumerBuffer(id, capMl, Math.Min(capMl, held));
                    st.HeldFuelMl = Math.Max(0, held - capMl);
                    st.ConsumerId = id;
                }
            }
        }

        private static void ReleaseConsumer(DropStationRecord st)
        {
            if (st.ConsumerId > 0 && PipeNetworkService.IsRunning && PipeNetworkService.Kernel.RemoveConsumer(st.ConsumerId, out long buffered))
            {
                st.HeldFuelMl += Math.Max(0, buffered);
            }
            st.ConsumerId = 0;
        }

        private static GridCell? FindPipe(BuildingRecord b, int fuel)
        {
            RingScratch.Clear();
            FootScratch.Clear();
            FootSet.Clear();
            HomeGridService.FootprintOf(b, FootScratch);
            foreach (GridCell c in FootScratch)
            {
                FootSet.Add(c);
            }
            foreach (GridCell c in FootScratch)
            {
                for (int k = 0; k < 4; k++)
                {
                    GridCell n = k == 0 ? new GridCell(c.X, c.Y + 1) : k == 1 ? new GridCell(c.X + 1, c.Y) : k == 2 ? new GridCell(c.X, c.Y - 1) : new GridCell(c.X - 1, c.Y);
                    if (!FootSet.Contains(n) && !RingScratch.Contains(n))
                    {
                        RingScratch.Add(n);
                    }
                }
            }
            GridCell? empty = null;
            foreach (GridCell c in RingScratch)
            {
                if (!PipeNetworkService.Kernel.TryGetCellInfo(c.X, c.Y, out PipeCellInfo ci) || ci.Kind == PipePieceKind.Pump)
                {
                    continue;
                }
                if (ci.Fluid == fuel)
                {
                    return c;
                }
                if (ci.Fluid == 0)
                {
                    empty ??= c;
                }
            }
            return empty;
        }

        /// <summary>这座站接着的管线格是不是别的流体（状态行写原因）。</summary>
        public static bool WrongFluidNear(BuildingRecord b)
        {
            if (b == null || !PipeNetworkService.IsRunning)
            {
                return false;
            }
            int fuel = PipeNetworkService.FluidId("fuel");
            if (FindPipe(b, fuel) != null)
            {
                return false;
            }
            foreach (GridCell c in RingScratch)
            {
                if (PipeNetworkService.Kernel.TryGetCellInfo(c.X, c.Y, out PipeCellInfo ci) && ci.Fluid != 0 && ci.Fluid != fuel)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>自动补满（站在运转、开关开着）：仓库里的投送舱装进站里直到满编。</summary>
        private static bool AutoFill(CampaignState state, DropStationRecord st, BuildingRecord b)
        {
            if (!st.AutoFill || b == null || b.ConstructionState != BuildingConstructionState.Operational)
            {
                return false;
            }
            return LoadPods(state, st, b, int.MaxValue) > 0;
        }

        /// <summary>从仓库装投送舱进站（投送站面板“装填”与自动补满同一入口）：最多装到满编。返回装了几个。</summary>
        public static int LoadPods(CampaignState state, DropStationRecord st, BuildingRecord b, int max)
        {
            if (st == null || b == null || !ItemCatalog.TryGet(PodItemId, out ItemDef item))
            {
                return 0;
            }
            int room = Capacity(b) - PodCount(state, st.BuildingId);
            int n = Math.Min(Math.Min(room, max), HomeInventory.Stock(state, item));
            if (n <= 0)
            {
                return 0;
            }
            int took = HomeInventory.RemoveUpTo(state, item, n);
            DropPodState d = StateOf(state);
            var pods = new List<DropPodRecord>(d.Pods);
            for (int i = 0; i < took; i++)
            {
                pods.Add(new DropPodRecord { Serial = d.NextPodSerial++, StationId = st.BuildingId, State = StateDocked });
            }
            d.Pods = pods.ToArray();
            HomeInventory.Touch();
            Touch();
            return took;
        }

        /// <summary>归属站没了：被摧毁（只剩虚影）、拆掉（建筑记录没了）、不再是投送站。只缺电 / 被禁用不算（FG08 第 8 节、ADR-EXP-004：改降只在站被摧毁时发生）。</summary>
        public static bool IsLost(BuildingRecord home) =>
            home == null || !IsStation(home) || home.ConstructionState == BuildingConstructionState.Damaged || home.ConstructionState == BuildingConstructionState.Destroyed;

        /// <summary>离 <paramref name="at"/> 最近的另一座能用、还有空位（舱数 &lt; 容量，FGR-EXP-013 T1 4 / T2 8）的投送站（距离并列按建筑 ID 定序）。都满了 = null（按原定地点降落）。</summary>
        public static BuildingRecord NearestUsableStation(CampaignState state, Vector2 at, string excludeId)
        {
            BuildingRecord best = null;
            float bestD = float.MaxValue;
            foreach (DropStationRecord st in Stations(state))
            {
                if (st.BuildingId == excludeId)
                {
                    continue;
                }
                BuildingRecord b = HomeGridService.FindBuilding(state, st.BuildingId);
                if (!IsUsable(b) || PodCount(state, st.BuildingId) >= Capacity(b))
                {
                    continue;
                }
                float dd = Vector2.Distance(b.Position, at);
                if (dd < bestD - 1e-3f || (Math.Abs(dd - bestD) <= 1e-3f && best != null && string.CompareOrdinal(b.BuildingId, best.BuildingId) < 0))
                {
                    bestD = dd;
                    best = b;
                }
            }
            return best;
        }

        private static void Divert(CampaignState state, DropPodRecord pod, BuildingRecord alt, Vector2 from, long now)
        {
            pod.StationId = alt.BuildingId;
            pod.FromX = from.x;
            pod.FromY = from.y;
            pod.ToX = alt.Position.x;
            pod.ToY = alt.Position.y;
            pod.StartTick = now;
            pod.ArriveTick = now + Math.Max(1, GameClock.TicksFor(LegSeconds(Vector2.Distance(from, alt.Position))));
            pod.Note = "diverted";
            StateOf(state).Diverted++;
            ScheduleEventCheck();
            NotificationCenter.Post("drop_alert", GameText.Format("drop.notify.diverted", pod.Serial, BuildingOps.NameOf(alt)), new Vector3(alt.Position.x, 0f, alt.Position.y));
        }

        // ─────────────────────────────── 事件 ───────────────────────────────

        private static readonly Dictionary<int, int> GroupSlots = new Dictionary<int, int>();
        private static readonly List<int> ReturnedScratch = new List<int>(8);

        private static void ProcessDue(CampaignState state, long now)
        {
            EventPasses++;
            DropPodState d = StateOf(state);
            GroupSlots.Clear();
            ReturnedScratch.Clear();
            bool changed = false;
            DropPodRecord[] pods = d.Pods;
            for (int i = 0; i < pods.Length; i++)
            {
                DropPodRecord pod = pods[i];
                if (pod.State == StateOutbound && now >= pod.ArriveTick)
                {
                    OnOutboundArrive(state, pod, now);
                    changed = true;
                }
                else if (pod.State == StateReturning && now >= pod.ArriveTick)
                {
                    OnReturnArrive(state, pod, now);
                    changed = true;
                }
                else if (pod.State == StateCooling && now >= pod.CooldownUntilTick)
                {
                    pod.State = StateDocked;
                    changed = true;
                }
            }
            if (ReturnedScratch.Count > 0)
            {
                // 回收的机器按远征卸货规则卸到仓库或解析台（与行军返回同一入口；下一个模拟步处理）。
                StandingRuleService.OnExpeditionReturned(state, new List<int>(ReturnedScratch));
                ReturnedScratch.Clear();
            }
            // 拆掉的站的舱落地后变回物品（见 OnReturnArrive）。
            if (_podsToRemove.Count > 0)
            {
                var keep = new List<DropPodRecord>(d.Pods.Length);
                foreach (DropPodRecord p in d.Pods)
                {
                    if (!_podsToRemove.Contains(p.Serial))
                    {
                        keep.Add(p);
                    }
                }
                d.Pods = keep.ToArray();
                _podsToRemove.Clear();
            }
            long next = long.MaxValue;
            foreach (DropPodRecord p in d.Pods)
            {
                if (p.State == StateOutbound || p.State == StateReturning)
                {
                    next = Math.Min(next, p.ArriveTick);
                }
                else if (p.State == StateCooling)
                {
                    next = Math.Min(next, p.CooldownUntilTick);
                }
            }
            _nextEventTick = next;
            if (changed)
            {
                Touch();
            }
        }

        private static readonly HashSet<int> _podsToRemove = new HashSet<int>();

        private static int NextSlot(int group)
        {
            GroupSlots.TryGetValue(group, out int n);
            GroupSlots[group] = n + 1;
            return n;
        }

        /// <summary>到达落点 / 回收点：落点被敌人占领（防空 / 据点）→ 不降落、带着载荷返回；投送 → 机器下舱、货物落地；回收 → 接上机器 / 地上的货物。之后起飞返程。</summary>
        private static void OnOutboundArrive(CampaignState state, DropPodRecord pod, long now)
        {
            DropPodState d = StateOf(state);
            var target = new Vector2(pod.TargetX, pod.TargetY);
            Vector2 origin = new Vector2(pod.FromX, pod.FromY);
            string occupied = CheckOccupied(state, target.x, target.y, out string who, out OutpostRecord aa);
            if (occupied != null)
            {
                if (aa != null)
                {
                    RevealAntiAir(state, aa.OutpostId); // 被防空逼回：这处防空探明，地图上画出红色区域
                }
                pod.Note = occupied;
                d.Aborted++;
                if (NextSlot(-pod.Group) == 0)
                {
                    NotificationCenter.Post("drop_alert", GameText.Format(occupied == "antiair" ? "drop.notify.aborted_antiair" : "drop.notify.aborted_outpost", who), new Vector3(target.x, 0f, target.y));
                }
                StartReturn(state, pod, target, origin, now);
                return;
            }
            if (pod.Mission == MissionDelivery)
            {
                bool first = NextSlot(-pod.Group) == 0;
                if (pod.MachineId > 0)
                {
                    PlaceMachine(state, pod.MachineId, GroundSpot(state, target, NextSlot(pod.Group), 0f));
                    pod.MachineId = 0;
                }
                if (pod.CargoAmount > 0 && pod.ConstructionPack)
                {
                    // FG8-OUT-01（FGR-EXP-018）：施工包——先倒进落点附近还缺这种材料的施工现场，剩下的照常落在地上。
                    int poured = WorldSim.OwnOutpostService.PourPack(state, target, pod.CargoItem, pod.CargoAmount);
                    pod.CargoAmount -= poured;
                    if (poured > 0)
                    {
                        WorldSim.OwnOutpostService.CountPackLanded(state);
                    }
                }
                pod.ConstructionPack = false;
                if (pod.CargoAmount > 0)
                {
                    Vector2 spot = GroundSpot(state, target, NextSlot(pod.Group), 0f);
                    HomeValleyCargo.SpawnGroundItem(state, HomeValleyLayout.RegionId, spot, pod.CargoItem, pod.CargoAmount, "drop:" + pod.Serial + ":" + now);
                    pod.CargoItem = string.Empty;
                    pod.CargoAmount = 0;
                }
                pod.CargoItem = pod.CargoAmount > 0 ? pod.CargoItem : string.Empty;
                d.Delivered++;
                if (first)
                {
                    NotificationCenter.Post("drop_landed", GameText.Format("drop.notify.landed", GroupCount(state, pod.Group)), new Vector3(target.x, 0f, target.y));
                    GuidanceHooks.Raise(GuidanceHooks.DropFirstLanded);
                }
            }
            else if (pod.Mission == MissionRecall)
            {
                PickUp(state, pod, target);
            }
            StartReturn(state, pod, target, origin, now);
        }

        private static int GroupCount(CampaignState state, int group)
        {
            int n = 0;
            foreach (DropPodRecord p in Pods(state))
            {
                n += p.Group == group ? 1 : 0;
            }
            return n;
        }

        /// <summary>机器下舱：放到地面（家园所在的星球表面，下一次对账时在家园内核里建出单位）。</summary>
        private static void PlaceMachine(CampaignState state, int logicId, Vector2 at)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                return;
            }
            rec.WorldPosition = at;
            MachineRegistry.MoveToRegion(rec, HomeValleyLayout.RegionId);
        }

        /// <summary>回收：先接指定的机器（还在回收点附近、活着、没被别的舱接走）；指定的不在了就接附近另一台没人接的；指定的是 0（货物舱）就装地上的货物（一种，最多 drop.cargo_units 个）。</summary>
        private static void PickUp(CampaignState state, DropPodRecord pod, Vector2 point)
        {
            int id = 0;
            float reach = RecallRadius * 1.5f;
            if (pod.RecallMachineId > 0 && Eligible(state, pod.RecallMachineId, point, reach))
            {
                id = pod.RecallMachineId;
            }
            else if (pod.RecallMachineId > 0)
            {
                RecallableMachines(state, point, MachineScratch);
                foreach (MachineRecord m in MachineScratch)
                {
                    if (!ClaimedByOtherPod(state, pod, m.LogicId))
                    {
                        id = m.LogicId;
                        break;
                    }
                }
                MachineScratch.Clear();
            }
            if (id > 0)
            {
                Board(state, pod, id);
                ExpeditionMarchService.OnMembersBoardedPod(state, new[] { id });
                pod.RecallMachineId = id;
                return;
            }
            if (pod.RecallMachineId == 0 && TakeGroundCargo(state, pod, point))
            {
                return;
            }
            pod.Note = "nothing-here";
        }

        private static bool Eligible(CampaignState state, int logicId, Vector2 point, float reach)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord m) || m == null || !m.IsAlive || m.RegionId != HomeValleyLayout.RegionId || m.IsInFactory || IsInPod(state, logicId))
            {
                return false;
            }
            Vector2 at = MachineRegistry.TryGetLivePosition(logicId, out Vector2 live) ? live : m.WorldPosition;
            return Vector2.Distance(at, point) <= reach;
        }

        /// <summary>同一批里还没到的舱是不是指定要接这台（不抢别的舱要接的机器）。</summary>
        private static bool ClaimedByOtherPod(CampaignState state, DropPodRecord self, int logicId)
        {
            foreach (DropPodRecord p in Pods(state))
            {
                if (p != self && p.State == StateOutbound && p.Mission == MissionRecall && p.RecallMachineId == logicId)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TakeGroundCargo(CampaignState state, DropPodRecord pod, Vector2 point)
        {
            GroundItemRecord[] all = state.GroundItems ?? Array.Empty<GroundItemRecord>();
            float r = RecallRadius;
            string item = null;
            int need = CargoUnits;
            int got = 0;
            var keep = new List<GroundItemRecord>(all.Length);
            foreach (GroundItemRecord g in all)
            {
                bool match = g != null && g.RegionId == HomeValleyLayout.RegionId && g.Amount > 0 && ItemCatalog.TryGet(g.ResourceType, out ItemDef d) && d.Form == ItemForm.Solid
                             && Vector2.Distance(g.Position, point) <= r && (item == null || g.ResourceType == item) && got < need;
                if (!match)
                {
                    keep.Add(g);
                    continue;
                }
                item ??= g.ResourceType;
                int take = Math.Min(need - got, g.Amount);
                got += take;
                g.Amount -= take;
                if (g.Amount > 0)
                {
                    keep.Add(g);
                }
            }
            if (got == 0)
            {
                return false;
            }
            state.GroundItems = keep.ToArray();
            pod.CargoItem = item;
            pod.CargoAmount = got;
            return true;
        }

        /// <summary>
        /// 起飞返程：归属站还在（含只缺电 / 被禁用）→ 飞回去；被摧毁 / 拆掉且不在静默夜 → 改降最近的另一座能用、有空位的站；没有（或静默夜，自动驾驶按原定）→ 飞回原定地点（归属站的位置；站的记录没了就用出发点）。
        /// </summary>
        private static void StartReturn(CampaignState state, DropPodRecord pod, Vector2 from, Vector2 origin, long now)
        {
            BuildingRecord home = HomeGridService.FindBuilding(state, pod.StationId);
            Vector2 dest = home != null ? home.Position : origin;
            if (IsLost(home) && !Env.SilentNightService.IsActive(state))
            {
                BuildingRecord alt = NearestUsableStation(state, from, pod.StationId);
                if (alt != null)
                {
                    pod.State = StateReturning;
                    Divert(state, pod, alt, from, now);
                    return;
                }
            }
            pod.State = StateReturning;
            pod.FromX = from.x;
            pod.FromY = from.y;
            pod.ToX = dest.x;
            pod.ToY = dest.y;
            pod.StartTick = now;
            pod.ArriveTick = now + Math.Max(1, GameClock.TicksFor(LegSeconds(Vector2.Distance(from, dest))));
        }

        /// <summary>FG8-EXP-05 修复第 2 轮：投送舱落地时带回的区域关键物 / 黑匣子 / 遗产件数（自检读）。</summary>
        public static int QuestItemsLanded { get; private set; }

        /// <summary>
        /// FG8-EXP-05 修复第 2 轮（审查 P1，关闭 DEBT-FG8DROP01-01 的后半段）：被回收的机器随舱落回投送站——投送站是正式的回收落点，
        /// 它携带的区域关键物 / 黑匣子 / 遗产在落地这一刻带回（<see cref="Economy.CargoService.DeliverQuestItem"/>：区域标 Cleared、目标推进、黑匣子进陈列馆、遗产入保管库），
        /// 不再依赖投送站离核心 / 仓库是否在 loot.pick.depot_radius 以内。O(区域关键物)，只在落地这一刻。
        /// </summary>
        private static void DeliverCarriedQuestItems(CampaignState state, int logicId)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord m) || m == null || !m.IsAlive)
            {
                return;
            }
            foreach (RegionQuestItemRecord q in state.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (q != null && q.State == RegionQuestItemState.Carried && q.CarrierLogicId == logicId)
                {
                    Economy.CargoService.DeliverQuestItem(state, q, m);
                    QuestItemsLanded++;
                }
            }
        }

        /// <summary>回到站里：机器在站旁下舱（回收的按远征卸货规则卸货），货物入库（放不下的落在站旁），之后冷却。站的建筑记录没了（拆掉）：在原地卸下，舱变回物品。</summary>
        private static void OnReturnArrive(CampaignState state, DropPodRecord pod, long now)
        {
            DropPodState d = StateOf(state);
            BuildingRecord b = HomeGridService.FindBuilding(state, pod.StationId);
            var at = new Vector2(pod.ToX, pod.ToY);
            float ring = b != null ? 4.5f : 1.5f;
            int slot = NextSlot(pod.Group);
            if (pod.MachineId > 0)
            {
                int id = pod.MachineId;
                PlaceMachine(state, id, GroundSpot(state, at, slot, ring));
                pod.MachineId = 0;
                if (pod.Mission == MissionRecall)
                {
                    ReturnedScratch.Add(id);
                    d.Recovered++;
                    // 修复第 3 轮（审查 P1）：远征还没结束（分批接回、其余成员还在外面）时，先把这台记进进行中的结算（货舱货按远征卸货规则），再带回关键物。
                    Economy.ExpeditionSettlementService.OnMemberLanded(state, id);
                    DeliverCarriedQuestItems(state, id);
                }
            }
            if (pod.CargoAmount > 0 && ItemCatalog.TryGet(pod.CargoItem, out ItemDef item))
            {
                int added = HomeInventory.Add(state, item, pod.CargoAmount);
                if (added < pod.CargoAmount)
                {
                    HomeValleyCargo.SpawnGroundItem(state, HomeValleyLayout.RegionId, GroundSpot(state, at, slot, ring), item.Id, pod.CargoAmount - added, "drop:back:" + pod.Serial + ":" + now);
                }
                HomeInventory.Touch();
            }
            pod.CargoItem = string.Empty;
            pod.CargoAmount = 0;
            bool firstOfGroup = NextSlot(-1000000 - pod.Group) == 0;
            if (firstOfGroup && pod.Mission == MissionRecall)
            {
                NotificationCenter.Post("drop_landed", GameText.Format("drop.notify.recalled", GroupCount(state, pod.Group), b != null ? BuildingOps.NameOf(b) : GameText.Get("drop.station.name")),
                    new Vector3(at.x, 0f, at.y));
            }
            pod.Mission = MissionNone;
            pod.RecallMachineId = 0;
            pod.FuelMl = 0;
            if (b == null || FindStation(state, pod.StationId) == null)
            {
                _podsToRemove.Add(pod.Serial);
                ReturnPodItems(state, 1, at);
                return;
            }
            pod.State = StateCooling;
            pod.CooldownUntilTick = now + Math.Max(1, GameClock.TicksFor(CooldownSeconds));
        }
    }
}
