using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG8-EXP-05 返回与结算（FG08 FGR-EXP-031 / 032 / 050；验收 FGT-EXP-008）。
    /// <para>远征结算（<see cref="Settle"/>）：远征队走回家园或带仓库的前哨站（<see cref="ExpeditionMarchService"/> 的“已返回”判定）、被投送舱接回（最后一台登舱）、
    /// 或全灭（行军途中 / 远征地点里）时，在离家报告结算之前写进这一份报告的 <see cref="AwayReportRecord.Settlement"/>：
    /// 带回的物品与去处（货舱里的货按“远征卸货”规则卸到仓库 / 固件库 / 解析台，规则关着写明“留在货舱里”）、带回的关键物 / 黑匣子 / 遗产（带回那一刻记，<see cref="NoteQuestItem"/>）、
    /// 三选一的选择记录、任务进度、生成的熔合线索、暴露变化、远征耗时、机器经历更新、留在原地的东西（普通战利品带回收倒计时，关键物永不消失）。</para>
    /// <para>离开一片区域前的清单（<see cref="CollectLeftBehind"/> / <see cref="LeaveWarning"/>）：远征地点的撤离面板、星球上下“返回家园”前（行军条要再点一次确认）都列出地上还没捡的战利品与消失倒计时。</para>
    /// 结算内容全部在发生的那一刻从存档里的真实记录取（与观察无关，FGR-BASE-021）；显示时按当前语言拼字（<see cref="AwayReportView"/>）。
    /// </summary>
    public static class ExpeditionSettlementService
    {
        public const string ViaFoot = "foot";
        public const string ViaPod = "pod";
        public const string ViaWiped = "wiped";

        public const string KindCargo = "cargo";
        public const string KindKey = "key";
        public const string KindBox = "box";
        public const string KindHeritage = "heritage";
        public const string KindLoot = "loot";
        public const string KindPacket = "packet";

        public const string DestWarehouse = "warehouse";
        public const string DestFirmware = "firmware";
        public const string DestAnalysis = "analysis";
        public const string DestAnalysisWait = "analysis_wait";
        public const string DestCargo = "cargo";
        public const string DestPod = "pod";
        public const string DestVault = "vault";
        public const string DestGallery = "gallery";
        public const string DestRecovered = "recovered";

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;

        /// <summary>星球上“离开这片区域前”列清单的半径（格，以队伍中心为圆心）。</summary>
        public static float LeaveRadius => Tuning("expedition.leave.list_radius", 60f);

        /// <summary>清单最多列几条（其余写“还有 N 处”）。</summary>
        public static int LeaveListMax => Math.Max(1, (int)Math.Round(Tuning("expedition.leave.list_max", 8f)));

        /// <summary>行军条“返回家园”二次确认里最多列几条。</summary>
        public static int LeaveWarnLines => Math.Max(1, (int)Math.Round(Tuning("expedition.leave.warn_lines", 3f)));

        // ── 自检读点 ──
        public static int SettleCount { get; private set; }
        public static string LastVia { get; private set; } = string.Empty;

        public static void ResetSessionState()
        {
            SettleCount = 0;
            LastVia = string.Empty;
        }

        // ─────────────────────────────── 远征途中带回的关键物 ───────────────────────────────

        /// <summary>远征队成员把一件区域关键物 / 黑匣子 / 遗产带回家园（<see cref="CargoService.DeliverQuestItem"/>）：记进这一次远征的结算。没有进行中的报告时不记。</summary>
        public static void NoteQuestItem(CampaignState s, RegionQuestItemRecord q)
        {
            AwayReportRecord r = AwayReportService.Current(s);
            if (r == null || q == null)
            {
                return;
            }
            AppendItem(r.Settlement, QuestRecord(q, Env.HeritageService.IsHeritage(q) ? DestVault : BlackBoxService.IsBlackBox(q) ? DestGallery : DestRecovered));
            AwayReportService.Touch();
        }

        /// <summary>
        /// 修复第 3 轮（审查 P1，分批用投送舱接回）：远征还没结束、这台成员的回收舱先落回投送站（<c>DropPodService</c> 的 OnReturnArrive，在带回关键物之前调用）——
        /// 记进进行中报告的 <see cref="ExpeditionSettlementRecord.Landed"/>，并把它货舱里的货按“远征卸货”规则写进带回的物品（卸货在下一模拟步按同一规则做，
        /// “送到解析台”的实际结果由 <see cref="OnUnloadAnalyzed"/> 回写）。之后它携带的关键物由 <see cref="CargoService.DeliverQuestItem"/> 记进同一份结算（<see cref="IsSettlingMember"/>）。
        /// 不是进行中报告的成员 / 没有进行中的报告：返回 false、什么都不做（远征已结束时落地的，结算里已经记成“随投送舱返回”）。O(货舱)，只在落地这一刻。
        /// </summary>
        public static bool OnMemberLanded(CampaignState s, int logicId)
        {
            AwayReportRecord r = AwayReportService.Current(s);
            if (r?.Members == null || Array.IndexOf(r.Members, logicId) < 0)
            {
                return false;
            }
            ExpeditionSettlementRecord st = r.Settlement ?? (r.Settlement = new ExpeditionSettlementRecord());
            int[] landed = st.Landed ?? Array.Empty<int>();
            if (Array.IndexOf(landed, logicId) >= 0)
            {
                return true;
            }
            var next = new int[landed.Length + 1];
            Array.Copy(landed, next, landed.Length);
            next[landed.Length] = logicId;
            st.Landed = next;
            StandingRuleRecord unload = StandingRuleService.ActiveUnloadRule(s);
            bool analyze = unload != null && unload.SendToAnalysis;
            foreach (LootHeldRecord h in s.LootPackets?.Held ?? Array.Empty<LootHeldRecord>())
            {
                if (h == null || h.Machine != logicId)
                {
                    continue;
                }
                st.CargoKept |= unload == null;
                AppendItem(st, new SettleItemRecord
                {
                    Kind = KindCargo, CargoKind = h.Kind ?? string.Empty, ItemId = h.ItemId ?? string.Empty, TargetId = h.TargetId ?? string.Empty, Amount = Math.Max(1, h.Amount),
                    Dest = unload == null ? DestCargo : DestOfCargo(h.Kind, analyze),
                });
            }
            AwayReportService.Touch();
            return true;
        }

        /// <summary>这台机器带回的关键物要不要记进进行中的远征结算：分批接回、已经随舱落地的成员（<see cref="ExpeditionSettlementRecord.Landed"/>）。行军名单里的成员由调用方另判。</summary>
        public static bool IsSettlingMember(CampaignState s, int logicId)
        {
            int[] landed = AwayReportService.Current(s)?.Settlement?.Landed;
            return landed != null && Array.IndexOf(landed, logicId) >= 0;
        }

        /// <summary>修复第 4 轮：这台机器在回收舱里、舱还没落回投送站（分批接回的返程途中）。O(舱数)，只在结算这一刻。</summary>
        private static bool RidingRecallPod(CampaignState s, int logicId)
        {
            DropPodRecord p = DropPodService.PodCarrying(s, logicId);
            return p != null && p.Mission == DropPodService.MissionRecall;
        }

        private static SettleItemRecord QuestRecord(RegionQuestItemRecord q, string dest) => new SettleItemRecord
        {
            Kind = Env.HeritageService.IsHeritage(q) ? KindHeritage : BlackBoxService.IsBlackBox(q) ? KindBox : KindKey,
            ItemId = q.ContentId ?? string.Empty,
            TargetId = q.SalvageInstanceId ?? string.Empty,
            Amount = 1,
            Dest = dest,
        };

        private static void AppendItem(ExpeditionSettlementRecord st, SettleItemRecord it)
        {
            SettleItemRecord[] old = st.Items ?? Array.Empty<SettleItemRecord>();
            foreach (SettleItemRecord x in old)
            {
                if (x != null && x.Kind != KindCargo && x.TargetId == it.TargetId && !string.IsNullOrEmpty(it.TargetId))
                {
                    x.Dest = it.Dest; // 同一件关键物只记一次（先登舱、后落地这类）
                    return;
                }
            }
            var next = new SettleItemRecord[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = it;
            st.Items = next;
        }

        // ─────────────────────────────── 结算 ───────────────────────────────

        /// <summary>货舱里一份货卸下后去哪（按协议；“远征卸货”勾了“送到解析台”时需要解析的物品排进解析台）。</summary>
        public static string DestOfCargo(string cargoKind, bool analyze)
        {
            switch (cargoKind)
            {
                case LootPickCatalog.KindFirmware:
                case LootPickCatalog.KindCracked:
                    return DestFirmware;
                case LootPickCatalog.KindModule:
                case LootPickCatalog.KindCore:
                    return analyze ? DestAnalysis : DestAnalysisWait;
                default:
                    return DestWarehouse;
            }
        }

        /// <summary>
        /// 远征结束的结算（在 <see cref="AwayReportService.End"/> 之前调用；没有进行中的报告时返回 null）。
        /// <paramref name="arriving"/> = 这次回来的存活成员（走回 / 登舱的；全灭时为空）。走回家园时它们携带的关键物在这里带回（<see cref="CargoService.DeliverQuestItem"/>）；
        /// 货舱里的货由随后的“远征卸货”规则卸下（结算按规则写去处）。机器经历：全员记“参加过远征”，回来的记“生还归来”、累计远征次数 +1。O(成员 + 货舱 + 关键物 + 地面物)，只在结算这一刻。
        /// </summary>
        public static AwayReportRecord Settle(CampaignState s, string via, IReadOnlyList<int> arriving)
        {
            AwayReportRecord r = AwayReportService.Current(s);
            if (r == null)
            {
                // 没有进行中的报告（异常旧档）：结算记录写不了，但机器经历照记（生还归来、累计远征次数），携带的关键物照常带回。
                foreach (int id in arriving ?? Array.Empty<int>())
                {
                    if (MachineRegistry.TryGetRecord(id, out MachineRecord m) && m != null && m.IsAlive)
                    {
                        MachineRegistry.TryMarkExperience(id, MachineExperienceFlags.Expedition);
                        MachineRegistry.TryMarkExperience(id, MachineExperienceFlags.Returned);
                        MachineRegistry.RecordExpeditionCompleted(id);
                        if (via != ViaPod)
                        {
                            foreach (RegionQuestItemRecord q in s?.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
                            {
                                if (q != null && q.State == RegionQuestItemState.Carried && q.CarrierLogicId == id)
                                {
                                    CargoService.DeliverQuestItem(s, q, m);
                                }
                            }
                        }
                    }
                }
                return null;
            }
            ExpeditionSettlementRecord st = r.Settlement ?? (r.Settlement = new ExpeditionSettlementRecord());
            st.Via = via ?? string.Empty;
            var back = new HashSet<int>();
            foreach (int id in arriving ?? Array.Empty<int>())
            {
                if (MachineRegistry.TryGetRecord(id, out MachineRecord m) && m != null && m.IsAlive)
                {
                    back.Add(id);
                }
            }
            // 修复第 3 轮（审查 P1）：分批接回、先随舱落地的成员也算回来了（全灭 / 最后一批登舱时它们已经在家）；它们的货与关键物落地时已记，下面第 1、2 步跳过。
            var landed = new HashSet<int>(st.Landed ?? Array.Empty<int>());
            foreach (int id in landed)
            {
                if (MachineRegistry.TryGetRecord(id, out MachineRecord m) && m != null && m.IsAlive)
                {
                    back.Add(id);
                }
            }
            // 修复第 4 轮（审查 P1）：分批接回、已登上回收舱但舱还在返程途中的成员（远征在它落地之前结束：其余成员全灭 / 走回了家）——
            // 也算回来了，它的货舱货与关键物记“随投送舱返回”（落地时按远征卸货规则卸 / 带回；那时报告已结束，不再重复记）。
            var riding = new HashSet<int>();
            foreach (int id in r.Members ?? Array.Empty<int>())
            {
                if (!back.Contains(id) && RidingRecallPod(s, id) && MachineRegistry.TryGetRecord(id, out MachineRecord m) && m != null && m.IsAlive)
                {
                    riding.Add(id);
                    back.Add(id);
                }
            }
            // 有成员随投送舱生还（已落地或在舱里）时，其余成员阵亡不算全灭：按“随投送舱返回”结算（阵亡成员的东西照常列进“留在原地”）。
            if (via == ViaWiped && back.Count > 0)
            {
                via = ViaPod;
                st.Via = via;
            }
            // 1. 货舱里的货（分拣 / 拾取的）：按“远征卸货”规则写去处。
            StandingRuleRecord unload = StandingRuleService.ActiveUnloadRule(s);
            bool analyze = unload != null && unload.SendToAnalysis;
            bool anyCargo = false;
            foreach (LootHeldRecord h in s.LootPackets?.Held ?? Array.Empty<LootHeldRecord>())
            {
                if (h == null || !back.Contains(h.Machine) || landed.Contains(h.Machine))
                {
                    continue;
                }
                bool byPod = via == ViaPod || riding.Contains(h.Machine);
                anyCargo |= !byPod;
                AppendItem(st, new SettleItemRecord
                {
                    Kind = KindCargo, CargoKind = h.Kind ?? string.Empty, ItemId = h.ItemId ?? string.Empty, TargetId = h.TargetId ?? string.Empty, Amount = Math.Max(1, h.Amount),
                    Dest = byPod ? DestPod : unload == null ? DestCargo : DestOfCargo(h.Kind, analyze),
                });
            }
            st.CargoKept = st.CargoKept || (anyCargo && unload == null); // 先落地那批的货留在货舱里时（OnMemberLanded）已经置上；随舱返回的不算
            // 2. 携带的关键物：走回家园 = 现在带回；登舱 = 随舱返回、落地时带回（记“随投送舱返回”）。
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (q == null || q.State != RegionQuestItemState.Carried || !back.Contains(q.CarrierLogicId) || landed.Contains(q.CarrierLogicId))
                {
                    continue;
                }
                if (via == ViaPod || riding.Contains(q.CarrierLogicId))
                {
                    AppendItem(st, QuestRecord(q, DestPod));
                }
                else if (MachineRegistry.TryGetRecord(q.CarrierLogicId, out MachineRecord carrier))
                {
                    CargoService.DeliverQuestItem(s, q, carrier);
                }
            }
            // 3. 数据包：带回的个数（回家后分拣）。
            int packets = 0;
            foreach (LootPackageRecord p in s.LootPackets?.Packets ?? Array.Empty<LootPackageRecord>())
            {
                packets += p != null && back.Contains(p.Holder) ? 1 : 0;
            }
            st.PacketsHome = packets;
            // 4. 三选一的选择记录（出发之后的）。
            var picks = new List<LootPickLogRecord>();
            // 修复第 2 轮（审查 P2）：只收这支远征队做的（数据包携带者是本次成员；旧档没有携带者时按“装进货舱”判断），在家分拣旧数据包的不算。
            var members = new HashSet<int>(r.Members ?? Array.Empty<int>());
            foreach (LootPickLogRecord p in s.LootPackets?.Picks ?? Array.Empty<LootPickLogRecord>())
            {
                if (p != null && p.Tick >= r.StartTick && (p.Holder > 0 ? members.Contains(p.Holder) : p.Destination == LootPickService.DestCargo))
                {
                    picks.Add(p);
                }
            }
            st.Picks = picks.ToArray();
            // 5. 任务进度：远征期间完成 / 失败的任务与 Demo 目标。
            var quests = new List<SettleQuestRecord>();
            foreach (ObjectiveRecord o in s.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
            {
                if (o == null || (o.State != ObjectiveState.Completed && o.State != ObjectiveState.Failed))
                {
                    continue;
                }
                bool inWindow = CampaignObjectiveTracker.IsQuestRecord(o)
                    ? o.EndedTick >= r.StartTick
                    : r.HasStart && o.State == ObjectiveState.Completed && o.CompletedAtPlaySeconds >= r.PlaySecondsAtStart && o.CompletedAtPlaySeconds > 0f;
                if (inWindow)
                {
                    quests.Add(new SettleQuestRecord { ObjectiveId = o.ObjectiveId ?? string.Empty, State = (int)o.State });
                }
            }
            st.Quests = quests.ToArray();
            // 6. 熔合线索（这次远征的场次生成的；修复第 2 轮（审查 P2）：远征在外期间家园突袭 / 在家模拟熔合产生的线索不算）。
            var clues = new List<int>();
            foreach (FusionClueRecord c in s.Research?.Fusion?.Clues ?? Array.Empty<FusionClueRecord>())
            {
                if (c != null && c.Tick >= r.StartTick && c.SessionKind == Combat.ReactionAttribution.KindExpedition && c.SiteId == r.RegionId)
                {
                    clues.Add(c.Serial);
                }
            }
            st.Clues = clues.ToArray();
            // 7. 暴露变化。
            st.HasExposure = r.HasStart;
            st.ExposureStart = r.ExposureAtStart;
            st.ExposureEnd = s.SignalExposure;
            // 8. 机器经历。
            var machines = new List<SettleMachineRecord>();
            foreach (int id in r.Members ?? Array.Empty<int>())
            {
                if (!MachineRegistry.TryGetRecord(id, out MachineRecord m) || m == null)
                {
                    continue;
                }
                MachineRegistry.TryMarkExperience(id, MachineExperienceFlags.Expedition);
                bool returned = back.Contains(id);
                if (returned)
                {
                    MachineRegistry.TryMarkExperience(id, MachineExperienceFlags.Returned);
                    MachineRegistry.RecordExpeditionCompleted(id);
                }
                machines.Add(new SettleMachineRecord
                {
                    LogicId = id, Alive = m.IsAlive, Expeditions = m.ExpeditionsCompleted, Injured = m.IsAlive && m.InjuryFlags != null && m.InjuryFlags.Length > 0,
                });
            }
            st.Machines = machines.ToArray();
            // 9. 留在原地的东西：远征地点里的 + 阵亡成员倒下的地方附近（星球上）。
            var left = new List<SettleLeftRecord>();
            int more = 0;
            // 远征地点里留下的东西只在这一趟进过地点时列（行军途中召回的没进过，不列目的地里的东西）。
            bool visited = ExpeditionMarchService.Current(s)?.VisitedSite == true || WorldSimulation.ActiveExpedition?.SiteId == r.RegionId;
            if (visited && !string.IsNullOrEmpty(r.RegionId) && r.RegionId != HomeValleyLayout.RegionId)
            {
                more += CollectLeftBehind(s, r.RegionId, null, 0f, left, int.MaxValue);
            }
            foreach (int id in r.Members ?? Array.Empty<int>())
            {
                if (MachineRegistry.TryGetRecord(id, out MachineRecord m) && m != null && !m.IsAlive && m.DeathTick >= r.StartTick
                    && (string.IsNullOrEmpty(m.DeathRegionId) || m.DeathRegionId == HomeValleyLayout.RegionId))
                {
                    more += CollectLeftBehind(s, HomeValleyLayout.RegionId, m.DeathPosition, LeaveRadius * 0.25f, left, int.MaxValue);
                }
            }
            Dedupe(left);
            Sort(left);
            int max = LeaveListMax;
            if (left.Count > max)
            {
                more += left.Count - max;
                left.RemoveRange(max, left.Count - max);
            }
            st.Left = left.ToArray();
            st.LeftMore = more;
            r.HasSettlement = true;
            SettleCount++;
            LastVia = st.Via;
            AwayReportService.Touch();
            Log.Info($"[ExpeditionSettlementService] 远征结算（{st.Via}）：带回 {st.Items.Length} 项、三选一 {st.Picks.Length} 次、任务 {st.Quests.Length} 条、线索 {st.Clues.Length} 条、留在原地 {st.Left.Length + st.LeftMore} 处。");
            return r;
        }

        // ─────────────────────────────── 留在原地的东西 ───────────────────────────────

        /// <summary>
        /// 收集 <paramref name="regionId"/> 里（<paramref name="center"/> 不为空时只算 <paramref name="radius"/> 格内）地上还没捡的东西：普通战利品（带回收倒计时；家园 / 带仓库的前哨站范围内的不算）、
        /// 区域关键物 / 黑匣子 / 遗产、地上的数据包（永不消失）。追加到 <paramref name="into"/>，返回超出 <paramref name="max"/> 没列的条数。O(地面物 + 关键物 + 数据包)。
        /// </summary>
        public static int CollectLeftBehind(CampaignState s, string regionId, Vector2? center, float radius, List<SettleLeftRecord> into, int max)
        {
            if (s == null || into == null || string.IsNullOrEmpty(regionId))
            {
                return 0;
            }
            int start = into.Count;
            float r2 = radius * radius;
            bool home = regionId == HomeValleyLayout.RegionId;
            foreach (GroundItemRecord g in s.GroundItems ?? Array.Empty<GroundItemRecord>())
            {
                if (g == null || g.Amount <= 0 || g.RegionId != regionId || (center.HasValue && (g.Position - center.Value).sqrMagnitude > r2))
                {
                    continue;
                }
                LootGroundTagRecord tag = FieldLootService.TagOf(s, g.SalvageInstanceId);
                string itemId = tag != null ? tag.ItemId : ItemCatalog.TryGetByResource(g.ResourceType, out ItemDef d) ? d.Id : g.ResourceType;
                // 家园所在星球上：只列战利品，家园物流自己的地面物不算。修复第 2 轮（审查 P1）：按来源判断，不按“有没有回收章”——
                // 关键材料按 D-13 不盖章（GroundLootExpiry.Stamp），但货舱掉落 / 野外掉落的关键材料恰恰是最要紧的一项。仓库 / 核心范围内的照旧不列。
                if (home && (LootPickService.IsDepotPosition(s, regionId, g.Position) || (g.ExpireTick <= 0 && !IsDroppedLoot(g.SalvageInstanceId))))
                {
                    continue;
                }
                into.Add(new SettleLeftRecord
                {
                    Kind = KeyItemRetention.NeverExpires(KeyItemRetention.OfItem(itemId)) ? KindKey : KindLoot,
                    ItemId = itemId ?? string.Empty, Amount = g.Amount, RegionId = regionId, X = g.Position.x, Y = g.Position.y, ExpireTick = g.ExpireTick,
                });
            }
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (q == null || q.State != RegionQuestItemState.OnGround || q.RegionId != regionId || (center.HasValue && (q.Position - center.Value).sqrMagnitude > r2))
                {
                    continue;
                }
                into.Add(new SettleLeftRecord
                {
                    Kind = Env.HeritageService.IsHeritage(q) ? KindHeritage : BlackBoxService.IsBlackBox(q) ? KindBox : KindKey,
                    ItemId = q.SalvageInstanceId ?? string.Empty, Amount = 1, RegionId = regionId, X = q.Position.x, Y = q.Position.y, ExpireTick = 0,
                });
            }
            foreach (LootPackageRecord p in s.LootPackets?.Packets ?? Array.Empty<LootPackageRecord>())
            {
                if (p == null || p.Holder != 0 || p.RegionId != regionId || (center.HasValue && (new Vector2(p.X, p.Y) - center.Value).sqrMagnitude > r2))
                {
                    continue;
                }
                into.Add(new SettleLeftRecord { Kind = KindPacket, ItemId = p.PacketId ?? string.Empty, Amount = 1, RegionId = regionId, X = p.X, Y = p.Y, ExpireTick = 0 });
            }
            int added = into.Count - start;
            if (added <= max)
            {
                return 0;
            }
            List<SettleLeftRecord> mine = into.GetRange(start, added);
            Sort(mine);
            into.RemoveRange(start, added);
            into.AddRange(mine.GetRange(0, max));
            return added - max;
        }

        /// <summary>这份地面物是战利品掉落（货舱阵亡掉落 / 丢弃，或野外掉落），不是家园物流自己的地面物。</summary>
        public static bool IsDroppedLoot(string salvageId) =>
            CargoService.IsCargoDrop(salvageId) || FieldLootService.Normalize(salvageId).StartsWith(FieldLootService.SalvagePrefix, StringComparison.Ordinal);

        // ─────────────────────────────── 卸货后回写“送到解析台”的实际结果 ───────────────────────────────

        /// <summary>
        /// 修复第 2 轮（审查 P1，FGR-EXP-050“带回的物品与去处”要与实际一致）：“远征卸货”勾了“送到解析台”时，结算先按开关写 analysis；
        /// 卸货那一步（<see cref="StandingRuleService"/> 的远征卸货，晚于结算一步）按 TryEnqueueItem 的真实结果回写：
        /// 排进去的确认为 analysis，没排进去的改成 analysis_wait 并记下原因（解析台没建 / 不能用 / 队列满）。一份货部分成功时拆成两条。
        /// 只改最近一份结算里、成员包含这批机器、还没确认过的 analysis 条目。O(结算条目)，只在卸货这一刻。
        /// </summary>
        public static void OnUnloadAnalyzed(CampaignState s, IReadOnlyList<int> machines, IReadOnlyDictionary<string, int> queued, string failReason)
        {
            AwayReportRecord r = FindSettlementFor(s, machines);
            if (r?.Settlement?.Items == null)
            {
                return;
            }
            var ok = new Dictionary<string, int>(StringComparer.Ordinal);
            if (queued != null)
            {
                foreach (KeyValuePair<string, int> kv in queued)
                {
                    ok[kv.Key] = kv.Value;
                }
            }
            var items = new List<SettleItemRecord>(r.Settlement.Items.Length);
            bool changed = false;
            foreach (SettleItemRecord it in r.Settlement.Items)
            {
                items.Add(it);
                if (it == null || it.Kind != KindCargo || it.Dest != DestAnalysis || it.Confirmed)
                {
                    continue;
                }
                changed = true;
                it.Confirmed = true;
                int have = ok.TryGetValue(it.ItemId ?? string.Empty, out int n) ? n : 0;
                int amount = Math.Max(1, it.Amount);
                int done = Math.Min(have, amount);
                ok[it.ItemId ?? string.Empty] = have - done;
                if (done >= amount)
                {
                    continue;
                }
                if (done == 0)
                {
                    it.Dest = DestAnalysisWait;
                    it.Reason = failReason ?? string.Empty;
                    continue;
                }
                it.Amount = done;
                items.Add(new SettleItemRecord
                {
                    Kind = it.Kind, CargoKind = it.CargoKind, ItemId = it.ItemId, TargetId = it.TargetId, Amount = amount - done, Dest = DestAnalysisWait,
                    Confirmed = true, Reason = failReason ?? string.Empty,
                });
            }
            if (changed)
            {
                r.Settlement.Items = items.ToArray();
                AwayReportService.Touch();
            }
        }

        /// <summary>最近一份（进行中的优先，否则刚结算的那份）带结算、成员包含 <paramref name="machines"/> 里任一台的报告。</summary>
        private static AwayReportRecord FindSettlementFor(CampaignState s, IReadOnlyList<int> machines)
        {
            if (s == null || machines == null || machines.Count == 0)
            {
                return null;
            }
            AwayReportRecord open = AwayReportService.Current(s);
            // 修复第 3 轮：分批接回、先落地那批的卸货发生在远征结束之前——进行中的报告里记了它们（Landed）时回写这一份，不去碰上一次远征的结算。
            bool openLanded = false;
            foreach (int id in machines)
            {
                openLanded |= open?.Settlement?.Landed != null && Array.IndexOf(open.Settlement.Landed, id) >= 0;
            }
            AwayReportRecord last = open != null && (open.HasSettlement || openLanded) ? open : null;
            if (last == null)
            {
                List<AwayReportRecord> recent = AwayReportService.Recent(s);
                last = recent.Count > 0 && recent[0].HasSettlement ? recent[0] : null;
            }
            if (last?.Members == null)
            {
                return null;
            }
            foreach (int id in machines)
            {
                if (Array.IndexOf(last.Members, id) >= 0)
                {
                    return last;
                }
            }
            return null;
        }

        private static void Dedupe(List<SettleLeftRecord> list)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                SettleLeftRecord e = list[i];
                string key = e.Kind + "|" + e.ItemId + "|" + e.RegionId + "|" + e.X.ToString("0.0", CultureInfo.InvariantCulture) + "|" + e.Y.ToString("0.0", CultureInfo.InvariantCulture);
                if (!seen.Add(key))
                {
                    list.RemoveAt(i);
                }
            }
        }

        /// <summary>排序：永不消失的关键物 / 黑匣子 / 遗产 / 数据包在前，普通战利品按“最快被回收”在前；同类按物品 ID、位置（确定性）。</summary>
        private static void Sort(List<SettleLeftRecord> list) => list.Sort((a, b) =>
        {
            bool ka = a.ExpireTick <= 0, kb = b.ExpireTick <= 0;
            if (ka != kb)
            {
                return ka ? -1 : 1;
            }
            if (a.ExpireTick != b.ExpireTick)
            {
                return a.ExpireTick.CompareTo(b.ExpireTick);
            }
            int c = string.CompareOrdinal(a.Kind, b.Kind);
            if (c != 0)
            {
                return c;
            }
            c = string.CompareOrdinal(a.ItemId, b.ItemId);
            if (c != 0)
            {
                return c;
            }
            return a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y);
        });

        /// <summary>留在原地的一条（当前语言）：“名字 ×n（地点 x, y）· 还剩 X 被敌方回收 / 永不消失”。</summary>
        public static string LeftText(CampaignState s, SettleLeftRecord e)
        {
            if (e == null)
            {
                return string.Empty;
            }
            return GameText.Format("settle.left.row", LeftName(s, e), BlackBoxService.PlaceName(e.RegionId), Mathf.RoundToInt(e.X), Mathf.RoundToInt(e.Y),
                GroundLootExpiry.CountdownText(e.ExpireTick));
        }

        public static string LeftName(CampaignState s, SettleLeftRecord e)
        {
            switch (e.Kind)
            {
                case KindPacket:
                    return GameText.Get("settle.name.packet");
                case KindBox:
                case KindHeritage:
                case KindKey when !ItemCatalog.TryGet(e.ItemId, out _):
                {
                    RegionQuestItemRecord q = FindQuest(s, e.ItemId);
                    return q != null ? QuestName(q) : GameText.Get("settle.name.key");
                }
                default:
                    return GameText.Format("settle.name.amount", ItemCatalog.NameOf(e.ItemId), e.Amount);
            }
        }

        private static RegionQuestItemRecord FindQuest(CampaignState s, string salvageId)
        {
            foreach (RegionQuestItemRecord q in s?.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (q != null && q.SalvageInstanceId == salvageId)
                {
                    return q;
                }
            }
            return null;
        }

        public static string QuestName(RegionQuestItemRecord q)
        {
            if (Env.HeritageService.IsHeritage(q))
            {
                return GameText.Format("heritage.cargo.name", Env.HeritageService.Name(Env.HeritageService.Find(Env.HeritageService.HeritageIdOf(q.SalvageInstanceId))));
            }
            if (BlackBoxService.IsBlackBox(q))
            {
                int id = BlackBoxService.LogicIdOf(q);
                return GameText.Format("cargo.blackbox", MachineRegistry.TryGetRecord(id, out MachineRecord dead) && dead != null ? MachineNaming.Short(dead) : "#" + id);
            }
            return Feedback.FeedbackCues.QuestItemName(q.ContentId);
        }

        /// <summary>
        /// 远征地点撤离 / 全灭面板“留在原地的东西”一栏（FGR-EXP-032“离开一片区域前，界面会列出地上还没捡的战利品”）：这片地点里地上还没捡的东西与倒计时，
        /// 每行一条，最多 <see cref="LeaveListMax"/> 条 + “还有 N 处”；什么都没有时写“地上没有留下东西”。
        /// </summary>
        public static string LeftListText(CampaignState s, string regionId)
        {
            var list = new List<SettleLeftRecord>();
            int more = CollectLeftBehind(s, regionId, null, 0f, list, LeaveListMax);
            if (list.Count == 0)
            {
                return GameText.Get("settle.site.left_none");
            }
            var sb = new System.Text.StringBuilder(GameText.Format("settle.site.left_title", list.Count + more));
            foreach (SettleLeftRecord e in list)
            {
                sb.Append('\n').Append(GameText.Format("settle.site.left_line", LeftText(s, e)));
            }
            if (more > 0)
            {
                sb.Append('\n').Append(GameText.Format("settle.left.more", more));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 星球上下“返回家园”之前：队伍附近（<see cref="LeaveRadius"/> 格内）地上还没捡的东西。有 → 返回 true，<paramref name="text"/> 是行军条上的提示
        /// （列前 <see cref="LeaveWarnLines"/> 条 + “还有 N 处” + “再点一次返回家园确认”）。没有在星球上的远征队 / 附近没东西 → false。
        /// </summary>
        public static bool LeaveWarning(CampaignState s, out string text, List<SettleLeftRecord> into = null)
        {
            text = null;
            if (!ExpeditionMarchService.TryTeamCenter(s, out Vector2 center, out _))
            {
                return false;
            }
            var list = into ?? new List<SettleLeftRecord>();
            list.Clear();
            int lines = LeaveWarnLines;
            int more = CollectLeftBehind(s, HomeValleyLayout.RegionId, center, LeaveRadius, list, lines);
            if (list.Count == 0)
            {
                return false;
            }
            var parts = new List<string>(list.Count);
            foreach (SettleLeftRecord e in list)
            {
                parts.Add(LeftText(s, e));
            }
            string body = string.Join(GameText.Get("settle.left.sep"), parts);
            text = more > 0 ? GameText.Format("settle.leave.warn_more", body, more) : GameText.Format("settle.leave.warn", body);
            return true;
        }
    }
}
