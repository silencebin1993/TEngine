using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GameConfig.fg;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>一个归还面积里程碑（fg.TbReturnMilestone 一行的运行时视图）。</summary>
    public sealed class ReturnMilestoneDef
    {
        public string Id;
        public int Cells;
        public string Reward;
        public float Value;
        public string NameKey;
        public string DescKey;
        public string MemoTitleKey;
        public string MemoBodyKey;
        public string Name => GameText.Get(NameKey);
        public string Desc => GameText.Get(DescKey);
    }

    /// <summary>再污染的结果（自检与 FG10-RAID-01 读）。</summary>
    public struct RepolluteResult
    {
        public int CellsRaised;
        public int LevelsRaised;
        public int BuildingsContaminated;
    }

    /// <summary>
    /// FG7-ENV-04（FG07 FGR-ENV-040～043；验收 FGT-ENV-005 / 006）：污染、净化塔与归还面积。<b>唯一写入口</b>（<see cref="WeatherState.Purification"/>，
    /// 以及生产代码里净化 / 再污染对格网污染层的改写）。
    ///
    /// - 污染格网：每格 0～3 级是格网区块里的局部数据（世界生成器随区块生成，FGR-GEN-030 / 031；不是固定坐标的全局网格），被改过的格子随区块差异进存档。
    ///   正常探索时没有任何地格表现；只在污染叠加层 / 建造模式里看得到等级（用户方向：地格只在建造等界面叠加）。
    /// - 净化塔（FGR-ENV-041）：推进在生产步里（<see cref="ProductionService"/> 的 purifier 模式：有电、冷却液够这段时间用才算在净化），
    ///   累计满 purify.hours_per_level 游戏小时调一次 <see cref="PurifyStep"/>：从中心（枢轴格）向外找“最里面还有污染的一环”（环号 = ⌈距离⌉），
    ///   把这一环里每格降 1 级。每一步 O(半径²) 的整数读写、每座塔每 2 游戏小时一次——与帧率、倍速、是否被观察无关（FGR-BASE-021）；
    ///   区块没加载时按种子同步生成（结果相同）。重叠的两座塔各自推进（重叠处更快，待用户复核）。
    /// - 归还面积（FGR-ENV-042）：生成时有污染、现在 0 级的格子数，在格网写入口里增量维护（<see cref="HomeGridMap.ReturnedCells"/>），与区块加载无关（B24）。
    ///   里程碑达成后奖励一直有效（天气损伤 / 信号覆盖由达成记录派生，稀土富矿达成时揭示一次）；每个里程碑发一条核心备忘（GameEvent <see cref="CoreMemoEvent"/>，FG11 叙事接口）。
    /// - 再污染（FGR-ENV-043）：<see cref="Repollute"/> 是化工突袭的接口（FG10-RAID-01 调用）：一片格子污染升级，上面的建筑不摧毁、进入“受污染”状态（天气损伤加倍），
    ///   所在格子全部净化到 0 级后自动解除。
    /// 不做玩家没要求的事（FGR-BASE-020）：净化塔只净化自己的范围、只用玩家接给它的冷却液；奖励不改玩家的任何设置。
    /// </summary>
    public static class PurificationService
    {
        public const string RewardWeather = "weather_damage";
        public const string RewardRichOre = "rich_ore";
        public const string RewardSignal = "signal_coverage";
        public const string RewardAchievement = "achievement";
        /// <summary>里程碑 / 受污染 / 稀土富矿变化时发（参数 = 归还面积）。</summary>
        public const string ChangedEvent = "Purification.Changed";
        /// <summary>FG11 叙事接口：一条核心备忘解锁（参数 = 里程碑 ID；标题 / 正文键在 fg.TbReturnMilestone）。FG9-NAR-01 的资料库订阅它，并可按存档里的达成记录补录。</summary>
        public const string CoreMemoEvent = "Narrative.CoreMemo";

        private static bool _installed;
        private static readonly List<ReturnMilestoneDef> Milestones = new List<ReturnMilestoneDef>(4);
        private static bool _loaded;
        private static readonly Dictionary<int, int[][]> RingCache = new Dictionary<int, int[][]>();
        private static readonly List<ProductionService.Producer> Purifiers = new List<ProductionService.Producer>(8);
        private static int _purifierRevision = -1;
        private static CampaignState _purifierState;
        private static readonly HashSet<string> ContaminatedSet = new HashSet<string>(StringComparer.Ordinal);
        private static string[] _contaminatedRef;
        private static readonly Dictionary<string, TowerInfo> Infos = new Dictionary<string, TowerInfo>(StringComparer.Ordinal);
        private static readonly List<GridCell> Footprint = new List<GridCell>(16);
        private static readonly HashSet<string> RaisedBuildings = new HashSet<string>(StringComparer.Ordinal);
        private static bool _purifiedHooked;
        private static long _lastCheckedArea = -1;
        private static int _lastRecoveryEdit = -1;

        /// <summary>里程碑 / 受污染 / 稀土富矿 / 净化一步完成时 +1（顶栏、日历、面板据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;
        public static bool Installed => _installed;

        // ── 统计（自检读）──
        public static int StepCount { get; private set; }
        public static long CellsLowered { get; private set; }
        public static int MaxStepCells { get; private set; }
        public static double LastStepMs { get; private set; }
        public static double MaxStepMs { get; private set; }
        public static int MilestoneCount { get; private set; }
        public static int RecoveredCount { get; private set; }
        public static double LastRichSearchMs { get; private set; }
        public static int LastRichSearchSamples { get; private set; }

        /// <summary>正式入口（<c>GameRoot.Startup</c>）：接上酸雨的净化区判定与家园信号覆盖倍率。幂等。</summary>
        public static void Install()
        {
            _installed = true;
            WeatherService.PurifiedProvider = InPurifiedZone;
            Signal.SignalCoverageService.HomeRadiusFactorProvider = CoverageFactor;
            Revision++;
        }

        /// <summary>自检之间复位（断开接入点、清缓存，不动存档）。</summary>
        public static void ResetForTests()
        {
            _installed = false;
            if (WeatherService.PurifiedProvider == (Func<CampaignState, GridCell, bool>)InPurifiedZone)
            {
                WeatherService.PurifiedProvider = null;
            }
            if (Signal.SignalCoverageService.HomeRadiusFactorProvider == (Func<CampaignState, float>)CoverageFactor)
            {
                Signal.SignalCoverageService.HomeRadiusFactorProvider = null;
            }
            _loaded = false;
            Milestones.Clear();
            Purifiers.Clear();
            _purifierRevision = -1;
            _purifierState = null;
            ContaminatedSet.Clear();
            _contaminatedRef = null;
            Infos.Clear();
            _zoneState = null;
            _zoneStep = int.MinValue;
            _zoneCount = 0;
            _purifiedHooked = false;
            _lastCheckedArea = -1;
            _lastRecoveryEdit = -1;
            StepCount = 0;
            InfoScans = 0;
            CellsLowered = 0;
            MaxStepCells = 0;
            LastStepMs = MaxStepMs = 0;
            MilestoneCount = 0;
            RecoveredCount = 0;
            Revision++;
        }

        /// <summary>自检：清零“最慢一次 / 最多一次”的统计。</summary>
        public static void ResetStepStats()
        {
            LastStepMs = MaxStepMs = 0;
            MaxStepCells = 0;
        }

        // ── 表与调参 ─────────────────────────────────────────────────────────────

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;
        public static double HoursPerLevel => Math.Max(0.01, Tuning("purify.hours_per_level", 2f));
        /// <summary>一次降级要累计的世界步数（统一时钟：暂停不走、倍速只是一帧多走几步）。</summary>
        public static long StepTicks => Math.Max(1L, GameClock.TicksFor(HoursPerLevel * GameClock.HourSeconds));
        public static int RichRadius => Math.Max(4, (int)Math.Round(Tuning("purify.rich_ore.radius", 12f)));
        public static int RichYieldMult => Math.Max(1, (int)Math.Round(Tuning("purify.rich_ore.yield_mult", 2f)));
        public static int SearchRadius => Math.Max(32, (int)Math.Round(Tuning("purify.rich_ore.search_radius", 400f)));
        public static int SearchStep => Math.Max(1, (int)Math.Round(Tuning("purify.rich_ore.search_step", 6f)));

        public static IReadOnlyList<ReturnMilestoneDef> AllMilestones
        {
            get
            {
                EnsureLoaded();
                return Milestones;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            Milestones.Clear();
            try
            {
                TbReturnMilestone t = ConfigSystem.Instance.Tables?.TbReturnMilestone;
                if (t != null)
                {
                    foreach (ReturnMilestone r in t.DataList)
                    {
                        Milestones.Add(new ReturnMilestoneDef
                        {
                            Id = r.Id, Cells = r.Cells, Reward = r.Reward, Value = r.Value, NameKey = r.NameKey, DescKey = r.DescKey,
                            MemoTitleKey = r.MemoTitleKey, MemoBodyKey = r.MemoBodyKey,
                        });
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("[PurificationService] 读取 fg.TbReturnMilestone 失败：" + e.Message);
            }
            Milestones.Sort((a, b) => a.Cells.CompareTo(b.Cells));
        }

        // ── 状态 ─────────────────────────────────────────────────────────────────

        public static PurificationState StateOf(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            s.Weather ??= new WeatherState();
            return s.Weather.Purification ??= new PurificationState();
        }

        /// <summary>已归还的面积（格）：生成时有污染、现在 0 级的格子数。</summary>
        public static long ReturnedArea(CampaignState s) => s == null ? 0 : HomeGridService.MapFor(s).ReturnedCells;

        /// <summary>净化塔这一类建筑（生产模式 purifier）：可以压在 2 级以上的污染地上（FGR-LOG-012）。</summary>
        public static bool IsPollutionExempt(string typeId) =>
            typeId != null && ProducerCatalog.TryGet(typeId, out ProducerDef def) && def.Mode == ProducerMode.Purifier;

        public static bool IsPurifierType(string typeId) => IsPollutionExempt(typeId);

        /// <summary>这座净化塔现在的净化半径（格；等级行的 value）。</summary>
        public static int RadiusOf(BuildingRecord b)
        {
            if (b == null)
            {
                return 0;
            }
            BuildingTier row = BuildingOps.TierRow(b.BuildingTypeId, BuildingOps.TierOf(b));
            return row != null ? Math.Max(1, (int)Math.Round(row.Value)) : 8;
        }

        /// <summary>某一级的净化半径（放置预览、研发说明用）。</summary>
        public static int RadiusOfTier(string typeId, int tier)
        {
            BuildingTier row = BuildingOps.TierRow(typeId, tier);
            return row != null ? Math.Max(1, (int)Math.Round(row.Value)) : 8;
        }

        /// <summary>净化的中心格 = 枢轴格（3×3 建筑的正中那格）。</summary>
        public static GridCell CenterOf(BuildingRecord b) => new GridCell(b.GridX, b.GridY);

        // ── 环 ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 半径 R 的环表：第 k 环 = 与中心的距离向上取整等于 k 的格子（k = 0 是中心格本身；dx² + dy² ≤ R² 的格子都在范围内）。
        /// 整数开方，确定性；按半径缓存（每个半径只算一次）。偏移打包为 (dx + 4096) | (dy + 4096) &lt;&lt; 13。
        /// </summary>
        public static int[][] Rings(int radius)
        {
            radius = Math.Max(0, Math.Min(256, radius));
            if (RingCache.TryGetValue(radius, out int[][] rings))
            {
                return rings;
            }
            var lists = new List<int>[radius + 1];
            for (int i = 0; i <= radius; i++)
            {
                lists[i] = new List<int>(8 * (i + 1));
            }
            int r2 = radius * radius;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int d2 = dx * dx + dy * dy;
                    if (d2 > r2)
                    {
                        continue;
                    }
                    lists[RingOf(d2)].Add(Pack(dx, dy));
                }
            }
            rings = new int[radius + 1][];
            for (int i = 0; i <= radius; i++)
            {
                rings[i] = lists[i].ToArray();
            }
            RingCache[radius] = rings;
            return rings;
        }

        /// <summary>环号 = ⌈√d²⌉（整数运算）。</summary>
        public static int RingOf(int d2)
        {
            if (d2 <= 0)
            {
                return 0;
            }
            int k = (int)Math.Sqrt(d2);
            while (k * k > d2)
            {
                k--;
            }
            while ((k + 1) * (k + 1) <= d2)
            {
                k++;
            }
            return k * k == d2 ? k : k + 1;
        }

        private static int Pack(int dx, int dy) => (dx + 4096) | ((dy + 4096) << 13);
        private static int Dx(int packed) => (packed & 0x1FFF) - 4096;
        private static int Dy(int packed) => (packed >> 13) - 4096;

        // ── 推进（ProductionService 的 purifier 模式调）─────────────────────────────

        /// <summary>这段时间（<paramref name="ticks"/> 个世界步）塔有电、冷却液够用：累计净化进度，满一次降级的时长就推进一环。</summary>
        public static void Accrue(CampaignState state, ProductionService.Producer p, long ticks)
        {
            if (state == null || p?.Rec == null || ticks <= 0)
            {
                return;
            }
            long st = StepTicks;
            p.Rec.PurifyTicks += ticks;
            int guard = 0;
            while (p.Rec.PurifyTicks >= st && guard++ < 64)
            {
                p.Rec.PurifyTicks -= st;
                PurifyStep(state, p.Building, p.Rec);
            }
        }

        /// <summary>
        /// 一次降级：从中心向外找最里面还有污染的一环，这一环里每格降 1 级（全清干净后下一次转向下一环）。返回这次降了几格。
        /// O(半径²) 的整数读写（最坏：范围内全干净时扫完整个圆）。超出世界坐标上限的格子跳过。
        /// </summary>
        public static int PurifyStep(CampaignState state, BuildingRecord b, ProducerRecord rec)
        {
            if (state == null || b == null)
            {
                return 0;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            HomeGridMap map = HomeGridService.MapFor(state);
            int radius = RadiusOf(b);
            int[][] rings = Rings(radius);
            GridCell c = CenterOf(b);
            int limit = GridContent.TuningInt("world.coord_limit");
            int lowered = 0;
            for (int k = 0; k < rings.Length && lowered == 0; k++)
            {
                foreach (int off in rings[k])
                {
                    var cell = new GridCell(c.X + Dx(off), c.Y + Dy(off));
                    if (limit > 0 && !WorldCoord.WithinLimit(cell, limit))
                    {
                        continue;
                    }
                    byte level = map.GetPollution(cell);
                    if (level == 0)
                    {
                        continue;
                    }
                    map.SetPollution(cell, (byte)(level - 1));
                    lowered++;
                }
            }
            StepCount++;
            if (lowered > 0)
            {
                CellsLowered += lowered;
                MaxStepCells = Math.Max(MaxStepCells, lowered);
                if (rec != null)
                {
                    rec.PurifiedLevels += lowered;
                }
                PurificationState d = StateOf(state);
                d.LevelsPurified += lowered;
                AfterPollutionChanged(state);
            }
            LastStepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            MaxStepMs = Math.Max(MaxStepMs, LastStepMs);
            return lowered;
        }

        /// <summary>净化 / 再污染改了格子之后：受污染的建筑够不够解除、归还面积有没有跨过里程碑、第一次净化的钩子。O(受污染建筑 × 占地 + 里程碑数)。</summary>
        private static void AfterPollutionChanged(CampaignState state)
        {
            Revision++;
            CheckRecovered(state);
            CheckMilestones(state);
        }

        /// <summary>
        /// FG7-ENV-04：每个世界模拟步（<c>WorldSimulation.StepOnce</c>，天气之后）：每游戏秒查一次归还面积与受污染建筑（O(1) 比较 + 格网有改动时 O(受污染建筑 × 占地)）。
        /// 净化塔以外的写入（读档、将来收复领地的净化）同样会触发里程碑与解除，不依赖塔的推进时机。
        /// </summary>
        public static void WorldStep(CampaignState state, long ticksBefore, int worldHz)
        {
            if (state == null || ticksBefore % Math.Max(1, worldHz) != 0)
            {
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(state);
            // 受污染的建筑：格网有改动（净化 / 再污染）或名单不空（建筑可能被拆除 / 摧毁）时每游戏秒查一次，O(受污染建筑 × 占地)。
            if (map.TerrainEditRevision != _lastRecoveryEdit || (state.Weather?.Purification?.Contaminated?.Length ?? 0) > 0)
            {
                _lastRecoveryEdit = map.TerrainEditRevision;
                CheckRecovered(state);
            }
            if (map.ReturnedCells != _lastCheckedArea)
            {
                CheckMilestones(state);
            }
            // 统计趋势（B13）：每跨过一个游戏小时记一次归还面积（O(1)，进存档，读档后接着同一序列）。
            long hour = GameClock.Ticks / Math.Max(1L, GameClock.TicksFor(GameClock.HourSeconds));
            PurificationState d = StateOf(state);
            if (hour != d.SampleHour)
            {
                d.SampleHour = hour;
                long[] old = d.AreaSamples ?? Array.Empty<long>();
                int keep = Math.Min(old.Length, TrendSamples - 1);
                var next = new long[keep + 1];
                Array.Copy(old, old.Length - keep, next, 0, keep);
                next[keep] = map.ReturnedCells;
                d.AreaSamples = next;
            }
        }

        /// <summary>趋势最多保留几个小时样本（含当前）：25 个 = 最近 24 游戏小时的变化。</summary>
        public const int TrendSamples = 25;

        /// <summary>
        /// 统计面板“物流与能源”页的环境段（B19 关键产出进统计面板、B13 趋势）：已归还 N 格与下一个里程碑、最近 24 游戏小时的增量、净化塔在净化 / 暂停的座数与累计降级 / 冷却液、
        /// 再污染与受污染的建筑。O(净化塔数)，只在面板打开刷新时调用。
        /// </summary>
        public static void BuildStatsLines(CampaignState s, List<(string Text, string Cls)> into)
        {
            if (s == null || into == null)
            {
                return;
            }
            PurificationState d = StateOf(s);
            into.Add((GameText.Get("stats.env.title"), "st-row-title"));
            into.Add((GameText.Format("stats.env.returned", ReturnedArea(s).ToString("N0", CultureInfo.InvariantCulture), NextMilestoneText(s)), null));
            long[] samples = d.AreaSamples ?? Array.Empty<long>();
            if (samples.Length >= 2)
            {
                int hours = samples.Length - 1;
                long gain = samples[samples.Length - 1] - samples[0];
                into.Add((GameText.Format("stats.env.trend", hours, gain.ToString("N0", CultureInfo.InvariantCulture), (gain / (double)hours).ToString("0.#", CultureInfo.InvariantCulture)), null));
            }
            else
            {
                into.Add((GameText.Get("stats.env.trend_none"), "st-row-dim"));
            }
            IReadOnlyList<ProductionService.Producer> towers = PurifiersOf(s);
            if (towers.Count == 0)
            {
                into.Add((GameText.Get("stats.env.none"), "st-row-dim"));
            }
            else
            {
                int working = 0;
                long levels = 0, coolant = 0;
                foreach (ProductionService.Producer p in towers)
                {
                    working += p.Rec != null && p.Rec.PurifySupplied ? 1 : 0;
                    levels += p.Rec?.PurifiedLevels ?? 0;
                    coolant += p.Rec?.CoolantUsedMl ?? 0;
                }
                into.Add((GameText.Format("stats.env.towers", towers.Count, working, towers.Count - working, levels.ToString("N0", CultureInfo.InvariantCulture),
                    (coolant / 1000).ToString("N0", CultureInfo.InvariantCulture)), working < towers.Count ? "st-row-warn" : null));
            }
            if (d.Repollutions > 0 || ContaminatedCount(s) > 0)
            {
                into.Add((GameText.Format("stats.env.repollution", d.Repollutions, d.LevelsRaised.ToString("N0", CultureInfo.InvariantCulture), ContaminatedCount(s)),
                    ContaminatedCount(s) > 0 ? "st-row-warn" : null));
            }
        }

        // ── 归还面积里程碑（FGR-ENV-042）────────────────────────────────────────────

        public static bool IsReached(CampaignState s, string milestoneId)
        {
            PurificationState d = s?.Weather?.Purification;
            return d?.Reached != null && Array.IndexOf(d.Reached, milestoneId) >= 0;
        }

        /// <summary>下一个还没达成的里程碑（全部达成 = null）。</summary>
        public static ReturnMilestoneDef NextMilestone(CampaignState s)
        {
            foreach (ReturnMilestoneDef m in AllMilestones)
            {
                if (!IsReached(s, m.Id))
                {
                    return m;
                }
            }
            return null;
        }

        /// <summary>归还面积跨过的里程碑逐个达成（按面积从小到大；达成一次就记下，之后面积因再污染掉回去也不收回奖励）。</summary>
        public static void CheckMilestones(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            long area = ReturnedArea(state);
            _lastCheckedArea = area;
            if (area > 0 && !_purifiedHooked)
            {
                _purifiedHooked = true;
                GuidanceHooks.Raise(GuidanceHooks.EnvFirstPurified);
            }
            foreach (ReturnMilestoneDef m in AllMilestones)
            {
                if (area >= m.Cells && !IsReached(state, m.Id))
                {
                    Reach(state, m, area);
                }
            }
        }

        private static void Reach(CampaignState state, ReturnMilestoneDef m, long area)
        {
            PurificationState d = StateOf(state);
            d.Reached = Append(d.Reached, m.Id);
            d.ReachedTicks = Append(d.ReachedTicks, GameClock.Ticks);
            MilestoneCount++;
            Revision++;
            string detail = GameText.Get(m.DescKey);
            switch (m.Reward)
            {
                case RewardRichOre:
                    detail = RevealRichDeposit(state, m) ?? detail;
                    break;
                case RewardSignal:
                    Signal.SignalCoverageService.Invalidate(); // 下一次取样按新半径重建覆盖
                    break;
            }
            Vector3 at = CoreAt(state);
            NotificationCenter.Post("purify_milestone", GameText.Format("purify.notify.milestone", area.ToString("N0", CultureInfo.InvariantCulture), detail), at);
            NotificationCenter.Post("core_memo", GameText.Format("purify.notify.memo", GameText.Get(m.MemoTitleKey), GameText.Get(m.MemoBodyKey)), at);
            GuidanceHooks.Raise(GuidanceHooks.EnvFirstReturnMilestone);
            GameEvent.Send(CoreMemoEvent, m.Id);
            GameEvent.Send(ChangedEvent, area);
        }

        /// <summary>全局天气损伤系数：1 + 已达成的“天气损伤”里程碑的值之和（5,000 格 = 1 − 0.25 = 0.75），不低于 0。</summary>
        public static double WeatherDamageFactor(CampaignState s) => Math.Max(0.0, 1.0 + SumReached(s, RewardWeather));

        /// <summary>家园信号覆盖半径倍率：1 + 已达成的“信号覆盖”里程碑的值之和（50,000 格 = 1.1）。</summary>
        public static float CoverageFactor(CampaignState s) => (float)Math.Max(0.1, 1.0 + SumReached(s, RewardSignal));

        private static double SumReached(CampaignState s, string reward)
        {
            PurificationState d = s?.Weather?.Purification;
            if (d?.Reached == null || d.Reached.Length == 0)
            {
                return 0.0;
            }
            double sum = 0;
            foreach (ReturnMilestoneDef m in AllMilestones)
            {
                if (m.Reward == reward && Array.IndexOf(d.Reached, m.Id) >= 0)
                {
                    sum += m.Value;
                }
            }
            return sum;
        }

        /// <summary>FG13-END-06 结局接口：归还 100,000 格后结局里多的那一句（没达成 = null）。</summary>
        public static string EndingLineKey(CampaignState s) => AchievementReached(s) ? "purify.ending_line" : null;

        /// <summary>FG15-SYS-04 成就接口：“归还十万格”是否已达成（成就系统落地时据此解锁，旧档同样补发）。</summary>
        public static bool AchievementReached(CampaignState s)
        {
            foreach (ReturnMilestoneDef m in AllMilestones)
            {
                if (m.Reward == RewardAchievement && IsReached(s, m.Id))
                {
                    return true;
                }
            }
            return false;
        }

        // ── 稀土富矿（归还 20,000 格）─────────────────────────────────────────────

        /// <summary>
        /// 按生成规则找一处稀土矿脉格（B25：不写坐标）：先沿世界规划层的稀土矿带（按到核心的距离、编号排序）找，没有规划 / 找不到再从核心向外按步长粗采样；
        /// 优先还没探索的格，都探索过了也接受。地形用生成器的纯函数采样（不生成区块）。找到 → 记下富矿、揭示（探索）这一圈；返回通知正文。
        /// </summary>
        private static string RevealRichDeposit(CampaignState state, ReturnMilestoneDef m)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int samples = 0;
            bool found = TryFindRichCell(state, out GridCell cell, ref samples);
            LastRichSearchSamples = samples;
            LastRichSearchMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (!found)
            {
                return GameText.Format("purify.notify.rich_ore_none", SearchRadius);
            }
            PurificationState d = StateOf(state);
            int r = RichRadius;
            var rec = new RichDepositRecord { X = cell.X, Y = cell.Y, Radius = r, Source = m.Id };
            var next = new RichDepositRecord[(d.RichDeposits?.Length ?? 0) + 1];
            if (d.RichDeposits != null)
            {
                Array.Copy(d.RichDeposits, next, d.RichDeposits.Length);
            }
            next[next.Length - 1] = rec;
            d.RichDeposits = next;
            HomeGridService.RevealArea(state, new Vector2(cell.X, cell.Y), r);
            return GameText.Format("purify.notify.rich_ore", cell.X, cell.Y, r, RichYieldMult);
        }

        /// <summary>找富矿落点（自检直接调，断言在种子测试集上都找得到、落在稀土矿脉上）。</summary>
        public static bool TryFindRichCell(CampaignState state, out GridCell cell, ref int samples)
        {
            cell = default;
            HomeGridMap map = HomeGridService.MapFor(state);
            byte rare = GridContent.TryTerrainCode("ore_rare", out byte code) ? code : (byte)255;
            if (rare == 255)
            {
                return false;
            }
            GridCell core = HomeGridService.CorePivot(state);
            int maxR = SearchRadius;
            int limit = GridContent.TuningInt("world.coord_limit");
            ExploredAreaRecord[] explored = state.Grid?.Explored ?? Array.Empty<ExploredAreaRecord>();
            RichDepositRecord[] deposits = StateOf(state).RichDeposits ?? Array.Empty<RichDepositRecord>();
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantUnexplored = pass == 0;
                WorldPlan plan = WorldGenService.PlanFor(state);
                if (plan != null)
                {
                    var belts = new List<PlannedBelt>();
                    foreach (PlannedBelt b in plan.Belts)
                    {
                        if (b != null && b.Terrain == "ore_rare" && SegmentDistance(core, b) <= maxR)
                        {
                            belts.Add(b);
                        }
                    }
                    belts.Sort((a, b) =>
                    {
                        int c = SegmentDistance(core, a).CompareTo(SegmentDistance(core, b));
                        return c != 0 ? c : a.Index.CompareTo(b.Index);
                    });
                    foreach (PlannedBelt b in belts)
                    {
                        long lx = b.X1 - b.X0, ly = b.Y1 - b.Y0;
                        int len = (int)Math.Sqrt(lx * lx + ly * ly);
                        int rad = Math.Max(1, b.Radius);
                        for (int t = 0; t <= len; t += 2)
                        {
                            int px = b.X0 + (int)(len == 0 ? 0 : lx * t / len);
                            int py = b.Y0 + (int)(len == 0 ? 0 : ly * t / len);
                            for (int oy = -rad; oy <= rad; oy += 2)
                            {
                                for (int ox = -rad; ox <= rad; ox += 2)
                                {
                                    var cnd = new GridCell(px + ox, py + oy);
                                    if (Candidate(map, cnd, rare, limit, core, maxR, explored, deposits, wantUnexplored, ref samples))
                                    {
                                        cell = cnd;
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
                int step = SearchStep;
                for (int r = Math.Max(step, 16); r <= maxR; r += step)
                {
                    int n = Math.Max(8, (int)(2 * Math.PI * r / step));
                    for (int i = 0; i < n; i++)
                    {
                        double a = i * 2 * Math.PI / n;
                        var cnd = new GridCell(core.X + (int)Math.Round(r * Math.Cos(a)), core.Y + (int)Math.Round(r * Math.Sin(a)));
                        if (Candidate(map, cnd, rare, limit, core, maxR, explored, deposits, wantUnexplored, ref samples))
                        {
                            cell = cnd;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static bool Candidate(HomeGridMap map, GridCell c, byte rare, int limit, GridCell core, int maxR, ExploredAreaRecord[] explored,
            RichDepositRecord[] deposits, bool wantUnexplored, ref int samples)
        {
            long dx = c.X - core.X, dy = c.Y - core.Y;
            if (dx * dx + dy * dy > (long)maxR * maxR || (limit > 0 && !WorldCoord.WithinLimit(c, limit)))
            {
                return false;
            }
            foreach (RichDepositRecord d in deposits)
            {
                if (d != null && Dist2(c, d.X, d.Y) <= (long)d.Radius * d.Radius)
                {
                    return false;
                }
            }
            if (wantUnexplored && IsExplored(explored, c))
            {
                return false;
            }
            samples++;
            map.TerrainSource.Sample(c.X, c.Y, out byte terrain, out _);
            return terrain == rare;
        }

        private static bool IsExplored(ExploredAreaRecord[] explored, GridCell c)
        {
            foreach (ExploredAreaRecord a in explored)
            {
                if (a != null && Dist2(c, a.CenterX, a.CenterY) <= (long)a.Radius * a.Radius)
                {
                    return true;
                }
            }
            return false;
        }

        private static long Dist2(GridCell c, int x, int y)
        {
            long dx = c.X - x, dy = c.Y - y;
            return dx * dx + dy * dy;
        }

        private static int SegmentDistance(GridCell p, PlannedBelt b)
        {
            double ax = b.X0, ay = b.Y0, bx = b.X1, by = b.Y1;
            double vx = bx - ax, vy = by - ay;
            double l2 = vx * vx + vy * vy;
            double t = l2 <= 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - ax) * vx + (p.Y - ay) * vy) / l2));
            double qx = ax + t * vx - p.X, qy = ay + t * vy - p.Y;
            return (int)Math.Sqrt(qx * qx + qy * qy);
        }

        /// <summary>提取钻的产出倍数：脚下是稀土矿、中心落在某处稀土富矿范围里 = purify.rich_ore.yield_mult，否则 1。</summary>
        public static int DrillYieldMult(CampaignState s, ProductionService.Producer p)
        {
            if (p?.VeinOre == null || p.VeinOre.Id != "rare_earth_ore")
            {
                return 1;
            }
            RichDepositRecord[] deps = s?.Weather?.Purification?.RichDeposits;
            if (deps == null || deps.Length == 0)
            {
                return 1;
            }
            Vector2 pos = p.Building.Position;
            foreach (RichDepositRecord d in deps)
            {
                if (d == null)
                {
                    continue;
                }
                float dx = pos.x - d.X, dy = pos.y - d.Y;
                if (dx * dx + dy * dy <= (float)d.Radius * d.Radius)
                {
                    return RichYieldMult;
                }
            }
            return 1;
        }

        /// <summary>这些格子里有没有落在某处稀土富矿范围里的（放置预览用）。</summary>
        public static RichDepositRecord DepositAt(CampaignState s, List<GridCell> cells)
        {
            RichDepositRecord[] deps = s?.Weather?.Purification?.RichDeposits;
            if (deps == null || deps.Length == 0 || cells == null)
            {
                return null;
            }
            foreach (RichDepositRecord d in deps)
            {
                foreach (GridCell c in cells)
                {
                    if (d != null && Dist2(c, d.X, d.Y) <= (long)d.Radius * d.Radius)
                    {
                        return d;
                    }
                }
            }
            return null;
        }

        // ── 净化区（酸雨豁免）与塔的列表 ────────────────────────────────────────────

        /// <summary>家园里全部净化塔（生产建筑索引里 purifier 模式的那些；索引变了才重建）。</summary>
        public static IReadOnlyList<ProductionService.Producer> PurifiersOf(CampaignState state)
        {
            int n = ProductionService.ProducerCount; // 确保索引按当前战役建好
            if (!ReferenceEquals(state, _purifierState) || ProductionService.Revision != _purifierRevision)
            {
                _purifierState = state;
                _purifierRevision = ProductionService.Revision;
                Purifiers.Clear();
                IReadOnlyList<ProductionService.Producer> all = ProductionService.All;
                for (int i = 0; i < all.Count && i < n; i++)
                {
                    if (all[i]?.Def != null && all[i].Def.Mode == ProducerMode.Purifier)
                    {
                        Purifiers.Add(all[i]);
                    }
                }
            }
            return Purifiers;
        }

        /// <summary>
        /// 这一格在净化区里（酸雨不扣耐久，FG07 第 3.3 节“不在净化区里的建筑……”）：落在某座<b>正在净化</b>（有电、有冷却液，存档里的 PurifySupplied）的塔的半径之内。
        /// O(净化塔数)；只在酸雨这一分钟真有整点伤害的目标上调用。
        /// </summary>
        public static bool InPurifiedZone(CampaignState state, GridCell cell)
        {
            if (state == null)
            {
                return false;
            }
            EnsureZones(state);
            for (int i = 0; i < _zoneCount; i++)
            {
                long dx = cell.X - ZoneX[i], dy = cell.Y - ZoneY[i];
                if (dx * dx + dy * dy <= ZoneR2[i])
                {
                    return true;
                }
            }
            return false;
        }

        // 净化区缓存：同一次生产步、同一份生产索引、同一批建筑等级内只算一次（酸雨一分钟的批量结算会对上万格传送带逐格查询；
        // PurifySupplied 只在生产步里变，半径只随等级变）。查询本身是纯整数循环、不分配。
        private static int[] ZoneX = new int[8];
        private static int[] ZoneY = new int[8];
        private static long[] ZoneR2 = new long[8];
        private static int _zoneCount;
        private static int _zoneStep = int.MinValue;
        private static int _zoneRevision = -1;
        private static int _zoneOpsRevision = -1;
        private static CampaignState _zoneState;

        private static void EnsureZones(CampaignState state)
        {
            int step = ProductionService.StepCount;
            if (ReferenceEquals(state, _zoneState) && step == _zoneStep && _zoneRevision == ProductionService.Revision && _zoneOpsRevision == BuildingOps.Revision)
            {
                return;
            }
            _zoneState = state;
            _zoneStep = step;
            _zoneCount = 0;
            if (!ReferenceEquals(ProductionService.IndexedState, state) && ReferenceEquals(state, CampaignSession.Current))
            {
                _ = ProductionService.ProducerCount; // 当前战役的索引还没建（刚读档）：建一次
            }
            _zoneRevision = ProductionService.Revision;
            _zoneOpsRevision = BuildingOps.Revision;
            if (!ReferenceEquals(ProductionService.IndexedState, state))
            {
                return; // 不是当前生产索引的战役（自检里的旁路状态）：没有净化区
            }
            IReadOnlyList<ProductionService.Producer> all = ProductionService.All;
            for (int i = 0; i < all.Count; i++)
            {
                ProductionService.Producer p = all[i];
                if (p?.Def == null || p.Def.Mode != ProducerMode.Purifier || p.Rec == null || !p.Rec.PurifySupplied || p.Building == null)
                {
                    continue;
                }
                if (_zoneCount == ZoneX.Length)
                {
                    Array.Resize(ref ZoneX, _zoneCount * 2);
                    Array.Resize(ref ZoneY, _zoneCount * 2);
                    Array.Resize(ref ZoneR2, _zoneCount * 2);
                }
                int r = RadiusOf(p.Building);
                ZoneX[_zoneCount] = p.Building.GridX;
                ZoneY[_zoneCount] = p.Building.GridY;
                ZoneR2[_zoneCount] = (long)r * r;
                _zoneCount++;
            }
        }

        // ── 塔的进度信息（面板 / 悬停 / 叠加层；格网没改过就用缓存）─────────────────────

        private sealed class TowerInfo
        {
            public int Edit = int.MinValue;
            public HomeGridMap Map;
            /// <summary>扫描时范围所在的区块与它们的内容版本（≤ 9 个）。格网别处有改动（另一座塔降级、拆完废墟）时只比这几个区块，
            /// 没变就沿用缓存——不让任意一格改动把所有塔都重扫一遍（修复轮 1，P2）。</summary>
            public HomeGridMap.Chunk[] Chunks = Array.Empty<HomeGridMap.Chunk>();
            public int[] ChunkRevs = Array.Empty<int>();
            public int Radius;
            public GridCell Center;
            /// <summary>最里面还有污染的那一环（全干净 = 环数）。</summary>
            public int Front;
            public int[] RingMax = Array.Empty<int>();
            /// <summary>从现在起全部净化完还要几次降级（每环要它最高等级那么多次）。</summary>
            public int StepsLeft;
            public int Polluted;
        }

        private static TowerInfo InfoOf(CampaignState state, BuildingRecord b)
        {
            HomeGridMap map = HomeGridService.MapFor(state);
            int radius = RadiusOf(b);
            GridCell center = CenterOf(b);
            if (!Infos.TryGetValue(b.BuildingId, out TowerInfo info))
            {
                Infos[b.BuildingId] = info = new TowerInfo();
            }
            if (ReferenceEquals(info.Map, map) && info.Radius == radius && info.Center == center)
            {
                if (info.Edit == map.TerrainEditRevision)
                {
                    return info;
                }
                if (ChunksUnchanged(map, info))
                {
                    info.Edit = map.TerrainEditRevision; // 改动不在这座塔的范围区块里：缓存仍然准确
                    return info;
                }
            }
            InfoScans++;
            int[][] rings = Rings(radius);
            info.Map = map;
            info.Edit = map.TerrainEditRevision;
            info.Radius = radius;
            info.Center = center;
            if (info.RingMax.Length != rings.Length)
            {
                info.RingMax = new int[rings.Length];
            }
            info.Front = rings.Length;
            info.StepsLeft = 0;
            info.Polluted = 0;
            int limit = GridContent.TuningInt("world.coord_limit");
            for (int k = 0; k < rings.Length; k++)
            {
                int max = 0;
                foreach (int off in rings[k])
                {
                    var cell = new GridCell(center.X + Dx(off), center.Y + Dy(off));
                    if (limit > 0 && !WorldCoord.WithinLimit(cell, limit))
                    {
                        continue;
                    }
                    int lv = map.GetPollution(cell);
                    if (lv > 0)
                    {
                        info.Polluted++;
                        max = Math.Max(max, lv);
                    }
                }
                info.RingMax[k] = max;
                info.StepsLeft += max;
                if (max > 0 && info.Front == rings.Length)
                {
                    info.Front = k;
                }
            }
            RecordChunks(map, info, center, radius);
            return info;
        }

        /// <summary>自检读：塔的进度缓存重扫了几次（格网别处的改动不应让范围外的塔重扫）。</summary>
        public static int InfoScans { get; private set; }

        private static readonly List<HomeGridMap.Chunk> ChunkScratch = new List<HomeGridMap.Chunk>(9);

        /// <summary>记下范围包围盒里已加载的区块与内容版本（刚扫完整个圆，圆碰到的区块都已加载；没加载的区块不含圆里的格子）。O(区块数)。</summary>
        private static void RecordChunks(HomeGridMap map, TowerInfo info, GridCell center, int radius)
        {
            int cs = Math.Max(1, map.ChunkSize);
            int cx0 = GridMath.FloorDiv(center.X - radius, cs), cx1 = GridMath.FloorDiv(center.X + radius, cs);
            int cy0 = GridMath.FloorDiv(center.Y - radius, cs), cy1 = GridMath.FloorDiv(center.Y + radius, cs);
            ChunkScratch.Clear();
            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    HomeGridMap.Chunk c = map.TryGetLoaded(cx, cy);
                    if (c != null)
                    {
                        ChunkScratch.Add(c);
                    }
                }
            }
            if (info.Chunks.Length != ChunkScratch.Count)
            {
                info.Chunks = new HomeGridMap.Chunk[ChunkScratch.Count];
                info.ChunkRevs = new int[ChunkScratch.Count];
            }
            for (int i = 0; i < ChunkScratch.Count; i++)
            {
                info.Chunks[i] = ChunkScratch[i];
                info.ChunkRevs[i] = ChunkScratch[i].ContentRevision;
            }
        }

        /// <summary>范围区块都还是同一个已加载区块、内容版本没变（被回收后重新生成的区块是新对象，也算变了）。O(区块数) 次字典查找。</summary>
        private static bool ChunksUnchanged(HomeGridMap map, TowerInfo info)
        {
            if (info.Chunks.Length == 0)
            {
                return false;
            }
            for (int i = 0; i < info.Chunks.Length; i++)
            {
                HomeGridMap.Chunk c = info.Chunks[i];
                if (c == null || c.ContentRevision != info.ChunkRevs[i] || !ReferenceEquals(map.TryGetLoaded(c.ChunkX, c.ChunkY), c))
                {
                    return false;
                }
            }
            return true;
        }

        public static bool IsDone(CampaignState state, ProductionService.Producer p) => p?.Building != null && InfoOf(state, p.Building).StepsLeft == 0;

        /// <summary>这座塔的净化前沿（第几环，0 起）与总环数。</summary>
        public static void FrontOf(CampaignState state, BuildingRecord b, out int front, out int rings, out int stepsLeft)
        {
            TowerInfo info = InfoOf(state, b);
            front = info.Front;
            rings = info.RingMax.Length;
            stepsLeft = info.StepsLeft;
        }

        /// <summary>离下一次降级还要几个游戏秒（只算有电有冷却液的时间）。</summary>
        public static double SecondsToNextStep(ProducerRecord rec)
        {
            long left = Math.Max(0L, StepTicks - (rec?.PurifyTicks ?? 0));
            return left / (double)Math.Max(1, GameClock.StepHz);
        }

        /// <summary>全部净化完还要几个游戏秒（按现在的速度；只算有电有冷却液的时间）。</summary>
        public static double SecondsToFinish(CampaignState state, BuildingRecord b, ProducerRecord rec)
        {
            TowerInfo info = InfoOf(state, b);
            if (info.StepsLeft <= 0)
            {
                return 0;
            }
            return Math.Max(0.0, info.StepsLeft * HoursPerLevel * GameClock.HourSeconds - (rec?.PurifyTicks ?? 0) / (double)Math.Max(1, GameClock.StepHz));
        }

        /// <summary>生产状态原因（工作中 / 全部净化完）。</summary>
        public static string WorkingText(CampaignState state, ProductionService.Producer p)
        {
            if (p?.Building == null)
            {
                return string.Empty;
            }
            TowerInfo info = InfoOf(state, p.Building);
            if (info.StepsLeft == 0)
            {
                return GameText.Format("prod.reason.purify_done", info.Radius);
            }
            return GameText.Format("prod.reason.purify_working", info.Front + 1, info.RingMax.Length, info.Radius, CalendarService.ShortDuration(SecondsToNextStep(p.Rec)));
        }

        /// <summary>放置预览：这一格放 T1 净化塔，半径内多少格要净化、多久净化完（只算有电有冷却液的时间）。</summary>
        public static void AddPlacementNotes(CampaignState state, string typeId, List<GridCell> cells, List<string> notes)
        {
            if (state == null || cells == null || cells.Count == 0 || notes == null)
            {
                return;
            }
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (GridCell c in cells)
            {
                minX = Math.Min(minX, c.X);
                minY = Math.Min(minY, c.Y);
                maxX = Math.Max(maxX, c.X);
                maxY = Math.Max(maxY, c.Y);
            }
            var center = new GridCell((minX + maxX) / 2, (minY + maxY) / 2);
            int radius = RadiusOfTier(typeId, 1);
            int[][] rings = Rings(radius);
            HomeGridMap map = HomeGridService.MapFor(state);
            int polluted = 0, steps = 0;
            foreach (int[] ring in rings)
            {
                int max = 0;
                foreach (int off in ring)
                {
                    int lv = map.GetPollution(new GridCell(center.X + Dx(off), center.Y + Dy(off)));
                    if (lv > 0)
                    {
                        polluted++;
                        max = Math.Max(max, lv);
                    }
                }
                steps += max;
            }
            notes.Add(polluted == 0
                ? GameText.Format("purify.place.note_clean", radius)
                : GameText.Format("purify.place.note", radius, polluted, CalendarService.ShortDuration(steps * HoursPerLevel * GameClock.HourSeconds)));
        }

        // ── 冷却液接管格（修复轮 1：塔可以压在重污染上，但管线不能——FGR-LOG-012 只豁免净化塔和传送带；FG00 B06 / B11 不让玩家照提示走进死局）──

        /// <summary>净化塔放在（枢轴, 朝向）时冷却液口外那一格（管线要铺到这里，与 ProductionService 算 PipeCell 同一公式）。没有输入口返回 false。O(端口数)。</summary>
        public static bool TryIntakeCell(string typeId, GridCell pivot, int rotation, out GridCell cell)
        {
            cell = default;
            if (!ProducerCatalog.TryGet(typeId, out ProducerDef def) || def.Mode != ProducerMode.Purifier)
            {
                return false;
            }
            int rot = GridMath.NormalizeRotation(rotation);
            foreach (FluidPortDef f in def.FluidPorts)
            {
                if (f == null || f.IsOutput)
                {
                    continue;
                }
                GridCell port = GridMath.PortCell(pivot, f.LocalX, f.LocalY, rot);
                Vector2Int v = GridMath.DirVector(GridMath.RotateDir(f.Dir, rot));
                cell = new GridCell(port.X + v.x, port.Y + v.y);
                return true;
            }
            return false;
        }

        /// <summary>这一格污染达到不可建等级、上面也没有早先铺好的管线：管线铺不过去。O(1)。</summary>
        private static bool IntakeBlockedAt(CampaignState state, GridCell cell, out int level)
        {
            level = 0;
            int limit = GridContent.TuningInt("world.coord_limit");
            if (state == null || limit > 0 && !WorldCoord.WithinLimit(cell, limit))
            {
                return false;
            }
            HomeGridMap.Chunk chunk = HomeGridService.MapFor(state).ChunkAt(cell, out int idx);
            level = chunk.Pollution[idx];
            int block = GridContent.TuningInt("grid.pollution_block_level");
            return block > 0 && level >= block && chunk.Pipe[idx] == 0;
        }

        /// <summary>放置预览：冷却液口外那一格是 2 级以上（管线铺不过去，塔会一直缺冷却液）→ 警告，写明旋转 / 放到边缘。只警告不阻止（还可以先放、之后净化周边）。</summary>
        public static void AddIntakeWarning(CampaignState state, string typeId, GridCell pivot, int rotation, List<string> warnings)
        {
            if (warnings != null && TryIntakeCell(typeId, pivot, rotation, out GridCell cell) && IntakeBlockedAt(state, cell, out int level))
            {
                warnings.Add(GameText.Format("purify.warn.intake_polluted", cell.X, cell.Y, level));
            }
        }

        /// <summary>建成的净化塔缺冷却液时的原因：冷却液口外那一格被重污染挡住（管线铺不过去）→ 写明挪到边缘；否则返回 null（照常写接口在哪 / 网络没供给）。</summary>
        public static string IntakeHint(CampaignState state, ProductionService.Producer p)
        {
            if (state == null || p?.Def == null || p.Def.Mode != ProducerMode.Purifier || p.Fluids.Length == 0 || p.Fluids[0] == null)
            {
                return null;
            }
            GridCell c = p.Fluids[0].PipeCell;
            return IntakeBlockedAt(state, c, out int level) ? GameText.Format("purify.reason.intake_polluted", c.X, c.Y, level) : null;
        }

        /// <summary>同上，给“净化暂停”通知用：被重污染挡住时返回带位置的专门通知正文，否则 null。</summary>
        public static string StalledNotifyText(CampaignState state, ProductionService.Producer p, string label)
        {
            if (state == null || p?.Def == null || p.Def.Mode != ProducerMode.Purifier || p.Fluids.Length == 0 || p.Fluids[0] == null)
            {
                return null;
            }
            GridCell c = p.Fluids[0].PipeCell;
            return IntakeBlockedAt(state, c, out int level) ? GameText.Format("purify.notify.stalled_intake", label, c.X, c.Y, level) : null;
        }

        // ── 悬停格子（FGR-ENV-040“悬停格子显示等级和影响”）─────────────────────────

        /// <summary>
        /// 一格的污染读数：标题“格子（x, y）· 污染 N 级”，正文写影响（能不能建、天气加倍）、是不是净化回来的、哪座塔在净化 / 多久降到 0 级 / 暂停原因、
        /// 上面的建筑是否受污染。O(净化塔数 + 一座塔的进度缓存)。建造模式状态行与污染叠加层下的世界悬停共用。
        /// </summary>
        public static bool TryDescribeCell(CampaignState state, GridCell cell, out string title, out string body)
        {
            title = body = null;
            if (state == null)
            {
                return false;
            }
            int limit = GridContent.TuningInt("world.coord_limit");
            if (limit > 0 && !WorldCoord.WithinLimit(cell, limit))
            {
                return false;
            }
            HomeGridMap map = HomeGridService.MapFor(state);
            // 修复轮 1（P2，FGR-ENV-050）：迷雾里的格子不读污染（只写“未探索”），也不为悬停去同步生成没加载的区块。
            if (!map.IsExploredNoLoad(cell))
            {
                title = GameText.Format("purify.hover.fog_title", cell.X, cell.Y);
                body = GameText.Get("purify.hover.fog");
                return true;
            }
            if (!TryLoadedCell(map, cell, out HomeGridMap.Chunk chunk, out int idx))
            {
                return false;
            }
            int level = chunk.Pollution[idx];
            int baseLevel = chunk.Modified && chunk.BasePollution != null ? chunk.BasePollution[idx] : level;
            int block = Math.Max(1, GridContent.TuningInt("grid.pollution_block_level"));
            int warn = Math.Max(1, GridContent.TuningInt("grid.pollution_warn_level"));
            title = GameText.Format("purify.hover.title", cell.X, cell.Y, level);
            var sb = new StringBuilder(128);
            sb.Append(level >= block ? GameText.Format("purify.hover.level_block", level) : level >= warn ? GameText.Get("purify.hover.level1") : GameText.Get("purify.hover.level0"));
            if (level == 0 && baseLevel > 0)
            {
                sb.Append('\n').Append(GameText.Format("purify.hover.returned", baseLevel));
            }
            // 覆盖这一格的净化塔：正在净化的优先（写多久降到 0 级 / 在净化区里），否则写暂停原因。
            ProductionService.Producer best = null;
            ProductionService.Producer paused = null;
            foreach (ProductionService.Producer p in PurifiersOf(state))
            {
                int r = RadiusOf(p.Building);
                if (Dist2(cell, p.Building.GridX, p.Building.GridY) > (long)r * r)
                {
                    continue;
                }
                if (p.Rec != null && p.Rec.PurifySupplied)
                {
                    best ??= p;
                }
                else
                {
                    paused ??= p;
                }
            }
            if (best != null)
            {
                string name = BuildingOps.NameOf(best.Building);
                if (level == 0)
                {
                    sb.Append('\n').Append(GameText.Format("purify.hover.zone", name));
                }
                else
                {
                    TowerInfo info = InfoOf(state, best.Building);
                    int ring = RingOf((int)Dist2(cell, best.Building.GridX, best.Building.GridY));
                    int steps = level;
                    for (int k = info.Front; k < ring && k < info.RingMax.Length; k++)
                    {
                        steps += info.RingMax[k];
                    }
                    double secs = Math.Max(0.0, steps * HoursPerLevel * GameClock.HourSeconds - best.Rec.PurifyTicks / (double)Math.Max(1, GameClock.StepHz));
                    sb.Append('\n').Append(GameText.Format("purify.hover.eta", name, CalendarService.ShortDuration(secs), ring + 1, info.Front + 1));
                }
            }
            else if (paused != null)
            {
                string why = paused.Building.ConstructionState == BuildingConstructionState.Operational
                    ? ProductionService.ReasonText(state, paused).Replace("\n", " · ")
                    : GameText.Get(BuildingOps.UiStatusNameKey(BuildingStatusService.Evaluate(state, paused.Building).Kind));
                sb.Append('\n').Append(GameText.Format("purify.hover.paused", BuildingOps.NameOf(paused.Building), why));
            }
            else if (level > 0)
            {
                sb.Append('\n').Append(GameText.Get("purify.hover.uncovered"));
            }
            BuildingRecord on = HomeGridService.BuildingAt(state, cell);
            if (on != null && IsContaminated(state, on.BuildingId))
            {
                sb.Append('\n').Append(GameText.Format("purify.hover.contaminated", BuildingOps.NameOf(on)));
            }
            body = sb.ToString();
            if (level > 0)
            {
                GuidanceHooks.Raise(GuidanceHooks.EnvFirstPollutionSeen);
            }
            return true;
        }

        /// <summary>建造模式状态行的一段：污染叠加层开着，或这一格有污染 / 是净化回来的 / 有塔覆盖时才写（干净的空地不打扰）。</summary>
        public static string StatusLine(CampaignState state, GridCell cell, bool overlayOn)
        {
            if (state == null)
            {
                return null;
            }
            if (!overlayOn)
            {
                int limit = GridContent.TuningInt("world.coord_limit");
                if (limit > 0 && !WorldCoord.WithinLimit(cell, limit))
                {
                    return null;
                }
                HomeGridMap map = HomeGridService.MapFor(state);
                if (!map.IsExploredNoLoad(cell) || !TryLoadedCell(map, cell, out HomeGridMap.Chunk chunk, out int idx))
                {
                    return null; // 迷雾 / 没加载：叠加层没开时不打扰（建造预览自己会写“迷雾”）
                }
                bool returned = chunk.Modified && chunk.BasePollution != null && chunk.BasePollution[idx] > 0 && chunk.Pollution[idx] == 0;
                if (chunk.Pollution[idx] == 0 && !returned)
                {
                    return null;
                }
            }
            return TryDescribeCell(state, cell, out string title, out string body) ? title + "\n" + body : null;
        }

        /// <summary>已加载区块里的这一格（不触发生成）；没加载返回 false。</summary>
        private static bool TryLoadedCell(HomeGridMap map, GridCell cell, out HomeGridMap.Chunk chunk, out int idx)
        {
            ChunkAddress a = GridMath.Address(cell, map.ChunkSize);
            idx = a.LocalY * map.ChunkSize + a.LocalX;
            chunk = map.TryGetLoaded(a.ChunkX, a.ChunkY);
            return chunk != null;
        }

        // ── 再污染与受污染（FGR-ENV-043）────────────────────────────────────────────

        private static void EnsureContaminatedIndex(CampaignState s)
        {
            string[] arr = s?.Weather?.Purification?.Contaminated;
            if (ReferenceEquals(arr, _contaminatedRef))
            {
                return;
            }
            _contaminatedRef = arr;
            ContaminatedSet.Clear();
            if (arr != null)
            {
                foreach (string id in arr)
                {
                    if (id != null)
                    {
                        ContaminatedSet.Add(id);
                    }
                }
            }
        }

        /// <summary>这座建筑处于“受污染”状态（天气损伤加倍）。O(1)。</summary>
        public static bool IsContaminated(CampaignState s, string buildingId)
        {
            if (s == null || buildingId == null)
            {
                return false;
            }
            EnsureContaminatedIndex(s);
            return ContaminatedSet.Count > 0 && ContaminatedSet.Contains(buildingId);
        }

        public static int ContaminatedCount(CampaignState s)
        {
            EnsureContaminatedIndex(s);
            return ContaminatedSet.Count;
        }

        /// <summary>建筑悬停的一行：受污染时写明（所在格子最高几级、怎么恢复）；不是 = null。</summary>
        public static string BuildingHoverLine(CampaignState state, BuildingRecord b)
        {
            if (b == null || !IsContaminated(state, b.BuildingId))
            {
                return null;
            }
            return GameText.Format("purify.building.contaminated", MaxFootprintPollution(state, b));
        }

        private static int MaxFootprintPollution(CampaignState state, BuildingRecord b)
        {
            if (!GridContent.TryGetBuilding(b.BuildingTypeId, out BuildingGrid g))
            {
                return HomeGridService.MapFor(state).GetPollution(new GridCell(b.GridX, b.GridY));
            }
            GridMath.FootprintCells(new GridCell(b.GridX, b.GridY), g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(b.Rotation), Footprint);
            HomeGridMap map = HomeGridService.MapFor(state);
            int max = 0;
            foreach (GridCell c in Footprint)
            {
                max = Math.Max(max, map.GetPollution(c));
            }
            return max;
        }

        /// <summary>
        /// 化工突袭的再污染接口（FG10-RAID-01 调用；FG09“化工阵营的突袭会让一片区域的污染升级”）：<paramref name="center"/> 半径 <paramref name="radius"/> 格内每格污染 +<paramref name="levels"/>（最高 3 级）。
        /// 被升级的格子上的建筑不摧毁，进入“受污染”状态（天气损伤加倍）；格子上的传送带同样按格子的污染等级受天气加倍损伤。发“再污染”通知（可定位）与第一次的引导钩子。
        /// 只改格网污染层与本服务的状态；O(半径²)。
        /// </summary>
        public static RepolluteResult Repollute(CampaignState state, GridCell center, int radius, int levels, string sourceKey = "purify.source.chemical_raid")
        {
            var res = new RepolluteResult();
            if (state == null || radius < 0 || levels <= 0)
            {
                return res;
            }
            HomeGridMap map = HomeGridService.MapFor(state);
            int limit = GridContent.TuningInt("world.coord_limit");
            RaisedBuildings.Clear();
            foreach (int[] ring in Rings(radius))
            {
                foreach (int off in ring)
                {
                    var cell = new GridCell(center.X + Dx(off), center.Y + Dy(off));
                    if (limit > 0 && !WorldCoord.WithinLimit(cell, limit))
                    {
                        continue;
                    }
                    byte lv = map.GetPollution(cell);
                    byte nv = (byte)Math.Min(3, lv + levels);
                    if (nv == lv)
                    {
                        continue;
                    }
                    map.SetPollution(cell, nv);
                    res.CellsRaised++;
                    res.LevelsRaised += nv - lv;
                    BuildingRecord b = HomeGridService.BuildingAt(state, cell);
                    if (b != null && !HomeValleyController.IsPlannedGhost(b) && b.ConstructionState != BuildingConstructionState.Destroyed)
                    {
                        RaisedBuildings.Add(b.BuildingId);
                    }
                }
            }
            PurificationState d = StateOf(state);
            d.LevelsRaised += res.LevelsRaised;
            d.Repollutions++;
            if (RaisedBuildings.Count > 0)
            {
                var ids = new List<string>(RaisedBuildings);
                ids.Sort(StringComparer.Ordinal); // 确定性：同一次再污染按 ID 排序追加
                var list = new List<string>(d.Contaminated ?? Array.Empty<string>());
                foreach (string id in ids)
                {
                    if (!list.Contains(id))
                    {
                        list.Add(id);
                        res.BuildingsContaminated++;
                    }
                }
                d.Contaminated = list.ToArray();
            }
            Revision++;
            if (res.CellsRaised > 0)
            {
                NotificationCenter.Post("pollution_raised", GameText.Format("purify.notify.repolluted", GameText.Get(sourceKey), res.CellsRaised, res.BuildingsContaminated),
                    new Vector3(center.X, 0f, center.Y));
                GuidanceHooks.Raise(GuidanceHooks.EnvFirstRepollution);
                CheckMilestones(state); // 归还面积可能减少（已达成的里程碑不收回）
                GameEvent.Send(ChangedEvent, ReturnedArea(state));
            }
            return res;
        }

        /// <summary>
        /// 受污染的建筑：所在格子全部 0 级 → 解除并通知；建筑记录已不在（拆除完工）或变成了规划虚影 → 静默移除。
        /// 被摧毁的建筑（本工程里是“受损、耐久 0、留在原地可修”）仍在受污染的格子上，保留受污染：修好后照样天气加倍，直到净化。O(受污染建筑 × 占地)。
        /// </summary>
        public static void CheckRecovered(CampaignState state)
        {
            PurificationState d = state?.Weather?.Purification;
            if (d?.Contaminated == null || d.Contaminated.Length == 0)
            {
                return;
            }
            List<string> keep = null;
            for (int i = 0; i < d.Contaminated.Length; i++)
            {
                string id = d.Contaminated[i];
                BuildingRecord b = HomeGridService.FindBuilding(state, id);
                bool gone = b == null || b.ConstructionState == BuildingConstructionState.Destroyed || HomeValleyController.IsPlannedGhost(b);
                bool clean = !gone && MaxFootprintPollution(state, b) == 0;
                if (gone || clean)
                {
                    keep ??= new List<string>(d.Contaminated.Take(i));
                    if (clean)
                    {
                        RecoveredCount++;
                        NotificationCenter.Post("contamination_cleared", GameText.Format("purify.notify.recovered", BuildingOps.NameOf(b)),
                            new Vector3(b.Position.x, 0f, b.Position.y));
                    }
                    continue;
                }
                keep?.Add(id);
            }
            if (keep != null)
            {
                d.Contaminated = keep.ToArray();
                Revision++;
            }
        }

        // ── 界面文字（顶栏、日历）─────────────────────────────────────────────────

        /// <summary>“下一个里程碑：20,000 格 —— 揭示新的资源点（还差 N 格）”/“全部归还里程碑已达成”。</summary>
        public static string NextMilestoneText(CampaignState s)
        {
            ReturnMilestoneDef next = NextMilestone(s);
            if (next == null)
            {
                return GameText.Get("purify.cal.all_done");
            }
            long left = Math.Max(0L, next.Cells - ReturnedArea(s));
            return GameText.Format("purify.cal.next", next.Cells.ToString("N0", CultureInfo.InvariantCulture), next.Desc, left.ToString("N0", CultureInfo.InvariantCulture));
        }

        /// <summary>日历面板页脚的归还面积段：已归还 N 格 · 下一个里程碑；已达成的里程碑与奖励；稀土富矿位置。</summary>
        public static string CalendarText(CampaignState s)
        {
            if (s == null)
            {
                return string.Empty;
            }
            var sb = new StringBuilder(160);
            sb.Append(GameText.Format("purify.cal.line", ReturnedArea(s).ToString("N0", CultureInfo.InvariantCulture), NextMilestoneText(s)));
            PurificationState d = s.Weather?.Purification;
            if (d?.Reached != null && d.Reached.Length > 0)
            {
                var parts = new List<string>(d.Reached.Length);
                foreach (ReturnMilestoneDef m in AllMilestones)
                {
                    if (Array.IndexOf(d.Reached, m.Id) >= 0)
                    {
                        parts.Add(GameText.Format("purify.cal.reached_item", m.Name, m.Desc));
                    }
                }
                sb.Append('\n').Append(GameText.Format("purify.cal.reached", string.Join(GameText.Get("purify.cal.join"), parts)));
            }
            if (d?.RichDeposits != null)
            {
                foreach (RichDepositRecord r in d.RichDeposits)
                {
                    if (r != null)
                    {
                        sb.Append('\n').Append(GameText.Format("purify.cal.deposit", r.X, r.Y, r.Radius, RichYieldMult));
                    }
                }
            }
            return sb.ToString();
        }

        private static Vector3 CoreAt(CampaignState s)
        {
            GridCell c = HomeGridService.CorePivot(s);
            return new Vector3(c.X, 0f, c.Y);
        }

        private static T[] Append<T>(T[] old, T item)
        {
            old ??= Array.Empty<T>();
            var next = new T[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = item;
            return next;
        }
    }
}
