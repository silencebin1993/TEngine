using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Endgame
{
    /// <summary>
    /// FG13-END-04 白潮滩头（登陆场；FG11 FGR-END-021、FG17 FGR-GEN-041；验收 FGT-END-005）。唯一写入口（<see cref="BeachheadState"/> 与滩头的敌人记录）。
    ///
    /// - 着陆：撑过清洗波次（事件账本 <see cref="PurgeWaveService.ClearedEventId"/>）后的第一个世界步。位置只由规划层决定（B25）：规划层预留区里的锚点 white_tide_landing
    ///   （按种子放下、开局校验可达，FGR-GEN-041）是原点与朝向，手工模板 <see cref="TemplateId"/>（fg.TbAnchorTemplatePoint：旗舰降落区、3 个轨道锚点、登陆信标、屏蔽塔、
    ///   护卫无人机、净化步行机）按它落到星球格上（FG8-GEN-02 的 <see cref="TerritoryPlanner.ToWorld"/>，不另造生成器）；点位落在悬崖 / 水面上的就近挪到可走格（确定）。
    ///   编成 = fg.TbEnemyEncounter 区域 <see cref="RegionId"/> 的 base 行，全部播种成星球战斗内核里的区域敌人记录（区域 = 家园 / 星球，随存档）；
    ///   登陆信标周围 beachhead.scorch_radius 格写成灭活焦土（来源 = 信标）。着陆后自动存档一次（<see cref="SaveReason.BeachheadLanded"/>）。揭示 / 着陆之前预留区没有任何东西。
    /// - 目标：敌人表职能 structure 的滩头单位（3 个轨道锚点 + 登陆信标，按表列识别，不认 ID）。全部摧毁 = 切断投送：事件账本 <see cref="ClearedEventId"/>（主线“切断投送”完成）
    ///   + <see cref="ClearedEvent"/>（FG13-END-05 首领战从这里开始）。
    /// - 持续投送：还有目标立着时，每 beachhead.drop_interval_seconds × 目标总数 ÷ 存活目标数 游戏秒落一个登陆舱（敌人表读法 anchor_drop 的白潮行），落在信标旁、
    ///   信标没了落在剩下的锚点旁；舱落地投放的单位走 FG13-FAC-01 的区域投放（<see cref="CombatSite"/> SpawnPodCargo）。滩头投送来的单位活着的达到上限时这一次跳过。
    /// - 防空：滩头存活的、职能 escort 的单位（护卫无人机）形成防空覆盖（<see cref="DropPodService.AntiAirAt"/> 唯一判定入口读 <see cref="TryEscortCover"/>）；全部打掉后投送舱才能落进来。
    /// - 屏蔽塔：FG13-FAC-01 的接入判定（表列 flBlockRange）原样生效，这里只负责把塔摆进滩头。
    /// - 家园骚扰上限：着陆到切断投送之间，突袭导演每个计划的等级不超过 <see cref="HarassLevelCap"/>（= 1）；着陆时还没预警的计划一并降级重编。
    /// - 失败：核心被摧毁 → 不再推进（失败页写明读档后接着打，<see cref="FailureRetryText"/>）；读回的存档里已摧毁的目标保留。
    /// 只看统一时钟步数（暂停不走、倍速同比），与观察无关（星球内核一直在跑）。每步 O(1)；每游戏秒一次 O(滩头记录数)；着陆一次 O(编成 + 焦土格)。
    /// </summary>
    public static class BeachheadService
    {
        /// <summary>编成表里的区域 ID，也是规划层锚点 ID（fg.TbAnchorSite white_tide_landing）。</summary>
        public const string RegionId = "white_tide_landing";
        public const string AnchorId = "white_tide_landing";
        public const string TemplateId = "white_tide_beachhead";
        public const string BeaconPoint = "wt_beacon";
        public const string ZonePoint = "wt_flagship_zone";
        public const string EntryPoint = "wt_entry";
        /// <summary>滩头敌人记录的实例 ID 前缀（编成行 ID 都是 wt_…；投送的舱 wt_pod_序号，舱投放的单位再接 .c序号）。</summary>
        public const string Prefix = "wt_";
        public const string PodPrefix = "wt_pod_";
        public const string ClearedEventId = "endgame_beachhead_cleared";
        public const string LandedEvent = "Endgame.BeachheadLanded";
        public const string ClearedEvent = "Endgame.BeachheadCleared";
        public const string NotifyType = RevealService.OrbitalNotifyType;
        public const string RoleEscort = "escort";
        public const string BehaviorAnchorDrop = "anchor_drop";

        /// <summary>着陆后的自动存档（自检可替换）。</summary>
        public static Func<SaveResult> LandedSaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.BeachheadLanded, toast: false);

        public static int Revision { get; private set; }
        public static int LandCount { get; private set; }
        public static int DropSpawnCount { get; private set; }
        public static int GrantCount { get; private set; }
        public static int LandSaveCount { get; private set; }

        public static void ResetCountersForTests()
        {
            LandCount = 0;
            DropSpawnCount = 0;
            GrantCount = 0;
            LandSaveCount = 0;
        }

        // ── 调参 ─────────────────────────────────────────────────────────

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        public static float DropFirstSeconds => Mathf.Max(1f, Tuning("beachhead.drop_first_seconds", 30f));
        public static float DropIntervalSeconds => Mathf.Max(1f, Tuning("beachhead.drop_interval_seconds", 45f));
        public static int DropAliveCap => Mathf.Max(1, Mathf.RoundToInt(Tuning("beachhead.drop_alive_cap", 12f)));
        public static float DropRing => Mathf.Max(0f, Tuning("beachhead.drop_ring", 4f));
        public static int SnapRadius => Mathf.Max(0, Mathf.RoundToInt(Tuning("beachhead.snap_radius", 8f)));
        public static int ScorchRadius => Mathf.Clamp(Mathf.RoundToInt(Tuning("beachhead.scorch_radius", 4f)), 0, 8);
        public static float ScorchRecover => Mathf.Max(0f, Tuning("beachhead.scorch_recover", 30f));
        public static float ScorchStagger => Mathf.Max(0f, Tuning("beachhead.scorch_stagger", 0.5f));
        public static int HarassCapLevel => Mathf.Clamp(Mathf.RoundToInt(Tuning("beachhead.harass_level_cap", 1f)), 1, 4);
        public static float EscortCoverRadius => Mathf.Max(0f, Tuning("drop.antiair.radius_escort", 60f));

        // ── 读 ───────────────────────────────────────────────────────────

        public static BeachheadState StateOf(CampaignState s) => s?.Progress?.Beachhead;
        public static bool Landed(CampaignState s) => (StateOf(s)?.LandedTick ?? -1) >= 0;
        public static bool Done(CampaignState s) => s != null && CampaignEventLedger.Contains(s, ClearedEventId);
        public static bool Active(CampaignState s) => Landed(s) && !Done(s);

        /// <summary>FGR-END-021“这一段期间，家园只会遭受 1 级骚扰”：登陆场进行中返回等级上限，否则 0（不限）。突袭导演建计划 / 并入触发时读。</summary>
        public static int HarassLevelCap(CampaignState s) => Active(s) ? HarassCapLevel : 0;

        public static bool IsBeachheadInstance(string instanceId) => instanceId != null && instanceId.StartsWith(Prefix, StringComparison.Ordinal);

        public static bool IsPodInstance(string instanceId) => instanceId != null && instanceId.StartsWith(PodPrefix, StringComparison.Ordinal);

        /// <summary>滩头编成行（按 sortOrder）。</summary>
        public static List<EnemyEncounter> Encounters() => EnemyDefs.Encounters(RegionId, EnemyDefs.ConditionBase);

        /// <summary>这一行编成是不是投送目标：敌人表职能 structure（轨道锚点、登陆信标；按列，不认 ID）。</summary>
        public static bool IsTargetRow(EnemyEncounter enc) => enc != null && EnemyDefs.TryGet(enc.EnemyTypeId, out MechEnemy row) && row.Role == EnemyDefs.RoleStructure;

        public static RegionEnemyRecord Find(CampaignState s, string instanceId)
        {
            if (s?.RegionEnemies == null || instanceId == null)
            {
                return null;
            }
            foreach (RegionEnemyRecord e in s.RegionEnemies)
            {
                if (e != null && e.EnemyInstanceId == instanceId)
                {
                    return e;
                }
            }
            return null;
        }

        /// <summary>投送目标的存活数与总数（O(编成行)）。没着陆 = 0 / 总数。</summary>
        public static int TargetsAlive(CampaignState s, out int total, out bool beaconAlive)
        {
            total = 0;
            beaconAlive = false;
            int alive = 0;
            foreach (EnemyEncounter enc in Encounters())
            {
                if (!IsTargetRow(enc))
                {
                    continue;
                }
                total++;
                RegionEnemyRecord r = Find(s, enc.Id);
                if (r != null && r.IsAlive)
                {
                    alive++;
                    beaconAlive |= enc.AnchorId == BeaconPoint;
                }
            }
            return alive;
        }

        public static string HudText(CampaignState s)
        {
            if (!Landed(s))
            {
                return string.Empty;
            }
            if (Done(s))
            {
                return GameText.Get("beachhead.hud.done");
            }
            int alive = TargetsAlive(s, out int total, out bool beacon);
            int anchorsAlive = alive - (beacon ? 1 : 0);
            return GameText.Format("beachhead.hud.active", anchorsAlive, Math.Max(0, total - 1), GameText.Get(beacon ? "beachhead.hud.beacon_up" : "beachhead.hud.beacon_down"),
                HarassCapLevel);
        }

        public static string QuestProgressText(CampaignState s)
        {
            if (!Active(s))
            {
                return null;
            }
            int alive = TargetsAlive(s, out int total, out _);
            return GameText.Format("quest.prog.beachhead", total - alive, total);
        }

        /// <summary>失败页一行（登陆场进行中：读档后接着打，存档时已摧毁的目标保留）；不在登陆场 = 空。</summary>
        public static string FailureRetryText(CampaignState s)
        {
            if (!Active(s))
            {
                return string.Empty;
            }
            int alive = TargetsAlive(s, out int total, out _);
            return GameText.Format("failure.beachhead_retry", total - alive, total);
        }

        // ── 手工点位落位 ─────────────────────────────────────────────────

        private static WorldPlan _layoutPlan;
        private static readonly Dictionary<string, Vector2> Layout = new Dictionary<string, Vector2>(StringComparer.Ordinal);
        private static readonly Dictionary<string, AnchorTemplatePoint> LayoutRows = new Dictionary<string, AnchorTemplatePoint>(StringComparer.Ordinal);

        public static bool TryAnchor(CampaignState s, out PlannedAnchor anchor)
        {
            anchor = null;
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (plan == null)
            {
                return false;
            }
            foreach (PlannedAnchor a in plan.Anchors)
            {
                if (a != null && a.Id == AnchorId)
                {
                    anchor = a;
                    return true;
                }
            }
            return false;
        }

        /// <summary>手工点位在星球上的位置（格坐标；纯函数，按规划层锚点与模板，结果按世界缓存）。旧世界（没有这个锚点）返回 false。</summary>
        public static bool TryPoint(CampaignState s, string pointId, out Vector2 world)
        {
            world = default;
            if (!EnsureLayout(s))
            {
                return false;
            }
            return pointId != null && Layout.TryGetValue(pointId, out world);
        }

        public static bool TryPointRow(CampaignState s, string pointId, out AnchorTemplatePoint row)
        {
            row = null;
            return EnsureLayout(s) && pointId != null && LayoutRows.TryGetValue(pointId, out row);
        }

        public static IEnumerable<string> PointIds(CampaignState s)
        {
            if (EnsureLayout(s))
            {
                foreach (string k in LayoutRows.Keys)
                {
                    yield return k;
                }
            }
        }

        private static bool EnsureLayout(CampaignState s)
        {
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (plan == null)
            {
                return false;
            }
            if (ReferenceEquals(plan, _layoutPlan))
            {
                return Layout.Count > 0;
            }
            _layoutPlan = plan;
            Layout.Clear();
            LayoutRows.Clear();
            if (!TryAnchor(s, out PlannedAnchor a))
            {
                return false;
            }
            int snap = SnapRadius;
            foreach (AnchorTemplatePoint p in WorldGenContent.TemplatePoints(TemplateId))
            {
                TerritoryPlanner.ToWorld(a, p.LocalX, p.LocalY, out int x, out int y);
                if (p.Kind != "zone")
                {
                    Snap(s, ref x, ref y, snap);
                }
                Layout[p.PointId] = new Vector2(x, y);
                LayoutRows[p.PointId] = p;
            }
            return Layout.Count > 0;
        }

        /// <summary>落在悬崖 / 水面上的点挪到最近的可走格（切比雪夫环、固定顺序；生成器给的地形，纯函数）。找不到留在原处。</summary>
        private static void Snap(CampaignState s, ref int x, ref int y, int radius)
        {
            for (int r = 0; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        if (WorldGenQuery.IsWalkableGenerated(s, x + dx, y + dy))
                        {
                            x += dx;
                            y += dy;
                            return;
                        }
                    }
                }
            }
        }

        /// <summary>投送舱防空：滩头存活的护卫无人机（职能 escort）的实时中点；一台都没有 = false。O(滩头记录数)。</summary>
        public static bool TryEscortCover(CampaignState s, out Vector2 center, out float radius)
        {
            center = default;
            radius = EscortCoverRadius;
            if (!Landed(s) || s.RegionEnemies == null || radius <= 0f)
            {
                return false;
            }
            CombatSite site = CombatSites.Get(HomeValleyLayout.RegionId);
            Vector2 sum = Vector2.zero;
            int n = 0;
            foreach (RegionEnemyRecord e in s.RegionEnemies)
            {
                if (e == null || !e.IsAlive || !IsBeachheadInstance(e.EnemyInstanceId) || !EnemyDefs.TryGet(e.EnemyTypeId, out MechEnemy row) || row.Role != RoleEscort)
                {
                    continue;
                }
                sum += site != null && site.TryGetEnemyPosition(e.EnemyInstanceId, out Vector2 live) ? live : e.Position;
                n++;
            }
            if (n == 0)
            {
                return false;
            }
            center = sum / n;
            return true;
        }

        // ── 写 ───────────────────────────────────────────────────────────

        /// <summary>世界模拟的一个固定步（WorldSimulation 在清洗波次之后调用）。</summary>
        public static void WorldStep(CampaignState s, long tick)
        {
            BeachheadState st = StateOf(s);
            if (st == null || st.DoneTick >= 0)
            {
                return;
            }
            if (HomeValleySoftlockGuard.IsCoreDestroyed(s))
            {
                return; // 失败页接管；读档后从存档里的状态接着打
            }
            CombatSite site = CombatSites.Get(HomeValleyLayout.RegionId);
            if (site == null || site.IsDisposed)
            {
                return;
            }
            if (st.LandedTick < 0)
            {
                if (PurgeWaveService.Done(s) && TryAnchor(s, out _))
                {
                    Land(s, st, site, tick);
                }
                return;
            }
            if (tick % Math.Max(1, GameClock.StepHz) == 0)
            {
                EnsureSpawned(s, site);
                if (TargetsAlive(s, out int total, out _) == 0 && total > 0)
                {
                    Finish(s, st, tick);
                    return;
                }
            }
            if (st.NextDropTick >= 0 && tick >= st.NextDropTick)
            {
                Drop(s, st, site, tick);
            }
        }

        private static void Land(CampaignState s, BeachheadState st, CombatSite site, long tick)
        {
            var list = new List<RegionEnemyRecord>(s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>());
            var fresh = new List<RegionEnemyRecord>();
            foreach (EnemyEncounter enc in Encounters())
            {
                if (Find(s, enc.Id) != null || !EnemyDefs.TryGet(enc.EnemyTypeId, out MechEnemy row) || !TryPoint(s, enc.AnchorId, out Vector2 at))
                {
                    if (!TryPoint(s, enc.AnchorId, out _))
                    {
                        EnemyDefs.ReportEncounterProblem(RegionId, enc.Id + " @ " + enc.AnchorId);
                    }
                    continue;
                }
                float hp = EnemyDefs.SeedHealth(s, row);
                var rec = new RegionEnemyRecord
                {
                    EnemyInstanceId = enc.Id, RegionId = HomeValleyLayout.RegionId, EnemyTypeId = row.Id, Position = at, Health = hp, MaxHealth = hp, IsAlive = true,
                    CycleCooldownRemaining = enc.InitialCycle,
                };
                list.Add(rec);
                fresh.Add(rec);
            }
            s.RegionEnemies = list.ToArray();
            foreach (RegionEnemyRecord rec in fresh)
            {
                Spawn(s, site, rec);
            }
            Scorch(s, site);
            st.LandedTick = tick;
            st.NextDropTick = tick + GameClock.TicksFor(DropFirstSeconds);
            RaidDirectorService.CapPendingLevels(s, HarassCapLevel);
            LandCount++;
            Revision++;
            Log.Info($"[BeachheadService] 白潮在滩头着陆：{fresh.Count} 个单位落位");
            Vector2 c = TryPoint(s, BeaconPoint, out Vector2 b) ? b : Vector2.zero;
            NotificationCenter.Post(NotifyType, GameText.Get("beachhead.notify.landed"), new Vector3(c.x, 0f, c.y));
            CampaignObjectiveTracker.Recompute(s); // 主线“切断投送”出现
            GameEvent.Send(LandedEvent);
            WorldSim.WorldSimulation.RunAfterStep(() =>
            {
                if (CampaignSession.Current == s && Active(s))
                {
                    LandSaveCount++;
                    LandedSaveHandler?.Invoke();
                }
            });
        }

        private static void Spawn(CampaignState s, CombatSite site, RegionEnemyRecord rec)
        {
            if (EnemySpawnBuilder.TryBuild(site, s, rec, null, out BinGames.Sim.Combat.CombatSpawn spawn))
            {
                site.SpawnEnemy(rec, spawn);
            }
        }

        /// <summary>登陆信标周围写灭活焦土（来源 = 信标：它立着焦土不退，被毁后逐格恢复）。O(焦土格)。</summary>
        private static void Scorch(CampaignState s, CombatSite site)
        {
            RegionEnemyRecord beacon = Find(s, BeaconPoint);
            int r = ScorchRadius;
            if (beacon == null || !beacon.IsAlive || r <= 0)
            {
                return;
            }
            int cx = Mathf.RoundToInt(beacon.Position.x);
            int cy = Mathf.RoundToInt(beacon.Position.y);
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy <= r * r && (dx != 0 || dy != 0))
                    {
                        site.AddInertMark(cx + dx, cy + dy, BeaconPoint, ScorchRecover, ScorchStagger);
                    }
                }
            }
        }

        /// <summary>滩头记录还活着、却不在内核里（没有快照的新内核等）→ 补回；信标是重新补回的就把焦土也补上。O(滩头记录数)。</summary>
        private static void EnsureSpawned(CampaignState s, CombatSite site)
        {
            if (s.RegionEnemies == null)
            {
                return;
            }
            bool beaconRespawned = false;
            foreach (RegionEnemyRecord e in s.RegionEnemies)
            {
                if (e == null || !e.IsAlive || e.RegionId != HomeValleyLayout.RegionId || !IsBeachheadInstance(e.EnemyInstanceId)
                    || (site.TryGetEnemyUnit(e.EnemyInstanceId, out int unit) && site.UnitExists(unit)))
                {
                    continue;
                }
                Spawn(s, site, e);
                beaconRespawned |= e.EnemyInstanceId == BeaconPoint;
            }
            if (beaconRespawned)
            {
                Scorch(s, site);
            }
        }

        /// <summary>两次投送的间隔：目标总数 ÷ 存活目标数 × beachhead.drop_interval_seconds（拔掉一个目标，投送就慢一截）。没有目标立着 = -1。</summary>
        public static long IntervalTicks(int alive, int total) =>
            alive <= 0 ? -1 : GameClock.TicksFor(DropIntervalSeconds * Math.Max(1, total) / (double)alive);

        /// <summary>滩头投送来的单位（舱与舱投放的）还活着的台数。O(记录数)。</summary>
        public static int DeliveredAlive(CampaignState s)
        {
            int n = 0;
            if (s?.RegionEnemies != null)
            {
                foreach (RegionEnemyRecord e in s.RegionEnemies)
                {
                    if (e != null && e.IsAlive && IsPodInstance(e.EnemyInstanceId))
                    {
                        n++;
                    }
                }
            }
            return n;
        }

        private static MechEnemy PodRow()
        {
            foreach (MechEnemy row in EnemyDefs.Rows)
            {
                if (row != null && row.Faction == EnemyDefs.FactionFleet && row.Behavior == BehaviorAnchorDrop)
                {
                    return row;
                }
            }
            return null;
        }

        private static void Drop(CampaignState s, BeachheadState st, CombatSite site, long tick)
        {
            int alive = TargetsAlive(s, out int total, out bool beacon);
            long interval = IntervalTicks(alive, total);
            st.NextDropTick = interval < 0 ? -1 : tick + interval;
            if (interval < 0)
            {
                return;
            }
            Revision++;
            MechEnemy pod = PodRow();
            if (pod == null || DeliveredAlive(s) + 1 + EnemyFleetCargoCount(pod) > DropAliveCap)
            {
                st.DropsSkipped++;
                return;
            }
            Vector2 at = LandingSpot(s, st.DropCount, beacon);
            string id = PodPrefix + st.DropCount.ToString(CultureInfo.InvariantCulture);
            st.DropCount++;
            if (Find(s, id) != null)
            {
                return;
            }
            float hp = EnemyDefs.SeedHealth(s, pod);
            var rec = new RegionEnemyRecord { EnemyInstanceId = id, RegionId = HomeValleyLayout.RegionId, EnemyTypeId = pod.Id, Position = at, Health = hp, MaxHealth = hp, IsAlive = true };
            var list = new List<RegionEnemyRecord>(s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()) { rec };
            s.RegionEnemies = list.ToArray();
            Spawn(s, site, rec);
            DropSpawnCount++;
        }

        private static int EnemyFleetCargoCount(MechEnemy pod) => EnemyFleetParams.Cargo(pod, out _, out int count) ? count : 0;

        /// <summary>第 <paramref name="serial"/> 个舱的落点：信标立着 → 信标旁；否则按序号轮到的那个存活锚点旁。方向按序号等分（确定，与记录排列无关）。</summary>
        public static Vector2 LandingSpot(CampaignState s, int serial, bool beaconAlive)
        {
            Vector2 center = Vector2.zero;
            if (beaconAlive && TryPoint(s, BeaconPoint, out Vector2 b))
            {
                center = b;
            }
            else
            {
                var anchors = new List<Vector2>(3);
                foreach (EnemyEncounter enc in Encounters())
                {
                    RegionEnemyRecord r = IsTargetRow(enc) && enc.AnchorId != BeaconPoint ? Find(s, enc.Id) : null;
                    if (r != null && r.IsAlive)
                    {
                        anchors.Add(r.Position);
                    }
                }
                if (anchors.Count > 0)
                {
                    center = anchors[serial % anchors.Count];
                }
            }
            float a = serial * 137.5f * Mathf.Deg2Rad;
            return center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * DropRing;
        }

        public static int ScrapDrops { get; private set; }

        /// <summary>滩头单位被击毁（家园战斗规则的阵亡回调）：废料按敌人表 scrapLoot 掉在原地（开搬运单，与领地据点同一做法；舱投放的小兵不掉）；投送目标再写一条可定位的通知与击毁字幕。</summary>
        public static void OnPartKilled(CampaignState s, RegionEnemyRecord e)
        {
            if (s == null || e == null || !IsBeachheadInstance(e.EnemyInstanceId) || !EnemyDefs.TryGet(e.EnemyTypeId, out MechEnemy row))
            {
                return;
            }
            if (row.ScrapLoot > 0 && e.EnemyInstanceId.IndexOf(".c", StringComparison.Ordinal) < 0)
            {
                GroundItemRecord g = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, e.Position, CampaignEconomyLedger.ResourceScrap, row.ScrapLoot,
                    e.EnemyInstanceId + ":scrap");
                if (g != null)
                {
                    ScrapDrops++;
                    HomeValleyWorkOrders.TryCreateHaulPool(s, g.GroundItemId);
                }
            }
            if (IsPodInstance(e.EnemyInstanceId) || row.Role != EnemyDefs.RoleStructure)
            {
                return;
            }
            Revision++;
            int alive = TargetsAlive(s, out int total, out _);
            string text = GameText.Format("beachhead.notify.part_down", GameText.Get(row.NameKey), alive, total);
            FeedbackCues.RaiseLocated(FeedbackCueId.EnemyDestroyed, e.Position, text);
            NotificationCenter.Post(NotifyType, text, new Vector3(e.Position.x, 0f, e.Position.y));
        }

        private static void Finish(CampaignState s, BeachheadState st, long tick)
        {
            st.DoneTick = tick;
            st.NextDropTick = -1;
            Revision++;
            if (!CampaignEventLedger.TryGrant(s, ClearedEventId, "Endgame", s.PlaySeconds, "beachhead"))
            {
                return;
            }
            GrantCount++;
            Log.Info("[BeachheadService] 滩头的轨道锚点与登陆信标全部摧毁：投送切断");
            CampaignObjectiveTracker.Recompute(s);
            NotificationCenter.Post(NotifyType, GameText.Get("beachhead.notify.cleared"));
            GameEvent.Send(ClearedEvent);
        }
    }
}
