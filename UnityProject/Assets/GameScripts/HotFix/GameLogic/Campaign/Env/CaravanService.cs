using System;
using System.Collections.Generic;
using System.Text;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>
    /// FG12-EVT-06 流浪商队（FG10 FGR-EVT-050 / 051，FGU-39，FGT-EVT-007；承接 DEBT-FG12EVT01-02 的商队部分）：事件导演的 caravan 处理器 + 交易。
    /// - 何时：导演按 fg.TbWorldEvent caravan 行安排（4～7 个游戏日、停留 durationSeconds = 1 个游戏日、homeOnly=1：突袭预警 / 出发时不来，已来的由导演以 left_raid 提前结束）；
    ///   “第一幕末起”= 第一幕节奏点 foundry_core（击败铸造主核心）已到或已进入第 2 幕，写在 <see cref="Handler.CanStart"/>。
    /// - Prepare：在归还核心周围 [stop_min, stop_max] 格的环里按（世界种子, 实例序号, 第几次）取一处可走地面作停靠点（B25）；出发点在同一方向再往外 entry_extra 格（迷雾外）。
    /// - 中立（FGR-EVT-050）：商队是纯表现的中立物件，不进战斗内核（没有内核实体、没有耐久）——炮塔、敌人、玩家的目标选择都选不到它，也就不会被攻击。
    /// - 商品（FGR-EVT-051）：到达（OnStart）时按（世界种子, 序号）从 fg.TbCaravanWare 抽 3～5 件（三类先各一件），价格（合金 / 精密零件 / 技术数据）与库存来自表；
    ///   每次来访刷新，离开（到时 / 突袭将至）时清空。稀有固件上架前要过 FG19 准入（FGR-EMG-014：编译后的端口声明里有它）；人类遗产每件每个世界只有一件；
    ///   已掌握的部件再买只加组件库存（FGR-EMG-193）。商品与剩余库存存在 <see cref="DirectorEventState"/>，随存档。
    /// 开销：每步 O(1)（导演负责计时）；抽商品 / 买入只在那一刻 O(表行数)。
    /// </summary>
    public static class CaravanService
    {
        public const string EventId = "caravan";
        public const string CatFirmware = "firmware";
        public const string CatHeritage = "heritage";
        public const string CatBlueprint = "blueprint";
        public const string NotifyType = "caravan";
        public const string AlloyId = "alloy";
        public const string PrecisionId = "precision_part";
        /// <summary>“第一幕末”：第一幕节奏表（fg.TbActPacing）里击败铸造主核心那一点。</summary>
        public const string Act1EndPace = "foundry_core";
        public const string BlockAct = "caravan.block.act";
        private const uint SaltPlace = 0x43415231u; // "CAR1"
        private const uint SaltWares = 0x43415232u;
        private static readonly string[] CategoryOrder = { CatFirmware, CatHeritage, CatBlueprint };

        public static int Revision { get; private set; } = 1;

        // ── 自检读数（会话内） ──
        public static int Arrivals { get; private set; }
        public static int Purchases { get; private set; }
        public static int Denied { get; private set; }
        public static int PlaceRejects { get; private set; }
        public static string LastMessage { get; private set; } = string.Empty;

        /// <summary>自检：把某些固件当作没通过准入（模拟 FGR-EMG-014 不准入的固件），null = 照编译结果。</summary>
        public static Func<string, bool> AdmissionOverrideForTests;

        public sealed class Handler : EventDirectorHandler
        {
            public override string EventId => CaravanService.EventId;

            public override bool CanStart(CampaignState s, EventDirectorDef def, out string reasonKey)
            {
                reasonKey = Act1Ended(s) ? null : BlockAct;
                return reasonKey == null;
            }

            public override bool Prepare(CampaignState s, EventDirectorInstanceRecord r) => CaravanService.Prepare(s, r);
            public override void OnStart(CampaignState s, EventDirectorInstanceRecord r) => CaravanService.OnStart(s, r);
            public override void OnEnd(CampaignState s, EventDirectorInstanceRecord r, string outcome) => CaravanService.OnEnd(s, r, outcome);
        }

        public static void ResetSessionState()
        {
            Arrivals = Purchases = Denied = PlaceRejects = 0;
            LastMessage = string.Empty;
            Revision++;
        }

        private static float T(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;
        public static float StopMin => Mathf.Max(1f, T("event.caravan.stop_min", 10f));
        public static float StopMax => Mathf.Max(StopMin + 1f, T("event.caravan.stop_max", 18f));
        public static float EntryExtra => Mathf.Max(1f, T("event.caravan.entry_extra", 40f));
        public static float ArriveSeconds => Mathf.Max(0f, T("event.caravan.arrive_seconds", 30f));
        public static float ClickRadius => Mathf.Max(0.5f, T("event.caravan.click_radius", 2.5f));
        public static int WaresMin => Mathf.Max(1, Mathf.RoundToInt(T("event.caravan.wares_min", 3f)));
        public static int WaresMax => Mathf.Max(WaresMin, Mathf.RoundToInt(T("event.caravan.wares_max", 5f)));

        public static bool Act1Ended(CampaignState s) => s?.Progress != null && (s.Progress.Act >= 2 || Act1PacingService.IsReached(s, Act1EndPace));

        // ── 表 ─────────────────────────────────────────────────────────────────

        private static List<CaravanWare> _rows;
        private static readonly Dictionary<string, CaravanWare> RowById = new Dictionary<string, CaravanWare>(StringComparer.Ordinal);

        public static void Reload()
        {
            _rows = null;
            RowById.Clear();
        }

        public static IReadOnlyList<CaravanWare> Rows
        {
            get
            {
                if (_rows != null)
                {
                    return _rows;
                }
                _rows = new List<CaravanWare>();
                RowById.Clear();
                try
                {
                    TbCaravanWare t = ConfigSystem.Instance?.Tables?.TbCaravanWare;
                    if (t != null)
                    {
                        _rows.AddRange(t.DataList);
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[CaravanService] 读取 fg.TbCaravanWare 失败：" + e.Message);
                }
                _rows.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
                foreach (CaravanWare w in _rows)
                {
                    RowById[w.Id] = w;
                }
                if (_rows.Count == 0)
                {
                    Log.Warning("[CaravanService] fg.TbCaravanWare 缺失：商队没有商品。");
                }
                return _rows;
            }
        }

        public static CaravanWare Row(string wareId)
        {
            _ = Rows;
            return wareId != null && RowById.TryGetValue(wareId, out CaravanWare w) ? w : null;
        }

        // ── 状态 ─────────────────────────────────────────────────────────────────

        /// <summary>停在家园的商队实例（导演的进行中事件；没有返回 null）。O(进行中事件数)。</summary>
        public static EventDirectorInstanceRecord Present(CampaignState s)
        {
            foreach (EventDirectorInstanceRecord r in EventDirectorService.Active(s))
            {
                if (r != null && r.EventId == EventId && r.State == EventDirectorService.StateActive)
                {
                    return r;
                }
            }
            return null;
        }

        public static IReadOnlyList<CaravanWareRecord> Wares(CampaignState s) =>
            Present(s) != null ? (IReadOnlyList<CaravanWareRecord>)(EventDirectorService.StateOf(s)?.CaravanWares ?? Array.Empty<CaravanWareRecord>()) : Array.Empty<CaravanWareRecord>();

        /// <summary>还要停留几秒（按世界步；暂停不走、倍速同比例）。</summary>
        public static double SecondsLeft(EventDirectorInstanceRecord r) =>
            r == null ? 0 : Math.Max(0, (r.EndTick - GameClock.Ticks) / (double)Math.Max(1, GameClock.StepHz));

        /// <summary>出发点（迷雾外）：从核心朝停靠点的方向再往外 entry_extra 格。</summary>
        public static Vector2 EntryPoint(CampaignState s, EventDirectorInstanceRecord r)
        {
            Vector2 core = SignalUplinkService.CorePosition(s);
            var stop = new Vector2(r.X, r.Y);
            Vector2 dir = stop - core;
            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector2.right;
            return stop + dir * EntryExtra;
        }

        /// <summary>主视图里商队此刻的位置：到达后 arrive_seconds 内从迷雾外走到停靠点（纯表现，只取决于步数）。</summary>
        public static Vector2 ViewPosition(CampaignState s, EventDirectorInstanceRecord r)
        {
            var stop = new Vector2(r.X, r.Y);
            long arrive = GameClock.TicksFor(ArriveSeconds);
            if (arrive <= 0)
            {
                return stop;
            }
            float k = Mathf.Clamp01((float)((GameClock.Ticks - r.StartTick) / (double)arrive));
            return Vector2.Lerp(EntryPoint(s, r), stop, k);
        }

        // ── 处理器 ─────────────────────────────────────────────────────────────

        /// <summary>按（世界种子, 序号, 第几次）在核心周围的环里取停靠点（可走的生成地面，且车身 ±2 格内没有玩家建筑——不画进建筑里、不与建筑点选重叠）；都不合格 = false（这次不来，导演重新抽间隔）。</summary>
        public static bool PickStop(CampaignState s, int serial, out Vector2 at)
        {
            Vector2 core = SignalUplinkService.CorePosition(s);
            uint seed = unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));
            int tries = Mathf.Clamp(Mathf.RoundToInt(T("event.caravan.place_attempts", 32f)), 1, 256);
            float r0 = StopMin, r1 = StopMax;
            for (int k = 0; k < tries; k++)
            {
                double a = WorldGenMath.Hash(seed, serial, 2 * k, SaltPlace) / 4294967296.0 * Math.PI * 2.0;
                double u = WorldGenMath.Hash(seed, serial, 2 * k + 1, SaltPlace) / 4294967296.0;
                double d = r0 + u * (r1 - r0);
                var p = new Vector2(core.x + (float)(Math.Cos(a) * d), core.y + (float)(Math.Sin(a) * d));
                if (WorldGenQuery.IsWalkableGenerated(s, Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y)) && !NearBuilding(s, p))
                {
                    at = p;
                    return true;
                }
            }
            at = default;
            return false;
        }

        /// <summary>停靠点 ±2 格（车身 1.8×4.2 米）内有建筑（含规划 / 施工中）。每次来访最多 place_attempts×25 次 O(1) 查格。</summary>
        public static bool NearBuilding(CampaignState s, Vector2 p)
        {
            GridCell c = GridCell.FromWorld(p);
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    if (HomeGridService.BuildingAt(s, new GridCell(c.X + dx, c.Y + dy)) != null)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool Prepare(CampaignState s, EventDirectorInstanceRecord r)
        {
            if (!PickStop(s, r.Serial, out Vector2 at))
            {
                PlaceRejects++;
                return false;
            }
            r.X = at.x;
            r.Y = at.y;
            r.RegionId = HomeValleyLayout.RegionId;
            return true;
        }

        private static void OnStart(CampaignState s, EventDirectorInstanceRecord r)
        {
            DirectorEventState d = EventDirectorService.StateOf(s);
            if (d == null)
            {
                return;
            }
            d.CaravanVisit = r.Serial;
            d.CaravanWares = RollWares(s, r.Serial);
            Arrivals++;
            Revision++;
            var names = new StringBuilder();
            foreach (CaravanWareRecord w in d.CaravanWares)
            {
                if (names.Length > 0)
                {
                    names.Append('、');
                }
                names.Append(WareName(Row(w.WareId)));
            }
            NotificationCenter.Post(NotifyType, GameText.Format("caravan.notify.arrive", d.CaravanWares.Length, names.ToString()), new Vector3(r.X, 0f, r.Y));
        }

        private static void OnEnd(CampaignState s, EventDirectorInstanceRecord r, string outcome)
        {
            DirectorEventState d = EventDirectorService.StateOf(s);
            if (d == null || d.CaravanVisit != r.Serial)
            {
                return;
            }
            d.CaravanVisit = 0;
            d.CaravanWares = Array.Empty<CaravanWareRecord>();
            Revision++;
            if (outcome == EventDirectorService.OutcomeLeftRaid)
            {
                NotificationCenter.Post(NotifyType, GameText.Get("caravan.notify.left_raid"), new Vector3(r.X, 0f, r.Y));
            }
        }

        // ── 商品 ─────────────────────────────────────────────────────────────

        /// <summary>这件商品现在能不能上架 / 买（与库存无关）：幕够了、内容存在、固件过准入、遗产还没得到、部件第一次买或能进组件库存。</summary>
        public static bool Available(CampaignState s, CaravanWare w)
        {
            if (w == null || s == null || Math.Max(1, s.Progress?.Act ?? 1) < w.ActMin)
            {
                return false;
            }
            switch (w.Category)
            {
                case CatFirmware:
                    return FirmwareKinds.IsFirmware(w.ContentId) && Admitted(w.ContentId);
                case CatHeritage:
                    return HeritageService.CanTrade(s, w.ContentId);
                case CatBlueprint:
                    return MechanicalContentFacade.TryGet(w.ContentId, out _)
                           && (!MechanicalContentUnlock.IsUnlocked(s, w.ContentId) || LootPickService.DuplicateStockItem(s, w.ContentId) != null);
                default:
                    return false;
            }
        }

        /// <summary>FG19 FGR-EMG-014：固件通过准入 = 编译后的端口声明里有它（不准入的整条切轨、不进编译结果）。</summary>
        public static bool Admitted(string firmwareId)
        {
            if (AdmissionOverrideForTests != null)
            {
                return AdmissionOverrideForTests(firmwareId);
            }
            return !string.IsNullOrEmpty(firmwareId) && EmgCatalog.Current.OwnerIndex.ContainsKey(firmwareId);
        }

        /// <summary>按（世界种子, 来访序号）抽 3～5 件：三类先各抽一件（该类有可上架的），再按权重从剩下的补满；同一件不重复。</summary>
        public static CaravanWareRecord[] RollWares(CampaignState s, int serial)
        {
            uint seed = unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));
            var pool = new List<CaravanWare>();
            foreach (CaravanWare w in Rows)
            {
                if (Available(s, w))
                {
                    pool.Add(w);
                }
            }
            int span = WaresMax - WaresMin + 1;
            int want = Math.Min(pool.Count, WaresMin + (int)(WorldGenMath.Hash(seed, serial, 0, SaltWares) % (uint)span));
            var picked = new List<CaravanWare>(want);
            int draw = 1;
            foreach (string cat in CategoryOrder)
            {
                if (picked.Count >= want)
                {
                    break;
                }
                CaravanWare p = PickWeighted(pool, cat, WorldGenMath.Hash(seed, serial, draw++, SaltWares));
                if (p != null)
                {
                    picked.Add(p);
                    pool.Remove(p);
                }
            }
            while (picked.Count < want && pool.Count > 0)
            {
                CaravanWare p = PickWeighted(pool, null, WorldGenMath.Hash(seed, serial, draw++, SaltWares));
                picked.Add(p);
                pool.Remove(p);
            }
            picked.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            var result = new CaravanWareRecord[picked.Count];
            for (int i = 0; i < picked.Count; i++)
            {
                result[i] = new CaravanWareRecord { WareId = picked[i].Id, Left = Math.Max(1, picked[i].Stock) };
            }
            return result;
        }

        private static CaravanWare PickWeighted(List<CaravanWare> pool, string category, uint hash)
        {
            int sum = 0;
            foreach (CaravanWare w in pool)
            {
                if (category == null || w.Category == category)
                {
                    sum += Math.Max(1, w.Weight);
                }
            }
            if (sum <= 0)
            {
                return null;
            }
            long roll = (long)(hash % (uint)sum);
            foreach (CaravanWare w in pool)
            {
                if (category != null && w.Category != category)
                {
                    continue;
                }
                roll -= Math.Max(1, w.Weight);
                if (roll < 0)
                {
                    return w;
                }
            }
            return null;
        }

        // ── 价格与说明 ─────────────────────────────────────────────────────────

        /// <summary>价格清单（合金 / 精密零件 / 技术数据，0 的不列）：与建造材料同一结构，缺多少用同一个说明（<see cref="BuildMaterials.DescribeShortfall"/>）。</summary>
        public static List<BuildMaterialNeed> Price(CaravanWare w)
        {
            var list = new List<BuildMaterialNeed>(3);
            if (w == null)
            {
                return list;
            }
            Add(list, AlloyId, w.Alloy);
            Add(list, PrecisionId, w.Precision);
            Add(list, ItemCatalog.TechDataId, w.Tech);
            return list;
        }

        private static void Add(List<BuildMaterialNeed> list, string itemId, int amount)
        {
            if (amount > 0 && ItemCatalog.TryGet(itemId, out ItemDef item))
            {
                list.Add(new BuildMaterialNeed(item, amount));
            }
        }

        public static string CategoryName(string cat) => GameText.Get("caravan.cat." + cat);

        public static string WareName(CaravanWare w)
        {
            if (w == null)
            {
                return string.Empty;
            }
            switch (w.Category)
            {
                case CatFirmware:
                    return FirmwareKinds.DisplayName(w.ContentId) ?? w.ContentId;
                case CatHeritage:
                    return HeritageService.Display(HeritageService.Find(w.ContentId));
                default:
                    return FeedbackCues.ContentName(w.ContentId);
            }
        }

        /// <summary>买到后会怎样（行里写明：解锁 / 再得一枚芯片 / 只加组件库存 / 遗产入库）。</summary>
        public static string EffectText(CampaignState s, CaravanWare w)
        {
            switch (w?.Category)
            {
                case CatFirmware:
                    return GameText.Get(FirmwareKinds.IsRaw(s, w.ContentId) ? "caravan.row.effect.fw_new" : "caravan.row.effect.fw_more");
                case CatHeritage:
                    return GameText.Get("caravan.row.effect.heritage");
                case CatBlueprint:
                    return GameText.Get(MechanicalContentUnlock.IsUnlocked(s, w.ContentId) ? "caravan.row.effect.bp_dup" : "caravan.row.effect.bp_new");
                default:
                    return string.Empty;
            }
        }

        /// <summary>一行：“【类别】名称：效果。价格：合金 ×30（仓库 12）… · 库存 2”。</summary>
        public static string RowText(CampaignState s, CaravanWareRecord rec)
        {
            CaravanWare w = Row(rec?.WareId);
            if (w == null)
            {
                return string.Empty;
            }
            return GameText.Format("caravan.row", CategoryName(w.Category), WareName(w), EffectText(s, w), BuildMaterials.DescribeList(s, Price(w)), rec.Left);
        }

        /// <summary>材料不够时写明差多少（“缺 合金 ×12：……”）；够了返回 null。</summary>
        public static string Shortfall(CampaignState s, CaravanWare w) => BuildMaterials.DescribeShortfall(s, Price(w));

        // ── 买入 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 买一件（交易面板的“买入”）。原子：商队在 → 这件还有库存且仍可买 → 材料都够 → 扣材料 → 交付 → 库存 -1。任一步不行就不改任何状态、返回 false，原因在 <paramref name="message"/>。
        /// </summary>
        public static bool Buy(CampaignState s, string wareId, out string message)
        {
            DirectorEventState d = EventDirectorService.StateOf(s);
            EventDirectorInstanceRecord inst = Present(s);
            CaravanWareRecord rec = null;
            if (d != null && inst != null)
            {
                foreach (CaravanWareRecord r in d.CaravanWares)
                {
                    if (r != null && r.WareId == wareId)
                    {
                        rec = r;
                        break;
                    }
                }
            }
            CaravanWare w = Row(wareId);
            if (inst == null || d == null)
            {
                return Deny(GameText.Get("caravan.buy.gone"), out message);
            }
            if (rec == null || w == null || rec.Left <= 0 || !Available(s, w))
            {
                return Deny(GameText.Get("caravan.buy.unavailable"), out message);
            }
            List<BuildMaterialNeed> price = Price(w);
            if (!BuildMaterials.AllInStock(s, price))
            {
                return Deny(GameText.Format("caravan.buy.short", BuildMaterials.DescribeShortfall(s, price)), out message);
            }
            foreach (BuildMaterialNeed n in price)
            {
                HomeInventory.TryRemove(s, n.Item, n.Amount);
                ProductionStats.RecordUnits(s, n.Item, n.Amount, produced: false); // FG4-ECO-08：扣料记进生产统计的消耗
                if (n.Item.Id == ItemCatalog.TechDataId)
                {
                    TechDataFlow.Spend(s, TechDataFlow.Other, n.Amount);
                }
            }
            string source = "caravan:" + inst.Serial.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + w.Id + ":" + rec.Left.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string effect = Deliver(s, w, source, new Vector2(inst.X, inst.Y));
            rec.Left--;
            d.CaravanBought++;
            Purchases++;
            Revision++;
            HomeInventory.Touch();
            message = GameText.Format("caravan.buy.ok", WareName(w), effect);
            LastMessage = message;
            FeedbackCues.Raise(FeedbackCueId.Pickup, message);
            return true;
        }

        private static bool Deny(string why, out string message)
        {
            message = why;
            LastMessage = why;
            Denied++;
            FeedbackCues.Raise(FeedbackCueId.Denied, why);
            return false;
        }

        /// <summary>交付（扣完材料之后）：固件 = 与分拣“已破解固件”同一路径；遗产 = 与带回入库同一效果；部件 = 第一次解锁蓝图、再买只加组件库存（仓库满拆成技术数据）。</summary>
        private static string Deliver(CampaignState s, CaravanWare w, string source, Vector2 at)
        {
            switch (w.Category)
            {
                case CatFirmware:
                {
                    string effect = EffectText(s, w);
                    HomeValleyAnalysis.CrackFirmwareContent(s, w.ContentId);
                    FieldLootService.DeliverEnemyItem(s, AnalysisCatalog.EncryptedFirmwareId, w.ContentId, EventId, at, source);
                    return effect;
                }
                case CatHeritage:
                    return HeritageService.ObtainFromTrade(s, w.ContentId) ?? string.Empty;
                default:
                    if (!MechanicalContentUnlock.IsUnlocked(s, w.ContentId))
                    {
                        s.UnlockedContentIds ??= Array.Empty<string>();
                        var ids = new List<string>(s.UnlockedContentIds) { w.ContentId };
                        s.UnlockedContentIds = ids.ToArray();
                        return GameText.Get("caravan.row.effect.bp_new");
                    }
                    LootPickService.DeliverDuplicatePart(s, LootPickService.DuplicateStockItem(s, w.ContentId), w.ContentId, 1, source);
                    return GameText.Get("caravan.row.effect.bp_dup");
            }
        }
    }
}
