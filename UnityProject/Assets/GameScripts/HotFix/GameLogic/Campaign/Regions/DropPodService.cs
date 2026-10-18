using System;
using System.Collections.Generic;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG8-DROP-01（FG08 FGR-EXP-013 投送站、014 投送、015 回收、016 投送与静默夜；FG13 FGU-46；FGT-EXP-003）：投送站与投送舱的唯一业务入口。存档真相在 <see cref="GridState.Drop"/>。
    /// <para>投送站（5×5，研发“远征 · 投送站”解锁，T1 容纳 4 个舱、T2 8 个）从仓库装入组件工坊生产的投送舱（自动补满可在面板关掉），燃油从站边一格的管线进来存在站里。</para>
    /// <para>投送：每个舱载 1 台机器（连同货舱）或 drop.cargo_units 个单位的货物，落点必须已探索、有着陆条件（平地、不在敌方防空覆盖内、不在危害带中心、没有敌方据点）、在投送距离内；
    /// 飞行 = 距离 ÷ drop.speed_cells + drop.landing_seconds 秒着陆；燃油按往返距离付（每舱每 1,000 格 drop.fuel_per_1000 升）；回到站里冷却 drop.cooldown_seconds 秒。</para>
    /// <para>回收：在有着陆条件的地点呼叫，舱从站里飞过去接上指定的机器（连同货舱；多出来的舱装地上的货物），回到站里按远征卸货规则卸货。</para>
    /// <para>静默夜不能发起投送或回收（没有信号）；已经在飞的舱按自动驾驶飞完、降落在原定地点（不改降）。落点在飞行途中被敌人占领（防空覆盖 / 据点）时不降落，带着载荷返回。
    /// 投送站在舱飞行途中被摧毁：返程的舱改降到最近的另一座能用的投送站；没有别的投送站时降落在原定地点（被毁的站旁）。</para>
    /// <para>只在世界模拟步里推进（只看步序号，与镜头 / 观察无关，FGR-BASE-021）。每步 O(1)（到下一个事件步才 O(舱数)，舱数 ≤ 站数 × 8）；每 drop.sync_seconds 对账一次 O(建筑数 + 站数)。</para>
    /// </summary>
    public static partial class DropPodService
    {
        public const string StationTypeId = "drop_station";
        public const string PodItemId = "drop_pod";
        /// <summary>机器在舱里时机器记录的区域（不在任何地点的战斗内核里：不交战、不能被接入、不接工作单）。</summary>
        public const string InPodRegionId = "drop_pod";

        public const int StateDocked = 0;
        public const int StateCooling = 1;
        public const int StateOutbound = 2;
        public const int StateReturning = 3;

        public const int MissionNone = 0;
        public const int MissionDelivery = 1;
        public const int MissionRecall = 2;

        /// <summary>界面刷新用：任何站 / 舱状态变化 +1。</summary>
        public static int Revision { get; private set; } = 1;

        private static void Touch() => Revision++;

        // ── 自检读点（本会话）──
        public static int SyncCount { get; private set; }
        public static int EventPasses { get; private set; }
        public static int LastRejectCount { get; private set; }
        public static string LastReject { get; private set; } = string.Empty;

        private static long _nextEventTick;
        private static bool _builtHook;

        /// <summary>新会话（新建 / 读档 / 回主菜单）：运行时缓存清空（存档数据不动）。</summary>
        public static void ResetSessionState()
        {
            _nextEventTick = 0;
            _builtHook = false;
            SyncCount = 0;
            EventPasses = 0;
            LastRejectCount = 0;
            LastReject = string.Empty;
            Touch();
        }

        // ─────────────────────────────── 数值（fg.TbHomeTuning，fgdata_drop.py）───────────────────────────────

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;

        public static float RangeCells => Tuning("drop.range_cells", 3000f);
        public static float SpeedCells => Tuning("drop.speed_cells", 60f);
        public static float LandingSeconds => Tuning("drop.landing_seconds", 5f);
        public static float FuelPer1000 => Tuning("drop.fuel_per_1000", 30f);
        public static float CooldownSeconds => Tuning("drop.cooldown_seconds", 60f);
        public static int CargoUnits => Mathf.Max(1, Mathf.RoundToInt(Tuning("drop.cargo_units", 20f)));
        public static float LoadRadius => Tuning("drop.load_radius", 12f);
        public static float RecallRadius => Tuning("drop.recall_radius", 10f);
        public static int FlatRadius => Mathf.Max(0, Mathf.RoundToInt(Tuning("drop.landing.flat_radius", 3f)));
        public static float HazardCenterRatio => Mathf.Clamp01(Tuning("drop.landing.hazard_center_ratio", 0.5f));
        public static float OutpostClearCells => Tuning("drop.landing.outpost_clear_cells", 16f);
        public static float TankLiters => Tuning("drop.station.fuel_tank_liters", 1200f);
        public static int FuelLpm => Mathf.Max(1, Mathf.RoundToInt(Tuning("drop.station.fuel_lpm", 240f)));
        public static float SyncSeconds => Tuning("drop.sync_seconds", 1f);

        /// <summary>防空覆盖半径（格）：铸造对空高炮 / 白潮护卫无人机，按据点等级加 drop.antiair.radius_per_tier。不是这两个阵营的据点 = 0。</summary>
        public static float AntiAirRadius(OutpostRecord o)
        {
            if (o == null || o.Destroyed)
            {
                return 0f;
            }
            float baseR = o.TerritoryId == "foundry" ? Tuning("drop.antiair.radius_foundry", 70f)
                : o.TerritoryId == "white_tide" ? Tuning("drop.antiair.radius_white_tide", 55f) : 0f;
            return baseR <= 0f ? 0f : baseR + Mathf.Max(0, o.Tier - 1) * Tuning("drop.antiair.radius_per_tier", 5f);
        }

        public static float MaxAntiAirRadius => Mathf.Max(Tuning("drop.antiair.radius_foundry", 70f), Tuning("drop.antiair.radius_white_tide", 55f)) + 10f * Tuning("drop.antiair.radius_per_tier", 5f);

        // ─────────────────────────────── 存档域 ───────────────────────────────

        public static DropPodState StateOf(CampaignState s) => s?.Grid?.Drop;

        public static IReadOnlyList<DropStationRecord> Stations(CampaignState s) => StateOf(s)?.Stations ?? Array.Empty<DropStationRecord>();

        public static IReadOnlyList<DropPodRecord> Pods(CampaignState s) => StateOf(s)?.Pods ?? Array.Empty<DropPodRecord>();

        /// <summary>读档 / 新档补全（<see cref="CampaignFgStateDomains.EnsureAll"/> 调）：数组补空、重复站点只留第一条、坏值钳回、重复序号重编、序号计数补齐。</summary>
        public static void EnsureState(CampaignState s)
        {
            if (s?.Grid == null)
            {
                return;
            }
            SignalUplinkProviderInstall();
            DropPodState d = s.Grid.Drop ??= new DropPodState();
            d.Stations ??= Array.Empty<DropStationRecord>();
            d.Pods ??= Array.Empty<DropPodRecord>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var keep = new List<DropStationRecord>(d.Stations.Length);
            foreach (DropStationRecord st in d.Stations)
            {
                if (st == null || string.IsNullOrEmpty(st.BuildingId) || !seen.Add(st.BuildingId))
                {
                    continue;
                }
                st.HeldFuelMl = Math.Max(0, st.HeldFuelMl);
                st.ConsumerId = Math.Max(0, st.ConsumerId);
                st.Launches = Math.Max(0, st.Launches);
                keep.Add(st);
            }
            if (keep.Count != d.Stations.Length)
            {
                d.Stations = keep.ToArray();
            }
            var serials = new HashSet<int>();
            var pods = new List<DropPodRecord>(d.Pods.Length);
            int max = 0;
            foreach (DropPodRecord p in d.Pods)
            {
                if (p == null)
                {
                    continue;
                }
                p.StationId ??= string.Empty;
                p.CargoItem ??= string.Empty;
                p.Note ??= string.Empty;
                if (p.State < StateDocked || p.State > StateReturning)
                {
                    p.State = StateDocked;
                }
                if (p.Mission < MissionNone || p.Mission > MissionRecall)
                {
                    p.Mission = MissionNone;
                }
                bool badLeg = float.IsNaN(p.FromX) || float.IsNaN(p.FromY) || float.IsNaN(p.ToX) || float.IsNaN(p.ToY) || float.IsNaN(p.TargetX) || float.IsNaN(p.TargetY);
                bool flying = p.State == StateOutbound || p.State == StateReturning;
                if (badLeg || (!flying && (p.MachineId > 0 || p.CargoAmount > 0)))
                {
                    // 航线坏了，或者在站 / 冷却的舱里还装着机器 / 货物（坏档）：按“此刻回到归属站”处理——下一个事件步走正常的回站卸载
                    // （OnReturnArrive：机器在站旁下舱、货物入库，之后冷却），机器不会困在舱里。
                    Vector2 home = StationSpot(s, keep, p.StationId);
                    p.FromX = p.ToX = p.TargetX = home.x;
                    p.FromY = p.ToY = p.TargetY = home.y;
                    p.State = StateReturning;
                    p.StartTick = 0;
                    p.ArriveTick = 0;
                }
                if (p.ArriveTick < p.StartTick)
                {
                    p.ArriveTick = p.StartTick;
                }
                p.CargoAmount = Math.Max(0, p.CargoAmount);
                if (p.CargoAmount == 0)
                {
                    p.CargoItem = string.Empty;
                }
                p.FuelMl = Math.Max(0, p.FuelMl);
                if (p.Serial <= 0 || !serials.Add(p.Serial))
                {
                    p.Serial = 0;
                }
                max = Math.Max(max, p.Serial);
                pods.Add(p);
            }
            if (d.NextPodSerial <= max)
            {
                d.NextPodSerial = max + 1;
            }
            foreach (DropPodRecord p in pods)
            {
                if (p.Serial <= 0)
                {
                    p.Serial = d.NextPodSerial++;
                    serials.Add(p.Serial);
                }
            }
            if (pods.Count != d.Pods.Length)
            {
                d.Pods = pods.ToArray();
            }
            if (d.NextGroupSerial < 1)
            {
                d.NextGroupSerial = 1;
            }
            d.Launches = Math.Max(0, d.Launches);
            d.Recalls = Math.Max(0, d.Recalls);
            d.Delivered = Math.Max(0, d.Delivered);
            d.Recovered = Math.Max(0, d.Recovered);
            d.Diverted = Math.Max(0, d.Diverted);
            d.Aborted = Math.Max(0, d.Aborted);
            var known = new List<string>();
            foreach (string id in d.KnownAntiAir ?? Array.Empty<string>())
            {
                if (!string.IsNullOrEmpty(id) && !known.Contains(id))
                {
                    known.Add(id);
                }
            }
            d.KnownAntiAir = known.ToArray();
            _nextEventTick = 0;
        }

        /// <summary>坏档修复用：舱归属站的位置（站记录 → 建筑记录 → 第一座站 → 原点）。</summary>
        private static Vector2 StationSpot(CampaignState s, List<DropStationRecord> stations, string stationId)
        {
            foreach (DropStationRecord st in stations)
            {
                if (st.BuildingId == stationId)
                {
                    return new Vector2(st.X, st.Y);
                }
            }
            foreach (BuildingRecord b in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.BuildingId == stationId)
                {
                    return b.Position;
                }
            }
            return stations.Count > 0 ? new Vector2(stations[0].X, stations[0].Y) : Vector2.zero;
        }

        /// <summary>FG1-SIG-03 的“投送途中”判定接口（DEBT-FG1SIG03-02）：机器在舱里 = 拒绝接入并写明原因。</summary>
        private static void SignalUplinkProviderInstall()
        {
            Signal.SignalUplinkService.InDeliveryProvider ??= IsInPod;
        }

        public static DropStationRecord FindStation(CampaignState s, string buildingId)
        {
            if (string.IsNullOrEmpty(buildingId))
            {
                return null;
            }
            foreach (DropStationRecord st in Stations(s))
            {
                if (st.BuildingId == buildingId)
                {
                    return st;
                }
            }
            return null;
        }

        public static DropPodRecord FindPod(CampaignState s, int serial)
        {
            foreach (DropPodRecord p in Pods(s))
            {
                if (p.Serial == serial)
                {
                    return p;
                }
            }
            return null;
        }

        /// <summary>这台机器在不在某个投送舱里（在飞或刚落回站里还没卸下）。O(舱数)。</summary>
        public static bool IsInPod(int logicId) => IsInPod(CampaignSession.Current, logicId);

        public static bool IsInPod(CampaignState s, int logicId)
        {
            if (logicId <= 0)
            {
                return false;
            }
            foreach (DropPodRecord p in Pods(s))
            {
                if (p.MachineId == logicId)
                {
                    return true;
                }
            }
            return false;
        }

        public static DropPodRecord PodCarrying(CampaignState s, int logicId)
        {
            foreach (DropPodRecord p in Pods(s))
            {
                if (logicId > 0 && p.MachineId == logicId)
                {
                    return p;
                }
            }
            return null;
        }

        public static bool IsStationType(string typeId) => typeId == StationTypeId;

        public static bool IsStation(BuildingRecord b) => b != null && b.BuildingTypeId == StationTypeId && !HomeGridService.IsRelocationGhost(b);

        /// <summary>能发射 / 接收：建成、运转、有电（被摧毁 = 虚影、禁用、缺电都不行）。</summary>
        public static bool IsUsable(BuildingRecord b) =>
            IsStation(b) && b.ConstructionState == BuildingConstructionState.Operational
                         && (b.PowerState == BuildingPowerState.Powered || b.PowerState == BuildingPowerState.NotApplicable);

        private static bool IsBuilt(BuildingRecord b) =>
            IsStation(b) && (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled
                             || b.ConstructionState == BuildingConstructionState.Damaged || b.ConstructionState == BuildingConstructionState.Destroyed);

        /// <summary>这座站能容纳几个舱（等级效果值：T1 4、T2 8）。</summary>
        public static int Capacity(BuildingRecord b)
        {
            if (b == null)
            {
                return 0;
            }
            GameConfig.fg.BuildingTier row = BuildingOps.TierRow(StationTypeId, BuildingOps.TierOf(b));
            return row != null ? Mathf.Max(1, Mathf.RoundToInt(row.Value)) : 4;
        }

        public static int PodCount(CampaignState s, string stationId)
        {
            int n = 0;
            foreach (DropPodRecord p in Pods(s))
            {
                n += p.StationId == stationId ? 1 : 0;
            }
            return n;
        }

        public static int CountState(CampaignState s, string stationId, int state)
        {
            int n = 0;
            foreach (DropPodRecord p in Pods(s))
            {
                n += p.StationId == stationId && p.State == state ? 1 : 0;
            }
            return n;
        }

        /// <summary>冷却中的舱里最早还要多少游戏秒才能用（没有冷却中的 = -1）。</summary>
        public static double SoonestReadySeconds(CampaignState s, string stationId)
        {
            long best = long.MaxValue;
            foreach (DropPodRecord p in Pods(s))
            {
                if (p.StationId == stationId && p.State == StateCooling && p.CooldownUntilTick < best)
                {
                    best = p.CooldownUntilTick;
                }
            }
            return best == long.MaxValue ? -1 : Math.Max(0, best - GameClock.Ticks) / (double)Math.Max(1, GameClock.StepHz);
        }

        // ─────────────────────────────── 燃油 ───────────────────────────────

        public static long FuelMl(DropStationRecord st)
        {
            if (st == null)
            {
                return 0;
            }
            long buffered = st.ConsumerId > 0 && PipeNetworkService.IsRunning ? PipeNetworkService.Kernel.ConsumerBuffer(st.ConsumerId) : 0;
            return st.HeldFuelMl + Math.Max(0, buffered);
        }

        public static float FuelLiters(DropStationRecord st) => FuelMl(st) / 1000f;

        private static long TakeFuel(DropStationRecord st, long ml)
        {
            long need = ml;
            if (need > 0 && st.HeldFuelMl > 0)
            {
                long t = Math.Min(need, st.HeldFuelMl);
                st.HeldFuelMl -= t;
                need -= t;
            }
            if (need > 0 && st.ConsumerId > 0 && PipeNetworkService.IsRunning)
            {
                need -= PipeNetworkService.Kernel.TakeConsumerBuffer(st.ConsumerId, need);
            }
            return ml - need;
        }

        /// <summary>每舱燃油（毫升，往返 2 × 距离）：每 1,000 格 drop.fuel_per_1000 升。</summary>
        public static long FuelPerPodMl(float distance) => (long)Math.Ceiling(2.0 * distance / 1000.0 * FuelPer1000 * 1000.0);

        /// <summary>单程飞行（游戏秒）= 距离 ÷ 速度 + 着陆。</summary>
        public static double LegSeconds(float distance) => distance / Math.Max(1f, SpeedCells) + LandingSeconds;

        // ─────────────────────────────── 防空与着陆条件 ───────────────────────────────

        private static readonly List<OutpostRecord> OutpostScratch = new List<OutpostRecord>(16);

        /// <summary>这一格在不在敌方防空覆盖里（铸造 / 白潮未被摧毁的据点，半径见 <see cref="AntiAirRadius"/>）。返回覆盖它的据点（没有 = null）。</summary>
        public static OutpostRecord AntiAirAt(CampaignState s, float x, float y)
        {
            OutpostScratch.Clear();
            WorldOutpostSystem.OutpostsNear(s, x, y, MaxAntiAirRadius, OutpostScratch);
            OutpostRecord hit = null;
            float best = float.MaxValue;
            foreach (OutpostRecord o in OutpostScratch)
            {
                float r = AntiAirRadius(o);
                if (r <= 0f)
                {
                    continue;
                }
                float d = Vector2.Distance(new Vector2(o.CellX, o.CellY), new Vector2(x, y));
                if (d <= r && d < best)
                {
                    best = d;
                    hit = o;
                }
            }
            OutpostScratch.Clear();
            return hit;
        }

        /// <summary>
        /// 防空覆盖圈（战略地图红色区域）：视野范围内、已探明的铸造 / 白潮据点（据点格已探索，或防空已在瞄准 / 被拒 / 被逼回时探明，见 <see cref="IsAntiAirKnown"/>）。
        /// 与着陆拒绝同一口径：落点能被防空拒绝的地方，选点悬停时就已探明、画出红色区域。<paramref name="into"/> 先清空。
        /// </summary>
        public static void AntiAirCircles(CampaignState s, float cx, float cy, float radius, List<(Vector2 Center, float Radius, string OutpostId)> into)
        {
            into.Clear();
            OutpostScratch.Clear();
            WorldOutpostSystem.OutpostsNear(s, cx, cy, radius + MaxAntiAirRadius, OutpostScratch);
            foreach (OutpostRecord o in OutpostScratch)
            {
                float r = AntiAirRadius(o);
                if (r > 0f && IsAntiAirKnown(s, o))
                {
                    into.Add((new Vector2(o.CellX, o.CellY), r, o.OutpostId));
                }
            }
            OutpostScratch.Clear();
        }

        /// <summary>据点格已探索（玩家见过这座据点）。</summary>
        public static bool IsOutpostSeen(CampaignState s, OutpostRecord o)
        {
            HomeGridMap map = o != null ? HomeGridService.MapFor(s) : null;
            return map != null && map.IsExploredNoLoad(new GridCell(o.CellX, o.CellY));
        }

        /// <summary>这座据点的防空已探明（据点格已探索，或在 <see cref="DropPodState.KnownAntiAir"/> 里）。</summary>
        public static bool IsAntiAirKnown(CampaignState s, OutpostRecord o)
        {
            if (o == null)
            {
                return false;
            }
            if (IsOutpostSeen(s, o))
            {
                return true;
            }
            string[] known = StateOf(s)?.KnownAntiAir;
            return known != null && Array.IndexOf(known, o.OutpostId) >= 0;
        }

        /// <summary>探明一处敌方防空（瞄准时 / 发射被拒 / 舱被逼回）：之后战略地图画出它的红色区域。返回是不是新探明的。</summary>
        public static bool RevealAntiAir(CampaignState s, string outpostId)
        {
            DropPodState d = StateOf(s);
            if (d == null || string.IsNullOrEmpty(outpostId))
            {
                return false;
            }
            d.KnownAntiAir ??= Array.Empty<string>();
            if (Array.IndexOf(d.KnownAntiAir, outpostId) >= 0)
            {
                return false;
            }
            var list = new List<string>(d.KnownAntiAir) { outpostId };
            d.KnownAntiAir = list.ToArray();
            Touch();
            return true;
        }

        /// <summary>预览被防空拒绝时探明那处防空（面板 / 选点悬停 / 发射被拒调；不改世界，只记玩家的情报）。</summary>
        public static void RevealAntiAir(CampaignState s, DropPreview p)
        {
            if (p != null && p.AntiAir)
            {
                RevealAntiAir(s, p.AntiAirOutpostId);
            }
        }

        /// <summary>给玩家看的据点名：据点格已探索写名字，还在迷雾里写“未探明的敌方据点”（不泄露迷雾里的据点）。</summary>
        public static string OutpostLabel(CampaignState s, OutpostRecord o) =>
            IsOutpostSeen(s, o) ? TerritoryControlService.OutpostName(o) : GameText.Get("drop.outpost.unknown");

        private static OutpostRecord EnemyOutpostNear(CampaignState s, float x, float y)
        {
            OutpostScratch.Clear();
            float clear = OutpostClearCells;
            WorldOutpostSystem.OutpostsNear(s, x, y, clear, OutpostScratch);
            OutpostRecord hit = null;
            foreach (OutpostRecord o in OutpostScratch)
            {
                if (o != null && !o.Destroyed && Vector2.Distance(new Vector2(o.CellX, o.CellY), new Vector2(x, y)) <= clear)
                {
                    hit = o;
                    break;
                }
            }
            OutpostScratch.Clear();
            return hit;
        }

        /// <summary>
        /// 着陆条件（FGR-EXP-014）：已探索、平地（落点周围 drop.landing.flat_radius 格都是可走地面、没有建筑）、不在危害带 / 危害区中心、不在敌方防空覆盖内、旁边没有敌方据点。
        /// 返回原因码（null = 能降落）；<paramref name="detail"/> 是给玩家看的一句（防空写是哪个据点）。
        /// </summary>
        public static string CheckLanding(CampaignState s, float x, float y, out string detail, bool requireExplored = true) =>
            CheckLanding(s, x, y, out detail, out _, requireExplored);

        /// <summary>同上；<paramref name="antiAir"/> = 拒绝原因是防空时覆盖落点的据点（其余 = null）。</summary>
        public static string CheckLanding(CampaignState s, float x, float y, out string detail, out OutpostRecord antiAir, bool requireExplored = true)
        {
            detail = null;
            antiAir = null;
            HomeGridMap map = HomeGridService.MapFor(s);
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            if (map == null || ctx == null)
            {
                detail = GameText.Get("drop.reason.no_world");
                return "no-world";
            }
            var cell = new GridCell(Mathf.RoundToInt(x), Mathf.RoundToInt(y));
            if (requireExplored && !map.IsExploredNoLoad(cell))
            {
                detail = GameText.Get("drop.reason.unexplored");
                return "unexplored";
            }
            int r = FlatRadius;
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    var c = new GridCell(cell.X + dx, cell.Y + dy);
                    if (!Walkable(ctx, map, c))
                    {
                        detail = GameText.Format("drop.reason.not_flat", r * 2 + 1);
                        return "not-flat";
                    }
                    if (map.OccupantAt(c) != null)
                    {
                        detail = GameText.Format("drop.reason.occupied_building", r * 2 + 1);
                        return "building";
                    }
                }
            }
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (plan != null)
            {
                PlannedHazardZone z = plan.HazardZoneAt(cell.X, cell.Y);
                if (z != null && Vector2.Distance(new Vector2(z.CenterX, z.CenterY), new Vector2(x, y)) <= z.Radius * HazardCenterRatio)
                {
                    detail = GameText.Get("drop.reason.hazard_center");
                    return "hazard-center";
                }
                PlannedTerritory bt = plan.HazardAt(cell.X, cell.Y);
                if (bt != null && bt.IsFaction && bt.HazardWidth > 0)
                {
                    float d = Vector2.Distance(new Vector2(bt.CenterX, bt.CenterY), new Vector2(x, y));
                    float mid = (bt.Radius + bt.OuterRadius) * 0.5f;
                    if (Mathf.Abs(d - mid) <= bt.HazardWidth * 0.5f * HazardCenterRatio)
                    {
                        detail = GameText.Get("drop.reason.hazard_center");
                        return "hazard-center";
                    }
                }
            }
            OutpostRecord aa = AntiAirAt(s, x, y);
            if (aa != null)
            {
                antiAir = aa;
                detail = GameText.Format("drop.reason.antiair", OutpostLabel(s, aa));
                return "antiair";
            }
            OutpostRecord enemy = EnemyOutpostNear(s, x, y);
            if (enemy != null)
            {
                detail = GameText.Format("drop.reason.enemy_outpost", OutpostLabel(s, enemy));
                return "enemy-outpost";
            }
            return null;
        }

        /// <summary>地面单位能走的格（当前地形，含玩家修改：悬崖 / 水面不行）。</summary>
        private static bool Walkable(WorldGenContext ctx, HomeGridMap map, GridCell c)
        {
            byte t = map.GetTerrain(c);
            return t != ctx.Source.Params.CodeCliff && t != ctx.Source.Params.CodeWater;
        }

        /// <summary>飞行途中落点“被敌人占领”：防空覆盖或敌方据点出现在落点（地形 / 迷雾不复查：起飞时已经确认过）。</summary>
        private static string CheckOccupied(CampaignState s, float x, float y, out string detail, out OutpostRecord antiAir)
        {
            detail = null;
            antiAir = AntiAirAt(s, x, y);
            if (antiAir != null)
            {
                detail = OutpostLabel(s, antiAir);
                return "antiair";
            }
            OutpostRecord enemy = EnemyOutpostNear(s, x, y);
            if (enemy != null)
            {
                detail = OutpostLabel(s, enemy);
                return "enemy-outpost";
            }
            return null;
        }

        /// <summary>落点 / 站旁一处可以放下第 <paramref name="index"/> 台机器的地面（可走、没有建筑；按环向外找，确定性）。找不到就用中心。</summary>
        public static Vector2 GroundSpot(CampaignState s, Vector2 center, int index, float startRadius)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            int found = 0;
            for (int ring = 0; ring < 10; ring++)
            {
                float rr = startRadius + ring * 2f;
                int steps = ring == 0 && startRadius < 0.5f ? 1 : 8 + ring * 4;
                for (int k = 0; k < steps; k++)
                {
                    float ang = k * Mathf.PI * 2f / steps;
                    Vector2 p = center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * rr;
                    var c = new GridCell(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
                    if (ctx == null || map == null || !Walkable(ctx, map, c) || map.OccupantAt(c) != null)
                    {
                        continue;
                    }
                    if (found++ == index)
                    {
                        return p;
                    }
                }
            }
            return center;
        }

        // ─────────────────────────────── 载荷候选 ───────────────────────────────

        /// <summary>能装进舱的机器：在这座站旁（drop.load_radius 内）、在家园所在的星球表面、活着、不在工厂里、不在舱里、不在行军名单里。O(机器数)。</summary>
        public static void LoadableMachines(CampaignState s, BuildingRecord station, List<MachineRecord> into)
        {
            into.Clear();
            if (station == null)
            {
                return;
            }
            float r = LoadRadius + 2.5f;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.RegionId != HomeValleyLayout.RegionId || m.IsInFactory || IsInPod(s, m.LogicId) || ExpeditionMarchService.IsMarchMember(s, m.LogicId))
                {
                    continue;
                }
                Vector2 at = MachineRegistry.TryGetLivePosition(m.LogicId, out Vector2 live) ? live : m.WorldPosition;
                if (Vector2.Distance(at, station.Position) <= r)
                {
                    into.Add(m);
                }
            }
            into.Sort((a, b) => a.LogicId.CompareTo(b.LogicId));
        }

        /// <summary>能投送的货物：仓库里的固体（能上传送带 / 进仓库的），按排序。</summary>
        public static void CargoChoices(CampaignState s, List<ItemDef> into)
        {
            into.Clear();
            foreach (ItemDef d in ItemCatalog.Items)
            {
                if (d != null && d.Form == ItemForm.Solid && d.Id != PodItemId && HomeInventory.Stock(s, d) > 0)
                {
                    into.Add(d);
                }
            }
        }

        /// <summary>回收点附近能接回来的机器（drop.recall_radius 内、星球表面、活着、不在工厂里、不在舱里）。</summary>
        public static void RecallableMachines(CampaignState s, Vector2 point, List<MachineRecord> into)
        {
            into.Clear();
            float r = RecallRadius;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.RegionId != HomeValleyLayout.RegionId || m.IsInFactory || IsInPod(s, m.LogicId))
                {
                    continue;
                }
                Vector2 at = MachineRegistry.TryGetLivePosition(m.LogicId, out Vector2 live) ? live : m.WorldPosition;
                if (Vector2.Distance(at, point) <= r)
                {
                    into.Add(m);
                }
            }
            into.Sort((a, b) => a.LogicId.CompareTo(b.LogicId));
        }

        /// <summary>回收点附近地上的货物件数（家园所在表面的地面物品，drop.recall_radius 内）。</summary>
        public static int GroundCargoNear(CampaignState s, Vector2 point)
        {
            int n = 0;
            float r = RecallRadius;
            foreach (GroundItemRecord g in s?.GroundItems ?? Array.Empty<GroundItemRecord>())
            {
                if (g != null && g.RegionId == HomeValleyLayout.RegionId && g.Amount > 0 && ItemCatalog.TryGet(g.ResourceType, out ItemDef d) && d.Form == ItemForm.Solid
                    && Vector2.Distance(g.Position, point) <= r)
                {
                    n += g.Amount;
                }
            }
            return n;
        }

        internal static void RecordReject(string message)
        {
            LastRejectCount++;
            LastReject = message ?? string.Empty;
        }

        internal static void RaiseBuiltHookOnce()
        {
            if (!_builtHook)
            {
                _builtHook = true;
                GuidanceHooks.Raise(GuidanceHooks.DropStationFirstBuilt);
            }
        }

        internal static void ScheduleEventCheck() => _nextEventTick = 0;
    }
}
