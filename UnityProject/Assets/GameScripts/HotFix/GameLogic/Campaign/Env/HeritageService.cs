using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameConfig.fg;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>遗产在这个世界里的状态（只读快照用）。</summary>
    public enum HeritageStatus : byte
    {
        /// <summary>这个世界里没能给它挑到遗迹点（生成器在它的距离范围里没有合格的遗迹点；自检拦截，正常不会出现）。</summary>
        Unplaced = 0,
        /// <summary>放在某处遗迹点里，还没被找到（那处遗迹还没回收）。</summary>
        Hidden = 1,
        /// <summary>找到了但留在地上（货舱装不下 / 携带者阵亡 / 被丢弃）：派机器去取。</summary>
        OnGround = 2,
        /// <summary>装在某台机器的货舱里，带回核心 / 仓库附近即入库。</summary>
        Carried = 3,
        /// <summary>已存入核心保管库（效果已生效）。</summary>
        Obtained = 4,
    }

    /// <summary>
    /// FG8-EXP-04（FG08 FGR-EXP-040 遗迹与人类遗产；FG17 FGR-GEN-033 遗迹点 / 每个世界各出现一次 / 剧情需要的保证可以到达；FG07 FGR-ENV-051）：人类遗产。
    ///
    /// - **放在哪**：遗迹点仍由世界生成器分布（<see cref="WorldFeatures"/>，不新建分布算法）。每件遗产（fg.TbHeritage）第一次需要时，
    ///   按（种子, 遗产ID）用整数哈希在它的距离范围里取若干个目标点，在每个目标点周围的方框里取生成器给出的、离目标最近的遗迹点
    ///   （距离在范围里、没被别的遗产占、没回收过；剧情需要的另要求不在危害区 / 危害带、地面可走；都不在白潮滩头预留区）。
    ///   结果写进存档（<see cref="HomeExplorationState.HeritageIds"/> / <see cref="HomeExplorationState.HeritageRelics"/>），之后不再变——
    ///   同一个世界每件遗产恰好一处（B25：只取决于种子与表，与玩家先去哪里、是否打开地图无关；旧档里已经回收的遗迹点不再选，避免遗产拿不到）。
    /// - **怎么拿**：回收这处遗迹完工时（<see cref="HomeExplorationService.CompleteRecovery"/>），遗产变成一件区域关键物
    ///   （<see cref="CampaignState.RegionQuestItems"/>，内容 human_legacy、实例 legacy:遗产ID，占货舱 loot.cargo.legacy 格），装进回收它的机器的货舱；
    ///   货舱装不下就留在原地（卡片负向），地图 / 悬停标“遗产留在原地”，右键那里（或战略地图点那处遗迹）派机器来取（取回单 = 同一种回收工单，目标 = 实例 ID）。
    /// - **带回**：携带者到了核心 / 带仓库的前哨站附近（<see cref="CargoService"/> 每秒检查，与黑匣子同一套）即入库（<see cref="OnDelivered"/>）：
    ///   旧时代蓝图登记（解锁记录）、预制固件解锁并放进固件存放（可以直接装配）、纪念物进展柜、数据档案技术数据入账（经济账本，事务号 legacy:遗产ID，天然幂等）。
    ///   携带者阵亡 → 掉在阵亡处（永不消失）；远征队员走近自动捡起（与黑匣子同一套）。
    /// - **看**：核心保管库面板（展柜 = 已入库的纪念物；收集 = 全部遗产与进度）、图鉴“人类遗产”页签（跨存档，未收集显示剪影 + 第几幕的提示）。
    ///
    /// 开销：挑遗迹点只在每个世界第一次需要时发生（≤ 20 件 × 尝试次数 × 一个方框的点位查询，点位按分布格缓存）；其余查询 O(遗产数 + 关键物数)，只在下令 / 完工 / 面板刷新时算，
    /// 不按帧（每秒的入库检查在 <see cref="CargoService.WorldStep"/> 里，O(关键物数)）。推进全在世界模拟步里，与是否被观察无关（FGR-BASE-021）。
    /// </summary>
    public static class HeritageService
    {
        public const string ContentId = "human_legacy";
        public const string InstancePrefix = "legacy:";
        public const string CodexPrefix = "heritage:";
        public const string NotifyType = "heritage";
        public const string CatBlueprint = "blueprint";
        public const string CatFirmware = "firmware";
        public const string CatMemento = "memento";
        public const string CatArchive = "archive";
        public static readonly string[] Categories = { CatBlueprint, CatFirmware, CatMemento, CatArchive };

        /// <summary>任何遗产状态变化 +1（面板 / 地图据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;

        /// <summary>自检读：最近一次挑遗迹点用了多久、查了多少个方框。</summary>
        public static double LastPlaceMs { get; private set; }
        public static int LastPlaceQueries { get; private set; }

        private static readonly List<Heritage> Rows = new List<Heritage>(24);
        private static readonly Dictionary<string, Heritage> ById = new Dictionary<string, Heritage>(StringComparer.Ordinal);
        private static bool _loaded;
        private static readonly List<WorldFeature> Scratch = new List<WorldFeature>(64);
        private static HomeExplorationState _placedState;
        private static string[] _placedIds;

        public static void Touch() => Revision++;

        public static void Reload()
        {
            _loaded = false;
            _placedState = null;
            _placedIds = null;
            EnsureLoaded();
        }

        public static void ResetForTests()
        {
            _loaded = false;
            _placedState = null;
            _placedIds = null;
            ForceUnreachableForTests = null;
            ReachRequests = 0;
            CancelReach();
            LastPlaceMs = 0;
            LastPlaceQueries = 0;
            Revision++;
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            Rows.Clear();
            ById.Clear();
            try
            {
                TbHeritage t = ConfigSystem.Instance?.Tables?.TbHeritage;
                if (t != null)
                {
                    Rows.AddRange(t.DataList);
                }
            }
            catch (Exception e)
            {
                Log.Error("[HeritageService] 读取 fg.TbHeritage 失败：" + e.Message);
            }
            Rows.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
            foreach (Heritage h in Rows)
            {
                ById[h.Id] = h;
            }
            if (Rows.Count == 0)
            {
                Log.Warning("[HeritageService] fg.TbHeritage 缺失：遗迹里不会有人类遗产。");
            }
        }

        public static IReadOnlyList<Heritage> All
        {
            get
            {
                EnsureLoaded();
                return Rows;
            }
        }

        public static Heritage Find(string id)
        {
            EnsureLoaded();
            return id != null && ById.TryGetValue(id, out Heritage h) ? h : null;
        }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        public static float PickupSeconds => Math.Max(0.1f, Tuning("heritage.pickup_seconds", 6f));
        public static int Space => LootPickCatalog.SpaceOf(ContentId, 1);

        // ── 名字与文字 ──────────────────────────────────────────────────────────

        public static string CategoryName(string cat) => GameText.Get("heritage.cat." + (cat ?? CatMemento));
        public static string Name(Heritage h) => h != null ? GameText.Get(h.NameKey) : string.Empty;
        /// <summary>“类别「名称」”。</summary>
        public static string Display(Heritage h) => h != null ? GameText.Format("heritage.name_fmt", CategoryName(h.Category), Name(h)) : string.Empty;

        public static string InstanceIdOf(string heritageId) => InstancePrefix + heritageId;
        public static string CodexIdOf(string heritageId) => CodexPrefix + heritageId;

        public static bool IsInstanceId(string id) => id != null && id.StartsWith(InstancePrefix, StringComparison.Ordinal);

        public static string HeritageIdOf(string instanceId) => IsInstanceId(instanceId) ? instanceId.Substring(InstancePrefix.Length) : null;

        public static bool IsHeritage(RegionQuestItemRecord q) => q != null && q.ContentId == ContentId && IsInstanceId(q.SalvageInstanceId);

        // ── 放在哪（FGR-GEN-033）──────────────────────────────────────────────

        /// <summary>
        /// 每个世界第一次需要时给每件遗产挑一处遗迹点并写进存档（幂等；表里新增的遗产补挑；世界身份变了整组重挑）。
        /// 生成器上下文还没就绪时什么都不做（之后再调）。
        /// </summary>
        public static void EnsurePlaced(CampaignState s)
        {
            HomeExplorationState st = HomeExplorationService.StateOf(s);
            // 快路径：同一个存档域、放置数组没换过（挑好之后只读）→ O(1) 返回（面板 / 查询每次都会调）。
            if (st != null && ReferenceEquals(st, _placedState) && ReferenceEquals(st.HeritageIds, _placedIds))
            {
                return;
            }
            WorldGenContext ctx = s != null ? WorldGenService.ContextFor(s) : null;
            if (st == null || ctx == null)
            {
                return;
            }
            EnsureLoaded();
            string world = ctx.Identity;
            if (!string.Equals(st.HeritageWorld, world, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(st.HeritageWorld))
                {
                    Log.Warning($"[HeritageService] 世界身份变了（{st.HeritageWorld} → {world}），遗产重新挑遗迹点。");
                }
                st.HeritageWorld = world;
                st.HeritageIds = Array.Empty<string>();
                st.HeritageRelics = Array.Empty<string>();
                st.HeritageReach = Array.Empty<int>();
                st.HeritageReachRejected = Array.Empty<string>();
            }
            if (st.HeritageIds.Length >= Rows.Count && AllPlacedListed(st))
            {
                _placedState = st;
                _placedIds = st.HeritageIds;
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int queries = 0;
            var used = new HashSet<string>(st.HeritageRelics ?? Array.Empty<string>(), StringComparer.Ordinal);
            AddRejected(st, used); // 校验过走不到的遗迹点不再选
            var ids = new List<string>(st.HeritageIds);
            var relics = new List<string>(st.HeritageRelics);
            foreach (Heritage h in Rows)
            {
                if (ids.Contains(h.Id))
                {
                    continue;
                }
                string relic = Pick(s, ctx, h, used, ref queries);
                if (relic == null)
                {
                    Log.Error($"[HeritageService] 世界 {world} 里没给遗产 {h.Id} 找到合格的遗迹点（{h.MinDistance}～{h.MaxDistance} 格）。");
                }
                else
                {
                    used.Add(relic);
                }
                ids.Add(h.Id);
                relics.Add(relic ?? string.Empty);
            }
            st.HeritageIds = ids.ToArray();
            st.HeritageRelics = relics.ToArray();
            _placedState = st;
            _placedIds = st.HeritageIds;
            LastPlaceQueries = queries;
            LastPlaceMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Revision++;
        }

        private static bool AllPlacedListed(HomeExplorationState st)
        {
            foreach (Heritage h in Rows)
            {
                if (Array.IndexOf(st.HeritageIds, h.Id) < 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>按（种子, 遗产ID）挑遗迹点：整数哈希取方向与距离（不用三角函数，各平台一致），在目标点周围的方框里取生成器给出的、离目标最近的合格遗迹点。</summary>
        private static string Pick(CampaignState s, WorldGenContext ctx, Heritage h, HashSet<string> used, ref int queries)
        {
            int attempts = Math.Max(4, (int)Math.Round(Tuning("heritage.place_attempts", 24f)));
            int half = Math.Max(32, (int)Math.Round(Tuning("heritage.place_search", 96f)));
            uint salt = Fnv(h.Id) ^ unchecked((uint)ctx.Seed * 2654435761u);
            int lo = Math.Max(0, h.MinDistance);
            int hi = Math.Max(lo + 1, h.MaxDistance);
            bool story = h.Story == 1;
            for (int k = 0; k < attempts; k++)
            {
                uint a = Mix(salt, (uint)k * 3u + 1u);
                uint b = Mix(salt, (uint)k * 3u + 2u);
                uint c = Mix(salt, (uint)k * 3u + 3u);
                int dx = (int)(a % 2049u) - 1024;
                int dy = (int)(b % 2049u) - 1024;
                long len = ISqrt((long)dx * dx + (long)dy * dy);
                if (len < 64)
                {
                    continue; // 方向太短（取整误差大）：换下一个
                }
                int r = lo + (int)(c % (uint)(hi - lo + 1));
                int tx = ctx.Core.X + (int)((long)dx * r / len);
                int ty = ctx.Core.Y + (int)((long)dy * r / len);
                Scratch.Clear();
                WorldGenQuery.FeaturesIn(s, tx - half, ty - half, tx + half, ty + half, Scratch);
                queries++;
                WorldFeature best = null;
                long bestD = long.MaxValue;
                foreach (WorldFeature f in Scratch)
                {
                    if (f.Kind != WorldFeatureKind.Relic || f.Distance < lo || f.Distance > hi || used.Contains(f.Id) || HomeExplorationService.IsRecovered(s, f.Id))
                    {
                        continue;
                    }
                    if (!Eligible(s, f, story))
                    {
                        continue;
                    }
                    long ex = f.X - tx;
                    long ey = f.Y - ty;
                    long d = ex * ex + ey * ey;
                    if (d < bestD || (d == bestD && best != null && string.CompareOrdinal(f.Id, best.Id) < 0))
                    {
                        bestD = d;
                        best = f;
                    }
                }
                Scratch.Clear();
                if (best != null)
                {
                    return best.Id;
                }
            }
            return null;
        }

        /// <summary>遗迹点能不能放这件遗产：都不放在白潮滩头预留区（FGR-GEN-041 剧情前不揭示）；剧情需要的另要求不在危害区 / 危害带里、地面可走（FGR-GEN-033 保证可以到达）。</summary>
        public static bool Eligible(CampaignState s, WorldFeature f, bool story)
        {
            PlannedTerritory t = WorldGenQuery.TerritoryAt(s, f.X, f.Y);
            if (t != null && !t.IsFaction)
            {
                return false;
            }
            if (!story)
            {
                return true;
            }
            if (HomeExplorationService.HazardAt(s, f.X, f.Y, visibleOnly: false, out _, out _) != null)
            {
                return false;
            }
            return WorldGenQuery.IsWalkableGenerated(s, f.X, f.Y);
        }

        private static uint Fnv(string text)
        {
            uint h = 2166136261u;
            foreach (char ch in text ?? string.Empty)
            {
                h = unchecked((h ^ ch) * 16777619u);
            }
            return h;
        }

        private static uint Mix(uint seed, uint n)
        {
            unchecked
            {
                uint x = seed ^ (n * 0x9E3779B9u);
                x ^= x >> 16;
                x *= 0x7FEB352Du;
                x ^= x >> 15;
                x *= 0x846CA68Bu;
                x ^= x >> 16;
                return x;
            }
        }

        private static long ISqrt(long v)
        {
            if (v <= 0)
            {
                return 0;
            }
            long r = (long)Math.Sqrt(v);
            while (r * r > v)
            {
                r--;
            }
            while ((r + 1) * (r + 1) <= v)
            {
                r++;
            }
            return r;
        }

        // ── 剧情遗产可达校验（FGR-GEN-033“保证可以到达”；与锚点同一口径：层级寻路，FGR-ARC-015）─────────────

        public const int ReachPending = 0;
        public const int ReachOk = 1;
        public const int ReachFailed = 3;

        /// <summary>自检注入：返回 true 的遗迹点把寻路结果当作“走不到”（请求照常发出、照常计数），驱动换点 / 失败的负向断言。</summary>
        public static Func<string, bool> ForceUnreachableForTests;

        /// <summary>自检读点：发出的可达校验请求数。</summary>
        public static int ReachRequests { get; private set; }

        private static readonly List<Unity.Mathematics.int2> ReachPoints = new List<Unity.Mathematics.int2>(256);
        private static int _reachSerial;

        public const int ReachResultOnLane = 0;
        public const int ReachResultReached = 1;
        public const int ReachResultBlocked = 2;

        private static bool LaneMine() =>
            Nav.NavService.BackgroundHeld && Nav.NavService.BackgroundOwnerTag == Nav.NavService.OwnerHeritageReach && Nav.NavService.BackgroundSerial == _reachSerial;

        private static void CancelReach()
        {
            if (LaneMine())
            {
                Nav.NavService.CancelBackground();
            }
        }

        private static void AddRejected(HomeExplorationState st, HashSet<string> into)
        {
            foreach (string e in st.HeritageReachRejected ?? Array.Empty<string>())
            {
                int at = e?.IndexOf('@') ?? -1;
                if (at >= 0)
                {
                    into.Add(e.Substring(at + 1));
                }
            }
        }

        private static int RejectedCount(HomeExplorationState st, string heritageId)
        {
            int n = 0;
            string prefix = heritageId + "@";
            foreach (string e in st.HeritageReachRejected ?? Array.Empty<string>())
            {
                n += e != null && e.StartsWith(prefix, StringComparison.Ordinal) ? 1 : 0;
            }
            return n;
        }

        /// <summary>这件遗产的可达校验结论（非剧情遗产 / 还没校验返回 <see cref="ReachPending"/>）。</summary>
        public static int ReachOf(CampaignState s, string heritageId)
        {
            HomeExplorationState st = HomeExplorationService.StateOf(s);
            int i = st?.HeritageIds != null ? Array.IndexOf(st.HeritageIds, heritageId) : -1;
            return i >= 0 && st.HeritageReach != null && i < st.HeritageReach.Length ? st.HeritageReach[i] : ReachPending;
        }

        /// <summary>剧情遗产是否都有结论（可达，或换了几次仍走不到）。</summary>
        public static bool ReachResolved(CampaignState s)
        {
            HomeExplorationState st = HomeExplorationService.StateOf(s);
            return st?.HeritageIds != null && st.HeritageIds.Length > 0 && NextReachIndex(s, st) < 0;
        }

        private static void SetReach(HomeExplorationState st, int i, int value)
        {
            if (st.HeritageReach == null || st.HeritageReach.Length < st.HeritageIds.Length)
            {
                var a = new int[st.HeritageIds.Length];
                if (st.HeritageReach != null)
                {
                    Array.Copy(st.HeritageReach, a, Math.Min(st.HeritageReach.Length, a.Length));
                }
                st.HeritageReach = a;
            }
            st.HeritageReach[i] = value;
        }

        /// <summary>下一件要校验的剧情遗产下标：剧情需要、挑到了遗迹点、还没有结论、还没被找到（找到了就不再挪，机器走到过即可达）。没有返回 -1。O(遗产数)。</summary>
        private static int NextReachIndex(CampaignState s, HomeExplorationState st)
        {
            for (int i = 0; i < st.HeritageIds.Length; i++)
            {
                Heritage h = Find(st.HeritageIds[i]);
                if (h == null || h.Story != 1 || i >= st.HeritageRelics.Length || string.IsNullOrEmpty(st.HeritageRelics[i]))
                {
                    continue;
                }
                int r = st.HeritageReach != null && i < st.HeritageReach.Length ? st.HeritageReach[i] : ReachPending;
                if (r != ReachPending)
                {
                    continue;
                }
                if (IsObtained(s, h.Id) || FindItem(s, h.Id) != null || HomeExplorationService.IsRecovered(s, st.HeritageRelics[i]))
                {
                    SetReach(st, i, ReachOk);
                    Revision++;
                    continue;
                }
                return i;
            }
            return -1;
        }

        /// <summary>
        /// 家园世界步（<see cref="WorldSim.WorldSimulation"/>，只看步序号，与观察无关）：剧情遗产所在遗迹点逐件做层级寻路可达校验（从归还核心到遗迹点，玩家通行类别），结论进存档。
        /// 走不到 → 这处遗迹点记进“走不到”名单，按同一挑选规则给这件遗产改挑下一处（还没被找到时才挪）；换了 heritage.reach_max_repicks 次仍走不到 → 结论“走不到”、写错误日志。
        /// 每 heritage.reach_check_seconds 看一次；后台寻路通道空闲才发（借用：别的请求方要用时先把这条算完取出、再让出，见 <see cref="StashReach"/>），
        /// 发出后固定 heritage.reach_latency_seconds 那一步采纳（不跨帧硬等）。在算的那一条记在存档里（遗迹点 / 采纳步 / 提前取出的结果）：结论只取决于发出那一刻的格网，
        /// 采纳只看步序号，存档 / 让出通道与否逐步一致。寻路在 AOT Burst 内核里；热更层平时 O(1)，每 check_seconds 一次 O(遗产数)。
        /// </summary>
        public static void WorldStep(CampaignState s, long tick, int stepHz)
        {
            HomeExplorationState st = HomeExplorationService.StateOf(s);
            if (st?.HeritageIds == null || st.HeritageIds.Length == 0)
            {
                return;
            }
            int hz = Math.Max(1, stepHz);
            if (!string.IsNullOrEmpty(st.HeritageReachRelic))
            {
                if (tick >= st.HeritageReachTick)
                {
                    AdoptReach(s, st);
                }
                return;
            }
            long every = Math.Max(1L, (long)Math.Round(Tuning("heritage.reach_check_seconds", 1f) * hz));
            if (tick % every != 0)
            {
                return;
            }
            int next = NextReachIndex(s, st);
            if (next < 0 || !Nav.NavService.IsBound || !ReferenceEquals(Nav.NavService.BoundState, s) || Nav.NavService.BackgroundHeld
                || !HomeExplorationService.TryGetRelic(s, st.HeritageRelics[next], out HomeExplorationService.RelicInfo info))
            {
                return;
            }
            int serial = _reachSerial + 1;
            if (!Nav.NavService.StartBackground(Nav.NavService.OwnerHeritageReach, next + 1, serial, BinGames.Sim.Nav.NavConst.ClassPlayer,
                    HomeGridService.CorePivot(s), new GridCell(info.X, info.Y), allowPartial: true))
            {
                return;
            }
            _reachSerial = serial;
            ReachRequests++;
            st.HeritageReachRelic = st.HeritageRelics[next];
            st.HeritageReachTick = tick + Math.Max(1L, (long)Math.Round(Tuning("heritage.reach_latency_seconds", 0.5f) * hz));
            st.HeritageReachResult = ReachResultOnLane;
            st.HeritageReachStatus = 0;
        }

        /// <summary>
        /// 在算的那一条现在就取出来、结论记进存档（还没到采纳步就先不用）：存档前（<see cref="WorldSim.WorldSimulation.SyncAllForSave"/>）、
        /// 别的请求方要用后台通道时（<see cref="Nav.NavService.StartBackground"/> 让出前）调。之后在原来的采纳步交到，与不存档 / 不让出逐步一致。
        /// </summary>
        public static void StashReach(CampaignState s)
        {
            HomeExplorationState st = HomeExplorationService.StateOf(s);
            if (st == null || string.IsNullOrEmpty(st.HeritageReachRelic) || st.HeritageReachResult != ReachResultOnLane || !LaneMine())
            {
                return;
            }
            st.HeritageReachResult = Collect(s, st.HeritageReachRelic, out int status) ? ReachResultReached : ReachResultBlocked;
            st.HeritageReachStatus = status;
        }

        /// <summary>取后台通道这一条的结果并判定（终点到了遗迹点，或部分路线的终点就在遗迹点旁 heritage.reach_near_cells 格内）。</summary>
        private static bool Collect(CampaignState s, string relic, out int status)
        {
            if (!Nav.NavService.CollectBackground(ReachPoints, out BinGames.Sim.Nav.NavResult res))
            {
                ReachPoints.Clear();
                status = -1;
                return false;
            }
            status = (int)res.Status;
            bool reached = res.Status == BinGames.Sim.Nav.NavStatus.Ok && ReachPoints.Count > 0;
            if (!reached && res.Status == BinGames.Sim.Nav.NavStatus.Partial && ReachPoints.Count > 0
                && HomeExplorationService.TryGetRelic(s, relic, out HomeExplorationService.RelicInfo info))
            {
                // 部分路线的终点就在遗迹点旁边（回收时机器站在旁边干活）也算到得了。
                long dx = res.End.x - info.X;
                long dy = res.End.y - info.Y;
                long near = Math.Max(1, (long)Math.Round(Tuning("heritage.reach_near_cells", 2f)));
                reached = dx * dx + dy * dy <= near * near;
            }
            ReachPoints.Clear();
            return reached;
        }

        private static void AdoptReach(CampaignState s, HomeExplorationState st)
        {
            string relic = st.HeritageReachRelic;
            int result = st.HeritageReachResult;
            int status = st.HeritageReachStatus;
            if (result == ReachResultOnLane)
            {
                if (LaneMine())
                {
                    result = Collect(s, relic, out status) ? ReachResultReached : ReachResultBlocked;
                }
                else
                {
                    result = -1; // 通道上已经没有这一条（换了战役 / 寻路服务重绑）：作废，下次重发
                }
            }
            st.HeritageReachRelic = string.Empty;
            st.HeritageReachTick = 0;
            st.HeritageReachResult = ReachResultOnLane;
            st.HeritageReachStatus = 0;
            int i = Array.IndexOf(st.HeritageRelics, relic);
            if (result < 0 || i < 0 || i >= st.HeritageIds.Length || (st.HeritageReach != null && i < st.HeritageReach.Length && st.HeritageReach[i] != ReachPending))
            {
                return; // 期间换过点 / 已有结论：作废
            }
            bool reached = result == ReachResultReached && !(ForceUnreachableForTests != null && ForceUnreachableForTests(relic));
            if (reached)
            {
                SetReach(st, i, ReachOk);
                Revision++;
                return;
            }
            string hid = st.HeritageIds[i];
            Heritage h = Find(hid);
            st.HeritageReachRejected = Append(st.HeritageReachRejected ?? Array.Empty<string>(), hid + "@" + relic);
            int maxRepicks = Math.Max(0, (int)Math.Round(Tuning("heritage.reach_max_repicks", 4f)));
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            string moved = null;
            if (h != null && ctx != null && RejectedCount(st, hid) <= maxRepicks && FindItem(s, hid) == null && !IsObtained(s, hid))
            {
                var used = new HashSet<string>(st.HeritageRelics, StringComparer.Ordinal);
                AddRejected(st, used);
                int q = 0;
                moved = Pick(s, ctx, h, used, ref q);
            }
            if (moved != null)
            {
                st.HeritageRelics[i] = moved;
                SetReach(st, i, ReachPending);
                Log.Warning($"[HeritageService] 剧情遗产 {hid} 所在遗迹点 {relic} 从核心走不到（寻路 {status}），改放到 {moved}，稍后再校验（FGR-GEN-033）。");
            }
            else
            {
                SetReach(st, i, ReachFailed);
                Log.Error($"[HeritageService] 剧情遗产 {hid} 换了 {RejectedCount(st, hid) - 1} 次仍找不到从核心走得到的遗迹点（最后一处 {relic}，寻路 {status}；FGR-GEN-033，需要调表或检查地形）。");
            }
            Revision++;
        }

        /// <summary>这件遗产放在哪处遗迹点（没挑到 / 还没挑返回 null）。</summary>
        public static string RelicOf(CampaignState s, string heritageId)
        {
            EnsurePlaced(s);
            HomeExplorationState st = HomeExplorationService.StateOf(s);
            if (st == null)
            {
                return null;
            }
            int i = Array.IndexOf(st.HeritageIds, heritageId);
            return i >= 0 && i < st.HeritageRelics.Length && !string.IsNullOrEmpty(st.HeritageRelics[i]) ? st.HeritageRelics[i] : null;
        }

        /// <summary>这处遗迹点里放的遗产（没有返回 null）。</summary>
        public static Heritage AtRelic(CampaignState s, string relicId)
        {
            if (relicId == null)
            {
                return null;
            }
            EnsurePlaced(s);
            HomeExplorationState st = HomeExplorationService.StateOf(s);
            if (st == null)
            {
                return null;
            }
            int i = Array.IndexOf(st.HeritageRelics, relicId);
            return i >= 0 && i < st.HeritageIds.Length ? Find(st.HeritageIds[i]) : null;
        }

        // ── 状态 ──────────────────────────────────────────────────────────────

        public static RegionQuestItemRecord FindItem(CampaignState s, string heritageId)
        {
            string iid = InstanceIdOf(heritageId);
            foreach (RegionQuestItemRecord q in s?.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (q != null && q.SalvageInstanceId == iid && q.ContentId == ContentId)
                {
                    return q;
                }
            }
            return null;
        }

        public static bool IsObtained(CampaignState s, string heritageId) =>
            Array.IndexOf(HomeExplorationService.StateOf(s)?.HeritageObtained ?? Array.Empty<string>(), heritageId) >= 0;

        public static HeritageStatus StatusOf(CampaignState s, string heritageId)
        {
            if (IsObtained(s, heritageId))
            {
                return HeritageStatus.Obtained;
            }
            RegionQuestItemRecord q = FindItem(s, heritageId);
            if (q != null)
            {
                return q.State == RegionQuestItemState.Carried ? HeritageStatus.Carried
                    : q.State == RegionQuestItemState.Recovered ? HeritageStatus.Obtained
                    : HeritageStatus.OnGround;
            }
            return RelicOf(s, heritageId) != null ? HeritageStatus.Hidden : HeritageStatus.Unplaced;
        }

        /// <summary>留在家园星球地上的遗产实例：离 <paramref name="p"/> 最近、<paramref name="radius"/> 格内（右键用；O(关键物数)）。</summary>
        public static string GroundAt(CampaignState s, Vector2 p, float radius = -1f)
        {
            if (s == null)
            {
                return null;
            }
            if (radius <= 0f)
            {
                radius = Tuning("relic.pick_radius", 2.5f);
            }
            string best = null;
            float bestD = radius * radius;
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (!IsHeritage(q) || q.State != RegionQuestItemState.OnGround || q.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                float d = (q.Position - p).sqrMagnitude;
                if (d <= bestD)
                {
                    bestD = d;
                    best = q.SalvageInstanceId;
                }
            }
            return best;
        }

        /// <summary>留在这处遗迹里（遗迹点格心 1 格内）的遗产实例；没有返回 null。</summary>
        public static RegionQuestItemRecord LeftAtRelic(CampaignState s, int x, int y)
        {
            foreach (RegionQuestItemRecord q in s?.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (IsHeritage(q) && q.State == RegionQuestItemState.OnGround && q.RegionId == HomeValleyLayout.RegionId
                    && Mathf.Abs(q.Position.x - x) <= 1f && Mathf.Abs(q.Position.y - y) <= 1f)
                {
                    return q;
                }
            }
            return null;
        }

        /// <summary>FG-GAP-127：建造校验用——方框里挡建造的点：没回收的遗迹点 + 留在家园星球地上的遗产（格）。O(方框里的分布格 + 关键物数)。</summary>
        public static void BlockingPointsIn(CampaignState s, int minX, int minY, int maxX, int maxY, List<GridCell> into, List<bool> known = null)
        {
            into.Clear();
            known?.Clear();
            if (s == null)
            {
                return;
            }
            if (WorldGenService.ContextFor(s) != null)
            {
                Scratch.Clear();
                WorldGenQuery.FeaturesIn(s, minX, minY, maxX, maxY, Scratch);
                foreach (WorldFeature f in Scratch)
                {
                    if (f.Kind == WorldFeatureKind.Relic && f.X >= minX && f.X <= maxX && f.Y >= minY && f.Y <= maxY && !HomeExplorationService.IsRecovered(s, f.Id))
                    {
                        into.Add(new GridCell(f.X, f.Y));
                        known?.Add(HomeExplorationService.IsFound(s, f.Id)); // 还没发现的：原因里不写坐标（不泄露迷雾里的点位）
                    }
                }
                Scratch.Clear();
            }
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (IsHeritage(q) && q.State == RegionQuestItemState.OnGround && q.RegionId == HomeValleyLayout.RegionId)
                {
                    var c = new GridCell(Mathf.RoundToInt(q.Position.x), Mathf.RoundToInt(q.Position.y));
                    if (c.X >= minX && c.X <= maxX && c.Y >= minY && c.Y <= maxY && !into.Contains(c))
                    {
                        into.Add(c);
                        known?.Add(true);
                    }
                }
            }
        }

        // ── 找到（回收遗迹完工）──────────────────────────────────────────────────

        /// <summary>
        /// 回收遗迹完工时调（<see cref="HomeExplorationService.CompleteRecovery"/>）：这处遗迹放着遗产、还没找到过 → 生成遗产实例放在遗迹点上，
        /// 装进回收它的机器的货舱（装不下就留在原地，卡片负向）。返回找到的遗产（没有返回 null）。
        /// </summary>
        public static Heritage OnRelicRecovered(CampaignState s, string relicId, int x, int y, string placeName, int machineLogicId)
        {
            Heritage h = AtRelic(s, relicId);
            if (h == null || IsObtained(s, h.Id) || FindItem(s, h.Id) != null)
            {
                return null;
            }
            var q = new RegionQuestItemRecord
            {
                SalvageInstanceId = InstanceIdOf(h.Id),
                RegionId = HomeValleyLayout.RegionId,
                ContentId = ContentId,
                State = RegionQuestItemState.OnGround,
                CarrierLogicId = 0,
                Position = new Vector2(x, y),
            };
            s.RegionQuestItems = Append(s.RegionQuestItems, q);
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.HeritageFirstFound);
            if (TryLoad(s, q, machineLogicId, out int free))
            {
                NotificationCenter.Post(NotifyType, GameText.Format("heritage.notify.found", placeName, x, y, Display(h), MachineNaming.Short(machineLogicId)), new Vector3(x, 0f, y));
                FeedbackCues.Raise(FeedbackCueId.Pickup);
            }
            else
            {
                string who = machineLogicId > 0 ? MachineNaming.Short(machineLogicId) : GameText.Get("relic.hover.ordered_pool");
                NotificationCenter.Post(NotifyType, GameText.Format("heritage.notify.left", placeName, x, y, Display(h), who, free, Space), new Vector3(x, 0f, y));
                GuidanceHooks.Raise(GuidanceHooks.HeritageFirstLeftBehind);
            }
            return h;
        }

        /// <summary>把地上的遗产装进这台机器的货舱：活着、空余 ≥ 遗产占用才装。<paramref name="free"/> = 这台机器装之前的空余格。</summary>
        public static bool TryLoad(CampaignState s, RegionQuestItemRecord q, int machineLogicId, out int free, string completingOrderId = null)
        {
            free = 0;
            if (q == null || q.State != RegionQuestItemState.OnGround || !MachineRegistry.TryGetRecord(machineLogicId, out MachineRecord m) || m == null || !m.IsAlive)
            {
                return false;
            }
            free = LootPickService.FreeSpace(s, m);
            if (free < Space)
            {
                return false;
            }
            q.State = RegionQuestItemState.Carried;
            q.CarrierLogicId = machineLogicId;
            q.Position = default;
            Revision++;
            LootPickService.Touch();
            CancelStalePickup(s, q, completingOrderId);
            return true;
        }

        /// <summary>
        /// 遗产离开地面（被装走 / 入库）后，指着它、还没完工的取回单作废（审查 P2：否则领单机器会走到 <see cref="PositionOf"/> 退回的核心位置白跑一趟）。
        /// <paramref name="exceptOrderId"/> = 正在完工的那一张（不取消它自己）。O(工单数)，只在遗产状态变化时。
        /// </summary>
        public static void CancelStalePickup(CampaignState s, RegionQuestItemRecord q, string exceptOrderId)
        {
            if (s == null || !IsHeritage(q) || q.State == RegionQuestItemState.OnGround)
            {
                return;
            }
            WorkOrderRecord o = HomeExplorationService.ActiveOrder(s, q.SalvageInstanceId);
            if (o == null || o.WorkOrderId == exceptOrderId)
            {
                return;
            }
            Vector2 at = o.AssignedMachineLogicId > 0 && MachineRegistry.TryGetLivePosition(o.AssignedMachineLogicId, out Vector2 live) ? live : HomeValleyLayout.Core.Position;
            HomeValleyWorkOrders.CancelOrder(s, o.WorkOrderId, at);
        }

        // ── 取回（遗产留在原地时的取回单）────────────────────────────────────────

        /// <summary>取回单能不能下：能返回 null，否则返回给玩家看的原因。</summary>
        public static string CheckPickup(CampaignState s, string instanceId, out RegionQuestItemRecord q)
        {
            q = FindItem(s, HeritageIdOf(instanceId));
            if (q == null || q.State != RegionQuestItemState.OnGround || q.RegionId != HomeValleyLayout.RegionId)
            {
                return GameText.Get("relic.reason.missing");
            }
            return null;
        }

        public static Vector2 PositionOf(CampaignState s, string instanceId)
        {
            RegionQuestItemRecord q = FindItem(s, HeritageIdOf(instanceId));
            return q != null && q.State == RegionQuestItemState.OnGround ? q.Position : HomeValleyLayout.Core.Position;
        }

        /// <summary>取回单完工（<see cref="HomeExplorationService.CompleteRecovery"/> 按实例 ID 分流过来）：装得下就装进货舱，装不下继续留在原地并说明。</summary>
        public static bool CompletePickup(CampaignState s, WorkOrderRecord order)
        {
            if (order == null || CheckPickup(s, order.TargetId, out RegionQuestItemRecord q) != null)
            {
                return false;
            }
            Heritage h = Find(HeritageIdOf(order.TargetId));
            int x = Mathf.RoundToInt(q.Position.x);
            int y = Mathf.RoundToInt(q.Position.y);
            if (TryLoad(s, q, order.AssignedMachineLogicId, out int free, order.WorkOrderId))
            {
                NotificationCenter.Post(NotifyType, GameText.Format("heritage.notify.picked", MachineNaming.Short(order.AssignedMachineLogicId), x, y, Display(h)), new Vector3(x, 0f, y));
                FeedbackCues.Raise(FeedbackCueId.Pickup);
                return true;
            }
            NotificationCenter.Post(NotifyType, GameText.Format("heritage.notify.still_full", MachineNaming.Short(order.AssignedMachineLogicId), x, y, Display(h), free, Space), new Vector3(x, 0f, y));
            return false;
        }

        // ── 入库（带回核心 / 仓库附近）───────────────────────────────────────────

        /// <summary>
        /// 遗产被带回（<see cref="CargoService"/> 发现携带者在核心 / 仓库附近，或地上的就在那里，已把实例标成 Recovered）：记入核心保管库并按类别生效。幂等。
        /// </summary>
        public static void OnDelivered(CampaignState s, RegionQuestItemRecord q)
        {
            HomeExplorationState st = HomeExplorationService.StateOf(s);
            Heritage h = Find(HeritageIdOf(q?.SalvageInstanceId));
            if (st == null || h == null || IsObtained(s, h.Id))
            {
                return;
            }
            CancelStalePickup(s, q, null);
            st.HeritageObtained = Append(st.HeritageObtained, h.Id);
            st.HeritageObtainedTicks = Append(st.HeritageObtainedTicks, GameClock.Ticks);
            string effect = ApplyEffect(s, st, h);
            MechanicCodexUnlock(h.Id);
            Revision++;
            NotificationCenter.Post(NotifyType, GameText.Format("heritage.notify.delivered", Display(h), effect));
            FeedbackCues.Raise(FeedbackCueId.AnalysisComplete);
            GuidanceHooks.Raise(GuidanceHooks.HeritageFirstDelivered);
        }

        private static string ApplyEffect(CampaignState s, HomeExplorationState st, Heritage h)
        {
            switch (h.Category)
            {
                case CatBlueprint:
                    Unlock(s, h.UnlockId);
                    return GameText.Get("heritage.effect.blueprint");
                case CatFirmware:
                    Unlock(s, h.UnlockId);
                    PrimitiveInventory.GrantHeritageFirmware(s, InstanceIdOf(h.Id), h.UnlockId);
                    return GameText.Format("heritage.effect.firmware", Signal.FirmwareKinds.DisplayName(h.UnlockId) ?? h.UnlockId);
                case CatArchive:
                    int amount = Math.Max(0, h.TechData);
                    string tx = InstanceIdOf(h.Id);
                    if (amount > 0 && CampaignEconomyLedger.Find(s, tx) == null)
                    {
                        CampaignEconomyLedger.ProposeProduce(s, tx, h.Id, CampaignEconomyLedger.ResourceTechData, amount);
                        CampaignEconomyLedger.Reserve(s, tx);
                        CampaignEconomyLedger.MarkRunning(s, tx);
                        if (CampaignEconomyLedger.Commit(s, tx).Success)
                        {
                            st.HeritageTechData += amount;
                            HomeInventory.Touch();
                        }
                    }
                    return GameText.Format("heritage.effect.archive", amount);
                default:
                    return GameText.Get("heritage.effect.memento");
            }
        }

        private static void Unlock(CampaignState s, string contentId)
        {
            if (string.IsNullOrEmpty(contentId) || contentId == "none")
            {
                return;
            }
            s.UnlockedContentIds ??= Array.Empty<string>();
            if (Array.IndexOf(s.UnlockedContentIds, contentId) < 0)
            {
                s.UnlockedContentIds = Append(s.UnlockedContentIds, contentId);
            }
        }

        private static void MechanicCodexUnlock(string heritageId) => GameLogic.Progression.MechanicCodex.Unlock(CodexIdOf(heritageId));

        /// <summary>进入战役（新建 / 读档 / 回滚）时：挑遗迹点（上下文就绪时），图鉴补解锁本存档已入库的遗产（跨存档图鉴，旧档 / 换机后补齐）。</summary>
        public static void OnCampaignEntered(CampaignState s)
        {
            if (s == null)
            {
                return;
            }
            EnsurePlaced(s);
            ReconcileRecovered(s);
            foreach (string id in HomeExplorationService.StateOf(s)?.HeritageObtained ?? Array.Empty<string>())
            {
                MechanicCodexUnlock(id);
            }
            Revision++;
        }

        /// <summary>
        /// 机器阵亡（<see cref="MachineRegistry.MarkDeadByLogicId"/>）：它携带的遗产在核心 / 仓库附近就直接入库，否则掉在阵亡处（永不消失），通知写位置。
        /// </summary>
        public static void OnMachineDied(CampaignState s, MachineRecord m, Vector2 at)
        {
            if (s == null || m == null)
            {
                return;
            }
            string region = string.IsNullOrEmpty(m.RegionId) ? HomeValleyLayout.RegionId : m.RegionId;
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (!IsHeritage(q) || q.State != RegionQuestItemState.Carried || q.CarrierLogicId != m.LogicId)
                {
                    continue;
                }
                q.CarrierLogicId = 0;
                if (region == HomeValleyLayout.RegionId && LootPickService.IsDepotPosition(s, region, at))
                {
                    q.State = RegionQuestItemState.Recovered;
                    OnDelivered(s, q);
                    continue;
                }
                q.State = RegionQuestItemState.OnGround;
                q.RegionId = region;
                q.Position = at;
                Revision++;
                Heritage h = Find(HeritageIdOf(q.SalvageInstanceId));
                NotificationCenter.Post(NotifyType, GameText.Format("heritage.notify.dropped", MachineNaming.Short(m), Display(h), Mathf.RoundToInt(at.x), Mathf.RoundToInt(at.y)),
                    new Vector3(at.x, 0f, at.y));
            }
        }

        /// <summary>
        /// 远征地点撤离结算（<see cref="Regions.FracturedCityRegion.ResolveExtraction"/> / <see cref="Regions.FoundryOutpostRegion.ResolveExtraction"/>）里遇到一件携带中的关键物时先问这里：
        /// 不是遗产返回 false（调用方照旧处理）；是遗产返回 true，由这里结算、调用方跳过它：
        /// 携带者从 <paramref name="regionId"/> 撤离成功 → 标 Recovered 并入库生效（带回家园，FGT-EXP-008 携带物不丢）；
        /// 携带者活着但不在这片区域（没参加这次远征，例如在家园星球上带着遗产往回开）→ 原样不动；
        /// 其余（携带者在这片区域没能撤离 / 找不到记录）→ 掉在它最后的位置、留在这片区域（永不丢失），通知写位置。
        /// </summary>
        public static bool ResolveExtractionItem(CampaignState s, RegionQuestItemRecord q, string regionId, bool survived)
        {
            if (s == null || !IsHeritage(q) || q.State != RegionQuestItemState.Carried)
            {
                return IsHeritage(q);
            }
            bool hasCarrier = MachineRegistry.TryGetRecord(q.CarrierLogicId, out MachineRecord m) && m != null;
            string carrierRegion = hasCarrier ? (string.IsNullOrEmpty(m.RegionId) ? HomeValleyLayout.RegionId : m.RegionId) : null;
            if (survived && carrierRegion == regionId)
            {
                q.State = RegionQuestItemState.Recovered;
                q.CarrierLogicId = 0;
                q.Position = default;
                Revision++;
                OnDelivered(s, q);
                return true;
            }
            if (hasCarrier && m.IsAlive && carrierRegion != regionId)
            {
                return true; // 不在这次撤离的区域里：不碰
            }
            string carrierName = hasCarrier ? MachineNaming.Short(m) : string.Empty;
            Economy.BlackBoxService.DropFromCarrier(s, q);
            q.RegionId = carrierRegion ?? regionId;
            Revision++;
            Heritage h = Find(HeritageIdOf(q.SalvageInstanceId));
            NotificationCenter.Post(NotifyType, GameText.Format("heritage.notify.dropped", carrierName, Display(h), Mathf.RoundToInt(q.Position.x), Mathf.RoundToInt(q.Position.y)),
                new Vector3(q.Position.x, 0f, q.Position.y));
            return true;
        }

        /// <summary>对账：实例已是 Recovered、却没记进保管库的遗产（旧版撤离结算留下的坏数据）补一次入库。O(关键物数)，只在进入战役时。</summary>
        private static void ReconcileRecovered(CampaignState s)
        {
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (IsHeritage(q) && q.State == RegionQuestItemState.Recovered)
                {
                    Heritage h = Find(HeritageIdOf(q.SalvageInstanceId));
                    if (h != null && !IsObtained(s, h.Id))
                    {
                        OnDelivered(s, q);
                    }
                }
            }
        }

        // ── 读点（面板 / 图鉴 / 自检）───────────────────────────────────────────

        public struct Counts
        {
            public int Total;
            public int Obtained;
            public int Moving;
            public int[] TotalBy;
            public int[] ObtainedBy;
        }

        public static Counts CountsOf(CampaignState s)
        {
            EnsureLoaded();
            var c = new Counts { TotalBy = new int[Categories.Length], ObtainedBy = new int[Categories.Length] };
            foreach (Heritage h in Rows)
            {
                int ci = Math.Max(0, Array.IndexOf(Categories, h.Category));
                c.Total++;
                c.TotalBy[ci]++;
                HeritageStatus st = StatusOf(s, h.Id);
                if (st == HeritageStatus.Obtained)
                {
                    c.Obtained++;
                    c.ObtainedBy[ci]++;
                }
                else if (st == HeritageStatus.OnGround || st == HeritageStatus.Carried)
                {
                    c.Moving++;
                }
            }
            return c;
        }

        public static string StatusText(CampaignState s)
        {
            Counts c = CountsOf(s);
            string line = GameText.Format("heritage.vault.status", c.Obtained, c.Total, c.ObtainedBy[0], c.TotalBy[0], c.ObtainedBy[1], c.TotalBy[1],
                c.ObtainedBy[2], c.TotalBy[2], c.ObtainedBy[3], c.TotalBy[3]);
            return c.Moving > 0 ? line + "\n" + GameText.Format("heritage.vault.status_moving", c.Moving) : line;
        }

        /// <summary>展柜：已入库的纪念物（入库先后）。</summary>
        public static List<string> Showcase(CampaignState s)
        {
            var list = new List<string>();
            foreach (string id in HomeExplorationService.StateOf(s)?.HeritageObtained ?? Array.Empty<string>())
            {
                Heritage h = Find(id);
                if (h != null && h.Category == CatMemento)
                {
                    list.Add(id);
                }
            }
            return list;
        }

        /// <summary>收集页：全部遗产（表顺序）。</summary>
        public static List<string> Collection()
        {
            EnsureLoaded();
            var list = new List<string>(Rows.Count);
            foreach (Heritage h in Rows)
            {
                list.Add(h.Id);
            }
            return list;
        }

        /// <summary>收集页 / 展柜的一行：没找到的不剧透名字（“？？？ · 类别 · 第几幕范围”）。</summary>
        public static string RowText(CampaignState s, string heritageId)
        {
            Heritage h = Find(heritageId);
            if (h == null)
            {
                return heritageId ?? string.Empty;
            }
            switch (StatusOf(s, h.Id))
            {
                case HeritageStatus.Obtained:
                    return GameText.Format("heritage.state.obtained", Display(h), TickText(ObtainedTick(s, h.Id)));
                case HeritageStatus.Carried:
                    RegionQuestItemRecord cq = FindItem(s, h.Id);
                    return GameText.Format("heritage.state.carried", Display(h), MachineNaming.Short(cq?.CarrierLogicId ?? 0));
                case HeritageStatus.OnGround:
                    RegionQuestItemRecord gq = FindItem(s, h.Id);
                    return GameText.Format("heritage.state.ground", Display(h), Mathf.RoundToInt(gq?.Position.x ?? 0f), Mathf.RoundToInt(gq?.Position.y ?? 0f));
                default:
                    return GameText.Format("heritage.state.unknown", CategoryName(h.Category), h.Act);
            }
        }

        /// <summary>选中一行时的描述：找到过的 = 名称 + 类别作用 + 背景故事（+ 入库后的效果）；没找到的 = 去哪找。</summary>
        public static string DetailText(CampaignState s, string heritageId)
        {
            Heritage h = Find(heritageId);
            if (h == null)
            {
                return string.Empty;
            }
            HeritageStatus st = StatusOf(s, h.Id);
            if (st == HeritageStatus.Hidden || st == HeritageStatus.Unplaced)
            {
                return GameText.Format("heritage.vault.detail_locked", h.Act, h.MinDistance, h.MaxDistance);
            }
            var sb = new StringBuilder(256);
            sb.Append(Display(h)).Append('\n');
            sb.Append(GameText.Get("heritage.cat." + h.Category + ".effect")).Append('\n');
            sb.Append(GameText.Get(h.DescKey));
            if (st == HeritageStatus.Obtained && h.Category == CatBlueprint)
            {
                sb.Append('\n').Append(GameText.Get("heritage.effect.blueprint"));
            }
            return sb.ToString();
        }

        /// <summary>图鉴条目正文（已解锁）：类别作用 + 背景故事。</summary>
        public static string CodexBody(string heritageId)
        {
            Heritage h = Find(heritageId);
            return h == null ? string.Empty : GameText.Get("heritage.cat." + h.Category + ".effect") + "\n" + GameText.Get(h.DescKey);
        }

        /// <summary>图鉴条目的获取提示（未解锁）：类别 + 第几幕范围。</summary>
        public static string CodexHint(string heritageId)
        {
            Heritage h = Find(heritageId);
            return h == null ? string.Empty : GameText.Format("codex.heritage.locked_hint", CategoryName(h.Category), h.Act);
        }

        public static long ObtainedTick(CampaignState s, string heritageId)
        {
            HomeExplorationState st = HomeExplorationService.StateOf(s);
            int i = st != null ? Array.IndexOf(st.HeritageObtained, heritageId) : -1;
            return i >= 0 && i < st.HeritageObtainedTicks.Length ? st.HeritageObtainedTicks[i] : 0L;
        }

        private static string TickText(long ticks) => GameClock.FormatDayTime(ticks / (double)Math.Max(1, GameClock.StepHz));

        /// <summary>自检 / 存读档比对用的快照（放置、状态、入库）。</summary>
        public static string Snapshot(CampaignState s)
        {
            var sb = new StringBuilder(512);
            HomeExplorationState st = HomeExplorationService.StateOf(s);
            if (st == null)
            {
                return string.Empty;
            }
            for (int i = 0; i < st.HeritageIds.Length; i++)
            {
                string id = st.HeritageIds[i];
                RegionQuestItemRecord q = FindItem(s, id);
                sb.Append(id).Append('@').Append(i < st.HeritageRelics.Length ? st.HeritageRelics[i] : "").Append(':').Append((int)StatusOf(s, id));
                if (q != null)
                {
                    sb.Append('/').Append((int)q.State).Append('/').Append(q.CarrierLogicId).Append('/')
                        .Append(q.Position.x.ToString("0.##", CultureInfo.InvariantCulture)).Append(',').Append(q.Position.y.ToString("0.##", CultureInfo.InvariantCulture));
                }
                sb.Append(';');
            }
            sb.Append("|obt=").Append(string.Join(",", st.HeritageObtained)).Append("|tech=").Append(st.HeritageTechData);
            return sb.ToString();
        }

        private static T[] Append<T>(T[] a, T v)
        {
            a ??= Array.Empty<T>();
            var n = new T[a.Length + 1];
            Array.Copy(a, n, a.Length);
            n[a.Length] = v;
            return n;
        }
    }
}
