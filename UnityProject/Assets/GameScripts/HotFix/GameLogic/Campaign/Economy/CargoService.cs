using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>货舱里的一行（界面 / 自检用的只读快照）。</summary>
    public struct CargoLine
    {
        /// <summary>行 ID：分拣 / 拾取的货 = <see cref="LootHeldRecord.HeldId"/>；关键物 = "quest:" + 回收实例 ID；工单在途 = "job:" + 机器 + ":" + 序号。</summary>
        public string Id;
        /// <summary><see cref="CargoService.LineHeld"/> / <see cref="CargoService.LineQuest"/> / <see cref="CargoService.LineJob"/>。</summary>
        public string Kind;
        public int Machine;
        public string ItemId;
        public int Amount;
        public int Space;
        /// <summary>能不能转移 / 丢弃（工单在途的货跟着工单走，不能）。</summary>
        public bool Movable;
        public string Text;
    }

    /// <summary>
    /// FG8-LOOT-03 货舱、自动拾取、阵亡掉落（FG08 FGR-LOOT-030、FGR-EXP-032；FG13 FGU-35；验收 FGT-EXP-007）。
    /// <para>不另起一套格子 / 背包：空间规则与容量沿用 <see cref="LootPickCatalog.SpaceOf"/> / <see cref="LootPickCatalog.CapacityOf"/>（Demo 的
    /// <see cref="HomeValleyCargo.SlotsForCargo"/> 与出发检查单的容量也改读同一组函数）；货物记录沿用三处已有的持有者——
    /// 野外分拣 / 拾取的货 = <see cref="LootPacketState.Held"/>（FG8-LOOT-02 的货舱记录，不写会被施工搬运整体覆盖的 <see cref="MachineRecord.Cargo"/>），
    /// 携带的关键物 / 黑匣子 = <see cref="RegionQuestItemRecord"/> Carried，工单在途 = <see cref="MachineRecord.Cargo"/>（只读显示）；
    /// 拾取 / 落地沿用 <see cref="HomeValleyCargo"/> 的地面物。</para>
    /// - 货舱界面：全队总量、逐机明细（<see cref="PanelMachines"/> / <see cref="LinesOf"/>）。
    /// - 丢弃（<see cref="TryDiscard"/>）：落在机器旁边，可以再捡回；工单在途的不能丢。
    /// - 转移（<see cref="TryTransfer"/>）：只在战略暂停中、同一地点 loot.cargo.transfer_radius 格内的存活机器之间；目标放不下拒绝（材料可以拆开转能装下的部分）。
    /// - 自动拾取（<see cref="WorldStep"/>）：在外的远征队成员走到 loot.cargo.pickup_radius 格内，按物品开关把地上的货装进货舱（放得下才装，材料可以只捡一部分）；
    ///   关键材料、黑匣子始终拾取（数据包由 <see cref="LootPickService"/> 捡）；家园 / 带仓库的前哨站附近不自动拾取（那里由搬运单处理）。
    /// - 快满预警（占用 ≥ loot.cargo.warn_ratio）与已满通知（每台机器进入该状态时各一次，降下来再重置）；第一次满的引导钩子。
    /// - 阵亡掉落：货舱物品由 <see cref="LootPickService.OnMachineDied"/> 掉在原地（带身份）；在外的远征队成员在星球上阵亡时黑匣子也掉在原地（<see cref="BlackBoxService.OnMachineDied"/>），
    ///   走近的队友自动捡起，带回家园 / 带仓库的前哨站即回收（本类 <see cref="WorldStep"/>）。
    /// 只看步序号（tick 是间隔的整数倍时），与观察 / 倍速 / 读档无关；暂停时不推进。没有在外的远征队、也没有携带中的黑匣子时 O(1)。
    /// </summary>
    public static class CargoService
    {
        public const string NotifyType = "cargo";
        public const string LineHeld = "held";
        public const string LineQuest = "quest";
        public const string LineJob = "job";
        /// <summary>自动拾取 / 拆开转移生成的货舱记录前缀（"cargo:" + 序号）。</summary>
        public const string HeldPrefix = "cargo:";
        public const string QuestPrefix = "quest:";
        public const string JobPrefix = "job:";

        public static float CheckSeconds => Math.Max(0.1f, Tuning("loot.cargo.check_seconds", 1f));
        public static float PickupRadius => Math.Max(0.5f, Tuning("loot.cargo.pickup_radius", 6f));
        public static float TransferRadius => Math.Max(1f, Tuning("loot.cargo.transfer_radius", 30f));
        public static float WarnRatio => Mathf.Clamp(Tuning("loot.cargo.warn_ratio", 0.8f), 0.1f, 1f);
        public static float DiscardClearRadius => Math.Max(1f, Tuning("loot.cargo.discard_clear_radius", 12f));
        public static int DiscardMax => Math.Max(8, (int)Math.Round(Tuning("loot.cargo.discard_max", 64f)));
        public static float PerfMs => Math.Max(0.001f, Tuning("loot.cargo.perf_ms", 0.5f));

        public static int Revision => LootPickService.Revision;
        public static long StepChecks { get; private set; }
        public static double StepMaxMs { get; private set; }
        public static double StepTotalMs { get; private set; }

        private static readonly List<(int Id, string Region, Vector2 Pos)> Members = new List<(int, string, Vector2)>(16);
        private static readonly List<Vector2> Depots = new List<Vector2>(16);
        private static readonly List<int> Scratch = new List<int>(16);
        private static List<ItemDef> _autoItems;
        private static int _autoItemsKey = -1;
        /// <summary>“放不下这件（还有空余）”已经提示过的（机器 | 地面物）——每台机器、每份地面物只提示一次（纯提示去重，不进存档）。</summary>
        private static readonly HashSet<string> NoRoomNoted = new HashSet<string>(StringComparer.Ordinal);

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        public static void ResetSessionState()
        {
            StepChecks = 0;
            StepMaxMs = 0;
            StepTotalMs = 0;
            _autoItems = null;
            _autoItemsKey = -1;
            NoRoomNoted.Clear();
        }

        public static void ResetStepStats()
        {
            StepChecks = 0;
            StepMaxMs = 0;
            StepTotalMs = 0;
        }

        private static LootPacketState StateOf(CampaignState s) => LootPickService.StateOf(s);

        // ─────────────────────────────── 容量 / 占用 ───────────────────────────────

        public static int Capacity(CampaignState s, MachineRecord m) => LootPickCatalog.CapacityOf(s, m);

        public static int Used(CampaignState s, MachineRecord m) => LootPickService.UsedSpace(s, m);

        public static int Free(CampaignState s, MachineRecord m) => LootPickService.FreeSpace(s, m);

        /// <summary>0 = 正常、1 = 快满（≥ 预警比例）、2 = 已满（没有空余）。容量为 0 的机器算正常（它本来就不装货）。</summary>
        public static int Level(int used, int cap)
        {
            if (cap <= 0)
            {
                return 0;
            }
            if (used >= cap)
            {
                return 2;
            }
            return used >= WarnThreshold(cap) ? 1 : 0;
        }

        /// <summary>占用到多少算“快满”：容量 × 预警比例向上取整，但至少比容量少 1 格（容量 4 的战斗履带 3 / 4 就预警，不会和“已满”重合）。</summary>
        public static int WarnThreshold(int cap) => cap <= 1 ? Math.Max(1, cap) : Math.Max(1, Math.Min(cap - 1, Mathf.CeilToInt(cap * WarnRatio)));

        public static string LevelSuffix(int level) => level == 2 ? GameText.Get("cargo.state.full") : level == 1 ? GameText.Get("cargo.state.warn") : string.Empty;

        private static bool Alive(int id, out MachineRecord m) => MachineRegistry.TryGetRecord(id, out m) && m != null && m.IsAlive;

        /// <summary>货舱界面列出的机器：在外的远征队里还活着的成员 + 货舱里有分拣 / 拾取的货或携带关键物的存活机器（LogicId 升序）。O(成员数 + 货舱记录数 + 关键物条数)。</summary>
        public static void PanelMachines(CampaignState s, List<int> into)
        {
            into.Clear();
            if (s == null)
            {
                return;
            }
            ExpeditionMarchState march = ExpeditionMarchService.Current(s);
            if (march?.Members != null)
            {
                foreach (int id in march.Members)
                {
                    if (ExpeditionMarchService.IsTeamMember(s, id) && Alive(id, out _) && !into.Contains(id))
                    {
                        into.Add(id);
                    }
                }
            }
            foreach (LootHeldRecord h in StateOf(s)?.Held ?? Array.Empty<LootHeldRecord>())
            {
                if (h != null && Alive(h.Machine, out _) && !into.Contains(h.Machine))
                {
                    into.Add(h.Machine);
                }
            }
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (q != null && q.State == RegionQuestItemState.Carried && Alive(q.CarrierLogicId, out _) && !into.Contains(q.CarrierLogicId))
                {
                    into.Add(q.CarrierLogicId);
                }
            }
            into.Sort();
        }

        /// <summary>是否有在外的远征队（星球上行军或在远征地点里）。</summary>
        public static bool TeamOut(CampaignState s)
        {
            ExpeditionMarchState m = ExpeditionMarchService.Current(s);
            return m?.Members != null && m.Members.Length > 0
                   && (m.Phase == ExpeditionMarchService.PhaseOutbound || m.Phase == ExpeditionMarchService.PhaseReturning || m.Phase == ExpeditionMarchService.PhaseAtSite);
        }

        /// <summary>一组机器的总占用 / 总容量，返回其中最严重的状态（0 / 1 / 2）。</summary>
        public static int Totals(CampaignState s, IReadOnlyList<int> machines, out int used, out int cap)
        {
            used = 0;
            cap = 0;
            int worst = 0;
            foreach (int id in machines)
            {
                if (!Alive(id, out MachineRecord m))
                {
                    continue;
                }
                int u = Used(s, m);
                int c = Capacity(s, m);
                used += u;
                cap += c;
                worst = Math.Max(worst, Level(u, c));
            }
            return worst;
        }

        /// <summary>一台机器货舱里的每一行（分拣 / 拾取的货、携带的关键物、工单在途的货）。</summary>
        public static void LinesOf(CampaignState s, int machine, List<CargoLine> into)
        {
            into.Clear();
            if (s == null || !MachineRegistry.TryGetRecord(machine, out MachineRecord m) || m == null)
            {
                return;
            }
            LootHeldRecord[] held = StateOf(s)?.Held ?? Array.Empty<LootHeldRecord>();
            for (int i = 0; i < held.Length; i++)
            {
                LootHeldRecord h = held[i];
                if (h == null || h.Machine != machine)
                {
                    continue;
                }
                // 同机同种材料按总件数取整（与 UsedSpace 一致）：每行显示它在这一组里多占的格数，各行加起来 = 机器标题行的占用。
                int space = h.Space;
                if (LootPickCatalog.IsBulk(h.Kind, h.ItemId))
                {
                    int before = Math.Max(0, LootPickService.BulkAmountBefore(held, i));
                    space = LootPickCatalog.SpaceOf(h.ItemId, before + h.Amount) - LootPickCatalog.SpaceOf(h.ItemId, before);
                }
                string name = LootPickService.HeldNameOf(h);
                into.Add(new CargoLine
                {
                    Id = h.HeldId, Kind = LineHeld, Machine = machine, ItemId = h.ItemId, Amount = h.Amount, Space = space, Movable = true,
                    Text = GameText.Format("cargo.line", name, space),
                });
            }
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (q == null || q.State != RegionQuestItemState.Carried || q.CarrierLogicId != machine)
                {
                    continue;
                }
                int space = LootPickCatalog.SpaceOf(q.ContentId, 1);
                into.Add(new CargoLine
                {
                    Id = QuestPrefix + q.SalvageInstanceId, Kind = LineQuest, Machine = machine, ItemId = q.ContentId, Amount = 1, Space = space, Movable = true,
                    Text = GameText.Format("cargo.line.quest", QuestName(q), space),
                });
            }
            for (int i = 0; i < (m.Cargo?.Length ?? 0); i++)
            {
                CargoEntry c = m.Cargo[i];
                if (c == null || c.Amount <= 0)
                {
                    continue;
                }
                string itemId = ItemCatalog.TryGetByResource(c.ResourceType, out ItemDef d) ? d.Id : c.ResourceType;
                int space = LootPickCatalog.SpaceOf(itemId, c.Amount);
                into.Add(new CargoLine
                {
                    Id = JobPrefix + machine.ToString(CultureInfo.InvariantCulture) + ":" + i.ToString(CultureInfo.InvariantCulture), Kind = LineJob, Machine = machine,
                    ItemId = itemId, Amount = c.Amount, Space = space, Movable = false,
                    Text = GameText.Format("cargo.line.job", d != null ? d.Name : c.ResourceType, c.Amount, space),
                });
            }
        }

        private static string QuestName(RegionQuestItemRecord q)
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

        /// <summary>机器的一行标题：“名字：已用 / 容量（状态）”。</summary>
        public static string MachineText(CampaignState s, int machine)
        {
            if (!MachineRegistry.TryGetRecord(machine, out MachineRecord m) || m == null)
            {
                return string.Empty;
            }
            int u = Used(s, m);
            int c = Capacity(s, m);
            return GameText.Format("cargo.machine", MachineNaming.Short(m), u, c, LevelSuffix(Level(u, c)));
        }

        // ─────────────────────────────── 转移 / 丢弃 ───────────────────────────────

        /// <summary>转移的候选目标：与 <paramref name="from"/> 同一地点、<see cref="TransferRadius"/> 格内、存活、不在工厂里的机器（不含自己；LogicId 升序）。</summary>
        public static void TransferTargets(CampaignState s, int from, List<int> into)
        {
            into.Clear();
            if (!Alive(from, out MachineRecord src))
            {
                return;
            }
            string region = LootPickService.RegionOfMachine(src);
            Vector2 at = LootPickService.PositionOfMachine(src);
            float r2 = TransferRadius * TransferRadius;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.IsInFactory || m.LogicId == from || LootPickService.RegionOfMachine(m) != region)
                {
                    continue;
                }
                if ((LootPickService.PositionOfMachine(m) - at).sqrMagnitude <= r2)
                {
                    into.Add(m.LogicId);
                }
            }
            into.Sort();
        }

        /// <summary><paramref name="from"/> 能不能把货转给 <paramref name="to"/>（都存活、目标不在工厂里、同一地点、<see cref="TransferRadius"/> 格内）；货舱面板按它决定每一行“转给目标”能不能点。</summary>
        public static bool InTransferRange(int from, int to)
        {
            if (from == to || !Alive(from, out MachineRecord a) || !Alive(to, out MachineRecord b) || b.IsInFactory)
            {
                return false;
            }
            return LootPickService.RegionOfMachine(a) == LootPickService.RegionOfMachine(b)
                   && (LootPickService.PositionOfMachine(a) - LootPickService.PositionOfMachine(b)).sqrMagnitude <= TransferRadius * TransferRadius;
        }

        private static LootHeldRecord FindHeld(LootPacketState st, string id)
        {
            foreach (LootHeldRecord h in st?.Held ?? Array.Empty<LootHeldRecord>())
            {
                if (h != null && h.HeldId == id)
                {
                    return h;
                }
            }
            return null;
        }

        private static RegionQuestItemRecord FindQuest(CampaignState s, string lineId)
        {
            if (lineId == null || !lineId.StartsWith(QuestPrefix, StringComparison.Ordinal))
            {
                return null;
            }
            string sid = lineId.Substring(QuestPrefix.Length);
            foreach (RegionQuestItemRecord q in s?.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (q != null && q.SalvageInstanceId == sid && q.State == RegionQuestItemState.Carried)
                {
                    return q;
                }
            }
            return null;
        }

        private static string NextHeldId(LootPacketState st)
        {
            long n = Math.Max(1L, st.NextCargoSerial);
            st.NextCargoSerial = n + 1;
            return HeldPrefix + n.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把货舱里的一行转到另一台机器（FGR-LOOT-030“在战略暂停中，可以在机器之间转移物品”）。拒绝时什么都不改，<paramref name="message"/> 写原因：
        /// 没暂停、工单在途、同一台、目标不在 / 不同地点 / 太远、目标放不下（负向：转移时目标机器也满）。材料目标放不下全部时只转能装下的部分（至少 1 格空余）。
        /// </summary>
        public static bool TryTransfer(CampaignState s, string lineId, int to, out string message)
        {
            message = null;
            LootPacketState st = StateOf(s);
            if (st == null || string.IsNullOrEmpty(lineId))
            {
                message = GameText.Get("cargo.reason.gone");
                return false;
            }
            if (lineId.StartsWith(JobPrefix, StringComparison.Ordinal))
            {
                message = GameText.Get("cargo.reason.job");
                return false;
            }
            LootHeldRecord h = FindHeld(st, lineId);
            RegionQuestItemRecord q = h == null ? FindQuest(s, lineId) : null;
            int from = h?.Machine ?? q?.CarrierLogicId ?? 0;
            if ((h == null && q == null) || !Alive(from, out MachineRecord src))
            {
                message = GameText.Get("cargo.reason.gone");
                return false;
            }
            if (!GameClock.Paused)
            {
                message = GameText.Get("cargo.reason.pause");
                return false;
            }
            if (to == from)
            {
                message = GameText.Get("cargo.reason.same");
                return false;
            }
            if (!Alive(to, out MachineRecord dst) || dst.IsInFactory)
            {
                message = GameText.Get("cargo.reason.target_dead");
                return false;
            }
            float dist = Vector2.Distance(LootPickService.PositionOfMachine(src), LootPickService.PositionOfMachine(dst));
            if (LootPickService.RegionOfMachine(src) != LootPickService.RegionOfMachine(dst) || dist > TransferRadius)
            {
                message = GameText.Format("cargo.reason.far", MachineNaming.Short(dst), MachineNaming.Short(src), Mathf.RoundToInt(dist), Mathf.RoundToInt(TransferRadius));
                return false;
            }
            int free = Free(s, dst);
            if (q != null)
            {
                int space = LootPickCatalog.SpaceOf(q.ContentId, 1);
                if (space > free)
                {
                    message = GameText.Format("cargo.reason.full", MachineNaming.Short(dst), space, free);
                    NoteFull(s, st, dst); // 只在目标真满（没有空余）时记“已满”；还有空余只是放不下这件，原因已写在 message 里
                    return false;
                }
                q.CarrierLogicId = to;
                Done(st, ref message, GameText.Format("cargo.transferred", QuestName(q), MachineNaming.Short(src), MachineNaming.Short(dst)));
                return true;
            }
            bool bulk = LootPickCatalog.IsBulk(h.Kind, h.ItemId);
            // 材料：能装多少 = 目标同种材料最后一格的余量 + 空余格 × 每格件数（同机同种合并成一条，按总件数占空间）。
            int room = bulk ? BulkRoom(s, dst, h.ItemId) : free;
            if (bulk ? h.Amount <= room : h.Space <= free)
            {
                string moved = LootPickService.HeldNameOf(h);
                h.Machine = to;
                MergeInto(st, h);
                Done(st, ref message, GameText.Format("cargo.transferred", moved, MachineNaming.Short(src), MachineNaming.Short(dst)));
                return true;
            }
            int k = bulk ? Math.Min(h.Amount - 1, room) : 0;
            if (k <= 0)
            {
                message = GameText.Format("cargo.reason.full", MachineNaming.Short(dst), h.Space, free);
                NoteFull(s, st, dst);
                return false;
            }
            int total = h.Amount;
            h.Amount -= k;
            h.Space = LootPickCatalog.SpaceOf(h.ItemId, h.Amount);
            var part = new LootHeldRecord
            {
                HeldId = NextHeldId(st), Machine = to, Kind = h.Kind, ItemId = h.ItemId, TargetId = h.TargetId, Amount = k,
                Space = LootPickCatalog.SpaceOf(h.ItemId, k), Faction = h.Faction,
            };
            MergeOrAppend(st, part);
            Done(st, ref message, GameText.Format("cargo.transferred_part", ItemCatalog.NameOf(h.ItemId), k, total, MachineNaming.Short(dst)));
            return true;
        }

        private static void Done(LootPacketState st, ref string message, string text)
        {
            message = text;
            st.CargoTransfers++;
            LootPickService.Touch();
            GuidanceHooks.Raise(GuidanceHooks.LootFirstCargoTransfer);
        }

        private static void AppendHeld(LootPacketState st, LootHeldRecord h)
        {
            var list = new LootHeldRecord[st.Held.Length + 1];
            Array.Copy(st.Held, list, st.Held.Length);
            list[st.Held.Length] = h;
            st.Held = list;
        }

        /// <summary>同一台机器上已有的同种材料记录（同 ItemId / TargetId，<see cref="LootPickCatalog.IsBulk"/>）；没有返回 null。<paramref name="except"/> 不算。</summary>
        private static LootHeldRecord FindBulk(LootPacketState st, int machine, string itemId, string targetId, LootHeldRecord except)
        {
            foreach (LootHeldRecord x in st?.Held ?? Array.Empty<LootHeldRecord>())
            {
                if (x != null && x != except && x.Machine == machine && x.ItemId == itemId && (x.TargetId ?? string.Empty) == (targetId ?? string.Empty)
                    && LootPickCatalog.IsBulk(x.Kind, x.ItemId))
                {
                    return x;
                }
            }
            return null;
        }

        /// <summary>新的一份货进货舱：材料并入同机同种的已有记录（件数相加、重算占用），否则新起一条。返回装进去的那条记录。</summary>
        private static LootHeldRecord MergeOrAppend(LootPacketState st, LootHeldRecord h)
        {
            LootHeldRecord into = LootPickCatalog.IsBulk(h.Kind, h.ItemId) ? FindBulk(st, h.Machine, h.ItemId, h.TargetId, null) : null;
            if (into == null)
            {
                AppendHeld(st, h);
                return h;
            }
            into.Amount += h.Amount;
            into.Space = LootPickCatalog.SpaceOf(into.ItemId, into.Amount);
            return into;
        }

        /// <summary>已经在 st.Held 里的 <paramref name="h"/> 换了机器之后：目标机器上有同种材料就并过去，并把 <paramref name="h"/> 移出列表。</summary>
        private static void MergeInto(LootPacketState st, LootHeldRecord h)
        {
            LootHeldRecord into = LootPickCatalog.IsBulk(h.Kind, h.ItemId) ? FindBulk(st, h.Machine, h.ItemId, h.TargetId, h) : null;
            if (into == null)
            {
                return;
            }
            into.Amount += h.Amount;
            into.Space = LootPickCatalog.SpaceOf(into.ItemId, into.Amount);
            var list = new List<LootHeldRecord>(st.Held.Length);
            foreach (LootHeldRecord x in st.Held)
            {
                if (x != h)
                {
                    list.Add(x);
                }
            }
            st.Held = list.ToArray();
        }

        /// <summary>这台机器还能装多少件某种材料：同种材料最后一格的余量 + 空余格 × 每格件数。</summary>
        public static int BulkRoom(CampaignState s, MachineRecord m, string itemId)
        {
            if (m == null)
            {
                return 0;
            }
            int per = LootPickCatalog.MaterialPerSpace;
            int have = LootPickService.HeldBulkAmount(s, m.LogicId, itemId);
            int slack = LootPickCatalog.SpaceOf(itemId, have) * per - have;
            return Math.Max(0, Free(s, m)) * per + Math.Max(0, slack);
        }

        /// <summary>丢弃货舱里的一行：落在携带机器旁边（不会凭空消失；关键物 / 黑匣子照样永不消失），这件不会马上被自动拾取捡回去（机器走开再回来才会）。工单在途的不能丢。</summary>
        public static bool TryDiscard(CampaignState s, string lineId, out string message)
        {
            message = null;
            LootPacketState st = StateOf(s);
            if (st == null || string.IsNullOrEmpty(lineId))
            {
                message = GameText.Get("cargo.reason.gone");
                return false;
            }
            if (lineId.StartsWith(JobPrefix, StringComparison.Ordinal))
            {
                message = GameText.Get("cargo.reason.job");
                return false;
            }
            LootHeldRecord h = FindHeld(st, lineId);
            if (h != null)
            {
                if (!LootPickService.TryDiscard(s, lineId, out message))
                {
                    message = message ?? GameText.Get("cargo.reason.gone");
                    return false;
                }
                MarkDiscarded(st, lineId);
                st.CargoDiscards++;
                return true;
            }
            RegionQuestItemRecord q = FindQuest(s, lineId);
            if (q == null || !MachineRegistry.TryGetRecord(q.CarrierLogicId, out MachineRecord m) || m == null)
            {
                message = GameText.Get("cargo.reason.gone");
                return false;
            }
            q.State = RegionQuestItemState.OnGround;
            q.CarrierLogicId = 0;
            q.RegionId = LootPickService.RegionOfMachine(m);
            q.Position = LootPickService.PositionOfMachine(m);
            MarkDiscarded(st, q.SalvageInstanceId);
            st.CargoDiscards++;
            BlackBoxService.Touch();
            LootPickService.Touch();
            message = GameText.Format("cargo.discarded", QuestName(q), MachineNaming.Short(m));
            return true;
        }

        private static void MarkDiscarded(LootPacketState st, string salvageId)
        {
            var list = new List<string>(st.CargoDiscarded ?? Array.Empty<string>());
            if (list.Contains(salvageId))
            {
                return;
            }
            list.Add(salvageId);
            while (list.Count > DiscardMax)
            {
                list.RemoveAt(0);
            }
            st.CargoDiscarded = list.ToArray();
        }

        private static bool IsDiscarded(LootPacketState st, string salvageId)
        {
            string[] d = st.CargoDiscarded;
            if (d == null || d.Length == 0 || salvageId == null)
            {
                return false;
            }
            string sid = salvageId.StartsWith("ground:", StringComparison.Ordinal) ? salvageId.Substring(7) : salvageId;
            return Array.IndexOf(d, sid) >= 0 || Array.IndexOf(d, salvageId) >= 0;
        }

        // ─────────────────────────────── 自动拾取开关 ───────────────────────────────

        /// <summary>
        /// 设置里可以开关的物品（FGR-LOOT-030“按材料种类开关自动拾取”）：会作为普通掉落落在地上的物品（fg.TbLootDrop 的物品 + 三选一的普通物品），
        /// 关键材料不在其中（始终拾取，D-13）。按物品表排序。只在物品表 / 掉落表换了时重建。
        /// </summary>
        public static IReadOnlyList<ItemDef> AutoPickupItems()
        {
            int key = HashCode.Combine(ItemCatalog.Revision, LootCatalog.Revision, LootPickCatalog.Revision);
            if (_autoItems != null && key == _autoItemsKey)
            {
                return _autoItems;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<ItemDef>();
            void Add(ItemDef d)
            {
                if (d != null && seen.Add(d.Id) && KeyItemRetention.OfItem(d.Id) == KeyItemRetention.Kind.Ordinary)
                {
                    list.Add(d);
                }
            }
            foreach (LootDropDef r in LootCatalog.Rows)
            {
                Add(r.Item);
            }
            foreach (LootPickDef r in LootPickCatalog.Rows)
            {
                Add(r.Item);
            }
            list.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
            _autoItems = list;
            _autoItemsKey = key;
            return _autoItems;
        }

        /// <summary>这种物品开着自动拾取吗（默认开；关键材料始终开）。</summary>
        public static bool IsAutoPickup(CampaignState s, string itemId)
        {
            if (string.IsNullOrEmpty(itemId) || KeyItemRetention.OfItem(itemId) != KeyItemRetention.Kind.Ordinary)
            {
                return true;
            }
            string[] off = StateOf(s)?.AutoPickupOff;
            return off == null || Array.IndexOf(off, itemId) < 0;
        }

        /// <summary>设置里的开关（进存档：自动拾取影响世界结果，读档 / 换机器后一致）。关键材料不能关。返回是否改了。</summary>
        public static bool SetAutoPickup(CampaignState s, string itemId, bool on)
        {
            LootPacketState st = StateOf(s);
            if (st == null || string.IsNullOrEmpty(itemId) || KeyItemRetention.OfItem(itemId) != KeyItemRetention.Kind.Ordinary || IsAutoPickup(s, itemId) == on)
            {
                return false;
            }
            var list = new List<string>(st.AutoPickupOff ?? Array.Empty<string>());
            if (on)
            {
                list.Remove(itemId);
            }
            else
            {
                list.Add(itemId);
                list.Sort(StringComparer.Ordinal);
            }
            st.AutoPickupOff = list.ToArray();
            LootPickService.Touch();
            return true;
        }

        public static void ResetAutoPickup(CampaignState s)
        {
            LootPacketState st = StateOf(s);
            if (st != null && (st.AutoPickupOff?.Length ?? 0) > 0)
            {
                st.AutoPickupOff = Array.Empty<string>();
                LootPickService.Touch();
            }
        }

        public static int AutoPickupOnCount(CampaignState s)
        {
            int n = 0;
            foreach (ItemDef d in AutoPickupItems())
            {
                n += IsAutoPickup(s, d.Id) ? 1 : 0;
            }
            return n;
        }

        // ─────────────────────────────── 世界步：自动拾取 / 黑匣子回收 / 预警 ───────────────────────────────

        /// <summary>
        /// 每 loot.cargo.check_seconds 游戏秒（tick 是间隔的整数倍时）：
        /// 1. 携带中的黑匣子：携带者在家园 / 带仓库的前哨站 → 回收进陈列馆队列；地上的黑匣子在家园范围内 → 直接回收；
        /// 2. 自动拾取：在外的远征队成员（不在家园范围内）走近地上的货 → 按开关装进货舱；
        /// 3. 丢弃标记：附近已经没有队员的丢弃物，恢复可以自动拾取；
        /// 4. 快满 / 已满：队员货舱进入该状态时通知一次。
        /// O(队员数 × 家园星球地面物数 + 关键物条数)；没有在外的远征队、也没有携带中 / 家园星球地上的黑匣子时 O(1)。
        /// </summary>
        public static void WorldStep(CampaignState s, long tick, int stepHz)
        {
            LootPacketState st = s?.LootPackets;
            if (st == null)
            {
                return;
            }
            long every = Math.Max(1L, (long)Math.Round(CheckSeconds * Math.Max(1, stepHz)));
            if (tick % every != 0)
            {
                return;
            }
            bool team = TeamOut(s);
            bool boxes = AnyPlanetBox(s);
            if (!team && !boxes && (st.CargoWarned?.Length ?? 0) == 0)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            LootPacketState state = StateOf(s);
            LootPickService.CollectDepotPositions(s, Depots);
            bool changed = false;
            if (boxes)
            {
                changed |= RecoverBoxes(s);
            }
            Members.Clear();
            if (team)
            {
                ExpeditionMarchState march = ExpeditionMarchService.Current(s);
                foreach (int id in march.Members)
                {
                    if (Alive(id, out MachineRecord m) && !m.IsInFactory)
                    {
                        Members.Add((id, LootPickService.RegionOfMachine(m), LootPickService.PositionOfMachine(m)));
                    }
                }
                Members.Sort((a, b) => a.Id.CompareTo(b.Id));
                changed |= ClearDiscardMarks(s, state);
                changed |= AutoPickup(s, state);
            }
            changed |= CheckLevels(s, state);
            Members.Clear();
            Depots.Clear();
            if (changed)
            {
                LootPickService.Touch();
            }
            StepChecks++;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            StepTotalMs += ms;
            StepMaxMs = Math.Max(StepMaxMs, ms);
        }

        private static bool InDepot(string region, Vector2 at)
        {
            if (region != HomeValleyLayout.RegionId)
            {
                return false;
            }
            float r2 = LootPickCatalog.DepotRadius * LootPickCatalog.DepotRadius;
            foreach (Vector2 d in Depots)
            {
                if ((d - at).sqrMagnitude <= r2)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool AnyPlanetBox(CampaignState s)
        {
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                // FG8-EXP-04：人类遗产与黑匣子同一套（带回家园即入库、地上的远征队员走近自动捡起）。
                if (q != null && (BlackBoxService.IsBlackBox(q) || Env.HeritageService.IsHeritage(q)) && (q.State == RegionQuestItemState.Carried || (q.State == RegionQuestItemState.OnGround && q.RegionId == HomeValleyLayout.RegionId)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>黑匣子回到家园：携带者在家园 / 带仓库的前哨站（或地上的就在那里）→ 标 Recovered、进陈列馆队列。</summary>
        private static bool RecoverBoxes(CampaignState s)
        {
            bool changed = false;
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                bool heritage = Env.HeritageService.IsHeritage(q);
                if (q == null || (!BlackBoxService.IsBlackBox(q) && !heritage))
                {
                    continue;
                }
                bool home = false;
                string who = null;
                if (q.State == RegionQuestItemState.Carried && Alive(q.CarrierLogicId, out MachineRecord c) && !c.IsInFactory)
                {
                    home = InDepot(LootPickService.RegionOfMachine(c), LootPickService.PositionOfMachine(c));
                    who = MachineNaming.Short(c);
                }
                else if (q.State == RegionQuestItemState.OnGround && q.RegionId == HomeValleyLayout.RegionId)
                {
                    home = InDepot(q.RegionId, q.Position);
                }
                if (!home)
                {
                    continue;
                }
                q.State = RegionQuestItemState.Recovered;
                q.CarrierLogicId = 0;
                if (heritage)
                {
                    // FG8-EXP-04：遗产带回核心 / 仓库附近 → 存入核心保管库并按类别生效（通知由遗产服务发）。
                    Env.HeritageService.OnDelivered(s, q);
                    changed = true;
                    continue;
                }
                BlackBoxService.OnRecovered(s, q);
                int id = BlackBoxService.LogicIdOf(q);
                string box = GameText.Format("cargo.blackbox", MachineRegistry.TryGetRecord(id, out MachineRecord dead) && dead != null ? MachineNaming.Short(dead) : "#" + id);
                NotificationCenter.Post(NotifyType, who != null ? GameText.Format("cargo.bb.recovered", who, box) : GameText.Format("cargo.bb.recovered_ground", box));
                changed = true;
            }
            return changed;
        }

        private static bool ClearDiscardMarks(CampaignState s, LootPacketState st)
        {
            string[] d = st.CargoDiscarded;
            if (d == null || d.Length == 0)
            {
                return false;
            }
            var keep = new List<string>(d.Length);
            float r2 = DiscardClearRadius * DiscardClearRadius;
            foreach (string sid in d)
            {
                bool where = TryGroundPosition(s, sid, out string region, out Vector2 at);
                bool near = false;
                if (where)
                {
                    foreach ((int _, string mr, Vector2 pos) in Members)
                    {
                        if (mr == region && (pos - at).sqrMagnitude <= r2)
                        {
                            near = true;
                            break;
                        }
                    }
                }
                if (near)
                {
                    keep.Add(sid);
                }
            }
            if (keep.Count == d.Length)
            {
                return false;
            }
            st.CargoDiscarded = keep.ToArray();
            return true;
        }

        /// <summary>丢弃物现在在地上的位置（地面物或地上的关键物）；不在地上（已被捡走 / 搬走）返回 false。</summary>
        private static bool TryGroundPosition(CampaignState s, string sid, out string region, out Vector2 at)
        {
            GroundItemRecord g = HomeValleyCargo.FindGroundItemBySalvageId(s, sid);
            if (g != null)
            {
                region = g.RegionId;
                at = g.Position;
                return true;
            }
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (q != null && q.SalvageInstanceId == sid && q.State == RegionQuestItemState.OnGround)
                {
                    region = q.RegionId;
                    at = q.Position;
                    return true;
                }
            }
            region = null;
            at = default;
            return false;
        }

        /// <summary>离 <paramref name="at"/> 最近、在拾取半径内、放得下 <paramref name="space"/>（材料：至少 1 格空余）的队员；没有返回 0。<paramref name="anyNear"/> = 有队员在半径内（不论放不放得下）。</summary>
        private static int Picker(CampaignState s, string region, Vector2 at, string itemId, int space, bool bulk, out bool anyNear, out int nearFull, out int nearNoRoom)
        {
            anyNear = false;
            nearFull = 0;
            nearNoRoom = 0;
            int best = 0;
            float bestD = PickupRadius * PickupRadius;
            foreach ((int id, string mr, Vector2 pos) in Members)
            {
                if (mr != region)
                {
                    continue;
                }
                float d = (pos - at).sqrMagnitude;
                if (d > PickupRadius * PickupRadius || InDepot(mr, pos))
                {
                    continue;
                }
                anyNear = true;
                if (!Alive(id, out MachineRecord m))
                {
                    continue;
                }
                int free = Free(s, m);
                if (bulk ? BulkRoom(s, m, itemId) <= 0 : free <= 0)
                {
                    nearFull = nearFull > 0 ? nearFull : id; // 真满（没有空余）
                    continue;
                }
                if (!bulk && free < space)
                {
                    nearNoRoom = nearNoRoom > 0 ? nearNoRoom : id; // 还有空余，只是放不下这一件
                    continue;
                }
                if (best == 0 || d < bestD - 1e-4f || (Math.Abs(d - bestD) <= 1e-4f && id < best))
                {
                    best = id;
                    bestD = d;
                }
            }
            return best;
        }

        private static bool AutoPickup(CampaignState s, LootPacketState st)
        {
            if (Members.Count == 0)
            {
                return false;
            }
            bool changed = false;
            // 地上的货（家园所在星球上的地面物；远征地点里的沿用 Demo 的“装载”交互，不在这里自动捡）。
            GroundItemRecord[] ground = s.GroundItems ?? Array.Empty<GroundItemRecord>();
            List<string> removed = null;
            foreach (GroundItemRecord g in ground)
            {
                // 先做最便宜的判定（地点 + 离队员的距离，O(队员数)、不分配），绝大多数远处的地面物在这里就跳过；身份查询 / 开关只对走近的做。
                if (g == null || g.Amount <= 0 || g.RegionId != HomeValleyLayout.RegionId || !NearAnyMember(g.RegionId, g.Position)
                    || InDepot(g.RegionId, g.Position) || IsDiscarded(st, g.SalvageInstanceId))
                {
                    continue;
                }
                if (!ItemCatalog.TryGetByResource(g.ResourceType, out ItemDef item))
                {
                    continue;
                }
                LootGroundTagRecord tag = FieldLootService.TagOf(s, g.SalvageInstanceId);
                string kind = KindOf(tag);
                string itemId = tag != null ? tag.ItemId : item.Id;
                if (!IsAutoPickup(s, itemId))
                {
                    continue;
                }
                bool bulk = LootPickCatalog.IsBulk(kind, itemId);
                int space = LootPickCatalog.SpaceOf(itemId, g.Amount);
                int by = Picker(s, g.RegionId, g.Position, itemId, space, bulk, out bool anyNear, out int nearFull, out int nearNoRoom);
                if (by <= 0)
                {
                    if (nearFull > 0 && MachineRegistry.TryGetRecord(nearFull, out MachineRecord full))
                    {
                        NoteFull(s, st, full);
                    }
                    else if (nearNoRoom > 0 && MachineRegistry.TryGetRecord(nearNoRoom, out MachineRecord tight))
                    {
                        string what = LootPickService.HeldNameOf(new LootHeldRecord { Kind = kind, ItemId = itemId, TargetId = tag?.TargetId ?? string.Empty, Amount = g.Amount });
                        NoteNoRoom(s, tight, g.GroundItemId, what, space);
                    }
                    continue;
                }
                MachineRegistry.TryGetRecord(by, out MachineRecord picker);
                int take = g.Amount;
                if (bulk)
                {
                    take = Math.Min(g.Amount, BulkRoom(s, picker, itemId));
                }
                if (take <= 0)
                {
                    continue;
                }
                var h = new LootHeldRecord
                {
                    HeldId = NextHeldId(st), Machine = by, Kind = kind, ItemId = itemId, TargetId = tag?.TargetId ?? string.Empty, Amount = take,
                    Space = LootPickCatalog.SpaceOf(itemId, take), Faction = tag?.Faction ?? string.Empty,
                };
                MergeOrAppend(st, h); // 材料并入同机同种已有的那条（FGR-LOOT-030“每 10 个占 1”按总件数算）
                st.CargoAutoPicked++;
                if (take >= g.Amount)
                {
                    (removed ??= new List<string>()).Add(g.GroundItemId);
                    if (tag != null)
                    {
                        FieldLootService.TakeTag(s, g.SalvageInstanceId);
                    }
                }
                else
                {
                    g.Amount -= take;
                }
                changed = true;
                GuidanceHooks.Raise(GuidanceHooks.LootFirstAutoPickup);
                Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.Pickup, g.Position,
                    GameText.Format("cargo.picked", MachineNaming.Short(picker), LootPickService.HeldNameOf(h)));
            }
            if (removed != null)
            {
                var keep = new List<GroundItemRecord>(ground.Length);
                foreach (GroundItemRecord g in ground)
                {
                    if (g != null && !removed.Contains(g.GroundItemId))
                    {
                        keep.Add(g);
                    }
                }
                s.GroundItems = keep.ToArray();
                CancelHauls(s, removed);
            }
            // 地上的黑匣子（始终拾取，占 1 格；家园星球上的——远征地点里的沿用“装载”交互）。
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (q == null || !(BlackBoxService.IsBlackBox(q) || Env.HeritageService.IsHeritage(q)) || q.State != RegionQuestItemState.OnGround || q.RegionId != HomeValleyLayout.RegionId
                    || IsDiscarded(st, q.SalvageInstanceId) || !NearAnyMember(q.RegionId, q.Position))
                {
                    continue;
                }
                int space = LootPickCatalog.SpaceOf(q.ContentId, 1);
                int by = Picker(s, q.RegionId, q.Position, q.ContentId, space, false, out _, out int nearFull, out _);
                if (by <= 0)
                {
                    if (nearFull > 0 && MachineRegistry.TryGetRecord(nearFull, out MachineRecord full))
                    {
                        NoteFull(s, st, full);
                    }
                    continue;
                }
                MachineRegistry.TryGetRecord(by, out MachineRecord picker);
                q.State = RegionQuestItemState.Carried;
                q.CarrierLogicId = by;
                st.CargoAutoPicked++;
                BlackBoxService.Touch();
                Env.HeritageService.Touch();
                Env.HeritageService.CancelStalePickup(s, q, null); // FG8-EXP-04：遗产被队员顺手捡起 → 还没完工的取回单作废（不白跑）
                changed = true;
                GuidanceHooks.Raise(GuidanceHooks.LootFirstAutoPickup);
                Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.Pickup, q.Position, GameText.Format("cargo.picked", MachineNaming.Short(picker), QuestName(q)));
                q.Position = default;
            }
            return changed;
        }

        private static bool NearAnyMember(string region, Vector2 at)
        {
            float r2 = PickupRadius * PickupRadius;
            foreach ((int _, string mr, Vector2 pos) in Members)
            {
                if (mr == region && (pos - at).sqrMagnitude <= r2)
                {
                    return true;
                }
            }
            return false;
        }

        private static string KindOf(LootGroundTagRecord tag)
        {
            if (tag == null)
            {
                return LootPickCatalog.KindItem;
            }
            if (tag.ItemId == AnalysisCatalog.EncryptedFirmwareId)
            {
                return tag.Origin == LootPickService.OriginPrototype ? LootPickCatalog.KindCracked : LootPickCatalog.KindFirmware;
            }
            return tag.ItemId == AnalysisCatalog.DataCoreId ? LootPickCatalog.KindCore : LootPickCatalog.KindModule;
        }

        /// <summary>被自动拾取整份拿走的地面物：还没装上车的搬运单（待分配 / 赶路中 / 等待）取消，不让家园机器白跑一趟。</summary>
        private static void CancelHauls(CampaignState s, List<string> groundIds)
        {
            WorkOrderRecord[] orders = s.WorkOrders;
            if (orders == null)
            {
                return;
            }
            Scratch.Clear();
            for (int i = 0; i < orders.Length; i++)
            {
                WorkOrderRecord o = orders[i];
                if (o != null && o.Kind == WorkOrderKind.Haul && groundIds.Contains(o.SourceId)
                    && (o.State == WorkOrderState.Ready || o.State == WorkOrderState.Reserved || o.State == WorkOrderState.Waiting))
                {
                    Scratch.Add(i);
                }
            }
            foreach (int i in Scratch)
            {
                WorkOrderRecord o = orders[i];
                Vector2 at = MachineRegistry.TryGetRecord(o.AssignedMachineLogicId, out MachineRecord m) && m != null ? LootPickService.PositionOfMachine(m) : Vector2.zero;
                HomeValleyWorkOrders.CancelOrder(s, o.WorkOrderId, at);
                if (o.State == WorkOrderState.Cancelled)
                {
                    o.FailureReason = "source-vanished";
                }
            }
            Scratch.Clear();
        }

        // ─────────────────────────────── 快满 / 已满 ───────────────────────────────

        private static int WarnedLevel(LootPacketState st, int id)
        {
            int[] w = st.CargoWarned;
            int[] lv = st.CargoWarnLevels;
            if (w == null)
            {
                return 0;
            }
            int i = Array.IndexOf(w, id);
            return i >= 0 && lv != null && i < lv.Length ? lv[i] : 0;
        }

        private static void SetWarned(LootPacketState st, int id, int level)
        {
            var ids = new List<int>(st.CargoWarned ?? Array.Empty<int>());
            var lvs = new List<int>(st.CargoWarnLevels ?? Array.Empty<int>());
            while (lvs.Count < ids.Count)
            {
                lvs.Add(0);
            }
            int i = ids.IndexOf(id);
            if (level <= 0)
            {
                if (i >= 0)
                {
                    ids.RemoveAt(i);
                    lvs.RemoveAt(i);
                }
            }
            else if (i >= 0)
            {
                lvs[i] = level;
            }
            else
            {
                ids.Add(id);
                lvs.Add(level);
            }
            st.CargoWarned = ids.ToArray();
            st.CargoWarnLevels = lvs.ToArray();
        }

        /// <summary>自动拾取 / 转移因为货舱满被拒：机器还没被提示“已满”时提示一次（通知 + 第一次货舱满的引导钩子）。</summary>
        private static void NoteFull(CampaignState s, LootPacketState st, MachineRecord m)
        {
            if (m == null)
            {
                return;
            }
            int u = Used(s, m);
            int c = Capacity(s, m);
            // 只有真满（Level == 2）才记“已满”：还有空余时记了也会被 CheckLevels 按真实占用降回去，下一个对账秒又重发。
            if (Level(u, c) < 2 || WarnedLevel(st, m.LogicId) >= 2)
            {
                return;
            }
            SetWarned(st, m.LogicId, 2);
            Notify(m, 2, u, c);
        }

        /// <summary>货舱还有空余、但放不下这一件（按件计的物品）：每台机器、每份地面物只提示一次“放不下 X：需要 n，只剩 m”。</summary>
        private static void NoteNoRoom(CampaignState s, MachineRecord m, string groundId, string itemName, int space)
        {
            if (m == null)
            {
                return;
            }
            if (NoRoomNoted.Count >= 512)
            {
                NoRoomNoted.Clear();
            }
            if (!NoRoomNoted.Add(m.LogicId.ToString(CultureInfo.InvariantCulture) + "|" + groundId))
            {
                return;
            }
            Vector2 at = LootPickService.PositionOfMachine(m);
            NotificationCenter.Post(NotifyType, GameText.Format("cargo.no_room_notify", MachineNaming.Short(m), itemName, space, Free(s, m)), new Vector3(at.x, 0f, at.y));
        }

        private static void Notify(MachineRecord m, int level, int used, int cap)
        {
            Vector2 at = LootPickService.PositionOfMachine(m);
            if (level >= 2)
            {
                GuidanceHooks.Raise(GuidanceHooks.LootFirstCargoFull);
                NotificationCenter.Post(NotifyType, GameText.Format("cargo.full_notify", MachineNaming.Short(m), used, cap), new Vector3(at.x, 0f, at.y));
            }
            else
            {
                NotificationCenter.Post(NotifyType, GameText.Format("cargo.warn_notify", MachineNaming.Short(m), used, cap), new Vector3(at.x, 0f, at.y));
            }
        }

        /// <summary>队员货舱状态：升到快满 / 已满时各提示一次；降下来就清掉记录（之后再满再提示）。不在队里的机器的记录清掉。</summary>
        private static bool CheckLevels(CampaignState s, LootPacketState st)
        {
            bool changed = false;
            foreach ((int id, string _, Vector2 _) in Members)
            {
                if (!Alive(id, out MachineRecord m))
                {
                    continue;
                }
                int u = Used(s, m);
                int c = Capacity(s, m);
                int level = Level(u, c);
                int was = WarnedLevel(st, id);
                if (level > was)
                {
                    SetWarned(st, id, level);
                    Notify(m, level, u, c);
                    changed = true;
                }
                else if (level < was)
                {
                    SetWarned(st, id, level);
                    changed = true;
                }
            }
            int[] warned = st.CargoWarned;
            if (warned != null && warned.Length > 0)
            {
                foreach (int id in warned)
                {
                    bool inTeam = false;
                    foreach ((int mid, string _, Vector2 _) in Members)
                    {
                        inTeam |= mid == id;
                    }
                    if (!inTeam)
                    {
                        SetWarned(st, id, 0);
                        changed = true;
                    }
                }
            }
            return changed;
        }

        // ─────────────────────────────── 地图标记（FG-GAP-104）───────────────────────────────

        /// <summary>地图 / 小地图上的一个地面战利品标记。</summary>
        public struct GroundMarker
        {
            public string Id;
            public float X;
            public float Y;
            public string Label;
            /// <summary>packet / blackbox / cargo / key。</summary>
            public string Kind;
        }

        /// <summary>
        /// 家园所在星球上地上的战利品（FG-GAP-104：地面外观之外还要在地图上标出来）：地上的数据包、黑匣子、阵亡 / 丢弃掉落的货舱物品、关键材料。
        /// 普通野外掉落不标（数量多、由搬运单 / 自动拾取处理）。在敌方领地里的写明“在某某领地内”。O(数据包 + 关键物 + 地面物)。
        /// </summary>
        public static void CollectGroundMarkers(CampaignState s, List<GroundMarker> into)
        {
            into.Clear();
            if (s == null)
            {
                return;
            }
            foreach (LootPackageRecord p in s.LootPackets?.Packets ?? Array.Empty<LootPackageRecord>())
            {
                if (p != null && p.Holder <= 0 && (string.IsNullOrEmpty(p.RegionId) || p.RegionId == HomeValleyLayout.RegionId))
                {
                    into.Add(Marker(s, "loot-packet:" + p.PacketId, p.X, p.Y, GameText.Get("cargo.map.packet"), "packet"));
                }
            }
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (q != null && q.State == RegionQuestItemState.OnGround && q.RegionId == HomeValleyLayout.RegionId)
                {
                    into.Add(Marker(s, "loot-quest:" + q.SalvageInstanceId, q.Position.x, q.Position.y,
                        BlackBoxService.IsBlackBox(q) ? QuestName(q) : GameText.Format("cargo.map.key", QuestName(q), 1), BlackBoxService.IsBlackBox(q) ? "blackbox" : "key"));
                }
            }
            foreach (GroundItemRecord g in s.GroundItems ?? Array.Empty<GroundItemRecord>())
            {
                if (g == null || g.RegionId != HomeValleyLayout.RegionId || g.Amount <= 0 || !ItemCatalog.TryGetByResource(g.ResourceType, out ItemDef d))
                {
                    continue;
                }
                bool key = KeyItemRetention.OfItem(d.Id) != KeyItemRetention.Kind.Ordinary;
                bool dropped = IsCargoDrop(g.SalvageInstanceId);
                if (!key && !dropped)
                {
                    continue;
                }
                LootGroundTagRecord tag = dropped ? FieldLootService.TagOf(s, g.SalvageInstanceId) : null;
                string name = tag != null
                    ? LootPickService.CandidateName(new LootCandidateRecord { Kind = KindOf(tag), ItemId = tag.ItemId, TargetId = tag.TargetId, Amount = g.Amount })
                    : d.Name;
                into.Add(Marker(s, "loot-ground:" + g.GroundItemId, g.Position.x, g.Position.y,
                    GameText.Format(key ? "cargo.map.key" : "cargo.map.cargo", name, g.Amount), key ? "key" : "cargo"));
            }
        }

        /// <summary>这份地面物是从货舱里掉出来的（阵亡掉落 / 丢弃：回收实例 ID 是货舱记录 ID）。</summary>
        public static bool IsCargoDrop(string salvageId)
        {
            if (string.IsNullOrEmpty(salvageId))
            {
                return false;
            }
            string sid = salvageId;
            while (sid.StartsWith("ground:", StringComparison.Ordinal))
            {
                sid = sid.Substring(7);
            }
            return sid.StartsWith(HeldPrefix, StringComparison.Ordinal) || sid.StartsWith(LootPickService.HeldPrefix, StringComparison.Ordinal);
        }

        private static GroundMarker Marker(CampaignState s, string id, float x, float y, string label, string kind)
        {
            PlannedTerritory t = WorldGenQuery.TerritoryAt(s, Mathf.RoundToInt(x), Mathf.RoundToInt(y));
            if (t != null && t.IsFaction)
            {
                label += GameText.Format("cargo.map.enemy", GameText.Get(t.NameKey));
            }
            return new GroundMarker { Id = id, X = x, Y = y, Label = label, Kind = kind };
        }
    }
}
