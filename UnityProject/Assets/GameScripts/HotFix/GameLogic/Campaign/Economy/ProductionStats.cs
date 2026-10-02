using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-08（FG04 FGR-ECO-050 生产统计；第 6 节“统计（按时间窗口聚合保存）”；第 7 节“统计按时间窗口聚合，不能每帧遍历所有建筑”；FG13 FGU-13）。
    /// - <b>记账</b>：产出 / 消耗发生的那一刻（生产建筑完成一份、开工扣料、流体泵抽出、燃油发电机烧油、废液池销毁、经济账本收支、装配站取料）调用
    ///   <see cref="RecordUnits"/> / <see cref="RecordFluidMl"/>，把数量加进四个时间窗口（1 分钟 / 10 分钟 / 1 小时 / 10 小时）各自的“当前桶”——
    ///   每次 O(窗口数 × 该桶的物品种类)，与建筑数无关；没有任何“每帧遍历建筑 / 物品”的步骤。桶按世界步序号划分（与镜头、帧率、倍速、是否观察家园无关；暂停时世界不走步）。
    /// - <b>存档</b>：桶本身就是存档（<see cref="ProductionStatsState"/>），读档后接着同一桶累计；调参改了桶长 / 桶数时该窗口整体重建。
    /// - <b>查询</b>：窗口合计 = 窗口内各桶相加，O(桶数 × 物品种类)，只在面板 / 顶栏刷新时调用（顶栏按 eco.topbar.refresh_seconds 节流）。
    /// - <b>持续赤字</b>：每 eco.stats.deficit_check_seconds 游戏秒检查一次最近 eco.stats.deficit_probe_seconds 的收支：消耗大于产出就计时，
    ///   持续超过 eco.stats.deficit_minutes 发一次警告（同一段赤字只发一次），收支恢复就清掉。
    /// - <b>瓶颈 / 建筑效率</b>：读每座生产建筑自己的 10 分钟桶（<see cref="ProducerStatBucket"/>，缺料步数随生产步累加），只在面板打开时按节流查询，O(生产建筑 × 桶数)。
    /// </summary>
    public static class ProductionStats
    {
        public const int TierCount = 4;

        // ── 调参 ─────────────────────────────────────────────────────────────

        public static int BucketCount => Math.Max(2, GridContent.TuningInt("eco.stats.buckets"));

        public static int TierBucketSeconds(int tier) =>
            Math.Max(1, GridContent.TuningInt("eco.stats.tier" + Math.Max(0, Math.Min(TierCount - 1, tier)).ToString(CultureInfo.InvariantCulture) + ".bucket_seconds"));

        /// <summary>窗口长度（游戏秒）= 桶长 × 桶数：初值 60 / 600 / 3600 / 36000。</summary>
        public static int WindowSeconds(int tier) => TierBucketSeconds(tier) * BucketCount;

        public static string WindowName(int tier) => GameText.Get("stats.window." + Math.Max(0, Math.Min(TierCount - 1, tier)).ToString(CultureInfo.InvariantCulture));

        public static float DeficitMinutes => Math.Max(0.1f, GridContent.Tuning("eco.stats.deficit_minutes"));
        public static int DeficitProbeSeconds => Math.Max(1, GridContent.TuningInt("eco.stats.deficit_probe_seconds"));
        public static int DeficitCheckSeconds => Math.Max(1, GridContent.TuningInt("eco.stats.deficit_check_seconds"));

        // ── 自检读点 ─────────────────────────────────────────────────────────

        /// <summary>记账次数（每次产出 / 消耗事件 +1）。</summary>
        public static long RecordCount { get; private set; }
        /// <summary>查询时逐建筑扫描的累计座数（瓶颈 / 建筑效率；自检据此证明“不打开面板就不扫建筑”）。</summary>
        public static long BuildingScans { get; private set; }
        /// <summary>赤字检查次数 / 发出的赤字警告次数。</summary>
        public static int DeficitChecks { get; private set; }
        public static int DeficitWarnings { get; private set; }
        /// <summary>记账累计耗时（毫秒；性能自检读）。</summary>
        public static double RecordMs { get; private set; }
        /// <summary>任何统计数据变了 +1（面板据此判断是否重建；不在记账热路径里自增，按桶滚动 / 赤字变化 / 固定变化）。</summary>
        public static int Revision { get; private set; } = 1;

        public static void Touch() => Revision++;

        public static void ResetSessionState()
        {
            RecordCount = 0;
            BuildingScans = 0;
            DeficitChecks = 0;
            DeficitWarnings = 0;
            RecordMs = 0;
            Revision++;
        }

        // ── 记账 ─────────────────────────────────────────────────────────────

        /// <summary>固体按件、流体按升（配方行的单位）。</summary>
        public static void RecordUnits(CampaignState state, ItemDef item, long amount, bool produced)
        {
            if (item == null || amount <= 0)
            {
                return;
            }
            Record(state, item.Id, item.Form == ItemForm.Fluid ? amount * 1000L : amount, produced);
        }

        /// <summary>流体按毫升（流体泵、燃油发电机、废液池按毫升精确累计）。</summary>
        public static void RecordFluidMl(CampaignState state, ItemDef fluid, long ml, bool produced)
        {
            if (fluid == null || ml <= 0)
            {
                return;
            }
            Record(state, fluid.Id, ml, produced);
        }

        /// <summary>燃油发电机燃油耗尽一次（存档计数）。</summary>
        public static void NoteFuelOut(CampaignState state)
        {
            if (state != null)
            {
                Ensure(state).FuelOuts++;
                Revision++;
            }
        }

        /// <summary>经济账本 / 家园资源类型（"Scrap" / "TechData" / 物品 ID）的收支：按件记。</summary>
        public static void RecordResource(CampaignState state, string resourceType, long amount, bool produced)
        {
            if (amount > 0 && ItemCatalog.TryGetByResource(resourceType, out ItemDef def))
            {
                RecordUnits(state, def, amount, produced);
            }
        }

        private static void Record(CampaignState state, string itemId, long amount, bool produced)
        {
            if (state == null || string.IsNullOrEmpty(itemId))
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ProductionStatsState st = Ensure(state);
            long now = GameClock.Ticks;
            int hz = Math.Max(1, GameClock.StepHz);
            RefreshTuningCache();
            int n = _cachedBuckets;
            for (int tier = 0; tier < TierCount; tier++)
            {
                ProdStatBucketRecord b = CurrentBucket(st, tier, now, hz, n, _cachedTierSeconds[tier]);
                if (produced)
                {
                    Add(ref b.Produced, itemId, amount);
                }
                else
                {
                    Add(ref b.Consumed, itemId, amount);
                }
            }
            RecordCount++;
            RecordMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            // FG4-ECO-09：远征在外时同一笔也记进离家报告（同一记账口径，FGT-ECO-007 报告产量 = 统计窗口的产量）。
            AwayReportService.OnRecord(state, itemId, amount, produced);
        }

        /// <summary>统计域（旧存档补空、开始时刻定为现在）。</summary>
        public static ProductionStatsState Ensure(CampaignState state)
        {
            if (state.Stats?.Production == null || state.Stats.Production.Tiers == null)
            {
                CampaignFgStateDomains.EnsureAll(state);
            }
            ProductionStatsState st = state.Stats.Production;
            if (st.StartTick < 0)
            {
                st.StartTick = GameClock.Ticks;
            }
            if (!st.PinsInitialized)
            {
                // 资源顶栏默认固定废料（最基础的建造资源）；玩家全部取消后保持为空，不再补回。
                st.PinsInitialized = true;
                if (st.Pins.Length == 0 && ItemCatalog.TryGet(ItemCatalog.ScrapId, out _))
                {
                    st.Pins = new[] { new PinnedItemRecord { ItemId = ItemCatalog.ScrapId } };
                }
            }
            if (st.Tiers.Length != TierCount)
            {
                var next = new ProdStatTierRecord[TierCount];
                for (int i = 0; i < TierCount; i++)
                {
                    next[i] = i < st.Tiers.Length && st.Tiers[i] != null ? st.Tiers[i] : new ProdStatTierRecord();
                }
                st.Tiers = next;
            }
            return st;
        }

        // 记账热路径不查调参表（每次查表要拼键字符串）：按调参版本缓存桶长与桶数。
        private static int _cachedTuningRevision = -1;
        private static int _cachedBuckets = 30;
        private static readonly int[] _cachedTierSeconds = new int[TierCount];

        private static void RefreshTuningCache()
        {
            if (_cachedTuningRevision == GridContent.Revision)
            {
                return;
            }
            _cachedBuckets = BucketCount;
            for (int i = 0; i < TierCount; i++)
            {
                _cachedTierSeconds[i] = TierBucketSeconds(i);
            }
            _cachedTuningRevision = GridContent.Revision;
        }

        private static ProdStatBucketRecord CurrentBucket(ProductionStatsState st, int tier, long now, int hz, int n, int sec)
        {
            ProdStatTierRecord t = st.Tiers[tier] ?? (st.Tiers[tier] = new ProdStatTierRecord());
            if (t.BucketSeconds != sec || t.Buckets == null || t.Buckets.Length != n)
            {
                // 调参改了桶长 / 桶数：该窗口整体重建（旧桶的时间划分对不上）。
                t.BucketSeconds = sec;
                t.Buckets = new ProdStatBucketRecord[n];
            }
            long idx = now / Math.Max(1L, (long)sec * hz);
            int slot = (int)(idx % n);
            ProdStatBucketRecord b = t.Buckets[slot] ?? (t.Buckets[slot] = new ProdStatBucketRecord());
            if (b.Index != idx)
            {
                b.Index = idx;
                b.Produced = Array.Empty<ItemAmountRecord>();
                b.Consumed = Array.Empty<ItemAmountRecord>();
                Revision++;
            }
            return b;
        }

        internal static void Add(ref ItemAmountRecord[] arr, string itemId, long amount)
        {
            arr ??= Array.Empty<ItemAmountRecord>();
            for (int i = 0; i < arr.Length; i++)
            {
                ItemAmountRecord r = arr[i];
                if (r != null && r.ItemId == itemId)
                {
                    r.Amount += amount;
                    return;
                }
            }
            var next = new ItemAmountRecord[arr.Length + 1];
            Array.Copy(arr, next, arr.Length);
            next[arr.Length] = new ItemAmountRecord { ItemId = itemId, Amount = amount };
            arr = next;
        }

        // ── 查询 ─────────────────────────────────────────────────────────────

        /// <summary>一种物品在一个窗口里的收支。数量为原始单位（固体件、流体毫升）；速率为显示单位（件 / 升）每游戏分钟。</summary>
        public struct ItemRate
        {
            public ItemDef Item;
            public long Produced;
            public long Consumed;
            public float ProducedPerMinute;
            public float ConsumedPerMinute;
            public float NetPerMinute => ProducedPerMinute - ConsumedPerMinute;
        }

        /// <summary>
        /// 窗口实际覆盖的游戏秒（速率的分母）：查询取的是 (当前桶 - 桶数, 当前桶] 这些桶——前面 桶数-1 个是走完的满桶，最后一个是还没走完的当前桶，
        /// 所以覆盖时长 = (桶数 - 1) × 桶长 + 当前桶已走过的秒数（审查修复：原先按完整的 桶长 × 桶数 算，速率系统性偏低 0～1/桶数）。
        /// 统计开始不到一个窗口时 = 已统计的时长（至少一个桶长），不把没统计的时间算成 0 产量。
        /// </summary>
        public static float EffectiveSeconds(CampaignState state, int tier)
        {
            int sec = TierBucketSeconds(tier);
            int n = BucketCount;
            int hz = Math.Max(1, GameClock.StepHz);
            long bucketTicks = Math.Max(1L, (long)sec * hz);
            double inCurrent = (GameClock.Ticks % bucketTicks) / (double)hz;
            double covered = (n - 1) * (double)sec + inCurrent;
            long start = state?.Stats?.Production?.StartTick ?? -1;
            if (start >= 0)
            {
                double elapsed = (GameClock.Ticks - start) / (double)hz;
                covered = Math.Min(covered, elapsed);
            }
            return (float)Math.Max(sec, covered);
        }

        /// <summary>窗口里有收支的全部物品（按物品表顺序）。O(桶数 × 物品种类)。</summary>
        public static void CollectRates(CampaignState state, int tier, List<ItemRate> into)
        {
            into.Clear();
            ProdStatTierRecord t = TierOf(state, tier);
            if (t == null)
            {
                return;
            }
            long nowIdx = GameClock.Ticks / Math.Max(1L, (long)t.BucketSeconds * Math.Max(1, GameClock.StepHz));
            int n = t.Buckets.Length;
            var raw = new Dictionary<string, (long P, long C)>(StringComparer.Ordinal);
            foreach (ProdStatBucketRecord b in t.Buckets)
            {
                if (b == null || b.Index < 0 || b.Index > nowIdx || b.Index <= nowIdx - n)
                {
                    continue;
                }
                foreach (ItemAmountRecord r in b.Produced ?? Array.Empty<ItemAmountRecord>())
                {
                    if (r != null && r.Amount > 0)
                    {
                        raw.TryGetValue(r.ItemId, out (long P, long C) v);
                        raw[r.ItemId] = (v.P + r.Amount, v.C);
                    }
                }
                foreach (ItemAmountRecord r in b.Consumed ?? Array.Empty<ItemAmountRecord>())
                {
                    if (r != null && r.Amount > 0)
                    {
                        raw.TryGetValue(r.ItemId, out (long P, long C) v);
                        raw[r.ItemId] = (v.P, v.C + r.Amount);
                    }
                }
            }
            float minutes = EffectiveSeconds(state, tier) / 60f;
            foreach (ItemDef d in ItemCatalog.Items)
            {
                if (raw.TryGetValue(d.Id, out (long P, long C) v))
                {
                    into.Add(Make(d, v.P, v.C, minutes));
                }
            }
        }

        /// <summary>一种物品在一个窗口里的收支（没有记录 = 全 0）。</summary>
        public static ItemRate RateOf(CampaignState state, int tier, ItemDef item)
        {
            if (item == null)
            {
                return default;
            }
            ProdStatTierRecord t = TierOf(state, tier);
            long p = 0, c = 0;
            if (t != null)
            {
                long nowIdx = GameClock.Ticks / Math.Max(1L, (long)t.BucketSeconds * Math.Max(1, GameClock.StepHz));
                int n = t.Buckets.Length;
                foreach (ProdStatBucketRecord b in t.Buckets)
                {
                    if (b == null || b.Index < 0 || b.Index > nowIdx || b.Index <= nowIdx - n)
                    {
                        continue;
                    }
                    p += Sum(b.Produced, item.Id);
                    c += Sum(b.Consumed, item.Id);
                }
            }
            return Make(item, p, c, EffectiveSeconds(state, tier) / 60f);
        }

        private static ItemRate Make(ItemDef d, long p, long c, float minutes)
        {
            float div = d.Form == ItemForm.Fluid ? 1000f : 1f;
            return new ItemRate
            {
                Item = d,
                Produced = p,
                Consumed = c,
                ProducedPerMinute = p / div / Math.Max(0.001f, minutes),
                ConsumedPerMinute = c / div / Math.Max(0.001f, minutes),
            };
        }

        private static long Sum(ItemAmountRecord[] arr, string id)
        {
            if (arr == null)
            {
                return 0;
            }
            foreach (ItemAmountRecord r in arr)
            {
                if (r != null && r.ItemId == id)
                {
                    return r.Amount;
                }
            }
            return 0;
        }

        private static ProdStatTierRecord TierOf(CampaignState state, int tier)
        {
            ProductionStatsState st = state?.Stats?.Production;
            if (st?.Tiers == null || tier < 0 || tier >= st.Tiers.Length)
            {
                return null;
            }
            ProdStatTierRecord t = st.Tiers[tier];
            return t?.Buckets == null || t.Buckets.Length == 0 || t.BucketSeconds <= 0 ? null : t;
        }

        /// <summary>
        /// 速率曲线（FGU-13）：窗口里每桶的产量 / 消耗（显示单位：件或升，旧桶在前；没记录的桶 = 0）。返回桶数。
        /// </summary>
        public static int Series(CampaignState state, int tier, ItemDef item, float[] produced, float[] consumed)
        {
            int n = BucketCount;
            Array.Clear(produced, 0, Math.Min(n, produced.Length));
            Array.Clear(consumed, 0, Math.Min(n, consumed.Length));
            ProdStatTierRecord t = TierOf(state, tier);
            if (t == null || item == null)
            {
                return Math.Min(n, produced.Length);
            }
            float div = item.Form == ItemForm.Fluid ? 1000f : 1f;
            long nowIdx = GameClock.Ticks / Math.Max(1L, (long)t.BucketSeconds * Math.Max(1, GameClock.StepHz));
            int len = t.Buckets.Length;
            foreach (ProdStatBucketRecord b in t.Buckets)
            {
                if (b == null || b.Index < 0 || b.Index > nowIdx || b.Index <= nowIdx - len)
                {
                    continue;
                }
                int at = len - 1 - (int)(nowIdx - b.Index);
                if (at < 0 || at >= produced.Length || at >= consumed.Length)
                {
                    continue;
                }
                produced[at] = Sum(b.Produced, item.Id) / div;
                consumed[at] = Sum(b.Consumed, item.Id) / div;
            }
            return Math.Min(len, produced.Length);
        }

        /// <summary>家园有没有任何产线（任何一座生产建筑，或统计里已经有过收支）；都没有时面板显示空状态（卡片负向）。</summary>
        public static bool HasAnyLine(CampaignState state)
        {
            if (state == null)
            {
                return false;
            }
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.RegionId == Regions.HomeValleyLayout.RegionId && ProducerCatalog.IsProducer(b.BuildingTypeId) && !Grid.HomeGridService.IsRelocationGhost(b))
                {
                    return true;
                }
            }
            return false;
        }

        // ── 持续赤字 ─────────────────────────────────────────────────────────

        /// <summary>世界步末调用（家园载入时）：到了检查的步就检查一次。只看步序号，与观察无关。</summary>
        public static void WorldStep(CampaignState state, long ticks, int stepHz)
        {
            if (state == null)
            {
                return;
            }
            long interval = Math.Max(1L, (long)DeficitCheckSeconds * Math.Max(1, stepHz));
            if (ticks % interval != 0)
            {
                return;
            }
            CheckDeficits(state);
        }

        private static readonly Dictionary<string, (long P, long C)> Probe = new Dictionary<string, (long P, long C)>(StringComparer.Ordinal);
        private static readonly List<DeficitRecord> DeficitScratch = new List<DeficitRecord>(8);

        /// <summary>检查一次（自检也直接调用）。O(检测窗口桶数 × 物品种类 + 赤字条数)。</summary>
        public static void CheckDeficits(CampaignState state)
        {
            ProductionStatsState st = Ensure(state);
            DeficitChecks++;
            int hz = Math.Max(1, GameClock.StepHz);
            long now = GameClock.Ticks;
            // 用 10 分钟窗口（第 1 档）的桶：检测窗口 = 最近 deficit_probe_seconds 秒覆盖到的那些桶。
            const int probeTier = 1;
            ProdStatTierRecord t = TierOf(state, probeTier);
            Probe.Clear();
            if (t != null)
            {
                long bucketTicks = Math.Max(1L, (long)t.BucketSeconds * hz);
                long nowIdx = now / bucketTicks;
                int k = Math.Max(1, (int)Math.Ceiling(DeficitProbeSeconds / (double)t.BucketSeconds));
                foreach (ProdStatBucketRecord b in t.Buckets)
                {
                    if (b == null || b.Index < 0 || b.Index > nowIdx || b.Index <= nowIdx - k)
                    {
                        continue;
                    }
                    foreach (ItemAmountRecord r in b.Produced ?? Array.Empty<ItemAmountRecord>())
                    {
                        if (r != null && r.Amount > 0)
                        {
                            Probe.TryGetValue(r.ItemId, out (long P, long C) v);
                            Probe[r.ItemId] = (v.P + r.Amount, v.C);
                        }
                    }
                    foreach (ItemAmountRecord r in b.Consumed ?? Array.Empty<ItemAmountRecord>())
                    {
                        if (r != null && r.Amount > 0)
                        {
                            Probe.TryGetValue(r.ItemId, out (long P, long C) v);
                            Probe[r.ItemId] = (v.P, v.C + r.Amount);
                        }
                    }
                }
            }
            long probeTicks = (long)DeficitProbeSeconds * hz;
            long warnTicks = (long)Math.Round(DeficitMinutes * 60.0 * hz);
            bool changed = false;
            DeficitScratch.Clear();
            // 已在赤字的：恢复（消耗不大于产出 / 没有消耗）就删掉，否则继续计时、到点发警告。
            foreach (DeficitRecord d in st.Deficits)
            {
                if (d == null || string.IsNullOrEmpty(d.ItemId))
                {
                    changed = true;
                    continue;
                }
                if (!Probe.TryGetValue(d.ItemId, out (long P, long C) v) || v.C <= v.P)
                {
                    changed = true;
                    continue;
                }
                DeficitScratch.Add(d);
            }
            // 新出现的赤字：从检测窗口的起点开始计时（不早于统计开始）。
            foreach (KeyValuePair<string, (long P, long C)> kv in Probe)
            {
                if (kv.Value.C <= kv.Value.P || FindDeficit(DeficitScratch, kv.Key) != null)
                {
                    continue;
                }
                DeficitScratch.Add(new DeficitRecord { ItemId = kv.Key, SinceTick = Math.Max(Math.Max(0, st.StartTick), now - probeTicks) });
                changed = true;
            }
            foreach (DeficitRecord d in DeficitScratch)
            {
                if (!d.Warned && now - d.SinceTick >= warnTicks)
                {
                    d.Warned = true;
                    changed = true;
                    DeficitWarnings++;
                    Probe.TryGetValue(d.ItemId, out (long P, long C) v);
                    PostDeficit(state, d, v.P, v.C);
                }
            }
            if (changed || DeficitScratch.Count != st.Deficits.Length)
            {
                DeficitScratch.Sort((a, b) => a.SinceTick != b.SinceTick ? a.SinceTick.CompareTo(b.SinceTick) : string.CompareOrdinal(a.ItemId, b.ItemId));
                st.Deficits = DeficitScratch.ToArray();
                Revision++;
            }
        }

        private static DeficitRecord FindDeficit(List<DeficitRecord> list, string id)
        {
            foreach (DeficitRecord d in list)
            {
                if (d.ItemId == id)
                {
                    return d;
                }
            }
            return null;
        }

        private static void PostDeficit(CampaignState state, DeficitRecord d, long p, long c)
        {
            ItemCatalog.TryGet(d.ItemId, out ItemDef item);
            // FG4-ECO-09（DEBT-FG4ECO08-02）：定位到最近窗口里缺这种物品最久的建筑；没有建筑缺它（只是消耗大于产出）时如实不带位置。
            BuildingRecord at = MostStarvedFor(d.ItemId);
            NotificationCenter.Post("eco_deficit", DeficitText(state, d, item, p, c),
                at != null ? new UnityEngine.Vector3(at.Position.x, 0f, at.Position.y) : (UnityEngine.Vector3?)null);
            GuidanceHooks.Raise(GuidanceHooks.StatsFirstDeficit);
        }

        /// <summary>赤字警告的定位次数（每次警告扫一次生产建筑的缺料桶；自检读）。</summary>
        public static int DeficitLocateScans { get; private set; }

        /// <summary>最近 building.stats.window_minutes 里缺 <paramref name="itemId"/> 最久的生产建筑（没有 = null）。O(生产建筑 × 桶数)，只在发持续赤字警告时调用一次。</summary>
        public static BuildingRecord MostStarvedFor(string itemId)
        {
            DeficitLocateScans++;
            int n = ProductionService.StatBuckets;
            long now = GameClock.Ticks / ProductionService.StatBucketTicks(GameClock.StepHz);
            BuildingRecord best = null;
            long bestTicks = 0;
            foreach (ProductionService.Producer p in ProductionService.All)
            {
                long sum = 0;
                foreach (ProducerStatBucket b in p?.Rec?.Stats ?? Array.Empty<ProducerStatBucket>())
                {
                    if (b?.Starve == null || b.Index < 0 || b.Index <= now - n || b.Index > now)
                    {
                        continue;
                    }
                    foreach (ItemStackRecord s in b.Starve)
                    {
                        if (s != null && s.ItemId == itemId)
                        {
                            sum += s.Amount;
                        }
                    }
                }
                if (sum > bestTicks)
                {
                    bestTicks = sum;
                    best = p.Building;
                }
            }
            return bestTicks > 0 ? best : null;
        }

        /// <summary>“合金：消耗大于产出已 12 分钟（最近 2 分钟消耗 40 / 产出 20）；按当前速度库存约 8 分钟后耗尽”。</summary>
        public static string DeficitText(CampaignState state, DeficitRecord d, ItemDef item, long p, long c)
        {
            string name = item != null ? item.Name : ItemCatalog.NameOf(d.ItemId);
            float minutes = (GameClock.Ticks - d.SinceTick) / (float)Math.Max(1, GameClock.StepHz) / 60f;
            float probeMin = DeficitProbeSeconds / 60f;
            string probe = GameText.Format("stats.deficit.probe", UiNumber(probeMin), Amount(item, c), Amount(item, p));
            string eta = string.Empty;
            if (item != null && item.Form != ItemForm.Fluid)
            {
                int stock = HomeInventory.Stock(state, item);
                float netPerMin = (c - p) / Math.Max(0.01f, probeMin);
                eta = stock <= 0
                    ? GameText.Get("stats.deficit.empty")
                    : GameText.Format("stats.deficit.eta", UiNumber(stock / Math.Max(0.01f, netPerMin)));
            }
            return GameText.Format("stats.deficit.line", name, UiNumber((float)Math.Floor(minutes)), probe, eta);
        }

        /// <summary>当前持续赤字（检测窗口里消耗大于产出）的物品，按开始时间先后。</summary>
        public static IReadOnlyList<DeficitRecord> Deficits(CampaignState state) =>
            (IReadOnlyList<DeficitRecord>)state?.Stats?.Production?.Deficits ?? Array.Empty<DeficitRecord>();

        /// <summary>这种物品是否在持续赤字里（已发警告 = 超过 eco.stats.deficit_minutes）。</summary>
        public static bool IsDeficit(CampaignState state, string itemId, out bool warned)
        {
            warned = false;
            foreach (DeficitRecord d in Deficits(state))
            {
                if (d != null && d.ItemId == itemId)
                {
                    warned = d.Warned;
                    return true;
                }
            }
            return false;
        }

        // ── 瓶颈与建筑效率（只在面板打开时查询）─────────────────────────────────

        public sealed class StarvedBuilding
        {
            public BuildingRecord Building;
            public float Seconds;
        }

        public sealed class Bottleneck
        {
            public ItemDef Item;
            public float Seconds;
            public readonly List<StarvedBuilding> Buildings = new List<StarvedBuilding>(4);
        }

        /// <summary>
        /// 瓶颈查找（FGR-ECO-050）：最近 building.stats.window_minutes 里各生产建筑缺料 / 缺流体的时长，按物品合计（最常缺的在前），每种物品列出缺它的建筑（缺得最久的在前）。
        /// O(生产建筑 × 桶数 × 缺料种类)，只在面板刷新时调用。
        /// </summary>
        public static void CollectBottlenecks(CampaignState state, List<Bottleneck> into)
        {
            into.Clear();
            if (state == null)
            {
                return;
            }
            int n = ProductionService.StatBuckets;
            long now = GameClock.Ticks / ProductionService.StatBucketTicks(GameClock.StepHz);
            float hz = Math.Max(1, GameClock.StepHz);
            var byItem = new Dictionary<string, Bottleneck>(StringComparer.Ordinal);
            foreach (ProductionService.Producer p in ProductionService.All)
            {
                BuildingScans++;
                if (p?.Rec?.Stats == null || p.Building == null)
                {
                    continue;
                }
                Dictionary<string, long> mine = null;
                foreach (ProducerStatBucket b in p.Rec.Stats)
                {
                    if (b?.Starve == null || b.Index < 0 || b.Index > now || b.Index <= now - n)
                    {
                        continue;
                    }
                    foreach (ItemStackRecord s in b.Starve)
                    {
                        if (s == null || s.Amount <= 0)
                        {
                            continue;
                        }
                        mine ??= new Dictionary<string, long>(StringComparer.Ordinal);
                        mine.TryGetValue(s.ItemId, out long v);
                        mine[s.ItemId] = v + s.Amount;
                    }
                }
                if (mine == null)
                {
                    continue;
                }
                foreach (KeyValuePair<string, long> kv in mine)
                {
                    if (!byItem.TryGetValue(kv.Key, out Bottleneck bn))
                    {
                        ItemCatalog.TryGet(kv.Key, out ItemDef def);
                        bn = new Bottleneck { Item = def };
                        byItem[kv.Key] = bn;
                    }
                    float sec = kv.Value / hz;
                    bn.Seconds += sec;
                    bn.Buildings.Add(new StarvedBuilding { Building = p.Building, Seconds = sec });
                }
            }
            foreach (Bottleneck bn in byItem.Values)
            {
                if (bn.Item == null)
                {
                    continue;
                }
                bn.Buildings.Sort((a, b) => a.Seconds != b.Seconds ? b.Seconds.CompareTo(a.Seconds) : string.CompareOrdinal(a.Building.BuildingId, b.Building.BuildingId));
                into.Add(bn);
            }
            into.Sort((a, b) => a.Seconds != b.Seconds ? b.Seconds.CompareTo(a.Seconds) : string.CompareOrdinal(a.Item.Id, b.Item.Id));
        }

        public sealed class BuildingEfficiency
        {
            public BuildingRecord Building;
            public ProductionService.Producer Producer;
            /// <summary>效率 0～1（-1 = 不适用：连续工作的建筑或没有理论份数）。</summary>
            public float Efficiency;
            public int Done;
            public readonly List<ItemStackRecord> Outputs = new List<ItemStackRecord>(2);
        }

        /// <summary>按建筑查看效率（FGR-ECO-050）：全部生产建筑最近 10 分钟的效率与产出（效率低的在前，不适用的排最后）。O(生产建筑 × 桶数)，只在面板刷新时调用。</summary>
        public static void CollectEfficiencies(CampaignState state, List<BuildingEfficiency> into)
        {
            into.Clear();
            if (state == null)
            {
                return;
            }
            foreach (ProductionService.Producer p in ProductionService.All)
            {
                BuildingScans++;
                if (p?.Building == null || Grid.HomeGridService.IsRelocationGhost(p.Building))
                {
                    continue;
                }
                var e = new BuildingEfficiency { Building = p.Building, Producer = p, Efficiency = -1f };
                if (ProductionService.TryWindowStats(p, e.Outputs, out int done, out long theory))
                {
                    e.Done = done;
                    e.Efficiency = theory > 0 ? Math.Min(1f, done * 1000f / theory) : -1f;
                }
                into.Add(e);
            }
            into.Sort((a, b) =>
            {
                bool na = a.Efficiency < 0f, nb = b.Efficiency < 0f;
                if (na != nb)
                {
                    return na ? 1 : -1;
                }
                if (a.Efficiency != b.Efficiency)
                {
                    return a.Efficiency.CompareTo(b.Efficiency);
                }
                return string.CompareOrdinal(a.Building.BuildingId, b.Building.BuildingId);
            });
        }

        // ── 文本 ─────────────────────────────────────────────────────────────

        /// <summary>数量（原始单位：流体毫升 → “12 升”，固体件 → “12”）。</summary>
        public static string Amount(ItemDef item, long raw) =>
            item != null && item.Form == ItemForm.Fluid ? GameText.Format("stats.unit.liters", UiNumber(raw / 1000f)) : UiNumber(raw);

        /// <summary>速率（显示单位每分钟）。</summary>
        public static string Rate(ItemDef item, float perMinute) =>
            GameText.Format(item != null && item.Form == ItemForm.Fluid ? "stats.unit.lpm" : "stats.unit.pm", UiNumber(perMinute));

        public static string UiNumber(float v)
        {
            float a = Math.Abs(v);
            return a >= 100f || Math.Abs(v - Math.Round(v)) < 0.05f
                ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture)
                : v.ToString(a >= 10f ? "0.#" : "0.##", CultureInfo.InvariantCulture);
        }
    }
}
