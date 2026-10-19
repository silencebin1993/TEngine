using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG8-LOOT-02 三选一与保底（FG08 FGR-LOOT-020 三选一数据包、FGR-LOOT-021 候选池、FGR-LOOT-022 数据包不会丢；FG13 FGU-34；验收 FGT-EXP-006）。
    /// - 生成：野外精英被击毁（<see cref="FieldLootService.OnFieldKill"/> 分流过来）、事件完成（FG10 调 <see cref="GrantEventPacket"/>）、首领另加一个（首领 Story 调 <see cref="Grant"/>）
    ///   时生成一个待分拣数据包。<b>不在战斗中弹出</b>：只有顶栏计数“待分拣 ×N”与一条字幕。3 个候选在生成时就按（世界种子, 第几个数据包, 候选位）确定，
    ///   观察与否、倍速、存读档前后同一结果（FGR-BASE-021）。候选池 = 该阵营池 + 通用池；稀有度按 loot.pick.weight.*（60 / 30 / 9 / 1）抽；同一个数据包内不重复；
    ///   连续 loot.pick.pity（5）个数据包没有稀有及以上，下一个必有一个稀有位。
    /// - 携带：数据包记在一台机器上（击毁它的机器；炮塔 / 不明来源时交给 loot.pick.share_radius 内最近的己方机器；都没有就留在原地），不占货舱。
    ///   携带的机器阵亡：交给同一支远征队还活着的机器；没有可交的（全灭 / 不在远征队里）就留在阵亡处——和关键材料一样永不消失（D-13，<see cref="KeyItemRetention"/>），
    ///   己方机器走到 loot.pick.claim_radius 内自动捡起。
    /// - 分拣（<see cref="TryPick"/>）：携带机器在家园 / 带仓库的前哨站（离归还核心或运转中的仓库 loot.pick.depot_radius 内）时随时可以；在野外只在战略暂停中。
    ///   在家园 / 前哨站：选中的直接入库（加密固件进固件库成为未破解芯片，原型刻录数据入库即已破解，未解析模块 / 数据核心进仓库并登记解析身份，材料与组件进仓库）；
    ///   仓库放不下就拒绝并写明原因、数据包留着。在野外（只限在外的远征队成员）：装进携带机器（或同队空间够的机器）的货舱，回到家园时按“远征卸货”规则卸下（规则关着时在仓库旁手动“入库”）；
    ///   不在远征队里的携带者要先开回家园 / 仓库旁再分拣。全队放不下就拒绝，
    ///   提示先丢弃货舱里的东西或改选更小的候选——不会强行塞进去，也不会丢失。没选的两个按稀有度转为少量技术数据（经济账本生产事务，统计面板记“三选一没选的候选”）。
    /// 开销：生成 O(候选池行数)，只在击毁 / 事件那一刻；每 loot.pick.check_seconds 游戏秒对账一次（只在有数据包或货舱物品时做）O(数据包数 + 机器数)；
    /// 界面查询 O(数据包数)。不按帧遍历单位。
    /// </summary>
    public static class LootPickService
    {
        public const string SourceElite = "elite";
        public const string SourceEvent = "event";
        public const string SourceBoss = "boss";
        public const string NotifyType = "loot_packet";
        /// <summary>经济账本事务号前缀（没选的候选转成的技术数据）：lootpick:数据包ID。</summary>
        public const string TxPrefix = "lootpick:";
        /// <summary>入库 / 掉落时的回收实例 ID 前缀：pick:数据包ID。</summary>
        public const string HeldPrefix = "pick:";
        /// <summary>落在地上的原型刻录数据的身份来源标记（搬回时仍按已破解入库）。</summary>
        public const string OriginPrototype = "pick-proto";
        public const string OriginPick = "pick";
        public const string DestDepot = "depot";
        public const string DestCargo = "cargo";
        /// <summary>携带者不在远征队里、又不在家园 / 仓库旁：要先开回去再分拣（暂停也不行）。</summary>
        public const string ReasonGoHome = "loot.pick.reason.go_home";

        private const uint SaltTier = 0x50494B31u; // "PIK1"
        private const uint SaltRow = 0x50494B32u;
        private const uint SaltAmount = 0x50494B33u;
        private const uint SaltLore = 0x50494B34u;
        private const uint SaltPity = 0x50494B35u;

        // ── 自检 / 性能读点（本进程累计）──
        public static long GenEvents { get; private set; }
        public static double GenMaxMs { get; private set; }
        public static double GenTotalMs { get; private set; }
        public static long StepChecks { get; private set; }
        public static double StepMaxMs { get; private set; }
        /// <summary>任何数据包 / 货舱物品变了 +1（顶栏、面板据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;

        private static int _lastDepotReady = -1;
        private static long _lastGotCueTick = -1;
        private static readonly List<LootPickDef> PoolScratch = new List<LootPickDef>(96);
        private static readonly List<LootPickDef> TierScratch = new List<LootPickDef>(48);
        private static readonly HashSet<string> KeyScratch = new HashSet<string>(StringComparer.Ordinal);

        public static void ResetSessionState()
        {
            GenEvents = 0;
            GenMaxMs = 0;
            GenTotalMs = 0;
            StepChecks = 0;
            StepMaxMs = 0;
            _lastDepotReady = -1;
            _lastGotCueTick = -1;
            Revision++;
        }

        public static LootPacketState StateOf(CampaignState s)
        {
            LootPacketState st = FieldLootService.StateOf(s);
            if (st == null)
            {
                return null;
            }
            st.Packets ??= Array.Empty<LootPackageRecord>();
            st.Held ??= Array.Empty<LootHeldRecord>();
            st.Picks ??= Array.Empty<LootPickLogRecord>();
            st.SeenKeys ??= Array.Empty<string>();
            if (st.NextPacketSerial < 1)
            {
                st.NextPacketSerial = 1;
            }
            return st;
        }

        // ─────────────────────────────── 生成 ───────────────────────────────

        /// <summary>野外精英被击毁（家园战斗内核里不在攻城的敌方精英：领地精英残部、驻军 / 巡逻迎战、行进途中的拦截战）。</summary>
        public static LootPackageRecord OnEliteKilled(CampaignState s, CombatSite site, string faction, Vector2 at, int killerUnit)
        {
            if (s == null)
            {
                return null;
            }
            int holder = ResolveHolder(s, site, killerUnit, at);
            return Grant(s, SourceElite, faction, holder, HomeValleyLayout.RegionId, at);
        }

        /// <summary>FG10 事件完成时的三选一（FGR-LOOT-010“事件：三选一或固定”）：交给 <paramref name="holder"/>（0 = 交给 <paramref name="at"/> 附近最近的己方机器，没有就留在原地）。</summary>
        public static LootPackageRecord GrantEventPacket(CampaignState s, string faction, Vector2 at, int holder = 0)
        {
            if (s == null)
            {
                return null;
            }
            if (holder <= 0 || !Alive(holder))
            {
                holder = NearestMachine(HomeValleyLayout.RegionId, at, LootPickCatalog.ShareRadius, 0);
            }
            return Grant(s, SourceEvent, faction, holder, HomeValleyLayout.RegionId, at);
        }

        /// <summary>
        /// 生成一个数据包（三个候选此刻定下）。<paramref name="holder"/> = 携带的机器（0 = 留在 <paramref name="regionId"/> 的 <paramref name="at"/>）。
        /// 候选池空（坏表）时不生成并记 Error。返回数据包。
        /// </summary>
        public static LootPackageRecord Grant(CampaignState s, string source, string faction, int holder, string regionId, Vector2 at)
        {
            LootPacketState st = StateOf(s);
            if (st == null)
            {
                return null;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long serial = st.NextPacketSerial;
            bool pity = st.NoRareStreak >= LootPickCatalog.Pity;
            List<LootCandidateRecord> cands = RollCandidates(Seed(s), faction, serial, pity);
            if (cands.Count == 0)
            {
                Log.Error($"[LootPickService] 阵营 {faction} 的三选一候选池为空（fg.TbLootPick），没有生成数据包。");
                return null;
            }
            st.NextPacketSerial = serial + 1;
            bool rare = false;
            foreach (LootCandidateRecord c in cands)
            {
                rare |= LootPickCatalog.IsRareOrBetter(c.Tier);
            }
            st.NoRareStreak = rare ? 0 : st.NoRareStreak + 1;
            if (pity)
            {
                st.PityHits++;
            }
            var p = new LootPackageRecord
            {
                PacketId = "lp-" + serial.ToString(CultureInfo.InvariantCulture),
                Serial = serial,
                Source = source ?? SourceElite,
                Faction = faction ?? string.Empty,
                Tick = GameClock.Ticks,
                Holder = holder > 0 && Alive(holder) ? holder : 0,
                RegionId = string.IsNullOrEmpty(regionId) ? HomeValleyLayout.RegionId : regionId,
                X = at.x,
                Y = at.y,
                Pity = pity,
                Candidates = cands.ToArray(),
            };
            var list = new List<LootPackageRecord>(st.Packets.Length + 1);
            list.AddRange(st.Packets);
            list.Add(p);
            st.Packets = list.ToArray();
            st.PacketsMade++;
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.LootFirstPacket);
            // 不在战斗中弹出：只有顶栏计数与一条字幕（带位置）；同一步里击毁多个精英只给一条（不刷屏，计数照样涨）。
            if (_lastGotCueTick != GameClock.Ticks)
            {
                _lastGotCueTick = GameClock.Ticks;
                Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.Pickup, at,
                    GameText.Format("loot.pick.got", OriginText(p), st.Packets.Length));
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            GenEvents++;
            GenTotalMs += ms;
            GenMaxMs = Math.Max(GenMaxMs, ms);
            return p;
        }

        private static uint Seed(CampaignState s) => unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));

        /// <summary>
        /// 抽一个数据包的候选（确定）：每个候选位先按权重抽稀有度（保底包有一个候选位只在稀有 / 原型里抽），再在“该阵营池 + 通用池”里这一档还没用过的行中按权重抽；
        /// 这一档没有可用的行时依次往低一档、再往高一档找（保底位只往稀有及以上找）。同一个数据包内不重复（种类 + 物品 + 身份）。O(候选池行数 × 候选数)。
        /// </summary>
        public static List<LootCandidateRecord> RollCandidates(uint seed, string faction, long serial, bool pity)
        {
            var result = new List<LootCandidateRecord>(3);
            LootPickCatalog.PoolFor(faction, PoolScratch);
            if (PoolScratch.Count == 0)
            {
                return result;
            }
            int n = LootPickCatalog.CandidateCount;
            int key = unchecked((int)(serial & 0x7FFFFFFF));
            int pitySlot = pity ? (int)(WorldGenMath.Hash(seed, key, 0, SaltPity) % (uint)n) : -1;
            KeyScratch.Clear();
            for (int slot = 0; slot < n; slot++)
            {
                bool rareOnly = slot == pitySlot;
                int tier = RollTier(WorldGenMath.Hash(seed, key, slot, SaltTier), rareOnly);
                LootPickDef row = PickRow(seed, key, slot, tier, rareOnly);
                if (row == null)
                {
                    continue;
                }
                KeyScratch.Add(DedupeKey(row));
                int span = row.Max - row.Min + 1;
                int amount = row.Min + (int)(WorldGenMath.Hash(seed, key, slot, SaltAmount) % (uint)Math.Max(1, span));
                string target = row.Target ?? string.Empty;
                if (row.Kind == LootPickCatalog.KindCore)
                {
                    IReadOnlyList<AnalysisLoreDef> lore = AnalysisCatalog.Lore;
                    target = lore.Count > 0 ? lore[(int)(WorldGenMath.Hash(seed, key, slot, SaltLore) % (uint)lore.Count)].Id : string.Empty;
                }
                result.Add(new LootCandidateRecord
                {
                    RowId = row.Id, Kind = row.Kind, ItemId = row.Item.Id, TargetId = target, Amount = amount, Tier = row.Tier,
                });
            }
            KeyScratch.Clear();
            PoolScratch.Clear();
            return result;
        }

        /// <summary>包内去重键：同一种固件的“加密固件”与“原型刻录数据”是同一个身份，同一个数据包里只出一个（种类按固件算）。</summary>
        private static string DedupeKey(LootPickDef r) =>
            r.Kind == LootPickCatalog.KindCracked ? LootPickCatalog.KeyOf(LootPickCatalog.KindFirmware, r.Item?.Id, r.Target) : r.Key;

        /// <summary>按权重抽稀有度下标（<paramref name="rareOnly"/> = 只在稀有及以上里抽）。</summary>
        public static int RollTier(uint hash, bool rareOnly)
        {
            int from = rareOnly ? LootPickCatalog.RareIndex : 0;
            float total = 0f;
            for (int i = from; i < LootPickCatalog.Tiers.Length; i++)
            {
                total += LootPickCatalog.Weight(i);
            }
            if (total <= 0f)
            {
                return from;
            }
            double r = hash / 4294967296.0 * total;
            for (int i = from; i < LootPickCatalog.Tiers.Length; i++)
            {
                r -= LootPickCatalog.Weight(i);
                if (r < 0)
                {
                    return i;
                }
            }
            return LootPickCatalog.Tiers.Length - 1;
        }

        private static LootPickDef PickRow(uint seed, int key, int slot, int tier, bool rareOnly)
        {
            int last = LootPickCatalog.Tiers.Length - 1;
            int floor = rareOnly ? LootPickCatalog.RareIndex : 0;
            // 先本档，再往低（不低于 floor），再往高。
            for (int step = 0; step <= last * 2; step++)
            {
                int t = step == 0 ? tier : step <= tier - floor ? tier - step : tier + (step - (tier - floor));
                if (t < floor || t > last)
                {
                    continue;
                }
                TierScratch.Clear();
                int total = 0;
                foreach (LootPickDef r in PoolScratch)
                {
                    if (r.TierIndex == t && !KeyScratch.Contains(DedupeKey(r)))
                    {
                        TierScratch.Add(r);
                        total += r.Weight;
                    }
                }
                if (total <= 0)
                {
                    continue;
                }
                uint h = WorldGenMath.Hash(seed, key, slot * 8 + t, SaltRow);
                int pick = (int)(h % (uint)total);
                foreach (LootPickDef r in TierScratch)
                {
                    pick -= r.Weight;
                    if (pick < 0)
                    {
                        TierScratch.Clear();
                        return r;
                    }
                }
            }
            TierScratch.Clear();
            if (rareOnly)
            {
                return PickRow(seed, key, slot, tier, false); // 池里一个稀有都没有（坏表）：退回普通抽法，候选照样有 3 个
            }
            return null;
        }

        // ─────────────────────────────── 携带者 ───────────────────────────────

        private static bool Alive(int logicId) => logicId > 0 && MachineRegistry.TryGetRecord(logicId, out MachineRecord m) && m != null && m.IsAlive;

        /// <summary>击毁它的机器（致命一击来自己方机器）；否则 <see cref="LootPickCatalog.ShareRadius"/> 内最近的己方机器；都没有返回 0（留在原地）。</summary>
        public static int ResolveHolder(CampaignState s, CombatSite site, int killerUnit, Vector2 at)
        {
            if (site != null && killerUnit > 0 && site.TryGetMachineOfUnit(killerUnit, out int logic) && Alive(logic))
            {
                return logic;
            }
            return NearestMachine(HomeValleyLayout.RegionId, at, LootPickCatalog.ShareRadius, 0);
        }

        private static Vector2 PositionOf(MachineRecord m) => MachineRegistry.TryGetLivePosition(m.LogicId, out Vector2 live) ? live : m.WorldPosition;

        private static string RegionOf(MachineRecord m) => string.IsNullOrEmpty(m?.RegionId) ? HomeValleyLayout.RegionId : m.RegionId;

        /// <summary>某地点 <paramref name="radius"/> 格内最近的存活己方机器（排除 <paramref name="except"/>；同距离按 LogicId 小的）。O(机器数)。</summary>
        public static int NearestMachine(string regionId, Vector2 at, float radius, int except)
        {
            int best = 0;
            float bestD = radius * radius;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.LogicId == except || m.IsInFactory || RegionOf(m) != regionId)
                {
                    continue;
                }
                float d = (PositionOf(m) - at).sqrMagnitude;
                if (d < bestD || (Math.Abs(d - bestD) < 1e-4f && best > 0 && m.LogicId < best))
                {
                    bestD = d;
                    best = m.LogicId;
                }
            }
            return best;
        }

        /// <summary>同一支在外远征队里还活着、同一地点的机器（离 <paramref name="at"/> 最近的；同距离按 LogicId 小的）。不在远征队里返回 0。</summary>
        public static int Teammate(CampaignState s, int deadId, Vector2 at)
        {
            ExpeditionMarchState march = ExpeditionMarchService.Current(s);
            if (march?.Members == null || !ExpeditionMarchService.IsTeamMember(s, deadId))
            {
                return 0;
            }
            string region = MachineRegistry.TryGetRecord(deadId, out MachineRecord dead) ? RegionOf(dead) : HomeValleyLayout.RegionId;
            int best = 0;
            float bestD = float.MaxValue;
            foreach (int id in march.Members)
            {
                if (id == deadId || !MachineRegistry.TryGetRecord(id, out MachineRecord m) || m == null || !m.IsAlive || RegionOf(m) != region)
                {
                    continue;
                }
                float d = (PositionOf(m) - at).sqrMagnitude;
                if (d < bestD || (Math.Abs(d - bestD) < 1e-4f && id < best))
                {
                    bestD = d;
                    best = id;
                }
            }
            return best;
        }

        /// <summary>机器在家园 / 带仓库的前哨站：在家园所在的星球表面上，离归还核心或运转中的仓库 <see cref="LootPickCatalog.DepotRadius"/> 格以内。O(建筑数)。</summary>
        public static bool AtDepot(CampaignState s, MachineRecord m)
        {
            if (s == null || m == null || !m.IsAlive || m.IsInFactory || RegionOf(m) != HomeValleyLayout.RegionId)
            {
                return false;
            }
            CollectDepots(s, DepotScratch);
            bool at = AtDepot(m, DepotScratch);
            DepotScratch.Clear();
            return at;
        }

        private static readonly List<Vector2> DepotScratch = new List<Vector2>(16);
        private static readonly List<(int Id, string Region, Vector2 Pos)> MachineScratch = new List<(int, string, Vector2)>(64);

        /// <summary>运转中的归还核心与仓库的位置（O(建筑数)；对账时每次只收集一遍）。</summary>
        private static void CollectDepots(CampaignState s, List<Vector2> into)
        {
            into.Clear();
            foreach (BuildingRecord b in s?.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.RegionId == HomeValleyLayout.RegionId && b.ConstructionState == BuildingConstructionState.Operational
                    && (b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore || b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse))
                {
                    into.Add(b.Position);
                }
            }
        }

        private static bool AtDepot(MachineRecord m, List<Vector2> depots)
        {
            if (m == null || !m.IsAlive || m.IsInFactory || RegionOf(m) != HomeValleyLayout.RegionId)
            {
                return false;
            }
            Vector2 at = PositionOf(m);
            float r2 = LootPickCatalog.DepotRadius * LootPickCatalog.DepotRadius;
            foreach (Vector2 d in depots)
            {
                if ((d - at).sqrMagnitude <= r2)
                {
                    return true;
                }
            }
            return false;
        }

        // ─────────────────────────────── 阵亡 / 拾取 / 对账 ───────────────────────────────

        /// <summary>
        /// 机器阵亡（<see cref="MachineRegistry.MarkDeadByLogicId"/>，存活 → 阵亡的唯一翻转点）：它携带的数据包交给同队还活着的机器；没有可交的留在阵亡处（永不消失）。
        /// 它货舱里分拣得到的物品掉在原地（普通战利品，带身份，与野外掉落同一条搬运路径）。O(数据包数 + 货舱物品数 + 成员数)，只在阵亡时。
        /// </summary>
        public static void OnMachineDied(CampaignState s, MachineRecord m, Vector2 at)
        {
            LootPacketState st = StateOf(s);
            if (st == null || m == null || m.LogicId <= 0)
            {
                return;
            }
            int moved = 0;
            int dropped = 0;
            int mate = 0;
            foreach (LootPackageRecord p in st.Packets)
            {
                if (p == null || p.Holder != m.LogicId)
                {
                    continue;
                }
                if (KeyItemRetention.PassesToTeammate(KeyItemRetention.Kind.DataPacket))
                {
                    mate = mate > 0 ? mate : Teammate(s, m.LogicId, at);
                }
                if (mate > 0)
                {
                    p.Holder = mate;
                    moved++;
                }
                else if (KeyItemRetention.StaysOnWipe(KeyItemRetention.Kind.DataPacket))
                {
                    p.Holder = 0;
                    p.RegionId = RegionOf(m);
                    p.X = at.x;
                    p.Y = at.y;
                    dropped++;
                }
            }
            string name = MachineNaming.Short(m);
            if (moved > 0 && MachineRegistry.TryGetRecord(mate, out MachineRecord to))
            {
                NotificationCenter.Post(NotifyType, GameText.Format("loot.pick.transfer", name, moved, MachineNaming.Short(to)), new Vector3(at.x, 0f, at.y));
            }
            if (dropped > 0)
            {
                GuidanceHooks.Raise(GuidanceHooks.LootFirstPacketDropped);
                NotificationCenter.Post(NotifyType, GameText.Format("loot.pick.dropped", name, dropped, Mathf.RoundToInt(at.x), Mathf.RoundToInt(at.y)), new Vector3(at.x, 0f, at.y));
            }
            DropHeldOf(s, st, m, at);
            if (moved + dropped > 0)
            {
                Revision++;
            }
        }

        private static void DropHeldOf(CampaignState s, LootPacketState st, MachineRecord m, Vector2 at)
        {
            if (st.Held.Length == 0)
            {
                return;
            }
            var keep = new List<LootHeldRecord>(st.Held.Length);
            foreach (LootHeldRecord h in st.Held)
            {
                if (h != null && h.Machine == m.LogicId)
                {
                    DropOnGround(s, h, RegionOf(m), at);
                }
                else if (h != null)
                {
                    keep.Add(h);
                }
            }
            if (keep.Count != st.Held.Length)
            {
                st.Held = keep.ToArray();
                Revision++;
            }
        }

        /// <summary>货舱里的一份分拣物品落在地上（身份登记进地面身份表，搬回家园时按协议入库；家园所在星球上的开搬运单）。</summary>
        private static void DropOnGround(CampaignState s, LootHeldRecord h, string region, Vector2 at)
        {
            if (!ItemCatalog.TryGet(h.ItemId, out ItemDef item))
            {
                return;
            }
            GroundItemRecord g = HomeValleyCargo.SpawnGroundItem(s, region, at, item.ResourceType, Math.Max(1, h.Amount), h.HeldId);
            if (g == null)
            {
                return;
            }
            if (h.Kind != LootPickCatalog.KindItem)
            {
                FieldLootService.TagGround(s, h.HeldId, h.ItemId, h.TargetId, h.Kind == LootPickCatalog.KindCracked ? OriginPrototype : OriginPick, h.Faction);
            }
            if (region == HomeValleyLayout.RegionId)
            {
                HomeValleyWorkOrders.TryCreateHaulPool(s, g.GroundItemId);
            }
        }

        /// <summary>
        /// 每 loot.pick.check_seconds 游戏秒（只看步序号：tick 是间隔的整数倍时，与观察 / 读档无关）：携带者对账（记录没了 / 已阵亡却没走过阵亡回调的旧档 → 留在原处），
        /// 地上的数据包被走近的己方机器捡起，携带者回到家园 / 带仓库的前哨站时提示一次“可以分拣了”。没有数据包时 O(1)。
        /// </summary>
        public static void WorldStep(CampaignState s, long tick, int stepHz)
        {
            LootPacketState st = s?.LootPackets;
            if (st?.Packets == null || st.Packets.Length == 0)
            {
                _lastDepotReady = 0;
                return;
            }
            long every = Math.Max(1L, (long)Math.Round(LootPickCatalog.CheckSeconds * Math.Max(1, stepHz)));
            if (tick % every != 0)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            StateOf(s);
            bool changed = false;
            int ready = 0;
            float claim2 = LootPickCatalog.ClaimRadius * LootPickCatalog.ClaimRadius;
            CollectDepots(s, DepotScratch);
            MachineScratch.Clear();
            bool anyGround = false;
            foreach (LootPackageRecord p in st.Packets)
            {
                anyGround |= p != null && (p.Holder <= 0 || !Alive(p.Holder));
            }
            if (anyGround)
            {
                foreach (MachineRecord m in MachineRegistry.AllRecords)
                {
                    if (m != null && m.IsAlive && !m.IsInFactory)
                    {
                        MachineScratch.Add((m.LogicId, RegionOf(m), PositionOf(m)));
                    }
                }
            }
            foreach (LootPackageRecord p in st.Packets)
            {
                if (p == null)
                {
                    continue;
                }
                if (p.Holder > 0 && !Alive(p.Holder))
                {
                    MachineRegistry.TryGetRecord(p.Holder, out MachineRecord dead);
                    Vector2 at = dead != null ? (dead.DeathTick > 0 ? dead.DeathPosition : dead.WorldPosition) : new Vector2(p.X, p.Y);
                    p.RegionId = dead != null ? (string.IsNullOrEmpty(dead.DeathRegionId) ? RegionOf(dead) : dead.DeathRegionId) : p.RegionId;
                    p.X = at.x;
                    p.Y = at.y;
                    p.Holder = 0;
                    changed = true;
                }
                if (p.Holder <= 0)
                {
                    int by = 0;
                    float bestD = claim2;
                    var at = new Vector2(p.X, p.Y);
                    string region = string.IsNullOrEmpty(p.RegionId) ? HomeValleyLayout.RegionId : p.RegionId;
                    foreach ((int id, string mRegion, Vector2 pos) in MachineScratch)
                    {
                        if (mRegion != region)
                        {
                            continue;
                        }
                        float d = (pos - at).sqrMagnitude;
                        if (d <= bestD && (by == 0 || d < bestD || id < by))
                        {
                            bestD = d;
                            by = id;
                        }
                    }
                    if (by > 0 && MachineRegistry.TryGetRecord(by, out MachineRecord picker))
                    {
                        p.Holder = by;
                        changed = true;
                        Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.Pickup, at, GameText.Format("loot.pick.claimed", MachineNaming.Short(picker), 1));
                    }
                }
                if (p.Holder > 0 && MachineRegistry.TryGetRecord(p.Holder, out MachineRecord h) && AtDepot(h, DepotScratch))
                {
                    ready++;
                }
            }
            DepotScratch.Clear();
            MachineScratch.Clear();
            if (ready > 0 && _lastDepotReady >= 0 && ready > _lastDepotReady)
            {
                NotificationCenter.Post(NotifyType, GameText.Format("loot.pick.home", ready));
            }
            _lastDepotReady = ready;
            if (changed)
            {
                Revision++;
            }
            StepChecks++;
            StepMaxMs = Math.Max(StepMaxMs, (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        // ─────────────────────────────── 查询（界面 / 自检）───────────────────────────────

        public static int Count(CampaignState s) => s?.LootPackets?.Packets?.Length ?? 0;

        public static IReadOnlyList<LootPackageRecord> Packets(CampaignState s) => StateOf(s)?.Packets ?? Array.Empty<LootPackageRecord>();

        public static LootPackageRecord Find(CampaignState s, string packetId)
        {
            foreach (LootPackageRecord p in Packets(s))
            {
                if (p != null && p.PacketId == packetId)
                {
                    return p;
                }
            }
            return null;
        }

        /// <summary>现在能不能分拣这个数据包（不能时给原因文本键）；<paramref name="atDepot"/> = 选中的直接入库（否则装进货舱）。</summary>
        public static bool CanProcess(CampaignState s, LootPackageRecord p, out string reasonKey, out bool atDepot)
        {
            atDepot = false;
            reasonKey = null;
            if (s == null || p == null)
            {
                reasonKey = "loot.pick.reason.none";
                return false;
            }
            if (p.Holder <= 0 || !MachineRegistry.TryGetRecord(p.Holder, out MachineRecord m) || m == null || !m.IsAlive)
            {
                reasonKey = "loot.pick.reason.ground";
                return false;
            }
            atDepot = AtDepot(s, m);
            if (atDepot)
            {
                return true;
            }
            // 野外装货舱只给在外的远征队成员：回家时由“远征卸货”规则卸下（FGR-EXP-050）。不在远征队里的机器（家园巡逻 / 迎战）没有这条卸货路径，
            // 开回家园 / 仓库旁再分拣（选中的直接入库），不会把东西塞进一个永远卸不下的货舱。
            if (!ExpeditionMarchService.IsTeamMember(s, m.LogicId))
            {
                reasonKey = ReasonGoHome;
                return false;
            }
            if (GameClock.Paused)
            {
                return true;
            }
            reasonKey = "loot.pick.reason.not_now";
            return false;
        }

        /// <summary>现在就能分拣的数据包数。O(数据包数 × 建筑数)，界面按版本 / 每秒刷新时调用。</summary>
        public static int ReadyCount(CampaignState s)
        {
            int n = 0;
            foreach (LootPackageRecord p in Packets(s))
            {
                n += CanProcess(s, p, out _, out _) ? 1 : 0;
            }
            return n;
        }

        /// <summary>这一个候选选中后，没选的转成多少技术数据。</summary>
        public static int LeftoverTechIf(LootPackageRecord p, int index)
        {
            int t = 0;
            for (int i = 0; i < (p?.Candidates?.Length ?? 0); i++)
            {
                if (i != index && p.Candidates[i] != null)
                {
                    t += LootPickCatalog.TechFor(p.Candidates[i].Tier);
                }
            }
            return t;
        }

        public static int SpaceOf(LootCandidateRecord c) => c == null ? 0 : LootPickCatalog.SpaceOf(c.ItemId, c.Amount);

        /// <summary>一台机器货舱已用的空间：分拣得到的物品 + 机器货舱里的其它货物（施工材料等，按同一套占用）。</summary>
        public static int UsedSpace(CampaignState s, MachineRecord m)
        {
            if (m == null)
            {
                return 0;
            }
            int used = 0;
            foreach (LootHeldRecord h in StateOf(s)?.Held ?? Array.Empty<LootHeldRecord>())
            {
                used += h != null && h.Machine == m.LogicId ? h.Space : 0;
            }
            foreach (CargoEntry c in m.Cargo ?? Array.Empty<CargoEntry>())
            {
                if (c != null && c.Amount > 0)
                {
                    used += LootPickCatalog.SpaceOf(ItemCatalog.TryGetByResource(c.ResourceType, out ItemDef d) ? d.Id : c.ResourceType, c.Amount);
                }
            }
            return used;
        }

        public static int FreeSpace(CampaignState s, MachineRecord m) => Math.Max(0, LootPickCatalog.CapacityOf(s, m) - UsedSpace(s, m));

        /// <summary>
        /// 能装下 <paramref name="space"/> 的机器：先携带者，再同队还活着、同一地点的机器（LogicId 升序）。<paramref name="maxFree"/> = 其中单台最大的空余。
        /// 没有返回 0。
        /// </summary>
        public static int CargoRoomFor(CampaignState s, int holder, int space, out int maxFree)
        {
            maxFree = 0;
            if (!MachineRegistry.TryGetRecord(holder, out MachineRecord hm) || hm == null || !hm.IsAlive)
            {
                return 0;
            }
            int free = FreeSpace(s, hm);
            maxFree = free;
            if (free >= space)
            {
                return holder;
            }
            ExpeditionMarchState march = ExpeditionMarchService.Current(s);
            if (march?.Members == null || !ExpeditionMarchService.IsTeamMember(s, holder))
            {
                return 0;
            }
            var ids = new List<int>(march.Members);
            ids.Sort();
            foreach (int id in ids)
            {
                if (id == holder || !MachineRegistry.TryGetRecord(id, out MachineRecord m) || m == null || !m.IsAlive || RegionOf(m) != RegionOf(hm))
                {
                    continue;
                }
                int f = FreeSpace(s, m);
                maxFree = Math.Max(maxFree, f);
                if (f >= space)
                {
                    return id;
                }
            }
            return 0;
        }

        /// <summary>在家园 / 前哨站直接入库时放不放得下（加密固件进固件库，满了进待领取、不丢，所以总放得下）。</summary>
        public static bool DepotCanStore(CampaignState s, LootCandidateRecord c)
        {
            if (c == null || !ItemCatalog.TryGet(c.ItemId, out ItemDef item))
            {
                return false;
            }
            if (c.Kind == LootPickCatalog.KindFirmware || c.Kind == LootPickCatalog.KindCracked)
            {
                return true;
            }
            return HomeInventory.Space(s, item) >= Math.Max(1, c.Amount);
        }

        public readonly struct PickResult
        {
            public readonly bool Success;
            public readonly string Message;
            public readonly string Destination;
            public readonly int Machine;
            public readonly int TechData;

            public PickResult(bool success, string message, string destination, int machine, int tech)
            {
                Success = success;
                Message = message;
                Destination = destination;
                Machine = machine;
                TechData = tech;
            }

            public static PickResult Fail(string message) => new PickResult(false, message, null, 0, 0);
        }

        /// <summary>
        /// 分拣：选第 <paramref name="index"/> 个候选。能不能分拣见 <see cref="CanProcess"/>；在家园 / 前哨站直接入库（仓库放不下 → 拒绝），在野外战略暂停中装进货舱
        /// （全队放不下 → 拒绝并提示先丢弃或改选更小的）。被拒时数据包原样留着。成功后没选的转成技术数据、记一条选择记录、三个候选都记为“见过”。
        /// </summary>
        public static PickResult TryPick(CampaignState s, string packetId, int index)
        {
            LootPacketState st = StateOf(s);
            LootPackageRecord p = Find(s, packetId);
            if (st == null || p == null)
            {
                return PickResult.Fail(GameText.Get("loot.pick.reason.resolved"));
            }
            if (index < 0 || index >= p.Candidates.Length || p.Candidates[index] == null)
            {
                return PickResult.Fail(GameText.Get("loot.pick.reason.bad_index"));
            }
            if (!CanProcess(s, p, out string why, out bool atDepot))
            {
                return PickResult.Fail(GameText.Get(why));
            }
            LootCandidateRecord c = p.Candidates[index];
            MachineRegistry.TryGetRecord(p.Holder, out MachineRecord holder);
            Vector2 at = holder != null ? PositionOf(holder) : new Vector2(p.X, p.Y);
            string heldId = HeldPrefix + p.PacketId;
            string dest;
            int machine = 0;
            if (atDepot)
            {
                if (!DepotCanStore(s, c))
                {
                    return PickResult.Fail(GameText.Format("loot.pick.depot_full", CandidateName(c)));
                }
                Deliver(s, c.Kind, c.ItemId, c.TargetId, c.Amount, p.Faction, at, heldId);
                dest = DestDepot;
            }
            else
            {
                int space = SpaceOf(c);
                machine = CargoRoomFor(s, p.Holder, space, out int maxFree);
                if (machine <= 0)
                {
                    return PickResult.Fail(GameText.Format("loot.pick.full", space, maxFree));
                }
                var held = new List<LootHeldRecord>(st.Held.Length + 1);
                held.AddRange(st.Held);
                held.Add(new LootHeldRecord
                {
                    HeldId = heldId, Machine = machine, Kind = c.Kind, ItemId = c.ItemId, TargetId = c.TargetId, Amount = c.Amount, Space = space, Faction = p.Faction,
                });
                st.Held = held.ToArray();
                dest = DestCargo;
            }
            int tech = LeftoverTechIf(p, index);
            GrantTech(s, TxPrefix + p.PacketId, p.PacketId, tech);
            foreach (LootCandidateRecord x in p.Candidates)
            {
                MarkSeen(st, x);
            }
            var rest = new List<LootPackageRecord>(st.Packets.Length);
            foreach (LootPackageRecord x in st.Packets)
            {
                if (x != null && x != p)
                {
                    rest.Add(x);
                }
            }
            st.Packets = rest.ToArray();
            var log = new List<LootPickLogRecord>(st.Picks.Length + 1);
            int skip = Math.Max(0, st.Picks.Length + 1 - LootPickCatalog.LogMax);
            for (int i = skip; i < st.Picks.Length; i++)
            {
                log.Add(st.Picks[i]);
            }
            log.Add(new LootPickLogRecord
            {
                PacketId = p.PacketId, Source = p.Source, Faction = p.Faction, ChosenRowId = c.RowId, ChosenTier = c.Tier, Destination = dest, TechData = tech, Tick = GameClock.Ticks,
            });
            st.Picks = log.ToArray();
            st.PacketsResolved++;
            st.LeftoverTech += tech;
            Revision++;
            HomeInventory.Touch();
            GuidanceHooks.Raise(GuidanceHooks.LootFirstPick);
            string msg = dest == DestDepot
                ? GameText.Format("loot.pick.done.depot", CandidateName(c), tech)
                : GameText.Format("loot.pick.done.cargo", CandidateName(c), MachineRegistry.TryGetRecord(machine, out MachineRecord mm) ? MachineNaming.Short(mm) : string.Empty, tech);
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Pickup, msg);
            return new PickResult(true, msg, dest, machine, tech);
        }

        private static void GrantTech(CampaignState s, string tx, string owner, int amount)
        {
            if (amount <= 0 || CampaignEconomyLedger.Find(s, tx) != null)
            {
                return;
            }
            CampaignEconomyLedger.ProposeProduce(s, tx, owner, CampaignEconomyLedger.ResourceTechData, amount);
            CampaignEconomyLedger.Reserve(s, tx);
            CampaignEconomyLedger.MarkRunning(s, tx);
            if (CampaignEconomyLedger.Commit(s, tx).Success)
            {
                HomeInventory.Touch();
            }
        }

        /// <summary>一份分拣物品入库（家园）：加密固件 → 固件库未破解芯片；原型刻录数据 → 先按内容破解再发芯片；未解析模块 / 数据核心 → 仓库 + 解析身份；材料 / 组件 → 仓库（放不下的落在 <paramref name="at"/> 由机器搬回）。
        /// 同一个 <paramref name="salvageId"/> 的芯片只发一次。</summary>
        private static void Deliver(CampaignState s, string kind, string itemId, string targetId, int amount, string faction, Vector2 at, string salvageId)
        {
            switch (kind)
            {
                case LootPickCatalog.KindCracked:
                    HomeValleyAnalysis.CrackFirmwareContent(s, targetId);
                    FieldLootService.DeliverEnemyItem(s, AnalysisCatalog.EncryptedFirmwareId, targetId, OriginPick, at, salvageId);
                    break;
                case LootPickCatalog.KindFirmware:
                    FieldLootService.DeliverEnemyItem(s, AnalysisCatalog.EncryptedFirmwareId, targetId, OriginPick, at, salvageId);
                    break;
                case LootPickCatalog.KindModule:
                case LootPickCatalog.KindCore:
                    HomeValleyAnalysis.Acquire(s, itemId, targetId, OriginPick, Math.Max(1, amount), at);
                    break;
                default:
                    if (ItemCatalog.TryGet(itemId, out ItemDef item))
                    {
                        int stored = HomeInventory.Add(s, item, amount);
                        if (stored < amount)
                        {
                            HomeValleyConstruction.ReturnMaterials(s, at, item.ResourceType, amount - stored, salvageId);
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// 远征队回到家园（StandingRuleService 的“远征卸货”规则开着时，对每台返回的机器调用）：这台机器货舱里分拣得到的物品全部入库。返回卸下的件数。
        /// 规则关着时不调用——货留在货舱里（FGR-BASE-020），面板“货舱里”一栏照常列出，机器在仓库旁时玩家可以逐件点“入库”（<see cref="TryStoreHeld"/>）。
        /// </summary>
        public static int UnloadHeld(CampaignState s, int machineId, Vector2 at)
        {
            LootPacketState st = StateOf(s);
            if (st == null || st.Held.Length == 0)
            {
                return 0;
            }
            int n = 0;
            var keep = new List<LootHeldRecord>(st.Held.Length);
            foreach (LootHeldRecord h in st.Held)
            {
                if (h != null && h.Machine == machineId)
                {
                    Deliver(s, h.Kind, h.ItemId, h.TargetId, h.Amount, h.Faction, at, h.HeldId);
                    n += Math.Max(1, h.Amount);
                }
                else if (h != null)
                {
                    keep.Add(h);
                }
            }
            if (n > 0)
            {
                st.Held = keep.ToArray();
                Revision++;
                HomeInventory.Touch();
            }
            return n;
        }

        /// <summary>丢弃货舱里的一份分拣物品：落在携带机器旁边（普通战利品，可以再捡回 / 由机器搬回）。</summary>
        public static bool TryDiscard(CampaignState s, string heldId, out string message)
        {
            message = null;
            LootPacketState st = StateOf(s);
            LootHeldRecord h = null;
            foreach (LootHeldRecord x in st?.Held ?? Array.Empty<LootHeldRecord>())
            {
                if (x != null && x.HeldId == heldId)
                {
                    h = x;
                    break;
                }
            }
            if (h == null || !MachineRegistry.TryGetRecord(h.Machine, out MachineRecord m) || m == null)
            {
                return false;
            }
            var keep = new List<LootHeldRecord>(st.Held.Length);
            foreach (LootHeldRecord x in st.Held)
            {
                if (x != null && x != h)
                {
                    keep.Add(x);
                }
            }
            st.Held = keep.ToArray();
            DropOnGround(s, h, RegionOf(m), PositionOf(m));
            Revision++;
            message = GameText.Format("loot.pick.discarded", HeldName(h), MachineNaming.Short(m));
            return true;
        }

        public static IReadOnlyList<LootHeldRecord> Held(CampaignState s) => StateOf(s)?.Held ?? Array.Empty<LootHeldRecord>();

        /// <summary>货舱里分拣得到的物品件数（顶栏用，O(1)）。</summary>
        public static int HeldCount(CampaignState s) => s?.LootPackets?.Held?.Length ?? 0;

        /// <summary>这份货舱物品现在能不能手动入库：携带它的机器活着、在家园 / 带仓库的前哨站。</summary>
        public static bool CanStoreHeld(CampaignState s, LootHeldRecord h) =>
            h != null && MachineRegistry.TryGetRecord(h.Machine, out MachineRecord m) && m != null && m.IsAlive && AtDepot(s, m);

        /// <summary>
        /// 玩家在面板上点“入库”：携带机器在家园 / 仓库旁时，这一份按协议入库（与远征卸货同一条 <see cref="Deliver"/>）。
        /// 用于“远征卸货”规则关着时回来的货、旧档里不在远征队的机器上的货；不在仓库旁时拒绝并写明原因，货留在货舱里。
        /// </summary>
        public static bool TryStoreHeld(CampaignState s, string heldId, out string message)
        {
            message = null;
            LootPacketState st = StateOf(s);
            LootHeldRecord h = null;
            foreach (LootHeldRecord x in st?.Held ?? Array.Empty<LootHeldRecord>())
            {
                if (x != null && x.HeldId == heldId)
                {
                    h = x;
                    break;
                }
            }
            if (h == null || !MachineRegistry.TryGetRecord(h.Machine, out MachineRecord m) || m == null || !m.IsAlive)
            {
                message = GameText.Get("loot.pick.reason.resolved");
                return false;
            }
            if (!AtDepot(s, m))
            {
                message = GameText.Format("loot.pick.store.not_home", HeldName(h), MachineNaming.Short(m));
                return false;
            }
            var keep = new List<LootHeldRecord>(st.Held.Length);
            foreach (LootHeldRecord x in st.Held)
            {
                if (x != null && x != h)
                {
                    keep.Add(x);
                }
            }
            st.Held = keep.ToArray();
            Deliver(s, h.Kind, h.ItemId, h.TargetId, h.Amount, h.Faction, PositionOf(m), h.HeldId);
            Revision++;
            HomeInventory.Touch();
            message = GameText.Format("loot.pick.stored", HeldName(h), MachineNaming.Short(m));
            return true;
        }

        // ─────────────────────────────── 新 / 对比 / 文字 ───────────────────────────────

        private static string KeyOf(LootCandidateRecord c) =>
            LootPickCatalog.KeyOf(c.Kind, c.ItemId, c.Kind == LootPickCatalog.KindItem ? string.Empty : c.TargetId);

        private static void MarkSeen(LootPacketState st, LootCandidateRecord c)
        {
            if (c == null)
            {
                return;
            }
            string k = KeyOf(c);
            if (Array.IndexOf(st.SeenKeys, k) >= 0)
            {
                return;
            }
            var list = new List<string>(st.SeenKeys) { k };
            st.SeenKeys = list.ToArray();
        }

        /// <summary>从没见过：没在以前的数据包里分拣过（三个候选分拣时都记为见过），也不是已经有 / 已经解锁的东西。</summary>
        public static bool IsNew(CampaignState s, LootCandidateRecord c)
        {
            LootPacketState st = StateOf(s);
            if (st == null || c == null || Array.IndexOf(st.SeenKeys, KeyOf(c)) >= 0)
            {
                return false;
            }
            switch (c.Kind)
            {
                case LootPickCatalog.KindFirmware:
                case LootPickCatalog.KindCracked:
                    return ChipCount(s, c.TargetId) == 0 && !MechanicalContentUnlock.IsUnlocked(s, c.TargetId);
                case LootPickCatalog.KindModule:
                    return !MechanicalContentUnlock.IsUnlocked(s, c.TargetId);
                case LootPickCatalog.KindCore:
                    return Array.IndexOf(s.Research?.Analysis?.LoreRead ?? Array.Empty<string>(), c.TargetId) < 0;
                default:
                    return HomeInventory.Stock(s, c.ItemId) <= 0;
            }
        }

        private static int ChipCount(CampaignState s, string firmwareId)
        {
            int n = 0;
            foreach (PrimitiveChipRecord r in s?.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
            {
                n += r != null && r.CardDefId == firmwareId ? 1 : 0;
            }
            return n;
        }

        /// <summary>候选与已有同类物品的比较（FG08 第 7 章“三选一时可以把候选和自己已有的同类物品比较”）。</summary>
        public static string CompareText(CampaignState s, LootCandidateRecord c)
        {
            if (s == null || c == null)
            {
                return string.Empty;
            }
            switch (c.Kind)
            {
                case LootPickCatalog.KindFirmware:
                case LootPickCatalog.KindCracked:
                {
                    FirmwareCategory cat = FirmwareKinds.CategoryOf(c.TargetId);
                    string catKey = FirmwareKinds.TryGetRow(c.TargetId, out GameConfig.fg.FirmwareKind row) ? row.Category : string.Empty;
                    var kinds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (PrimitiveChipRecord r in s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
                    {
                        if (r != null && FirmwareKinds.IsFirmware(r.CardDefId) && FirmwareKinds.CategoryOf(r.CardDefId) == cat)
                        {
                            kinds.Add(r.CardDefId);
                        }
                    }
                    bool cracked = c.Kind == LootPickCatalog.KindCracked || !FirmwareKinds.IsRaw(s, c.TargetId);
                    return GameText.Format("loot.pick.cmp.fw", ChipCount(s, c.TargetId), GameText.Get("firmware.category." + catKey), kinds.Count,
                        GameText.Get(cracked ? "loot.pick.cmp.fw.cracked" : "loot.pick.cmp.fw.raw"));
                }
                case LootPickCatalog.KindModule:
                    return GameText.Format("loot.pick.cmp.module", Feedback.FeedbackCues.ContentName(c.TargetId),
                        GameText.Get(MechanicalContentUnlock.IsUnlocked(s, c.TargetId) ? "loot.pick.cmp.module.known" : "loot.pick.cmp.module.unknown"),
                        HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId));
                case LootPickCatalog.KindCore:
                    return GameText.Format("loot.pick.cmp.core", HomeInventory.Stock(s, AnalysisCatalog.DataCoreId),
                        (s.Research?.Analysis?.LoreRead ?? Array.Empty<string>()).Length, AnalysisCatalog.Lore.Count);
                default:
                {
                    ItemDef item = ItemCatalog.Find(c.ItemId);
                    return GameText.Format("loot.pick.cmp.item", HomeInventory.Stock(s, c.ItemId), item != null ? HomeInventory.Space(s, item) : 0);
                }
            }
        }

        public static string CandidateName(LootCandidateRecord c)
        {
            if (c == null)
            {
                return string.Empty;
            }
            switch (c.Kind)
            {
                case LootPickCatalog.KindFirmware:
                    return GameText.Format("loot.pick.name.firmware", FirmwareKinds.DisplayName(c.TargetId) ?? c.TargetId);
                case LootPickCatalog.KindCracked:
                    return GameText.Format("loot.pick.name.cracked", FirmwareKinds.DisplayName(c.TargetId) ?? c.TargetId);
                case LootPickCatalog.KindModule:
                    return GameText.Format("loot.pick.name.module", Feedback.FeedbackCues.ContentName(c.TargetId));
                case LootPickCatalog.KindCore:
                    return GameText.Get("loot.pick.name.core");
                default:
                    return GameText.Format("loot.pick.name.item", ItemCatalog.Find(c.ItemId)?.Name ?? c.ItemId, c.Amount);
            }
        }

        private static string HeldName(LootHeldRecord h) =>
            CandidateName(new LootCandidateRecord { Kind = h.Kind, ItemId = h.ItemId, TargetId = h.TargetId, Amount = h.Amount });

        public static string HeldRowText(LootHeldRecord h) =>
            GameText.Format("loot.pick.held.row", HeldName(h), h.Space, MachineRegistry.TryGetRecord(h.Machine, out MachineRecord m) ? MachineNaming.Short(m) : "#" + h.Machine.ToString(CultureInfo.InvariantCulture));

        /// <summary>类型 · 稀有度 · 协议（图标 + 文字）· 占货舱。</summary>
        public static string MetaText(CampaignState s, LootCandidateRecord c)
        {
            if (c == null)
            {
                return string.Empty;
            }
            string type = c.Kind == LootPickCatalog.KindFirmware || c.Kind == LootPickCatalog.KindCracked ? "loot.pick.type.firmware"
                : c.Kind == LootPickCatalog.KindModule ? "loot.pick.type.component"
                : c.Kind == LootPickCatalog.KindCore ? "loot.pick.type.data"
                : c.ItemId == "combat_component" || c.ItemId == "structure_module" || c.ItemId == "drop_pod" ? "loot.pick.type.component"
                : "loot.pick.type.material";
            string protocol = c.Kind == LootPickCatalog.KindCracked
                ? GameText.Get("loot.pick.cracked.badge")
                : ItemProtocol.Badge(ItemProtocol.Of(c.ItemId)) + " · " + ItemProtocol.DestinationShort(c.Kind == LootPickCatalog.KindFirmware
                    ? ItemProtocol.DestinationOfFirmware(s, c.TargetId)
                    : ItemProtocol.DestinationOf(ItemCatalog.Find(c.ItemId)));
            return GameText.Format("loot.pick.meta", GameText.Get(type), RarityName(c.Tier), protocol, SpaceOf(c));
        }

        public static string RarityName(string tier) => GameText.Get("loot.rarity." + (string.IsNullOrEmpty(tier) ? "common" : tier));

        public static string OriginText(LootPackageRecord p)
        {
            string faction = string.IsNullOrEmpty(p?.Faction) ? GameText.Get("loot.pick.faction.none") : GameText.Get("faction." + p.Faction);
            return p?.Source == SourceEvent ? GameText.Get("loot.pick.origin.event")
                : p?.Source == SourceBoss ? GameText.Format("loot.pick.origin.boss", faction)
                : GameText.Format("loot.pick.origin.elite", faction);
        }

        /// <summary>“携带：机器名” / “在地上：地点（x, y）”。</summary>
        public static string HolderText(LootPackageRecord p)
        {
            if (p == null)
            {
                return string.Empty;
            }
            if (p.Holder > 0 && MachineRegistry.TryGetRecord(p.Holder, out MachineRecord m) && m != null && m.IsAlive)
            {
                return GameText.Format("loot.pick.holder", MachineNaming.Short(m));
            }
            return GameText.Format("loot.pick.holder.ground", GameText.Get(ExpeditionTextKeys.SiteNameKey(string.IsNullOrEmpty(p.RegionId) ? HomeValleyLayout.RegionId : p.RegionId)), Mathf.RoundToInt(p.X), Mathf.RoundToInt(p.Y));
        }

        /// <summary>货舱读数：携带机器已用 / 容量，全队单台最大空余。</summary>
        public static string CargoText(CampaignState s, LootPackageRecord p)
        {
            if (p == null || p.Holder <= 0 || !MachineRegistry.TryGetRecord(p.Holder, out MachineRecord m) || m == null)
            {
                return string.Empty;
            }
            CargoRoomFor(s, p.Holder, int.MaxValue, out int maxFree);
            return GameText.Format("loot.pick.cargo", MachineNaming.Short(m), UsedSpace(s, m), LootPickCatalog.CapacityOf(s, m), maxFree);
        }
    }
}
