using System;
using System.Collections.Generic;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.WorldSim
{
    /// <summary>
    /// FG9-ACT1-01（承接 DEBT-FG9FAC01-02；FG09 第 4 章“诱饵中继：在小地图上制造虚假信号点，监听站情报能识别真假”）：静默领地的信号点。唯一写入口
    /// （<see cref="FactionState.ContactsDetected"/> / <see cref="FactionState.ContactsIdentified"/>）。
    ///
    /// - 信号点 = 静默领地里没被摧毁的据点（领地主力据点 + 远征地点里的驻军据点）+ 按（世界种子, 序号, 试探）放的诱饵中继（site.decoy.count 个；
    ///   纯函数，不进存档；离锚点、彼此、真据点都有间距）。诱饵中继不是据点：不计入控制度、不出突袭。
    /// - 侦测：在外的远征队成员（星球上 / 在远征地点里时取地点锚点的入口）site.contact.detect_cells 格内的信号点被侦测到（进存档、只增不减）。
    ///   战略地图 / 小地图上：迷雾里的已侦测信号点一律画成“未识别信号”（真假一模一样）；揭开迷雾后真据点照常按据点画，诱饵写“诱饵中继（已识破）”。
    /// - 甄别：监听站每破译出一条“反制预览”情报（FGR-RND-050 五类情报之一），顺带甄别当时已侦测到的全部信号点（进存档，一直有效）：
    ///   真据点写“信号源：静默据点”，诱饵写“诱饵中继（假信号）”。
    /// 只看存档与步序号（与观察、帧率、倍速无关）；侦测每 site.contact.check_seconds 一次 O(队员 × 信号点)，信号点个位数到几十个。
    /// </summary>
    public static class SignalContactService
    {
        public const string Territory = "silent";
        public const string DecoyPrefix = "decoy.silent.";
        public const string NotifyType = "signal_contact";

        private const uint DecoySalt = 0x44434F59u; // "DCOY"

        private static readonly List<(string Id, Vector2 Cell)> DecoyList = new List<(string, Vector2)>(8);
        private static object _decoyState;
        private static object _decoyPlan;
        private static readonly List<Vector2> MemberScratch = new List<Vector2>(8);
        private static readonly List<string> NewScratch = new List<string>(8);
        private static readonly List<OutpostRecord> NearScratch = new List<OutpostRecord>(16);

        /// <summary>自检读点。</summary>
        public static int DetectChecks { get; private set; }

        public static bool IsDecoy(string id) => id != null && id.StartsWith(DecoyPrefix, StringComparison.Ordinal);

        public static void ResetSessionState()
        {
            DecoyList.Clear();
            _decoyState = null;
            _decoyPlan = null;
            DetectChecks = 0;
        }

        // ─────────────────────────────── 诱饵中继（纯函数） ───────────────────────────────

        /// <summary>本世界静默领地里的诱饵中继（ID, 星球格）。没有规划层 / 没有静默领地返回空。</summary>
        public static IReadOnlyList<(string Id, Vector2 Cell)> Decoys(CampaignState s)
        {
            WorldPlan plan = s != null && WorldGenService.UsesWorldGen(s) ? WorldGenService.PlanFor(s) : null;
            if (ReferenceEquals(s, _decoyState) && ReferenceEquals(plan, _decoyPlan))
            {
                return DecoyList;
            }
            _decoyState = s;
            _decoyPlan = plan;
            DecoyList.Clear();
            PlannedTerritory t = plan?.Find(Territory);
            if (t == null || plan.Anchors.Count == 0)
            {
                return DecoyList; // 旧世界（v1 / v2）没有锚点与模板内容：不放诱饵
            }
            uint seed = unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));
            int n = Mathf.Max(0, Mathf.RoundToInt(Tuning("site.decoy.count", 4f)));
            float rmin = Mathf.Clamp01(Tuning("site.decoy.radial_min", 0.2f));
            float rmax = Mathf.Clamp(Tuning("site.decoy.radial_max", 0.85f), rmin, 1f);
            for (int i = 0; i < n; i++)
            {
                for (int k = 0; k < 16; k++)
                {
                    uint h = WorldGenMath.Hash(seed, i, k, DecoySalt);
                    int angle = (int)(h % 360u);
                    float frac = rmin + (rmax - rmin) * (((h >> 9) & 0xFFFF) / 65535f);
                    int dist = Mathf.RoundToInt(t.Radius * frac);
                    int x = t.CenterX + (int)(((long)dist * WorldPlan.Cos(angle) + 32768) >> 16);
                    int y = t.CenterY + (int)(((long)dist * WorldPlan.Cos(angle - 90) + 32768) >> 16);
                    var c = new Vector2(x, y);
                    // 修复轮（第 1 轮审查 P2）：诱饵还要落在可走的地形上（不在水里 / 悬崖上）、离静默领地的真据点至少 30 格（地图上不和真信号点叠在一起）。
                    if (TerritoryPlanner.NearAnyAnchor(plan, x, y, 8) || TooClose(c, 30f) || !WorldGenQuery.IsWalkableGenerated(s, x, y) || NearRealOutpost(s, c, 30f))
                    {
                        continue;
                    }
                    DecoyList.Add((DecoyPrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture), c));
                    break;
                }
            }
            return DecoyList;
        }

        /// <summary>离静默领地的领地据点（新游戏时按种子放下，摧毁 / 重建都不挪位置）是否太近。只在诱饵列表重算时调用。</summary>
        private static bool NearRealOutpost(CampaignState s, Vector2 c, float gap)
        {
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(s))
            {
                if (o != null && o.TerritoryId == Territory && o.Kind == WorldOutpostSystem.KindTerritory
                    && (new Vector2(o.CellX, o.CellY) - c).sqrMagnitude < gap * gap)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TooClose(Vector2 c, float gap)
        {
            foreach ((string _, Vector2 p) in DecoyList)
            {
                if ((p - c).sqrMagnitude < gap * gap)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>信号点的位置（诱饵 = 纯函数位置；据点 = 据点记录）。查不到返回 false。</summary>
        public static bool TryPosition(CampaignState s, string id, out Vector2 cell, out bool alive)
        {
            alive = false;
            cell = default;
            if (IsDecoy(id))
            {
                foreach ((string did, Vector2 p) in Decoys(s))
                {
                    if (did == id)
                    {
                        cell = p;
                        alive = true;
                        return true;
                    }
                }
                return false;
            }
            OutpostRecord o = WorldOutpostSystem.Find(s, id);
            if (o == null)
            {
                return false;
            }
            cell = new Vector2(o.CellX, o.CellY);
            alive = !o.Destroyed;
            return true;
        }

        private static bool IsRealContact(OutpostRecord o) =>
            o != null && !o.Destroyed && o.TerritoryId == Territory && TerritoryControlService.CountsForControl(o);

        // ─────────────────────────────── 侦测 ───────────────────────────────

        public static bool IsDetected(CampaignState s, string id) => s?.Factions?.ContactsDetected != null && Array.IndexOf(s.Factions.ContactsDetected, id) >= 0;

        public static bool IsIdentified(CampaignState s, string id) => s?.Factions?.ContactsIdentified != null && Array.IndexOf(s.Factions.ContactsIdentified, id) >= 0;

        /// <summary>每 site.contact.check_seconds 一次：远征队成员附近的信号点记为已侦测，新侦测到的合成一条通知。返回本次新侦测几个。</summary>
        public static int WorldStep(CampaignState s, long tick)
        {
            if (s?.Factions == null)
            {
                return 0;
            }
            long every = Math.Max(1L, (long)Math.Round(Tuning("site.contact.check_seconds", 2f) * GameClock.StepHz));
            if (tick % every != 0)
            {
                return 0;
            }
            if (!CollectMembers(s))
            {
                return 0;
            }
            DetectChecks++;
            return Detect(s, Tuning("site.contact.detect_cells", 150f));
        }

        /// <summary>按当前队员位置侦测一次（自检也走这里）。</summary>
        public static int Detect(CampaignState s, float radius)
        {
            if (MemberScratch.Count == 0)
            {
                CollectMembers(s);
            }
            NewScratch.Clear();
            float r2 = radius * radius;
            foreach ((string id, Vector2 p) in Decoys(s))
            {
                if (Near(p, r2) && !IsDetected(s, id))
                {
                    NewScratch.Add(id);
                }
            }
            // 修复轮（第 1 轮审查 P2）：真据点走空间桶（每个队员查一次附近的据点，O(队员 × 附近据点)），不再遍历全部据点。
            foreach (Vector2 m in MemberScratch)
            {
                NearScratch.Clear();
                WorldOutpostSystem.OutpostsNear(s, m.x, m.y, radius, NearScratch);
                foreach (OutpostRecord o in NearScratch)
                {
                    if (IsRealContact(o) && (new Vector2(o.CellX, o.CellY) - m).sqrMagnitude <= r2 && !IsDetected(s, o.OutpostId) && !NewScratch.Contains(o.OutpostId))
                    {
                        NewScratch.Add(o.OutpostId);
                    }
                }
            }
            NearScratch.Clear();
            MemberScratch.Clear();
            if (NewScratch.Count == 0)
            {
                return 0;
            }
            CampaignFgStateDomains.EnsureAll(s);
            var list = new List<string>(s.Factions.ContactsDetected);
            list.AddRange(NewScratch);
            list.Sort(StringComparer.Ordinal);
            s.Factions.ContactsDetected = list.ToArray();
            int n = NewScratch.Count;
            NewScratch.Clear();
            NotificationCenter.Post(NotifyType, GameText.Format("site.contact.notify.seen", n));
            GuidanceHooks.Raise(GuidanceHooks.SiteFirstContactSeen);
            return n;
        }

        private static bool Near(Vector2 p, float r2)
        {
            foreach (Vector2 m in MemberScratch)
            {
                if ((m - p).sqrMagnitude <= r2)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>在外的远征队成员的星球位置（在远征地点里的取地点入口）。没有在外的队伍返回 false。</summary>
        private static bool CollectMembers(CampaignState s)
        {
            MemberScratch.Clear();
            ExpeditionMarchState march = Regions.ExpeditionMarchService.Current(s);
            if (march?.Members == null || march.Members.Length == 0 || march.Phase == Regions.ExpeditionMarchService.PhaseNone
                || march.Phase == Regions.ExpeditionMarchService.PhaseReturned)
            {
                return false;
            }
            foreach (int id in march.Members)
            {
                if (!MachineRegistry.TryGetRecord(id, out MachineRecord m) || m == null || !m.IsAlive)
                {
                    continue;
                }
                if (m.RegionId == HomeValleyLayout.RegionId)
                {
                    MemberScratch.Add(MachineRegistry.TryGetLivePosition(id, out Vector2 live) ? live : m.WorldPosition);
                }
                else if (ExpeditionSiteAnchors.TryGet(s, m.RegionId, out Vector2 entry))
                {
                    MemberScratch.Add(entry);
                }
            }
            return MemberScratch.Count > 0;
        }

        // ─────────────────────────────── 甄别 ───────────────────────────────

        /// <summary>监听站破译出一条反制预览：甄别全部已侦测、还没甄别的信号点。返回本次甄别几个。</summary>
        public static int OnCounterPreviewProduced(CampaignState s)
        {
            if (s?.Factions?.ContactsDetected == null || s.Factions.ContactsDetected.Length == 0)
            {
                return 0;
            }
            CampaignFgStateDomains.EnsureAll(s);
            var list = new List<string>(s.Factions.ContactsIdentified);
            int decoys = 0;
            int real = 0;
            foreach (string id in s.Factions.ContactsDetected)
            {
                if (list.Contains(id))
                {
                    continue;
                }
                list.Add(id);
                if (IsDecoy(id))
                {
                    decoys++;
                }
                else
                {
                    real++;
                }
            }
            if (decoys + real == 0)
            {
                return 0;
            }
            list.Sort(StringComparer.Ordinal);
            s.Factions.ContactsIdentified = list.ToArray();
            NotificationCenter.Post(NotifyType, GameText.Format("site.contact.notify.identified", decoys, real));
            GuidanceHooks.Raise(GuidanceHooks.SiteFirstContactIdentified);
            return decoys + real;
        }

        // ─────────────────────────────── 地图 ───────────────────────────────

        /// <summary>
        /// 地图要画的信号点（视野内、已侦测、还在的）：迷雾里的画成“未识别信号”或甄别后的真 / 假；揭开迷雾的真据点不在这里画（由据点图层按据点画），
        /// 揭开迷雾的诱饵写“诱饵中继（已识破）”。O(已侦测的信号点)。
        /// </summary>
        public static void CollectForMap(CampaignState s, HomeGridMap map, double minX, double minY, double maxX, double maxY, List<(string Id, Vector2 Cell, string Label, bool Decoy)> into)
        {
            into.Clear();
            string[] detected = s?.Factions?.ContactsDetected;
            if (detected == null || detected.Length == 0)
            {
                return;
            }
            foreach (string id in detected)
            {
                if (!TryPosition(s, id, out Vector2 c, out bool alive) || !alive || c.x < minX || c.x > maxX || c.y < minY || c.y > maxY)
                {
                    continue;
                }
                bool explored = map != null && map.IsExploredNoLoad(new GridCell(Mathf.RoundToInt(c.x), Mathf.RoundToInt(c.y)));
                bool decoy = IsDecoy(id);
                if (explored && !decoy)
                {
                    continue;
                }
                string label = explored ? GameText.Get("site.contact.decoy_seen")
                    : !IsIdentified(s, id) ? GameText.Get("site.contact.unknown")
                    : decoy ? GameText.Get("site.contact.decoy") : GameText.Get("site.contact.real");
                into.Add((id, c, label, decoy && (explored || IsIdentified(s, id))));
            }
        }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
